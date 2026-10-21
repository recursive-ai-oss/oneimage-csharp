namespace OneImage.Tests.Jpeg;

public class JpegEncodeMoreTests
{
    private static void Seg(MemoryStream ms, byte marker, ReadOnlySpan<byte> payload)
    {
        ms.WriteByte(0xFF);
        ms.WriteByte(marker);
        ms.WriteByte((byte)((payload.Length + 2) >> 8));
        ms.WriteByte((byte)(payload.Length + 2));
        ms.Write(payload);
    }

    private static byte[] Blob(int length, int seed)
    {
        byte[] b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Builds a JPEG whose metadata segments are in the awkward spots: multi-chunk ICC, EXIF, XMP and Photoshop-wrapped IPTC.</summary>
    private static (byte[] File, byte[] Icc, byte[] Exif, byte[] Xmp, byte[] Iptc) WithMetadata()
    {
        byte[] baseFile = JpegTestSupport.Read("base-420");
        byte[] icc = Blob(150_001, 1);
        byte[] exif = Blob(300, 2);
        byte[] xmp = Blob(1000, 3);
        byte[] iptc = Blob(101, 4);
        var ms = new MemoryStream();
        ms.Write(baseFile.AsSpan(0, 2));
        Seg(ms, 0xE1, [.. "Exif\0\0"u8, .. exif]);
        Seg(ms, 0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. xmp]);
        const int chunk = 65519;
        int count = (icc.Length + chunk - 1) / chunk;
        // Chunks are deliberately written out of order.
        foreach (int i in Enumerable.Range(0, count).Reverse())
        {
            byte[] part = icc.AsSpan(i * chunk, Math.Min(chunk, icc.Length - (i * chunk))).ToArray();
            Seg(ms, 0xE2, [.. "ICC_PROFILE\0"u8, (byte)(i + 1), (byte)count, .. part]);
        }

        byte[] ps = [.. "Photoshop 3.0\0"u8, .. "8BIM"u8, 0x04, 0x04, 0, 0, 0, 0, 0, (byte)iptc.Length, .. iptc, 0];
        Seg(ms, 0xED, ps);
        ms.Write(baseFile.AsSpan(2));
        return (ms.ToArray(), icc, exif, xmp, iptc);
    }

    [Fact]
    public void MetadataIsReadAndSurvivesASave()
    {
        (byte[] file, byte[] icc, byte[] exif, byte[] xmp, byte[] iptc) = WithMetadata();
        Image img = Image.Load(file);
        Assert.True(icc.AsSpan().SequenceEqual(img.Metadata.IccProfile.Span));
        Assert.True(exif.AsSpan().SequenceEqual(img.Metadata.Exif.Span));
        Assert.True(xmp.AsSpan().SequenceEqual(img.Metadata.Xmp.Span));
        Assert.True(iptc.AsSpan().SequenceEqual(img.Metadata.Iptc.Span));

        byte[] saved = JpegEncodeTests.Encode(img, new JpegEncoderOptions());
        Image again = Image.Load(saved);
        Assert.True(icc.AsSpan().SequenceEqual(again.Metadata.IccProfile.Span));
        Assert.True(exif.AsSpan().SequenceEqual(again.Metadata.Exif.Span));
        Assert.True(xmp.AsSpan().SequenceEqual(again.Metadata.Xmp.Span));
        Assert.True(iptc.AsSpan().SequenceEqual(again.Metadata.Iptc.Span));
    }

    [Fact]
    public void BrokenIccChunkSetIsIgnoredNotFatal()
    {
        byte[] baseFile = JpegTestSupport.Read("base-420");
        var ms = new MemoryStream();
        ms.Write(baseFile.AsSpan(0, 2));
        Seg(ms, 0xE2, [.. "ICC_PROFILE\0"u8, 1, 3, 1, 2, 3]);
        Seg(ms, 0xE2, [.. "ICC_PROFILE\0"u8, 3, 3, 4, 5, 6]);
        ms.Write(baseFile.AsSpan(2));
        Image img = Image.Load(ms.ToArray());
        Assert.Equal(0, img.Metadata.IccProfile.Length);
        Assert.Equal(67, img.Width);
    }

    [Theory]
    [InlineData("cmyk")]
    [InlineData("ycck")]
    public void CmykRoundTripKeepsInkValues(string name)
    {
        byte[] src = JpegTestSupport.Read(name);
        Image img = Image.Load(src);
        Assert.Equal(PixelFormat.Cmyk8, img.Frames[0].Node.Format);
        byte[] saved = JpegEncodeTests.Encode(img, new JpegEncoderOptions { Quality = 100 });
        (_, byte[] a, _) = JpegTestSupport.Decode(src);
        (ImageInfo info, byte[] b, PixelFormat fmt) = JpegTestSupport.Decode(saved);
        Assert.Equal(PixelFormat.Cmyk8, fmt);
        Assert.Equal(67, info.Width);
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }

        Assert.True(max <= 12, $"max difference {max}");
    }

    [Fact]
    public void OutputIsIdenticalAcrossRunsAndDegrees()
    {
        Rgb24[] px = JpegInteropTests.Photo(300, 200, 5);
        Image img = Image.FromPixels<Rgb24>(px, 300, 200);
        foreach (bool progressive in new[] { false, true })
        {
            var o = new JpegEncoderOptions { Quality = 82, Progressive = progressive };
            byte[] first = JpegEncodeTests.Encode(img, o);
            byte[] second = JpegEncodeTests.Encode(img, o);
            Assert.Equal(first, second);
            using var ms = new MemoryStream();
            img.Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = o, MaxDegree = 1 });
            Assert.Equal(first, ms.ToArray());
            using var ms8 = new MemoryStream();
            img.Save(ms8, JpegFormat.Instance, new SaveOptions { EncoderOptions = o, MaxDegree = 8 });
            Assert.Equal(first, ms8.ToArray());
        }
    }

    [Fact]
    public void OptimizedTablesShrinkTheFileAndProgressiveRoundTrips()
    {
        Rgb24[] px = JpegInteropTests.Photo(400, 300, 6);
        Image img = Image.FromPixels<Rgb24>(px, 400, 300);
        int standard = JpegEncodeTests.Encode(img, new JpegEncoderOptions { Quality = 80, OptimizeHuffman = false }).Length;
        byte[] optimized = JpegEncodeTests.Encode(img, new JpegEncoderOptions { Quality = 80, OptimizeHuffman = true });
        byte[] progressive = JpegEncodeTests.Encode(img, new JpegEncoderOptions { Quality = 80, Progressive = true });
        Assert.True(optimized.Length < standard, $"{optimized.Length} !< {standard}");
        (_, byte[] a, _) = JpegTestSupport.Decode(optimized);
        (_, byte[] b, _) = JpegTestSupport.Decode(progressive);
        Assert.Equal(a, b);
        TestContext.Current.SendDiagnosticMessage($"standard {standard} B, optimized {optimized.Length} B, progressive {progressive.Length} B");
    }

    [Fact]
    public void BadOptionsAreArgumentErrors()
    {
        Image img = Image.FromPixels<Rgb24>(new Rgb24[16], 4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegEncodeTests.Encode(img, new JpegEncoderOptions { Quality = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegEncodeTests.Encode(img, new JpegEncoderOptions { Quality = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegEncodeTests.Encode(img, new JpegEncoderOptions { RestartInterval = -1 }));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(16, 16)]
    [InlineData(17, 33)]
    public void TinyImagesRoundTrip(int w, int h)
    {
        Rgb24[] px = JpegInteropTests.Photo(w, h, w + h);
        Image img = Image.FromPixels<Rgb24>(px, w, h);
        foreach (JpegSubsampling sub in Enum.GetValues<JpegSubsampling>())
        {
            byte[] jpeg = JpegEncodeTests.Encode(img, new JpegEncoderOptions { Quality = 100, Subsampling = sub });
            (ImageInfo info, byte[] data, _) = JpegTestSupport.Decode(jpeg);
            Assert.Equal((w, h), (info.Width, info.Height));
            Assert.Equal(w * h * 3, data.Length);
        }
    }
}
