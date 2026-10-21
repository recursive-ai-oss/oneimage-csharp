using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>WebP through the public Load/Save API: formats, metadata, determinism.</summary>
public sealed class WebPApiTests
{
    private static readonly WebPFormat Format = new();

    private static Image Photo(int w, int h, int seed, bool alpha) =>
        Image.FromPixels<Rgba32>(MemoryMarshal.Cast<byte, Rgba32>(WebPOracle.MakeImage(w, h, seed, alpha)), w, h);

    private static byte[] Save(Image img, WebPEncoderOptions? o = null)
    {
        using var ms = new MemoryStream();
        img.Save(ms, Format, new SaveOptions { EncoderOptions = o });
        return ms.ToArray();
    }

    public static IEnumerable<object[]> Modes() =>
    [
        [false, false], [false, true], [true, false], [true, true],
    ];

    [Theory]
    [MemberData(nameof(Modes))]
    public void EncodingIsDeterministic(bool lossless, bool alpha)
    {
        Image img = Photo(61, 47, 7, alpha);
        byte[] a = Save(img, new WebPEncoderOptions { Lossless = lossless });
        byte[] b = Save(Photo(61, 47, 7, alpha), new WebPEncoderOptions { Lossless = lossless });
        Assert.Equal(a, b);
        // Concurrent encoders on the same input also agree.
        byte[][] many = new byte[4][];
        Parallel.For(0, 4, i => many[i] = Save(img, new WebPEncoderOptions { Lossless = lossless }));
        Assert.All(many, m => Assert.Equal(a, m));
    }

    [Fact]
    public void AnimationEncodingIsDeterministic()
    {
        List<Image> frames = [Photo(30, 20, 1, true), Photo(30, 20, 2, true), Photo(30, 20, 3, false)];
        byte[] a = Save(WebPAnimationTests.AnimationOf(frames, [10, 20, 30], 0));
        byte[] b = Save(WebPAnimationTests.AnimationOf(frames, [10, 20, 30], 0));
        Assert.Equal(a, b);
    }

    [Fact]
    public void OpaqueImagesAreWrittenWithoutAlphaAndLoadAsRgb()
    {
        foreach (bool lossless in new[] { false, true })
        {
            byte[] file = Save(Photo(40, 30, 3, false), new WebPEncoderOptions { Lossless = lossless });
            Image back = Image.Load(file);
            Assert.Equal(PixelFormat.Rgb8, back.Format);
            Assert.Equal(40, back.Width);
            Assert.Equal(30, back.Height);
            Image withAlpha = Image.Load(Save(Photo(40, 30, 3, true), new WebPEncoderOptions { Lossless = lossless }));
            Assert.Equal(PixelFormat.Rgba8, withAlpha.Format);
        }
    }

    [Fact]
    public void SaveAndLoadThroughFilesStreamsAndBuffers()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oneimage-webp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Image img = Photo(50, 36, 9, true);
            string path = Path.Combine(dir, "a.webp");
            img.Save(path, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = true } });
            Image back = Image.Load(path);
            Assert.Equal(Rgba32Bytes(img, img), Rgba32Bytes(back, img));
            Assert.Equal("webp", back.SourceFormat?.Name);

            var writer = new ArrayBufferWriter<byte>();
            img.Save(writer, Format, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = true } });
            Assert.Equal(File.ReadAllBytes(path), writer.WrittenSpan.ToArray());

            ImageInfo info = Image.Identify(path);
            Assert.Equal(50, info.Width);
            Assert.Equal(36, info.Height);
            img.SaveAsync(Path.Combine(dir, "b.webp")).GetAwaiter().GetResult();
            Assert.True(File.Exists(Path.Combine(dir, "b.webp")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static byte[] Rgba32Bytes(Image img, Image? ignoreHiddenColor = null)
    {
        byte[] b = MemoryMarshal.AsBytes(img.ToArray<Rgba32>().AsSpan()).ToArray();
        if (ignoreHiddenColor != null)
        {
            byte[] o = MemoryMarshal.AsBytes(ignoreHiddenColor.ToArray<Rgba32>().AsSpan()).ToArray();
            for (int i = 0; i < b.Length; i += 4)
            {
                if (o[i + 3] == 0)
                {
                    b.AsSpan(i, 3).Clear();
                }
            }
        }

        return b;
    }

    [Fact]
    public void LossyQualityControlsSize()
    {
        Image img = Photo(120, 90, 4, false);
        int low = Save(img, new WebPEncoderOptions { Quality = 20 }).Length;
        int mid = Save(img).Length;
        int high = Save(img, new WebPEncoderOptions { Quality = 95 }).Length;
        Assert.True(low < mid && mid < high, $"{low} {mid} {high}");
        Assert.Throws<ArgumentOutOfRangeException>(() => Save(img, new WebPEncoderOptions { Quality = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Save(img, new WebPEncoderOptions { Effort = 7 }));
    }

    private static List<(string Id, byte[] Data)> Chunks(byte[] file)
    {
        var list = new List<(string, byte[])>();
        int pos = 12;
        while (pos + 8 <= file.Length)
        {
            int size = (int)BitConverter.ToUInt32(file, pos + 4);
            list.Add((Encoding.ASCII.GetString(file, pos, 4), file.AsSpan(pos + 8, size).ToArray()));
            pos += 8 + size + (size & 1);
        }

        return list;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void MetadataRoundTrips(bool lossless, bool alpha)
    {
        byte[] icc = new byte[300];
        new Random(1).NextBytes(icc);
        byte[] xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><odd/></x:xmpmeta>");
        byte[] exif = MetadataTests.BuildExif(true, 6);
        var meta = new ImageMetadata { IccProfile = icc, Exif = exif, Xmp = xmp, Orientation = 6 };
        Image src = Photo(52, 41, 5, alpha);
        Image img = WebPAnimationTests.AnimationOf([src], [0], 0, meta);
        byte[] file = Save(img, new WebPEncoderOptions { Lossless = lossless });

        List<(string Id, byte[] Data)> chunks = Chunks(file);
        Assert.Equal("VP8X", chunks[0].Id);
        Assert.Equal(0x20 | 0x08 | 0x04, chunks[0].Data[0] & (0x20 | 0x08 | 0x04));
        Assert.Equal(alpha, (chunks[0].Data[0] & 0x10) != 0);
        Assert.Equal("ICCP", chunks[1].Id);
        Assert.Equal(icc, chunks[1].Data);

        Image back = Image.Load(file);
        Assert.Equal(icc, back.Metadata.IccProfile.ToArray());
        Assert.Equal(xmp, back.Metadata.Xmp.ToArray());
        Assert.Equal(6, back.Metadata.Orientation);
        // The EXIF blob is rewritten for the output size; the typed values survive.
        ExifData tags = back.Metadata.ReadExif()!;
        Assert.Equal(6, tags.Orientation);
        
        // libwebp still decodes the file with all the chunks.
        byte[] theirs = WebPOracle.SkiaDecode(file, out int w, out int h);
        Assert.Equal((52, 41), (w, h));
        Assert.NotEmpty(theirs);

        // And a file without metadata stays in the simple layout when it has no alpha.
        if (!alpha)
        {
            Assert.DoesNotContain(Chunks(Save(src, new WebPEncoderOptions { Lossless = lossless })), c => c.Id == "VP8X");
        }
    }

    [Fact]
    public void ExifDimensionTagsAreCorrectedOnWrite()
    {
        // The blob built for a different size must come back describing this image.
        byte[] exif = MetadataTests.BuildExif(false, 3);
        var meta = new ImageMetadata { Exif = exif, Orientation = 3 };
        Image img = WebPAnimationTests.AnimationOf([Photo(20, 10, 1, false)], [0], 0, meta);
        Image back = Image.Load(Save(img, new WebPEncoderOptions { Lossless = true }));
        Assert.Equal(3, back.Metadata.Orientation);
        Assert.Equal(meta.ExifForOutput(20, 10, 3).ToArray(), back.Metadata.Exif.ToArray());
    }

    [Fact]
    public void MalformedExifIsCarriedVerbatim()
    {
        byte[] junk = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        Image img = WebPAnimationTests.AnimationOf([Photo(12, 12, 1, false)], [0], 0, new ImageMetadata { Exif = junk });
        Image back = Image.Load(Save(img));
        Assert.Equal(junk, back.Metadata.Exif.ToArray());
        Assert.Equal(1, back.Metadata.Orientation);
    }

    [Fact]
    public void MagickReadsOurFiles()
    {
        foreach (bool lossless in new[] { false, true })
        {
            byte[] file = Save(Photo(33, 27, 8, true), new WebPEncoderOptions { Lossless = lossless });
            using var m = new MagickImage(file);
            Assert.Equal((33u, 27u), (m.Width, m.Height));
            Assert.True(m.HasAlpha);
        }
    }
}

public sealed class WebPHiddenColorTests
{
    [Fact]
    public void ExactLosslessKeepsColorUnderTransparency()
    {
        Rgba32[] px = [new(10, 20, 30, 0), new(1, 2, 3, 255), new(200, 100, 50, 0), new(9, 9, 9, 128)];
        Image img = Image.FromPixels<Rgba32>(px, 2, 2);
        Assert.Equal(px, img.ToArray<Rgba32>());
        using var ms = new MemoryStream();
        img.Save(ms, new WebPFormat(), new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = true } });
        Assert.Equal(px, Image.Load(ms.ToArray()).ToArray<Rgba32>());
    }
}
