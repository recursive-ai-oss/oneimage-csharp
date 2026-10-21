using System.Collections.Concurrent;

namespace OneImage;

/// <summary>
/// One 128x128 block of pixels in a node's or canvas's native format, row stride <c>128 * bytesPerPixel</c>.
/// Tiles are reference counted: whoever holds a reference may read; the buffer returns to the pool when the last
/// reference goes. A tile is mutated only by the canvas that owns it (see <see cref="Owner"/>); everything shared is immutable.
/// Pixels outside the image in edge tiles are undefined.
/// </summary>
internal sealed class Tile
{
    public const int Size = 128;
    public const int Shift = 7;

    private static readonly ConcurrentDictionary<PixelFormat, Tile> s_zero = new();
    private static long s_rented;
    private static long s_returned;
    private static long s_copied;

    private readonly ITileStore? _store;
    private byte[] _buffer;
    private int _refs;

    private Tile(byte[] buffer, int length, ITileStore? store, int refs)
    {
        _buffer = buffer;
        Length = length;
        _store = store;
        _refs = refs;
    }

    /// <summary>Gets the number of pooled buffers rented so far (test statistic).</summary>
    public static long RentedCount => Interlocked.Read(ref s_rented);

    /// <summary>Gets the number of pooled buffers returned so far (test statistic).</summary>
    public static long ReturnedCount => Interlocked.Read(ref s_returned);

    /// <summary>Gets the number of tile-to-tile data copies made so far (test statistic).</summary>
    public static long CopyCount => Interlocked.Read(ref s_copied);

    /// <summary>Gets the object that may mutate this tile in place, or null if it is immutable.</summary>
    public object? Owner { get; set; }

    public int Length { get; }

    /// <summary>Gets the tile's bytes. Every read and write of tile memory goes through here, so a store that spills tiles can make them resident at this point.</summary>
    public Span<byte> Span => _buffer.AsSpan(0, Length);

    public static int ByteSize(PixelFormat format) => Size * Size * format.BytesPerPixel;

    public static Tile Rent(int length, bool clear)
    {
        ITileStore store = TileStores.Current;
        byte[] buf = store.Rent(length, clear);
        Interlocked.Increment(ref s_rented);
        return new Tile(buf, length, store, refs: 1);
    }

    /// <summary>Returns a shared, never-written all-zero tile (the content of sparse regions).</summary>
    public static Tile Zero(PixelFormat format) =>
        s_zero.GetOrAdd(format, static f => new Tile(new byte[ByteSize(f)], ByteSize(f), store: null, refs: 1 << 30));

    public Tile CopyForWrite(object owner)
    {
        Tile t = Rent(Length, clear: false);
        Span.CopyTo(t.Span);
        t.Owner = owner;
        Interlocked.Increment(ref s_copied);
        return t;
    }

    public Tile Retain()
    {
        Interlocked.Increment(ref _refs);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0 && _store != null)
        {
            byte[] buf = _buffer;
            _buffer = [];
            _store.Return(buf);
            Interlocked.Increment(ref s_returned);
        }
    }
}

/// <summary>Byte-budgeted LRU cache of computed tiles. A miss is recomputed; eviction never loses data.</summary>
internal sealed class TileCache
{
    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _map = [];
    private readonly Dictionary<Key, InFlight> _inFlight = [];
    private Entry? _head;
    private Entry? _tail;
    private long _bytes;
    private long _budget;
    private long _hits;
    private long _misses;

    public TileCache(long budget) => _budget = budget;

    public static TileCache Global { get; } = new(256L * 1024 * 1024);

    /// <summary>Gets the cache for tiles derived from streaming decodes: just enough to share tiles between neighbouring renders.</summary>
    public static TileCache Streaming { get; } = new(8L * 1024 * 1024);

    public long Budget
    {
        get { lock (_gate) { return _budget; } }
        set
        {
            lock (_gate)
            {
                _budget = value;
                Evict();
            }
        }
    }

    public long Bytes { get { lock (_gate) { return _bytes; } } }

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public int Count { get { lock (_gate) { return _map.Count; } } }

    /// <summary>Returns a retained tile or null. The caller must release it.</summary>
    public Tile? TryGet(long node, int tx, int ty)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(new Key(node, tx, ty), out Entry? e))
            {
                MoveToFront(e);
                Interlocked.Increment(ref _hits);
                return e.Tile.Retain();
            }
        }

        Interlocked.Increment(ref _misses);
        return null;
    }

    /// <summary>
    /// Returns the cached tile, or computes it exactly once even when several threads ask at the same time (the others wait
    /// for the first). Waiting only ever depends on tiles lower in the graph, so it cannot deadlock. The caller must release the result.
    /// </summary>
    public Tile GetOrCompute<TState>(long node, int tx, int ty, TState state, Func<TState, Tile> compute, CancellationToken cancellationToken)
    {
        var key = new Key(node, tx, ty);
        while (true)
        {
            InFlight? waitFor = null;
            InFlight? mine = null;
            lock (_gate)
            {
                if (_map.TryGetValue(key, out Entry? e))
                {
                    MoveToFront(e);
                    Interlocked.Increment(ref _hits);
                    return e.Tile.Retain();
                }

                if (_inFlight.TryGetValue(key, out waitFor))
                {
                    // Someone else is computing it.
                }
                else
                {
                    mine = new InFlight();
                    _inFlight[key] = mine;
                }
            }

            if (mine == null)
            {
                lock (waitFor!)
                {
                    while (!waitFor.Done)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Monitor.Wait(waitFor, 20);
                    }
                }

                continue;
            }

            Interlocked.Increment(ref _misses);
            try
            {
                Tile t = compute(state);
                Add(node, tx, ty, t);
                return t;
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight.Remove(key);
                }

                lock (mine)
                {
                    mine.Done = true;
                    Monitor.PulseAll(mine);
                }
            }
        }
    }

    /// <summary>Stores a reference to the tile (the caller keeps its own).</summary>
    public void Add(long node, int tx, int ty, Tile tile)
    {
        lock (_gate)
        {
            var key = new Key(node, tx, ty);
            if (_map.ContainsKey(key))
            {
                return;
            }

            var e = new Entry(key, tile.Retain());
            _map[key] = e;
            PushFront(e);
            _bytes += tile.Length;
            Evict();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            long saved = _budget;
            _budget = 0;
            Evict();
            _budget = saved;
        }
    }

    private void Evict()
    {
        while (_bytes > _budget && _tail is { } e)
        {
            Unlink(e);
            _map.Remove(e.Key);
            _bytes -= e.Tile.Length;
            e.Tile.Release();
        }
    }

    private void PushFront(Entry e)
    {
        e.Next = _head;
        e.Prev = null;
        if (_head != null)
        {
            _head.Prev = e;
        }

        _head = e;
        _tail ??= e;
    }

    private void Unlink(Entry e)
    {
        if (e.Prev != null)
        {
            e.Prev.Next = e.Next;
        }
        else
        {
            _head = e.Next;
        }

        if (e.Next != null)
        {
            e.Next.Prev = e.Prev;
        }
        else
        {
            _tail = e.Prev;
        }

        e.Prev = null;
        e.Next = null;
    }

    private void MoveToFront(Entry e)
    {
        if (_head != e)
        {
            Unlink(e);
            PushFront(e);
        }
    }

    private readonly record struct Key(long Node, int Tx, int Ty);

    private sealed class InFlight
    {
        public bool Done { get; set; }
    }

    private sealed class Entry(Key key, Tile tile)
    {
        public Key Key { get; } = key;

        public Tile Tile { get; } = tile;

        public Entry? Prev { get; set; }

        public Entry? Next { get; set; }
    }
}

/// <summary>Per-call rendering state: parallel degree, cancellation and the cache to use.</summary>
internal sealed class RenderContext
{
    private RenderContext? _nested;

    public RenderContext(int degree, TileCache cache, CancellationToken cancellationToken)
        : this(degree, cache, cache, cancellationToken)
    {
    }

    public RenderContext(int degree, TileCache cache, TileCache streamCache, CancellationToken cancellationToken)
    {
        Degree = degree;
        CancellationToken = cancellationToken;
        Cache = cache;
        StreamCache = streamCache;
    }

    public int Degree { get; }

    public CancellationToken CancellationToken { get; }

    public TileCache Cache { get; }

    /// <summary>Gets the small cache for tiles derived from a streaming decode, so they never compete with (or outgrow) the global budget.</summary>
    public TileCache StreamCache { get; }

    public TileCache CacheFor(Node node) => node.Streaming ? StreamCache : Cache;

    /// <summary>Gets the context for work done inside an already-parallel tile computation: same token and cache, degree 1.</summary>
    public RenderContext Nested => Degree == 1 ? this : _nested ??= new RenderContext(1, Cache, StreamCache, CancellationToken);

    public static RenderContext Create(int maxDegree, CancellationToken cancellationToken) =>
        new(ImageEngine.ResolveDegree(maxDegree), TileCache.Global, TileCache.Streaming, cancellationToken);
}
