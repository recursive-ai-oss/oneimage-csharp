using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using ImageBrush = SixLabors.ImageSharp.Drawing.Processing.ImageBrush;
using LinearGradientBrush = SixLabors.ImageSharp.Drawing.Processing.LinearGradientBrush;
using RadialGradientBrush = SixLabors.ImageSharp.Drawing.Processing.RadialGradientBrush;
using Ours = OneImage;
using OursBrush = OneImage.Drawing.Brush;
using OursPath = OneImage.Drawing.Path;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Drawing;

/// <summary>
/// Gradient, pattern and image brush fills on a 2000 × 2000 RGBA image: a linear gradient over the whole image, a radial gradient
/// in a circle, a hatch pattern over a 1000-point polygon, and a 64 × 64 image brush over the same polygon. OneImage fills a
/// <see cref="Canvas"/> in place, as ImageSharp.Drawing mutates its image; the "Image+copy" rows build the lazy fill and copy the
/// result out.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class BrushBenchmarks : IDisposable
{
    private const int Size = 2000;
    private Ours.Canvas _canvas = null!;
    private Ours.Image _image = null!;
    private Ours.Rgba32[] _out = null!;
    private SharpRgba _sharp = null!;
    private OursPath _polygon = null!, _circle = null!, _all = null!;
    private IPath _sharpPolygon = null!, _sharpCircle = null!;
    private OursBrush _linear = null!, _radial = null!, _hatch = null!, _imageBrush = null!;
    private SixLabors.ImageSharp.Drawing.Processing.Brush _sharpLinear = null!, _sharpRadial = null!, _sharpHatch = null!, _sharpImageBrush = null!;
    private SharpRgba _sharpTexture = null!;
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

        _polygon = OursPath.Polygon(points);
        _sharpPolygon = new Polygon(new LinearLineSegment(points.Select(p => new PointF(p.X, p.Y)).ToArray()));
        _circle = OursPath.Circle(1000, 1000, 900);
        _sharpCircle = new EllipsePolygon(1000, 1000, 900);
        _all = OursPath.Rectangle(0, 0, Size, Size);

        var red = new Ours.Rgba32(220, 40, 30, 255);
        var yellow = new Ours.Rgba32(250, 220, 20, 255);
        var blue = new Ours.Rgba32(20, 60, 230, 255);
        SixLabors.ImageSharp.Color S(Ours.Rgba32 c) => SixLabors.ImageSharp.Color.FromPixel(new SixLabors.ImageSharp.PixelFormats.Rgba32(c.R, c.G, c.B, c.A));
        _linear = OursBrush.LinearGradient(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(Size, Size), [new(0f, red), new(0.5f, yellow), new(1f, blue)]);
        _sharpLinear = new LinearGradientBrush(new PointF(0, 0), new PointF(Size, Size), GradientRepetitionMode.None, new SixLabors.ImageSharp.Drawing.Processing.ColorStop(0f, S(red)), new SixLabors.ImageSharp.Drawing.Processing.ColorStop(0.5f, S(yellow)), new SixLabors.ImageSharp.Drawing.Processing.ColorStop(1f, S(blue)));
        _radial = OursBrush.RadialGradient(new System.Numerics.Vector2(1000, 1000), 900, [new(0f, yellow), new(0.6f, red), new(1f, blue)]);
        _sharpRadial = new RadialGradientBrush(new PointF(1000, 1000), 900, GradientRepetitionMode.None, new SixLabors.ImageSharp.Drawing.Processing.ColorStop(0f, S(yellow)), new SixLabors.ImageSharp.Drawing.Processing.ColorStop(0.6f, S(red)), new SixLabors.ImageSharp.Drawing.Processing.ColorStop(1f, S(blue)));
        _hatch = OursBrush.Hatch(HatchStyle.ForwardDiagonal, red, new Ours.Rgba32(255, 255, 255, 255));
        _sharpHatch = Brushes.ForwardDiagonal(S(red), S(new Ours.Rgba32(255, 255, 255, 255)));

        var tex = new Ours.Rgba32[64 * 64];
        _sharpTexture = new SharpRgba(64, 64);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                var c = new Ours.Rgba32((byte)(x * 4), (byte)(y * 4), (byte)((x ^ y) * 4), 255);
                tex[(y * 64) + x] = c;
                _sharpTexture[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32(c.R, c.G, c.B, c.A);
            }
        }

        _imageBrush = OursBrush.FromImage(Ours.Image.FromPixels<Ours.Rgba32>(tex, 64, 64));
        _sharpImageBrush = new ImageBrush(_sharpTexture);

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
        _sharpTexture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private Ours.Rgba32 Copy(Ours.Image img)
    {
        img.CopyPixelsTo<Ours.Rgba32>(_out, new Ours.ImageRect(0, 0, Size, Size));
        return _out[0];
    }

    [Benchmark(Description = "linear-gradient-whole OneImage Canvas")]
    public void LinearCanvas() => _canvas.Fill(_linear, _all, antialias: false);

    [Benchmark(Description = "linear-gradient-whole OneImage Image+copy")]
    public Ours.Rgba32 LinearImage() => Copy(_image.Fill(_linear, _all, antialias: false));

    [Benchmark(Baseline = true, Description = "linear-gradient-whole ImageSharp.Drawing")]
    public void LinearSharp() => _sharp.Mutate(c => c.Fill(_options, _sharpLinear));

    [Benchmark(Description = "radial-gradient-circle OneImage Canvas")]
    public void RadialCanvas() => _canvas.Fill(_radial, _circle);

    [Benchmark(Description = "radial-gradient-circle OneImage Image+copy")]
    public Ours.Rgba32 RadialImage() => Copy(_image.Fill(_radial, _circle));

    [Benchmark(Description = "radial-gradient-circle ImageSharp.Drawing")]
    public void RadialSharp() => _sharp.Mutate(c => c.Fill(_options, _sharpRadial, _sharpCircle));

    [Benchmark(Description = "hatch-polygon-1000 OneImage Canvas")]
    public void HatchCanvas() => _canvas.Fill(_hatch, _polygon);

    [Benchmark(Description = "hatch-polygon-1000 OneImage Image+copy")]
    public Ours.Rgba32 HatchImage() => Copy(_image.Fill(_hatch, _polygon));

    [Benchmark(Description = "hatch-polygon-1000 ImageSharp.Drawing")]
    public void HatchSharp() => _sharp.Mutate(c => c.Fill(_options, _sharpHatch, _sharpPolygon));

    [Benchmark(Description = "image-brush-polygon-1000 OneImage Canvas")]
    public void ImageBrushCanvas() => _canvas.Fill(_imageBrush, _polygon);

    [Benchmark(Description = "image-brush-polygon-1000 OneImage Image+copy")]
    public Ours.Rgba32 ImageBrushImage() => Copy(_image.Fill(_imageBrush, _polygon));

    [Benchmark(Description = "image-brush-polygon-1000 ImageSharp.Drawing")]
    public void ImageBrushSharp() => _sharp.Mutate(c => c.Fill(_options, _sharpImageBrush, _sharpPolygon));
}
