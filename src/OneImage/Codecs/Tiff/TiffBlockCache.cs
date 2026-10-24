using System.Buffers;

namespace OneImage.Codecs.Tiff;

/// <summary>
/// A decoded strip or tile in pooled memory, shared by reference count: the cache holds one reference and every reader another,
/// and the buffer goes back to the pool when the last one lets go, so evicting a block a reader still copies from is safe.
/// </summary>
internal sealed class TiffBlock
{
    private byte[]? _data;
    private int _refs = 1;

    public TiffBlock(byte[] pooled) => _data = pooled;

    /// <summary>Gets the buffer, which is longer than the block; valid only while a reference is held.</summary>
    public byte[] Data => _data!;

    /// <summary>Takes a reference; false when the block was already given back.</summary>
    public bool TryAcquire()
    {
        int r = Volatile.Read(ref _refs);
        while (r > 0)
        {
            int seen = Interlocked.CompareExchange(ref _refs, r + 1, r);
            if (seen == r)
            {
                return true;
            }

            r = seen;
        }

        return false;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            byte[] d = _data!;
            _data = null;
            Pools<byte>.Pool.Return(d);
        }
    }
}

/// <summary>
/// A byte-budgeted LRU cache of decoded strips and tiles. Region reads ask for 128x128 pieces, which usually fall inside one
/// bigger strip or tile; the cache keeps that block so neighbors do not decode it again. A block is computed once even when
/// several threads ask for it at the same time. The newest block always stays, even if it alone exceeds the budget.
/// <see cref="Clear"/> gives everything back to the pool once a whole-frame read is over.
/// </summary>
internal sealed class TiffBlockCache
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _map = [];
    private readonly LinkedList<Entry> _lru = new();
    private readonly long _budget;
    private long _used;

    public TiffBlockCache(long budgetBytes) => _budget = budgetBytes;

    private sealed class Entry(long key)
    {
        public long Key { get; } = key;

        public object Gate { get; } = new();

        public TiffBlock? Block { get; set; }

        public long Size { get; set; }

        public LinkedListNode<Entry>? Node { get; set; }
    }

    /// <summary>Returns the block with a reference for the caller, who must <see cref="TiffBlock.Release"/> it. <paramref name="compute"/> returns a pooled buffer and the block's length.</summary>
    public TiffBlock GetOrCompute<TState>(long key, TState state, Func<TState, byte[]> compute, Func<TState, int> length)
    {
        while (true)
        {
            Entry e;
            lock (_gate)
            {
                if (_map.TryGetValue(key, out Entry? found))
                {
                    e = found;
                    if (e.Node is { } n && n != _lru.First)
                    {
                        _lru.Remove(n);
                        _lru.AddFirst(n);
                    }
                }
                else
                {
                    e = new Entry(key);
                    e.Node = _lru.AddFirst(e);
                    _map[key] = e;
                }
            }

            lock (e.Gate)
            {
                if (e.Block is { } done)
                {
                    if (done.TryAcquire())
                    {
                        return done;
                    }

                    // Evicted or cleared between the lookup and now: start over with a fresh entry.
                    continue;
                }

                TiffBlock block;
                try
                {
                    block = new TiffBlock(compute(state));
                }
                catch
                {
                    lock (_gate)
                    {
                        Drop(e);
                    }

                    throw;
                }

                long size = length(state);
                block.TryAcquire();

                // Publishing the block and the liveness check happen under one lock: Clear/Drop may run at any moment and
                // must either see the finished entry (and release the cache's reference) or not see the block at all.
                lock (_gate)
                {
                    if (e.Node is not null)
                    {
                        e.Block = block;
                        e.Size = size;
                        _used += e.Size;
                        while (_used > _budget && _lru.Last is { } last && last.Value != e)
                        {
                            Drop(last.Value);
                        }
                    }
                    else
                    {
                        // Cleared while computing: the reader keeps the only reference.
                        block.Release();
                    }
                }

                return block;
            }
        }
    }

    /// <summary>Forgets every block; readers still copying from one keep it alive until they finish.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            while (_lru.Last is { } last)
            {
                Drop(last.Value);
            }
        }
    }

    // Caller holds _gate.
    private void Drop(Entry e)
    {
        if (e.Node is not { } n)
        {
            return;
        }

        _lru.Remove(n);
        e.Node = null;
        _map.Remove(e.Key);
        if (e.Block is { } b)
        {
            _used -= e.Size;
            b.Release();
        }
    }
}
