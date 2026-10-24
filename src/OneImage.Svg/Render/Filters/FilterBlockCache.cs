using System.Buffers;
using System.Runtime.CompilerServices;

namespace OneImage.Svg.Render;

/// <summary>Identifies one block of a filter's output: the block's place on a grid anchored to the element's own pixel grid, and that grid's scale and sub-pixel offset.</summary>
internal readonly record struct FilterBlockKey(long Bx, long By, int Size, long ScaleX, long ScaleY, long FracX, long FracY);

/// <summary>
/// Filter output kept in square blocks. A filter with a wide margin (a large blur) needs that margin around every piece of output that is
/// asked for; tiles of 128 pixels would each refilter several times their own area. Blocks of 512 or 1024 pixels are computed once, on
/// the first request that touches them, and shared by every later request on any thread, so the margin is paid per block.
/// Pixels are straight 8-bit RGBA, as the filter would have drawn them. Blocks are charged against one process-wide byte budget and
/// evicted least recently used first; an evicted block is simply computed again when needed.
/// </summary>
internal static class FilterBlockCache
{
    private const long BudgetBytes = 96L * 1024 * 1024;

    private static readonly ConditionalWeakTable<RLayer, Store> s_stores = [];
    private static readonly Lock s_lock = new();
    private static readonly LinkedList<Block> s_lru = new();
    private static long s_bytes;

    /// <summary>Gets the bytes of pixels held now (for tests).</summary>
    internal static long HeldBytes
    {
        get
        {
            lock (s_lock)
            {
                return s_bytes;
            }
        }
    }

    /// <summary>Gets the number of block computations since the process started (for tests).</summary>
    internal static long Computed => Interlocked.Read(ref s_computed);

    private static long s_computed;
    private static volatile bool s_disabled;

    /// <summary>Gets or sets whether blocks are bypassed, so tests can compare cached output with output computed without sharing.</summary>
    internal static bool Disabled
    {
        get => s_disabled;
        set => s_disabled = value;
    }

    /// <summary>
    /// Returns the block, computing it with <paramref name="compute"/> when it is not held. Concurrent requests for one block wait for the
    /// first to finish. The lease keeps the pixels valid until it is disposed.
    /// </summary>
    public static Lease Get(RLayer layer, FilterBlockKey key, Func<(Rgba32[]? Pixels, PxRect Rect)> compute)
    {
        Block block;
        lock (s_lock)
        {
            Store store = s_stores.GetValue(layer, static _ => new Store());
            if (!store.Blocks.TryGetValue(key, out Block? existing))
            {
                existing = new Block(new WeakReference<Store>(store), key);
                store.Blocks[key] = existing;
            }

            block = existing;
            block.Refs++;
            if (block.Node is { } node)
            {
                s_lru.Remove(node);
                s_lru.AddFirst(node);
            }
        }

        try
        {
            lock (block.Gate)
            {
                if (!block.Ready)
                {
                    (Rgba32[]? pixels, PxRect rect) = compute();
                    Interlocked.Increment(ref s_computed);
                    lock (s_lock)
                    {
                        block.Pixels = pixels;
                        block.Rect = rect;
                        block.Bytes = pixels is null ? 0 : (long)pixels.Length * 4;
                        block.Ready = true;
                        block.Node = s_lru.AddFirst(block);
                        s_bytes += block.Bytes;
                        Evict(block);
                    }
                }
            }
        }
        catch
        {
            Release(block);
            throw;
        }

        return new Lease(block);
    }

    // Drops least recently used blocks until the budget holds, never the block just computed.
    private static void Evict(Block keep)
    {
        while (s_bytes > BudgetBytes && s_lru.Last is { } last && last.Value != keep)
        {
            Block victim = last.Value;
            s_lru.RemoveLast();
            victim.Node = null;
            victim.Evicted = true;
            if (victim.Owner.TryGetTarget(out Store? owner))
            {
                owner.Blocks.Remove(victim.Key);
            }

            s_bytes -= victim.Bytes;
            if (victim.Refs == 0)
            {
                Return(victim);
            }
        }
    }

    private static void Release(Block block)
    {
        lock (s_lock)
        {
            block.Refs--;
            if (!block.Ready && block.Refs == 0)
            {
                // A computation that failed leaves nothing behind.
                if (block.Owner.TryGetTarget(out Store? owner))
                {
                    owner.Blocks.Remove(block.Key);
                }
            }

            if (block.Evicted && block.Refs == 0)
            {
                Return(block);
            }
        }
    }

    private static void Return(Block block)
    {
        if (block.Pixels is { } p)
        {
            block.Pixels = null;
            Pools<Rgba32>.Pool.Return(p);
        }
    }

    internal readonly struct Lease : IDisposable
    {
        private readonly Block _block;

        internal Lease(Block block) => _block = block;

        /// <summary>Gets the pixels of <see cref="Rect"/>, or <see langword="null"/> when the block is entirely transparent.</summary>
        public Rgba32[]? Pixels => _block.Pixels;

        /// <summary>Gets the rectangle the pixels cover, in the anchored grid.</summary>
        public PxRect Rect => _block.Rect;

        public void Dispose() => Release(_block);
    }

    /// <summary>The blocks of one layer. It is reachable only from the layer (the blocks point back weakly), so when the layer's scene is
    /// collected its finalizer gives the blocks back instead of leaving them to age out of the budget.</summary>
    internal sealed class Store
    {
        public Dictionary<FilterBlockKey, Block> Blocks { get; } = [];

        ~Store()
        {
            lock (s_lock)
            {
                foreach (Block block in Blocks.Values)
                {
                    if (block.Node is { } node)
                    {
                        s_lru.Remove(node);
                        block.Node = null;
                        s_bytes -= block.Bytes;
                    }

                    block.Evicted = true;
                    if (block.Refs == 0)
                    {
                        Return(block);
                    }
                }

                Blocks.Clear();
            }
        }
    }

    internal sealed class Block(WeakReference<Store> owner, FilterBlockKey key)
    {
        public WeakReference<Store> Owner { get; } = owner;

        public FilterBlockKey Key { get; } = key;

        public object Gate { get; } = new();

        public Rgba32[]? Pixels { get; set; }

        public PxRect Rect { get; set; }

        public long Bytes { get; set; }

        public int Refs { get; set; }

        public bool Ready { get; set; }

        public bool Evicted { get; set; }

        public LinkedListNode<Block>? Node { get; set; }
    }
}
