using OneImage.Drawing;
using Xunit;
using Path = OneImage.Drawing.Path;

namespace OneImage.Tests.Drawing;

/// <summary>In-place drawing with and without <see cref="Canvas.Snapshot"/>: snapshots keep their pixels, and one-shot work leaves no tiles behind.</summary>
[Collection(MemoryMeasurement.Name)]
public class CanvasSnapshotDrawTests
{
    private const int W = 400, H = 300;
    private const int Steps = 7;
    private static readonly Rgba32 Red = new(220, 30, 40, 255);
    private static readonly Rgba32 Blue = new(20, 40, 230, 200);
    private static readonly Rgba32[] Patch = Enumerable.Repeat(Blue, 150 * 150).ToArray();
    private static readonly Pen BluePen = new(Brush.Solid(Blue), 9, StrokeJoin.Round);

    private static Canvas NewCanvas()
    {
        var c = new Canvas(W, H, PixelFormat.Rgba8);
        c.Clear(new Rgba32(255, 255, 255, 255));
        return c;
    }

    private static void Draw(Canvas c, int step)
    {
        switch (step % Steps)
        {
            case 0: c.Fill(Red, Path.Circle(200, 150, 120)); break;
            case 1: c.Draw(BluePen, Path.Circle(200, 150, 140)); break;
            case 2: c.Clear(new Rgba32(10, 200, 10, 255)); break;
            case 3:
                using (PixelLock<Rgba32> l = c.Lock<Rgba32>(new ImageRect(100, 100, 200, 90), LockAccess.ReadWrite))
                {
                    for (int y = 0; y < l.Height; y++)
                    {
                        l.GetRow(y).Fill(Red);
                    }
                }

                break;
            case 4: c.CopyFrom<Rgba32>(Patch, new ImageRect(50, 40, 150, 150)); break;
            case 5: c.Fill(Brush.Solid(Blue), Path.Circle(150, 120, 100), linear: true); break;
            default: c.Fill(Brush.Solid(Red), Path.Circle(250, 160, 90), linear: true, clip: Path.Circle(200, 150, 60)); break;
        }
    }

    [Fact]
    public void SnapshotKeepsOldPixelsWhileCanvasIsDrawn()
    {
        using Canvas withSnapshots = NewCanvas();
        using Canvas plain = NewCanvas();
        var snapshots = new List<(Image Image, Rgba32[] Pixels)>();
        for (int step = 0; step < 2 * Steps; step++)
        {
            Image s = withSnapshots.Snapshot();
            snapshots.Add((s, s.ToArray<Rgba32>()));
            Draw(withSnapshots, step);
            Draw(plain, step);

            // Output bytes do not depend on whether snapshots exist, and every earlier snapshot still shows its own pixels.
            Assert.Equal(plain.Snapshot().ToArray<Rgba32>(), withSnapshots.Snapshot().ToArray<Rgba32>());
            foreach ((Image image, Rgba32[] pixels) in snapshots)
            {
                Assert.Equal(pixels, image.ToArray<Rgba32>());
            }
        }
    }

    [Fact]
    public void DrawingWithoutSnapshotsKeepsTilesAndRentalsSteady()
    {
        using Canvas c = NewCanvas();
        for (int step = 0; step < Steps; step++)
        {
            Draw(c, step);
        }

        int canvasTiles = c.AllocatedTiles;
        long cache = TileCache.Global.Bytes;
        long outstanding = Tile.RentedCount - Tile.ReturnedCount;
        for (int step = 0; step < 10 * Steps; step++)
        {
            Draw(c, step);
        }

        // Every replaced tile came back to the pool and the one-shot graphs left nothing in the shared cache.
        Assert.Equal(canvasTiles, c.AllocatedTiles);
        Assert.Equal(outstanding, Tile.RentedCount - Tile.ReturnedCount);
        Assert.Equal(cache, TileCache.Global.Bytes);
    }

    [Fact]
    public void LinearFillAfterSnapshotLeavesNothingInTheCache()
    {
        using Canvas c = NewCanvas();
        long cache = TileCache.Global.Bytes;
        Image before = c.Snapshot();
        Rgba32[] expected = before.ToArray<Rgba32>();
        c.Fill(Brush.Solid(Blue), Path.Circle(200, 150, 120), linear: true);
        Assert.Equal(cache, TileCache.Global.Bytes);
        Assert.Equal(expected, before.ToArray<Rgba32>());
        Assert.NotEqual(expected, c.Snapshot().ToArray<Rgba32>());
    }
}
