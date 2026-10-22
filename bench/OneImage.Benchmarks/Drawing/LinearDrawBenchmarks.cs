using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OursBrush = OneImage.Drawing.Brush;
using OursPath = OneImage.Drawing.Path;
using OursPen = OneImage.Drawing.Pen;

namespace OneImage.Benchmarks.Drawing;

/// <summary>
/// Fills and strokes on a 2000 × 2000 Rgba32 image and canvas, in the encoded space and in linear light: a solid circle, a linear
/// gradient circle, a Multiply fill and a ring stroke. Image rows render the lazy result into a reused buffer.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class LinearDrawBenchmarks : IDisposable
{
    private const int Size = 2000;
    private Image _base = null!;
    private Canvas _canvas = null!;
    private Rgba32[] _out = null!;
    private OursPath _circle = null!, _ring = null!;
    private OursBrush _gradient = null!;
    private OursPen _pen = null!;
    private readonly Rgba32 _red = new(220, 30, 30, 255);

    /// <summary>Gets or sets whether the draw blends in linear light.</summary>
    [Params(false, true)]
    public bool Linear { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _canvas = new Canvas(Size, Size, PixelFormat.Rgba8);
        _canvas.Clear(new Rgba32(235, 240, 250, 255));
        _base = _canvas.Snapshot();
        _out = new Rgba32[Size * Size];
        _circle = OursPath.Circle(1000, 1000, 800);
        _ring = OursPath.Circle(1000, 1000, 900);
        _gradient = OursBrush.LinearGradient(new Vector2(200, 200), new Vector2(1800, 1800), [new(0f, new Rgba32(255, 0, 0, 255)), new(1f, new Rgba32(0, 0, 255, 255))]);
        _pen = new OursPen(OursBrush.Solid(new Rgba32(30, 30, 220, 255)), 12, OneImage.Drawing.StrokeJoin.Round);
    }

    [Benchmark(Description = "Image: copy only")]
    public void ImageCopyOnly() => _base.CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, Size, Size));

    [Benchmark(Description = "Image: solid circle")]
    public void ImageSolid() => _base.Fill(_red, _circle, linear: Linear).CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, Size, Size));

    [Benchmark(Description = "Image: gradient circle")]
    public void ImageGradient() => _base.Fill(_gradient, _circle, linear: Linear).CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, Size, Size));

    [Benchmark(Description = "Image: multiply circle")]
    public void ImageMultiply() => _base.Fill(_red, _circle, blend: BlendMode.Multiply, linear: Linear).CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, Size, Size));

    [Benchmark(Description = "Image: ring stroke")]
    public void ImageStroke() => _base.Draw(_pen, _ring, linear: Linear).CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, Size, Size));

    [Benchmark(Description = "Canvas: solid circle")]
    public void CanvasSolid() => _canvas.Fill(_red, _circle, linear: Linear);

    [Benchmark(Description = "Canvas: gradient circle")]
    public void CanvasGradient() => _canvas.Fill(_gradient, _circle, linear: Linear);

    [Benchmark(Description = "Canvas: multiply circle")]
    public void CanvasMultiply() => _canvas.Fill(_red, _circle, blend: BlendMode.Multiply, linear: Linear);

    [Benchmark(Description = "Canvas: ring stroke")]
    public void CanvasStroke() => _canvas.Draw(_pen, _ring, linear: Linear);

    public void Dispose()
    {
        _canvas?.Dispose();
        GC.SuppressFinalize(this);
    }
}
