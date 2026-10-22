using ImageMagick;
using SkiaSharp;

namespace OneImage.Tests.Gif;

public class GifEncodeTests
{
    private static Rgba32[][] MovingSquare(int w, int h, int n, bool transparentBackground = false)
    {
        Rgba32[] bg = transparentBackground ? new Rgba32[w * h] : GifTestUtil.Pattern(w, h, 5, 60, false);
        var frames = new Rgba32[n][];
        for (int i = 0; i < n; i++)
        {
            Rgba32[] f = (Rgba32[])bg.Clone();
            int sx = 3 + (i * 4);
            for (int y = 10; y < 22; y++)
            {
                for (int x = sx; x < sx + 10 && x < w; x++)
                {
                    f[(y * w) + x] = new Rgba32((byte)(40 * (i % 5)), 200, (byte)(255 - (30 * (i % 7))), 255);
                }
            }

            frames[i] = f;
        }

        return frames;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(200, false)]
    [InlineData(255, false)]
    [InlineData(256, false)]
    [InlineData(40, true)]
    public void StillImageWithFewColorsRoundTripsExactly(int colors, bool holes)
    {
        Rgba32[] src = GifTestUtil.Pattern(67, 41, 3, colors, holes);
        byte[] gif = GifTestUtil.Encode(Image.FromPixels<Rgba32>(src, 67, 41));
        Assert.True(GifTestUtil.SameVisible(src, GifTestUtil.Pixels(GifTestUtil.Decode(gif))), "our decoder");

        // Magick.NET
        using var m = new MagickImage(gif);
        Rgba32[] viaMagick = ToRgba(m.GetPixels().ToByteArray(PixelMapping.RGBA)!);
        Assert.True(GifTestUtil.SameVisible(src, viaMagick), "Magick.NET");

        // SkiaSharp
        using SKBitmap bmp = SKBitmap.Decode(gif, new SKImageInfo(67, 41, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.True(GifTestUtil.SameVisible(src, ToRgba(bmp.GetPixelSpan().ToArray())), "SkiaSharp");
    }

    private static Rgba32[] ToRgba(byte[] raw)
    {
        var px = new Rgba32[raw.Length / 4];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32(raw[i * 4], raw[(i * 4) + 1], raw[(i * 4) + 2], raw[(i * 4) + 3]);
        }

        return px;
    }

    [Fact]
    public void AnimationRoundTripsFramesDelaysAndLoop()
    {
        Rgba32[][] src = MovingSquare(80, 40, 12);
        Image anim = GifTestUtil.Animation(80, 40, src, 70, 3);
        byte[] gif = GifTestUtil.Encode(anim);
        Image back = GifTestUtil.Decode(gif);
        Assert.Equal(12, back.FrameCount);
        Assert.Equal(3, back.LoopCount);
        for (int i = 0; i < 12; i++)
        {
            Assert.True(GifTestUtil.SameVisible(src[i], GifTestUtil.Pixels(back, i)), $"frame {i}");
            Assert.Equal(70 + (i * 10), back.Frames[i].Delay.TotalMilliseconds);
        }

        // An independent decoder agrees.
        var (refFrames, _, _, _, _) = MagickCorpus.Reference(gif);
        for (int i = 0; i < 12; i++)
        {
            Assert.True(GifTestUtil.SameVisible(src[i], ToRgba(refFrames[i])), $"magick frame {i}");
        }
    }

    [Fact]
    public void LoopCountForeverAndOnce()
    {
        Rgba32[][] src = MovingSquare(40, 30, 3);
        Assert.Equal(0, GifTestUtil.Decode(GifTestUtil.Encode(GifTestUtil.Animation(40, 30, src, 50, 0))).LoopCount);
        Assert.Equal(1, GifTestUtil.Decode(GifTestUtil.Encode(GifTestUtil.Animation(40, 30, src, 50, 1))).LoopCount);
    }

    [Fact]
    public void FrameDifferencingShrinksAMostlyStaticAnimation()
    {
        Rgba32[][] src = MovingSquare(160, 120, 20);
        Image anim = GifTestUtil.Animation(160, 120, src);
        byte[] diff = GifTestUtil.Encode(anim);
        byte[] full = GifTestUtil.Encode(anim, new GifEncoderOptions { FrameDifferencing = false });
        Assert.True(diff.Length * 3 < full.Length, $"diff {diff.Length} full {full.Length}");
        Image a = GifTestUtil.Decode(diff);
        Image b = GifTestUtil.Decode(full);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(GifTestUtil.Pixels(b, i), GifTestUtil.Pixels(a, i));
            Assert.True(GifTestUtil.SameVisible(src[i], GifTestUtil.Pixels(a, i)));
        }
    }

    [Fact]
    public void IdenticalFramesKeepTheirDelay()
    {
        Rgba32[] f = GifTestUtil.Pattern(30, 20, 1, 8, false);
        byte[] gif = GifTestUtil.Encode(GifTestUtil.Animation(30, 20, [f, f, f], 40));
        Image back = GifTestUtil.Decode(gif);
        Assert.Equal(3, back.FrameCount);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(f, GifTestUtil.Pixels(back, i));
        }
    }

    [Fact]
    public void TransparentAnimationRoundTrips()
    {
        Rgba32[][] src = MovingSquare(60, 40, 8, transparentBackground: true);
        byte[] gif = GifTestUtil.Encode(GifTestUtil.Animation(60, 40, src));
        Image back = GifTestUtil.Decode(gif);
        for (int i = 0; i < 8; i++)
        {
            Assert.True(GifTestUtil.SameVisible(src[i], GifTestUtil.Pixels(back, i)), $"frame {i}");
        }
    }

    [Fact]
    public void TransparencyAppearingLaterRestartsInSafeMode()
    {
        Rgba32[][] src = MovingSquare(60, 40, 6);
        Rgba32[] last = (Rgba32[])src[5].Clone();
        for (int i = 0; i < 300; i++)
        {
            last[i] = default;
        }

        src[5] = last;
        Image back = GifTestUtil.Decode(GifTestUtil.Encode(GifTestUtil.Animation(60, 40, src)));
        for (int i = 0; i < 6; i++)
        {
            Assert.True(GifTestUtil.SameVisible(src[i], GifTestUtil.Pixels(back, i)), $"frame {i}");
        }
    }

    [Fact]
    public void FramesOfDifferentSizesAreWrittenSelfContained()
    {
        Image a = Image.FromPixels<Rgba32>(GifTestUtil.Pattern(30, 20, 1, 8, false), 30, 20);
        Image b = Image.FromPixels<Rgba32>(GifTestUtil.Pattern(12, 9, 2, 5, false), 12, 9);
        var img = new Image([a.Frames[0].Node, b.Frames[0].Node], [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)], 0, ImageMetadata.Empty, null);
        Image back = GifTestUtil.Decode(GifTestUtil.Encode(img));
        Assert.Equal(30, back.Width);
        Rgba32[] f1 = GifTestUtil.Pixels(back, 1);
        Rgba32[] expect = GifTestUtil.Pixels(b);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                Rgba32 e = x < 12 && y < 9 ? expect[(y * 12) + x] : default;
                Assert.True(e.A == 0 ? f1[(y * 30) + x].A == 0 : f1[(y * 30) + x] == e, $"{x},{y}");
            }
        }
    }

    [Fact]
    public void ManyColorAnimationFallsBackToFramePalettes()
    {
        var frames = new List<Rgba32[]>();
        for (int i = 0; i < 4; i++)
        {
            frames.Add(GifTestUtil.Pattern(50, 40, 100 + i, 200, false)); // each exact, union far above 256
        }

        Image back = GifTestUtil.Decode(GifTestUtil.Encode(GifTestUtil.Animation(50, 40, frames)));
        for (int i = 0; i < 4; i++)
        {
            Assert.True(GifTestUtil.SameVisible(frames[i], GifTestUtil.Pixels(back, i)), $"frame {i}");
        }
    }

    [Fact]
    public void PhotoIsQuantizedWithReasonableQuality()
    {
        Rgba32[] src = GifTestUtil.SyntheticPhoto(200, 150);
        Image back = GifTestUtil.Decode(GifTestUtil.Encode(Image.FromPixels<Rgba32>(src, 200, 150)));
        Assert.True(GifTestUtil.Psnr(src, GifTestUtil.Pixels(back)) > 28);
    }

    [Fact]
    public void OutputIsIdenticalAcrossRunsAndDegrees()
    {
        Rgba32[][] src = MovingSquare(90, 60, 6);
        src[3] = GifTestUtil.SyntheticPhoto(90, 60, 3);
        Image anim = GifTestUtil.Animation(90, 60, src);
        byte[] a = GifTestUtil.Encode(anim);
        byte[] b = GifTestUtil.Encode(anim);
        using var ms = new MemoryStream();
        anim.Save(ms, GifFormat.Instance, new SaveOptions { MaxDegree = 1 });
        Assert.Equal(a, b);
        Assert.Equal(a, ms.ToArray());
    }

    [Fact]
    public void XmpPacketRoundTrips()
    {
        byte[] xmp = "<x:xmpmeta>hello \u00e9 world</x:xmpmeta>"u8.ToArray();
        Image img = Image.FromPixels<Rgba32>(GifTestUtil.Pattern(20, 20, 1, 5, false), 20, 20);
        var withXmp = new Image([img.Frames[0].Node], [TimeSpan.Zero], 1, new ImageMetadata { Xmp = xmp }, null);
        byte[] gif = GifTestUtil.Encode(withXmp);
        Assert.Equal(xmp, GifTestUtil.Decode(gif).Metadata.Xmp.ToArray());
        using var m = new MagickImage(gif); // other readers still parse the file
        Assert.Equal(20u, m.Width);
    }

    [Fact]
    public void MaxColorsIsHonored()
    {
        Rgba32[] src = GifTestUtil.SyntheticPhoto(80, 60);
        Image back = GifTestUtil.Decode(GifTestUtil.Encode(Image.FromPixels<Rgba32>(src, 80, 60), new GifEncoderOptions { MaxColors = 16, Dither = Dither.None }));
        Assert.True(GifTestUtil.Pixels(back).Distinct().Count() <= 16);
    }
}
