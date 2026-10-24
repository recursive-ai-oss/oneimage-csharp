using System.Runtime.InteropServices;
using Xunit;

namespace OneImage.Tests.Region;

/// <summary>
/// Giving decoded pixels back (on an image, on a frame, through a frame's single-frame image) and reading regions without building
/// the region plane: what stays in memory afterwards, and that the pixels read never change.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class ReleaseDecodedPixelsTests : IDisposable
{
    private readonly long _savedThreshold = DecodeSourceNode.StreamingMinBytes;
    private readonly long _savedBudget = ImageEngine.RegionCacheBytes;

    public void Dispose()
    {
        DecodeSourceNode.StreamingMinBytes = _savedThreshold;
        ImageEngine.RegionCacheBytes = _savedBudget;
    }

    private sealed class CountingFormat(IImageFormat inner) : IImageFormat, IImageDecoder
    {
        private int _decodes;

        public int Decodes => Volatile.Read(ref _decodes);

        public string Name => inner.Name;

        public IReadOnlyList<string> Extensions => inner.Extensions;

        public string MimeType => inner.MimeType;

        public int HeaderLength => inner.HeaderLength;

        public IImageDecoder? Decoder => this;

        public IImageEncoder? Encoder => null;

        public DecoderCapabilities Capabilities => inner.Decoder!.Capabilities;

        public bool Matches(ReadOnlySpan<byte> header) => inner.Matches(header);

        public ImageInfo ReadInfo(Stream source, ImageLimits limits) => inner.Decoder!.ReadInfo(source, limits);

        public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _decodes);
            inner.Decoder!.DecodeFrame(source, info, frameIndex, request, sink, limits, cancellationToken);
        }
    }

    private static (Image Image, CountingFormat Counter) LoadCounting(byte[] bytes, IImageFormat format)
    {
        var counter = new CountingFormat(format);
        return (Image.Load(bytes, new LoadOptions { Configuration = new ImageConfiguration().Add(counter) }), counter);
    }

    private static byte[] PageJpeg(int w, int h, int orientation)
    {
        Image image = Image.FromPixels<Rgb24>(MemoryMarshal.Cast<byte, Rgb24>(RegionInputs.Pixels(w, h, 3)), w, h);
        if (orientation != 1)
        {
            byte[] exif = [0x4D, 0x4D, 0, 0x2A, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0];
            image = image.WithMetadata(new ImageMetadata { Exif = exif, Orientation = orientation });
        }

        using var ms = new MemoryStream();
        image.Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 80 } });
        return ms.ToArray();
    }

    private static void ReadBands(Image image, Gray8[] into, bool useRegionCache)
    {
        foreach (ImageRect r in RegionPatterns.Bands(image.Width, image.Height, 80))
        {
            image.CopyPixelsTo<Gray8>(into.AsSpan(r.Y * image.Width, r.Width * r.Height), r, useRegionCache);
        }
    }

    private static string Describe(string what, long bytes)
    {
        string text = $"{what}: {bytes / 1024} KiB";
        TestContext.Current.SendDiagnosticMessage(text);
        return text;
    }

    [Fact]
    public void FrameReleaseGivesBackThePlane()
    {
        byte[] bytes = PageJpeg(2480, 3508, 1);
        Image image = Image.Load(bytes);
        var all = new Gray8[2480 * 3508];
        long baseline = LiveHeap.Total(collect: true) - all.Length;
        ReadBands(image, all, true);
        long held = LiveHeap.Total(collect: true) - baseline - all.Length;
        Assert.True(held > 6L << 20, Describe("held after bands", held));

        image.Frames[0].ReleaseDecodedPixels();
        long after = LiveHeap.Total(collect: true) - baseline - all.Length;
        Assert.True(after < 3L << 20, Describe("after frame release", after));
        GC.KeepAlive(image);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReleaseThroughASingleFrameImageFreesTheFrame(bool readThroughToImage)
    {
        byte[] bytes = PageJpeg(2480, 3508, 1);
        Image image = Image.Load(bytes);
        Image single = image.Frames[0].ToImage();
        Image reader = readThroughToImage ? single : image;
        Image releaser = readThroughToImage ? image : single;
        var all = new Gray8[2480 * 3508];
        long baseline = LiveHeap.Total(collect: true) - all.Length;
        ReadBands(reader, all, true);

        // The reads built the plane (checked on the plane itself: a heap delta here also counts whatever earlier tests left behind).
        Assert.True(image.Frames[0].Node.PlanesIfCreated!.PlaneBytes >= 2480L * 3508 / 2);

        releaser.ReleaseDecodedPixels();
        long after = LiveHeap.Total(collect: true) - baseline - all.Length;
        Assert.True(after < 3L << 20, Describe("after release through the other image", after));

        // Both still read the same pixels.
        Gray8[] again = new Gray8[all.Length];
        ReadBands(reader, again, true);
        Assert.True(all.AsSpan().SequenceEqual(again));
        GC.KeepAlive(image);
        GC.KeepAlive(single);
    }

    [Fact]
    public void ReleaseGivesBackWhatAWholePageReadAndLaterReadsKept()
    {
        // A gray conversion of a small RGB PNG: the second read keeps the decoded frame as tiles and the conversion's tiles sit in the cache.
        byte[] png = RegionInputs.PngBytes(1700, 2200, 3, interlaced: false);
        long baseline = LiveHeap.Total(collect: true);
        Image image = Image.Load(png).ConvertTo(PixelFormat.Gray8);
        long loaded = LiveHeap.Total(collect: true);
        Gray8[] whole = image.ToArray<Gray8>();
        var band = new Gray8[1700 * 100];
        for (int y = 0; y < 2200; y += 100)
        {
            image.CopyPixelsTo<Gray8>(band, new ImageRect(0, y, 1700, 100));
        }

        long held = LiveHeap.Total(collect: true) - loaded - whole.Length;
        Assert.True(held > 8L << 20, Describe("held after whole read and bands", held));

        image.ReleaseDecodedPixels();
        long after = LiveHeap.Total(collect: true) - loaded - whole.Length;
        Assert.True(after < 512L << 10, Describe("after release", after));

        // The image still reads, identically.
        Assert.True(whole.AsSpan().SequenceEqual(image.ToArray<Gray8>()));
        GC.KeepAlive(image);
        Assert.True(loaded >= baseline);
    }

    [Fact]
    public void ReleaseGivesBackTheTilesOfARandomAccessDecode()
    {
        // TIFF tiles are cached in the shared tile cache and the decoder keeps a block cache; both are decode-derived.
        const int W = 1500;
        const int H = 1200;
        Image src = Image.FromPixels<Rgb24>(MemoryMarshal.Cast<byte, Rgb24>(RegionInputs.Pixels(W, H, 3)), W, H);
        using var ms = new MemoryStream();
        src.Save(ms, TiffFormat.Instance, new SaveOptions { EncoderOptions = new TiffEncoderOptions { Compression = TiffCompression.Deflate } });
        Image image = Image.Load(ms.ToArray());
        Gray8[] expected = Image.Load(ms.ToArray()).ToArray<Gray8>();
        long loaded = LiveHeap.Total(collect: true);
        var buf = new Gray8[W * 200];
        for (int y = 0; y < H; y += 200)
        {
            image.CopyPixelsTo<Gray8>(buf, new ImageRect(0, y, W, 200));
        }

        long held = LiveHeap.Total(collect: true) - loaded;
        image.ReleaseDecodedPixels();
        long after = LiveHeap.Total(collect: true) - loaded;
        Describe("tiff held / after", held);
        Describe("tiff after release", after);
        // What is left is idle pooled block buffers (ArrayPool, not held by the image), well under what the reads kept.
        Assert.True(after < held / 2, $"{after} bytes remain of {held}");
        Assert.True(expected.AsSpan().SequenceEqual(image.ToArray<Gray8>()));
        GC.KeepAlive(image);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void ReadingWithoutTheRegionCacheBuildsNoPlaneAndReadsTheSameBytes(int orientation)
    {
        byte[] bytes = PageJpeg(2480, 3508, orientation);
        int w = orientation == 6 ? 3508 : 2480;
        int h = orientation == 6 ? 2480 : 3508;
        Gray8[] expected = Image.Load(bytes).ToArray<Gray8>();

        (Image image, CountingFormat counter) = LoadCounting(bytes, JpegFormat.Instance);
        var all = new Gray8[w * h];
        long baseline = LiveHeap.Total(collect: true) - all.Length;
        for (int round = 0; round < 2; round++)
        {
            foreach (ImageRect r in RegionPatterns.Bands(w, h, 80))
            {
                image.CopyPixelsTo<Gray8>(all.AsSpan(r.Y * w, r.Width * r.Height), r, useRegionCache: false);
            }

            Assert.True(expected.AsSpan().SequenceEqual(all), $"round {round}");
        }

        long held = LiveHeap.Total(collect: true) - baseline - all.Length;
        Assert.True(held < 12L << 20, Describe($"held after bypassed bands (orientation {orientation}, {counter.Decodes} decodes)", held));
        if (orientation == 1)
        {
            // Top-down bands never go back above the window: one pass for the first round and a few more for the second (the window
            // has moved on), never one per band.
            Assert.True(counter.Decodes <= 4, $"{counter.Decodes} decodes");
        }

        // Nothing was counted toward a plane: the same image, read the default way, still behaves like a fresh one.
        Assert.Equal(0, image.Frames[0].Node.PlanesIfCreated?.PlaneBytes ?? 0);
        image.ReleaseDecodedPixels();
        Assert.True(LiveHeap.Total(collect: true) - baseline - all.Length < 3L << 20);
        GC.KeepAlive(image);
    }

    [Fact]
    public void ReadingWithoutTheRegionCacheUsesAnExistingPlane()
    {
        byte[] bytes = PageJpeg(2480, 3508, 1);
        (Image image, CountingFormat counter) = LoadCounting(bytes, JpegFormat.Instance);
        var all = new Gray8[2480 * 3508];
        ReadBands(image, all, true);
        int decodes = counter.Decodes;
        Gray8[] again = new Gray8[all.Length];
        ReadBands(image, again, false);
        Assert.True(all.AsSpan().SequenceEqual(again));
        Assert.Equal(decodes, counter.Decodes);
    }

    [Fact]
    public void DefaultReadsStillBuildThePlane()
    {
        byte[] bytes = PageJpeg(2480, 3508, 1);
        Image image = Image.Load(bytes);
        var all = new Gray8[2480 * 3508];
        ReadBands(image, all, true);
        Assert.True(image.Frames[0].Node.PlanesIfCreated!.PlaneBytes >= 2480L * 3508 / 2);
        GC.KeepAlive(image);
    }
}
