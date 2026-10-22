using System.Diagnostics;
using Xunit;

namespace OneImage.Tests.Filter;

/// <summary>Tile locality of the filters and their behavior on a huge sequential (streaming) source.</summary>
[Collection(MemoryMeasurement.Name)]
public class FilterLocalityStreamingTests
{
    private static int TilesOf(ImageRect r) => r.IsEmpty ? 0 : (((r.Right - 1) >> 7) - (r.X >> 7) + 1) * (((r.Bottom - 1) >> 7) - (r.Y >> 7) + 1);

    private static void CheckOneTile(Func<Image, Image> op, int tx, int ty, int expectedTiles)
    {
        var src = new Resample.RawBytesNode(new byte[1000 * 1000 * 4], 1000, 1000, PixelFormat.Rgba8, cacheable: false);
        Image img = op(Image.FromNode(src));
        Node node = img.Frames[0].Node;
        ImageRect tile = node.TileRect(tx, ty);
        ImageRect need = node.RequiredInput(0, tile);
        img.CopyPixelsTo<Rgba32>(new Rgba32[tile.Width * tile.Height], tile, maxDegree: 1);
        Assert.Equal(expectedTiles, src.Computed);
        Assert.Equal(TilesOf(need), src.Computed);
    }

    [Fact]
    public void InteriorTileReadsOnlyItsRegionPlusTheMargin()
    {
        // A margin that stays inside the neighbouring tiles: 3x3 tiles. A margin of 1 pixel already reaches the neighbours.
        CheckOneTile(i => i.GaussianBlur(3), 3, 3, 9);
        CheckOneTile(i => i.GaussianBlur(0.2), 3, 3, 9);
        CheckOneTile(i => i.Median(5), 3, 3, 9);
        CheckOneTile(i => i.Sobel(), 3, 3, 9);
        CheckOneTile(i => i.Sharpen(1, 2), 3, 3, 9);
        CheckOneTile(i => i.Convolve([1f, 1f, 1f], 3, 1), 3, 3, 3);
    }

    [Fact]
    public void CornerAndEdgeTilesReadFewerTiles()
    {
        CheckOneTile(i => i.GaussianBlur(3), 0, 0, 4);
        CheckOneTile(i => i.GaussianBlur(3), 7, 7, 4);
        CheckOneTile(i => i.GaussianBlur(3, edge: EdgeMode.Mirror), 0, 3, 6);
    }

    [Fact]
    public void MarginsUpToTheLimitStillReachOnlyOneNeighbour()
    {
        // Sigma 14 has radius 42; radius 127 is the most any filter reads, still less than one tile.
        CheckOneTile(i => i.GaussianBlur(14), 3, 3, 9);
        CheckOneTile(i => i.Median(127), 3, 3, 9);
    }

    private static Image LoadGen(int w, int h) =>
        Image.Load(GenFormat.Header(w, h), new LoadOptions { Configuration = GenFormat.Instance.Configuration() });

    private static long Run(Image img, string label, long frameBytes)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                Thread.Sleep(2);
            }
        });
        sampler.Start();
        var sw = Stopwatch.StartNew();
        long check = 0;
        try
        {
            // Read strip by strip, top to bottom, the way a sequential encoder would.
            Rgba32[] strip = new Rgba32[img.Width * Tile.Size];
            for (int y = 0; y < img.Height; y += Tile.Size)
            {
                int hh = Math.Min(Tile.Size, img.Height - y);
                img.Frames[0].CopyPixelsTo<Rgba32>(strip, new ImageRect(0, y, img.Width, hh));
                check += strip[0].R + strip[(hh * img.Width) - 1].G;
            }
        }
        finally
        {
            stop.Cancel();
            sampler.Join();
        }

        sw.Stop();
        long growth = peak - baseline;
        TestNotes.Add($"{label}: peak managed growth {growth / (1 << 20)} MiB ({100.0 * growth / frameBytes:F2}% of the {frameBytes / (1 << 20)} MiB frame), {sw.ElapsedMilliseconds} ms, check {check}");
        return growth;
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(16.0)]
    [InlineData(20.0)]
    [InlineData(150.0)]
    public void GaussianOnHugeSequentialSourceStaysInBoundedMemory(double sigma)
    {
        const int W = 8000;
        const int H = 30000;
        long frameBytes = (long)W * H * 4;
        Image img = LoadGen(W, H);
        Assert.True(img.Frames[0].Node.Streaming);
        long growth = Run(img.GaussianBlur(sigma), $"gaussian sigma {sigma} on {W}x{H}", frameBytes);
        Assert.True(growth < frameBytes / 10, $"peak growth {growth} exceeds 10% of the frame ({frameBytes})");
    }

    [Fact]
    public void OtherFiltersOnHugeSequentialSourceStayInBoundedMemory()
    {
        const int W = 8000;
        const int H = 24000;
        long frameBytes = (long)W * H * 4;
        foreach ((string name, Func<Image, Image> op) in new (string, Func<Image, Image>)[]
        {
            ("sharpen 2", i => i.Sharpen(1, 2)),
            ("median 3", i => i.Median(3)),
            ("sobel", i => i.Sobel()),
            ("box 40", i => i.BoxBlur(40)),
        })
        {
            long growth = Run(op(LoadGen(W, H)), name, frameBytes);
            Assert.True(growth < frameBytes / 10, $"{name}: peak growth {growth} exceeds 10% of the frame ({frameBytes})");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void StreamedResultEqualsMaterializedResult(int which)
    {
        const int W = 1700;
        const int H = 5200;
        Func<Image, Image> op = which switch
        {
            0 => i => i.GaussianBlur(3),
            1 => i => i.Sharpen(1.2, 2),
            2 => i => i.Median(2),
            3 => i => i.Sobel(),
            4 => i => i.BoxBlur(7),
            _ => i => i.GaussianBlur(25),
        };
        Image streamed = LoadGen(W, H);
        Assert.True(streamed.Frames[0].Node.Streaming);
        Rgba32[] source = streamed.ToArray<Rgba32>();
        Image materialized = Image.FromPixels<Rgba32>(source, W, H);
        Image viaStream = op(LoadGen(W, H));
        Image reference = op(materialized);
        Assert.Equal(reference.Format, viaStream.Format);
        if (reference.Format == PixelFormat.Rgba8)
        {
            Assert.True(reference.ToArray<Rgba32>().AsSpan().SequenceEqual(viaStream.ToArray<Rgba32>()), "streamed and materialized results differ");
        }
        else
        {
            Assert.True(reference.ToArray<Gray8>().AsSpan().SequenceEqual(viaStream.ToArray<Gray8>()), "streamed and materialized results differ");
        }
    }
}
