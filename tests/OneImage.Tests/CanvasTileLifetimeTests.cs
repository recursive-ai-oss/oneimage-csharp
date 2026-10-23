using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage.Tests;

/// <summary>Tiles replaced by copy-on-write go back to the pool once no canvas table or snapshot holds them, and never while one can still read them.</summary>
[Collection(MemoryMeasurement.Name)]
public class CanvasTileLifetimeTests
{
    private const int W = 300, H = 260;

    private static long Outstanding => Tile.RentedCount - Tile.ReturnedCount;

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static Canvas Filled(byte v)
    {
        var c = new Canvas(W, H, PixelFormat.Rgba8);
        c.Clear(new Rgba32(v, v, v, 255));
        return c;
    }

    [Fact]
    public void DroppedSnapshotsReturnReplacedTiles()
    {
        Settle();
        long baseline = Outstanding;
        using (Canvas c = Filled(1))
        {
            DrawAfterSnapshots(c);
            Settle();

            // Only the canvas's own tiles are still out.
            Assert.Equal(baseline + c.AllocatedTiles, Outstanding);
        }

        Settle();
        Assert.Equal(baseline, Outstanding);
    }

    // A snapshot's tile table only gets a finalizer once it has outlived a collection (tables that die young are left to the collector),
    // so each snapshot here is alive across one.
    private static void SnapAcrossCollection(Canvas c)
    {
        Image s = c.Snapshot();
        GC.Collect();
        GC.KeepAlive(s);
    }

    private static void DrawAfterSnapshots(Canvas c)
    {
        for (int i = 0; i < 6; i++)
        {
            SnapAcrossCollection(c);
            c.Clear(new Rgba32((byte)(10 + i), 0, 0, 255));
            SnapAcrossCollection(c);
            c.Fill(new Rgba32(0, (byte)i, 0, 255), Path.Circle(150, 130, 100));
            SnapAcrossCollection(c);
            c.Fill(Brush.Solid(new Rgba32(0, 0, (byte)i, 255)), Path.Circle(100, 100, 70), linear: true);
            SnapAcrossCollection(c);
            c.Clip(Path.Circle(120, 120, 90), im => im.Invert());
        }
    }

    [Fact]
    public void DisposedCanvasKeepsSnapshotsValidThenReleasesThem()
    {
        Settle();
        long baseline = Outstanding;
        ReadSnapshotOfDisposedCanvas(baseline);
        Settle();
        Assert.Equal(baseline, Outstanding);
    }

    // The snapshot lives only in this frame. A test method's own locals stay reachable until it returns when the JIT does not
    // optimize (a Debug build, which is what `dotnet test` runs by default), so "the snapshot is dropped" has to mean "the frame
    // that held it is gone", not "the local was set to null".
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadSnapshotOfDisposedCanvas(long baseline)
    {
        Image snap = KeepOne(out Rgba32[] expected);
        Settle();
        Assert.Equal(expected, snap.ToArray<Rgba32>());
        Assert.True(Outstanding > baseline);
    }

    private static Image KeepOne(out Rgba32[] expected)
    {
        Canvas c = Filled(7);
        Image s = c.Snapshot();
        c.Fill(new Rgba32(200, 0, 0, 255), Path.Circle(100, 100, 80));
        c.Dispose();
        expected = new Rgba32[W * H];
        Array.Fill(expected, new Rgba32(7, 7, 7, 255));
        return s;
    }

    [Fact]
    public void FromPixelsGivesItsTileBackWhenTheImageIsDropped()
    {
        Settle();
        long baseline = Outstanding;
        Make();
        Settle();
        Assert.Equal(baseline, Outstanding);

        static void Make()
        {
            Image im = Image.FromPixels<Rgba32>(new Rgba32[50 * 50], 50, 50);
            Assert.Equal(50, im.ToArray<Rgba32>().Length / 50);
        }
    }

    [Fact]
    public void ConcurrentSnapshotReadersSeeTheirOwnPixelsWhileTheWriterReplacesTiles()
    {
        const int Frames = 400;
        var shadow = new Rgba32[W * H];
        var queue = new BlockingCollection<(Image Image, Rgba32[] Expected)>(boundedCapacity: 32);
        var failures = new ConcurrentQueue<string>();
        using var canvas = new Canvas(W, H, PixelFormat.Rgba8);
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            foreach ((Image image, Rgba32[] expected) in queue.GetConsumingEnumerable())
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    Rgba32[] got = image.ToArray<Rgba32>();
                    if (!got.AsSpan().SequenceEqual(expected))
                    {
                        failures.Enqueue("snapshot content changed");
                        return;
                    }

                    Thread.Yield();
                }
            }
        })).ToArray();

        var rnd = new Random(11);
        for (int f = 0; f < Frames; f++)
        {
            int w = rnd.Next(1, 200), h = rnd.Next(1, 200), x = rnd.Next(0, W - w), y = rnd.Next(0, H - h);
            var px = new Rgba32[w * h];
            Array.Fill(px, new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)f, 255));
            switch (f % 4)
            {
                case 0:
                case 1:
                    canvas.CopyFrom<Rgba32>(px, new ImageRect(x, y, w, h));
                    for (int r = 0; r < h; r++)
                    {
                        px.AsSpan(r * w, w).CopyTo(shadow.AsSpan(((y + r) * W) + x, w));
                    }

                    break;
                case 2:
                    Rgba32 c = px[0];
                    canvas.Clear(c);
                    Array.Fill(shadow, c);
                    break;
                default:
                    canvas.Clip(Path.Rectangle(x, y, w, h), im => im.Invert(), antialias: false);
                    for (int r = 0; r < h; r++)
                    {
                        for (int cx = 0; cx < w; cx++)
                        {
                            ref Rgba32 p = ref shadow[((y + r) * W) + x + cx];
                            p = new Rgba32((byte)(255 - p.R), (byte)(255 - p.G), (byte)(255 - p.B), p.A);
                        }
                    }

                    break;
            }

            // Some snapshots are queued for readers, some are dropped at once; the collector runs now and then so finalizers fire mid-run.
            Image snap = canvas.Snapshot();
            if (f % 3 != 2)
            {
                queue.Add((snap, (Rgba32[])shadow.Clone()));
            }

            if (f % 10 == 9)
            {
                GC.Collect();
            }
        }

        queue.CompleteAdding();
        Task.WaitAll(readers);
        Assert.Empty(failures);
        Assert.Equal(shadow, canvas.Snapshot().ToArray<Rgba32>());
    }
}
