using BenchmarkDotNet.Attributes;

namespace OneImage.Benchmarks.Resample;

public partial class SmallResizeBenchmarks : IDisposable
{
    private PixelResizer _resizer = null!;
    private PixelResizer _single = null!;

    partial void SetupSpan()
    {
        _resizer = new PixelResizer();
        _single = new PixelResizer(maxDegree: 1);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _resizer.Dispose();
        _single.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The allocation-free span path on the same source every call, into a reused buffer.</summary>
    [Benchmark(Description = "PixelResizer, 2300x60 -> h32")]
    public int SpanResize()
    {
        (int w, int h) = PixelResizer.GetSize(2300, 60, height: Height);
        _resizer.Resize<OneImage.Gray8>(_pixels[0], 2300, 60, _out, w, h);
        return w;
    }

    /// <summary>The span path with a different source width each call.</summary>
    [Benchmark(Description = "PixelResizer, varying widths")]
    public int SpanResizeVarying()
    {
        int i = _next++ & 127;
        int sw = s_widths[i];
        (int w, int h) = PixelResizer.GetSize(sw, 60, height: Height);
        _resizer.Resize<OneImage.Gray8>(_pixels[i], sw, 60, _out, w, h);
        return w;
    }

    /// <summary>The span path kept on the calling thread.</summary>
    [Benchmark(Description = "PixelResizer maxDegree 1, 2300x60 -> h32")]
    public int SpanResizeSingleThread()
    {
        (int w, int h) = PixelResizer.GetSize(2300, 60, height: Height);
        _single.Resize<OneImage.Gray8>(_pixels[0], 2300, 60, _out, w, h);
        return w;
    }
}
