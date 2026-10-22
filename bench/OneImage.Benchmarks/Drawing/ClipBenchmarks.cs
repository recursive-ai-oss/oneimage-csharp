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
/// Boolean path operations and clipping against ImageSharp.Drawing 2.1.7: the union of 200 circles, a star minus a 1000-point
/// polygon (both only build the resulting path), and a Gaussian blur clipped to a circle on a 2000 × 2000 RGBA image
/// (OneImage in place on a <see cref="Canvas"/>, as ImageSharp mutates its image, and as a lazy <see cref="Image"/> copied out).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class ClipBenchmarks : IDisposable
{
    private const int Size = 2000;
    private OursPath[] _circles = null!;
    private IPath[] _sharpCircles = null!;
    private OursPath _star = null!;
    private OursPath _blob = null!;
    private IPath _sharpStar = null!;
    private IPath _sharpBlob = null!;
    private OursPath _clipCircle = null!;
    private IPath _sharpClipCircle = null!;
    private Ours.Canvas _canvas = null!;
    private Ours.Image _image = null!;
    private Ours.Rgba32[] _out = null!;
    private SharpRgba _sharp = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(5);
        var centers = Enumerable.Range(0, 200).Select(_ => (X: (float)(rnd.NextDouble() * Size), Y: (float)(rnd.NextDouble() * Size), R: (float)(40 + (rnd.NextDouble() * 120)))).ToArray();
        _circles = centers.Select(c => OursPath.Circle(c.X, c.Y, c.R)).ToArray();
        _sharpCircles = centers.Select(c => (IPath)new EllipsePolygon(c.X, c.Y, c.R)).ToArray();

        var star = new System.Numerics.Vector2[24];
        for (int i = 0; i < star.Length; i++)
        {
            double a = i * Math.PI / 12;
            double r = (i & 1) == 0 ? 900 : 380;
            star[i] = new System.Numerics.Vector2((float)(1000 + (r * Math.Cos(a))), (float)(1000 + (r * Math.Sin(a))));
        }

        var blob = new System.Numerics.Vector2[1000];
        for (int i = 0; i < blob.Length; i++)
        {
            double a = i * 2 * Math.PI / blob.Length;
            double r = 600 + (140 * Math.Sin(a * 7)) + (40 * Math.Cos(a * 31));
            blob[i] = new System.Numerics.Vector2((float)(1100 + (r * Math.Cos(a))), (float)(950 + (r * Math.Sin(a))));
        }

        _star = OursPath.Polygon(star);
        _blob = OursPath.Polygon(blob);
        _sharpStar = new Polygon(new LinearLineSegment(star.Select(p => new PointF(p.X, p.Y)).ToArray()));
        _sharpBlob = new Polygon(new LinearLineSegment(blob.Select(p => new PointF(p.X, p.Y)).ToArray()));
        _clipCircle = OursPath.Circle(1000, 1000, 700);
        _sharpClipCircle = new EllipsePolygon(1000, 1000, 700);

        _canvas = new Ours.Canvas(Size, Size, Ours.PixelFormat.Rgba8);
        var px = new Ours.Rgba32[Size * Size];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Ours.Rgba32((byte)i, (byte)(i >> 8), (byte)((i % Size) ^ (i / Size)), 255);
        }

        _canvas.CopyFrom<Ours.Rgba32>(px, new Ours.ImageRect(0, 0, Size, Size));
        _image = _canvas.Snapshot();
        _out = new Ours.Rgba32[Size * Size];
        _sharp = new SharpRgba(Size, Size);
        _sharp.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < Size; y++)
            {
                Span<SixLabors.ImageSharp.PixelFormats.Rgba32> row = rows.GetRowSpan(y);
                for (int x = 0; x < Size; x++)
                {
                    Ours.Rgba32 p = px[(y * Size) + x];
                    row[x] = new SixLabors.ImageSharp.PixelFormats.Rgba32(p.R, p.G, p.B, 255);
                }
            }
        });
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _canvas?.Dispose();
        _sharp?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "union-200-circles OneImage")]
    public OursPath UnionOurs() => PathBoolean.UnionAll(_circles);

    [Benchmark(Description = "union-200-circles ImageSharp.Drawing")]
    public IPath UnionSharp() => _sharpCircles[0].Clip(
        new ShapeOptions { ClippingOperation = ClippingOperation.Union, IntersectionRule = SixLabors.ImageSharp.Drawing.IntersectionRule.NonZero },
        _sharpCircles.AsSpan(1).ToArray());

    [Benchmark(Description = "star-minus-1000 OneImage")]
    public OursPath DifferenceOurs() => _star.Subtract(_blob);

    [Benchmark(Description = "star-minus-1000 ImageSharp.Drawing")]
    public IPath DifferenceSharp() => _sharpStar.Clip(new ShapeOptions { ClippingOperation = ClippingOperation.Difference, IntersectionRule = SixLabors.ImageSharp.Drawing.IntersectionRule.NonZero }, _sharpBlob);

    [Benchmark(Description = "blur-in-circle OneImage Canvas")]
    public void BlurCanvas() => _canvas.Clip(_clipCircle, i => i.GaussianBlur(8, linear: false));

    [Benchmark(Description = "blur-in-circle OneImage Image+copy")]
    public Ours.Rgba32 BlurImage()
    {
        _image.Clip(_clipCircle, i => i.GaussianBlur(8, linear: false)).CopyPixelsTo<Ours.Rgba32>(_out, new Ours.ImageRect(0, 0, Size, Size));
        return _out[0];
    }

    [Benchmark(Description = "blur-in-circle ImageSharp.Drawing")]
    public void BlurSharp() => _sharp.Mutate(c => c.Clip(_sharpClipCircle, p => p.GaussianBlur(8)));
}
