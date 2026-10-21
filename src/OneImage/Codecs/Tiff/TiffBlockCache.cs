namespace OneImage.Codecs.Tiff;

/// <summary>
/// A byte-budgeted LRU cache of decoded strips and tiles. Region reads ask for 128x128 pieces, which usually fall inside one
/// bigger strip or tile; the cache keeps that block so neighbors do not decode it again. A block is computed once even when
/// several threads ask for it at the same time. The newest block always stays, even if it alone exceeds the budget.
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

        public byte[]? Data { get; set; }

        public LinkedListNode<Entry>? Node { get; set; }
    }

    public byte[] GetOrCompute<TState>(long key, TState state, Func<TState, byte[]> compute)
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
            if (e.Data is { } done)
            {
                return done;
            }

            byte[] data;
            try
            {
                data = compute(state);
            }
            catch
            {
                lock (_gate)
                {
                    if (e.Node is { } n)
                    {
                        _lru.Remove(n);
                        e.Node = null;
                        _map.Remove(e.Key);
                    }
                }

                throw;
            }

            e.Data = data;
            lock (_gate)
            {
                if (e.Node is not null)
                {
                    _used += data.Length;
                    while (_used > _budget && _lru.Last is { } last && last.Value != e)
                    {
                        Entry victim = last.Value;
                        _lru.RemoveLast();
                        victim.Node = null;
                        _map.Remove(victim.Key);
                        _used -= victim.Data?.Length ?? 0;
                    }
                }
            }

            return data;
        }
    }
}
