using BenchmarkDotNet.Attributes;
using OneImage.Kernels;
using Sources = OneImage.Benchmarks.Resample.ResizeSources;

namespace OneImage.Benchmarks.Core;

/// <summary>Copy-out and format conversion of a 6000x4000 (24 MP) Rgba32 image already in memory.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CoreHotPathBenchmarks : IDisposable
{
    private Sources _src = null!;
    private OneImage.Rgba32[] _rgba = [];
    private OneImage.Rgb24[] _rgb = [];
    private byte[] _rowSrc = [];
    private byte[] _rowDst = [];
    private Image _asRgb = null!;

    [GlobalSetup]
    public void Setup()
    {
        _src = new Sources(6000, 4000);
        _rgba = new OneImage.Rgba32[6000 * 4000];
        _rgb = new OneImage.Rgb24[6000 * 4000];
        _asRgb = _src.Ours.ConvertTo(PixelFormat.Rgb8);
        _rowSrc = new byte[6000 * 4];
        new Random(1).NextBytes(_rowSrc);
        _rowDst = new byte[6000 * 4];
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _src.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "copy out 6000x4000 Rgba32 to caller buffer")]
    public int CopyOut()
    {
        _src.Ours.CopyPixelsTo<OneImage.Rgba32>(_rgba, new ImageRect(0, 0, 6000, 4000));
        return _rgba.Length;
    }

    [Benchmark(Description = "copy out region 512x512 Rgba32")]
    public int CopyOutSmall()
    {
        _src.Ours.CopyPixelsTo<OneImage.Rgba32>(_rgba.AsSpan(0, 512 * 512), new ImageRect(1000, 1000, 512, 512));
        return _rgba.Length;
    }

    [Benchmark(Description = "Rgba32 -> Rgb24 24 MP through the graph")]
    public int ConvertGraph()
    {
        _asRgb.CopyPixelsTo<OneImage.Rgb24>(_rgb, new ImageRect(0, 0, 6000, 4000));
        return _rgb.Length;
    }

    [Benchmark(Description = "Rgba32 -> Rgb24 kernel only, 4000 rows of 6000")]
    public int ConvertKernelOnly()
    {
        for (int y = 0; y < 4000; y++)
        {
            ConvertKernel.ConvertRow(_rowSrc, PixelFormat.Rgba8, _rowDst, PixelFormat.Rgb8, 6000);
        }

        return _rowDst[0];
    }
}
