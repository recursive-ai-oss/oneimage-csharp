namespace OneImage.Tests.Jpeg;

public class JpegColorTests
{
    /// <summary>
    /// An Adobe CMYK/YCCK JPEG decoded and converted to sRGB with the generic CMYK profile agrees with LittleCMS converting the
    /// same decoded CMYK samples with the same two profiles.
    /// </summary>
    [Theory]
    [InlineData("cmyk")]
    [InlineData("ycck")]
    public void CmykJpegToSrgbMatchesLittleCms(string name)
    {
        Image img = Image.Load(JpegTestSupport.Read(name));
        Assert.Equal(PixelFormat.Cmyk8, img.Format);
        Assert.Equal(ColorProfile.GenericCmyk, img.ColorProfile);
        int w = img.Width, h = img.Height;
        Cmyk32[] px = img.ToArray<Cmyk32>();
        byte[] cmyk = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++)
        {
            (cmyk[i * 4], cmyk[(i * 4) + 1], cmyk[(i * 4) + 2], cmyk[(i * 4) + 3]) = (px[i].C, px[i].M, px[i].Y, px[i].K);
        }

        Rgb24[] ours = img.ConvertColor(ColorProfile.Srgb).ToArray<Rgb24>();
        byte[] theirs = ColorTestUtil.Lcms(cmyk, w, h, ColorModel.Cmyk, ColorModel.Rgb,
            ColorProfile.GenericCmyk.IccBytes.ToArray(), ColorProfile.Srgb.IccBytes.ToArray(),
            ImageMagick.RenderingIntent.Relative, true);

        byte[] oursBytes = new byte[w * h * 3];
        for (int i = 0; i < ours.Length; i++)
        {
            oursBytes[(i * 3)] = ours[i].R;
            oursBytes[(i * 3) + 1] = ours[i].G;
            oursBytes[(i * 3) + 2] = ours[i].B;
        }

        float[] la = ColorTestUtil.ToLab(oursBytes, 3, ColorProfile.Srgb);
        float[] lb = ColorTestUtil.ToLab(theirs, 3, ColorProfile.Srgb);
        double max = 0, sum = 0;
        int lsb = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            double e = ColorTestUtil.DeltaE2000(la.AsSpan(i * 3, 3), lb.AsSpan(i * 3, 3));
            max = Math.Max(max, e);
            sum += e;
            for (int c = 0; c < 3; c++)
            {
                lsb = Math.Max(lsb, Math.Abs(oursBytes[(i * 3) + c] - theirs[(i * 3) + c]));
            }
        }

        TestContext.Current.SendDiagnosticMessage($"{name}: CMYK->sRGB vs LittleCMS max dE00 {max:F3}, mean {sum / ours.Length:F3}, max LSB {lsb}");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "jpeg-accuracy.txt"), $"{name}-to-srgb-vs-lcms dE00 max={max:F3} mean={sum / ours.Length:F3} lsb={lsb}\n");
        Assert.True(sum / ours.Length < 0.5, $"mean dE00 {sum / ours.Length}");
        Assert.True(max < 2.5, $"max dE00 {max}");
    }

    [Fact]
    public void ExifIsCorrectedOnWriteAndOrientationIsRead()
    {
        // Minimal little-endian TIFF with Orientation=6 and ImageWidth/ImageLength = 1 x 1 (to be corrected).
        byte[] exif =
        [
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
            3, 0,
            0x00, 0x01, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0,
            0x01, 0x01, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0,
            0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0,
            0, 0, 0, 0,
        ];
        using var canvas = new Canvas(20, 10, PixelFormat.Rgb8);
        Image src = canvas.Snapshot();
        var meta = new ImageMetadata { Exif = exif, Orientation = 6 };
        Image back = Image.Load(JpegEncodeTests.Encode(src.WithMetadata(meta), new JpegEncoderOptions()), new LoadOptions { AutoOrient = false });
        Assert.Equal(6, back.Metadata.Orientation);
        ExifData e = back.Metadata.ReadExif()!;
        Assert.Equal(6, e.Orientation);
        Assert.Equal(20, e.PixelWidth);
        Assert.Equal(10, e.PixelHeight);
    }
}

public class JpegTwoPassTests
{
    /// <summary>Very large images are transformed twice instead of stored; the file must be the same bytes.</summary>
    [Theory]
    [InlineData("base-420")]
    [InlineData("base-444")]
    public void TwoPassOptimizedEqualsStoredOptimized(string name)
    {
        Image img = Image.Load(JpegTestSupport.Read(name));
        var o = new JpegEncoderOptions { Quality = 85, RestartInterval = 3 };
        byte[] stored = JpegEncodeTests.Encode(img, o);
        byte[] twoPass;
        try
        {
            OneImage.Codecs.Jpeg.JpegEncodeSession.StoredPixelLimitForTests = 1;
            twoPass = JpegEncodeTests.Encode(img, o);
        }
        finally
        {
            OneImage.Codecs.Jpeg.JpegEncodeSession.StoredPixelLimitForTests = 0;
        }

        Assert.True(stored.AsSpan().SequenceEqual(twoPass));
    }
}
