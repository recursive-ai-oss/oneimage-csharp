using System.Diagnostics;

namespace OneImage.Tests;

[Collection(MemoryMeasurement.Name)]
public sealed class StreamingTests : IDisposable
{
    private readonly long _savedThreshold = DecodeSourceNode.StreamingMinBytes;
    private readonly long _savedRegionCache = ImageEngine.RegionCacheBytes;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneimage-stream-" + Guid.NewGuid().ToString("N"));

    public StreamingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        DecodeSourceNode.StreamingMinBytes = _savedThreshold;
        ImageEngine.RegionCacheBytes = _savedRegionCache;
        Directory.Delete(_dir, recursive: true);
    }

    private static Image LoadGen(int w, int h) => Image.Load(GenFormat.Header(w, h), new LoadOptions { Configuration = GenFormat.Instance.Configuration() });

    private static void SaveChecked(Image img, GenCheck check) =>
        img.Save(Stream.Null, GenFormat.Instance, new SaveOptions { EncoderOptions = check });

    [Fact]
    public void LargeSequentialImageStreamsInBoundedMemory()
    {
        const int W = 8000;
        const int H = 30000;
        long frameBytes = (long)W * H * 4;
        Assert.True(frameBytes >= DecodeSourceNode.StreamingMinBytes);

        Image img = LoadGen(W, H).Crop(100, 200, 7000, 29000).Invert();
        var check = new GenCheck(7000) { OffsetX = 100, OffsetY = 200, Inverted = true };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, LiveHeap.Read(baseline, frameBytes / 10));
                Thread.Sleep(2);
            }
        });
        sampler.Start();
        var sw = Stopwatch.StartNew();
        try
        {
            SaveChecked(img, check);
        }
        finally
        {
            stop.Cancel();
            sampler.Join();
        }

        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        long growth = peak - baseline;
        TestContext.Current.SendDiagnosticMessage($"streaming {W}x{H} Rgba32 ({frameBytes / (1 << 20)} MiB): peak managed heap growth {growth / (1 << 20)} MiB ({100.0 * growth / frameBytes:F1}% of frame), total allocated {allocated / (1 << 20)} MiB, {sw.ElapsedMilliseconds} ms");

        Assert.Equal(29000, check.RowsSeen);
        Assert.Equal(0, check.Mismatches);
        // A store that allocates a fresh array per tile (the checking store) leaves garbage the pooled store avoids; the bound is for the pooled one.
        Assert.True(TileStores.Current is not PooledTileStore || growth < frameBytes / 10, $"peak growth {growth} bytes exceeds 10% of the frame ({frameBytes})");
    }

    [Fact]
    public void ForwardPassDecodesOnceAndMatchesMaterializedResult()
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        var raw = new RawFormat();
        var rnd = new Random(3);
        Rgba32[] px = Enumerable.Range(0, 500 * 700).Select(_ => new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))).ToArray();
        using var ms = new MemoryStream();
        Image.FromPixels<Rgba32>(px, 500, 700).Save(ms, raw);

        Image img = Image.Load(ms.ToArray(), new LoadOptions { Configuration = raw.Configuration() });
        Assert.True(img.Frames[0].Node.Streaming);
        Rgba32[] first = img.Crop(3, 5, 400, 600).Invert().ToArray<Rgba32>(maxDegree: 4);
        Assert.Equal(1, raw.DecodeCalls);
        Rgba32[] expected = Image.FromPixels<Rgba32>(px, 500, 700).Crop(3, 5, 400, 600).Invert().ToArray<Rgba32>();
        Assert.Equal(expected, first);
    }

    [Fact]
    public void GoingBackRestartsThenFallsBackToMaterializing()
    {
        // The node's own policy, which the region cache (on by default for reads through CopyPixelsTo) replaces: see RegionCacheTests.
        ImageEngine.RegionCacheBytes = 0;
        DecodeSourceNode.StreamingMinBytes = 0;
        var raw = new RawFormat();
        Rgba32[] px = Enumerable.Range(0, 200 * 1500).Select(i => new Rgba32((byte)i, (byte)(i >> 8), (byte)(i >> 16), 255)).ToArray();
        using var ms = new MemoryStream();
        Image.FromPixels<Rgba32>(px, 200, 1500).Save(ms, raw);
        Image img = Image.Load(ms.ToArray(), new LoadOptions { Configuration = raw.Configuration() });

        Rgba32[] Row(int tileRow)
        {
            Rgba32[] r = new Rgba32[200];
            img.CopyPixelsTo<Rgba32>(r, new ImageRect(0, tileRow * 128, 200, 1), maxDegree: 1);
            return r;
        }

        // Walk to the bottom, then go back to the top over and over: each return needs a new pass until restarts run out.
        for (int round = 0; round < 5; round++)
        {
            for (int tr = 0; tr < 11; tr++)
            {
                Assert.Equal(px.AsSpan(tr * 128 * 200, 200).ToArray(), Row(tr));
            }
        }

        Assert.Equal(1 + DecodeSourceNode.MaxRestarts + 1, raw.DecodeCalls);
        Assert.Equal(px.AsSpan(1280 * 200, 200).ToArray(), Row(10));
    }

    [Fact]
    public void DecoderErrorsReachTheConsumer()
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        var raw = new RawFormat();
        using var ms = new MemoryStream();
        Image.FromPixels<Rgba32>(new Rgba32[100 * 600], 100, 600).Save(ms, raw);
        byte[] cut = ms.ToArray().AsSpan(0, (int)ms.Length - 100 * 4 * 150).ToArray();
        Image img = Image.Load(cut, new LoadOptions { Configuration = raw.Configuration() });
        Assert.Throws<ImageFormatException>(() => img.ToArray<Rgba32>());

        // The rows before the damage are still readable.
        Rgba32[] head = new Rgba32[100];
        img.CopyPixelsTo<Rgba32>(head, new ImageRect(0, 0, 100, 1));
    }

    [Fact]
    public void ChangedFileIsDetectedByTheDecoderThread()
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        var raw = new RawFormat();
        string path = Path.Combine(_dir, "s.raw");
        Image.FromPixels<Rgba32>(new Rgba32[64 * 300], 64, 300).Save(path, new SaveOptions { Configuration = raw.Configuration() });
        Image img = Image.Load(path, new LoadOptions { Configuration = raw.Configuration() });
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(9));
        Assert.Throws<ImageSourceChangedException>(() => img.ToArray<Rgba32>());
    }

    [Fact]
    public void CancellationWhileWaitingForTheDecoder()
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Image img = LoadGen(300, 600);
        Assert.Throws<OperationCanceledException>(() => img.ToArray<Rgba32>(0, cts.Token));
        Assert.Equal(PixelFormat.Rgba8, img.Format);
    }

    [Fact]
    public void AbandonedImageStopsItsDecoderThread()
    {
        DecodeSourceNode.StreamingMinBytes = 0;
        int before = StreamingSession.LiveThreads;
        static void Touch()
        {
            Image img = LoadGen(300, 3000);
            var one = new Rgba32[1];
            img.CopyPixelsTo<Rgba32>(one, new ImageRect(0, 0, 1, 1));
            Assert.Equal(GenFormat.Pixel(0, 0), one[0]);
        }

        Touch();
        var sw = Stopwatch.StartNew();
        while (StreamingSession.LiveThreads > before && sw.ElapsedMilliseconds < 10000)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(20);
        }

        Assert.True(StreamingSession.LiveThreads <= before, "the abandoned decoder thread must stop");
    }

    [Fact]
    public void SmallImagesDoNotStream()
    {
        var raw = new RawFormat();
        using var ms = new MemoryStream();
        Image.FromPixels<Rgba32>(new Rgba32[100 * 100], 100, 100).Save(ms, raw);
        Image img = Image.Load(ms.ToArray(), new LoadOptions { Configuration = raw.Configuration() });
        Assert.False(img.Frames[0].Node.Streaming);
        int before = StreamingSession.LiveThreads;
        img.ToArray<Rgba32>();
        Assert.True(StreamingSession.LiveThreads <= before);
    }

    [Fact]
    public void RandomAccessDecoderOpensTheFileOnce()
    {
        var raw = new RawFormat(DecoderCapabilities.RandomAccess);
        string path = Path.Combine(_dir, "ra.raw");
        var rnd = new Random(1);
        Rgba32[] px = Enumerable.Range(0, 700 * 500).Select(_ => new Rgba32((byte)rnd.Next(256), 1, 2, 3)).ToArray();
        Image.FromPixels<Rgba32>(px, 700, 500).Save(path, new SaveOptions { Configuration = raw.Configuration() });

        long opens = FileImageSource.OpenCount;
        Image img = Image.Load(path, new LoadOptions { Configuration = raw.Configuration() });
        Rgba32[] all = img.ToArray<Rgba32>(maxDegree: 4);
        Rgba32[] part = img.Crop(130, 130, 300, 300).ToArray<Rgba32>();
        Assert.Equal(px, all);
        Assert.Equal(px[(130 * 700) + 130], part[0]);
        Assert.True(raw.DecodeCalls >= 24, "tiles are decoded one by one");
        Assert.Equal(opens + 1, FileImageSource.OpenCount);
    }
}
