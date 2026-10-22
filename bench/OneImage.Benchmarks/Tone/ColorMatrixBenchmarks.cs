using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Tone;

/// <summary>
/// The color-matrix family on a 2048x1536 RGBA image held in memory, codecs excluded. oneimage builds a new lazy image per call and writes
/// into a caller buffer; ImageSharp mutates in place (repeated passes change the values but not the cost). Both libraries get the same
/// degree of parallelism. The matrices are the same ones.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class ColorMatrixBenchmarks : IDisposable
{
    private const int W = 2048;
    private const int H = 1536;
    private OneImage.Image _ours = null!;
    private IsImage _sharp = null!;
    private OneImage.Rgba32[] _buffer = [];
    private OneImage.ColorMatrix _general;
    private SixLabors.ImageSharp.ColorMatrix _generalSharp;

    /// <summary>Degree of parallelism given to both libraries.</summary>
    [Params(1, 4)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] raw = new byte[W * H * 4];
        new Random(42).NextBytes(raw);
        _sharp = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(raw, W, H);
        var px = new OneImage.Rgba32[W * H];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OneImage.Rgba32>(raw).CopyTo(px);
        _ours = OneImage.Image.FromPixels<OneImage.Rgba32>(px, W, H);
        _buffer = new OneImage.Rgba32[W * H];
        _sharp.Configuration.MaxDegreeOfParallelism = Threads;
        _general = new OneImage.ColorMatrix(0.9f, 0.1f, -0.2f, 0f, 0.3f, 0.6f, 0.2f, 0f, -0.1f, 0.4f, 1.1f, 0f, 0f, 0f, 0f, 1f, 0.05f, -0.1f, 0.2f, 0f);
        _generalSharp = new SixLabors.ImageSharp.ColorMatrix(0.9f, 0.1f, -0.2f, 0f, 0.3f, 0.6f, 0.2f, 0f, -0.1f, 0.4f, 1.1f, 0f, 0f, 0f, 0f, 1f, 0.05f, -0.1f, 0.2f, 0f);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sharp?.Dispose();
        GC.SuppressFinalize(this);
    }

    private int Pull(OneImage.Image img)
    {
        img.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, W, H), maxDegree: Threads);
        return _buffer[12345].R;
    }

    private int Mutate(Action<IImageProcessingContext> op)
    {
        _sharp.Mutate(op);
        return _sharp[1, 1].R;
    }

    [Benchmark(Description = "gamma 1.0001 (table op, for scale): oneimage")]
    public int CopyOurs() => Pull(_ours.Gamma(1.0001));

    [Benchmark(Description = "sepia: oneimage")]
    public int SepiaOurs() => Pull(_ours.Sepia());

    [Benchmark(Description = "sepia: ImageSharp")]
    public int SepiaSharp() => Mutate(c => c.Sepia());

    [Benchmark(Description = "sepia: ImageSharp (clone: separate output like oneimage)")]
    public int SepiaSharpClone()
    {
        using IsImage copy = _sharp.Clone(c => c.Sepia());
        return copy[1, 1].R;
    }

    [Benchmark(Description = "sepia+contrast+opacity: oneimage (one fused pass)")]
    public int ChainOurs() => Pull(_ours.Sepia().BrightnessContrast(0.05, 0.2).Opacity(0.8f));

    [Benchmark(Description = "sepia+contrast+opacity: ImageSharp (three passes, in place)")]
    public int ChainSharp() => Mutate(c => c.Sepia().Brightness(1.05f).Contrast(1.2f).Opacity(0.8f));

    [Benchmark(Description = "kodachrome: oneimage")]
    public int KodachromeOurs() => Pull(_ours.Kodachrome());

    [Benchmark(Description = "kodachrome: ImageSharp")]
    public int KodachromeSharp() => Mutate(c => c.Kodachrome());

    [Benchmark(Description = "black-white: oneimage")]
    public int BlackWhiteOurs() => Pull(_ours.BlackWhite());

    [Benchmark(Description = "black-white: ImageSharp")]
    public int BlackWhiteSharp() => Mutate(c => c.BlackWhite());

    [Benchmark(Description = "color-blindness (protanopia): oneimage")]
    public int ProtanopiaOurs() => Pull(_ours.ColorBlindness(ColorBlindnessMode.Protanopia));

    [Benchmark(Description = "color-blindness (protanopia): ImageSharp")]
    public int ProtanopiaSharp() => Mutate(c => c.ColorBlindness(SixLabors.ImageSharp.Processing.ColorBlindnessMode.Protanopia));

    [Benchmark(Description = "opacity: oneimage")]
    public int OpacityOurs() => Pull(_ours.Opacity(0.5f));

    [Benchmark(Description = "opacity: ImageSharp")]
    public int OpacitySharp() => Mutate(c => c.Opacity(0.5f));

    [Benchmark(Description = "filter(matrix): oneimage")]
    public int MatrixOurs() => Pull(_ours.ApplyColorMatrix(_general));

    [Benchmark(Description = "filter(matrix): ImageSharp")]
    public int MatrixSharp() => Mutate(c => c.Filter(_generalSharp));
}
