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
using OursPen = OneImage.Drawing.Pen;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Drawing;

/// <summary>
/// Strokes on a 2000 × 2000 RGBA image: a 1000-point polyline at width 3 with round joins, 300 stroked circles (width 3), and a
/// dashed path (a 400-point wave, width 3, pattern 3 on 1 off in widths). OneImage draws on a <see cref="Canvas"/> in place (as
/// ImageSharp.Drawing mutates its image); the "Image" rows build the lazy strokes and copy the result out.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class StrokeBenchmarks : IDisposable
{
    private const int Size = 2000;
    private Ours.Canvas _canvas = null!;
    private Ours.Image _image = null!;
    private Ours.Rgba32[] _out = null!;
    private SharpRgba _sharp = null!;
    private OursPath _polyline = null!;
    private OursPath _wave = null!;
    private OursPath[] _circles = null!;
    private IPath _sharpPolyline = null!;
    private IPath _sharpWave = null!;
    private IPath[] _sharpCircles = null!;
    private OursPen _pen = null!;
    private OursPen _dashPen = null!;
    private SolidPen _sharpPen = null!;
    private PatternPen _sharpDashPen = null!;
    private DrawingOptions _options = null!;

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

        _polyline = OursPath.Polyline(points);
        _sharpPolyline = new SixLabors.ImageSharp.Drawing.Path(new LinearLineSegment(points.Select(p => new PointF(p.X, p.Y)).ToArray()));

        var wave = Enumerable.Range(0, 400).Select(i => new System.Numerics.Vector2(50 + (i * 4.8f), 1000 + (float)(600 * Math.Sin(i * 0.05)))).ToArray();
        _wave = OursPath.Polyline(wave);
        _sharpWave = new SixLabors.ImageSharp.Drawing.Path(new LinearLineSegment(wave.Select(p => new PointF(p.X, p.Y)).ToArray()));

        var centers = Enumerable.Range(0, 300).Select(_ => (X: (float)(rnd.NextDouble() * Size), Y: (float)(rnd.NextDouble() * Size), R: (float)(20 + (rnd.NextDouble() * 60)))).ToArray();
        _circles = centers.Select(c => OursPath.Circle(c.X, c.Y, c.R)).ToArray();
        _sharpCircles = centers.Select(c => (IPath)new EllipsePolygon(c.X, c.Y, c.R)).ToArray();

        var color = new Ours.Rgba32(200, 60, 30, 255);
        _pen = new OursPen(OneImage.Drawing.Brush.Solid(color), 3, StrokeJoin.Round);
        _dashPen = OursPen.Dash(color, 3);
        var sharpColor = Color.FromPixel(new SixLabors.ImageSharp.PixelFormats.Rgba32(200, 60, 30, 255));
        _sharpPen = new SolidPen(new PenOptions(sharpColor, 3) { JointStyle = JointStyle.Round });
        _sharpDashPen = new PatternPen(new PenOptions(sharpColor, 3, [3f, 1f]));
        _options = new DrawingOptions { GraphicsOptions = new GraphicsOptions { Antialias = true } };

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

    private Ours.Rgba32 CopyOut(Ours.Image img)
    {
        img.CopyPixelsTo<Ours.Rgba32>(_out, new Ours.ImageRect(0, 0, Size, Size));
        return _out[0];
    }

    [Benchmark(Description = "polyline-1000 OneImage Canvas")]
    public void PolylineCanvas() => _canvas.Draw(_pen, _polyline);

    [Benchmark(Description = "polyline-1000 OneImage Image+copy")]
    public Ours.Rgba32 PolylineImage() => CopyOut(_image.Draw(_pen, _polyline));

    [Benchmark(Baseline = true, Description = "polyline-1000 ImageSharp.Drawing")]
    public void PolylineSharp() => _sharp.Mutate(c => c.Draw(_options, _sharpPen, _sharpPolyline));

    [Benchmark(Description = "circles-300 OneImage Canvas")]
    public void CirclesCanvas()
    {
        foreach (OursPath p in _circles)
        {
            _canvas.Draw(_pen, p);
        }
    }

    [Benchmark(Description = "circles-300 OneImage Image+copy")]
    public Ours.Rgba32 CirclesImage()
    {
        Ours.Image img = _image;
        foreach (OursPath p in _circles)
        {
            img = img.Draw(_pen, p);
        }

        return CopyOut(img);
    }

    [Benchmark(Description = "circles-300 ImageSharp.Drawing")]
    public void CirclesSharp() => _sharp.Mutate(c =>
    {
        foreach (IPath p in _sharpCircles)
        {
            c.Draw(_options, _sharpPen, p);
        }
    });

    [Benchmark(Description = "dashed-wave OneImage Canvas")]
    public void DashedCanvas() => _canvas.Draw(_dashPen, _wave);

    [Benchmark(Description = "dashed-wave OneImage Image+copy")]
    public Ours.Rgba32 DashedImage() => CopyOut(_image.Draw(_dashPen, _wave));

    [Benchmark(Description = "dashed-wave ImageSharp.Drawing")]
    public void DashedSharp() => _sharp.Mutate(c => c.Draw(_options, _sharpDashPen, _sharpWave));
}
