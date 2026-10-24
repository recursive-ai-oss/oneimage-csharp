using System.Runtime.InteropServices;
using Xunit;

namespace OneImage.Tests.Region;

/// <summary>
/// What a loaded image keeps between region reads: repeated and out-of-order reads of a streaming-size JPEG or PNG must not
/// decode the image again, and the memory it holds is bounded and can be given back.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class RegionCacheTests : IDisposable
{
    private readonly long _savedThreshold = DecodeSourceNode.StreamingMinBytes;
    private readonly long _savedBudget = ImageEngine.RegionCacheBytes;

    public void Dispose()
    {
        DecodeSourceNode.StreamingMinBytes = _savedThreshold;
        ImageEngine.RegionCacheBytes = _savedBudget;
    }

    /// <summary>Wraps a format's decoder and counts how many frame decodes were started.</summary>
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

    private static byte[] Gray(Image image, ImageRect r)
    {
        var buffer = new Gray8[r.Width * r.Height];
        image.CopyPixelsTo<Gray8>(buffer, r);
        return MemoryMarshal.AsBytes(buffer.AsSpan()).ToArray();
    }

    // The truth for any region: cut out of one whole-frame read of a separate load (nothing shared with the image under test).
    private static byte[] Expected(Gray8[] whole, int width, ImageRect r)
    {
        var rows = new byte[r.Width * r.Height];
        for (int y = 0; y < r.Height; y++)
        {
            MemoryMarshal.AsBytes(whole.AsSpan(((r.Y + y) * width) + r.X, r.Width)).CopyTo(rows.AsSpan(y * r.Width, r.Width));
        }

        return rows;
    }

    private static void AssertBandsAndRandoms(Image image, Gray8[] whole, int rounds)
    {
        for (int round = 0; round < rounds; round++)
        {
            foreach (ImageRect r in RegionPatterns.Bands(image.Width, image.Height, 80))
            {
                Assert.True(Expected(whole, image.Width, r).AsSpan().SequenceEqual(Gray(image, r)), $"band at y={r.Y}, round {round}");
            }

            foreach (ImageRect r in RegionPatterns.Randoms(image.Width, image.Height, 12, 40 + round))
            {
                Assert.True(Expected(whole, image.Width, r).AsSpan().SequenceEqual(Gray(image, r)), $"random {r}, round {round}");
            }
        }
    }

    [Theory]
    [InlineData("jpeg-base", false, 1)]
    [InlineData("jpeg-prog", true, 1)]
    [InlineData("jpeg-base-rotated", false, 6)]
    [InlineData("jpeg-base-rotated-ccw", false, 8)]
    public void PageJpegBandsAndRandomReadsDecodeAtMostTwice(string name, bool progressive, int orientation)
    {
        // The real page size, with the real streaming threshold: 2480 x 3508 RGB is 26 MB.
        byte[] bytes = PageJpeg(2480, 3508, progressive, orientation);
        Assert.True(2480L * 3508 * 3 >= DecodeSourceNode.StreamingMinBytes);
        Gray8[] whole = Image.Load(bytes).ToArray<Gray8>();

        (Image image, CountingFormat counter) = LoadCounting(bytes, JpegFormat.Instance);
        AssertBandsAndRandoms(image, whole, rounds: 2);

        // One decode for the first read (it has no way to know more reads follow) and one that fills the cache; a rotated image
        // misses once more before its first pass keeps its rows. After that every read is a copy.
        Assert.True(counter.Decodes <= 2, $"{name}: {counter.Decodes} decodes");
    }

    [Fact]
    public void PageJpegRotatedWithoutTheCacheRestartsPasses()
    {
        // The comparison that shows what the cache buys: the same rotated reads with the cache off.
        byte[] bytes = PageJpeg(2480, 3508, progressive: false, orientation: 6);
        Gray8[] whole = Image.Load(bytes).ToArray<Gray8>();
        ImageEngine.RegionCacheBytes = 0;
        (Image image, CountingFormat counter) = LoadCounting(bytes, JpegFormat.Instance);
        AssertBandsAndRandoms(image, whole, rounds: 1);
        TestContext.Current.SendDiagnosticMessage($"cache off, rotated page: {counter.Decodes} decodes");
        Assert.True(counter.Decodes >= 2);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 6)]
    public void StreamedSmallImagesReadBackIdentically(bool progressive, int orientation)
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        byte[] bytes = PageJpeg(301, 517, progressive, orientation);
        Gray8[] whole = Image.Load(bytes).ToArray<Gray8>();
        (Image image, CountingFormat counter) = LoadCounting(bytes, JpegFormat.Instance);
        AssertBandsAndRandoms(image, whole, rounds: 3);
        Assert.True(counter.Decodes <= 2, $"{counter.Decodes} decodes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PngBandsAndRandomReadsDecodeAtMostTwice(bool interlaced)
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        byte[] bytes = PagePng(333, 619, interlaced);
        Gray8[] whole = Image.Load(bytes).ToArray<Gray8>();
        (Image image, CountingFormat counter) = LoadCounting(bytes, PngFormat.Instance);
        AssertBandsAndRandoms(image, whole, rounds: 2);
        Assert.True(counter.Decodes <= 2, $"{counter.Decodes} decodes");
    }

    [Fact]
    public void ReadingAnotherFormatSwitchesToTheFrameFormatOnce()
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        byte[] bytes = PageJpeg(301, 517, progressive: false, orientation: 1);
        Gray8[] wholeGray = Image.Load(bytes).ToArray<Gray8>();
        Rgb24[] wholeRgb = Image.Load(bytes).ToArray<Rgb24>();
        (Image image, CountingFormat counter) = LoadCounting(bytes, JpegFormat.Instance);
        var rnd = new Random(2);
        for (int i = 0; i < 40; i++)
        {
            int w = rnd.Next(1, 300);
            int h = rnd.Next(1, 400);
            var r = new ImageRect(rnd.Next(0, 301 - w), rnd.Next(0, 517 - h), w, h);
            if (i % 2 == 0)
            {
                Assert.True(Expected(wholeGray, 301, r).AsSpan().SequenceEqual(Gray(image, r)));
            }
            else
            {
                var got = new Rgb24[w * h];
                image.CopyPixelsTo<Rgb24>(got, r);
                for (int y = 0; y < h; y++)
                {
                    Assert.True(wholeRgb.AsSpan(((r.Y + y) * 301) + r.X, w).SequenceEqual(got.AsSpan(y * w, w)));
                }
            }
        }

        // First read, the gray plane, then the frame-format plane that serves both.
        Assert.True(counter.Decodes <= 3, $"{counter.Decodes} decodes");
    }

    [Fact]
    public void AFrameBiggerThanTheBudgetKeepsNothing()
    {
        byte[] bytes = PageJpeg(2480, 3508, progressive: false, orientation: 1);
        ImageEngine.RegionCacheBytes = 4L << 20;
        (Image image, CountingFormat counter) = LoadCounting(bytes, JpegFormat.Instance);
        long baseline = LiveHeap.Total(collect: true);
        var r = new ImageRect(0, 3000, 2480, 100);
        Gray(image, r);
        Gray(image, new ImageRect(0, 3200, 2480, 100));
        long held = LiveHeap.Total(collect: true) - baseline;
        TestContext.Current.SendDiagnosticMessage($"budget 4 MiB: {held / 1024} KiB held after two reads near the bottom, {counter.Decodes} decodes");
        Assert.True(held < 12L << 20, $"{held} bytes held");
    }

    [Fact]
    public void AFirstRegionReadKeepsOnlyTheSlidingWindow()
    {
        byte[] bytes = PageJpeg(2480, 3508, progressive: false, orientation: 1);
        (Image image, CountingFormat counter) = LoadCounting(bytes, JpegFormat.Instance);
        long baseline = LiveHeap.Total(collect: true);
        Gray(image, new ImageRect(100, 3300, 600, 100));
        long held = LiveHeap.Total(collect: true) - baseline;
        TestContext.Current.SendDiagnosticMessage($"one crop near the bottom of a 26 MB frame: {held / 1024} KiB held");
        Assert.Equal(1, counter.Decodes);
        Assert.True(held < 12L << 20, $"{held} bytes held");
        GC.KeepAlive(image);
    }

    [Fact]
    public void TheCacheHoldsAboutOnePlaneAndReleaseGivesItBack()
    {
        byte[] bytes = PageJpeg(2480, 3508, progressive: false, orientation: 1);
        (Image image, CountingFormat counter) = LoadCounting(bytes, JpegFormat.Instance);
        long baseline = LiveHeap.Total(collect: true);
        var all = new Gray8[2480 * 3508];
        foreach (ImageRect r in RegionPatterns.Bands(2480, 3508, 80))
        {
            image.CopyPixelsTo<Gray8>(all.AsSpan(0, r.Width * r.Height), r);
        }

        long held = LiveHeap.Total(collect: true) - baseline - all.Length;
        TestContext.Current.SendDiagnosticMessage($"after 44 gray bands of a 26 MB frame: {held / 1024} KiB held (a gray plane is {2480 * 3508 / 1024} KiB)");
        Assert.True(held < 14L << 20, $"{held} bytes held");
        int decodes = counter.Decodes;

        image.ReleaseDecodedPixels();
        long afterRelease = LiveHeap.Total(collect: true) - baseline - all.Length;
        TestContext.Current.SendDiagnosticMessage($"after ReleaseDecodedPixels: {afterRelease / 1024} KiB");
        Assert.True(afterRelease < 4L << 20, $"{afterRelease} bytes still held");

        // The image is still valid: the same bands decode again and read the same.
        Gray8[] whole = Image.Load(bytes).ToArray<Gray8>();
        AssertBandsAndRandoms(image, whole, rounds: 1);
        Assert.True(counter.Decodes > decodes);
    }

    [Fact]
    public void ConcurrentReadersOfOneImageAgree()
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        byte[] bytes = PageJpeg(623, 801, progressive: false, orientation: 6);
        Gray8[] whole = Image.Load(bytes).ToArray<Gray8>();
        int w = 801;
        int h = 623;
        for (int iteration = 0; iteration < 6; iteration++)
        {
            Image image = Image.Load(bytes);
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
            {
                var rnd = new Random((iteration * 100) + worker);
                for (int i = 0; i < 25; i++)
                {
                    int rw = rnd.Next(1, w);
                    int rh = rnd.Next(1, h);
                    var r = new ImageRect(rnd.Next(0, w - rw + 1), rnd.Next(0, h - rh + 1), rw, rh);
                    Assert.True(Expected(whole, w, r).AsSpan().SequenceEqual(Gray(image, r)), $"worker {worker} {r}");
                }
            });
        }
    }

    [Fact]
    public void ACorruptTailFailsOnlyTheReadsThatNeedIt()
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        byte[] full = PageJpeg(301, 517, progressive: false, orientation: 1);
        byte[] cut = full.AsSpan(0, (full.Length * 2) / 3).ToArray();
        Gray8[] reference = Image.Load(cut, new LoadOptions()).Frames.Count == 1 ? TopRows(cut, 150) : [];
        Image image = Image.Load(cut);

        // Rows near the top are intact; reading them again and again keeps working, and the damaged tail keeps failing the same way.
        for (int round = 0; round < 3; round++)
        {
            var top = new ImageRect(0, 0, 301, 100);
            Assert.True(Expected(reference, 301, top).AsSpan().SequenceEqual(Gray(image, top)), $"round {round}");
            Assert.Throws<ImageFormatException>(() => Gray(image, new ImageRect(0, 400, 301, 100)));
        }
    }

    private static Gray8[] TopRows(byte[] jpeg, int rows)
    {
        var top = new Gray8[301 * rows];
        Image.Load(jpeg).CopyPixelsTo<Gray8>(top, new ImageRect(0, 0, 301, rows));
        return top;
    }

    private static byte[] PageJpeg(int w, int h, bool progressive, int orientation)
    {
        byte[] px = RegionInputs.Pixels(w, h, 3);
        Image image = Image.FromPixels<Rgb24>(MemoryMarshal.Cast<byte, Rgb24>(px), w, h);
        if (orientation != 1)
        {
            byte[] exif = [0x4D, 0x4D, 0, 0x2A, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0];
            image = image.WithMetadata(new ImageMetadata { Exif = exif, Orientation = orientation });
        }

        using var ms = new MemoryStream();
        image.Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 80, Progressive = progressive } });
        return ms.ToArray();
    }

    private static byte[] PagePng(int w, int h, bool interlaced)
    {
        // The identity tests' builders, through their input list.
        return RegionInputs.PngBytes(w, h, 3, interlaced);
    }
}
