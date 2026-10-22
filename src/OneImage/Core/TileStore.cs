using System.Buffers;

namespace OneImage;

/// <summary>
/// Where the pixel memory of tiles lives. <see cref="Tile"/> rents its buffer here and returns it when the last reference is
/// released; everything above (nodes, canvases, caches) only ever touches tile memory through <see cref="Tile.Span"/>.
/// The v1 store is a pool of managed arrays. A store that spills cold tiles to disk plugs in the same way: it hands out
/// buffers, takes them back, and gains residency callbacks that <see cref="Tile.Span"/> invokes, with no change to ops or the public API.
/// </summary>
internal interface ITileStore
{
    /// <summary>Returns a buffer of at least <paramref name="length"/> bytes. When <paramref name="clear"/> is set the first <paramref name="length"/> bytes are zero; otherwise their content is unspecified.</summary>
    /// <param name="length">Bytes needed.</param>
    /// <param name="clear">Whether the memory must be zeroed.</param>
    /// <returns>The buffer.</returns>
    byte[] Rent(int length, bool clear);

    /// <summary>Takes back a buffer obtained from <see cref="Rent"/>; it is never touched again.</summary>
    /// <param name="buffer">The buffer.</param>
    void Return(byte[] buffer);
}

/// <summary>
/// The default store: power-of-two sized managed arrays kept in one process-wide queue per size, up to a byte cap. Tiles are rented
/// on one worker and released on another, which the per-core buckets of <see cref="ArrayPool{T}"/> handle poorly (buffers released
/// on a full bucket are dropped), so a burst of tiles from a thumbnail would otherwise leave fresh garbage behind every image.
/// </summary>
internal sealed class PooledTileStore : ITileStore
{
    private const int MinShift = 12;
    private const int MaxShift = 22;
    private const long MaxPooledBytes = 48L << 20;

    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]>[] _queues = CreateQueues();
    private long _pooledBytes;

    public static PooledTileStore Instance { get; } = new();

    public byte[] Rent(int length, bool clear)
    {
        int shift = Shift(length);
        if (shift > MaxShift)
        {
            return ArrayPool<byte>.Shared.Rent(length);
        }

        byte[] buf;
        if (_queues[shift - MinShift].TryDequeue(out byte[]? pooled))
        {
            Interlocked.Add(ref _pooledBytes, -pooled.Length);
            buf = pooled;
        }
        else
        {
            buf = GC.AllocateUninitializedArray<byte>(1 << shift);
        }

        if (clear)
        {
            buf.AsSpan(0, length).Clear();
        }

        return buf;
    }

    public void Return(byte[] buffer)
    {
        int shift = Shift(buffer.Length);
        if (shift > MaxShift || buffer.Length != 1 << shift)
        {
            // Not one of ours (a larger rental, or a size that is not a power of two): the shared pool decides.
            ArrayPool<byte>.Shared.Return(buffer);
            return;
        }

        if (Interlocked.Add(ref _pooledBytes, buffer.Length) > MaxPooledBytes)
        {
            Interlocked.Add(ref _pooledBytes, -buffer.Length);
            return;
        }

        _queues[shift - MinShift].Enqueue(buffer);
    }

    private static int Shift(int length) => Math.Max(MinShift, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(1, length) - 1));

    private static System.Collections.Concurrent.ConcurrentQueue<byte[]>[] CreateQueues()
    {
        var q = new System.Collections.Concurrent.ConcurrentQueue<byte[]>[MaxShift - MinShift + 1];
        for (int i = 0; i < q.Length; i++)
        {
            q[i] = new();
        }

        return q;
    }
}

/// <summary>Holds the store new tiles are rented from. A tile returns its memory to the store it came from, so swapping is safe at any time.</summary>
internal static class TileStores
{
    private static ITileStore s_current = PooledTileStore.Instance;

    public static ITileStore Current
    {
        get => Volatile.Read(ref s_current);
        set => Volatile.Write(ref s_current, value ?? throw new ArgumentNullException(nameof(value)));
    }
}
