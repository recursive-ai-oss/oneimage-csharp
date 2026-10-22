using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Convolution;
using IsColor = SixLabors.ImageSharp.Color;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using IsRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace OneImage.Benchmarks.Filter;

/// <summary>
/// The neighbourhood effects against ImageSharp 3.1.12 on 1600x1200 RGBA pixels already in memory. Both libraries produce the
/// whole result in memory. Ours runs at its default of min(4, cores) threads; ImageSharp is limited to the same 4 threads unless the
/// environment variable ONEIMAGE_IS_DEGREE says otherwise (0 = all cores).
/// Oil paint uses brush 15 here, which is a wider window in this library than in ImageSharp (see the OilPaint docs), and
/// edge detection rows use the gray default.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class EffectsBenchmarks : IDisposable
{
    private const int W = 1600;
    private const int H = 1200;
    private OneImage.Image _ours = null!;
    private IsImage _sharp = null!;
    private OneImage.Rgba32[] _buffer = [];

    [GlobalSetup]
    public void Setup()
    {
        byte[] raw = new byte[W * H * 4];
        var rnd = new Random(42);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 4;
                raw[i] = (byte)((x * 255L / W) + rnd.Next(16));
                raw[i + 1] = (byte)((y * 255L / H) + rnd.Next(16));
                raw[i + 2] = (byte)(((x + y) & 255) + rnd.Next(8));
                raw[i + 3] = 255;
            }
        }

        SixLabors.ImageSharp.Configuration config = SixLabors.ImageSharp.Configuration.Default.Clone();
        int degree = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_IS_DEGREE"), out int d) ? d : 4;
        if (degree > 0)
        {
            config.MaxDegreeOfParallelism = degree;
        }

        _sharp = SixLabors.ImageSharp.Image.LoadPixelData<IsRgba32>(config, raw, W, H);
        _ours = OneImage.Image.FromPixels<OneImage.Rgba32>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OneImage.Rgba32>(raw), W, H);
        _buffer = new OneImage.Rgba32[W * H];
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sharp.Dispose();
        GC.SuppressFinalize(this);
    }

    private int Run(OneImage.Image image)
    {
        image.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, W, H));
        return _buffer[0].R;
    }

    private int Run(Action<IImageProcessingContext> op)
    {
        using IsImage r = _sharp.Clone(op);
        return r.Width;
    }

    [Benchmark]
    [BenchmarkCategory("pixelate")]
    public int PixelateOurs() => Run(_ours.Pixelate(8));

    [Benchmark]
    [BenchmarkCategory("pixelate")]
    public int PixelateImageSharp() => Run(c => c.Pixelate(8));

    [Benchmark]
    [BenchmarkCategory("oil")]
    public int OilPaintOurs() => Run(_ours.OilPaint(10, 15));

    [Benchmark]
    [BenchmarkCategory("oil")]
    public int OilPaintImageSharp() => Run(c => c.OilPaint(10, 15));

    [Benchmark]
    [BenchmarkCategory("vignette")]
    public int VignetteOurs() => Run(_ours.Vignette());

    [Benchmark]
    [BenchmarkCategory("vignette")]
    public int VignetteImageSharp() => Run(c => c.Vignette(IsColor.Black));

    [Benchmark]
    [BenchmarkCategory("glow")]
    public int GlowOurs() => Run(_ours.Glow());

    [Benchmark]
    [BenchmarkCategory("glow")]
    public int GlowImageSharp() => Run(c => c.Glow(IsColor.Black));

    [Benchmark]
    [BenchmarkCategory("bokeh")]
    public int BokehOurs() => Run(_ours.BokehBlur(32, 2, 3));

    [Benchmark]
    [BenchmarkCategory("bokeh")]
    public int BokehImageSharp() => Run(c => c.BokehBlur(32, 2, 3f));

    [Benchmark]
    [BenchmarkCategory("gaussian-sharpen")]
    public int GaussianSharpenOurs() => Run(_ours.GaussianSharpen(3));

    [Benchmark]
    [BenchmarkCategory("gaussian-sharpen")]
    public int GaussianSharpenImageSharp() => Run(c => c.GaussianSharpen(3f));

    [Benchmark]
    [BenchmarkCategory("kirsch")]
    public int KirschOurs() => Run(_ours.DetectEdges(EdgeDetector.Kirsch));

    [Benchmark]
    [BenchmarkCategory("kirsch")]
    public int KirschImageSharp() => Run(c => c.DetectEdges(KnownEdgeDetectorKernels.Kirsch));

    [Benchmark]
    [BenchmarkCategory("scharr")]
    public int ScharrOurs() => Run(_ours.DetectEdges(EdgeDetector.Scharr));

    [Benchmark]
    [BenchmarkCategory("scharr")]
    public int ScharrImageSharp() => Run(c => c.DetectEdges(KnownEdgeDetectorKernels.Scharr));

    [Benchmark]
    [BenchmarkCategory("laplacian")]
    public int Laplacian5x5Ours() => Run(_ours.DetectEdges(EdgeDetector.Laplacian5x5));

    [Benchmark]
    [BenchmarkCategory("laplacian")]
    public int Laplacian5x5ImageSharp() => Run(c => c.DetectEdges(KnownEdgeDetectorKernels.Laplacian5x5));

    [Benchmark]
    [BenchmarkCategory("entropy-crop")]
    public int EntropyCropOurs() => _ours.EntropyCrop().Width;

    [Benchmark]
    [BenchmarkCategory("entropy-crop")]
    public int EntropyCropImageSharp()
    {
        using IsImage r = _sharp.Clone(c => c.EntropyCrop());
        return r.Width;
    }
}
