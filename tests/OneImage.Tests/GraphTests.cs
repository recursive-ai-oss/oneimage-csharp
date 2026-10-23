using OneImage.Kernels;

namespace OneImage.Tests;

/// <summary>
/// Counts tiles through the process-wide counters (and, for some tests, the global cache and its budget), which any test running at
/// the same time also moves, so this class runs alone with the other measuring tests.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public class GraphTests
{
    private static RenderContext Ctx(int degree = 1, TileCache? cache = null, CancellationToken ct = default) =>
        new(degree, cache ?? new TileCache(1L << 30), ct);

    [Fact]
    public void RegionRenderPullsOnlyIntersectingTiles()
    {
        var src = new PatternNode(10000, 10000);
        var region = new ImageRect(3000, 3000, 512, 512);
        byte[] buf = new byte[512 * 512 * 4];
        Render.ReadRegion(src, region, buf, 0, PixelFormat.Rgba8, Ctx());

        // x and y 3000..3511 touch tiles 23..27 in each direction.
        Assert.Equal(25, src.Computed);
        Assert.Equal(PatternNode.Expected(3000, 3000).R, buf[0]);
        int last = ((511 * 512) + 511) * 4;
        Assert.Equal(PatternNode.Expected(3511, 3511).G, buf[last + 1]);
    }

    [Fact]
    public void TileAlignedCropReusesInputTiles()
    {
        var src = new PatternNode(2000, 2000);
        Image cropped = Image.FromNode(src).Crop(256, 128, 256, 256);
        long copies = Tile.CopyCount;
        Rgba32[] px = cropped.ToArray<Rgba32>();
        Assert.Equal(4, src.Computed);
        Assert.Equal(PatternNode.Expected(256 + 255, 128 + 255), px[^1]);
        Assert.Equal(copies, Tile.CopyCount);
    }

    [Fact]
    public void RequiredInputOfCropIsTheShiftedRegion()
    {
        var src = new PatternNode(500, 500);
        var crop = new CropNode(src, new ImageRect(10, 20, 100, 100));
        Assert.Equal(new ImageRect(15, 27, 4, 4), crop.RequiredInput(0, new ImageRect(5, 7, 4, 4)));
    }

    [Fact]
    public void ResultsMatchAcrossCacheBudgetsAndEvictedTilesAreRecomputed()
    {
        var src = new PatternNode(1000, 900);
        Node graph = new RowOpNode(new CropNode(src, new ImageRect(3, 5, 900, 800)), "invert", new OpDescriptor("invert"));
        byte[] expected = new byte[900 * 800 * 4];
        Render.ReadRegion(graph, new ImageRect(0, 0, 900, 800), expected, 0, PixelFormat.Rgba8, Ctx());

        var tiny = new TileCache(3 * Tile.ByteSize(PixelFormat.Rgba8));
        byte[] actual = new byte[expected.Length];
        Render.ReadRegion(graph, new ImageRect(0, 0, 900, 800), actual, 0, PixelFormat.Rgba8, Ctx(cache: tiny));
        Assert.Equal(expected, actual);
        Assert.True(tiny.Bytes <= tiny.Budget);
        int firstPass = src.Computed;

        Array.Clear(actual);
        Render.ReadRegion(graph, new ImageRect(0, 0, 900, 800), actual, 0, PixelFormat.Rgba8, Ctx(cache: tiny));
        Assert.Equal(expected, actual);
        Assert.True(src.Computed > firstPass, "evicted tiles must be recomputed");
        Assert.True(tiny.Bytes <= tiny.Budget);
    }

    [Fact]
    public void CacheHitsAvoidRecomputeAndLeasesReturnToPool()
    {
        // Images dropped by earlier tests give their cached tiles back from a finalizer; let that finish first so it cannot
        // move the counters in the middle of this test.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long outstanding = Tile.RentedCount - Tile.ReturnedCount;
        var cache = new TileCache(1L << 30);
        var src = new PatternNode(500, 500);
        byte[] buf = new byte[500 * 500 * 4];
        Render.ReadRegion(src, new ImageRect(0, 0, 500, 499), buf, 0, PixelFormat.Rgba8, Ctx(cache: cache));
        Render.ReadRegion(src, new ImageRect(0, 0, 500, 499), buf, 0, PixelFormat.Rgba8, Ctx(cache: cache));
        Assert.Equal(16, src.Computed);
        Assert.Equal(16, cache.Count);
        cache.Clear();
        Assert.Equal(outstanding, Tile.RentedCount - Tile.ReturnedCount);
    }

    [Fact]
    public void WholeImageRenderDoesNotFillTheCache()
    {
        var cache = new TileCache(1L << 30);
        var src = new PatternNode(500, 500);
        Render.ReadRegion(src, new ImageRect(0, 0, 500, 500), new byte[500 * 500 * 4], 0, PixelFormat.Rgba8, Ctx(cache: cache));
        Assert.Equal(16, src.Computed);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void CanvasTilesAreNeverEvicted()
    {
        TileCache.Global.Clear();
        long budget = ImageEngine.CacheBudgetBytes;
        try
        {
            ImageEngine.CacheBudgetBytes = 0;
            using var canvas = new Canvas(400, 400, PixelFormat.Rgba8);
            canvas.CopyFrom<Rgba32>(Enumerable.Repeat(new Rgba32(7, 8, 9, 10), 400 * 400).ToArray(), new ImageRect(0, 0, 400, 400));
            Image view = canvas.Snapshot().Invert();
            Assert.Equal(new Rgba32(248, 247, 246, 10), view.ToArray<Rgba32>()[1234]);
            Assert.Equal(new Rgba32(7, 8, 9, 10), canvas[399, 399]);
            Assert.Equal(0, TileCache.Global.Bytes);
            Assert.Equal(16, canvas.AllocatedTiles);
        }
        finally
        {
            ImageEngine.CacheBudgetBytes = budget;
        }
    }

    [Fact]
    public void ResultsAreIdenticalAcrossDegrees()
    {
        var src = new PatternNode(1100, 900);
        Image img = Image.FromNode(src).Crop(13, 9, 1000, 800).Invert();
        Rgba32[] one = img.ToArray<Rgba32>(maxDegree: 1);
        Rgba32[] many = img.ToArray<Rgba32>(maxDegree: 8);
        Assert.Equal(one, many);
        Assert.Equal(new Rgba32((byte)(255 - PatternNode.Expected(13, 9).R), (byte)(255 - PatternNode.Expected(13, 9).G), (byte)(255 - PatternNode.Expected(13, 9).B), PatternNode.Expected(13, 9).A), one[0]);
    }

    [Fact]
    public void PerCallDegreeIsHonored()
    {
        var serial = new PatternNode(1280, 1280, delayMs: 5, cacheable: false);
        Render.ReadRegion(serial, new ImageRect(0, 0, 1280, 1280), new byte[1280 * 1280 * 4], 0, PixelFormat.Rgba8, Ctx(degree: 1));
        Assert.Equal(1, serial.MaxActive);

        var parallel = new PatternNode(1280, 1280, delayMs: 5, cacheable: false);
        Render.ReadRegion(parallel, new ImageRect(0, 0, 1280, 1280), new byte[1280 * 1280 * 4], 0, PixelFormat.Rgba8, Ctx(degree: 4));
        Assert.InRange(parallel.MaxActive, 2, 4);

        var viaApi = new PatternNode(1280, 1280, delayMs: 5, cacheable: false);
        Image.FromNode(viaApi).ToArray<Rgba32>(maxDegree: 2);
        Assert.InRange(viaApi.MaxActive, 1, 2);
    }

    [Fact]
    public void CancellationIsHonored()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var src = new PatternNode(500, 500);
        Assert.Throws<OperationCanceledException>(() => Image.FromNode(src).ToArray<Rgba32>(0, cts.Token));
        Assert.Equal(0, src.Computed);
    }

    [Fact]
    public void ConvertAndDescribe()
    {
        var src = new PatternNode(300, 200);
        Image img = Image.FromNode(src).Crop(1, 1, 50, 50).ConvertTo(PixelFormat.Gray8);
        Assert.Equal(PixelFormat.Gray8, img.Format);
        Gray8[] g = img.ToArray<Gray8>();
        Assert.Equal(2500, g.Length);
        Rgba32[] rgba = img.ToArray<Rgba32>();
        Assert.Equal(g[10].V, rgba[10].R);
        Assert.Equal(255, rgba[10].A);
        string text = img.DescribeGraph();
        Assert.Contains("convert(", text, StringComparison.Ordinal);
        Assert.Contains("crop(x=1, y=1, width=50, height=50)", text, StringComparison.Ordinal);
        Assert.Throws<ImageOperationException>(() => img.ConvertTo(new PixelFormat(ColorModel.Lab, SampleType.U8)));
    }

    [Fact]
    public void KernelRegistryKnowsBuiltInsAndRejectsUnknown()
    {
        Assert.NotNull(KernelRegistry.Get<RowKernel>("invert"));
        Assert.Throws<ImageOperationException>(() => KernelRegistry.Get<RowKernel>("nope"));
    }
}

/// <summary>
/// Pipeline-level tile counting. These renders go through the process-wide tile cache; tests running in parallel can
/// evict a source tile that two output tiles share, which is correct but recomputes it, so they run on their own.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public class GraphCountTests
{
    [Fact]
    public void CropNeverComputesTilesOutsideTheCrop()
    {
        var src = new PatternNode(10000, 10000);
        Image cropped = Image.FromNode(src).Crop(1000, 1000, 300, 300);
        Rgba32[] px = cropped.ToArray<Rgba32>();

        // 1000..1299 spans tiles 7..10: 16 tiles at most.
        Assert.True(src.Computed <= 16, $"computed {src.Computed}");
        Assert.Equal(PatternNode.Expected(1000, 1000), px[0]);
        Assert.Equal(PatternNode.Expected(1299, 1299), px[^1]);
    }
}
