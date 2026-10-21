using System.Buffers.Binary;
using Xunit;

namespace OneImage.Tests.Geometry;

/// <summary>
/// Test-only format "XIF1": width, height, EXIF length (int32 LE each), the EXIF blob, then RGBA rows. The decoder reports the blob as
/// metadata, optionally also as <see cref="ImageMetadata.Orientation"/> as a real codec would. The encoder writes the metadata
/// it is handed, so tests can read back what Save recorded.
/// </summary>
internal sealed class ExifTestFormat : IImageFormat, IImageDecoder, IImageEncoder
{
    private readonly bool _setProperty;

    public ExifTestFormat(bool setOrientationProperty = true, DecoderCapabilities capabilities = DecoderCapabilities.None)
    {
        _setProperty = setOrientationProperty;
        Capabilities = capabilities;
    }

    public string Name => "xif";

    public IReadOnlyList<string> Extensions { get; } = [".xif"];

    public string MimeType => "application/x-xif-test";

    public int HeaderLength => 4;

    public IImageDecoder? Decoder => this;

    public IImageEncoder? Encoder => this;

    public DecoderCapabilities Capabilities { get; }

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } = [PixelFormat.Rgba8];

    public bool SupportsMultipleFrames => false;

    public ImageConfiguration Configuration() => new ImageConfiguration().Add(this);

    public bool Matches(ReadOnlySpan<byte> header) => header.Length >= 4 && header[..4].SequenceEqual("XIF1"u8);

    public static byte[] File(int w, int h, byte[] exif)
    {
        using var ms = new MemoryStream();
        Write(ms, w, h, exif, i => OrientTests.Px(i % w, i / w));
        return ms.ToArray();
    }

    private static void Write(Stream s, int w, int h, ReadOnlyMemory<byte> exif, Func<int, Rgba32> pixel)
    {
        Span<byte> head = stackalloc byte[16];
        "XIF1"u8.CopyTo(head);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], w);
        BinaryPrimitives.WriteInt32LittleEndian(head[8..], h);
        BinaryPrimitives.WriteInt32LittleEndian(head[12..], exif.Length);
        s.Write(head);
        s.Write(exif.Span);
        byte[] row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba32 p = pixel((y * w) + x);
                row[x * 4] = p.R;
                row[(x * 4) + 1] = p.G;
                row[(x * 4) + 2] = p.B;
                row[(x * 4) + 3] = p.A;
            }

            s.Write(row);
        }
    }

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        (int w, int h, byte[] exif, _) = Header(source);
        limits.CheckFrame(w, h);
        int orientation = 1;
        if (_setProperty && ExifData.TryParse(exif, out ExifData? d) && d?.Orientation is { } o)
        {
            orientation = o;
        }

        return new ImageInfo(this, [new FrameInfo(w, h, PixelFormat.Rgba8)], 0, new ImageMetadata { Exif = exif, Orientation = orientation });
    }

    private static (int W, int H, byte[] Exif, long Data) Header(Stream s)
    {
        byte[] head = new byte[16];
        s.Position = 0;
        s.ReadExactly(head);
        int w = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        int h = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
        int n = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(12));
        byte[] exif = new byte[n];
        s.ReadExactly(exif);
        return (w, h, exif, 16 + n);
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        (int w, int h, _, long data) = Header(source);
        ImageRect r = request.Region;
        int d = request.ScaleDenominator;
        byte[] full = new byte[w * 4];
        byte[] row = new byte[r.Width * 4];
        for (int y = 0; y < r.Height; y++)
        {
            // A scaled decode takes the pixel at the center of each d x d block.
            long sy = Math.Min(h - 1, ((r.Y + y) * d) + (d / 2));
            source.Position = data + (sy * w * 4);
            source.ReadExactly(full);
            for (int x = 0; x < r.Width; x++)
            {
                Array.Copy(full, Math.Min(w - 1, ((r.X + x) * d) + (d / 2)) * 4, row, x * 4, 4);
            }

            sink.WriteRows(y, row, 1);
        }
    }

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ImageFrameReader f = request.Frames[0];
        byte[] all = new byte[f.Width * f.Height * 4];
        f.ReadRows(0, f.Height, all);
        Write(destination, f.Width, f.Height, request.Metadata.Exif, i => new Rgba32(all[i * 4], all[(i * 4) + 1], all[(i * 4) + 2], all[(i * 4) + 3]));
    }

    /// <summary>Reads the EXIF blob back out of a saved file.</summary>
    public static byte[] ExifOf(byte[] file) => Header(new MemoryStream(file)).Exif;
}

public class AutoOrientLoadTests
{
    private const int W = 150;
    private const int H = 90;

    private static (int W, int H, Func<int, int, (int X, int Y)> Src) Expected(int o) => o switch
    {
        1 => (W, H, (x, y) => (x, y)),
        2 => (W, H, (x, y) => (W - 1 - x, y)),
        3 => (W, H, (x, y) => (W - 1 - x, H - 1 - y)),
        4 => (W, H, (x, y) => (x, H - 1 - y)),
        5 => (H, W, (x, y) => (y, x)),
        6 => (H, W, (x, y) => (y, H - 1 - x)),
        7 => (H, W, (x, y) => (W - 1 - y, H - 1 - x)),
        _ => (H, W, (x, y) => (W - 1 - y, x)),
    };

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(6, false)]
    [InlineData(8, false)]
    public void LoadAppliesTheStoredOrientationLazily(int orientation, bool decoderSetsProperty)
    {
        var fmt = new ExifTestFormat(decoderSetsProperty);
        byte[] file = ExifTestFormat.File(W, H, MetadataTests.BuildExif(true, orientation));
        Image img = Image.Load(file, new LoadOptions { Configuration = fmt.Configuration() });
        (int ew, int eh, Func<int, int, (int X, int Y)> src) = Expected(orientation);
        Assert.Equal((ew, eh), (img.Width, img.Height));
        Assert.Equal(1, img.Metadata.Orientation);
        Rgba32[] got = img.ToArray<Rgba32>();
        for (int y = 0; y < eh; y++)
        {
            for (int x = 0; x < ew; x++)
            {
                (int sx, int sy) = src(x, y);
                Assert.Equal(OrientTests.Px(sx, sy), got[(y * ew) + x]);
            }
        }
    }

    [Fact]
    public void OptOutKeepsStoredPixelsAndTag()
    {
        var fmt = new ExifTestFormat();
        byte[] file = ExifTestFormat.File(W, H, MetadataTests.BuildExif(false, 6));
        Image raw = Image.Load(file, new LoadOptions { Configuration = fmt.Configuration(), AutoOrient = false });
        Assert.Equal((W, H), (raw.Width, raw.Height));
        Assert.Equal(6, raw.Metadata.Orientation);
        Assert.Equal(OrientTests.Px(3, 4), raw.ToArray<Rgba32>()[(4 * W) + 3]);

        // The explicit op gives the same result as the default load, and a second call changes nothing.
        Image up = raw.AutoOrient();
        Image auto = Image.Load(file, new LoadOptions { Configuration = fmt.Configuration() });
        Assert.Equal(auto.ToArray<Rgba32>(), up.ToArray<Rgba32>());
        Assert.Equal(1, up.Metadata.Orientation);
        Assert.Same(up, up.AutoOrient());
    }

    [Fact]
    public void SaveWritesUprightPixelsWithTheTagResetAndSizeCorrected()
    {
        var fmt = new ExifTestFormat();
        foreach (int orientation in new[] { 1, 3, 6, 8 })
        {
            byte[] file = ExifTestFormat.File(W, H, MetadataTests.BuildExif(true, orientation));
            Image img = Image.Load(file, new LoadOptions { Configuration = fmt.Configuration() });
            using var ms = new MemoryStream();
            img.Save(ms, fmt);
            byte[] saved = ms.ToArray();

            ExifData written = ExifData.Parse(ExifTestFormat.ExifOf(saved));
            Assert.Equal(1, written.Orientation);
            Assert.Equal((img.Width, img.Height), (written.PixelWidth ?? img.Width, written.PixelHeight ?? img.Height));

            // Loading the saved file again must not rotate it a second time.
            Image again = Image.Load(saved, new LoadOptions { Configuration = fmt.Configuration() });
            Assert.Equal((img.Width, img.Height), (again.Width, again.Height));
            Assert.Equal(img.ToArray<Rgba32>(), again.ToArray<Rgba32>());
        }
    }

    [Fact]
    public void OptOutSaveKeepsTheRecordedTag()
    {
        var fmt = new ExifTestFormat();
        byte[] file = ExifTestFormat.File(W, H, MetadataTests.BuildExif(true, 6));
        Image raw = Image.Load(file, new LoadOptions { Configuration = fmt.Configuration(), AutoOrient = false });
        using var ms = new MemoryStream();
        raw.Save(ms, fmt);
        Assert.Equal(6, ExifData.Parse(ExifTestFormat.ExifOf(ms.ToArray())).Orientation);
    }

    [Fact]
    public void MalformedExifDoesNotBreakLoadOrSave()
    {
        var fmt = new ExifTestFormat();
        byte[] file = ExifTestFormat.File(W, H, [1, 2, 3, 4, 5]);
        Image img = Image.Load(file, new LoadOptions { Configuration = fmt.Configuration() });
        Assert.Equal((W, H), (img.Width, img.Height));
        using var ms = new MemoryStream();
        img.Save(ms, fmt);
        Assert.Empty(ExifTestFormat.ExifOf(ms.ToArray()));
    }

    [Fact]
    public void ResizeAndCropPushThroughTheOrientationToTheDecoder()
    {
        // Graph shape only: the orient node sits above the decode node, and crop moves below it.
        var fmt = new ExifTestFormat();
        byte[] file = ExifTestFormat.File(W, H, MetadataTests.BuildExif(true, 6));
        Image img = Image.Load(file, new LoadOptions { Configuration = fmt.Configuration() });
        string graph = img.Crop(10, 20, 30, 40).DescribeGraph();
        Assert.StartsWith("orient(orientation=6)", graph, StringComparison.Ordinal);
        Assert.Contains("crop(", graph, StringComparison.Ordinal);
        Assert.Equal(img.Crop(10, 20, 30, 40).ToArray<Rgba32>(), Crop(img.ToArray<Rgba32>(), img.Width, 10, 20, 30, 40));
    }

    [Fact]
    public void ShrinkOnLoadAndCropPushdownSurviveAutoOrient()
    {
        var fmt = new ExifTestFormat(true, DecoderCapabilities.Scale | DecoderCapabilities.Region);
        byte[] file = ExifTestFormat.File(1600, 1200, MetadataTests.BuildExif(true, 6));
        Image img = Image.Load(file, new LoadOptions { Configuration = fmt.Configuration() });
        Assert.Equal((1200, 1600), (img.Width, img.Height));
        string small = img.Resize(width: 150).DescribeGraph();
        Assert.Contains("scale=8", small, StringComparison.Ordinal);
        Assert.StartsWith("resample(", small, StringComparison.Ordinal);
        Assert.Contains("orient(orientation=6)", small, StringComparison.Ordinal);
        Image cropped = img.Crop(100, 200, 64, 32);
        string g = cropped.DescribeGraph();
        Assert.Contains("decode(", g, StringComparison.Ordinal);
        Assert.Contains("width=32, height=64", g, StringComparison.Ordinal);
        Assert.Equal(Crop(img.ToArray<Rgba32>(), 1200, 100, 200, 64, 32), cropped.ToArray<Rgba32>());
    }

    private static Rgba32[] Crop(Rgba32[] px, int w, int x0, int y0, int cw, int ch)
    {
        var r = new Rgba32[cw * ch];
        for (int y = 0; y < ch; y++)
        {
            Array.Copy(px, ((y0 + y) * w) + x0, r, y * cw, cw);
        }

        return r;
    }

    [Fact]
    public void OrientNodeMatchesTheDocumentedFlipThenRotateDecomposition()
    {
        Image img = OrientTests.Make(77, 45);
        for (int o = 1; o <= 8; o++)
        {
            OrientationOps ops = OrientationOps.For(o);
            Image viaOps = ops.FlipHorizontal ? img.Flip(FlipAxis.Horizontal) : img;
            viaOps = viaOps.Rotate(ops.RotateClockwise);
            Assert.Equal(viaOps.ToArray<Rgba32>(), img.Orient(o).ToArray<Rgba32>());
        }
    }
}
