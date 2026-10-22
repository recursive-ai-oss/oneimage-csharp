using System.Diagnostics;
using Xunit;

namespace OneImage.Tests.Geometry;

public class GeometryLocalityTests
{
    private static Resample.RawBytesNode Source(int w, int h) => new(new byte[w * h * 4], w, h, PixelFormat.Rgba8, cacheable: false);

    private static int TilesOf(ImageRect r) => r.IsEmpty ? 0 : (((r.Right - 1) >> 7) - (r.X >> 7) + 1) * (((r.Bottom - 1) >> 7) - (r.Y >> 7) + 1);

    private static void CheckOneTile(Func<Image, Image> op, int sw, int sh, int tx, int ty, int maxTiles)
    {
        var src = Source(sw, sh);
        Image img = op(Image.FromNode(src));
        Node node = img.Frames[0].Node;
        ImageRect tile = node.TileRect(tx, ty);
        ImageRect need = node.RequiredInput(0, tile);
        Assert.False(need.IsEmpty);
        img.CopyPixelsTo<Rgba32>(new Rgba32[tile.Width * tile.Height], tile, maxDegree: 1);
        Assert.Equal(TilesOf(need), src.Computed);
        Assert.InRange(src.Computed, 1, maxTiles);
        Assert.True(src.Computed < src.TilesX * src.TilesY);
    }

    [Fact]
    public void Rotate90ReadsOnlyTheTilesBehindTheOutputTile() => CheckOneTile(i => i.Rotate(90), 3000, 2000, 5, 3, 4);

    [Fact]
    public void Rotate270AndTransposeReadOnlyWhatTheyNeed()
    {
        CheckOneTile(i => i.Rotate(270), 3000, 2000, 2, 11, 4);
        CheckOneTile(i => i.Orient(5), 3000, 2000, 7, 1, 4);
    }

    [Fact]
    public void FlipsReadOneTileColumnOrRow()
    {
        CheckOneTile(i => i.Flip(FlipAxis.Horizontal), 3000, 2000, 0, 3, 2);
        CheckOneTile(i => i.Flip(FlipAxis.Vertical), 3000, 2000, 4, 0, 2);
        CheckOneTile(i => i.Rotate(180), 3000, 2000, 4, 4, 4);
    }

    [Fact]
    public void PadOnlyReadsImageTilesItOverlaps()
    {
        CheckOneTile(i => i.Pad(100, 100, 100, 100), 3000, 2000, 10, 10, 4);

        // A tile entirely inside the border reads nothing.
        var src = Source(3000, 2000);
        Image padded = Image.FromNode(src).Pad(300, 300, 0, 0);
        padded.CopyPixelsTo<Rgba32>(new Rgba32[128 * 128], new ImageRect(0, 0, 128, 128), maxDegree: 1);
        Assert.Equal(0, src.Computed);
    }

    [Fact]
    public void PadMirrorAndClampReadLittle()
    {
        CheckOneTile(i => i.Pad(200, 200, 200, 200, EdgeMode.Mirror), 3000, 2000, 0, 0, 4);
        CheckOneTile(i => i.Pad(200, 200, 200, 200, EdgeMode.Clamp), 3000, 2000, 0, 0, 4);
    }

    [Fact]
    public void ArbitraryRotationReadsATiltedBoxOnly()
    {
        CheckOneTile(i => i.Rotate(17, filter: ResamplingFilter.Lanczos3), 3000, 2000, 10, 10, 9);
        CheckOneTile(i => i.Affine(1, 0.1, 0, 1, expand: false), 3000, 2000, 5, 5, 6);
    }

    [Fact]
    public void RotatedTileOutsideTheImageReadsNothing()
    {
        var src = Source(500, 500);
        Image r = Image.FromNode(src).Rotate(45);
        Node n = r.Frames[0].Node;
        // The top-left corner tile of an expanded 45 degree rotation is empty background.
        r.CopyPixelsTo<Rgba32>(new Rgba32[100], new ImageRect(0, 0, 10, 10), maxDegree: 1);
        Assert.Equal(0, src.Computed);
        Assert.True(n.RequiredInput(0, n.TileRect(0, 0)).IsEmpty);
    }

    [Fact]
    public void CropAfterRotateComputesAFewTiles()
    {
        var src = Source(4000, 3000);
        Image img = Image.FromNode(src).Rotate(90).Crop(0, 0, 100, 100);
        img.CopyPixelsTo<Rgba32>(new Rgba32[100 * 100], new ImageRect(0, 0, 100, 100), maxDegree: 1);
        Assert.InRange(src.Computed, 1, 2);
    }
}

[Collection(MemoryMeasurement.Name)]
public sealed class GeometryStreamingTests : IDisposable
{
    private readonly long _saved = DecodeSourceNode.StreamingMinBytes;

    public void Dispose() => DecodeSourceNode.StreamingMinBytes = _saved;

    private static Image LoadGen(int w, int h) => Image.Load(GenFormat.Header(w, h), new LoadOptions { Configuration = GenFormat.Instance.Configuration() });

    [Fact]
    public void FlipHorizontalAndPadStreamInBoundedMemory()
    {
        const int W = 8000;
        const int H = 30000;
        long frameBytes = (long)W * H * 4;
        Image img = LoadGen(W, H).Flip(FlipAxis.Horizontal).Pad(8, 8, 8, 8, EdgeMode.Clamp).Resize(width: 400);
        Assert.True(img.Frames[0].Node.Streaming);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = GC.GetTotalMemory(true);
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, LiveHeap.Read(baseline, frameBytes / 10));
                Thread.Sleep(2);
            }
        });
        sampler.Start();
        var sw = Stopwatch.StartNew();
        try
        {
            Assert.Equal(400, img.Width);
            _ = img.ToArray<Rgba32>();
        }
        finally
        {
            stop.Cancel();
            sampler.Join();
        }

        long growth = peak - baseline;
        TestContext.Current.SendDiagnosticMessage($"geometry streaming {W}x{H}: flip+pad+resize peak managed growth {growth / (1 << 20)} MiB ({100.0 * growth / frameBytes:F1}% of the frame), {sw.ElapsedMilliseconds} ms");
        // The bound is for the pooled store; a store that allocates a fresh array per tile leaves garbage the pool avoids.
        Assert.True(TileStores.Current is not PooledTileStore || growth < frameBytes / 10, $"peak growth {growth} exceeds 10% of the frame");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StreamedGeometryEqualsMaterializedGeometry(int which)
    {
        const int W = 2200;
        const int H = 3000;
        Func<Image, Image> op = which switch
        {
            0 => i => i.Flip(FlipAxis.Horizontal).Pad(13, 7, 5, 9),
            1 => i => i.Rotate(90),
            2 => i => i.Flip(FlipAxis.Vertical).Pad(3, 4, 5, 6, EdgeMode.Mirror),
            _ => i => i.Rotate(2.5, filter: ResamplingFilter.Bilinear),
        };
        DecodeSourceNode.StreamingMinBytes = 0;
        Image streamed = LoadGen(W, H);
        Assert.True(streamed.Frames[0].Node.Streaming);
        Rgba32[] got = op(streamed).ToArray<Rgba32>();
        Rgba32[] source = LoadGen(W, H).ToArray<Rgba32>();
        Rgba32[] want = op(Image.FromPixels<Rgba32>(source, W, H)).ToArray<Rgba32>();
        Assert.True(want.AsSpan().SequenceEqual(got), "streamed and materialized results differ");
    }
}
