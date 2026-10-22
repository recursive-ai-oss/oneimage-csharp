using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Ours = OneImage;
using OursPath = OneImage.Drawing.Path;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Drawing;

/// <summary>
/// Solid-color fills on a 2000 × 2000 RGBA image: one 1000-point polygon, and 300 circles. OneImage fills a <see cref="Canvas"/>
/// in place (as ImageSharp.Drawing mutates its image), and the "Image" row builds the lazy fill and copies the result out.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class FillBenchmarks : IDisposable
{
    private const int Size = 2000;
    private Ours.Canvas _canvas = null!;
    private Ours.Image _image = null!;
    private Ours.Rgba32[] _out = null!;
    private SharpRgba _sharp = null!;
    private OursPath _polygon = null!;
    private OursPath[] _circles = null!;
    private IPath _sharpPolygon = null!;
    private IPath[] _sharpCircles = null!;
    private OneImage.Drawing.Brush _brush = null!;
    private SixLabors.ImageSharp.Color _sharpColor;
    private DrawingOptions _options = null!;

    [Params(255, 160)]
    public int Alpha { get; set; } = 255;

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(3);
        var points = new System.Numerics.Vector2[1000];
        for (int i = 0; i < points.Length; i++)
        {
            double a = i * 2 * Math.PI / points.Length;
            double r = 800 + (150 * Math.Sin(a * 9)) + (rnd.NextDouble() * 20);
            points[i] = new System.Numerics.Vector2((float)(1000 + (r * Math.Cos(a))), (float)(1000 + (r * Math.Sin(a))));
        }

        _polygon = OursPath.Polygon(points);
        _sharpPolygon = new Polygon(new LinearLineSegment(points.Select(p => new PointF(p.X, p.Y)).ToArray()));
        var centers = Enumerable.Range(0, 300).Select(_ => (X: (float)(rnd.NextDouble() * Size), Y: (float)(rnd.NextDouble() * Size), R: (float)(20 + (rnd.NextDouble() * 60)))).ToArray();
        _circles = centers.Select(c => OursPath.Circle(c.X, c.Y, c.R)).ToArray();
        _sharpCircles = centers.Select(c => (IPath)new EllipsePolygon(c.X, c.Y, c.R)).ToArray();

        var color = new Ours.Rgba32(200, 60, 30, (byte)Alpha);
        _brush = OneImage.Drawing.Brush.Solid(color);
        _sharpColor = SixLabors.ImageSharp.Color.FromPixel(new SixLabors.ImageSharp.PixelFormats.Rgba32(200, 60, 30, (byte)Alpha));
        _options = new DrawingOptions
        {
            GraphicsOptions = new GraphicsOptions { Antialias = true },
            ShapeOptions = new ShapeOptions { IntersectionRule = SixLabors.ImageSharp.Drawing.IntersectionRule.NonZero },
        };

        _canvas = new Ours.Canvas(Size, Size, Ours.PixelFormat.Rgba8);
        _canvas.Clear(new Ours.Rgba32(240, 240, 240, 255));
        _image = _canvas.Snapshot();
        _out = new Ours.Rgba32[Size * Size];
        _sharp = new SharpRgba(Size, Size, new SixLabors.ImageSharp.PixelFormats.Rgba32(240, 240, 240, 255));
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _canvas?.Dispose();
        _sharp?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "polygon-1000 OneImage Canvas")]
    public void PolygonCanvas() => _canvas.Fill(_brush, _polygon);

    [Benchmark(Description = "polygon-1000 OneImage Image+copy")]
    public Ours.Rgba32 PolygonImage()
    {
        _image.Fill(_brush, _polygon).CopyPixelsTo<Ours.Rgba32>(_out, new Ours.ImageRect(0, 0, Size, Size));
        return _out[0];
    }

    [Benchmark(Baseline = true, Description = "polygon-1000 ImageSharp.Drawing")]
    public void PolygonSharp() => _sharp.Mutate(c => c.Fill(_options, _sharpColor, _sharpPolygon));

    [Benchmark(Description = "circles-300 OneImage Canvas")]
    public void CirclesCanvas()
    {
        foreach (OursPath p in _circles)
        {
            _canvas.Fill(_brush, p);
        }
    }

    [Benchmark(Description = "circles-300 OneImage Image+copy")]
    public Ours.Rgba32 CirclesImage()
    {
        Ours.Image img = _image;
        foreach (OursPath p in _circles)
        {
            img = img.Fill(_brush, p);
        }

        img.CopyPixelsTo<Ours.Rgba32>(_out, new Ours.ImageRect(0, 0, Size, Size));
        return _out[0];
    }

    [Benchmark(Description = "circles-300 ImageSharp.Drawing")]
    public void CirclesSharp() => _sharp.Mutate(c =>
    {
        foreach (IPath p in _sharpCircles)
        {
            c.Fill(_options, _sharpColor, p);
        }
    });
}
