using System.Buffers.Binary;

namespace OneImage.Tests.Png;

public class PngEncodeTests
{
    private static readonly (int W, int H)[] Sizes = [(1, 1), (7, 5), (33, 17), (130, 129)];

    public static IEnumerable<TheoryDataRow<string>> Formats() => PngGen.AllFormats.Select(f => new TheoryDataRow<string>(f.ToString()));

    private static PixelFormat Parse(string s) => PngGen.AllFormats.First(f => f.ToString() == s);

    [Theory]
    [MemberData(nameof(Formats))]
    public void RoundTripsBitExactThroughOurDecoderAndLibpng(string name)
    {
        PixelFormat f = Parse(name);
        foreach ((int w, int h) in Sizes)
        {
            byte[] px = PngGen.Noise(w, h, f, w * 31 + h);
            byte[] png = PngTestUtil.Encode(px, w, h, f);
            Decoded back = PngTestUtil.Decode(png);
            ushort[] expected = Canon.ToRgba16(f, px, w * h);
            Assert.Equal(expected, back.ToRgba16());
            Assert.Equal(f.Sample, back.Format.Sample);
            (int vw, int vh, ushort[] vips) = PngTestUtil.NetVipsDecode(png);
            Assert.Equal((w, h), (vw, vh));
            Assert.Equal(expected, vips);
        }
    }

    [Theory]
    [InlineData(PngFilterMode.Adaptive)]
    [InlineData(PngFilterMode.None)]
    [InlineData(PngFilterMode.Sub)]
    [InlineData(PngFilterMode.Up)]
    [InlineData(PngFilterMode.Average)]
    [InlineData(PngFilterMode.Paeth)]
    public void EveryFilterAndLevelRoundTrips(PngFilterMode filter)
    {
        foreach (PixelFormat f in new[] { PixelFormat.Rgb8, PixelFormat.Rgba16, PixelFormat.Gray8, PixelFormat.GrayAlpha8 })
        {
            byte[] px = PngGen.Photo(91, 40, f, 5);
            foreach (int level in new[] { 0, 1, 6, 9 })
            {
                byte[] png = PngTestUtil.Encode(px, 91, 40, f, new PngEncoderOptions { Filter = filter, CompressionLevel = level, ColorMode = PngColorMode.Truecolor });
                Assert.Equal(px, PngTestUtil.Decode(png).Data);
                Assert.Equal(Canon.ToRgba16(f, px, 91 * 40), PngTestUtil.NetVipsDecode(png).Rgba);
            }
        }
    }

    [Fact]
    public void IndexedOutputForFewColorsIsLosslessAndSmallestDepth()
    {
        foreach ((int colors, int depth) in new[] { (2, 1), (3, 2), (4, 2), (5, 4), (16, 4), (17, 8), (256, 8) })
        {
            var rnd = new Random(colors);
            var palette = Enumerable.Range(0, colors).Select(i => new byte[] { (byte)(i * 37), (byte)(i * 91 + 3), (byte)(i * 5), 255 }).ToArray();
            int w = 61, h = 29;
            byte[] px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                // Cycle through every color first so all of them are present, then random.
                byte[] c = palette[i < colors ? i : rnd.Next(colors)];
                c.CopyTo(px, i * 4);
            }

            byte[] png = PngTestUtil.Encode(px, w, h, PixelFormat.Rgba8);
            Assert.Equal((3, depth), PngGen.Header(png));
            Assert.False(PngGen.Has(png, "tRNS"));
            Decoded back = PngTestUtil.Decode(png);
            Assert.Equal(PixelFormat.Rgb8, back.Format);
            Assert.Equal(Canon.ToRgba16(PixelFormat.Rgba8, px, w * h), back.ToRgba16());
            Assert.Equal(back.ToRgba16(), PngTestUtil.NetVipsDecode(png).Rgba);
        }
    }

    [Fact]
    public void IndexedWithAlphaWritesTrnsAndKeepsAlpha()
    {
        int w = 40, h = 10;
        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte a = (i % 3) switch { 0 => 255, 1 => 0, _ => 128 };
            new byte[] { (byte)(i % 5 * 50), (byte)(i % 7 * 30), 9, a }.CopyTo(px, i * 4);
        }

        byte[] png = PngTestUtil.Encode(px, w, h, PixelFormat.Rgba8);
        Assert.Equal(3, PngGen.Header(png).ColorType);
        Assert.True(PngGen.Has(png, "tRNS"));
        Decoded back = PngTestUtil.Decode(png);
        Assert.Equal(PixelFormat.Rgba8, back.Format);
        Assert.Equal(px, back.Data);
        Assert.Equal(Canon.ToRgba16(PixelFormat.Rgba8, px, w * h), PngTestUtil.NetVipsDecode(png).Rgba);
    }

    [Fact]
    public void ManyColorsStayTruecolorUnlessIndexedIsRequested()
    {
        byte[] px = PngGen.Noise(64, 64, PixelFormat.Rgb8, 3);
        byte[] png = PngTestUtil.Encode(px, 64, 64, PixelFormat.Rgb8);
        Assert.Equal((2, 8), PngGen.Header(png));
        // Truecolor mode never writes a palette even for two colors.
        byte[] two = new byte[16 * 16 * 3];
        byte[] png2 = PngTestUtil.Encode(two, 16, 16, PixelFormat.Rgb8, new PngEncoderOptions { ColorMode = PngColorMode.Truecolor });
        Assert.Equal((2, 8), PngGen.Header(png2));
    }

    [Fact]
    public void OutputIsIdenticalAcrossRunsAndStructurallyValid()
    {
        byte[] px = PngGen.Photo(300, 200, PixelFormat.Rgb8, 9);
        byte[] a = PngTestUtil.Encode(px, 300, 200, PixelFormat.Rgb8);
        byte[] b = PngTestUtil.Encode(px, 300, 200, PixelFormat.Rgb8);
        Assert.Equal(a, b);
        Assert.Equal("IHDR", PngGen.Chunks(a).First().Name);
        Assert.Equal("IEND", PngGen.Chunks(a).Last().Name);
    }

    [Fact]
    public void MetadataBlobsRoundTrip()
    {
        byte[] icc = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 % 251)).ToArray();
        byte[] exif = [.. "MM\0*\0\0\0\b\0\0\0\0\0\0"u8, .. Enumerable.Range(0, 100).Select(i => (byte)i)];
        byte[] xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf/></x:xmpmeta>"u8.ToArray();
        var meta = new ImageMetadata { IccProfile = icc, Exif = exif, Xmp = xmp };
        byte[] px = PngGen.Noise(20, 20, PixelFormat.Rgb8, 1);
        Image img = PngTestUtil.FromRaw(px, 20, 20, PixelFormat.Rgb8);
        Image withMeta = PngGen.Animation([img], [0], 0, meta);
        using var ms = new MemoryStream();
        withMeta.Save(ms, PngFormat.Instance);
        byte[] png = ms.ToArray();
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(png), ImageLimits.Default);
        Assert.Equal(icc, info.Metadata.IccProfile.ToArray());
        // The image pipeline rewrites the EXIF block on save (dimensions, orientation), so only its TIFF framing is stable.
        Assert.True(info.Metadata.Exif.Span.StartsWith("MM\0*"u8));
        Assert.Equal(xmp, info.Metadata.Xmp.ToArray());
        Assert.Equal(px, PngTestUtil.Decode(png).Data);
        // libpng must accept the file with the metadata chunks present.
        Assert.Equal(Canon.ToRgba16(PixelFormat.Rgb8, px, 400), PngTestUtil.NetVipsDecode(png).Rgba);
    }

    [Fact]
    public void ExifWithJpegStylePrefixIsStoredWithoutIt()
    {
        byte[] tiff = [.. "II*\0\b\0\0\0\0\0"u8];
        var meta = new ImageMetadata { Exif = (byte[])[.. "Exif\0\0"u8, .. tiff] };
        Image img = PngGen.Animation([PngTestUtil.FromRaw(new byte[12], 2, 2, PixelFormat.Rgb8)], [0], 0, meta);
        using var ms = new MemoryStream();
        img.Save(ms, PngFormat.Instance);
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(ms.ToArray()), ImageLimits.Default);
        Assert.True(info.Metadata.Exif.Span.StartsWith("II*\0"u8));
        Assert.False(info.Metadata.Exif.Span.StartsWith("Exif"u8));
    }

    [Fact]
    public void EncodedSizeIsCloseToLibpngDefault()
    {
        var report = new List<string>();
        var cases = new (string Name, byte[] Px, int W, int H, PixelFormat F)[]
        {
            ("photo", PngGen.Photo(1200, 800, PixelFormat.Rgb8, 4), 1200, 800, PixelFormat.Rgb8),
            ("screenshot", PngGen.Screenshot(1280, 720, 6), 1280, 720, PixelFormat.Rgba8),
        };
        foreach (var c in cases)
        {
            byte[] ours = PngTestUtil.Encode(c.Px, c.W, c.H, c.F);
            using var bmp = new SkiaSharp.SKBitmap(c.W, c.H, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
            byte[] rgba = new byte[c.W * c.H * 4];
            for (int i = 0; i < c.W * c.H; i++)
            {
                int bpp = c.F.BytesPerPixel;
                rgba[i * 4] = c.Px[i * bpp];
                rgba[(i * 4) + 1] = c.Px[(i * bpp) + 1];
                rgba[(i * 4) + 2] = c.Px[(i * bpp) + 2];
                rgba[(i * 4) + 3] = bpp == 4 ? c.Px[(i * bpp) + 3] : (byte)255;
            }

            System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
            using SkiaSharp.SKData data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            long skia = data.Size;
            report.Add($"{c.Name}: ours {ours.Length}, SkiaSharp (libpng default) {skia}, ratio {(double)ours.Length / skia:F3}");
            Assert.True(ours.Length <= skia * 1.05, report[^1]);
        }

        TestContext.Current.SendDiagnosticMessage(string.Join("; ", report));
        Console.WriteLine(string.Join("\n", report));
    }
}
