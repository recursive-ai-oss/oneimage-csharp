using System.Numerics;
using OneImage.Drawing;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Xunit;
using IntersectionRule = OneImage.Drawing.IntersectionRule;
using Path = OneImage.Drawing.Path;
using Sharp = SixLabors.ImageSharp;
using SharpDrawing = SixLabors.ImageSharp.Drawing;

namespace OneImage.Tests.Drawing;

/// <summary>Boolean results filled by both libraries (ImageSharp.Drawing 2.1.7): the alpha planes of a white fill are compared.</summary>
public class BooleanImageSharpTests
{
    private const int Size = 140;

    // Console output is not shown by every runner; BOOLEAN_LOG names a file that also receives the figures.
    private static void Log(string line)
    {
        Console.WriteLine(line);
        string? file = Environment.GetEnvironmentVariable("BOOLEAN_LOG");
        if (file != null)
        {
            lock (typeof(BooleanImageSharpTests))
            {
                File.AppendAllText(file, line + "\n");
            }
        }
    }

    private static Vector2[] StarPoints(float cx, float cy, int prongs, float inner, float outer)
    {
        var pts = new Vector2[prongs * 2];
        for (int i = 0; i < pts.Length; i++)
        {
            double a = (i * Math.PI / prongs) - (Math.PI / 2);
            double r = (i & 1) == 0 ? outer : inner;
            pts[i] = new Vector2((float)(cx + (r * Math.Cos(a))), (float)(cy + (r * Math.Sin(a))));
        }

        return pts;
    }

    private static Vector2[] Blob(int n, float cx, float cy)
    {
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            double a = i * 2 * Math.PI / n;
            double r = 33 + (7 * Math.Sin(7 * a)) + (3 * Math.Cos(13 * a));
            pts[i] = new Vector2((float)(cx + (r * Math.Cos(a))), (float)(cy + (r * Math.Sin(a))));
        }

        return pts;
    }

    private static Polygon Poly(Vector2[] pts) => new Polygon(new LinearLineSegment(pts.Select(v => new Sharp.PointF(v.X, v.Y)).ToArray()));

    private static Vector2[] Pentagram(float cx, float cy, float r) =>
        Enumerable.Range(0, 5).Select(i => new Vector2(cx + (r * MathF.Sin(i * 4 * MathF.PI / 5)), cy - (r * MathF.Cos(i * 4 * MathF.PI / 5)))).ToArray();

    private static byte[] OursAlpha(Path path)
    {
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[Size * Size], Size, Size).Fill(new Rgba32(255, 255, 255, 255), path).ToArray<Rgba32>();
        return px.Select(p => p.A).ToArray();
    }

    private static byte[] TheirsAlpha(IPath path)
    {
        using var img = new Sharp.Image<Sharp.PixelFormats.Rgba32>(Size, Size);
        var options = new DrawingOptions { ShapeOptions = new ShapeOptions { IntersectionRule = SharpDrawing.IntersectionRule.EvenOdd } };
        img.Mutate(c => c.Fill(options, Sharp.Color.White, path));
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

    private static ClippingOperation Their(BooleanOperation op) => op switch
    {
        BooleanOperation.Union => ClippingOperation.Union,
        BooleanOperation.Intersection => ClippingOperation.Intersection,
        BooleanOperation.Difference => ClippingOperation.Difference,
        _ => ClippingOperation.Xor,
    };

    public static TheoryData<string, BooleanOperation> Cases()
    {
        var data = new TheoryData<string, BooleanOperation>();
        foreach (string name in new[] { "circles", "star-rect", "pentagram-rect", "holes", "coincident", "blob-star", "bowtie-self", "pentagram-eo" })
        {
            foreach (BooleanOperation op in Enum.GetValues<BooleanOperation>())
            {
                data.Add(name, op);
            }
        }

        return data;
    }

    private static (Path S, Path C, IPath TS, IPath TC, IntersectionRule Rule, SharpDrawing.IntersectionRule TheirRule) Make(string name)
    {
        var nz = IntersectionRule.NonZero;
        var tnz = SharpDrawing.IntersectionRule.NonZero;
        switch (name)
        {
            case "circles": return (Path.Circle(55, 65, 40), Path.Circle(88, 72, 35), new EllipsePolygon(55, 65, 40), new EllipsePolygon(88, 72, 35), nz, tnz);
            case "star-rect":
                {
                    Vector2[] s = StarPoints(70, 70, 5, 28, 64);
                    return (Path.Polygon(s), Path.Rectangle(45.5f, 30.25f, 50.5f, 70.75f), Poly(s), new RectangularPolygon(45.5f, 30.25f, 50.5f, 70.75f), nz, tnz);
                }

            case "pentagram-rect":
                {
                    Vector2[] s = Pentagram(70, 72, 60);
                    return (Path.Polygon(s), Path.Rectangle(30, 50, 80, 40), Poly(s), new RectangularPolygon(30, 50, 80, 40), nz, tnz);
                }

            case "pentagram-eo":
                {
                    Vector2[] s = Pentagram(70, 72, 60);
                    return (Path.Polygon(s), Path.Circle(70, 72, 25), Poly(s), new EllipsePolygon(70, 72, 25), IntersectionRule.EvenOdd, SharpDrawing.IntersectionRule.EvenOdd);
                }

            case "holes":
                {
                    Path ours = Path.Combine(Path.Rectangle(10, 10, 100, 100), Path.Rectangle(35, 35, 50, 50));
                    IPath theirs = new ComplexPolygon(new RectangularPolygon(10, 10, 100, 100), new RectangularPolygon(35, 35, 50, 50));
                    return (ours, Path.Rectangle(60, 5, 25, 120), theirs, new RectangularPolygon(60, 5, 25, 120), IntersectionRule.EvenOdd, SharpDrawing.IntersectionRule.EvenOdd);
                }

            case "coincident":
                return (Path.Rectangle(10, 10, 60, 60), Path.Rectangle(70, 10, 40, 60), new RectangularPolygon(10, 10, 60, 60), new RectangularPolygon(70, 10, 40, 60), nz, tnz);
            case "blob-star":
                {
                    Vector2[] b = Blob(1000, 60, 70);
                    Vector2[] s = StarPoints(80, 70, 7, 20, 55);
                    return (Path.Polygon(b), Path.Polygon(s), Poly(b), Poly(s), nz, tnz);
                }

            case "bowtie-self":
                {
                    Vector2[] bow = [new(10, 10), new(110, 110), new(110, 10), new(10, 110)];
                    return (Path.Polygon(bow), Path.Circle(60, 60, 45), Poly(bow), new EllipsePolygon(60, 60, 45), nz, tnz);
                }

            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FilledResultsAgree(string name, BooleanOperation op)
    {
        var (s, c, ts, tc, rule, trule) = Make(name);
        Path ours = s.Boolean(op, c, rule, rule);
        IPath theirs = ts.Clip(new ShapeOptions { ClippingOperation = Their(op), IntersectionRule = trule }, tc);
        byte[] a = OursAlpha(ours), b = TheirsAlpha(theirs);
        long sum = 0;
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            sum += d;
            max = Math.Max(max, d);
        }

        double mean = (double)sum / a.Length;
        Log($"BOOLEAN {name} {op}: mean {mean:F4} max {max} ours {a.Sum(v => (long)v) / 255.0:F1} theirs {b.Sum(v => (long)v) / 255.0:F1}");
        Assert.True(mean <= 0.8, $"{name} {op}: mean difference {mean}");
    }
}
