using OneImage.Drawing;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Xunit;
using Xunit.Sdk;
using IntersectionRule = OneImage.Drawing.IntersectionRule;
using Path = OneImage.Drawing.Path;
using PathBuilder = OneImage.Drawing.PathBuilder;
using Sharp = SixLabors.ImageSharp;
using SharpDrawing = SixLabors.ImageSharp.Drawing;

namespace OneImage.Tests.Drawing;

/// <summary>Coverage agreement with SixLabors.ImageSharp.Drawing 2.1.7: the alpha plane of a white fill on a transparent image.</summary>
public class ImageSharpCoverageTests
{
    private static readonly System.Numerics.Vector2[] Pentagram = Enumerable.Range(0, 5)
        .Select(i => new System.Numerics.Vector2(
            64f + (56f * MathF.Sin(i * 4 * MathF.PI / 5)),
            66f - (56f * MathF.Cos(i * 4 * MathF.PI / 5))))
        .ToArray();

    private static readonly System.Numerics.Vector2[] Blob = Enumerable.Range(0, 1000)
        .Select(i =>
        {
            double a = i * 2 * Math.PI / 1000;
            double r = 55 + (12 * Math.Sin(7 * a)) + (5 * Math.Cos(13 * a));
            return new System.Numerics.Vector2((float)(70 + (r * Math.Cos(a))), (float)(70 + (r * Math.Sin(a))));
        })
        .ToArray();

    private static readonly System.Numerics.Vector2[] StarPoints = Enumerable.Range(0, 14)
        .Select(i =>
        {
            double a = (i * Math.PI / 7) - (Math.PI / 2);
            double r = (i & 1) == 0 ? 58 : 25;
            return new System.Numerics.Vector2((float)(64 + (r * Math.Cos(a))), (float)(64 + (r * Math.Sin(a))));
        })
        .ToArray();

    public static TheoryData<string> Cases() =>
        ["rect", "rect-thin", "circle", "ellipse", "star", "pentagram", "blob", "cubic", "svg-arc", "svg-mixed", "rounded", "multi-hole"];

    private static (Path Ours, IPath Theirs) Make(string name)
    {
        switch (name)
        {
            case "rect": return (Path.Rectangle(10.3f, 7.6f, 90.4f, 60.25f), new RectangularPolygon(10.3f, 7.6f, 90.4f, 60.25f));
            case "rect-thin": return (Path.Rectangle(20.5f, 30.1f, 0.4f, 70f), new RectangularPolygon(20.5f, 30.1f, 0.4f, 70f));
            case "circle": return (Path.Circle(64.3f, 63.7f, 50.2f), new EllipsePolygon(64.3f, 63.7f, 50.2f));
            case "ellipse": return (Path.Ellipse(64f, 64f, 55.5f, 22.25f), new EllipsePolygon(64f, 64f, 111f, 44.5f));
            case "star": return (Path.Polygon(StarPoints), new Polygon(new LinearLineSegment(StarPoints.Select(v => new Sharp.PointF(v.X, v.Y)).ToArray())));
            case "pentagram": return (Path.Polygon(Pentagram), new Polygon(new LinearLineSegment(Pentagram.Select(v => new Sharp.PointF(v.X, v.Y)).ToArray())));
            case "blob": return (Path.Polygon(Blob), new Polygon(new LinearLineSegment(Blob.Select(v => new Sharp.PointF(v.X, v.Y)).ToArray())));
            case "cubic":
                {
                    var ours = new PathBuilder().MoveTo(10, 100).CubicBezierTo(20, -10, 100, 140, 120, 20).CubicBezierTo(90, 60, 40, 80, 10, 100).Close().Build();
                    var theirs = new SharpDrawing.PathBuilder()
                        .AddCubicBezier(new(10, 100), new(20, -10), new(100, 140), new(120, 20))
                        .AddCubicBezier(new(120, 20), new(90, 60), new(40, 80), new(10, 100))
                        .CloseFigure()
                        .Build();
                    return (ours, theirs);
                }

            case "svg-arc": return Svg("M 20 64 A 40 30 20 1 1 100 80 L 64 120 Z");
            case "svg-mixed": return Svg("M10,10 h100 v50 q-50,60 -100,0 z M30,30 c10,-20 40,-20 50,0 s20,30 -10,40 z");
            case "rounded": return Svg("M 30 10 H 98 A 20 20 0 0 1 118 30 V 98 A 20 20 0 0 1 98 118 H 30 A 20 20 0 0 1 10 98 V 30 A 20 20 0 0 1 30 10 Z");
            case "multi-hole": return Svg("M5 5 H120 V120 H5 Z M30 30 V90 H90 V30 Z");
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private static (Path Ours, IPath Theirs) Svg(string d)
    {
        Assert.True(Path.TryParseSvg(d, out Path? ours));
        Assert.True(SharpDrawing.Path.TryParseSvgPath(d, out IPath? theirs));
        return (ours!, theirs);
    }

    private static byte[] OursAlpha(Path path, int w, int h, bool aa, IntersectionRule rule)
    {
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[w * h], w, h).Fill(new Rgba32(255, 255, 255, 255), path, aa, rule).ToArray<Rgba32>();
        return px.Select(p => p.A).ToArray();
    }

    private static byte[] TheirsAlpha(IPath path, int w, int h, bool aa, SharpDrawing.IntersectionRule rule)
    {
        using var img = new Sharp.Image<Sharp.PixelFormats.Rgba32>(w, h);
        var options = new DrawingOptions
        {
            GraphicsOptions = new Sharp.GraphicsOptions { Antialias = aa },
            ShapeOptions = new ShapeOptions { IntersectionRule = rule },
        };
        img.Mutate(c => c.Fill(options, Sharp.Color.White, path));
        var result = new byte[w * h];
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < h; y++)
            {
                Sharp.PixelFormats.Rgba32[] row = rows.GetRowSpan(y).ToArray();
                for (int x = 0; x < w; x++)
                {
                    result[(y * w) + x] = row[x].A;
                }
            }
        });
        return result;
    }

    private static (double Mean, int Max, int Over1) Compare(byte[] a, byte[] b)
    {
        long sum = 0;
        int max = 0, over = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            sum += d;
            max = Math.Max(max, d);
            if (d > 1)
            {
                over++;
            }
        }

        return ((double)sum / a.Length, max, over);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AntialiasedCoverageAgrees(string name)
    {
        (Path ours, IPath theirs) = Make(name);
        const int size = 140;
        foreach ((IntersectionRule o, SharpDrawing.IntersectionRule t) in new[] { (IntersectionRule.NonZero, SharpDrawing.IntersectionRule.NonZero), (IntersectionRule.EvenOdd, SharpDrawing.IntersectionRule.EvenOdd) })
        {
            (double mean, int max, int over) = Compare(OursAlpha(ours, size, size, true, o), TheirsAlpha(theirs, size, size, true, t));
            Console.WriteLine($"COVERAGE {name} {o}: mean {mean:F4} max {max} pixels>1 {over}");
            Assert.True(mean <= 0.6, $"{name} {o}: mean difference {mean}");
        }
    }

    // ImageSharp's aliased fill truncates crossings to whole pixels and samples rows near their bottom, so axis-aligned shapes at
    // fractional positions differ from the pixel-center rule used here by up to a pixel row or column; those are tested separately.
    [Theory]
    [MemberData(nameof(Cases))]
    public void AliasedCoverageAgrees(string name)
    {
        if (name is "rect" or "rect-thin")
        {
            return;
        }

        (Path ours, IPath theirs) = Make(name);
        const int size = 140;
        (double mean, int max, int over) = Compare(OursAlpha(ours, size, size, false, IntersectionRule.NonZero), TheirsAlpha(theirs, size, size, false, SharpDrawing.IntersectionRule.NonZero));
        Console.WriteLine($"ALIASED {name}: mean {mean:F4} max {max} pixels>1 {over}");
        Assert.True(mean <= 1.0, $"{name}: mean difference {mean}");
    }

    [Fact]
    public void AliasedIntegerRectangleIsExact()
    {
        Assert.Equal(
            TheirsAlpha(new RectangularPolygon(10, 7, 90, 60), 140, 140, false, SharpDrawing.IntersectionRule.NonZero),
            OursAlpha(Path.Rectangle(10, 7, 90, 60), 140, 140, false, IntersectionRule.NonZero));
    }

    [Fact]
    public void AliasedFractionalRectangleDiffersOnlyAtItsBorder()
    {
        byte[] ours = OursAlpha(Path.Rectangle(10.3f, 7.6f, 90.4f, 60.25f), 140, 140, false, IntersectionRule.NonZero);
        byte[] theirs = TheirsAlpha(new RectangularPolygon(10.3f, 7.6f, 90.4f, 60.25f), 140, 140, false, SharpDrawing.IntersectionRule.NonZero);
        for (int i = 0; i < ours.Length; i++)
        {
            if (ours[i] != theirs[i])
            {
                int x = i % 140, y = i / 140;
                bool border = (x is >= 9 and <= 12 or >= 99 and <= 102) || (y is >= 6 and <= 9 or >= 66 and <= 69);
                Assert.True(border, $"difference away from the border at ({x},{y})");
            }
        }
    }
}
