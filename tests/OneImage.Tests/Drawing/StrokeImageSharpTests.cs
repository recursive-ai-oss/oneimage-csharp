using System.Numerics;
using OneImage.Drawing;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Xunit;

using Brush = OneImage.Drawing.Brush;
using Path = OneImage.Drawing.Path;
using Pen = OneImage.Drawing.Pen;
using SharpPen = SixLabors.ImageSharp.Drawing.Processing.Pen;
using Sharp = SixLabors.ImageSharp;
using SharpDrawing = SixLabors.ImageSharp.Drawing;

namespace OneImage.Tests.Drawing;

/// <summary>
/// Stroke agreement with SixLabors.ImageSharp.Drawing 2.1.7: the alpha plane of a white stroke on a transparent image.
/// ImageSharp strokes land half a pixel right and down of where SVG puts them (its coordinates are pixel centers for pens, while its
/// fills use pixel corners), so our path is moved by (0.5, 0.5) here. ImageSharp has no bevel join, so bevels are checked by geometry in <see cref="StrokeTests"/>.
/// Set <c>ONEIMAGE_STROKE_REPORT</c> to a file name to collect the mean and max differences of every case.
/// </summary>
public class StrokeImageSharpTests(ITestOutputHelper output)
{
    private const int Size = 140;

    private static readonly Vector2[] ZigZag = [new(15, 110), new(40, 25), new(62, 100), new(90, 30), new(120, 95), new(128, 50)];
    private static readonly Vector2[] Sharp2 = [new(20, 20), new(120, 60), new(20, 100)];
    private static readonly Vector2[] Pentagon = Enumerable.Range(0, 5)
        .Select(i => new Vector2(70 + (50 * MathF.Sin(i * 2 * MathF.PI / 5)), 72 - (50 * MathF.Cos(i * 2 * MathF.PI / 5))))
        .ToArray();

    public static TheoryData<string, float> JoinCases()
    {
        var d = new TheoryData<string, float>();
        foreach (string j in new[] { "miter", "round", "square" })
        {
            foreach (float w in new[] { 1f, 2.5f, 12f })
            {
                d.Add(j, w);
            }
        }

        return d;
    }

    private static StrokeJoin Join(string j) => j switch { "round" => StrokeJoin.Round, "bevel" => StrokeJoin.Bevel, "square" => StrokeJoin.Square, _ => StrokeJoin.Miter };

    private static SharpDrawing.JointStyle SharpJoin(string j) => j switch { "round" => SharpDrawing.JointStyle.Round, "square" => SharpDrawing.JointStyle.Square, _ => SharpDrawing.JointStyle.Miter };

    private static byte[] OursAlpha(Path path, Pen pen)
    {
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[Size * Size], Size, Size).Draw(pen, path.Translate(0.5f, 0.5f)).ToArray<Rgba32>();
        return px.Select(p => p.A).ToArray();
    }

    private static byte[] TheirsAlpha(IPath path, SharpPen pen)
    {
        using var img = new Sharp.Image<Sharp.PixelFormats.Rgba32>(Size, Size);
        img.Mutate(c => c.Draw(new DrawingOptions(), pen, path));
        var result = new byte[Size * Size];
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < Size; y++)
            {
                Sharp.PixelFormats.Rgba32[] row = rows.GetRowSpan(y).ToArray();
                for (int x = 0; x < Size; x++)
                {
                    result[(y * Size) + x] = row[x].A;
                }
            }
        });
        return result;
    }

    private static Sharp.PointF[] P(Vector2[] v) => v.Select(p => new Sharp.PointF(p.X, p.Y)).ToArray();

    private static SharpPen MakeSharpPen(float width, SharpDrawing.JointStyle join, SharpDrawing.EndCapStyle cap, float[]? pattern = null) =>
        pattern == null
            ? new SolidPen(new SharpDrawing.Processing.PenOptions(Sharp.Color.White, width) { JointStyle = join, EndCapStyle = cap })
            : new PatternPen(new SharpDrawing.Processing.PenOptions(Sharp.Color.White, width, pattern) { JointStyle = join, EndCapStyle = cap });

    private (double Mean, int Max, int Count) Compare(string name, byte[] a, byte[] b)
    {
        double sum = 0;
        int max = 0, count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            sum += d;
            max = Math.Max(max, d);
            if (d > 64)
            {
                count++;
            }
        }

        double mean = sum / a.Length;
        string line = $"{name,-34} mean {mean,6:F3}  max {max,3}  pixels>64: {count}";
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("ONEIMAGE_STROKE_REPORT") is { Length: > 0 } report)
        {
            File.AppendAllText(report, line + Environment.NewLine);
        }

        return (mean, max, count);
    }

    [Theory]
    [MemberData(nameof(JoinCases))]
    public void JoinsOnOpenPolylines(string join, float width)
    {
        foreach ((string shape, Vector2[] pts) in new[] { ("zigzag", ZigZag), ("sharp", Sharp2) })
        {
            var ours = OursAlpha(Path.Polyline(pts), new Pen(Brush.Solid(new Rgba32(255, 255, 255, 255)), width, Join(join)));
            var theirs = TheirsAlpha(new SharpDrawing.Path(new LinearLineSegment(P(pts))), MakeSharpPen(width, SharpJoin(join), SharpDrawing.EndCapStyle.Butt));
            var (mean, _, _) = Compare($"{shape} {join} w{width}", ours, theirs);
            Assert.True(mean < 0.8, $"{shape} {join} w{width}: mean {mean}");
        }
    }

    [Theory]
    [InlineData("butt", 2.5f)]
    [InlineData("round", 2.5f)]
    [InlineData("square", 2.5f)]
    [InlineData("butt", 12f)]
    [InlineData("round", 12f)]
    [InlineData("square", 12f)]
    public void Caps(string cap, float width)
    {
        Vector2[] pts = [new(20, 30), new(115, 45), new(60, 110)];
        StrokeCap oc = cap switch { "round" => StrokeCap.Round, "square" => StrokeCap.Square, _ => StrokeCap.Butt };
        var tc = cap switch { "round" => SharpDrawing.EndCapStyle.Round, "square" => SharpDrawing.EndCapStyle.Square, _ => SharpDrawing.EndCapStyle.Butt };
        var ours = OursAlpha(Path.Polyline(pts), new Pen(Brush.Solid(new Rgba32(255, 255, 255, 255)), width, StrokeJoin.Round, oc));
        var theirs = TheirsAlpha(new SharpDrawing.Path(new LinearLineSegment(P(pts))), MakeSharpPen(width, SharpDrawing.JointStyle.Round, tc));
        var (mean, _, _) = Compare($"cap {cap} w{width}", ours, theirs);
        Assert.True(mean < 0.8, $"mean {mean}");
    }

    [Theory]
    [InlineData("miter", 1f)]
    [InlineData("miter", 2.5f)]
    [InlineData("round", 12f)]
    [InlineData("square", 12f)]
    public void ClosedPolygon(string join, float width)
    {
        var ours = OursAlpha(Path.Polygon(Pentagon), new Pen(Brush.Solid(new Rgba32(255, 255, 255, 255)), width, Join(join)));
        var theirs = TheirsAlpha(new Polygon(new LinearLineSegment(P(Pentagon))), MakeSharpPen(width, SharpJoin(join), SharpDrawing.EndCapStyle.Butt));
        var (mean, _, _) = Compare($"pentagon {join} w{width}", ours, theirs);
        Assert.True(mean < 0.8, $"mean {mean}");
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2.5f)]
    [InlineData(12f)]
    public void Curves(float width)
    {
        var ours = new OneImage.Drawing.PathBuilder().MoveTo(10, 100).CubicBezierTo(20, -10, 100, 140, 125, 20).Build();
        var theirs = new SharpDrawing.PathBuilder().AddCubicBezier(new(10, 100), new(20, -10), new(100, 140), new(125, 20)).Build();
        var a = OursAlpha(ours, new Pen(Brush.Solid(new Rgba32(255, 255, 255, 255)), width, StrokeJoin.Round, StrokeCap.Round));
        var b = TheirsAlpha(theirs, MakeSharpPen(width, SharpDrawing.JointStyle.Round, SharpDrawing.EndCapStyle.Round));
        var (mean, _, _) = Compare($"cubic w{width}", a, b);
        Assert.True(mean < 0.8, $"mean {mean}");
        var circle = OursAlpha(Path.Circle(70, 70, 50), new Pen(Brush.Solid(new Rgba32(255, 255, 255, 255)), width));
        var circleTheirs = TheirsAlpha(new EllipsePolygon(70, 70, 50), MakeSharpPen(width, SharpDrawing.JointStyle.Miter, SharpDrawing.EndCapStyle.Butt));
        Compare($"circle w{width}", circle, circleTheirs);
        Assert.True(Compare($"circle w{width}", circle, circleTheirs).Mean < 0.8);
    }

    [Theory]
    [InlineData("dash", 1f)]
    [InlineData("dash", 3f)]
    [InlineData("dot", 3f)]
    [InlineData("dashdot", 3f)]
    [InlineData("dashdotdot", 3f)]
    public void Dashes(string kind, float width)
    {
        Vector2[] pts = [new(10, 20), new(125, 30), new(30, 100), new(110, 120)];
        float[] pattern = kind switch
        {
            "dot" => [1, 1],
            "dashdot" => [3, 1, 1, 1],
            "dashdotdot" => [3, 1, 1, 1, 1, 1],
            _ => [3, 1],
        };
        var ours = OursAlpha(Path.Polyline(pts), new Pen(Brush.Solid(new Rgba32(255, 255, 255, 255)), width, dashPattern: pattern.Select(v => v * width).ToArray()));
        var theirs = TheirsAlpha(new SharpDrawing.Path(new LinearLineSegment(P(pts))), MakeSharpPen(width, SharpDrawing.JointStyle.Miter, SharpDrawing.EndCapStyle.Butt, pattern));
        var (mean, _, _) = Compare($"{kind} w{width}", ours, theirs);
        Assert.True(mean < 0.8, $"mean {mean}");
    }
}
