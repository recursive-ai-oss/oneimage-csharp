using Xunit;

namespace OneImage.Tests.Geometry;

public class TrimTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);

    private static Rgba32[] Canvas(int w, int h, Rgba32 fill)
    {
        var px = new Rgba32[w * h];
        Array.Fill(px, fill);
        return px;
    }

    private static void Rect(Rgba32[] px, int w, int x0, int y0, int x1, int y1, Rgba32 c)
    {
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                px[(y * w) + x] = c;
            }
        }
    }

    [Fact]
    public void FindsTheBoxOfTheContent()
    {
        Rgba32[] px = Canvas(300, 200, White);
        Rect(px, 300, 40, 25, 211, 143, new Rgba32(10, 20, 30, 255));
        Image t = Image.FromPixels<Rgba32>(px, 300, 200).Trim();
        Assert.Equal((171, 118), (t.Width, t.Height));
        Rgba32[] got = t.ToArray<Rgba32>();
        Assert.All(got, p => Assert.Equal(new Rgba32(10, 20, 30, 255), p));
    }

    [Fact]
    public void ThresholdDecidesWhatCountsAsBorder()
    {
        Rgba32[] px = Canvas(100, 100, White);
        Rect(px, 100, 30, 30, 70, 70, new Rgba32(0, 0, 0, 255));
        // Noise in the border: within 4 of white everywhere, plus one faint speck 9 away at (10, 90).
        var rnd = new Random(1);
        for (int i = 0; i < px.Length; i++)
        {
            if (px[i] == White)
            {
                int n = rnd.Next(5);
                px[i] = new Rgba32((byte)(255 - n), (byte)(255 - n), 255, 255);
            }
        }

        px[(90 * 100) + 10] = new Rgba32(246, 255, 255, 255);
        Image img = Image.FromPixels<Rgba32>(px, 100, 100);
        Assert.Equal((100, 100), (img.Trim(0, White).Width, img.Trim(0, White).Height));
        Assert.Equal((40, 40), (img.Trim(10, White).Width, img.Trim(10, White).Height));
        Image mid = img.Trim(5, White);
        Assert.Equal((60, 61), (mid.Width, mid.Height));
        Image none = img.Trim(8, White);
        Assert.Equal((60, 61), (none.Width, none.Height));
        Image nine = img.Trim(9, White);
        Assert.Equal((40, 40), (nine.Width, nine.Height));
    }

    [Fact]
    public void UsesTheTopLeftPixelUnlessToldOtherwiseAndHandlesTransparency()
    {
        Rgba32[] px = Canvas(50, 50, new Rgba32(1, 2, 3, 0));
        Rect(px, 50, 10, 20, 15, 22, new Rgba32(200, 0, 0, 255));
        Image img = Image.FromPixels<Rgba32>(px, 50, 50);
        Assert.Equal((5, 2), (img.Trim().Width, img.Trim().Height));
        Assert.Equal((5, 2), (img.Trim(0, new Rgba32(9, 9, 9, 0)).Width, img.Trim(0, new Rgba32(9, 9, 9, 0)).Height));
        // A white border is not the border of a transparent image.
        Assert.Equal((50, 50), (img.Trim(10, White).Width, img.Trim(10, White).Height));
    }

    [Fact]
    public void UniformImageTrimsToOnePixel()
    {
        Image t = Image.FromPixels<Rgba32>(Canvas(30, 30, White), 30, 30).Trim();
        Assert.Equal((1, 1), (t.Width, t.Height));
    }

    [Fact]
    public void WorksOnGrayAndAnimations()
    {
        Gray8[] g = new Gray8[40 * 40];
        Array.Fill(g, new Gray8(255));
        g[(5 * 40) + 6] = new Gray8(0);
        g[(9 * 40) + 20] = new Gray8(0);
        Image t = Image.FromPixels<Gray8>(g, 40, 40).Trim();
        Assert.Equal((15, 5), (t.Width, t.Height));
        Assert.Equal(PixelFormat.Gray8, t.Format);

        Rgba32[] a = Canvas(60, 60, White);
        Rect(a, 60, 10, 10, 20, 20, new Rgba32(0, 0, 0, 255));
        Rgba32[] b = Canvas(60, 60, White);
        Rect(b, 60, 30, 40, 50, 45, new Rgba32(0, 0, 0, 255));
        var anim = new Image([Image.FromPixels<Rgba32>(a, 60, 60).Frames[0].Node, Image.FromPixels<Rgba32>(b, 60, 60).Frames[0].Node], [TimeSpan.Zero, TimeSpan.Zero], 0, ImageMetadata.Empty, null);
        Image r = anim.Trim();
        Assert.Equal(2, r.FrameCount);
        Assert.Equal((40, 35), (r.Frames[0].Width, r.Frames[0].Height));
    }

    [Fact]
    public void RejectsBadArgumentsAndColorModels()
    {
        Image img = OrientTests.Make(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Trim(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Trim(256));
        var cmyk = Image.FromNode(new Resample.RawBytesNode(new byte[16 * 4], 4, 4, PixelFormat.Cmyk8));
        Assert.Throws<ImageOperationException>(() => cmyk.Trim());
    }
}
