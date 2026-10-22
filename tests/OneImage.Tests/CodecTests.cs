using System.Buffers;

namespace OneImage.Tests;

public sealed class CodecTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneimage-tests-" + Guid.NewGuid().ToString("N"));

    public CodecTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Image Sample(int w = 300, int h = 200)
    {
        var rnd = new Random(5);
        Rgba32[] px = Enumerable.Range(0, w * h).Select(_ => new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))).ToArray();
        return Image.FromPixels<Rgba32>(px, w, h);
    }

    private static LoadOptions Opts(RawFormat f, bool validate = false, ImageLimits? limits = null) =>
        new() { Configuration = f.Configuration(), Validate = validate, Limits = limits };

    [Fact]
    public void RoundTripsThroughEverySourceAndSink()
    {
        var raw = new RawFormat();
        Image img = Sample();
        Rgba32[] expected = img.ToArray<Rgba32>();
        var so = new SaveOptions { Configuration = raw.Configuration() };

        string path = Path.Combine(_dir, "a.raw");
        img.Save(path, so);
        using var ms = new MemoryStream();
        img.Save(ms, raw);
        var writer = new ArrayBufferWriter<byte>();
        img.Save(writer, raw);
        byte[] file = File.ReadAllBytes(path);
        Assert.Equal(file, ms.ToArray());
        Assert.Equal(file, writer.WrittenSpan.ToArray());

        Assert.Equal(expected, Image.Load(path, Opts(raw)).ToArray<Rgba32>());
        ms.Position = 0;
        Assert.Equal(expected, Image.Load(ms, Opts(raw)).ToArray<Rgba32>());
        Assert.Equal(expected, Image.Load(file, Opts(raw)).ToArray<Rgba32>());
        Assert.Equal(file.Length, ms.Length);
    }

    [Fact]
    public void AsyncVariantsWork()
    {
        var raw = new RawFormat();
        Image img = Sample();
        string path = Path.Combine(_dir, "async.raw");
        img.SaveAsync(path, new SaveOptions { Configuration = raw.Configuration() }).GetAwaiter().GetResult();
        Image loaded = Image.LoadAsync(path, Opts(raw)).GetAwaiter().GetResult();
        Assert.Equal(img.ToArray<Rgba32>(), loaded.ToArray<Rgba32>());

        using var ms = new MemoryStream();
        img.SaveAsync(ms, raw).GetAwaiter().GetResult();
        ms.Position = 0;
        ImageInfo info = Image.IdentifyAsync(ms, Opts(raw)).GetAwaiter().GetResult();
        Assert.Equal(300, info.Width);
        ms.Position = 0;
        Assert.Equal(200, Image.LoadAsync(ms, Opts(raw)).GetAwaiter().GetResult().Height);
        Assert.Equal(300, Image.IdentifyAsync(path, Opts(raw)).GetAwaiter().GetResult().Width);
    }

    [Fact]
    public void ChangedFileIsDetectedOnPixelPull()
    {
        var raw = new RawFormat();
        string path = Path.Combine(_dir, "c.raw");
        Sample(64, 64).Save(path, new SaveOptions { Configuration = raw.Configuration() });
        Image img = Image.Load(path, Opts(raw));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        Assert.Throws<ImageSourceChangedException>(() => img.ToArray<Rgba32>());

        string path2 = Path.Combine(_dir, "d.raw");
        Sample(64, 64).Save(path2, new SaveOptions { Configuration = raw.Configuration() });
        Image img2 = Image.Load(path2, Opts(raw));
        using (var fs = new FileStream(path2, FileMode.Append))
        {
            fs.WriteByte(0);
        }

        Assert.Throws<ImageSourceChangedException>(() => img2.ToArray<Rgba32>());
    }

    [Fact]
    public void TryLoadTryIdentifyAndValidate()
    {
        var raw = new RawFormat();
        Assert.False(Image.TryLoad(new byte[] { 1, 2, 3, 4, 5 }, out Image? none, Opts(raw)));
        Assert.Null(none);
        Assert.False(Image.TryIdentify(new byte[] { 1, 2, 3 }, out _, Opts(raw)));

        using var ms = new MemoryStream();
        Sample(100, 100).Save(ms, raw);
        byte[] good = ms.ToArray();
        Assert.True(Image.TryIdentify(good, out ImageInfo? info, Opts(raw)));
        Assert.Equal(100, info!.Width);
        Assert.True(Image.TryLoad(good, out Image? ok, Opts(raw)));
        Assert.NotNull(ok);

        byte[] truncated = good.AsSpan(0, good.Length - 500).ToArray();
        Image lazy = Image.Load(truncated, Opts(raw));
        Assert.Throws<ImageFormatException>(() => lazy.ToArray<Rgba32>());
        Assert.Throws<ImageFormatException>(() => Image.Load(truncated, Opts(raw, validate: true)));
        Assert.False(Image.TryLoad(truncated, out _, Opts(raw, validate: true)));
    }

    [Fact]
    public void ConfigurationWithoutTheCodecCannotLoadIt()
    {
        var raw = new RawFormat();
        using var ms = new MemoryStream();
        Sample(10, 10).Save(ms, raw);
        var empty = new LoadOptions { Configuration = new ImageConfiguration() };
        Assert.Throws<ImageFormatException>(() => Image.Load(ms.ToArray(), empty));
        Assert.Throws<ImageFormatException>(() => Image.Load(ms.ToArray()));
        Assert.Throws<ImageOperationException>(() => Sample(2, 2).Save(Path.Combine(_dir, "x.raw")));
    }

    [Fact]
    public void OverLimitHeaderThrowsBeforeAnyPixelBufferIsAllocated()
    {
        foreach (bool decoderChecks in new[] { true, false })
        {
            var raw = new RawFormat(checkLimitsInDecoder: decoderChecks);
            long rented = Tile.RentedCount;
            Assert.Throws<ImageLimitException>(() => Image.Load(RawFormat.HeaderOnly(60000, 60000), Opts(raw)));
            Assert.Throws<ImageLimitException>(() => Image.Load(RawFormat.HeaderOnly(70000, 2), Opts(raw)));
            Assert.Throws<ImageLimitException>(() => Image.Load(RawFormat.HeaderOnly(2, 2, frames: 5000), Opts(raw)));
            Assert.Throws<ImageLimitException>(() => Image.Identify(RawFormat.HeaderOnly(60000, 60000), Opts(raw)));
            Assert.Equal(rented, Tile.RentedCount);
        }
    }

    [Fact]
    public void PerCallLimitsAndUnrestrictedOverrideTheDefaults()
    {
        var raw = new RawFormat();
        byte[] big = RawFormat.HeaderOnly(20000, 20000);
        Assert.Throws<ImageLimitException>(() => Image.Load(big, Opts(raw)));
        Image img = Image.Load(big, Opts(raw, limits: ImageLimits.Unrestricted));
        Assert.Equal(20000, img.Width);

        byte[] small = RawFormat.HeaderOnly(16, 16);
        var strict = new ImageLimits { MaxPixelsPerFrame = 100 };
        Assert.Throws<ImageLimitException>(() => Image.Load(small, Opts(raw, limits: strict)));
        Assert.Equal(16, Image.Load(small, Opts(raw)).Width);

        // The configuration-wide limits apply when the call passes none.
        var cfg = raw.Configuration();
        cfg.Limits = strict;
        Assert.Throws<ImageLimitException>(() => Image.Load(small, new LoadOptions { Configuration = cfg }));
    }

    [Fact]
    public void AnimationFramesDelaysAndLoopSurviveOpsAndSave()
    {
        var raw = new RawFormat();
        byte[] file = BuildAnimation(40, 30, 3);
        Image anim = Image.Load(file, Opts(raw));
        Assert.Equal(3, anim.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(20), anim.Frames[1].Delay);
        Assert.Equal(7, anim.LoopCount);

        Image edited = anim.Crop(5, 5, 20, 10).Invert();
        Assert.Equal(3, edited.FrameCount);
        Assert.Equal(20, edited.Width);
        Assert.Equal(TimeSpan.FromMilliseconds(30), edited.Frames[2].Delay);
        Assert.Equal(7, edited.LoopCount);
        Assert.Equal(255 - 2, edited.Frames[2].ToArray<Gray8>()[0].V);

        var outMs = new MemoryStream();
        edited.Save(outMs, raw);
        Image back = Image.Load(outMs.ToArray(), Opts(raw));
        Assert.Equal(3, back.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(30), back.Frames[2].Delay);
        Assert.Equal(edited.Frames[1].ToArray<Gray8>(), back.Frames[1].ToArray<Gray8>());
    }

    private static byte[] BuildAnimation(int w, int h, int frames)
    {
        byte[] head = RawFormat.HeaderOnly(w, h, frames);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(17), 7);
        using var ms = new MemoryStream();
        ms.Write(head);
        for (int f = 0; f < frames; f++)
        {
            ms.Write(BitConverter.GetBytes((f + 1) * 10));
            ms.Write(Enumerable.Repeat((byte)f, w * h).ToArray());
        }

        return ms.ToArray();
    }

    [Fact]
    public void CancellationStopsSaveAndValidate()
    {
        var raw = new RawFormat();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Image img = Sample();
        Assert.Throws<OperationCanceledException>(() => img.Save(new MemoryStream(), raw, null, cts.Token));
        string path = Path.Combine(_dir, "cancel.raw");
        Assert.Throws<OperationCanceledException>(() => img.Save(path, new SaveOptions { Configuration = raw.Configuration() }, cts.Token));
        Assert.False(File.Exists(path), "a failed save must not leave a partial file");
        using var ms = new MemoryStream();
        img.Save(ms, raw);
        Assert.Throws<OperationCanceledException>(() => Image.Load(ms.ToArray(), Opts(raw, validate: true), cts.Token));
    }

    [Fact]
    public void SequentialDecoderRunsOnceAndFeedsTilesThroughTheLineBuffer()
    {
        var raw = new RawFormat();
        using var ms = new MemoryStream();
        Sample(300, 270).Save(ms, raw);
        Image img = Image.Load(ms.ToArray(), Opts(raw));
        // The partial read materializes the frame; the later whole-frame read then reuses it (a whole-frame read made first would
        // decode straight into the caller's buffer instead and keep nothing).
        Rgba32[] b = img.Crop(130, 130, 50, 50).ToArray<Rgba32>();
        Rgba32[] a = img.ToArray<Rgba32>(maxDegree: 4);
        Assert.Equal(1, raw.DecodeCalls);
        Assert.Equal(a[(130 * 300) + 130], b[0]);
        Assert.Equal(Sample(300, 270).ToArray<Rgba32>(), a);
    }

    [Fact]
    public void RandomAccessDecoderIsAskedForOneTileAtATime()
    {
        var raw = new RawFormat(DecoderCapabilities.RandomAccess);
        using var ms = new MemoryStream();
        Sample(600, 600).Save(ms, raw);
        Image img = Image.Load(ms.ToArray(), Opts(raw));
        var px = new Rgba32[100];
        img.CopyPixelsTo<Rgba32>(px, new ImageRect(130, 130, 10, 10));
        Assert.Equal(Sample(600, 600).Crop(130, 130, 10, 10).ToArray<Rgba32>(), px);
        Assert.Equal(1, raw.DecodeCalls);
        Assert.Equal(new ImageRect(128, 128, 128, 128), raw.LastRequest.Region);
    }

    [Fact]
    public void CropIsPushedDownToDecodersThatSupportRegions()
    {
        var raw = new RawFormat(DecoderCapabilities.Region);
        using var ms = new MemoryStream();
        Sample(600, 600).Save(ms, raw);
        Image img = Image.Load(ms.ToArray(), Opts(raw));
        Image cropped = img.Crop(100, 200, 64, 32).Crop(4, 4, 20, 10);
        Assert.StartsWith("decode(", cropped.DescribeGraph(), StringComparison.Ordinal);
        Rgba32[] px = cropped.ToArray<Rgba32>();
        Assert.Equal(new ImageRect(104, 204, 20, 10), raw.LastRequest.Region);
        Assert.Equal(Sample(600, 600).Crop(104, 204, 20, 10).ToArray<Rgba32>(), px);
    }

    [Fact]
    public void DecoderBugsSurfaceAsOperationErrors()
    {
        var sink = new StripToTileBuffer(new TileGrid(10, 10, PixelFormat.Gray8, null));
        Assert.Throws<ImageOperationException>(() => sink.WriteRows(3, new byte[10], 1));
        Assert.Throws<ImageOperationException>(() => sink.WriteRows(0, new byte[5], 1));
        sink.Dispose();
    }
}
