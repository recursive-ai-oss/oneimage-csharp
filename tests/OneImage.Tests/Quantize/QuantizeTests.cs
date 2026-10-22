using Rgba32Is = SixLabors.ImageSharp.PixelFormats.Rgba32;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using IS = SixLabors.ImageSharp;

namespace OneImage.Tests.Quantize;

public class QuantizeTests
{
    private static Image Photo(int w = 240, int h = 160) => Image.FromPixels<Rgba32>(Gif.GifTestUtil.SyntheticPhoto(w, h), w, h);

    [Fact]
    public void ImageWithFewColorsIsUnchanged()
    {
        Rgba32[] src = Gif.GifTestUtil.Pattern(50, 40, 2, 37, false);
        Image q = Image.FromPixels<Rgba32>(src, 50, 40).Quantize(64, Dither.FloydSteinberg);
        Assert.Equal(src, q.ToArray<Rgba32>());
    }

    [Theory]
    [InlineData(256)]
    [InlineData(64)]
    [InlineData(5)]
    [InlineData(2)]
    public void ResultHasAtMostTheRequestedColors(int colors)
    {
        foreach (Dither d in Enum.GetValues<Dither>())
        {
            Rgba32[] px = Photo().Quantize(colors, d).ToArray<Rgba32>();
            Assert.True(px.Distinct().Count() <= colors, $"{d}: {px.Distinct().Count()}");
        }
    }

    [Fact]
    public void OutputFormatFollowsInputAlpha()
    {
        Assert.Equal(PixelFormat.Rgba8, Photo().Quantize(16).Format);
        Assert.Equal(PixelFormat.Rgb8, Photo().ConvertTo(PixelFormat.Rgb8).Quantize(16).Format);
        Assert.Contains("quantize(colors=16, dither=FloydSteinberg, alpha=Full)", Photo().Quantize(16, Dither.FloydSteinberg).DescribeGraph());
    }

    [Fact]
    public void IsDeterministicAcrossRunsAndDegrees()
    {
        foreach (Dither d in Enum.GetValues<Dither>())
        {
            Rgba32[] a = Photo(300, 300).Quantize(32, d).ToArray<Rgba32>(maxDegree: 1);
            Rgba32[] b = Photo(300, 300).Quantize(32, d).ToArray<Rgba32>(maxDegree: 8);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void TilesComeOutRightWhicheverIsAskedFirst()
    {
        Image q = Photo(300, 260).Quantize(24, Dither.FloydSteinberg);
        Rgba32[] part = new Rgba32[40 * 40];
        q.CopyPixelsTo<Rgba32>(part, new ImageRect(250, 200, 40, 40));
        Rgba32[] all = q.ToArray<Rgba32>();
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Assert.Equal(all[((200 + y) * 300) + 250 + x], part[(y * 40) + x]);
            }
        }
    }

    [Fact]
    public void BinaryAlphaKeepsATransparentEntry()
    {
        Rgba32[] src = Gif.GifTestUtil.SyntheticPhoto(100, 80);
        for (int i = 0; i < src.Length; i += 7)
        {
            src[i] = new Rgba32(src[i].R, src[i].G, src[i].B, 40);
        }

        Rgba32[] q = Image.FromPixels<Rgba32>(src, 100, 80).Quantize(20, Dither.None, QuantizeAlpha.Binary).ToArray<Rgba32>();
        Assert.True(q.Distinct().Count() <= 20);
        for (int i = 0; i < src.Length; i++)
        {
            Assert.Equal(i % 7 == 0, q[i].A == 0);
        }
    }

    [Fact]
    public void FullAlphaKeepsGradientsOfOpacity()
    {
        var src = new Rgba32[64 * 8];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = new Rgba32(200, 30, 30, (byte)((i % 64) * 4));
        }

        Rgba32[] q = Image.FromPixels<Rgba32>(src, 64, 8).Quantize(8, Dither.None, QuantizeAlpha.Full).ToArray<Rgba32>();
        Assert.True(q.Select(p => p.A).Distinct().Count() > 4);
        Assert.True(Math.Abs(q[63].A - src[63].A) < 24);
    }

    [Fact]
    public void FloydSteinbergReducesBandingOnAGradient()
    {
        const int W = 512, H = 64;
        var src = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                byte v = (byte)(x * 255 / (W - 1));
                src[(y * W) + x] = new Rgba32(v, v, v, 255);
            }
        }

        Image g = Image.FromPixels<Rgba32>(src, W, H);
        double none = BlurredError(src, g.Quantize(6, Dither.None).ToArray<Rgba32>(), W, H);
        double fs = BlurredError(src, g.Quantize(6, Dither.FloydSteinberg).ToArray<Rgba32>(), W, H);
        double ordered = BlurredError(src, g.Quantize(6, Dither.Ordered).ToArray<Rgba32>(), W, H);

        // Banding shows up as a low-frequency error: compare after averaging 8x8 blocks.
        Assert.True(fs < none / 4, $"fs {fs} none {none}");
        Assert.True(ordered < none / 2, $"ordered {ordered} none {none}");
    }

    private static double BlurredError(Rgba32[] a, Rgba32[] b, int w, int h)
    {
        double sum = 0;
        int n = 0;
        for (int by = 0; by + 8 <= h; by += 8)
        {
            for (int bx = 0; bx + 8 <= w; bx += 8)
            {
                double sa = 0, sb = 0;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        sa += a[(((by + y) * w) + bx) + x].R;
                        sb += b[(((by + y) * w) + bx) + x].R;
                    }
                }

                double d = (sa - sb) / 64;
                sum += d * d;
                n++;
            }
        }

        return sum / n;
    }

    [Fact]
    public void QualityIsCloseToImageSharpWu()
    {
        Rgba32[] src = Gif.GifTestUtil.SyntheticPhoto(400, 300, 4);
        Rgba32[] ours = Image.FromPixels<Rgba32>(src, 400, 300).Quantize(256, Dither.None).ToArray<Rgba32>();
        double psnrOurs = Gif.GifTestUtil.Psnr(src, ours);

        var bytes = new byte[src.Length * 4];
        for (int i = 0; i < src.Length; i++)
        {
            bytes[i * 4] = src[i].R;
            bytes[(i * 4) + 1] = src[i].G;
            bytes[(i * 4) + 2] = src[i].B;
            bytes[(i * 4) + 3] = 255;
        }

        using IS.Image<Rgba32Is> sharp = IS.Image.LoadPixelData<Rgba32Is>(bytes, 400, 300);
        sharp.Mutate(x => x.Quantize(new WuQuantizer(new QuantizerOptions { MaxColors = 256, Dither = null })));
        var theirs = new Rgba32[src.Length];
        for (int y = 0; y < 300; y++)
        {
            for (int x = 0; x < 400; x++)
            {
                Rgba32Is p = sharp[x, y];
                theirs[(y * 400) + x] = new Rgba32(p.R, p.G, p.B, p.A);
            }
        }

        double psnrTheirs = Gif.GifTestUtil.Psnr(src, theirs);
        Assert.True(psnrOurs >= psnrTheirs - 0.5, $"ours {psnrOurs:F2} dB, ImageSharp Wu {psnrTheirs:F2} dB");
    }
}
