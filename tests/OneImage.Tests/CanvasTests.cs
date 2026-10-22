using System.Buffers;
using OneImage.Kernels;

[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace OneImage.Tests;

public class CanvasTests
{
    public static TheoryData<int, int> Sizes => new() { { 1, 1 }, { 129, 257 }, { 1000, 3 }, { 128, 128 }, { 300, 130 } };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgba32RoundTrips(int w, int h) => RoundTrip(w, h, r => new Rgba32((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256)));

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgba64RoundTrips(int w, int h) => RoundTrip(w, h, r => new Rgba64((ushort)r.Next(65536), (ushort)r.Next(65536), (ushort)r.Next(65536), (ushort)r.Next(65536)));

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Gray8RoundTrips(int w, int h) => RoundTrip(w, h, r => new Gray8((byte)r.Next(256)));

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RgbaFloatRoundTrips(int w, int h) => RoundTrip(w, h, r => new RgbaFloat(r.NextSingle(), r.NextSingle() * 100f, -r.NextSingle(), r.NextSingle()));

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RgbaHalfRoundTrips(int w, int h) => RoundTrip(w, h, r => new RgbaHalf((Half)r.NextSingle(), (Half)r.NextSingle(), (Half)r.NextSingle(), (Half)r.NextSingle()));

    private static void RoundTrip<T>(int w, int h, Func<Random, T> make)
        where T : unmanaged, IPixel<T>
    {
        var rnd = new Random(w * 31 + h);
        T[] a = Enumerable.Range(0, w * h).Select(_ => make(rnd)).ToArray();
        T[] b = Enumerable.Range(0, w * h).Select(_ => make(rnd)).ToArray();
        var full = new ImageRect(0, 0, w, h);

        // Write through Lock, read back through every other path.
        using (var canvas = new Canvas(w, h, T.Format))
        {
            using (PixelLock<T> l = canvas.Lock<T>(full, LockAccess.Write))
            {
                for (int y = 0; y < h; y++)
                {
                    a.AsSpan(y * w, w).CopyTo(l.GetRow(y));
                }
            }

            AssertAllPaths(canvas, a, w, h);

            // Write through CopyFrom, read back again.
            canvas.CopyFrom<T>(b, full);
            AssertAllPaths(canvas, b, w, h);

            // Partial rectangle write across tile edges leaves the rest alone.
            if (w > 3 && h > 3)
            {
                var sub = new ImageRect(1, 1, w - 2, h - 2);
                canvas.CopyFrom<T>(a.AsSpan(0, sub.Width * sub.Height), sub);
                T[] got = new T[w * h];
                canvas.CopyTo<T>(got, full);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        T expect = x >= 1 && y >= 1 && x < w - 1 && y < h - 1 ? a[((y - 1) * sub.Width) + x - 1] : b[(y * w) + x];
                        Assert.Equal(expect, got[(y * w) + x]);
                    }
                }
            }
        }
    }

    private static void AssertAllPaths<T>(Canvas canvas, T[] expected, int w, int h)
        where T : unmanaged, IPixel<T>
    {
        var full = new ImageRect(0, 0, w, h);
        T[] viaCopy = new T[w * h];
        canvas.CopyTo<T>(viaCopy, full);
        Assert.Equal(expected, viaCopy);

        using (PixelLock<T> l = canvas.Lock<T>(full, LockAccess.Read))
        {
            for (int y = 0; y < h; y++)
            {
                Assert.True(l.ReadRow(y).SequenceEqual(expected.AsSpan(y * w, w)));
            }
        }

        // A lock confined to one tile edits memory in place.
        int lw = Math.Min(w, 5);
        int lh = Math.Min(h, 5);
        using (PixelLock<T> l = canvas.Lock<T>(new ImageRect(0, 0, lw, lh), LockAccess.Read))
        {
            Assert.True(l.ReadRow(lh - 1).SequenceEqual(expected.AsSpan((lh - 1) * w, lw)));
        }

        Image snap = canvas.Snapshot();
        Assert.Equal(expected, snap.ToArray<T>());
        T[] viaSnap = new T[w * h];
        snap.CopyPixelsTo<T>(viaSnap, full);
        Assert.Equal(expected, viaSnap);
    }

    [Fact]
    public void IndexerReadsAndWritesRgba32()
    {
        using var canvas = new Canvas(200, 200, PixelFormat.Rgba8);
        canvas[150, 7] = new Rgba32(1, 2, 3, 4);
        Assert.Equal(new Rgba32(1, 2, 3, 4), canvas[150, 7]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), canvas[151, 7]);
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas[200, 0]);
    }

    [Fact]
    public void IndexerConvertsFromOtherFormats()
    {
        using var canvas = new Canvas(10, 10, PixelFormat.Gray8);
        canvas[3, 3] = new Rgba32(255, 255, 255, 255);
        Assert.Equal(new Gray8(255), canvas.Snapshot().ToArray<Gray8>()[(3 * 10) + 3]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), canvas[3, 3]);
    }

    [Fact]
    public void LockRejectsWrongPixelType()
    {
        using var canvas = new Canvas(10, 10, PixelFormat.Rgba8);
        Assert.Throws<ArgumentException>(() => canvas.Lock<Gray8>(new ImageRect(0, 0, 1, 1), LockAccess.Read));
        using PixelLock<Rgba32> l = canvas.Lock<Rgba32>(new ImageRect(0, 0, 1, 1), LockAccess.Read);
        Assert.Throws<ImageOperationException>(() => l.GetRow(0));
    }

    [Fact]
    public void OverLimitCanvasIsRejectedBeforeAllocation()
    {
        long before = Tile.RentedCount;
        Assert.Throws<ImageLimitException>(() => new Canvas(70000, 10, PixelFormat.Gray8));
        Assert.Throws<ImageLimitException>(() => new Canvas(20000, 20000, PixelFormat.Gray8));
        Assert.Throws<ImageLimitException>(() => new Canvas(int.MaxValue, int.MaxValue, PixelFormat.Gray8, ImageLimits.Unrestricted));
        Assert.Equal(before, Tile.RentedCount);
    }

    [Fact]
    public void DisposeReturnsPooledTilesAndUndisposedIsGcSafe()
    {
        // Images dropped by earlier tests give their cached tiles back from a finalizer; let that finish first so it cannot
        // move the counters in the middle of this test.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long outstanding = Tile.RentedCount - Tile.ReturnedCount;
        var canvas = new Canvas(300, 300, PixelFormat.Rgba8);
        canvas.CopyFrom<Rgba32>(new Rgba32[300 * 300], new ImageRect(0, 0, 300, 300));
        Assert.Equal(9, canvas.AllocatedTiles);
        Assert.Equal(outstanding + 9, Tile.RentedCount - Tile.ReturnedCount);
        canvas.Dispose();
        canvas.Dispose();
        Assert.Equal(outstanding, Tile.RentedCount - Tile.ReturnedCount);
        Assert.Throws<ObjectDisposedException>(() => canvas.Snapshot());

        static void Forget()
        {
            var c = new Canvas(500, 500, PixelFormat.Gray8);
            c.CopyFrom<Gray8>(new Gray8[500 * 500], new ImageRect(0, 0, 500, 500));
        }

        Forget();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public void SnapshotSurvivesCanvasDispose()
    {
        var canvas = new Canvas(200, 200, PixelFormat.Gray8);
        canvas.CopyFrom<Gray8>(Enumerable.Repeat(new Gray8(9), 200 * 200).ToArray(), new ImageRect(0, 0, 200, 200));
        Image snap = canvas.Snapshot();
        canvas.CopyFrom<Gray8>([new Gray8(1)], new ImageRect(0, 0, 1, 1));
        canvas.Dispose();
        Gray8[] px = snap.ToArray<Gray8>();
        Assert.All(px, p => Assert.Equal(9, p.V));
    }

    [Fact]
    public void SnapshotCopiesNoTileData()
    {
        using var canvas = new Canvas(1000, 1000, PixelFormat.Rgba8);
        canvas.CopyFrom<Rgba32>(new Rgba32[1000 * 1000], new ImageRect(0, 0, 1000, 1000));
        long rented = Tile.RentedCount;
        long copied = Tile.CopyCount;
        Image s1 = canvas.Snapshot();
        Image s2 = canvas.Snapshot();
        Assert.Equal(rented, Tile.RentedCount);
        Assert.Equal(copied, Tile.CopyCount);

        // The first write after a snapshot copies exactly the touched tile.
        canvas[5, 5] = new Rgba32(9, 9, 9, 9);
        Assert.Equal(copied + 1, Tile.CopyCount);
        canvas[6, 6] = new Rgba32(9, 9, 9, 9);
        Assert.Equal(copied + 1, Tile.CopyCount);
        Assert.Equal(default, s1.ToArray<Rgba32>()[(5 * 1000) + 5]);
        Assert.Equal(default, s2.ToArray<Rgba32>()[(5 * 1000) + 5]);
    }

    [Fact]
    public void ConcurrentReadersSeeSnapshotWhileCanvasChanges()
    {
        for (int iteration = 0; iteration < 5; iteration++)
        {
            const int W = 700;
            const int H = 400;
            var rnd = new Random(iteration);
            Rgba32[] a = Enumerable.Range(0, W * H).Select(_ => new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), 1, 255)).ToArray();
            Rgba32[] b = Enumerable.Range(0, W * H).Select(_ => new Rgba32((byte)rnd.Next(256), 2, (byte)rnd.Next(256), 255)).ToArray();
            using var canvas = new Canvas(W, H, PixelFormat.Rgba8);
            canvas.CopyFrom<Rgba32>(a, new ImageRect(0, 0, W, H));
            Image snap = canvas.Snapshot();
            using var go = new ManualResetEventSlim();
            int bad = 0;
            Task[] readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                go.Wait();
                for (int i = 0; i < 10; i++)
                {
                    if (!snap.ToArray<Rgba32>(maxDegree: 2).AsSpan().SequenceEqual(a))
                    {
                        Interlocked.Increment(ref bad);
                    }
                }
            })).ToArray();
            go.Set();
            for (int y = 0; y < H; y += 7)
            {
                int rows = Math.Min(7, H - y);
                canvas.CopyFrom<Rgba32>(b.AsSpan(y * W, rows * W), new ImageRect(0, y, W, rows));
            }

            Task.WaitAll(readers);
            Assert.Equal(0, bad);
            Assert.True(canvas.Snapshot().ToArray<Rgba32>().AsSpan().SequenceEqual(b));
            Assert.True(snap.ToArray<Rgba32>().AsSpan().SequenceEqual(a));
        }
    }

    [Fact]
    public void ToCanvasComputesTilesOnFirstTouch()
    {
        var src = new PatternNode(1000, 1000);
        Image img = Image.FromNode(src);
        using Canvas canvas = img.ToCanvas();
        Assert.Equal(0, src.Computed);
        Assert.Equal(PatternNode.Expected(5, 5), canvas[5, 5]);
        Assert.Equal(1, src.Computed);
        canvas[6, 5] = new Rgba32(1, 1, 1, 1);
        Assert.Equal(PatternNode.Expected(7, 5), canvas[7, 5]);
        Assert.Equal(new Rgba32(1, 1, 1, 1), canvas[6, 5]);
        Assert.True(src.Computed <= 2);
        Assert.Equal(PatternNode.Expected(900, 900), canvas.Snapshot().ToArray<Rgba32>()[(900 * 1000) + 900]);
    }
}
