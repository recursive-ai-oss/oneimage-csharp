using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Normalization;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using IsMethod = SixLabors.ImageSharp.Processing.Processors.Normalization.HistogramEqualizationMethod;

namespace OneImage.Benchmarks.Equalize;

/// <summary>
/// Histogram equalization on in-memory RGBA pictures, codecs excluded: global and tile interpolation with clipping on 2048x1536
/// (3 MP), sliding window on 1152x864 (1 MP). oneimage builds a new lazy image per call (so every call counts the whole image and
/// builds its mappings) and copies into a caller buffer; ImageSharp mutates in place. Both get the same degree of parallelism.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 8)]
public class EqualizeBenchmarks : IDisposable
{
    private const int W3 = 2048;
    private const int H3 = 1536;
    private const int W1 = 1152;
    private const int H1 = 864;

    private OneImage.Image _ours3 = null!;
    private OneImage.Image _ours1 = null!;
    private byte[] _raw3 = [];
    private byte[] _raw1 = [];
    private IsImage _sharp3 = null!;
    private IsImage _sharp1 = null!;
    private OneImage.Rgba32[] _buffer3 = [];
    private OneImage.Rgba32[] _buffer1 = [];

    /// <summary>Degree of parallelism given to both libraries.</summary>
    [Params(1, 4)]
    public int Threads { get; set; }

    private static byte[] Photo(int w, int h)
    {
        var rnd = new Random(7);
        byte[] raw = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double u = x / (double)w;
                double v = y / (double)h;
                int i = ((y * w) + x) * 4;
                raw[i] = (byte)Math.Clamp(120 + (80 * Math.Sin((u * 5) + 1) * Math.Cos(v * 3)) + rnd.Next(-8, 9), 0, 255);
                raw[i + 1] = (byte)Math.Clamp(110 + (70 * Math.Sin((v * 4) + 2)) + rnd.Next(-8, 9), 0, 255);
                raw[i + 2] = (byte)Math.Clamp(100 + (90 * Math.Cos(((u * u) + (v * v)) * 6)) + rnd.Next(-8, 9), 0, 255);
                raw[i + 3] = 255;
            }
        }

        return raw;
    }

    private static OneImage.Image Ours(byte[] raw, int w, int h)
    {
        var px = new OneImage.Rgba32[w * h];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OneImage.Rgba32>(raw).CopyTo(px);
        return OneImage.Image.FromPixels<OneImage.Rgba32>(px, w, h);
    }

    [GlobalSetup]
    public void Setup()
    {
        _raw3 = Photo(W3, H3);
        _raw1 = Photo(W1, H1);
        _ours3 = Ours(_raw3, W3, H3);
        _ours1 = Ours(_raw1, W1, H1);
        _buffer3 = new OneImage.Rgba32[W3 * H3];
        _buffer1 = new OneImage.Rgba32[W1 * H1];
        _sharp3 = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(_raw3, W3, H3);
        _sharp1 = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(_raw1, W1, H1);
        _sharp3.Configuration.MaxDegreeOfParallelism = Threads;
        _sharp1.Configuration.MaxDegreeOfParallelism = Threads;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sharp3?.Dispose();
        _sharp1?.Dispose();
        GC.SuppressFinalize(this);
    }

    private int Pull3(OneImage.Image img)
    {
        img.CopyPixelsTo<OneImage.Rgba32>(_buffer3, new ImageRect(0, 0, W3, H3), maxDegree: Threads);
        return _buffer3[12345].R;
    }

    private int Pull1(OneImage.Image img)
    {
        img.CopyPixelsTo<OneImage.Rgba32>(_buffer1, new ImageRect(0, 0, W1, H1), maxDegree: Threads);
        return _buffer1[12345].R;
    }

    private static HistogramEqualizationOptions Opts(IsMethod m, bool clip) => new() { Method = m, ClipHistogram = clip };

    [Benchmark(Description = "global 3MP: oneimage")]
    public int GlobalOurs() => Pull3(_ours3.HistogramEqualization());

    [Benchmark(Description = "global 3MP: ImageSharp")]
    public int GlobalSharp()
    {
        _sharp3.Mutate(c => c.HistogramEqualization(Opts(IsMethod.Global, false)));
        return _sharp3[1, 1].R;
    }

    [Benchmark(Description = "tiles+clip 3MP: oneimage")]
    public int TilesOurs() => Pull3(_ours3.HistogramEqualization(HistogramEqualizationMethod.AdaptiveTileInterpolation, clipHistogram: true));

    [Benchmark(Description = "tiles+clip 3MP: ImageSharp")]
    public int TilesSharp()
    {
        _sharp3.Mutate(c => c.HistogramEqualization(Opts(IsMethod.AdaptiveTileInterpolation, true)));
        return _sharp3[1, 1].R;
    }

    [Benchmark(Description = "window 1MP: oneimage")]
    public int WindowOurs() => Pull1(_ours1.HistogramEqualization(HistogramEqualizationMethod.AdaptiveSlidingWindow));

    [Benchmark(Description = "window 1MP: ImageSharp")]
    public int WindowSharp()
    {
        _sharp1.Mutate(c => c.HistogramEqualization(Opts(IsMethod.AdaptiveSlidingWindow, false)));
        return _sharp1[1, 1].R;
    }

    [Benchmark(Description = "window+clip 1MP: oneimage")]
    public int WindowClipOurs() => Pull1(_ours1.HistogramEqualization(HistogramEqualizationMethod.AdaptiveSlidingWindow, clipHistogram: true));

    [Benchmark(Description = "window+clip 1MP: ImageSharp")]
    public int WindowClipSharp()
    {
        _sharp1.Mutate(c => c.HistogramEqualization(Opts(IsMethod.AdaptiveSlidingWindow, true)));
        return _sharp1[1, 1].R;
    }
}
