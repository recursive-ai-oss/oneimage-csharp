using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Processing;
using VipsImage = NetVips.Image;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using Sources = OneImage.Benchmarks.Resample.ResizeSources;

namespace OneImage.Benchmarks.Geometry;

/// <summary>
/// Geometry ops on a 6000x4000 Rgba32 image already in memory, codecs excluded. oneimage rows render into a caller-owned buffer so
/// the number is the work of the op, not of allocating the 96 MB result; the competitors allocate their result image per call.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class GeometryBenchmarks : IDisposable
{
    private Sources _src = null!;
    private OneImage.Rgba32[] _buffer = [];

    [GlobalSetup]
    public void Setup()
    {
        _src = new Sources(6000, 4000);
        OneImage.Image biggest = _src.Ours.Rotate(17);
        _buffer = new OneImage.Rgba32[(long)biggest.Width * biggest.Height];
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

    [Benchmark(Description = "oneimage rotate 90")]
    public int OursRotate90() => Render(_src.Ours.Rotate(90));

    [Benchmark(Description = "ImageSharp rotate 90")]
    public int SharpRotate90()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Rotate(RotateMode.Rotate90));
        return r.Width;
    }

    [Benchmark(Description = "NetVips rot90")]
    public int VipsRotate90()
    {
        using VipsImage r = _src.Vips.Rot(NetVips.Enums.Angle.D90);
        return r.WriteToMemory<byte>().Length;
    }

    [Benchmark(Description = "oneimage flip horizontal")]
    public int OursFlip() => Render(_src.Ours.Flip(FlipAxis.Horizontal));

    [Benchmark(Description = "ImageSharp flip horizontal")]
    public int SharpFlip()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Flip(FlipMode.Horizontal));
        return r.Width;
    }

    [Benchmark(Description = "NetVips flip horizontal")]
    public int VipsFlip()
    {
        using VipsImage r = _src.Vips.Flip(NetVips.Enums.Direction.Horizontal);
        return r.WriteToMemory<byte>().Length;
    }

    [Benchmark(Description = "oneimage rotate 17 (Catmull-Rom, linear)")]
    public int OursRotate17() => Render(_src.Ours.Rotate(17));

    [Benchmark(Description = "oneimage rotate 17 (Catmull-Rom, encoded space)")]
    public int OursRotate17Encoded() => Render(_src.Ours.Rotate(17, linear: false));

    [Benchmark(Description = "oneimage rotate 17 (bilinear, encoded space)")]
    public int OursRotate17Bilinear() => Render(_src.Ours.Rotate(17, filter: ResamplingFilter.Bilinear, linear: false));

    [Benchmark(Description = "ImageSharp rotate 17 (bicubic)")]
    public int SharpRotate17()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Rotate(17f));
        return r.Width;
    }

    [Benchmark(Description = "NetVips rotate 17 (bicubic)")]
    public int VipsRotate17()
    {
        using VipsImage r = _src.Vips.Rotate(17.0);
        return r.WriteToMemory<byte>().Length;
    }
}
