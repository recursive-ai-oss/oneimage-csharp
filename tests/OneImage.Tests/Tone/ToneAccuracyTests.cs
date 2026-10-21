using Xunit;
using static OneImage.Tests.Tone.ToneUtil;

namespace OneImage.Tests.Tone;

public class ToneAccuracyTests
{
    private static double Lin(bool linear, double v, Func<double, double> f) => linear ? Enc(Clamp01Safe(f(Dec(v)))) : f(v);

    // Linear mode uses the exact curve on whatever the op produced; values beyond 0..1 clamp at the end like the storage does.
    private static double Clamp01Safe(double v) => v;

    public static IEnumerable<object[]> ChannelOps()
    {
        foreach (bool lin in new[] { false, true })
        {
            yield return [$"bc lin={lin}", lin, (Func<Image, Image>)(i => i.BrightnessContrast(0.1, 0.3, lin)), (Func<double, double>)(v => ((v - 0.5) * Math.Tan((0.3 + 1) * Math.PI / 4)) + 0.5 + 0.1)];
            yield return [$"bc2 lin={lin}", lin, (Func<Image, Image>)(i => i.BrightnessContrast(-0.2, -0.4, lin)), (Func<double, double>)(v => ((v - 0.5) * Math.Tan((-0.4 + 1) * Math.PI / 4)) + 0.5 - 0.2)];
            yield return [$"exposure lin={lin}", lin, (Func<Image, Image>)(i => i.Exposure(1.5, lin)), (Func<double, double>)(v => v * Math.Pow(2, 1.5))];
            yield return [$"gamma lin={lin}", lin, (Func<Image, Image>)(i => i.Gamma(2.2, lin)), (Func<double, double>)(v => v > 0 ? Math.Pow(v, 1 / 2.2) : 0)];
            yield return [$"levels lin={lin}", lin, (Func<Image, Image>)(i => i.Levels(0.1, 0.9, 1.4, 0.05, 0.95, ColorChannel.All, lin)), (Func<double, double>)(v => 0.05 + (0.9 * Math.Pow(Clamp01((v - 0.1) / 0.8), 1 / 1.4)))];
            yield return [$"invert lin={lin}", lin, (Func<Image, Image>)(i => i.Invert(lin)), (Func<double, double>)(v => 1 - v)];
            yield return [$"posterize lin={lin}", lin, (Func<Image, Image>)(i => i.Posterize(6, lin)), (Func<double, double>)(v => Math.Floor((Clamp01(v) * 5) + 0.5) / 5)];
            yield return [$"threshold lin={lin}", lin, (Func<Image, Image>)(i => i.Threshold(0.4, true, lin)), (Func<double, double>)(v => v >= 0.4 ? 1 : 0)];
        }
    }

    [Theory]
    [MemberData(nameof(ChannelOps))]
    public void ChannelOpsMatchDoubleReference(string name, bool linear, Func<Image, Image> op, Func<double, double> f)
    {
        Rgba32[] src = Pixels8(300, 150);
        Image img = Image.FromPixels<Rgba32>(src, 300, 150);
        Rgba32[] got = op(img).ToArray<Rgba32>();
        int worst = 0;
        for (int i = 0; i < src.Length; i++)
        {
            byte[] s = [src[i].R, src[i].G, src[i].B];
            byte[] g = [got[i].R, got[i].G, got[i].B];
            for (int k = 0; k < 3; k++)
            {
                double v = s[k] / 255.0;
                double e = name.StartsWith("threshold", StringComparison.Ordinal) ? f(v) : Lin(linear, v, f);
                worst = Math.Max(worst, Math.Abs(g[k] - Q(e, 255)));
            }

            Assert.Equal(src[i].A, got[i].A);
        }

        Assert.True(worst <= 1, $"{name}: worst error {worst} at 8 bits");

        Rgba64[] src16 = Pixels16(300, 150);
        Rgba64[] got16 = op(Image.FromPixels<Rgba64>(src16, 300, 150)).ToArray<Rgba64>();
        int worst16 = 0;
        for (int i = 0; i < src16.Length; i += 3)
        {
            ushort[] s = [src16[i].R, src16[i].G, src16[i].B];
            ushort[] g = [got16[i].R, got16[i].G, got16[i].B];
            for (int k = 0; k < 3; k++)
            {
                double v = s[k] / 65535.0;
                double e = name.StartsWith("threshold", StringComparison.Ordinal) ? f(v) : Lin(linear, v, f);
                worst16 = Math.Max(worst16, Math.Abs(g[k] - Q(e, 65535)));
            }

            Assert.Equal(src16[i].A, got16[i].A);
        }

        Assert.True(worst16 <= 1, $"{name}: worst error {worst16} at 16 bits");
    }

    [Fact]
    public void ReportHardwarePath()
    {
        // Printed so a run under the DOTNET_Enable* knobs shows which path the host took.
        string line = $"Vector256.IsHardwareAccelerated={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} Avx2.IsSupported={System.Runtime.Intrinsics.X86.Avx2.IsSupported} Avx512F.IsSupported={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}";
        if (Environment.GetEnvironmentVariable("ONEIMAGE_TONE_REPORT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + "\n");
        }

        Assert.NotEmpty(line);
    }

    [Fact]
    public void CurvesFollowMonotoneCubicAndHitControlPoints()
    {
        CurvePoint[] pts = [new(0, 0), new(0.25, 0.1), new(0.6, 0.7), new(1, 1)];
        Image img = Make8();
        Rgba32[] src = img.ToArray<Rgba32>();
        Rgba32[] got = img.Curves(pts).ToArray<Rgba32>();
        // Reference: Fritsch-Carlson tangents in double.
        double[] xs = pts.Select(p => p.X).ToArray();
        double[] ys = pts.Select(p => p.Y).ToArray();
        double[] d = [(ys[1] - ys[0]) / (xs[1] - xs[0]), (ys[2] - ys[1]) / (xs[2] - xs[1]), (ys[3] - ys[2]) / (xs[3] - xs[2])];
        double[] m = [d[0], (d[0] + d[1]) / 2, (d[1] + d[2]) / 2, d[2]];
        for (int i = 0; i < 3; i++)
        {
            double a = m[i] / d[i];
            double b = m[i + 1] / d[i];
            if ((a * a) + (b * b) > 9)
            {
                double tau = 3 / Math.Sqrt((a * a) + (b * b));
                m[i] = tau * a * d[i];
                m[i + 1] = tau * b * d[i];
            }
        }

        double Eval(double x)
        {
            int s = x >= xs[2] ? 2 : x >= xs[1] ? 1 : 0;
            double h = xs[s + 1] - xs[s];
            double t = (x - xs[s]) / h;
            return (((2 * t * t * t) - (3 * t * t) + 1) * ys[s]) + (((t * t * t) - (2 * t * t) + t) * h * m[s]) + (((-2 * t * t * t) + (3 * t * t)) * ys[s + 1]) + (((t * t * t) - (t * t)) * h * m[s + 1]);
        }

        int worst = 0;
        for (int i = 0; i < src.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(got[i].R - Q(Eval(src[i].R / 255.0), 255)));
            worst = Math.Max(worst, Math.Abs(got[i].G - Q(Eval(src[i].G / 255.0), 255)));
        }

        Assert.True(worst <= 1, $"curve error {worst}");

        // Monotone data must give a monotone result (no overshoot), checked over the full 0..255 ramp.
        Rgba32[] ramp = Enumerable.Range(0, 256).Select(i => new Rgba32((byte)i, (byte)i, (byte)i, 255)).ToArray();
        Rgba32[] r = Image.FromPixels<Rgba32>(ramp, 256, 1).Curves([new(0, 0), new(0.1, 0.5), new(0.2, 0.52), new(1, 1)]).ToArray<Rgba32>();
        for (int i = 1; i < 256; i++)
        {
            Assert.True(r[i].R >= r[i - 1].R, $"curve not monotone at {i}");
        }
    }

    [Fact]
    public void PerChannelLevelsAndCurvesTouchOnlyThatChannel()
    {
        Image img = Make8();
        Rgba32[] src = img.ToArray<Rgba32>();
        Rgba32[] got = img.Levels(inBlack: 0.2, channel: ColorChannel.Green).ToArray<Rgba32>();
        Rgba32[] cur = img.Curves([new(0, 1), new(1, 0)], ColorChannel.Blue).ToArray<Rgba32>();
        for (int i = 0; i < src.Length; i++)
        {
            Assert.Equal(src[i].R, got[i].R);
            Assert.Equal(src[i].B, got[i].B);
            Assert.InRange(Math.Abs(Q((src[i].G / 255.0 - 0.2) / 0.8, 255) - got[i].G), 0, 1);
            Assert.Equal(src[i].R, cur[i].R);
            Assert.Equal(255 - src[i].B, cur[i].B);
        }

        Assert.Throws<ImageOperationException>(() => img.ConvertTo(PixelFormat.Gray8).Levels(channel: ColorChannel.Red));
    }

    [Fact]
    public void FloatImagesAreNotClampedByLinearOps()
    {
        var px = new RgbaFloat[] { new(0.2f, 0.5f, 1.5f, 1f), new(-0.5f, 0.0f, 2f, 0.5f) };
        RgbaFloat[] got = Image.FromPixels<RgbaFloat>(px, 2, 1).Exposure(1, linear: false).Invert().ToArray<RgbaFloat>();
        Assert.Equal(1f - 0.4f, got[0].R, 5);
        Assert.Equal(1f - 3f, got[0].B, 5);
        Assert.Equal(1f + 1f, got[1].R, 5);
        Assert.Equal(0.5f, got[1].A);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GrayscaleMatchesRec709(bool linear)
    {
        Rgba32[] src = Pixels8(300, 150);
        Rgba32[] got = Image.FromPixels<Rgba32>(src, 300, 150).Grayscale(linear).ToArray<Rgba32>();
        int worst = 0;
        for (int i = 0; i < src.Length; i++)
        {
            double y = linear
                ? Enc((0.2126 * Dec(src[i].R / 255.0)) + (0.7152 * Dec(src[i].G / 255.0)) + (0.0722 * Dec(src[i].B / 255.0)))
                : ((0.2126 * src[i].R) + (0.7152 * src[i].G) + (0.0722 * src[i].B)) / 255.0;
            int e = Q(y, 255);
            Assert.Equal(got[i].R, got[i].G);
            Assert.Equal(got[i].R, got[i].B);
            worst = Math.Max(worst, Math.Abs(got[i].R - e));
        }

        Assert.True(worst <= 1, $"grayscale error {worst}");

        Rgba64[] src16 = Pixels16(200, 100);
        Rgba64[] got16 = Image.FromPixels<Rgba64>(src16, 200, 100).Grayscale(linear).ToArray<Rgba64>();
        int worst16 = 0;
        for (int i = 0; i < src16.Length; i++)
        {
            double y = linear
                ? Enc((0.2126 * Dec(src16[i].R / 65535.0)) + (0.7152 * Dec(src16[i].G / 65535.0)) + (0.0722 * Dec(src16[i].B / 65535.0)))
                : ((0.2126 * src16[i].R) + (0.7152 * src16[i].G) + (0.0722 * src16[i].B)) / 65535.0;
            worst16 = Math.Max(worst16, Math.Abs(got16[i].R - Q(y, 65535)));
        }

        Assert.True(worst16 <= 2, $"grayscale error {worst16} at 16 bits");
    }

    [Fact]
    public void GrayscaleToGrayFormatKeepsAlphaAndGrayIsUntouched()
    {
        Image img = Make8(opaque: false);
        Image g = img.Grayscale(toGrayFormat: true);
        Assert.Equal(PixelFormat.GrayAlpha8, g.Format);
        Rgba32[] full = img.Grayscale().ToArray<Rgba32>();
        GrayAlpha8[] small = g.ToArray<GrayAlpha8>();
        for (int i = 0; i < small.Length; i++)
        {
            Assert.Equal(full[i].R, small[i].V);
            Assert.Equal(full[i].A, small[i].A);
        }

        Image gray = img.ConvertTo(PixelFormat.Gray8);
        Assert.Equal(gray.ToArray<Gray8>(), gray.Grayscale().ToArray<Gray8>());
    }

    private static (double h, double s, double l) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double d = max - min;
        if (d == 0)
        {
            return (0, 0, l);
        }

        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        return (h / 6, s, l);
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        return t < 1.0 / 6 ? p + ((q - p) * 6 * t) : t < 0.5 ? q : t < 2.0 / 3 ? p + ((q - p) * ((2.0 / 3) - t) * 6) : p;
    }

    [Fact]
    public void HueSaturationLightnessMatchesReference()
    {
        Rgba32[] src = Pixels8(300, 150);
        Image img = Image.FromPixels<Rgba32>(src, 300, 150);
        foreach ((double hue, double sat, double light) in new[] { (40.0, 0.0, 0.0), (0.0, 0.5, 0.0), (0.0, -0.6, 0.2), (200.0, 0.3, -0.3), (-90.0, 1.0, 0.5) })
        {
            Rgba32[] got = img.HueSaturationLightness(hue, sat, light).ToArray<Rgba32>();
            int worst = 0;
            for (int i = 0; i < src.Length; i++)
            {
                (double h, double s, double l) = ToHsl(src[i].R / 255.0, src[i].G / 255.0, src[i].B / 255.0);
                h = (h + (hue / 360)) % 1;
                if (h < 0)
                {
                    h += 1;
                }

                s = Clamp01(s * (1 + sat));
                l = light >= 0 ? l + ((1 - l) * light) : l * (1 + light);
                double r, g, b;
                if (s == 0)
                {
                    r = g = b = l;
                }
                else
                {
                    double q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
                    double p = (2 * l) - q;
                    r = Hue(p, q, h + (1.0 / 3));
                    g = Hue(p, q, h);
                    b = Hue(p, q, h - (1.0 / 3));
                }

                worst = Math.Max(worst, Math.Abs(got[i].R - Q(r, 255)));
                worst = Math.Max(worst, Math.Abs(got[i].G - Q(g, 255)));
                worst = Math.Max(worst, Math.Abs(got[i].B - Q(b, 255)));
            }

            Assert.True(worst <= 1, $"hsl({hue},{sat},{light}) error {worst}");
        }
    }

    [Fact]
    public void HueRotationMovesPrimaries()
    {
        Rgba32[] px = [new(255, 0, 0, 255), new(0, 255, 0, 255), new(128, 128, 128, 255)];
        Rgba32[] got = Image.FromPixels<Rgba32>(px, 3, 1).HueSaturationLightness(hue: 120).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(0, 255, 0, 255), got[0]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), got[1]);
        Assert.Equal(px[2], got[2]);
        Rgba32[] desat = Image.FromPixels<Rgba32>(px, 3, 1).HueSaturationLightness(saturation: -1).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(128, 128, 128, 255), desat[0]);
    }

    [Fact]
    public void ChannelMixerMatchesReferenceAndSwapIsExact()
    {
        Rgba32[] src = Pixels8(300, 150);
        Image img = Image.FromPixels<Rgba32>(src, 300, 150);
        double[] m = [0.3, 0.6, 0.1, 0.2, 0.2, 0.6, 1.1, -0.1, 0.0];
        Rgba32[] got = img.ChannelMixer(m).ToArray<Rgba32>();
        int worst = 0;
        for (int i = 0; i < src.Length; i++)
        {
            double r = src[i].R / 255.0, g = src[i].G / 255.0, b = src[i].B / 255.0;
            worst = Math.Max(worst, Math.Abs(got[i].R - Q((m[0] * r) + (m[1] * g) + (m[2] * b), 255)));
            worst = Math.Max(worst, Math.Abs(got[i].G - Q((m[3] * r) + (m[4] * g) + (m[5] * b), 255)));
            worst = Math.Max(worst, Math.Abs(got[i].B - Q((m[6] * r) + (m[7] * g) + (m[8] * b), 255)));
        }

        Assert.True(worst <= 1, $"mixer error {worst}");
        Rgba32[] swapped = img.ChannelMixer([0, 0, 1, 0, 1, 0, 1, 0, 0]).ToArray<Rgba32>();
        for (int i = 0; i < src.Length; i++)
        {
            Assert.Equal(new Rgba32(src[i].B, src[i].G, src[i].R, src[i].A), swapped[i]);
        }

        Assert.Throws<ImageOperationException>(() => img.ConvertTo(PixelFormat.Gray8).ChannelMixer(m));
    }

    [Fact]
    public void IdentityParametersAreExactNoOps()
    {
        foreach (Image img in new[] { Make8(opaque: false), Make16(opaque: false), MakeFloat() })
        {
            Image[] ids =
            [
                img.BrightnessContrast(),
                img.Exposure(0),
                img.Gamma(1),
                img.Levels(),
                img.Curves([new(0, 0), new(1, 1)]),
                img.Curves([new(0, 0), new(0.5, 0.5), new(1, 1)]),
                img.HueSaturationLightness(),
                img.ChannelMixer([1, 0, 0, 0, 1, 0, 0, 0, 1]),
                img.RemoveAlpha().AddAlpha(1).RemoveAlpha().AddAlpha(1).ConvertTo(img.Format).BrightnessContrast(),
                img.Grayscale().Grayscale(),
            ];
            Assert.Equal(img.Format, ids[0].Format);
            byte[] expect = Bytes(img);
            for (int i = 0; i < 8; i++)
            {
                Assert.True(expect.AsSpan().SequenceEqual(Bytes(ids[i])), $"identity {i} changed pixels");
            }

            // A chain of identities is still the original node (nothing to compute).
            Assert.Same(img.Frames[0].Node, img.Gamma(1).Exposure(0).Levels().Frames[0].Node);
        }
    }

    // An identity step buried in a fused chain must not change a pixel either.
    [Fact]
    public void IdentityInsideFusedChainDoesNotChangePixels()
    {
        Image img = Make8(opaque: false);
        Image a = img.Invert().Curves([new(0, 0), new(1, 1)]).Invert();
        Assert.Equal(Bytes(img), Bytes(a));
        Image b = img.Levels(0.1, 0.9).Levels();
        Assert.Equal(Bytes(img.Levels(0.1, 0.9)), Bytes(b));
    }

    private static byte[] Bytes(Image img)
    {
        PixelFormat f = img.Format;
        byte[] buf = new byte[img.Width * img.Height * f.BytesPerPixel];
        // Pull through the internal reader to avoid converting to a pixel struct.
        Render.ReadRegion(img.Frames[0].Node, new ImageRect(0, 0, img.Width, img.Height), buf, 0, f, new RenderContext(1, TileCache.Global, default));
        return buf;
    }

    [Fact]
    public void BadArgumentsAndModelsAreRejected()
    {
        Image img = Make8();
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Gamma(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Levels(inBlack: 0.5, inWhite: 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.BrightnessContrast(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Posterize(1));
        Assert.Throws<ArgumentException>(() => img.Curves([new(0, 0)]));
        Assert.Throws<ArgumentException>(() => img.Curves([new(0, 0), new(0, 1)]));
        Assert.Throws<ArgumentException>(() => img.ChannelMixer([1, 2, 3]));
        Image cmyk = img.Crop(0, 0, 8, 8).ConvertTo(PixelFormat.Cmyk8);
        Assert.Throws<ImageOperationException>(() => cmyk.Gamma(2));
        Assert.Throws<ImageOperationException>(() => cmyk.Invert());
    }
}
