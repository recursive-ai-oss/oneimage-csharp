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
/// An evicted buffer is dropped for the garbage collector rather than pooled, because a reader that took its span just before
/// the eviction may still be using it.
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
    private long _garbage;
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
        byte[] buf = PooledTileStore.Instance.Rent(length, clear);
        AddResident(buf.Length);
        return buf;
    }

    public void Return(byte[] buffer)
    {
        Interlocked.Add(ref _resident, -buffer.Length);
        PooledTileStore.Instance.Return(buffer);
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

            buf = GC.AllocateUninitializedArray<byte>(tile.Length);
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

        EnforceBudget();
        return buf;
    }

    /// <summary>Evicts sealed tiles, least recently used first (second chance), until the resident bytes fit the budget or nothing more can go.</summary>
    internal void EnforceBudget()
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
            if (e.Touched)
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
                Interlocked.Add(ref _garbage, buf.Length);
            }
            finally
            {
                Monitor.Exit(e);
            }
        }

        CollectEvicted();
    }

    /// <summary>
    /// Evicted buffers are not pooled (see the type remarks) and by the time they are evicted they have usually been promoted, so the
    /// collector would find them late and the process would drift well past the budget. Once about half a budget of them has piled up
    /// we ask for one full collection; the arrays hold no references, so it is cheap.
    /// </summary>
    private void CollectEvicted()
    {
        long limit = Math.Clamp(_budget / 2, 16L << 20, 128L << 20);
        long seen = Interlocked.Read(ref _garbage);
        if (seen >= limit && Interlocked.CompareExchange(ref _garbage, 0, seen) == seen)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
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
        public bool Sealed;
        public bool Queued;
        public bool Dead;
        public volatile bool Touched;
    }

    internal SpillEntry? Spill => _spill;

    internal bool HasBuffer => Volatile.Read(ref _buffer) != null;

    internal byte[]? CurrentBuffer => Volatile.Read(ref _buffer);

    internal void SetBuffer(byte[] buffer) => Volatile.Write(ref _buffer, buffer);

    internal void DropBuffer() => Volatile.Write(ref _buffer, null);

    internal byte[]? TakeBuffer()
    {
        byte[]? b = _buffer;
        _buffer = null;
        return b;
    }

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
        byte[]? b = Volatile.Read(ref _buffer);
        if (b != null)
        {
            e.Touched = true;
            return b.AsSpan(0, Length);
        }

        return e.Store.PageIn(this).AsSpan(0, Length);
    }
}
