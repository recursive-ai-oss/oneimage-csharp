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

/// <summary>The default store: shared <see cref="ArrayPool{T}"/> buffers.</summary>
internal sealed class PooledTileStore : ITileStore
{
    public static PooledTileStore Instance { get; } = new();

    public byte[] Rent(int length, bool clear)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(length);
        if (clear)
        {
            buf.AsSpan(0, length).Clear();
        }

        return buf;
    }

    public void Return(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);
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
