using OneImage.Kernels;
using Xunit;

namespace OneImage.Tests.Geometry;

public class OrientTests
{
    internal static Rgba32 Px(int x, int y) => new((byte)((x * 7) + y), (byte)((y * 13) + (x >> 3)), (byte)(x ^ (y * 3)), (byte)(255 - ((x + (2 * y)) % 61)));

    internal static Image Make(int w, int h)
    {
        var px = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[(y * w) + x] = Px(x, y);
            }
        }

        return Image.FromPixels<Rgba32>(px, w, h);
    }

    // Independent reference: where each output pixel comes from, written out per orientation.
    private static (int W, int H, Func<int, int, (int X, int Y)> Source) Reference(int orientation, int w, int h) => orientation switch
    {
        1 => (w, h, (x, y) => (x, y)),
        2 => (w, h, (x, y) => (w - 1 - x, y)),
        3 => (w, h, (x, y) => (w - 1 - x, h - 1 - y)),
        4 => (w, h, (x, y) => (x, h - 1 - y)),
        5 => (h, w, (x, y) => (y, x)),
        6 => (h, w, (x, y) => (y, h - 1 - x)),
        7 => (h, w, (x, y) => (w - 1 - y, h - 1 - x)),
        _ => (h, w, (x, y) => (w - 1 - y, x)),
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void AllEightOrientationsMoveEveryPixelCorrectly(int orientation)
    {
        // 301 x 170: neither side is a multiple of the tile size, and the sides differ.
        const int W = 301;
        const int H = 170;
        (int rw, int rh, Func<int, int, (int X, int Y)> src) = Reference(orientation, W, H);
        Image r = Make(W, H).Orient(orientation);
        Assert.Equal((rw, rh), (r.Width, r.Height));
        Rgba32[] got = r.ToArray<Rgba32>();
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                (int sx, int sy) = src(x, y);
                Assert.Equal(Px(sx, sy), got[(y * rw) + x]);
            }
        }
    }

    [Fact]
    public void ExifOrientationSixShowsTheTopLeftPixelAtTheTopRight()
    {
        // An image stored with tag 6 is rotated 90 degrees clockwise to be upright: its stored top-left ends up top-right.
        Rgba32[] got = Make(40, 30).Orient(6).ToArray<Rgba32>();
        Assert.Equal(Px(0, 0), got[29]);
        Assert.Equal(Px(39, 0), got[(39 * 30) + 29]);
    }

    [Fact]
    public void RotateAndFlipAreTheNamedOrientations()
    {
        Image img = Make(150, 140);
        Assert.Equal(img.Orient(6).ToArray<Rgba32>(), img.Rotate(90).ToArray<Rgba32>());
        Assert.Equal(img.Orient(3).ToArray<Rgba32>(), img.Rotate(180).ToArray<Rgba32>());
        Assert.Equal(img.Orient(8).ToArray<Rgba32>(), img.Rotate(270).ToArray<Rgba32>());
        Assert.Equal(img.Orient(8).ToArray<Rgba32>(), img.Rotate(-90).ToArray<Rgba32>());
        Assert.Equal(img.Orient(6).ToArray<Rgba32>(), img.Rotate(450).ToArray<Rgba32>());
        Assert.Equal(img.Orient(2).ToArray<Rgba32>(), img.Flip(FlipAxis.Horizontal).ToArray<Rgba32>());
        Assert.Equal(img.Orient(4).ToArray<Rgba32>(), img.Flip(FlipAxis.Vertical).ToArray<Rgba32>());
        Assert.Same(img, img.Rotate(0));
        Assert.Same(img, img.Rotate(360));
    }

    [Fact]
    public void RotationsAndFlipsCompose()
    {
        Image img = Make(200, 131);
        Rgba32[] original = img.ToArray<Rgba32>();
        Assert.Equal(original, img.Rotate(90).Rotate(90).Rotate(90).Rotate(90).ToArray<Rgba32>());
        Assert.Equal(original, img.Rotate(90).Rotate(270).ToArray<Rgba32>());
        Assert.Equal(original, img.Flip(FlipAxis.Horizontal).Flip(FlipAxis.Horizontal).ToArray<Rgba32>());
        Assert.Equal(original, img.Flip(FlipAxis.Vertical).Flip(FlipAxis.Vertical).ToArray<Rgba32>());
        // Two flips are a half turn; a turn then a flip is a transpose-type orientation.
        Assert.Equal(img.Rotate(180).ToArray<Rgba32>(), img.Flip(FlipAxis.Horizontal).Flip(FlipAxis.Vertical).ToArray<Rgba32>());
        Assert.Equal(img.Orient(5).ToArray<Rgba32>(), img.Rotate(90).Flip(FlipAxis.Horizontal).ToArray<Rgba32>());
        Assert.Equal(img.Orient(7).ToArray<Rgba32>(), img.Rotate(90).Flip(FlipAxis.Vertical).ToArray<Rgba32>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(16)]
    public void EveryPixelSizeMoves(int bytesPerPixel)
    {
        PixelFormat fmt = bytesPerPixel switch
        {
            1 => PixelFormat.Gray8,
            2 => PixelFormat.Gray16,
            3 => PixelFormat.Rgb8,
            6 => PixelFormat.Rgb16,
            12 => PixelFormat.RgbF32,
            _ => PixelFormat.RgbaF32,
        };
        Assert.Equal(bytesPerPixel, fmt.BytesPerPixel);
        const int W = 137;
        const int H = 150;
        byte[] raw = new byte[W * H * bytesPerPixel];
        new Random(bytesPerPixel).NextBytes(raw);
        if (fmt.Sample == SampleType.F32)
        {
            // Keep float samples finite so the bytes survive any conversion path untouched.
            for (int i = 0; i < raw.Length; i += 4)
            {
                raw[i + 3] = 0x3f;
            }
        }

        Image img = Resample.ResampleAccuracyTests.ImageOf(raw, W, H, fmt);
        byte[] got = Resample.ResampleAccuracyTests.ToBytes(img.Orient(8), fmt);
        (_, _, Func<int, int, (int X, int Y)> src) = Reference(8, W, H);
        for (int y = 0; y < W; y++)
        {
            for (int x = 0; x < H; x++)
            {
                (int sx, int sy) = src(x, y);
                Assert.True(raw.AsSpan(((sy * W) + sx) * bytesPerPixel, bytesPerPixel).SequenceEqual(got.AsSpan(((y * H) + x) * bytesPerPixel, bytesPerPixel)));
            }
        }
    }

    [Fact]
    public void BadOrientationThrows()
    {
        Image img = Make(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Orient(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Orient(9));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Flip((FlipAxis)5));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Rotate(double.NaN));
    }

    [Fact]
    public void AutoOrientAppliesTheTagOnceAndResetsIt()
    {
        Image plain = Make(60, 40);
        var tagged = new Image([plain.Frames[0].Node], [TimeSpan.Zero], 0, new ImageMetadata { Orientation = 6, Exif = new byte[] { 1, 2, 3 } }, null);
        Image up = tagged.AutoOrient();
        Assert.Equal((40, 60), (up.Width, up.Height));
        Assert.Equal(1, up.Metadata.Orientation);
        Assert.Equal(new byte[] { 1, 2, 3 }, up.Metadata.Exif.ToArray());
        Assert.Equal(plain.Orient(6).ToArray<Rgba32>(), up.ToArray<Rgba32>());
        Assert.Same(up, up.AutoOrient());
        Assert.Same(plain, plain.AutoOrient());
    }

    [Fact]
    public void AnimationsMapOverEveryFrame()
    {
        Image a = Make(50, 30);
        Image b = Make(50, 30).Invert();
        var anim = new Image([a.Frames[0].Node, b.Frames[0].Node], [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)], 3, ImageMetadata.Empty, null);
        Image r = anim.Rotate(90).Pad(2, 2, 2, 2).Flip(FlipAxis.Horizontal);
        Assert.Equal(2, r.FrameCount);
        Assert.Equal(3, r.LoopCount);
        Assert.Equal(TimeSpan.FromMilliseconds(20), r.Frames[1].Delay);
        Assert.Equal((34, 54), (r.Frames[1].Width, r.Frames[1].Height));
        Rgba32[] second = r.Frames[1].ToArray<Rgba32>();
        Rgba32[] expected = b.Rotate(90).Pad(2, 2, 2, 2).Flip(FlipAxis.Horizontal).ToArray<Rgba32>();
        Assert.Equal(expected, second);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(128)]
    public void ReversedFourByteGatherMatchesAScalarLoop(int count)
    {
        byte[] src = new byte[(count * 4) + 12];
        new Random(count).NextBytes(src);
        byte[] got = new byte[count * 4];
        long first = (count * 4L) + 4;
        GeometryKernel.GatherRow(src, first, -4, got, count, 4);
        for (int i = 0; i < count; i++)
        {
            Assert.True(src.AsSpan((int)first - (i * 4), 4).SequenceEqual(got.AsSpan(i * 4, 4)));
        }
    }

    [Fact]
    public void KernelRejectsReadsOutsideItsBuffers()
    {
        Assert.Throws<ArgumentException>(() => GeometryKernel.GatherRow(new byte[16], 8, 4, new byte[32], 4, 4));
        Assert.Throws<ArgumentException>(() => GeometryKernel.GatherRow(new byte[16], 0, 4, new byte[8], 4, 4));
        Assert.Throws<ArgumentException>(() => GeometryKernel.GatherRow(new byte[16], 12, -8, new byte[16], 3, 4));
    }
}
