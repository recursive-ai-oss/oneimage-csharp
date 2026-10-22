using Xunit;

namespace OneImage.Tests.Color;

[Collection(MemoryMeasurement.Name)]
public class TransformCacheTests
{
    private static byte[] Profile(int variant)
    {
        byte[] icc = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "icc", "sRGB_v4_ICC_preference.icc"));
        // The creation date (header bytes 24-35) is free-form, so changing it makes a distinct but equally valid profile.
        BitConverter.TryWriteBytes(icc.AsSpan(28), variant);
        return icc;
    }

    [Fact]
    public void EqualProfilesFromDifferentImagesShareOneTransform()
    {
        ColorProfile a = ColorProfile.FromIcc(Profile(1));
        ColorProfile b = ColorProfile.FromIcc(Profile(1));
        Assert.NotSame(a, b);
        ColorTransform ta = ColorTransform.Get(a, ColorProfile.Srgb, RenderingIntent.Perceptual, true);
        ColorTransform tb = ColorTransform.Get(b, ColorProfile.Srgb, RenderingIntent.Perceptual, true);
        Assert.Same(ta, tb);
        Assert.NotSame(ta, ColorTransform.Get(ColorProfile.FromIcc(Profile(2)), ColorProfile.Srgb, RenderingIntent.Perceptual, true));
    }

    [Fact]
    public void RetentionStaysBoundedOverManyDistinctProfiles()
    {
        // Each transform of this table-based profile holds about 0.8 MB, so an unbounded cache would keep ~250 MB here.
        _ = ColorTransform.Get(ColorProfile.FromIcc(Profile(1000)), ColorProfile.Srgb, RenderingIntent.Perceptual, true);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        const long limit = 60L << 20;
        for (int i = 0; i < 300; i++)
        {
            ColorTransform t = ColorTransform.Get(ColorProfile.FromIcc(Profile(2000 + i)), ColorProfile.Srgb, RenderingIntent.Perceptual, true);
            Assert.True(t.UsesGrid);
        }

        long growth = LiveHeap.Read(baseline, limit) - baseline;
        Assert.True(growth < limit, $"transform cache retained {growth >> 20} MB");
    }

    [Fact]
    public void RecentlyUsedTransformSurvivesEviction()
    {
        ColorProfile hot = ColorProfile.FromIcc(Profile(5000));
        ColorTransform first = ColorTransform.Get(hot, ColorProfile.Srgb, RenderingIntent.Perceptual, true);
        for (int i = 0; i < ColorTransform.MaxCachedTransforms * 2; i++)
        {
            _ = ColorTransform.Get(ColorProfile.FromIcc(Profile(6000 + i)), ColorProfile.Srgb, RenderingIntent.Perceptual, true);
            Assert.Same(first, ColorTransform.Get(hot, ColorProfile.Srgb, RenderingIntent.Perceptual, true));
        }
    }
}
