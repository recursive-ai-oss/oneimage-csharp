using System.Buffers;
using Microsoft.Win32.SafeHandles;

using OneImage.Layers;

namespace OneImage;

/// <summary>
/// A tile store that keeps at most a byte budget of <i>sealed</i> (immutable) tiles in memory and parks the least recently
/// used ones in a temporary file, reading them back the next time their pixels are touched.
/// A tile is sealed by <see cref="Tile.Seal"/> (a canvas snapshot seals every tile it holds). Tiles that can still be written in
/// place are never moved, so they count against the budget but cannot be evicted: seal often (render, or flush the layer) while
/// building. All file access is positional through <see cref="RandomAccess"/>; the file is unlinked at creation on Unix and
/// opened delete-on-close on Windows, so it disappears when the store is disposed or the process ends.
/// An evicted buffer is not reused at once, because a reader that took its span just before the eviction may still be using it:
/// it waits for the readers that were active at eviction to leave (see <see cref="SpillEpochs"/>), or goes to the garbage collector when
/// its tile was read by code that did not announce itself.
/// </summary>
internal sealed class SpillTileStore : ITileStore, IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly Queue<Tile> _clock = new();
    private readonly Dictionary<int, Stack<long>> _free = [];
    private long _budget;
    private long _resident;
    private long _peak;
    private long _written;
    private long _read;
    private long _fileLength;
    private int _compactAt = 4096;
    private SafeFileHandle? _handle;
    private bool _disposed;

    public SpillTileStore(long budgetBytes, string? directory, long maxFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budget = budgetBytes;
        _directory = directory ?? System.IO.Path.GetTempPath();
        _maxFileBytes = maxFileBytes;
    }

    public SpillStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new SpillStatistics(_written, _read, Interlocked.Read(ref _resident), Interlocked.Read(ref _peak), _fileLength, _budget);
            }
        }
    }

    public byte[] Rent(int length, bool clear)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[]? buf = SpillEpochs.Take(length);
        if (buf != null)
        {
            if (clear)
            {
                buf.AsSpan(0, length).Clear();
            }
        }
        else
        {
            buf = PooledTileStore.Instance.Rent(length, clear);
        }

        AddResident(buf.Length);
        return buf;
    }

    public void Return(byte[] buffer)
    {
        Interlocked.Add(ref _resident, -buffer.Length);
        if (!SpillEpochs.TryFree(buffer))
        {
            PooledTileStore.Instance.Return(buffer);
        }
    }

    public void Dispose()
    {
        SafeFileHandle? h;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            h = _handle;
            _handle = null;
            _clock.Clear();
        }

        h?.Dispose();
    }

    /// <summary>Called by a tile whose last reference is gone: frees its memory and file slot.</summary>
    internal void OnDead(Tile tile)
    {
        Tile.SpillEntry e = tile.Spill!;
        long slot;
        byte[]? buf;
        lock (e)
        {
            e.Dead = true;
            buf = tile.TakeBuffer();
            slot = e.Slot;
            e.Slot = -1;
        }

        if (buf != null)
        {
            Return(buf);
        }

        if (slot >= 0)
        {
            lock (_gate)
            {
                if (!_free.TryGetValue(tile.Length, out Stack<long>? stack))
                {
                    _free[tile.Length] = stack = new Stack<long>();
                }

                stack.Push(slot);
            }
        }
    }

    /// <summary>Makes <paramref name="tile"/> evictable: its content will never change again.</summary>
    internal void Seal(Tile tile)
    {
        Tile.SpillEntry e = tile.Spill!;
        lock (e)
        {
            if (e.Sealed || e.Dead)
            {
                return;
            }

            e.Sealed = true;
            if (tile.HasBuffer)
            {
                Enqueue(tile, e);
            }
        }
    }

    /// <summary>Brings an evicted tile back into memory and returns its buffer.</summary>
    internal byte[] PageIn(Tile tile)
    {
        Tile.SpillEntry e = tile.Spill!;
        byte[] buf;
        lock (e)
        {
            byte[]? current = tile.CurrentBuffer;
            if (current != null)
            {
                return current;
            }

            if (e.Dead || e.Slot < 0)
            {
                throw new ObjectDisposedException(nameof(Tile), "The tile's spill file is gone (the document was disposed).");
            }

            SafeFileHandle handle;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || _handle == null, this);
                handle = _handle;
            }

            buf = SpillEpochs.Take(tile.Length) ?? PooledTileStore.Instance.Rent(tile.Length, clear: false);
            int done = 0;
            while (done < buf.Length)
            {
                int n = RandomAccess.Read(handle, buf.AsSpan(done), e.Slot + done);
                if (n <= 0)
                {
                    throw new IOException("The spill file ended early.");
                }

                done += n;
            }

            tile.SetBuffer(buf);
            e.Touched = true;
            AddResident(buf.Length);
            Interlocked.Increment(ref _read);
            Enqueue(tile, e);
        }

        EnforceBudget(tile);
        return buf;
    }

    /// <summary>Evicts sealed tiles, least recently used first (second chance), until the resident bytes fit the budget or nothing more can go.</summary>
    internal void EnforceBudget(Tile? keep = null)
    {
        if (Interlocked.Read(ref _resident) <= _budget)
        {
            return;
        }

        int guard;
        lock (_gate)
        {
            guard = (_clock.Count * 2) + 2;
        }

        while (guard-- > 0 && Interlocked.Read(ref _resident) > _budget)
        {
            Tile? victim;
            lock (_gate)
            {
                if (!_clock.TryDequeue(out victim))
                {
                    return;
                }

                victim.Spill!.Queued = false;
            }

            Tile.SpillEntry e = victim.Spill!;
            if (ReferenceEquals(victim, keep) || e.Touched)
            {
                e.Touched = false;
                lock (_gate)
                {
                    if (!e.Queued && !e.Dead)
                    {
                        e.Queued = true;
                        _clock.Enqueue(victim);
                    }
                }

                continue;
            }

            if (!Monitor.TryEnter(e))
            {
                continue;
            }

            try
            {
                byte[]? buf = victim.CurrentBuffer;
                if (buf == null || e.Dead || !e.Sealed)
                {
                    continue;
                }

                if (e.Slot < 0)
                {
                    e.Slot = AllocateSlot(victim.Length);
                    WriteAt(e.Slot, buf.AsSpan(0, victim.Length));
                    Interlocked.Increment(ref _written);
                }

                victim.DropBuffer();
                Interlocked.Add(ref _resident, -buf.Length);

                // After the drop: a reader that was not in a scope marks the tile before it looks at the buffer, so one of us sees the other.
                if (Interlocked.Exchange(ref e.Unscoped, 0) == 0)
                {
                    SpillEpochs.Defer(buf);
                }
            }
            finally
            {
                Monitor.Exit(e);
            }
        }
    }

    private void AddResident(long bytes)
    {
        long now = Interlocked.Add(ref _resident, bytes);
        long peak = Interlocked.Read(ref _peak);
        while (now > peak)
        {
            long seen = Interlocked.CompareExchange(ref _peak, now, peak);
            if (seen == peak)
            {
                break;
            }

            peak = seen;
        }
    }

    // Caller holds the tile's lock.
    private void Enqueue(Tile tile, Tile.SpillEntry e)
    {
        lock (_gate)
        {
            if (e.Queued || _disposed)
            {
                return;
            }

            e.Queued = true;
            _clock.Enqueue(tile);
            if (_clock.Count > _compactAt)
            {
                // Dead tiles linger on the queue until they reach the front; sweep them when the queue has doubled.
                int n = _clock.Count;
                for (int i = 0; i < n; i++)
                {
                    Tile t = _clock.Dequeue();
                    if (t.Spill!.Dead)
                    {
                        t.Spill.Queued = false;
                    }
                    else
                    {
                        _clock.Enqueue(t);
                    }
                }

                _compactAt = Math.Max(4096, _clock.Count * 2);
            }
        }
    }

    private long AllocateSlot(int length)
    {
        lock (_gate)
        {
            if (_free.TryGetValue(length, out Stack<long>? stack) && stack.Count > 0)
            {
                return stack.Pop();
            }

            if (_fileLength + length > _maxFileBytes)
            {
                throw new ImageLimitException($"The spill file would exceed its limit of {_maxFileBytes} bytes.");
            }

            long at = _fileLength;
            _fileLength += length;
            return at;
        }
    }

    private void WriteAt(long offset, ReadOnlySpan<byte> data)
    {
        SafeFileHandle handle;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _handle ??= OpenFile();
            handle = _handle;
        }

        RandomAccess.Write(handle, data, offset);
    }

    private SafeFileHandle OpenFile()
    {
        Directory.CreateDirectory(_directory);
        string path = System.IO.Path.Combine(_directory, "oneimage-spill-" + Guid.NewGuid().ToString("N") + ".tmp");
        if (OperatingSystem.IsWindows())
        {
            return File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        }

        SafeFileHandle h = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.RandomAccess);

        // Unlinked while open: the file cannot outlive the process, even after a crash.
        File.Delete(path);
        return h;
    }
}

internal sealed partial class Tile
{
    private SpillEntry? _spill;

    /// <summary>Spill bookkeeping, also the lock object that guards this tile's buffer while it moves between memory and disk.</summary>
    internal sealed class SpillEntry(SpillTileStore store)
    {
        public SpillTileStore Store { get; } = store;

        public long Slot = -1;
        public volatile bool Sealed;

        /// <summary>Set when the current buffer was read outside a read scope; its memory is then never reused.</summary>
        public int Unscoped;
        public bool Queued;
        public bool Dead;
        public volatile bool Touched;
    }

    internal SpillEntry? Spill => _spill;

    internal bool HasBuffer => Volatile.Read(ref _buffer) != null;

    internal byte[]? CurrentBuffer => Volatile.Read(ref _buffer);

    internal void SetBuffer(byte[] buffer) => Volatile.Write(ref _buffer, buffer);

    internal void DropBuffer() => Interlocked.Exchange(ref _buffer, null);

    internal byte[]? TakeBuffer()
    {
        byte[]? b = _buffer;
        _buffer = null;
        return b;
    }

    /// <summary>Announces a read of this tile's pixels for as long as the returned scope lives; a no-op for ordinary tiles.</summary>
    internal SpillEpochs.Scope EnterRead() => _spill == null ? default : SpillEpochs.Enter();

    /// <summary>Marks this tile immutable, which lets a spilling store move it to disk. No effect on ordinary tiles.</summary>
    public void Seal() => _spill?.Store.Seal(this);

    private static Tile RentSpilling(int length, bool clear, SpillTileStore store)
    {
        byte[] buf = store.Rent(length, clear);
        Interlocked.Increment(ref s_rented);
        var tile = new Tile(buf, length, store, refs: 1) { _spill = new SpillEntry(store) };
        store.EnforceBudget();
        return tile;
    }

    private Span<byte> SpillSpan()
    {
        SpillEntry e = _spill!;
        while (true)
        {
            byte[] b = Volatile.Read(ref _buffer) ?? e.Store.PageIn(this);
            if (e.Sealed && !SpillEpochs.InScope)
            {
                // A read nobody announced: this buffer must never be reused while the span may be alive. Mark it, then make sure it is still
                // the tile's buffer. An eviction drops the buffer and then looks at the mark, so either it sees the mark or we see the drop.
                Interlocked.Exchange(ref e.Unscoped, 1);
                if (!ReferenceEquals(Volatile.Read(ref _buffer), b))
                {
                    continue;
                }
            }

            e.Touched = true;
            return b.AsSpan(0, Length);
        }
    }
}
