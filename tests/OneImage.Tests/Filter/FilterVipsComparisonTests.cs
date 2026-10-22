using Xunit;
using Vips = NetVips.Image;

namespace OneImage.Tests.Filter;

/// <summary>Compares the filters with libvips on the same pixels. The differences are reported; the assertions pin only what should agree.</summary>
public class FilterVipsComparisonTests
{
    private const int W = 400;
    private const int H = 300;

    private static (int Max, double Mean) Compare(byte[] a, byte[] b, int channels, int border)
    {
        int max = 0;
        long sum = 0;
        long n = 0;
        for (int y = border; y < H - border; y++)
        {
            for (int x = border; x < W - border; x++)
            {
                for (int k = 0; k < channels; k++)
                {
                    int i = (((y * W) + x) * channels) + k;
                    int d = Math.Abs(a[i] - b[i]);
                    max = Math.Max(max, d);
                    sum += d;
                    n++;
                }
            }
        }

        return (max, (double)sum / n);
    }

    private static void Say(string text) => TestNotes.Add("vs libvips " + text);

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void GaussianBlurIsCloseToLibvips(double sigma)
    {
        byte[] raw = FilterReference.Picture(W, H, 3, false);
        using Vips v = Vips.NewFromMemory(raw, W, H, 3, NetVips.Enums.BandFormat.Uchar);

        // libvips blurs stored values; its mask stops where the value falls below min_ampl, 0.0111 being about 3 sigma.
        using Vips blurred = v.Gaussblur(sigma, minAmpl: 0.0111, precision: NetVips.Enums.Precision.Float);
        byte[] theirs = blurred.WriteToMemory<float>().Select(f => (byte)Math.Clamp(Math.Floor(f + 0.5f), 0, 255)).ToArray();
        byte[] ours = Resample.ResampleAccuracyTests.ToBytes(Resample.ResampleAccuracyTests.ImageOf(raw, W, H, PixelFormat.Rgb8).GaussianBlur(sigma, linear: false), PixelFormat.Rgb8);
        (int Max, double Mean) all = Compare(ours, theirs, 3, 0);
        (int Max, double Mean) inner = Compare(ours, theirs, 3, (int)Math.Ceiling(3 * sigma));
        Say($"gaussian sigma {sigma} (stored values): all pixels max {all.Max}, mean {all.Mean:F4}; interior max {inner.Max}, mean {inner.Mean:F4}");
        Assert.True(inner.Max <= 2, $"interior max diff {inner.Max}");
        Assert.True(inner.Mean < 0.3);
    }

    [Fact]
    public void MedianEqualsLibvipsInTheInterior()
    {
        byte[] raw = FilterReference.Picture(W, H, 3, false);
        using Vips v = Vips.NewFromMemory(raw, W, H, 3, NetVips.Enums.BandFormat.Uchar);
        foreach (int r in new[] { 1, 2 })
        {
            using Vips m = v.Median((2 * r) + 1);
            byte[] theirs = m.WriteToMemory<byte>();
            byte[] ours = Resample.ResampleAccuracyTests.ToBytes(Resample.ResampleAccuracyTests.ImageOf(raw, W, H, PixelFormat.Rgb8).Median(r), PixelFormat.Rgb8);
            (int Max, double Mean) all = Compare(ours, theirs, 3, 0);
            (int Max, double Mean) inner = Compare(ours, theirs, 3, r);
            Say($"median radius {r}: all pixels max {all.Max}, mean {all.Mean:F4}; interior max {inner.Max}");
            Assert.Equal(0, inner.Max);
        }
    }

    [Fact]
    public void SobelAndSharpenAreReportedAgainstLibvips()
    {
        byte[] raw = FilterReference.Picture(W, H, 1, false);
        using Vips v = Vips.NewFromMemory(raw, W, H, 1, NetVips.Enums.BandFormat.Uchar);
        Image mine = Resample.ResampleAccuracyTests.ImageOf(raw, W, H, PixelFormat.Gray8);

        // libvips: |gx| + |gy| of the same kernels, clipped to 255. Ours with scale 4: sqrt(gx^2 + gy^2), clipped. They agree on
        // axis-aligned gradients and differ by up to a factor of sqrt(2) on diagonal ones.
        using Vips s = v.Sobel();
        byte[] theirs = s.WriteToMemory<byte>();
        byte[] ours = Resample.ResampleAccuracyTests.ToBytes(mine.Sobel(4.0), PixelFormat.Gray8);
        (int Max, double Mean) inner = Compare(ours, theirs, 1, 1);
        Say($"sobel (euclidean vs libvips L1): interior max {inner.Max}, mean {inner.Mean:F3}");
        double[] luma = raw.Select(b => (double)b).ToArray();
        int exactHorizontal = 0;
        for (int y = 1; y < H - 1 && exactHorizontal < 1000; y++)
        {
            for (int x = 1; x < W - 1; x++)
            {
                double gy = (luma[((y + 1) * W) + x - 1] + (2 * luma[((y + 1) * W) + x]) + luma[((y + 1) * W) + x + 1]) - (luma[((y - 1) * W) + x - 1] + (2 * luma[((y - 1) * W) + x]) + luma[((y - 1) * W) + x + 1]);
                double gx = (luma[((y - 1) * W) + x + 1] - luma[((y - 1) * W) + x - 1]) + (2 * (luma[(y * W) + x + 1] - luma[(y * W) + x - 1])) + (luma[((y + 1) * W) + x + 1] - luma[((y + 1) * W) + x - 1]);
                if (gx == 0 && Math.Abs(gy) < 255 && gy != 0)
                {
                    Assert.True(Math.Abs(ours[(y * W) + x] - theirs[(y * W) + x]) <= 1);
                    exactHorizontal++;
                }
            }
        }

        Assert.True(exactHorizontal > 50);

        // libvips sharpen works on the L channel of CIELAB with a different mask and flat/jagged slopes, so only the size of the effect is comparable.
        using Vips rgb = Vips.NewFromMemory(FilterReference.Picture(W, H, 3, false), W, H, 3, NetVips.Enums.BandFormat.Uchar);
        using Vips sh = rgb.Sharpen(sigma: 1.0, x1: 0.0, m1: 1.0, m2: 1.0);
        byte[] vipsSharp = sh.WriteToMemory<byte>();
        byte[] src = FilterReference.Picture(W, H, 3, false);
        byte[] oursSharp = Resample.ResampleAccuracyTests.ToBytes(Resample.ResampleAccuracyTests.ImageOf(src, W, H, PixelFormat.Rgb8).Sharpen(1.0, 1.0, linear: false), PixelFormat.Rgb8);
        (int Max, double Mean) change = (0, 0);
        (int Max, double Mean) ourChange = Compare(oursSharp, src, 3, 3);
        (int Max, double Mean) theirChange = Compare(vipsSharp, src, 3, 3);
        (int Max, double Mean) between = Compare(oursSharp, vipsSharp, 3, 3);
        _ = change;
        Say($"sharpen: ours changes the input by mean {ourChange.Mean:F3} (max {ourChange.Max}), libvips by mean {theirChange.Mean:F3} (max {theirChange.Max}); ours vs libvips mean {between.Mean:F3}, max {between.Max}");
        Assert.True(ourChange.Mean > 0.5);
    }
}
