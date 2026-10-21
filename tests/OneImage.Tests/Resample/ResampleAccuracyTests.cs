using Xunit;

namespace OneImage.Tests.Resample;

/// <summary>A slow, direct implementation of the resampling definition in doubles, used as the oracle.</summary>
internal static class ReferenceResampler
{
    public static double[] Weights(ResamplingFilter f, int n, int m, int o)
    {
        double scale = (double)n / m;
        var w = new double[n];
        if (n == m)
        {
            w[o] = 1;
            return w;
        }

        switch (f)
        {
            case ResamplingFilter.Nearest:
                w[Math.Clamp((int)Math.Floor((o + 0.5) * scale), 0, n - 1)] = 1;
                return w;
            case ResamplingFilter.Box:
                for (int k = (int)Math.Floor(o * scale) - 1; k <= (int)Math.Ceiling((o + 1) * scale) + 1; k++)
                {
                    double ov = Math.Max(0, Math.Min(k + 1, (o + 1) * scale) - Math.Max(k, o * scale));
                    w[Math.Clamp(k, 0, n - 1)] += ov;
                }

                break;
            default:
                double center = ((o + 0.5) * scale) - 0.5;
                double sc = Math.Max(1, scale);
                double r = FilterFunctions.Support(f) * sc;
                for (int k = (int)Math.Floor(center - r) - 1; k <= (int)Math.Ceiling(center + r) + 1; k++)
                {
                    w[Math.Clamp(k, 0, n - 1)] += FilterFunctions.Evaluate(f, (k - center) / sc);
                }

                break;
        }

        double sum = w.Sum();
        for (int i = 0; i < n; i++)
        {
            w[i] /= sum;
        }

        return w;
    }

    private static double Decode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double Encode(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;

    /// <param name="src">Normalized (0..1) interleaved samples, straight alpha last when <paramref name="hasAlpha"/>.</param>
    public static double[] Resize(double[] src, int w, int h, int ch, bool hasAlpha, int dw, int dh, ResamplingFilter f, bool linear, ResamplingFilter? fy = null)
    {
        int colors = hasAlpha ? ch - 1 : ch;
        var pm = new double[w * h * ch];
        for (int i = 0; i < w * h; i++)
        {
            double a = hasAlpha ? src[(i * ch) + colors] : 1;
            for (int k = 0; k < colors; k++)
            {
                double v = src[(i * ch) + k];
                pm[(i * ch) + k] = (linear ? Decode(v) : v) * a;
            }

            if (hasAlpha)
            {
                pm[(i * ch) + colors] = a;
            }
        }

        var tmp = new double[dw * h * ch];
        for (int x = 0; x < dw; x++)
        {
            double[] wx = Weights(f, w, dw, x);
            for (int y = 0; y < h; y++)
            {
                for (int k = 0; k < ch; k++)
                {
                    double s = 0;
                    for (int i = 0; i < w; i++)
                    {
                        s += wx[i] * pm[(((y * w) + i) * ch) + k];
                    }

                    tmp[(((y * dw) + x) * ch) + k] = s;
                }
            }
        }

        var res = new double[dw * dh * ch];
        for (int y = 0; y < dh; y++)
        {
            double[] wy = Weights(fy ?? f, h, dh, y);
            for (int x = 0; x < dw; x++)
            {
                double[] px = new double[ch];
                for (int k = 0; k < ch; k++)
                {
                    double s = 0;
                    for (int i = 0; i < h; i++)
                    {
                        s += wy[i] * tmp[(((i * dw) + x) * ch) + k];
                    }

                    px[k] = s;
                }

                double a = hasAlpha ? px[colors] : 1;
                for (int k = 0; k < colors; k++)
                {
                    double v = a > 0 ? Math.Clamp(px[k] / a, 0, 1) : 0;
                    res[(((y * dw) + x) * ch) + k] = linear ? Encode(v) : v;
                }

                if (hasAlpha)
                {
                    res[(((y * dw) + x) * ch) + colors] = Math.Clamp(a, 0, 1);
                }
            }
        }

        return res;
    }
}

public class ResampleAccuracyTests
{
    private static byte[] RandomBytes(int n, int seed)
    {
        var rnd = new Random(seed);
        byte[] b = new byte[n];
        rnd.NextBytes(b);
        return b;
    }

    public static IEnumerable<TheoryDataRow<ResamplingFilter, int, int, bool>> Cases()
    {
        foreach (ResamplingFilter f in Enum.GetValues<ResamplingFilter>().Where(f => f != ResamplingFilter.Auto))
        {
            foreach ((int dw, int dh) in new[] { (40, 23), (200, 150), (30, 150), (98, 61), (31, 19) })
            {
                foreach (bool linear in new[] { true, false })
                {
                    yield return new(f, dw, dh, linear);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rgba32MatchesReference(ResamplingFilter f, int dw, int dh, bool linear)
    {
        const int W = 97;
        const int H = 61;
        byte[] raw = RandomBytes(W * H * 4, 11);
        Rgba32[] px = new Rgba32[W * H];
        for (int i = 0; i < px.Length; i++)
        {
            // Make a good share of pixels translucent so premultiplication matters.
            px[i] = new Rgba32(raw[i * 4], raw[(i * 4) + 1], raw[(i * 4) + 2], (byte)(raw[(i * 4) + 3] < 80 ? raw[(i * 4) + 3] : 255));
        }

        Image img = Image.FromPixels<Rgba32>(px, W, H).Resize(dw, dh, Fit.Stretch, filter: f, linear: linear);
        Rgba32[] got = img.ToArray<Rgba32>();
        double[] src = new double[W * H * 4];
        for (int i = 0; i < px.Length; i++)
        {
            src[i * 4] = px[i].R / 255.0;
            src[(i * 4) + 1] = px[i].G / 255.0;
            src[(i * 4) + 2] = px[i].B / 255.0;
            src[(i * 4) + 3] = px[i].A / 255.0;
        }

        double[] want = ReferenceResampler.Resize(src, W, H, 4, true, dw, dh, f, linear);
        double worst = 0;
        for (int i = 0; i < got.Length; i++)
        {
            double[] g = [got[i].R, got[i].G, got[i].B, got[i].A];
            for (int k = 0; k < 4; k++)
            {
                worst = Math.Max(worst, Math.Abs(g[k] - (want[(i * 4) + k] * 255)));
            }
        }

        // Rounding alone contributes up to 0.5; the rest is float32 vs double and table quantization.
        TestContext.Current.SendDiagnosticMessage($"accuracy {f} {W}x{H}->{dw}x{dh} linear={linear}: max error {worst:F3} LSB");
        Assert.True(worst <= 0.75, $"{f} {dw}x{dh} linear={linear}: max error {worst:F3} LSB");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OtherChannelLayoutsMatchReference(int channels)
    {
        const int W = 64;
        const int H = 40;
        PixelFormat fmt = channels switch { 1 => PixelFormat.Gray8, 2 => PixelFormat.GrayAlpha8, 3 => PixelFormat.Rgb8, _ => PixelFormat.Rgba8Premultiplied };
        byte[] raw = RandomBytes(W * H * channels, 5);
        if (channels == 4)
        {
            // Valid premultiplied data: color <= alpha.
            for (int i = 0; i < W * H; i++)
            {
                raw[(i * 4) + 3] = (byte)Math.Max(raw[(i * 4) + 3], (byte)1);
                for (int k = 0; k < 3; k++)
                {
                    raw[(i * 4) + k] = (byte)Math.Min(raw[(i * 4) + k], raw[(i * 4) + 3]);
                }
            }
        }

        Image src = ImageOf(raw, W, H, fmt);
        Image res = src.Resize(25, 17, Fit.Stretch);
        byte[] got = ToBytes(res, fmt);
        double[] n = new double[raw.Length];
        for (int i = 0; i < n.Length; i++)
        {
            n[i] = raw[i] / 255.0;
        }

        if (channels == 4)
        {
            // Reference works on straight alpha; unpremultiply the input, and premultiply the output for comparison.
            for (int i = 0; i < W * H; i++)
            {
                for (int k = 0; k < 3; k++)
                {
                    n[(i * 4) + k] = Math.Min(1, n[(i * 4) + k] / n[(i * 4) + 3]);
                }
            }
        }

        double[] want = ReferenceResampler.Resize(n, W, H, channels, channels is 2 or 4, 25, 17, ResamplingFilter.Lanczos3, true);
        double worst = 0;
        for (int i = 0; i < 25 * 17; i++)
        {
            double a = channels is 2 or 4 ? want[(i * channels) + channels - 1] : 1;
            for (int k = 0; k < channels; k++)
            {
                double v = want[(i * channels) + k];
                if (channels == 4 && k < 3)
                {
                    v *= a;
                }

                worst = Math.Max(worst, Math.Abs(got[(i * channels) + k] - (v * 255)));
            }
        }

        // The premultiplied storage rounds twice (straight code, then times alpha).
        Assert.True(worst <= (channels == 4 ? 1.6 : 0.75), $"{fmt}: max error {worst:F3}");
    }

    [Fact]
    public void AutoFilterIsChosenPerAxis()
    {
        const int W = 80;
        const int H = 30;
        byte[] raw = RandomBytes(W * H * 3, 21);
        byte[] got = ToBytes(ImageOf(raw, W, H, PixelFormat.Rgb8).Resize(40, 90, Fit.Stretch), PixelFormat.Rgb8);
        double[] want = ReferenceResampler.Resize(raw.Select(b => b / 255.0).ToArray(), W, H, 3, false, 40, 90, ResamplingFilter.Lanczos3, true, ResamplingFilter.CatmullRom);
        double worst = 0;
        for (int i = 0; i < got.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(got[i] - (want[i] * 255)));
        }

        Assert.True(worst <= 0.75, $"max error {worst:F3}");
    }

    [Fact]
    public void Rgba64MatchesReference()
    {
        const int W = 50;
        const int H = 30;
        var rnd = new Random(3);
        Rgba64[] px = new Rgba64[W * H];
        double[] src = new double[W * H * 4];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba64((ushort)rnd.Next(65536), (ushort)rnd.Next(65536), (ushort)rnd.Next(65536), (ushort)rnd.Next(30000, 65536));
            src[i * 4] = px[i].R / 65535.0;
            src[(i * 4) + 1] = px[i].G / 65535.0;
            src[(i * 4) + 2] = px[i].B / 65535.0;
            src[(i * 4) + 3] = px[i].A / 65535.0;
        }

        Rgba64[] got = Image.FromPixels<Rgba64>(px, W, H).Resize(21, 33, Fit.Stretch, filter: ResamplingFilter.Mitchell).ToArray<Rgba64>();
        double[] want = ReferenceResampler.Resize(src, W, H, 4, true, 21, 33, ResamplingFilter.Mitchell, true);
        double worst = 0;
        for (int i = 0; i < got.Length; i++)
        {
            double[] g = [got[i].R, got[i].G, got[i].B, got[i].A];
            for (int k = 0; k < 4; k++)
            {
                worst = Math.Max(worst, Math.Abs(g[k] - (want[(i * 4) + k] * 65535)));
            }
        }

        TestContext.Current.SendDiagnosticMessage($"accuracy Rgba64 Mitchell: max error {worst:F3} (16-bit units)");
        Assert.True(worst <= 2.0, $"max error {worst:F3} (16-bit units)");
    }

    [Fact]
    public void FloatPixelsMatchReference()
    {
        const int W = 33;
        const int H = 21;
        var rnd = new Random(8);
        RgbaFloat[] px = new RgbaFloat[W * H];
        double[] src = new double[W * H * 4];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new RgbaFloat(rnd.NextSingle(), rnd.NextSingle(), rnd.NextSingle(), 0.3f + (0.7f * rnd.NextSingle()));
            src[i * 4] = px[i].R;
            src[(i * 4) + 1] = px[i].G;
            src[(i * 4) + 2] = px[i].B;
            src[(i * 4) + 3] = px[i].A;
        }

        RgbaFloat[] got = Image.FromPixels<RgbaFloat>(px, W, H).Resize(14, 9, Fit.Stretch, filter: ResamplingFilter.CatmullRom).ToArray<RgbaFloat>();
        double[] want = ReferenceResampler.Resize(src, W, H, 4, true, 14, 9, ResamplingFilter.CatmullRom, true);
        for (int i = 0; i < got.Length; i++)
        {
            Assert.InRange(got[i].R, (float)want[i * 4] - 2e-4f, (float)want[i * 4] + 2e-4f);
            Assert.InRange(got[i].A, (float)want[(i * 4) + 3] - 2e-4f, (float)want[(i * 4) + 3] + 2e-4f);
        }
    }

    [Theory]
    [InlineData(1000, 700, 100, 70)]
    [InlineData(500, 90, 60, 90)]
    [InlineData(640, 480, 64, 480)]
    public void BoxReducedPathStaysCloseToSingleStepReference(int w, int h, int dw, int dh)
    {
        byte[] raw = RandomBytes(w * h * 3, 17);
        // Smooth-ish content so the two-step and one-step filters should agree closely.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 3;
                raw[i] = (byte)Math.Min(255, (x * 240 / w) + (raw[i] & 15));
                raw[i + 1] = (byte)Math.Min(255, (y * 240 / h) + (raw[i + 1] & 15));
                raw[i + 2] = (byte)(128 + (raw[i + 2] & 31));
            }
        }

        Image img = ImageOf(raw, w, h, PixelFormat.Rgb8);
        Assert.True(ResampleNode.ReduceFactor(w, dw) > 1 || ResampleNode.ReduceFactor(h, dh) > 1);
        byte[] got = ToBytes(img.Resize(dw, dh, Fit.Stretch), PixelFormat.Rgb8);
        double[] n = raw.Select(b => b / 255.0).ToArray();
        double[] want = ReferenceResampler.Resize(n, w, h, 3, false, dw, dh, ResamplingFilter.Lanczos3, true);
        double worst = 0;
        double total = 0;
        for (int i = 0; i < got.Length; i++)
        {
            double d = Math.Abs(got[i] - (want[i] * 255));
            worst = Math.Max(worst, d);
            total += d;
        }

        Assert.True(worst <= 8, $"max {worst:F2} mean {total / got.Length:F3}");
    }

    internal static Image ImageOf(byte[] raw, int w, int h, PixelFormat fmt) => Image.FromNode(new RawBytesNode(raw, w, h, fmt));

    internal static byte[] ToBytes(Image img, PixelFormat fmt)
    {
        byte[] b = new byte[img.Width * img.Height * fmt.BytesPerPixel];
        Node n = img.Frames[0].Node;
        Render.ReadRegion(n, new ImageRect(0, 0, n.Width, n.Height), b, 0, fmt, RenderContext.Create(0, default));
        return b;
    }
}

/// <summary>Test source over a raw byte array; counts tile computations.</summary>
internal sealed class RawBytesNode : Node
{
    private readonly byte[] _raw;
    private int _computed;

    public RawBytesNode(byte[] raw, int w, int h, PixelFormat fmt, bool cacheable = true)
        : base(w, h, fmt, new OpDescriptor("raw_bytes", new OpParameter("width", (long)w)), [])
    {
        _raw = raw;
        Cacheable = cacheable;
    }

    public override bool Cacheable { get; }

    public int Computed => Volatile.Read(ref _computed);

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Interlocked.Increment(ref _computed);
        Tile t = Tile.Rent(Tile.ByteSize(Format), clear: true);
        ImageRect r = TileRect(tx, ty);
        int bpp = Format.BytesPerPixel;
        for (int y = 0; y < r.Height; y++)
        {
            _raw.AsSpan((((r.Y + y) * Width) + r.X) * bpp, r.Width * bpp).CopyTo(t.Span.Slice(y * Tile.Size * bpp, r.Width * bpp));
        }

        return t;
    }
}
