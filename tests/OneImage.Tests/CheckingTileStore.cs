using System.Runtime.CompilerServices;

namespace OneImage.Tests;

/// <summary>
/// A test-only tile store: every buffer is a fresh array, uninitialized memory is filled with a marker so code that reads
/// tile bytes it never wrote shows up as wrong pixels, and buffers returned twice (or never rented here) are caught.
/// Set ONEIMAGE_TEST_TILESTORE=checking to run the whole suite against it.
/// </summary>
internal sealed class CheckingTileStore : ITileStore
{
    private readonly HashSet<byte[]> _live = new(ReferenceEqualityComparer.Instance);
    private long _rents;

    public long Rents => Interlocked.Read(ref _rents);

    public int Outstanding
    {
        get
        {
            lock (_live)
            {
                return _live.Count;
            }
        }
    }

    public byte[] Rent(int length, bool clear)
    {
        byte[] buf = new byte[length];
        if (!clear)
        {
            buf.AsSpan().Fill(0xCD);
        }

        lock (_live)
        {
            _live.Add(buf);
        }

        Interlocked.Increment(ref _rents);
        return buf;
    }

    public void Return(byte[] buffer)
    {
        lock (_live)
        {
            if (!_live.Remove(buffer))
            {
                throw new InvalidOperationException("A tile buffer was returned twice or did not come from this store.");
            }
        }
    }
}

internal static class TestStoreSelection
{
    [ModuleInitializer]
    internal static void Select()
    {
        if (Environment.GetEnvironmentVariable("ONEIMAGE_TEST_TILESTORE") == "checking")
        {
            TileStores.Current = new CheckingTileStore();
        }
    }
}

public class TileStoreTests
{
    [Fact]
    public void TilesRentAndReturnThroughThePluggedStore()
    {
        ITileStore previous = TileStores.Current;
        var store = new CheckingTileStore();
        TileStores.Current = store;
        try
        {
            var canvas = new Canvas(300, 300, PixelFormat.Rgba8);
            canvas.CopyFrom<Rgba32>(new Rgba32[300 * 300], new ImageRect(0, 0, 300, 300));
            Assert.Equal(9, store.Outstanding);
            Image snap = canvas.Snapshot();
            canvas[1, 1] = new Rgba32(1, 2, 3, 4);
            Assert.Equal(10, store.Outstanding);
            Assert.Equal(default, snap.ToArray<Rgba32>()[301]);
            canvas.Dispose();

            // Tiles a snapshot can still see are left to it (and the garbage collector), not returned.
            Assert.Equal(9, store.Outstanding);
            Assert.Equal(default, snap.ToArray<Rgba32>()[301]);

            var plain = new Canvas(300, 300, PixelFormat.Rgba8);
            plain.CopyFrom<Rgba32>(new Rgba32[300 * 300], new ImageRect(0, 0, 300, 300));
            Assert.Equal(18, store.Outstanding);
            plain.Dispose();
            Assert.Equal(9, store.Outstanding);
        }
        finally
        {
            TileStores.Current = previous;
        }
    }

    [Fact]
    public void UninitializedTileMemoryIsNeverExposed()
    {
        ITileStore previous = TileStores.Current;
        TileStores.Current = new CheckingTileStore();
        try
        {
            // Partly written canvas tile: unwritten pixels must read as zero, not as the store's marker.
            using var canvas = new Canvas(200, 200, PixelFormat.Gray8);
            canvas[3, 3] = new Rgba32(255, 255, 255, 255);
            Gray8[] px = canvas.Snapshot().ToArray<Gray8>();
            Assert.Equal(1, px.Count(p => p.V != 0));
        }
        finally
        {
            TileStores.Current = previous;
        }
    }
}
