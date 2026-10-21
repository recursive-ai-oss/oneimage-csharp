using Xunit;

namespace OneImage.Tests.Geometry;

public class PadTests
{
    private static Image Row(params byte[] values) =>
        Image.FromPixels<Gray8>(values.Select(v => new Gray8(v)).ToArray(), values.Length, 1);

    private static byte[] Bytes(Image img) => img.ToArray<Gray8>().Select(p => p.V).ToArray();

    [Fact]
    public void EdgeModesAreExactOnASmallRow()
    {
        Image abc = Row(1, 2, 3);
        Assert.Equal(new byte[] { 0, 0, 1, 2, 3, 0, 0, 0 }, Bytes(abc.Pad(2, 0, 3)));
        Assert.Equal(new byte[] { 1, 1, 1, 2, 3, 3, 3, 3 }, Bytes(abc.Pad(2, 0, 3, mode: EdgeMode.Clamp)));
        Assert.Equal(new byte[] { 2, 1, 1, 2, 3, 3, 2, 1 }, Bytes(abc.Pad(2, 0, 3, mode: EdgeMode.Mirror)));
        Assert.Equal(new byte[] { 2, 3, 1, 2, 3, 1, 2, 3 }, Bytes(abc.Pad(2, 0, 3, mode: EdgeMode.Wrap)));
    }

    [Fact]
    public void BordersLargerThanTheImageKeepFollowingTheMode()
    {
        Image abc = Row(1, 2, 3);
        Assert.Equal(new byte[] { 3, 2, 1, 1, 2, 3, 3, 2, 1, 1 }, Bytes(abc.Pad(3, 0, 4, mode: EdgeMode.Mirror)));
        Assert.Equal(new byte[] { 1, 2, 3, 1, 2, 3, 1, 2, 3, 1, 2 }, Bytes(abc.Pad(3, 0, 5, mode: EdgeMode.Wrap)));
        Assert.Equal(new byte[] { 1, 1, 1, 1, 1, 2, 3, 3, 3, 3, 3 }, Bytes(abc.Pad(4, 0, 4, mode: EdgeMode.Clamp)));
    }

    [Fact]
    public void ConstantUsesTheBackgroundConvertedToTheFormat()
    {
        Rgba32[] px = Image.FromPixels<Rgba32>(new[] { new Rgba32(1, 2, 3, 255) }, 1, 1).Pad(1, 1, 1, 1, background: new Rgba32(9, 8, 7, 200)).ToArray<Rgba32>();
        Assert.Equal(9, px.Length);
        Assert.Equal(new Rgba32(9, 8, 7, 200), px[0]);
        Assert.Equal(new Rgba32(1, 2, 3, 255), px[4]);
        Gray8[] gray = Image.FromPixels<Gray8>(new[] { new Gray8(50) }, 1, 1).Pad(1, 0, 0, 0, background: new Rgba32(255, 255, 255, 255)).ToArray<Gray8>();
        Assert.Equal(255, gray[0].V);
    }

    [Theory]
    [InlineData(EdgeMode.Constant)]
    [InlineData(EdgeMode.Clamp)]
    [InlineData(EdgeMode.Mirror)]
    [InlineData(EdgeMode.Wrap)]
    public void MultiTilePaddingMatchesAPerPixelReference(EdgeMode mode)
    {
        const int W = 150;
        const int H = 133;
        Rgba32 bg = new(5, 6, 7, 255);
        Image padded = OrientTests.Make(W, H).Pad(70, 5, 200, 140, mode, bg);
        Assert.Equal((W + 270, H + 145), (padded.Width, padded.Height));
        Rgba32[] got = padded.ToArray<Rgba32>();
        for (int y = 0; y < padded.Height; y++)
        {
            int sy = Ref(y - 5, H, mode);
            for (int x = 0; x < padded.Width; x++)
            {
                int sx = Ref(x - 70, W, mode);
                Rgba32 want = sx < 0 || sy < 0 ? bg : OrientTests.Px(sx, sy);
                Assert.Equal(want, got[(y * padded.Width) + x]);
            }
        }
    }

    // Written separately from the library: reflection by walking, wrap by repeated shifts.
    private static int Ref(int i, int n, EdgeMode mode)
    {
        if (i >= 0 && i < n)
        {
            return i;
        }

        switch (mode)
        {
            case EdgeMode.Clamp:
                return Math.Clamp(i, 0, n - 1);
            case EdgeMode.Wrap:
                while (i < 0)
                {
                    i += n;
                }

                while (i >= n)
                {
                    i -= n;
                }

                return i;
            case EdgeMode.Mirror:
                while (i < 0 || i >= n)
                {
                    i = i < 0 ? -i - 1 : (2 * n) - 1 - i;
                }

                return i;
            default:
                return -1;
        }
    }

    [Fact]
    public void ZeroPaddingIsTheSameImageAndBadArgumentsThrow()
    {
        Image img = OrientTests.Make(8, 8);
        Assert.Same(img.Frames[0].Node, img.Pad().Frames[0].Node);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Pad(left: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Pad(1, mode: (EdgeMode)7));
    }

    [Fact]
    public void ResizePadStillPlacesTheImage()
    {
        Rgba32[] px = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(200, 10, 10, 255), 60 * 120).ToArray(), 60, 120).Resize(180, 120, Fit.Pad, Anchor.Center, background: new Rgba32(0, 0, 255, 255)).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[0]);
        Assert.Equal(new Rgba32(200, 10, 10, 255), px[90]);
    }
}
