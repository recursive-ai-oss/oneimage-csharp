namespace OneImage.Tests.Png;

public sealed class PngStreamingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneimage-pngstream-" + Guid.NewGuid().ToString("N"));

    public PngStreamingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class PeakSampler : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private long _peak;

        public PeakSampler()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Baseline = GC.GetTotalMemory(true);
            _peak = Baseline;
            _thread = new Thread(() =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    _peak = Math.Max(_peak, GC.GetTotalMemory(false));
                    Thread.Sleep(2);
                }
            });
            _thread.Start();
        }

        public long Baseline { get; }

        public long Growth
        {
            get
            {
                _stop.Cancel();
                _thread.Join();
                return _peak - Baseline;
            }
        }

        public void Dispose() => _stop.Dispose();
    }

    private sealed class CountingSink : IRowSink
    {
        private int _next;
        private ulong _hash;

        public CountingSink(int w, int h, PixelFormat f)
        {
            Width = w;
            Height = h;
            Format = f;
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format { get; }

        public int Rows => _next;

        public ulong Hash => _hash;

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
            Assert.Equal(_next, y);
            _next += rowCount;
            _hash = (_hash * 31) + rows[0] + rows[^1] + (ulong)rows.Length;
        }
    }

    private string WriteBigPng(int w, int h)
    {
        string path = Path.Combine(_dir, "big.png");
        int bpp = 3;
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            PngEncoder.EncodeStill(
                fs,
                (y0, n, dest) =>
                {
                    for (int r = 0; r < n; r++)
                    {
                        Span<byte> row = dest.Slice(r * w * bpp, w * bpp);
                        int y = y0 + r;
                        for (int x = 0; x < w; x++)
                        {
                            row[x * 3] = (byte)(x >> 3);
                            row[(x * 3) + 1] = (byte)(y >> 2);
                            row[(x * 3) + 2] = (byte)((x ^ y) & 0x3F);
                        }
                    }
                },
                w,
                h,
                PixelFormat.Rgb8,
                ImageMetadata.Empty,
                new PngEncoderOptions { CompressionLevel = 1, Filter = PngFilterMode.Sub, ColorMode = PngColorMode.Truecolor },
                CancellationToken.None);
        }

        return path;
    }

    [Fact]
    public void LargeNonInterlacedPngDecodesInMemoryProportionalToWidth()
    {
        const int W = 8000;
        const int H = 20000;
        string path = WriteBigPng(W, H);
        long frameBytes = (long)W * H * 3;
        using var fs = File.OpenRead(path);
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(fs, ImageLimits.Default);
        var sink = new CountingSink(W, H, info.Frames[0].Format);

        long growth;
        using (var sampler = new PeakSampler())
        {
            PngFormat.Instance.Decoder.DecodeFrame(fs, info, 0, new DecodeRequest(new ImageRect(0, 0, W, H)), sink, ImageLimits.Default, CancellationToken.None);
            growth = sampler.Growth;
        }

        TestContext.Current.SendDiagnosticMessage($"png decode {W}x{H} RGB8 ({frameBytes >> 20} MiB raw, file {new FileInfo(path).Length >> 20} MiB): peak managed growth {growth >> 10} KiB ({100.0 * growth / frameBytes:F3}% of the frame)");
        Assert.Equal(H, sink.Rows);
        Assert.True(growth < frameBytes / 10, $"peak growth {growth} exceeds 10% of {frameBytes}");
    }

    [Fact]
    public void LargePngStreamsThroughTheImagePipelineInBoundedMemory()
    {
        const int W = 8000;
        const int H = 20000;
        string path = WriteBigPng(W, H);
        long frameBytes = (long)W * H * 3;
        Image img = Image.Load(path).Invert();
        Assert.Equal((W, H), (img.Width, img.Height));
        long growth;
        using (var sampler = new PeakSampler())
        {
            img.Save(Stream.Null, PngFormat.Instance, new SaveOptions { EncoderOptions = new PngEncoderOptions { CompressionLevel = 0, Filter = PngFilterMode.None, ColorMode = PngColorMode.Truecolor } });
            growth = sampler.Growth;
        }

        TestContext.Current.SendDiagnosticMessage($"png load+invert+save {W}x{H}: peak managed growth {growth >> 20} MiB ({100.0 * growth / frameBytes:F2}% of the frame)");
        Assert.True(TileStores.Current is not PooledTileStore || growth < frameBytes / 6, $"peak growth {growth} exceeds a sixth of the {frameBytes} byte frame");
    }
}
