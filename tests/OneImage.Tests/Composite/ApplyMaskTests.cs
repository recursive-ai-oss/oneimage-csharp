using Xunit;

namespace OneImage.Tests.Composite;

public class ApplyMaskTests
{
    private static Rgba32[] Random(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var p = new Rgba32[w * h];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)(rnd.Next(4) == 0 ? 255 : rnd.Next(256)));
        }

        return p;
    }

    private static int Lum(Rgba32 m, bool linear)
    {
        static double Lin(byte v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        double r = linear ? Lin(m.R) : m.R / 255.0;
        double g = linear ? Lin(m.G) : m.G / 255.0;
        double b = linear ? Lin(m.B) : m.B / 255.0;
        return (int)Math.Round(((0.2125 * r) + (0.7154 * g) + (0.0721 * b)) * m.A);
    }

    // Large enough to span several 128-pixel tiles in both directions, with an odd size so edge tiles are partial.
    private const int W = 301;
    private const int H = 259;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CoverageIsAlphaOrLuminanceTimesAlpha(bool luminance, bool linear)
    {
        Rgba32[] src = Random(W, H, 1);
        Rgba32[] mask = Random(W, H, 2);
        Rgba32[] got = Image.FromPixels<Rgba32>(src, W, H)
            .ApplyMask(Image.FromPixels<Rgba32>(mask, W, H), luminance ? MaskChannel.Luminance : MaskChannel.Alpha, linear: linear)
            .ToArray<Rgba32>();
        int worst = 0;
        for (int i = 0; i < src.Length; i++)
        {
            int cover = luminance ? Lum(mask[i], linear) : mask[i].A;
            int expected = (int)Math.Round(src[i].A * cover / 255.0);
            worst = Math.Max(worst, Math.Abs(got[i].A - expected));
            if (expected > 0)
            {
                Assert.Equal(src[i].R, got[i].R);
                Assert.Equal(src[i].G, got[i].G);
                Assert.Equal(src[i].B, got[i].B);
            }
        }

        Assert.True(worst <= 1, $"alpha differs by {worst}");
    }

    [Fact]
    public void WhiteOpaqueMaskKeepsTheImageAndBlackHidesIt()
    {
        Rgba32[] src = Random(40, 40, 3);
        Image img = Image.FromPixels<Rgba32>(src, 40, 40);
        var white = Enumerable.Repeat(new Rgba32(255, 255, 255, 255), 1600).ToArray();
        var black = Enumerable.Repeat(new Rgba32(0, 0, 0, 255), 1600).ToArray();
        Rgba32[] clean = [.. src.Select(p => p.A == 0 ? default : p)]; // alpha 0 reads back as transparent black
        Assert.Equal(clean, img.ApplyMask(Image.FromPixels<Rgba32>(white, 40, 40), MaskChannel.Luminance).ToArray<Rgba32>());
        Assert.All(img.ApplyMask(Image.FromPixels<Rgba32>(black, 40, 40), MaskChannel.Luminance).ToArray<Rgba32>(), p => Assert.Equal(0, p.A));
        Assert.Equal(clean, img.ApplyMask(Image.FromPixels<Rgba32>(black, 40, 40), MaskChannel.Alpha).ToArray<Rgba32>());
    }

    [Fact]
    public void ARectangularMaskAtAnOffsetLeavesTheRestTransparent()
    {
        const int Mw = 150, Mh = 90, Ox = 100, Oy = -20;
        Rgba32[] src = Random(W, H, 4);
        var mask = Enumerable.Repeat(new Rgba32(10, 10, 10, 255), Mw * Mh).ToArray();
        Rgba32[] got = Image.FromPixels<Rgba32>(src, W, H).ApplyMask(Image.FromPixels<Rgba32>(mask, Mw, Mh), MaskChannel.Alpha, Ox, Oy).ToArray<Rgba32>();
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                bool inside = x >= Ox && x < Ox + Mw && y >= Oy && y < Oy + Mh;
                Rgba32 s = src[(y * W) + x];
                Assert.Equal(inside && s.A != 0 ? s : default, got[(y * W) + x]);
            }
        }
    }

    [Fact]
    public void AMaskEntirelyOutsideTheImageHidesEverything()
    {
        Rgba32[] src = Random(50, 50, 5);
        Image mask = Image.FromPixels<Rgba32>(Random(10, 10, 6), 10, 10);
        Assert.All(Image.FromPixels<Rgba32>(src, 50, 50).ApplyMask(mask, x: 1000, y: 1000).ToArray<Rgba32>(), p => Assert.Equal(default, p));
    }

    [Fact]
    public void OtherFormatsAgreeWithTheEightBitPath()
    {
        Rgba32[] src = Random(W, H, 7);
        Rgba32[] mask = Random(W, H, 8);
        Image m = Image.FromPixels<Rgba32>(mask, W, H);
        Rgba32[] eight = Image.FromPixels<Rgba32>(src, W, H).ApplyMask(m, MaskChannel.Luminance).ToArray<Rgba32>();

        // 16-bit straight and 32-bit float premultiplied go through the float path.
        foreach (PixelFormat f in new[] { PixelFormat.Rgba16, PixelFormat.Rgba8Premultiplied })
        {
            Rgba32[] other = Image.FromPixels<Rgba32>(src, W, H).ConvertTo(f).ApplyMask(m, MaskChannel.Luminance).ConvertTo(PixelFormat.Rgba8).ToArray<Rgba32>();
            for (int i = 0; i < eight.Length; i++)
            {
                // Premultiplied storage loses color at low alpha, so compare alpha everywhere and color where the result is opaque enough.
                Assert.InRange(Math.Abs(other[i].A - eight[i].A), 0, 2);
                if (eight[i].A > 200)
                {
                    Assert.InRange(Math.Abs(other[i].R - eight[i].R), 0, 3);
                }
            }
        }
    }

    [Fact]
    public void GrayMasksAndBasesWork()
    {
        var g = Enumerable.Repeat(new Gray8(128), 64).ToArray();
        Image mask = Image.FromPixels<Gray8>(g, 8, 8);
        Image src = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(1, 2, 3, 255), 64).ToArray(), 8, 8);
        Rgba32[] got = src.ApplyMask(mask, MaskChannel.Luminance).ToArray<Rgba32>();
        Assert.All(got, p => Assert.InRange(p.A, 127, 129));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Image rgba = Image.FromPixels<Rgba32>(new Rgba32[4], 2, 2);
        Image rgb = Image.FromPixels<Rgb24>(new Rgb24[4], 2, 2);
        Assert.Throws<ArgumentNullException>(() => rgba.ApplyMask(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => rgba.ApplyMask(rgba, (MaskChannel)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => rgba.ApplyMask(rgba, x: int.MaxValue));
        Assert.Throws<ImageOperationException>(() => rgb.ApplyMask(rgba));
    }

    [Fact]
    public void OnlyTheTilesAMaskTouchesReadTheBase()
    {
        // A mask over one tile: the other tiles are transparent, whatever the base holds there.
        Rgba32[] src = Random(512, 512, 9);
        var mask = Enumerable.Repeat(new Rgba32(255, 255, 255, 255), 128 * 128).ToArray();
        Image masked = Image.FromPixels<Rgba32>(src, 512, 512).ApplyMask(Image.FromPixels<Rgba32>(mask, 128, 128), MaskChannel.Alpha, 128, 256);
        Rgba32[] got = masked.ToArray<Rgba32>();
        Rgba32 inside = src[(300 * 512) + 130];
        Assert.Equal(inside.A == 0 ? default : inside, got[(300 * 512) + 130]);
        Assert.Equal(default, got[(10 * 512) + 10]);
        Assert.Equal(default, got[(300 * 512) + 400]);
    }
}
