using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Normalization;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Dithering;

/// <summary>
/// Dithering, adaptive threshold and octree quantization on a 2000x1500 opaque RGBA photo held in memory.
/// oneimage builds a new lazy image per call and writes into a caller buffer; ImageSharp clones the image and runs the processor on
/// the clone (so it allocates the result, as a caller would). Both are given the same degree of parallelism; error diffusion is
/// sequential in both. The sliding-window rows use a 1000x750 crop of the photo because that method costs the window area per pixel.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 8)]
public class DitherHistogramBenchmarks : IDisposable
{
    private const int W = 2000;
    private const int H = 1500;
    private OneImage.Image _ours = null!;
    private IsImage _sharp = null!;
    private OneImage.Rgba32[] _buffer = [];
    private OneImage.Rgba32[] _pal = [];
    private SixLabors.ImageSharp.Color[] _isPal = [];

    /// <summary>Degree of parallelism given to both libraries.</summary>
    [Params(1, 4)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var px = new OneImage.Rgba32[W * H];
        var rnd = new Random(42);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                double n = Math.Sin(x * 0.013) * 40 + Math.Cos(y * 0.021) * 40 + rnd.Next(24);
                px[(y * W) + x] = new OneImage.Rgba32(
                    (byte)Math.Clamp((x * 200L / W) + n + 20, 0, 255),
                    (byte)Math.Clamp((y * 200L / H) + n + 20, 0, 255),
                    (byte)Math.Clamp((((x + y) * 100L) / (W + H)) + (n * 1.5) + 60, 0, 255),
                    255);
            }
        }

        _ours = OneImage.Image.FromPixels<OneImage.Rgba32>(px, W, H);
        _sharp = new IsImage(W, H);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                OneImage.Rgba32 p = px[(y * W) + x];
                _sharp[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32(p.R, p.G, p.B, p.A);
            }
        }

        _buffer = new OneImage.Rgba32[W * H];
        _pal = [.. KnownPalettes.WebSafe];
        _isPal = [.. SixLabors.ImageSharp.Color.WebSafePalette.ToArray()];
        _sharp.Configuration.MaxDegreeOfParallelism = Threads;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sharp?.Dispose();
        GC.SuppressFinalize(this);
    }

    private int Pull(OneImage.Image img, int w = W, int h = H)
    {
        img.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, w, h), maxDegree: Threads);
        return _buffer[12345].R;
    }

    private static int Run(IsImage src, Action<IImageProcessingContext> op)
    {
        using IsImage c = src.Clone(op);
        return c[12, 34].R;
    }

    [Benchmark(Description = "dither Floyd-Steinberg, web palette: oneimage")]
    public int FsOurs() => Pull(_ours.Dither(_pal, Dither.FloydSteinberg));

    [Benchmark(Description = "dither Floyd-Steinberg, web palette: oneimage fastMatch")]
    public int FsOursFast() => Pull(_ours.Dither(_pal, Dither.FloydSteinberg, fastMatch: true));

    [Benchmark(Description = "dither Floyd-Steinberg, web palette: ImageSharp")]
    public int FsSharp() => Run(_sharp, c => c.Dither(KnownDitherings.FloydSteinberg, _isPal));

    [Benchmark(Description = "dither Stucki, web palette: oneimage")]
    public int StuckiOurs() => Pull(_ours.Dither(_pal, Dither.Stucki));

    [Benchmark(Description = "dither Stucki, web palette: ImageSharp")]
    public int StuckiSharp() => Run(_sharp, c => c.Dither(KnownDitherings.Stucki, _isPal));

    [Benchmark(Description = "dither Bayer 8x8, web palette: oneimage")]
    public int BayerOurs() => Pull(_ours.Dither(_pal, Dither.Bayer8x8));

    [Benchmark(Description = "dither Bayer 8x8, web palette: oneimage fastMatch")]
    public int BayerOursFast() => Pull(_ours.Dither(_pal, Dither.Bayer8x8, fastMatch: true));

    [Benchmark(Description = "dither Bayer 8x8, web palette: ImageSharp")]
    public int BayerSharp() => Run(_sharp, c => c.Dither(KnownDitherings.Bayer8x8, _isPal));

    [Benchmark(Description = "binary dither Bayer 4x4: oneimage")]
    public int BinaryOurs() => Pull(_ours.BinaryDither(Dither.Bayer4x4));

    [Benchmark(Description = "binary dither Bayer 4x4: ImageSharp")]
    public int BinarySharp() => Run(_sharp, c => c.BinaryDither(KnownDitherings.Bayer4x4));

    [Benchmark(Description = "octree quantize 256, no dither: oneimage")]
    public int OctreeOurs() => Pull(_ours.Quantize(256, Dither.None, quantizer: Quantizer.Octree));

    [Benchmark(Description = "octree quantize 256, no dither: ImageSharp")]
    public int OctreeSharp() => Run(_sharp, c => c.Quantize(new OctreeQuantizer(new QuantizerOptions { Dither = null })));

    [Benchmark(Description = "auto level: oneimage")]
    public int AutoLevelOurs() => Pull(_ours.AutoLevel());

    [Benchmark(Description = "auto level: ImageSharp")]
    public int AutoLevelSharp() => Run(_sharp, c => c.HistogramEqualization(new HistogramEqualizationOptions { Method = SixLabors.ImageSharp.Processing.Processors.Normalization.HistogramEqualizationMethod.AutoLevel }));

    [Benchmark(Description = "adaptive threshold: oneimage")]
    public int ThresholdOurs() => Pull(_ours.AdaptiveThreshold());

    [Benchmark(Description = "adaptive threshold: ImageSharp")]
    public int ThresholdSharp() => Run(_sharp, c => c.AdaptiveThreshold());
}
