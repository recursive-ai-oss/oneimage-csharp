namespace OneImage.Tests;

[Collection(MemoryMeasurement.Name)]
public class CacheLifetimeTests
{
    // Separate methods so no local keeps the node alive while the collector runs.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long RenderAndDrop(TileCache cache)
    {
        var src = new PatternNode(600, 600);
        var ctx = new RenderContext(1, cache, default);
        Render.ReadRegion(src, new ImageRect(10, 10, 300, 300), new byte[300 * 300 * 4], 0, PixelFormat.Rgba8, ctx);
        Assert.True(cache.CountFor(src.Id) > 0);
        return src.Id;
    }

    [Fact]
    public void TilesOfDroppedNodesLeaveTheCache()
    {
        var cache = new TileCache(1L << 30);
        RenderAndDrop(cache);
        Assert.True(cache.Bytes > 0);
        for (int i = 0; i < 20 && cache.Bytes > 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void SaveReleasesThePipelinesCachedTiles()
    {
        var src = new PatternNode(600, 600);
        Image img = Image.FromNode(src).Invert();
        // A partial read fills the shared cache for the source node.
        img.CopyPixelsTo<Rgba32>(new Rgba32[100 * 100], new ImageRect(5, 5, 100, 100));
        Assert.True(TileCache.Global.CountFor(src.Id) > 0);
        using var ms = new MemoryStream();
        img.Save(ms, PngFormat.Instance);
        Assert.Equal(0, TileCache.Global.CountFor(src.Id));
        // Still correct afterwards: tiles are simply recomputed.
        Assert.Equal(new Rgba32((byte)(255 - PatternNode.Expected(7, 9).R), (byte)(255 - PatternNode.Expected(7, 9).G), (byte)(255 - PatternNode.Expected(7, 9).B), PatternNode.Expected(7, 9).A), img.ToArray<Rgba32>()[(9 * 600) + 7]);
    }
}
