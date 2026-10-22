using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Xunit;
using OneImage.Kernels;
using Is = SixLabors.ImageSharp;
using IsPx = SixLabors.ImageSharp.PixelFormats;

namespace OneImage.Tests.Resample;

public class NewResamplerTests
{
    private static readonly ResamplingFilter[] s_new =
    [
        ResamplingFilter.Hermite, ResamplingFilter.Lanczos5, ResamplingFilter.Lanczos8, ResamplingFilter.Robidoux,
        ResamplingFilter.RobidouxSharp, ResamplingFilter.Spline, ResamplingFilter.Welch,
    ];

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    public static IEnumerable<TheoryDataRow<ResamplingFilter>> NewFilters() => s_new.Select(f => new TheoryDataRow<ResamplingFilter>(f));

    [Fact]
    public void KernelValuesMatchTheClosedForms()
    {
        // Hermite: 2|x|^3 - 3|x|^2 + 1 on |x| < 1.
        foreach (double x in new[] { 0.0, 0.1, 0.5, 0.9, 1.0, 1.5 })
        {
            double want = x < 1 ? (2 * x * x * x) - (3 * x * x) + 1 : 0;
            Assert.Equal(want, FilterFunctions.Evaluate(ResamplingFilter.Hermite, x), 12);
            Assert.Equal(want, FilterFunctions.Evaluate(ResamplingFilter.Hermite, -x), 12);
        }

        // Welch: sinc(x) (1 - (x/3)^2) on |x| < 3.
        foreach (double x in new[] { 0.0, 0.3, 1.0, 1.7, 2.9, 3.0, 3.5 })
        {
            Assert.Equal(x < 3 ? Sinc(x) * (1 - (x * x / 9)) : 0, FilterFunctions.Evaluate(ResamplingFilter.Welch, x), 12);
        }

        // Lanczos a: sinc(x) sinc(x/a) on |x| < a.
        foreach ((ResamplingFilter f, int a) in new[] { (ResamplingFilter.Lanczos5, 5), (ResamplingFilter.Lanczos8, 8), (ResamplingFilter.Lanczos3, 3), (ResamplingFilter.Lanczos2, 2) })
        {
            foreach (double x in new[] { 0.0, 0.25, 0.5, 1.0, 1.5, 2.75, 4.2, 7.9, 8.0, 9.0 })
            {
                double want = x < a ? Sinc(x) * Sinc(x / a) : 0;
                Assert.Equal(want, FilterFunctions.Evaluate(f, x), 12);
            }

            Assert.Equal(a, FilterFunctions.Support(f));
        }

        // Spline: the cubic B-spline, 2/3 at 0, 1/6 at 1, 0 at 2.
        Assert.Equal(2.0 / 3, FilterFunctions.Evaluate(ResamplingFilter.Spline, 0), 12);
        Assert.Equal(1.0 / 6, FilterFunctions.Evaluate(ResamplingFilter.Spline, 1), 12);
        Assert.Equal(0, FilterFunctions.Evaluate(ResamplingFilter.Spline, 2), 12);
        Assert.Equal(((2.0 / 3) - 0.25 + (0.125 / 2)), FilterFunctions.Evaluate(ResamplingFilter.Spline, 0.5), 12);

        // Robidoux's published constants.
        var (b, c) = FilterFunctions.CubicParameters(ResamplingFilter.Robidoux)!.Value;
        Assert.Equal(0.37821575509399867, b, 12);
        Assert.Equal(0.31089212245300067, c, 12);
        (b, c) = FilterFunctions.CubicParameters(ResamplingFilter.RobidouxSharp)!.Value;
        Assert.Equal(0.2620145123990142, b, 12);
        Assert.Equal(0.3689927438004929, c, 12);
        // Cubic value at 0 is (6 - 2B) / 6.
        Assert.Equal((6 - (2 * 0.37821575509399867)) / 6, FilterFunctions.Evaluate(ResamplingFilter.Robidoux, 0), 12);
    }

    [Theory]
    [MemberData(nameof(NewFilters))]
    public void WeightsFormAPartitionOfUnityAndSupportIsHonored(ResamplingFilter f)
    {
        double support = FilterFunctions.Support(f);
        Assert.Equal(0, FilterFunctions.Evaluate(f, support + 1e-9), 12);
        // Cubics of the Mitchell family and Lanczos sum to 1 over integer shifts (Lanczos only approximately); the normalized table always does.
        foreach (double t in new[] { 0, 0.13, 0.5, 0.77 })
        {
            double sum = 0;
            for (int k = -10; k <= 10; k++)
            {
                sum += FilterFunctions.Evaluate(f, k - t);
            }

            Assert.InRange(sum, 0.9, 1.1);
        }

        // The resample weight table sums to exactly 1 per output, up and down.
        foreach ((int from, int to) in new[] { (100, 37), (37, 100), (64, 64), (500, 41) })
        {
            WeightTable table = WeightTable.Build(from, to, (double)from / to, f);
            for (int o = 0; o < to; o++)
            {
                double sum = 0;
                for (int i = 0; i < table.Count[o]; i++)
                {
                    sum += table.Weights[table.Offset[o] + i];
                }

                Assert.Equal(1.0, sum, 5);
            }
        }
    }

    [Theory]
    [MemberData(nameof(NewFilters))]
    public void AffineSamplerWeightsMatchTheKernel(ResamplingFilter f)
    {
        int support = AffineFilterTable.Support(f);
        float[] table = AffineFilterTable.Get(f);
        float[] w = new float[2 * support];
        foreach (float frac in new[] { 0f, 0.01f, 0.25f, 0.5f, 0.73f, 0.999f })
        {
            AffineFilterTable.Fill(f, table, support, frac, w);
            double sum = 0;
            for (int t = 0; t < 2 * support; t++)
            {
                sum += FilterFunctions.Evaluate(f, (t - support + 1) - frac);
            }

            for (int t = 0; t < 2 * support; t++)
            {
                Assert.Equal(FilterFunctions.Evaluate(f, (t - support + 1) - frac), w[t], 4);
            }
        }
    }

    // Smooth with some texture and a sharp edge, so the filters visibly differ.
    private static Rgba32[] Textured(int w, int h)
    {
        var px = new Rgba32[w * h];
        var rnd = new Random(5);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double v = 120 + (60 * Math.Sin(x * 0.21) * Math.Cos(y * 0.17)) + rnd.Next(-6, 7) + (x > w / 2 && y > h / 3 ? 40 : 0);
                px[(y * w) + x] = new Rgba32((byte)Math.Clamp(v, 0, 255), (byte)Math.Clamp(v * 0.8 + 20, 0, 255), (byte)Math.Clamp(255 - v, 0, 255), 255);
            }
        }

        return px;
    }

    private static SixLabors.ImageSharp.Processing.Processors.Transforms.IResampler Theirs(ResamplingFilter f) => f switch
    {
        ResamplingFilter.Hermite => KnownResamplers.Hermite,
        ResamplingFilter.Lanczos5 => KnownResamplers.Lanczos5,
        ResamplingFilter.Lanczos8 => KnownResamplers.Lanczos8,
        ResamplingFilter.Robidoux => KnownResamplers.Robidoux,
        ResamplingFilter.RobidouxSharp => KnownResamplers.RobidouxSharp,
        ResamplingFilter.Spline => KnownResamplers.Spline,
        _ => KnownResamplers.Welch,
    };

    [Theory]
    [MemberData(nameof(NewFilters))]
    public void ResizeIsCloseToImageSharp(ResamplingFilter f)
    {
        const int W = 192;
        const int H = 128;
        Rgba32[] src = Textured(W, H);
        using var sharp = new Is.Image<IsPx.Rgba32>(W, H);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Rgba32 p = src[(y * W) + x];
                sharp[x, y] = new IsPx.Rgba32(p.R, p.G, p.B, 255);
            }
        }

        foreach ((int tw, int th) in new[] { (61, 41), (400, 260), (96, 64) })
        {
            Image ours = Image.FromPixels<Rgba32>(src, W, H).Resize(tw, th, Fit.Stretch, filter: f, linear: false);
            using Is.Image<IsPx.Rgba32> theirs = sharp.Clone(c => c.Resize(tw, th, Theirs(f)));
            Rgba32[] o = ours.ToArray<Rgba32>();
            double sum = 0;
            int max = 0;
            for (int y = 0; y < th; y++)
            {
                for (int x = 0; x < tw; x++)
                {
                    Rgba32 a = o[(y * tw) + x];
                    IsPx.Rgba32 b = theirs[x, y];
                    int d = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                    sum += d;
                    max = Math.Max(max, d);
                }
            }

            double mean = sum / (tw * th);
            TestContext.Current.SendDiagnosticMessage($"{f} {W}x{H}->{tw}x{th}: mean abs diff vs ImageSharp {mean:F3}, max {max}");
            Assert.True(mean <= 0.3 && max <= 6, $"{f} ->{tw}x{th}: mean {mean:F3}, max {max}");
        }
    }

    [Theory]
    [MemberData(nameof(NewFilters))]
    public void ResizeMatchesTheDoubleReferenceAndKeepsFlatColorsFlat(ResamplingFilter f)
    {
        Image flat = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(40, 200, 90, 255), 70 * 50).ToArray(), 70, 50);
        Assert.All(flat.Resize(23, 17, Fit.Stretch, filter: f).ToArray<Rgba32>(), p => Assert.Equal(new Rgba32(40, 200, 90, 255), p));
        Assert.All(flat.Resize(150, 90, Fit.Stretch, filter: f).ToArray<Rgba32>(), p => Assert.Equal(new Rgba32(40, 200, 90, 255), p));

        // Straight double-precision 1-D reference along x (height unchanged is an exact copy), encoded values, opaque.
        const int W = 90;
        Rgba32[] src = Textured(W, 4);
        foreach (int tw in new[] { 31, 200 })
        {
            Rgba32[] got = Image.FromPixels<Rgba32>(src, W, 4).Resize(tw, 4, Fit.Stretch, filter: f, linear: false).ToArray<Rgba32>();
            double scale = (double)W / tw;
            double radius = FilterFunctions.Support(f) * Math.Max(1, scale);
            for (int x = 0; x < tw; x++)
            {
                double center = ((x + 0.5) * scale) - 0.5;
                double[] acc = new double[3];
                double total = 0;
                for (int k = (int)Math.Floor(center - radius); k <= (int)Math.Ceiling(center + radius); k++)
                {
                    double wgt = FilterFunctions.Evaluate(f, (k - center) / Math.Max(1, scale));
                    Rgba32 p = src[Math.Clamp(k, 0, W - 1)];
                    acc[0] += wgt * p.R;
                    acc[1] += wgt * p.G;
                    acc[2] += wgt * p.B;
                    total += wgt;
                }

                Rgba32 g = got[x];
                Assert.True(Math.Abs(g.R - Math.Clamp(acc[0] / total, 0, 255)) <= 1.0 && Math.Abs(g.G - Math.Clamp(acc[1] / total, 0, 255)) <= 1.0 && Math.Abs(g.B - Math.Clamp(acc[2] / total, 0, 255)) <= 1.0, $"{f} {W}->{tw} x={x}");
            }
        }
    }

    [Fact]
    public void AliasesAreTheSameFilters()
    {
        Assert.Equal(ResamplingFilter.CatmullRom, ResamplingFilter.Bicubic);
        Assert.Equal(ResamplingFilter.Bilinear, ResamplingFilter.Triangle);
        Rgba32[] src = Textured(60, 40);
        Image img = Image.FromPixels<Rgba32>(src, 60, 40);
        Assert.Equal(img.Resize(25, 17, Fit.Stretch, filter: ResamplingFilter.CatmullRom).ToArray<Rgba32>(), img.Resize(25, 17, Fit.Stretch, filter: ResamplingFilter.Bicubic).ToArray<Rgba32>());
    }

    [Theory]
    [MemberData(nameof(NewFilters))]
    public void NewFiltersWorkInRotateAndHaveStableDescriptors(ResamplingFilter f)
    {
        Image img = Image.FromPixels<Rgba32>(Textured(80, 60), 80, 60);
        Assert.Equal(img.Rotate(13, filter: f).ToArray<Rgba32>(maxDegree: 1), img.Rotate(13, filter: f).ToArray<Rgba32>(maxDegree: 4));
        Assert.Equal(FilterFunctions.Name(f), FilterFunctions.Name(f).ToLowerInvariant());
        Assert.NotEqual("auto", FilterFunctions.Name(f));
    }
}
