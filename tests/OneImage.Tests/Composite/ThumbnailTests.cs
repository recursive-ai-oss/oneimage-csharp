using OneImage.Codecs.Jpeg;
using OneImage.Tests.Jpeg;
using Xunit;

namespace OneImage.Tests.Composite;

public class ThumbnailTests
{
    private static byte[] Jpeg(Image image, int quality = 95) =>
        JpegEncodeTests.Encode(image, new JpegEncoderOptions { Quality = quality, Subsampling = JpegSubsampling.Yuv444 });

    private static Image Flat(int w, int h, Func<int, int, Rgb24> f)
    {
        var px = new Rgb24[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[(y * w) + x] = f(x, y);
            }
        }

        return Image.FromPixels<Rgb24>(px, w, h);
    }

    [Fact]
    public void CmykJpegThumbnailIsCorrectSrgb()
    {
        // The Adobe CMYK file at its own size: no resampling, so the thumbnail is exactly the color-managed conversion (which
        // the JPEG tests check against LittleCMS), and it carries the sRGB profile.
        byte[] file = JpegTestSupport.Read("cmyk");
        Image same = Image.Thumbnail(file, 67);
        Assert.Equal(PixelFormat.Rgb8, same.Format);
        Assert.Equal(Image.Load(file).ConvertColor(ColorProfile.Srgb).ToArray<Rgb24>(), same.ToArray<Rgb24>());
        Assert.True(same.Metadata.IccProfile.Span.SequenceEqual(ColorProfile.SrgbCompact.IccBytes.Span));

        // A smooth Adobe CMYK picture reduced by 4: the thumbnail matches converting at full size and then resizing.
        var cmyk = new Cmyk32[256 * 192];
        for (int y = 0; y < 192; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                cmyk[(y * 256) + x] = new Cmyk32((byte)(x * 255 / 255), (byte)(y * 255 / 191), (byte)(128 + (x / 4)), (byte)((x + y) / 8));
            }
        }

        byte[] big = JpegEncodeTests.Encode(Image.FromPixels<Cmyk32>(cmyk, 256, 192), new JpegEncoderOptions { Quality = 95, Subsampling = JpegSubsampling.Yuv444 });
        Image thumb = Image.Thumbnail(big, 64);
        Assert.Equal((64, 48), (thumb.Width, thumb.Height));
        Rgb24[] expected = Image.Load(big).ConvertColor(ColorProfile.Srgb).Resize(width: 64).ToArray<Rgb24>();
        Rgb24[] got = thumb.ToArray<Rgb24>();
        double sum = 0;
        int max = 0;
        for (int i = 0; i < got.Length; i++)
        {
            int d = Math.Max(Math.Abs(got[i].R - expected[i].R), Math.Max(Math.Abs(got[i].G - expected[i].G), Math.Abs(got[i].B - expected[i].B)));
            sum += d;
            max = Math.Max(max, d);
        }

        TestContext.Current.SendDiagnosticMessage($"CMYK thumbnail vs convert-then-resize: mean {sum / got.Length:F2}, max {max}");
        Assert.True(sum / got.Length < 2, $"mean {sum / got.Length}");

        // And the thumbnail survives a JPEG round trip carrying the sRGB profile.
        Image again = Image.Load(Jpeg(thumb));
        Assert.True(again.Metadata.IccProfile.Span.SequenceEqual(ColorProfile.SrgbCompact.IccBytes.Span));
    }

    private static Image WithExifOrientation6(out byte[] exif)
    {
        // 200 x 100 stored: left half red, right half blue. Orientation 6 means "rotate 90 clockwise to view", so upright it is
        // 100 x 200 with red on top and blue below.
        exif = MetadataTests.BuildExif(true, 6);
        Image raw = Flat(200, 100, (x, y) => x < 100 ? new Rgb24(220, 20, 20) : new Rgb24(20, 20, 220));
        return raw.WithMetadata(new ImageMetadata { Exif = exif, Orientation = 6 });
    }

    [Fact]
    public void RotatedExifThumbnailIsUprightAndHasNoGps()
    {
        Image src = WithExifOrientation6(out byte[] exif);
        byte[] file = Jpeg(src);
        Assert.NotNull(Image.Load(file, new LoadOptions { AutoOrient = false }).Metadata.ReadExif()!.Gps); // the input does carry GPS

        Image thumb = Image.Thumbnail(file, 50);
        Assert.Equal((50, 100), (thumb.Width, thumb.Height));
        Assert.Equal(1, thumb.Metadata.Orientation);
        Assert.True(thumb.Metadata.Exif.IsEmpty && thumb.Metadata.Xmp.IsEmpty && thumb.Metadata.Iptc.IsEmpty);
        Rgb24 top = thumb.ToArray<Rgb24>()[(10 * 50) + 25];
        Rgb24 bottom = thumb.ToArray<Rgb24>()[(90 * 50) + 25];
        Assert.True(top.R > 180 && top.B < 60, $"top {top}");
        Assert.True(bottom.B > 180 && bottom.R < 60, $"bottom {bottom}");

        byte[] outFile = Jpeg(thumb);
        Image reread = Image.Load(outFile, new LoadOptions { AutoOrient = false });
        Assert.True(reread.Metadata.Exif.IsEmpty);
        Assert.Equal(1, reread.Metadata.Orientation);
        Assert.Equal(-1, outFile.AsSpan().IndexOf("Exif\0\0"u8));
        Assert.Equal(-1, outFile.AsSpan().IndexOf("GPS"u8));
        Assert.Equal((50, 100), (reread.Width, reread.Height));
    }

    [Fact]
    public void KeepMetadataKeepsExifWithCorrectedTags()
    {
        Image src = WithExifOrientation6(out _);
        byte[] file = Jpeg(src);
        Image thumb = Image.Thumbnail(file, 50, keepMetadata: true);
        byte[] outFile = Jpeg(thumb);
        ExifData e = Image.Load(outFile, new LoadOptions { AutoOrient = false }).Metadata.ReadExif()!;
        Assert.Equal(1, e.Orientation);
        Assert.Equal(50, e.PixelWidth);
        Assert.Equal(100, e.PixelHeight);
        Assert.NotNull(e.Gps);
    }

    [Fact]
    public void DisplayP3JpegThumbnailIsConvertedToSrgb()
    {
        Rgb24[] colors = [new(200, 120, 90), new(90, 160, 120), new(150, 150, 60), new(110, 90, 170)];
        Image p3 = Flat(256, 256, (x, y) => colors[(x / 128) + (2 * (y / 128))]).AssignProfile(ColorProfile.DisplayP3);
        byte[] file = Jpeg(p3, 100);
        Image loaded = Image.Load(file);
        Assert.True(loaded.Metadata.IccProfile.Span.SequenceEqual(ColorProfile.DisplayP3.IccBytes.Span));

        Image thumb = Image.Thumbnail(file, 64);
        Assert.True(thumb.Metadata.IccProfile.Span.SequenceEqual(ColorProfile.SrgbCompact.IccBytes.Span));
        Rgb24[] got = thumb.ToArray<Rgb24>();
        byte[] px = colors.SelectMany(c => new[] { c.R, c.G, c.B }).ToArray();
        byte[] lcms = ColorTestUtil.Lcms(px, 4, 1, ColorModel.Rgb, ColorModel.Rgb, ColorProfile.DisplayP3.IccBytes.ToArray(), ColorProfile.Srgb.IccBytes.ToArray(), ImageMagick.RenderingIntent.Relative, true);
        int[] centers = [(16 * 64) + 16, (16 * 64) + 48, (48 * 64) + 16, (48 * 64) + 48];
        for (int i = 0; i < 4; i++)
        {
            Rgb24 g = got[centers[i]];
            // JPEG quantization of the P3 source adds a few LSB on top of the conversion.
            Assert.InRange(Math.Abs(g.R - lcms[3 * i]), 0, 4);
            Assert.InRange(Math.Abs(g.G - lcms[(3 * i) + 1]), 0, 4);
            Assert.InRange(Math.Abs(g.B - lcms[(3 * i) + 2]), 0, 4);
        }
    }

    [Fact]
    public void LargeJpegIsDecodedAtReducedSize()
    {
        byte[] file = Jpeg(Flat(2000, 1500, (x, y) => new Rgb24((byte)(x / 8), (byte)(y / 6), (byte)((x + y) / 14))), 85);
        Image thumb = Image.Thumbnail(file, 200);
        Assert.Equal((200, 150), (thumb.Width, thumb.Height));
        JpegDiagnostics.FullBlocks = 0;
        JpegDiagnostics.ReducedBlocks = 0;
        var buf = new Rgb24[200 * 150];
        thumb.CopyPixelsTo<Rgb24>(buf, new ImageRect(0, 0, 200, 150), maxDegree: 1);
        Assert.Equal(0, JpegDiagnostics.FullBlocks);
        Assert.True(JpegDiagnostics.ReducedBlocks > 0);
    }

    [Fact]
    public void OverloadsAgreeAndSizingRulesHold()
    {
        byte[] file = Jpeg(Flat(400, 200, (x, y) => new Rgb24((byte)x, (byte)y, 7)));
        string path = Path.Combine(AppContext.BaseDirectory, "thumb-test.jpg");
        File.WriteAllBytes(path, file);
        try
        {
            Rgb24[] a = Image.Thumbnail(path, 100).ToArray<Rgb24>();
            Rgb24[] b = Image.Thumbnail(file, 100).ToArray<Rgb24>();
            using var ms = new MemoryStream(file);
            Rgb24[] c = Image.Thumbnail(ms, 100).ToArray<Rgb24>();
            Rgb24[] d = Image.Thumbnail(Image.Load(file), 100).ToArray<Rgb24>();
            Assert.Equal(a, b);
            Assert.Equal(a, c);
            Assert.Equal(a, d);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal((100, 50), (Image.Thumbnail(file, 100).Width, Image.Thumbnail(file, 100).Height));
        Assert.Equal((400, 200), (Image.Thumbnail(file, 1000).Width, Image.Thumbnail(file, 1000).Height)); // no upscale by default
        Assert.Equal(1000, Image.Thumbnail(file, 1000, upscale: true).Width);
        Assert.Equal((60, 60), (Image.Thumbnail(file, 60, 60, Fit.Cover).Width, Image.Thumbnail(file, 60, 60, Fit.Cover).Height));
        Assert.Equal(50, Image.Thumbnail(file, null, 50).Height);
        Assert.Throws<ArgumentException>(() => Image.Thumbnail(file, null));
        Assert.Throws<ArgumentNullException>(() => Image.Thumbnail((Image)null!, 10));
    }

    [Fact]
    public void GrayStaysGrayAndAlphaSurvives()
    {
        Image g = Image.FromPixels<Gray8>(Enumerable.Repeat(new Gray8(90), 100 * 100).ToArray(), 100, 100);
        Image tg = Image.Thumbnail(g, 20);
        Assert.Equal(PixelFormat.Gray8, tg.Format);
        Image a = Image.FromPixels<Rgba32>(Enumerable.Repeat(new Rgba32(10, 200, 30, 128), 100 * 100).ToArray(), 100, 100);
        Image ta = Image.Thumbnail(a, 20);
        Assert.True(ta.Format.HasAlpha);
        Rgba32 p = ta.ToArray<Rgba32>()[210];
        Assert.InRange(p.A, 127, 129);
        Assert.InRange(p.G, 198, 202);
    }

    [Fact]
    public void ThumbnailSavesAsWebPAndOtherFormatsByExtension()
    {
        byte[] file = Jpeg(Flat(800, 600, (x, y) => new Rgb24((byte)(x / 4), (byte)(y / 3), 90)));
        string dir = Path.Combine(AppContext.BaseDirectory, "thumb-out");
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "in.jpg");
        File.WriteAllBytes(src, file);
        try
        {
            foreach (string ext in new[] { ".webp", ".png", ".jpg", ".tif" })
            {
                string dst = Path.Combine(dir, "t" + ext);
                Image.Thumbnail(src, 200).Save(dst);
                Image back = Image.Load(dst);
                Assert.Equal((200, 150), (back.Width, back.Height));
                Rgb24 p = back.ToArray<Rgb24>()[(75 * 200) + 100];
                Assert.InRange(p.R, 85, 160);
                Assert.InRange(p.G, 50, 105);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
