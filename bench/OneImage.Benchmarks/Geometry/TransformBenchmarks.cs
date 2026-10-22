using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using Sources = OneImage.Benchmarks.Resample.ResizeSources;

namespace OneImage.Benchmarks.Geometry;

/// <summary>
/// Perspective on a 3 MP (2000x1500) Rgba32 image in memory, codecs excluded. oneimage rows render into a caller-owned buffer; ImageSharp allocates
/// its result per call. The default oneimage row interpolates in linear light (its default); the encoded-space row does what ImageSharp does.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PerspectiveBenchmarks : IDisposable
{
    private Sources _src = null!;
    private OneImage.Rgba32[] _buffer = [];
    private OneImage.ProjectiveTransformBuilder _ours = null!;
    private SixLabors.ImageSharp.Processing.ProjectiveTransformBuilder _theirs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _src = new Sources(2000, 1500);
        _ours = new OneImage.ProjectiveTransformBuilder().AppendTaper(OneImage.TaperSide.Left, OneImage.TaperCorner.Both, 0.6);
        _theirs = new SixLabors.ImageSharp.Processing.ProjectiveTransformBuilder().AppendTaper(SixLabors.ImageSharp.Processing.TaperSide.Left, SixLabors.ImageSharp.Processing.TaperCorner.Both, 0.6f);
        OneImage.Image probe = _src.Ours.Perspective(_ours);
        _buffer = new OneImage.Rgba32[(long)probe.Width * probe.Height];
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _src.Dispose();
        GC.SuppressFinalize(this);
    }

    private int Render(OneImage.Image r)
    {
        r.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, r.Width, r.Height));
        return r.Width;
    }

    [Benchmark(Description = "oneimage perspective taper (Catmull-Rom, linear light)")]
    public int OursLinear() => Render(_src.Ours.Perspective(_ours));

    [Benchmark(Baseline = true, Description = "oneimage perspective taper (Catmull-Rom, encoded space)")]
    public int OursEncoded() => Render(_src.Ours.Perspective(_ours, linear: false));

    [Benchmark(Description = "ImageSharp Transform taper (bicubic)")]
    public int Sharp()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Transform(_theirs, KnownResamplers.CatmullRom));
        return r.Width;
    }
}

/// <summary>Resize with the added resamplers, 3 MP down to 800 wide and up 2x, oneimage in encoded space like ImageSharp, plus oneimage's default linear light.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ExtraResamplerBenchmarks : IDisposable
{
    private Sources _src = null!;
    private OneImage.Rgba32[] _buffer = [];

    [GlobalSetup]
    public void Setup()
    {
        _src = new Sources(2000, 1500);
        _buffer = new OneImage.Rgba32[4000 * 3000];
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _src.Dispose();
        GC.SuppressFinalize(this);
    }

    private int Render(OneImage.Image r)
    {
        r.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, r.Width, r.Height));
        return r.Width;
    }

    [Benchmark(Description = "oneimage Lanczos5 down 800 (encoded)")]
    public int OursLanczos5Down() => Render(_src.Ours.Resize(width: 800, filter: OneImage.ResamplingFilter.Lanczos5, linear: false));

    [Benchmark(Description = "ImageSharp Lanczos5 down 800")]
    public int SharpLanczos5Down()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Resize(800, 0, KnownResamplers.Lanczos5));
        return r.Width;
    }

    [Benchmark(Description = "oneimage Robidoux down 800 (encoded)")]
    public int OursRobidouxDown() => Render(_src.Ours.Resize(width: 800, filter: OneImage.ResamplingFilter.Robidoux, linear: false));

    [Benchmark(Description = "oneimage Robidoux down 800 (linear light)")]
    public int OursRobidouxDownLinear() => Render(_src.Ours.Resize(width: 800, filter: OneImage.ResamplingFilter.Robidoux));

    [Benchmark(Description = "ImageSharp Robidoux down 800")]
    public int SharpRobidouxDown()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Resize(800, 0, KnownResamplers.Robidoux));
        return r.Width;
    }

    [Benchmark(Description = "oneimage Lanczos5 up 2x (encoded)")]
    public int OursLanczos5Up() => Render(_src.Ours.Resize(width: 4000, filter: OneImage.ResamplingFilter.Lanczos5, linear: false));

    [Benchmark(Description = "ImageSharp Lanczos5 up 2x")]
    public int SharpLanczos5Up()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Resize(4000, 0, KnownResamplers.Lanczos5));
        return r.Width;
    }

    [Benchmark(Description = "oneimage Robidoux up 2x (encoded)")]
    public int OursRobidouxUp() => Render(_src.Ours.Resize(width: 4000, filter: OneImage.ResamplingFilter.Robidoux, linear: false));

    [Benchmark(Description = "ImageSharp Robidoux up 2x")]
    public int SharpRobidouxUp()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Resize(4000, 0, KnownResamplers.Robidoux));
        return r.Width;
    }
}
