using OneImage.Kernels;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using Sharp = SixLabors.ImageSharp;

namespace OneImage.Tests.Composite;

public class BlendModeTests
{
    // ---- W3C Compositing and Blending Level 1, in double precision, on straight colors in 0..1 ----

    private static double Lum(double[] c) => (0.3 * c[0]) + (0.59 * c[1]) + (0.11 * c[2]);

    private static double[] ClipColor(double[] c)
    {
        double l = Lum(c), n = c.Min(), x = c.Max();
        if (n < 0)
        {
            c = c.Select(v => l + ((v - l) * l / (l - n))).ToArray();
        }

        if (x > 1)
        {
            c = c.Select(v => l + ((v - l) * (1 - l) / (x - l))).ToArray();
        }

        return c;
    }

    private static double[] SetLum(double[] c, double l)
    {
        double d = l - Lum(c);
        return ClipColor(c.Select(v => v + d).ToArray());
    }

    private static double Sat(double[] c) => c.Max() - c.Min();

    private static double[] SetSat(double[] c, double s)
    {
        // The spec sorts the channels into max/mid/min; this is the same mapping written per channel.
        double mn = c.Min(), mx = c.Max();
        return mx > mn ? c.Select(v => (v - mn) * s / (mx - mn)).ToArray() : [0, 0, 0];
    }

    private static double Sep(BlendMode m, double cb, double cs)
    {
        static double Burn(double b, double s) => b >= 1 ? 1 : s <= 0 ? 0 : 1 - Math.Min(1, (1 - b) / s);
        static double Dodge(double b, double s) => b <= 0 ? 0 : s >= 1 ? 1 : Math.Min(1, b / (1 - s));
        static double HardLight(double b, double s) => s <= 0.5 ? b * 2 * s : (b + ((2 * s) - 1)) - (b * ((2 * s) - 1));
        switch (m)
        {
            case BlendMode.Normal: return cs;
            case BlendMode.Multiply: return cb * cs;
            case BlendMode.Screen: return cb + cs - (cb * cs);
            case BlendMode.Overlay: return HardLight(cs, cb);
            case BlendMode.Darken: return Math.Min(cb, cs);
            case BlendMode.Lighten: return Math.Max(cb, cs);
            case BlendMode.ColorDodge: return Dodge(cb, cs);
            case BlendMode.ColorBurn: return Burn(cb, cs);
            case BlendMode.HardLight: return HardLight(cb, cs);
            case BlendMode.SoftLight:
                if (cs <= 0.5)
                {
                    return cb - ((1 - (2 * cs)) * cb * (1 - cb));
                }

                double dd = cb <= 0.25 ? ((((16 * cb) - 12) * cb) + 4) * cb : Math.Sqrt(cb);
                return cb + (((2 * cs) - 1) * (dd - cb));
            case BlendMode.Difference: return Math.Abs(cb - cs);
            case BlendMode.Exclusion: return cb + cs - (2 * cb * cs);
            case BlendMode.Add: return Math.Min(1, cb + cs);
            case BlendMode.Subtract: return Math.Max(0, cb - cs);
            case BlendMode.LinearBurn: return Math.Max(0, cb + cs - 1);
            case BlendMode.VividLight: return cs <= 0.5 ? Burn(cb, 2 * cs) : Dodge(cb, (2 * cs) - 1);
            case BlendMode.LinearLight: return Math.Clamp(cb + (2 * cs) - 1, 0, 1);
            case BlendMode.PinLight: return cs <= 0.5 ? Math.Min(cb, 2 * cs) : Math.Max(cb, (2 * cs) - 1);
            case BlendMode.HardMix: return cb + cs >= 1 ? 1 : 0;
            case BlendMode.Divide: return cs <= 0 ? (cb <= 0 ? 0 : 1) : Math.Min(1, cb / cs);
            default: throw new ArgumentOutOfRangeException(nameof(m));
        }
    }

    private static double[] Blend(BlendMode m, double[] cb, double[] cs) => m switch
    {
        BlendMode.Hue => SetLum(SetSat(cs, Sat(cb)), Lum(cb)),
        BlendMode.Saturation => SetLum(SetSat(cb, Sat(cs)), Lum(cb)),
        BlendMode.Color => SetLum(cs, Lum(cb)),
        BlendMode.Luminosity => SetLum(cb, Lum(cs)),
        _ => [Sep(m, cb[0], cs[0]), Sep(m, cb[1], cs[1]), Sep(m, cb[2], cs[2])],
    };

    private static (double Fa, double Fb) Factors(AlphaComposition c, double ab, double asrc) => c switch
    {
        AlphaComposition.Clear => (0, 0),
        AlphaComposition.Src => (1, 0),
        AlphaComposition.Dest => (0, 1),
        AlphaComposition.SrcOver => (1, 1 - asrc),
        AlphaComposition.DestOver => (1 - ab, 1),
        AlphaComposition.SrcIn => (ab, 0),
        AlphaComposition.DestIn => (0, asrc),
        AlphaComposition.SrcOut => (1 - ab, 0),
        AlphaComposition.DestOut => (0, 1 - asrc),
        AlphaComposition.SrcAtop => (ab, 1 - asrc),
        AlphaComposition.DestAtop => (1 - ab, asrc),
        _ => (1 - ab, 1 - asrc),
    };

    /// <summary>Straight 8-bit RGBA in, straight 0..255 RGBA out (alpha scaled by 255).</summary>
    private static double[] Reference(BlendMode m, AlphaComposition comp, Rgba32 back, Rgba32 over, double opacity)
    {
        double ab = back.A / 255.0, asrc = over.A / 255.0 * opacity;
        double[] cb = [back.R / 255.0, back.G / 255.0, back.B / 255.0];
        double[] cs = [over.R / 255.0, over.G / 255.0, over.B / 255.0];
        double[] b = Blend(m, cb, cs);
        (double fa, double fb) = Factors(comp, ab, asrc);
        double ao = (asrc * fa) + (ab * fb);
        double[] o = new double[4];
        for (int c = 0; c < 3; c++)
        {
            double cs2 = ((1 - ab) * cs[c]) + (ab * Math.Clamp(b[c], 0, 1));
            double premult = (asrc * fa * cs2) + (ab * fb * cb[c]);
            o[c] = ao > 0 ? premult / ao * 255 : 0;
        }

        o[3] = ao * 255;
        return o;
    }

    private static Rgba32[] Random(int n, int seed, bool translucent)
    {
        var rnd = new Random(seed);
        var p = new Rgba32[n];
        for (int i = 0; i < n; i++)
        {
            int a = !translucent ? 255 : (rnd.Next(4) switch { 0 => 0, 1 => 255, _ => rnd.Next(256) });
            p[i] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)a);
        }

        return p;
    }

    public static TheoryData<BlendMode> AllModes()
    {
        var d = new TheoryData<BlendMode>();
        foreach (BlendMode m in Enum.GetValues<BlendMode>().Distinct())
        {
            d.Add(m);
        }

        return d;
    }

    // Pixels that sit on a discontinuity of the formula (Hard Mix at cb + cs = 1) can legitimately land on either side in float.
    private static bool OnEdge(BlendMode m, Rgba32 back, Rgba32 over) =>
        m == BlendMode.HardMix && (Math.Abs(((back.R + over.R) / 255.0) - 1) < 1e-3 || Math.Abs(((back.G + over.G) / 255.0) - 1) < 1e-3 || Math.Abs(((back.B + over.B) / 255.0) - 1) < 1e-3);

    private static void Check(BlendMode m, AlphaComposition comp, bool translucentBase, float opacity)
    {
        const int W = 97, H = 61;
        Rgba32[] b = Random(W * H, 11, translucentBase);
        Rgba32[] o = Random(W * H, 12, true);
        Rgba32[] got = Image.FromPixels<Rgba32>(b, W, H).Composite(Image.FromPixels<Rgba32>(o, W, H), 0, 0, opacity, false, m, comp).ToArray<Rgba32>();
        double worst = 0;
        for (int i = 0; i < b.Length; i++)
        {
            if (OnEdge(m, b[i], o[i]))
            {
                continue;
            }

            double[] r = Reference(m, comp, b[i], o[i], opacity);
            double[] g = [got[i].R, got[i].G, got[i].B, got[i].A];
            double ea = Math.Abs(g[3] - r[3]);
            worst = Math.Max(worst, ea);
            if (r[3] > 0.5)
            {
                // Straight color is only meaningful where something is visible; a tiny alpha is quantized away.
                for (int c = 0; c < 3; c++)
                {
                    double e = Math.Abs(g[c] - r[c]);
                    if (r[3] >= 8)
                    {
                        worst = Math.Max(worst, e);
                    }
                }
            }

            Assert.True(worst <= 1.0 + 1e-6, $"{m}/{comp} pixel {i}: got {got[i]} want {r[0]:F2},{r[1]:F2},{r[2]:F2},{r[3]:F2} (back {b[i]}, over {o[i]})");
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void EveryBlendModeMatchesW3cFormulas(BlendMode mode)
    {
        Check(mode, AlphaComposition.SrcOver, true, 1f);
        Check(mode, AlphaComposition.SrcOver, false, 1f);
        Check(mode, AlphaComposition.SrcOver, true, 0.6f);
    }

    [Theory]
    [InlineData(AlphaComposition.SrcOver)]
    [InlineData(AlphaComposition.Clear)]
    [InlineData(AlphaComposition.Src)]
    [InlineData(AlphaComposition.Dest)]
    [InlineData(AlphaComposition.DestOver)]
    [InlineData(AlphaComposition.SrcIn)]
    [InlineData(AlphaComposition.DestIn)]
    [InlineData(AlphaComposition.SrcOut)]
    [InlineData(AlphaComposition.DestOut)]
    [InlineData(AlphaComposition.SrcAtop)]
    [InlineData(AlphaComposition.DestAtop)]
    [InlineData(AlphaComposition.Xor)]
    public void EveryCompositionMatchesPorterDuff(AlphaComposition comp)
    {
        Check(BlendMode.Normal, comp, true, 1f);
        Check(BlendMode.Multiply, comp, true, 0.7f);
    }

    [Fact]
    public void PlusAddsPremultipliedValuesAndClampsAtOne()
    {
        Rgba32[] back = [new(255, 0, 0, 200), new(0, 0, 255, 100), new(10, 20, 30, 255), new(9, 8, 7, 0)];
        Rgba32[] over = [new(255, 0, 0, 200), new(0, 255, 0, 100), new(0, 0, 0, 0), new(100, 50, 25, 90)];
        Rgba32[] got = Image.FromPixels<Rgba32>(back, 4, 1).Composite(Image.FromPixels<Rgba32>(over, 4, 1), 0, 0, 1f, false, BlendMode.Normal, AlphaComposition.Plus).ToArray<Rgba32>();

        Assert.Equal(new Rgba32(255, 0, 0, 255), got[0]);
        Assert.InRange(got[1].R, 0, 0);
        Assert.InRange(got[1].G, 127, 129);
        Assert.InRange(got[1].B, 127, 129);
        Assert.InRange(got[1].A, 199, 201);
        Assert.Equal(back[2], got[2]);
        Assert.Equal(over[3], got[3]);
        Assert.Equal("plus", BlendNames.Name(AlphaComposition.Plus));
    }

    [Fact]
    public void NormalSourceOverKernelIsBitIdenticalToTheDirectFormula()
    {
        var rnd = new Random(5);
        float[] bk = new float[4 * 1000], ov = new float[4 * 1000], got = new float[4 * 1000];
        for (int i = 0; i < bk.Length; i++)
        {
            bk[i] = (float)rnd.NextDouble();
            ov[i] = (float)rnd.NextDouble();
        }

        const float op = 0.37f;
        BlendKernel.Row(BlendMode.Normal, bk, ov, got, 3, op, AlphaComposition.SrcOver);
        for (int p = 0; p < 1000; p++)
        {
            float sa = ov[(4 * p) + 3] * op, da = bk[(4 * p) + 3];
            for (int c = 0; c < 3; c++)
            {
                Assert.Equal(BitConverter.SingleToInt32Bits((ov[(4 * p) + c] * op) + (bk[(4 * p) + c] * (1f - sa))), BitConverter.SingleToInt32Bits(got[(4 * p) + c]));
            }

            Assert.Equal(BitConverter.SingleToInt32Bits(sa + (da * (1f - sa))), BitConverter.SingleToInt32Bits(got[(4 * p) + 3]));
        }
    }

    [Fact]
    public void DefaultOverloadsAgree()
    {
        const int W = 70, H = 50;
        Image b = Image.FromPixels<Rgba32>(Random(W * H, 1, true), W, H);
        Image o = Image.FromPixels<Rgba32>(Random(30 * 20, 2, true), 30, 20);
        Rgba32[] a1 = b.Composite(o, 5, 6, 0.5f, false).ToArray<Rgba32>();
        Rgba32[] a2 = b.Composite(o, 5, 6, 0.5f, false, BlendMode.Normal, AlphaComposition.SrcOver).ToArray<Rgba32>();
        Rgba32[] a3 = b.Composite(o, 5, 6, 0.5f).ToArray<Rgba32>();
        Assert.Equal(a1, a2);
        Assert.Equal(a1, a3);
    }

    [Theory]
    [InlineData(BlendMode.Multiply, AlphaComposition.SrcOver)]
    [InlineData(BlendMode.Luminosity, AlphaComposition.SrcAtop)]
    [InlineData(BlendMode.SoftLight, AlphaComposition.Xor)]
    public void RegionRenderEqualsFullRender(BlendMode mode, AlphaComposition comp)
    {
        const int W = 400, H = 300;
        Image b = Image.FromPixels<Rgba32>(Random(W * H, 3, true), W, H);
        Image o = Image.FromPixels<Rgba32>(Random(250 * 200, 4, true), 250, 200);
        Image c = b.Composite(o, 90, 60, 0.8f, false, mode, comp);
        Rgba32[] full = c.ToArray<Rgba32>();
        var rect = new ImageRect(100, 70, 140, 130);
        var part = new Rgba32[rect.Width * rect.Height];
        c.CopyPixelsTo<Rgba32>(part, rect);
        for (int y = 0; y < rect.Height; y++)
        {
            for (int x = 0; x < rect.Width; x++)
            {
                Assert.Equal(full[((rect.Y + y) * W) + rect.X + x], part[(y * rect.Width) + x]);
            }
        }
    }

    [Fact]
    public void OnlyPixelsUnderTheOverlayChange()
    {
        Rgba32[] b = Random(60 * 40, 8, true);
        Rgba32[] got = Image.FromPixels<Rgba32>(b, 60, 40).Composite(Image.FromPixels<Rgba32>(Random(10 * 10, 9, true), 10, 10), 20, 15, 1f, false, BlendMode.Normal, AlphaComposition.Clear).ToArray<Rgba32>();
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 60; x++)
            {
                bool inside = x is >= 20 and < 30 && y is >= 15 and < 25;
                if (inside)
                {
                    Assert.Equal(0, got[(y * 60) + x].A);
                }
                else
                {
                    Assert.Equal(b[(y * 60) + x], got[(y * 60) + x]);
                }
            }
        }
    }

    [Fact]
    public void GrayBaseAndRgbBase()
    {
        const int W = 40, H = 30;
        var g = new Gray8[W * H];
        for (int i = 0; i < g.Length; i++)
        {
            g[i] = new Gray8((byte)(i * 7));
        }

        Image gray = Image.FromPixels<Gray8>(g, W, H);
        Image ov = Image.FromPixels<Rgba32>(Random(W * H, 6, true), W, H);
        Gray8[] mul = gray.Composite(ov, 0, 0, 1f, false, BlendMode.Multiply).ToArray<Gray8>();
        Assert.Equal(W * H, mul.Length);
        // Hue on gray has no chroma to work with: the backdrop survives wherever the overlay is opaque enough to matter.
        Image opaque = Image.FromPixels<Rgba32>(Random(W * H, 6, false), W, H);
        Gray8[] hue = gray.Composite(opaque, 0, 0, 1f, false, BlendMode.Hue).ToArray<Gray8>();
        Assert.Equal(g, hue);
        Gray8[] lum = gray.Composite(opaque, 0, 0, 1f, false, BlendMode.Luminosity).ToArray<Gray8>();
        Assert.NotEqual(g, lum);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Image rgb = Image.FromPixels<Rgb24>(new Rgb24[16], 4, 4);
        Image ov = Image.FromPixels<Rgba32>(new Rgba32[16], 4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => rgb.Composite(ov, 0, 0, 1f, false, (BlendMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => rgb.Composite(ov, 0, 0, 1f, false, BlendMode.Normal, (AlphaComposition)99));
        Assert.Throws<ImageOperationException>(() => rgb.Composite(ov, 0, 0, 1f, false, BlendMode.Normal, AlphaComposition.SrcIn));
        // Operators that keep an opaque base opaque work on RGB.
        rgb.Composite(ov, 0, 0, 1f, false, BlendMode.Screen, AlphaComposition.SrcAtop).ToArray<Rgb24>();
    }

    [Fact]
    public void LinearLightBlendDiffersFromEncoded()
    {
        Image b = Image.FromPixels<Rgb24>(Enumerable.Repeat(new Rgb24(128, 128, 128), 64).ToArray(), 8, 8);
        Image o = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(128, 128, 128, 255), 64).ToArray(), 8, 8);
        Rgb24 enc = b.Composite(o, 0, 0, 1f, false, BlendMode.Multiply).ToArray<Rgb24>()[0];
        Rgb24 lin = b.Composite(o, 0, 0, 1f, true, BlendMode.Multiply).ToArray<Rgb24>()[0];
        Assert.InRange((int)enc.R, 63, 65);
        Assert.InRange((int)lin.R, 55, 62); // mid-gray squared in linear light, back to sRGB: darker than the encoded product
    }

    // ---- ImageSharp 3.1.12 as an oracle for the modes it has ----

    private static PixelColorBlendingMode Map(BlendMode m) => m switch
    {
        BlendMode.Normal => PixelColorBlendingMode.Normal,
        BlendMode.Multiply => PixelColorBlendingMode.Multiply,
        BlendMode.Add => PixelColorBlendingMode.Add,
        BlendMode.Subtract => PixelColorBlendingMode.Subtract,
        BlendMode.Screen => PixelColorBlendingMode.Screen,
        BlendMode.Darken => PixelColorBlendingMode.Darken,
        BlendMode.Lighten => PixelColorBlendingMode.Lighten,
        BlendMode.Overlay => PixelColorBlendingMode.Overlay,
        BlendMode.HardLight => PixelColorBlendingMode.HardLight,
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    public static TheoryData<BlendMode> SharpModes() => new()
    {
        BlendMode.Normal, BlendMode.Multiply, BlendMode.Add, BlendMode.Subtract, BlendMode.Screen, BlendMode.Darken, BlendMode.Lighten, BlendMode.Overlay, BlendMode.HardLight,
    };

    [Theory]
    [MemberData(nameof(SharpModes))]
    public void BlendModesAgreeWithImageSharpOnOpaqueBase(BlendMode mode)
    {
        const int W = 80, H = 60;
        Rgba32[] b = Random(W * H, 21, false);
        Rgba32[] o = Random(W * H, 22, true);
        Rgba32[] ours = Image.FromPixels<Rgba32>(b, W, H).Composite(Image.FromPixels<Rgba32>(o, W, H), 0, 0, 0.8f, false, mode).ToArray<Rgba32>();
        using var sb = Sharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(ToSharp(b), W, H);
        using var so = Sharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(ToSharp(o), W, H);
        sb.Mutate(c => c.DrawImage(so, new Sharp.Point(0, 0), Map(mode), 0.8f));
        int max = 0;
        for (int i = 0; i < b.Length; i++)
        {
            SixLabors.ImageSharp.PixelFormats.Rgba32 s = sb[i % W, i / W];
            max = Math.Max(max, Math.Max(Math.Abs(s.R - ours[i].R), Math.Max(Math.Abs(s.G - ours[i].G), Math.Abs(s.B - ours[i].B))));
        }

        // Both blend straight colors in the encoded space with the W3C general rule; ImageSharp rounds through 8-bit premultiplied
        // intermediates and its Add/Subtract clamp differently where the backdrop is not opaque, so allow a few levels.
        Assert.True(max <= 3, $"{mode}: max channel difference {max}");
    }

    private static byte[] ToSharp(Rgba32[] p)
    {
        byte[] r = new byte[p.Length * 4];
        for (int i = 0; i < p.Length; i++)
        {
            r[4 * i] = p[i].R;
            r[(4 * i) + 1] = p[i].G;
            r[(4 * i) + 2] = p[i].B;
            r[(4 * i) + 3] = p[i].A;
        }

        return r;
    }
}
