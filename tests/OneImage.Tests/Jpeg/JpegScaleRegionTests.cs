using OneImage.Codecs.Jpeg;

namespace OneImage.Tests.Jpeg;

/// <summary>Wraps the JPEG decoder and records what the core asked of it.</summary>
internal sealed class RecordingJpeg : IImageFormat, IImageDecoder
{
    public List<DecodeRequest> Requests { get; } = [];

    public string Name => "jpeg";

    public IReadOnlyList<string> Extensions => JpegFormat.Instance.Extensions;

    public string MimeType => JpegFormat.Instance.MimeType;

    public int HeaderLength => 3;

    public IImageDecoder? Decoder => this;

    public IImageEncoder? Encoder => null;

    public DecoderCapabilities Capabilities => JpegFormat.Instance.Capabilities;

    public bool Matches(ReadOnlySpan<byte> header) => JpegFormat.Instance.Matches(header);

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        ImageInfo i = JpegFormat.Instance.ReadInfo(source, limits);
        return new ImageInfo(this, i.Frames, i.LoopCount, i.Metadata);
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        JpegFormat.Instance.DecodeFrame(source, info, frameIndex, request, sink, limits, cancellationToken);
    }
}

public class JpegScaleRegionTests
{
    public static TheoryData<string> Names() => new() { "base-420", "base-422", "base-440", "base-444", "prog-420", "arith-420", "gray", "cmyk", "ycck", "p12-420", "base-420-restart-rows", "arith-prog-444", "base-411" };

    [Theory]
    [MemberData(nameof(Names))]
    public void RegionDecodeEqualsCropOfFullDecode(string name)
    {
        byte[] jpeg = JpegTestSupport.Read(name);
        var rnd = new Random(name.Length);
        foreach (int scale in new[] { 1, 2, 4, 8 })
        {
            (ImageInfo info, byte[] full, PixelFormat fmt) = JpegTestSupport.Decode(jpeg, scale);
            int bpp = fmt.BytesPerPixel;
            int fw = (info.Width + scale - 1) / scale;
            int fh = (info.Height + scale - 1) / scale;
            for (int k = 0; k < 8; k++)
            {
                int w = 1 + rnd.Next(fw);
                int h = 1 + rnd.Next(fh);
                var r = new ImageRect(rnd.Next(fw - w + 1), rnd.Next(fh - h + 1), w, h);
                (_, byte[] part, _) = JpegTestSupport.Decode(jpeg, scale, r);
                for (int y = 0; y < h; y++)
                {
                    ReadOnlySpan<byte> expected = full.AsSpan(((((r.Y + y) * fw) + r.X) * bpp), w * bpp);
                    Assert.True(expected.SequenceEqual(part.AsSpan(y * w * bpp, w * bpp)), $"{name} 1/{scale} region {r} row {y}");
                }
            }
        }
    }

    [Theory]
    [InlineData("base-420")]
    [InlineData("base-444")]
    [InlineData("prog-420")]
    public void EighthScaleNeverRunsFullSizeTransforms(string name)
    {
        byte[] jpeg = JpegTestSupport.Read(name);
        JpegDiagnostics.FullBlocks = 0;
        JpegDiagnostics.ReducedBlocks = 0;
        JpegTestSupport.Decode(jpeg, 8);
        Assert.Equal(0, JpegDiagnostics.FullBlocks);
        Assert.True(JpegDiagnostics.ReducedBlocks > 0);

        // Control: the unscaled decode does run them.
        JpegDiagnostics.FullBlocks = 0;
        JpegTestSupport.Decode(jpeg, 1);
        Assert.True(JpegDiagnostics.FullBlocks > 0);
    }

    [Fact]
    public void CroppedDecodeSkipsTransformsOutsideTheRegion()
    {
        byte[] jpeg = JpegTestSupport.Read("base-444");
        JpegDiagnostics.FullBlocks = 0;
        JpegTestSupport.Decode(jpeg, 1, new ImageRect(0, 0, 67, 45));
        long all = JpegDiagnostics.FullBlocks;
        JpegDiagnostics.FullBlocks = 0;
        JpegTestSupport.Decode(jpeg, 1, new ImageRect(30, 20, 8, 8));
        Assert.True(JpegDiagnostics.FullBlocks * 3 < all, $"{JpegDiagnostics.FullBlocks} of {all}");
    }

    [Fact]
    public void ThumbnailPathAsksTheDecoderForAnEighthAndNotForFullSize()
    {
        // A 4000 x 3000 image, resized to 250 wide, is a 1/16 reduction: the decoder should be asked for 1/8.
        var px = new Rgb24[4000 * 3000];
        for (int y = 0; y < 3000; y++)
        {
            for (int x = 0; x < 4000; x++)
            {
                px[(y * 4000) + x] = new Rgb24((byte)(x / 16), (byte)(y / 12), (byte)((x + y) / 30));
            }
        }

        byte[] jpeg = JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(px, 4000, 3000), new JpegEncoderOptions { Quality = 85 });
        var rec = new RecordingJpeg();
        var config = new ImageConfiguration().Add(rec);
        Image img = Image.Load(jpeg, new LoadOptions { Configuration = config });
        Rgb24[] thumb = img.Resize(width: 250).ToArray<Rgb24>();
        Assert.Equal(250 * 188, thumb.Length);
        lock (rec.Requests)
        {
            Assert.NotEmpty(rec.Requests);
            Assert.All(rec.Requests, r => Assert.Equal(8, r.ScaleDenominator));
        }
    }

    [Fact]
    public void TwentyFourMegapixelThumbnailLoadNeverTransformsFullBlocks()
    {
        const int W = 6000;
        const int H = 4000;
        var px = new Rgb24[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                px[(y * W) + x] = new Rgb24((byte)((x / 24) + (y & 7)), (byte)(y / 16), (byte)(((x ^ y) >> 5) & 255));
            }
        }

        byte[] jpeg = JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(px, W, H), new JpegEncoderOptions { Quality = 90 });
        px = [];
        JpegDiagnostics.FullBlocks = 0;
        JpegDiagnostics.ReducedBlocks = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        (_, byte[] data, _) = JpegTestSupport.Decode(jpeg, 8);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(750 * 500 * 3, data.Length);
        Assert.Equal(0, JpegDiagnostics.FullBlocks);
        Assert.True(JpegDiagnostics.ReducedBlocks > 0);
        TestContext.Current.SendDiagnosticMessage($"24 MP at 1/8: reduced blocks {JpegDiagnostics.ReducedBlocks}, allocated {allocated / 1024} KiB (full frame would be {W * H * 3 / 1024} KiB)");
        Assert.True(allocated < 8_000_000);
    }
}
