using OneImage.Kernels;
using Xunit;

namespace OneImage.Tests.Composite;

public class CompositeTests
{
    private static Rgb24[] RandomRgb(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var p = new Rgb24[w * h];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = new Rgb24((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        }

        return p;
    }

    private static Rgba32[] RandomRgba(int w, int h, int seed, bool opaque = false)
    {
        var rnd = new Random(seed);
        var p = new Rgba32[w * h];
        for (int i = 0; i < p.Length; i++)
        {
            // A third fully transparent, a third opaque, the rest anywhere between, so every branch of the math is hit.
            int a = opaque ? 255 : (rnd.Next(3) switch { 0 => 0, 1 => 255, _ => rnd.Next(256) });
            p[i] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)a);
        }

        return p;
    }

    /// <summary>Source-over in double precision on straight 0..255 values; returns straight RGBA.</summary>
    private static (double R, double G, double B, double A) Over(double[] s, double sa, double[] d, double da)
    {
        double oa = sa + (da * (1 - sa));
        double[] o = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double premult = (s[c] * sa) + (d[c] * da * (1 - sa));
            o[c] = oa > 0 ? premult / oa : 0;
        }

        return (o[0], o[1], o[2], oa * 255);
    }

    public static TheoryData<float, int, int> Placements() => new()
    {
        { 1f, 0, 0 }, { 1f, -20, 70 }, { 0.37f, 100, -30 }, { 0f, 5, 5 }, { 0.5f, 250, 200 }, { 1f, 127, 127 }, { 0.999f, -1000, 0 }, { 1f, 0, 400 },
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void OpaqueRgbBaseMatchesFloatReference(float opacity, int x, int y)
    {
        const int W = 300, H = 260, OW = 150, OH = 140;
        Rgb24[] b = RandomRgb(W, H, 1);
        Rgba32[] o = RandomRgba(OW, OH, 2);
        Rgb24[] got = Image.FromPixels<Rgb24>(b, W, H).Composite(Image.FromPixels<Rgba32>(o, OW, OH), x, y, opacity).ToArray<Rgb24>();
        int worst = 0;
        for (int py = 0; py < H; py++)
        {
            for (int px = 0; px < W; px++)
            {
                Rgb24 bp = b[(py * W) + px];
                Rgb24 e = bp;
                int ox = px - x, oy = py - y;
                double[] exp = [bp.R, bp.G, bp.B];
                if (ox >= 0 && oy >= 0 && ox < OW && oy < OH)
                {
                    Rgba32 op = o[(oy * OW) + ox];
                    double a = op.A / 255.0 * opacity;
                    exp = [(op.R * a) + (bp.R * (1 - a)), (op.G * a) + (bp.G * (1 - a)), (op.B * a) + (bp.B * (1 - a))];
                }

                Rgb24 g = got[(py * W) + px];
                worst = Math.Max(worst, (int)Math.Ceiling(Math.Max(Math.Abs(g.R - exp[0]), Math.Max(Math.Abs(g.G - exp[1]), Math.Abs(g.B - exp[2]))) - 1e-9));
                _ = e;
            }
        }

        Assert.True(worst <= 1, $"worst error {worst} LSB");
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public void SemiTransparentBaseMatchesFloatReference(float opacity, int x, int y)
    {
        const int W = 300, H = 260, OW = 150, OH = 140;
        Rgba32[] b = RandomRgba(W, H, 3);
        Rgba32[] o = RandomRgba(OW, OH, 4);
        Rgba32[] got = Image.FromPixels<Rgba32>(b, W, H).Composite(Image.FromPixels<Rgba32>(o, OW, OH), x, y, opacity).ToArray<Rgba32>();
        double worst = 0;
        for (int py = 0; py < H; py++)
        {
            for (int px = 0; px < W; px++)
            {
                Rgba32 bp = b[(py * W) + px];
                Rgba32 g = got[(py * W) + px];
                int ox = px - x, oy = py - y;
                if (!(ox >= 0 && oy >= 0 && ox < OW && oy < OH) || opacity == 0)
                {
                    Assert.Equal(bp, g);
                    continue;
                }

                Rgba32 op = o[(oy * OW) + ox];
                var e = Over([op.R, op.G, op.B], op.A / 255.0 * opacity, [bp.R, bp.G, bp.B], bp.A / 255.0);
                if (e.A < 0.5)
                {
                    // Nothing is visible here, so the color is not defined; only alpha matters.
                    Assert.True(g.A <= 1);
                    continue;
                }

                // Color is straight, so a pixel with little alpha amplifies the rounding of its premultiplied value.
                double tol = 1 + (255.0 / Math.Max(e.A, 1)) * 0.5;
                worst = Math.Max(worst, Math.Abs(g.A - e.A));
                Assert.True(Math.Abs(g.R - e.R) <= tol && Math.Abs(g.G - e.G) <= tol && Math.Abs(g.B - e.B) <= tol, $"({px},{py}) got {g} expected {e}");
            }
        }

        Assert.True(worst <= 1.01, $"alpha error {worst}");
    }

    [Fact]
    public void OverlayOutsideTheBaseLeavesItUntouchedAndOpacityZeroToo()
    {
        Rgb24[] b = RandomRgb(200, 200, 5);
        Image bi = Image.FromPixels<Rgb24>(b, 200, 200);
        Image ov = Image.FromPixels<Rgba32>(RandomRgba(50, 50, 6, opaque: true), 50, 50);
        foreach ((int x, int y) in new[] { (200, 0), (0, 200), (-50, 0), (0, -50), (-500, 900), (int.MaxValue / 8, 3) })
        {
            Assert.Equal(b, bi.Composite(ov, x, y).ToArray<Rgb24>());
        }

        Assert.Equal(b, bi.Composite(ov, 10, 10, opacity: 0f).ToArray<Rgb24>());
    }

    [Fact]
    public void OverlayLargerThanTheBaseCoversIt()
    {
        Rgb24[] b = RandomRgb(40, 30, 7);
        Rgba32[] o = RandomRgba(100, 90, 8, opaque: true);
        Rgb24[] got = Image.FromPixels<Rgb24>(b, 40, 30).Composite(Image.FromPixels<Rgba32>(o, 100, 90), -20, -10).ToArray<Rgb24>();
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Rgba32 e = o[((y + 10) * 100) + x + 20];
                Assert.Equal(new Rgb24(e.R, e.G, e.B), got[(y * 40) + x]);
            }
        }
    }

    [Fact]
    public void VectorAndScalarKernelsAgreeAtEveryWidth()
    {
        var rnd = new Random(9);
        foreach (uint op in new[] { 0u, 1u, 23456u, 65535u, 65536u })
        {
            for (int w = 1; w <= 70; w++)
            {
                byte[] b = new byte[(w * 3) + 4];
                byte[] o = new byte[(w * 4) + 4];
                rnd.NextBytes(b);
                rnd.NextBytes(o);
                byte[] d1 = new byte[(w * 3) + 4];
                byte[] d2 = new byte[(w * 3) + 4];
                CompositeKernel.OverRgb8(b, o, d1, w, op);
                CompositeKernel.OverRgb8Scalar(b, o, d2, w, op);
                Assert.True(d1.AsSpan(0, w * 3).SequenceEqual(d2.AsSpan(0, w * 3)), $"width {w}, opacity {op}");
            }
        }
    }

    [Fact]
    public void ResultDoesNotDependOnThreadCount()
    {
        Rgb24[] b = RandomRgb(700, 500, 10);
        Image img = Image.FromPixels<Rgb24>(b, 700, 500).Composite(Image.FromPixels<Rgba32>(RandomRgba(300, 300, 11), 300, 300), 123, 77, 0.7f);
        var one = new Rgb24[700 * 500];
        var many = new Rgb24[700 * 500];
        img.CopyPixelsTo<Rgb24>(one, new ImageRect(0, 0, 700, 500), maxDegree: 1);
        img.CopyPixelsTo<Rgb24>(many, new ImageRect(0, 0, 700, 500), maxDegree: 8);
        Assert.Equal(one, many);
    }

    [Fact]
    public void OnlyTilesTheOverlayTouchesAreComputed()
    {
        var baseNode = new PatternNode(3000, 2000);
        var overNode = new PatternNode(300, 300);
        Image img = Image.FromNode(baseNode).Composite(Image.FromNode(overNode), 500, 500);
        var buf = new Rgba32[128 * 128];

        // A tile far from the overlay: one base tile, no overlay tile.
        img.CopyPixelsTo<Rgba32>(buf, new ImageRect(2560, 1536, 128, 128), maxDegree: 1);
        Assert.Equal(1, baseNode.Computed);
        Assert.Equal(0, overNode.Computed);
        Assert.Equal(PatternNode.Expected(2560, 1536), buf[0]);

        // The tile holding the overlay's corner (500..512 of tile 3): one base tile and only the overlay tile at its origin.
        img.CopyPixelsTo<Rgba32>(buf, new ImageRect(384, 384, 128, 128), maxDegree: 1);
        Assert.Equal(2, baseNode.Computed);
        Assert.Equal(1, overNode.Computed);
        Rgba32 inside = buf[(120 * 128) + 120];
        Assert.NotEqual(PatternNode.Expected(504, 504), inside); // overlay changed it
        Assert.Equal(PatternNode.Expected(384, 384), buf[0]);
    }

    [Fact]
    public void P3OverlayIsConvertedToTheBasesSpace()
    {
        Rgb24[] colors = [new(200, 120, 90), new(90, 160, 120), new(150, 150, 60), new(110, 90, 170)];
        Image p3 = Image.FromPixels<Rgb24>(colors, 4, 1).AssignProfile(ColorProfile.DisplayP3);
        Image white = Image.FromPixels<Rgb24>(Enumerable.Repeat(new Rgb24(255, 255, 255), 8).ToArray(), 4, 2);
        Rgb24[] got = white.Composite(p3.ConvertTo(PixelFormat.Rgba8), 0, 0).ToArray<Rgb24>();
        byte[] px = colors.SelectMany(c => new[] { c.R, c.G, c.B }).ToArray();
        byte[] lcms = ColorTestUtil.Lcms(px, 4, 1, ColorModel.Rgb, ColorModel.Rgb, ColorProfile.DisplayP3.IccBytes.ToArray(), ColorProfile.Srgb.IccBytes.ToArray(), ImageMagick.RenderingIntent.Relative, true);
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(Math.Abs(got[i].R - lcms[3 * i]), 0, 2);
            Assert.InRange(Math.Abs(got[i].G - lcms[(3 * i) + 1]), 0, 2);
            Assert.InRange(Math.Abs(got[i].B - lcms[(3 * i) + 2]), 0, 2);
            Assert.True(Math.Abs(got[i].R - colors[i].R) + Math.Abs(got[i].G - colors[i].G) + Math.Abs(got[i].B - colors[i].B) > 6, "P3 values must not be copied as sRGB");
        }

        // Second row of the base is untouched.
        Assert.Equal(new Rgb24(255, 255, 255), got[4]);
    }

    [Fact]
    public void LinearBlendingMixesLight()
    {
        Image white = Image.FromPixels<Rgb24>(Enumerable.Repeat(new Rgb24(255, 255, 255), 4).ToArray(), 2, 2);
        Image halfBlack = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(0, 0, 0, 255), 4).ToArray(), 2, 2);
        Assert.InRange(white.Composite(halfBlack, opacity: 0.5f).ToArray<Rgb24>()[0].R, 127, 128);
        Image lin = white.Composite(halfBlack, opacity: 0.5f, linear: true);
        Assert.Equal(PixelFormat.Rgb8, lin.Format);
        Assert.InRange(lin.ToArray<Rgb24>()[0].G, 187, 189); // 0.5 linear light is 188 in sRGB
        Assert.Equal(white.Metadata.IccProfile.Length, lin.Metadata.IccProfile.Length);
    }

    [Fact]
    public void GrayAndSixteenBitBasesWork()
    {
        var g = new Gray8[64 * 64];
        Array.Fill(g, new Gray8(100));
        Image gi = Image.FromPixels<Gray8>(g, 64, 64);
        Image ov = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(200, 200, 200, 128), 100).ToArray(), 10, 10);
        Gray8[] got = gi.Composite(ov, 5, 5).ToArray<Gray8>();
        Assert.Equal(100, got[0].V);
        Assert.InRange(got[(6 * 64) + 6].V, 149, 151);

        var w = new Rgba64[32 * 32];
        Array.Fill(w, new Rgba64(0, 0, 0, 65535));
        Rgba64[] got16 = Image.FromPixels<Rgba64>(w, 32, 32).Composite(Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(255, 255, 255, 255), 16).ToArray(), 4, 4), 0, 0, 0.5f).ToArray<Rgba64>();
        Assert.InRange(got16[0].R, 32767, 32769);
        Assert.Equal(65535, got16[0].A);
    }

    [Fact]
    public void EveryFrameOfAnAnimationGetsTheOverlay()
    {
        Node f0 = new Resample.RawBytesNode(new byte[64 * 64 * 3], 64, 64, PixelFormat.Rgb8);
        Node f1 = new Resample.RawBytesNode(Enumerable.Repeat((byte)255, 64 * 64 * 3).ToArray(), 64, 64, PixelFormat.Rgb8);
        var anim = new Image([f0, f1], [TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80)], 3, ImageMetadata.Empty, null);
        Image ov = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(255, 0, 0, 255), 16).ToArray(), 4, 4);
        Image r = anim.Composite(ov, 2, 2);
        Assert.Equal(2, r.FrameCount);
        Assert.Equal(3, r.LoopCount);
        Assert.Equal(TimeSpan.FromMilliseconds(80), r.Frames[1].Delay);
        Assert.Equal(new Rgb24(255, 0, 0), r.Frames[0].ToImage().ToArray<Rgb24>()[(2 * 64) + 2]);
        Assert.Equal(new Rgb24(255, 0, 0), r.Frames[1].ToImage().ToArray<Rgb24>()[(2 * 64) + 2]);
        Assert.Equal(new Rgb24(255, 255, 255), r.Frames[1].ToImage().ToArray<Rgb24>()[0]);
    }

    [Fact]
    public void BadArgumentsAreRejected()
    {
        Image a = Image.FromPixels<Rgb24>(new Rgb24[4], 2, 2);
        Assert.Throws<ArgumentNullException>(() => a.Composite(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Composite(a, opacity: 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Composite(a, opacity: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Composite(a, x: int.MaxValue));
        Image cmyk = a.ConvertTo(PixelFormat.Cmyk8);
        Assert.Throws<ImageOperationException>(() => cmyk.Composite(a));
    }
}
