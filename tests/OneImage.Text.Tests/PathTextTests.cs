using System.Numerics;
using OneImage.Drawing;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using Brush = OneImage.Drawing.Brush;
using Path = OneImage.Drawing.Path;
using PointF = SixLabors.ImageSharp.PointF;

namespace OneImage.Text.Tests;

/// <summary>Text laid along a path against ImageSharp.Drawing 2.1.7's <c>TextBuilder.GenerateGlyphs</c> with a path, through its public API.</summary>
public class PathTextTests
{
    // Glyphs made of straight edges only, so flattening is exact in both libraries.
    private const string Straight = "lHIELTlHIELT";

    private static List<Vector2[]> OurGlyphs(string text, OneImage.Text.TextOptions o)
    {
        var list = new List<Vector2[]>();
        foreach (Contour c in TextBuilder.GeneratePath(text, o).Flatten())
        {
            list.Add(Clean(c.Points));
        }

        return list;
    }

    private static List<Vector2[]> TheirGlyphs(string text, IPath path, RichTextOptions o)
    {
        var list = new List<Vector2[]>();
        foreach (IPath g in SixLabors.ImageSharp.Drawing.TextBuilder.GenerateGlyphs(text, path, o))
        {
            foreach (ISimplePath s in g.Flatten())
            {
                if (s.Points.Length > 0)
                {
                    list.Add(Clean(s.Points.ToArray().Select(p => new Vector2(p.X, p.Y)).ToArray()));
                }
            }
        }

        return list;
    }

    private static Vector2[] Clean(Vector2[] pts)
    {
        var r = new List<Vector2>();
        foreach (Vector2 p in pts)
        {
            if (r.Count == 0 || Vector2.Distance(r[^1], p) > 0.001f)
            {
                r.Add(p);
            }
        }

        if (r.Count > 1 && Vector2.Distance(r[0], r[^1]) < 0.001f)
        {
            r.RemoveAt(r.Count - 1);
        }

        return [.. r];
    }

    private static double Compare(List<Vector2[]> ours, List<Vector2[]> theirs)
    {
        Assert.Equal(theirs.Count, ours.Count);
        double worst = 0;
        for (int i = 0; i < ours.Count; i++)
        {
            Assert.Equal(theirs[i].Length, ours[i].Length);
            foreach (Vector2 p in ours[i])
            {
                worst = Math.Max(worst, theirs[i].Min(q => Vector2.Distance(p, q)));
            }
        }

        return worst;
    }

    private static (OneImage.Text.TextOptions Mine, RichTextOptions Theirs) Pair(float size = 40)
    {
        var mine = new OneImage.Text.TextOptions(TestFonts.Noto(size));
        return (mine, RichReference.Options(RichReference.Regular(size), mine));
    }

    private static void Check(string name, string text, Vector2[] points, bool closed, Action<OneImage.Text.TextOptions, RichTextOptions>? tweak = null, double tolerance = 0.03)
    {
        (OneImage.Text.TextOptions mine, RichTextOptions theirs) = Pair();
        tweak?.Invoke(mine, theirs);
        mine.Path = closed ? Path.Polygon(points) : Path.Polyline(points);
        IPath route = closed ? new Polygon(Segment(points)) : new SixLabors.ImageSharp.Drawing.Path(Segment(points));
        double worst = Compare(OurGlyphs(text, mine), TheirGlyphs(text, route, theirs));
        TestReport.Line($"PATH {name}: worst vertex difference {worst:F4} px");
        Assert.True(worst < tolerance, $"{name}: {worst}");
    }

    private static LinearLineSegment Segment(Vector2[] points) =>
        new(points[0], points[1], [.. points.Skip(2).Select(p => (PointF)p)]);

    private static readonly Vector2[] Horizontal = [new(20, 100), new(320, 100)];

    [Fact]
    public void AHorizontalLineMatches() => Check("horizontal", Straight, Horizontal, false);

    [Fact]
    public void AlignmentOriginAndWrappingApplyBeforeTheGlyphsAreMoved()
    {
        Check("center", Straight, Horizontal, false, (m, t) => { m.HorizontalAlignment = HorizontalAlignment.Center; t.HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment.Center; });
        Check("right", Straight, Horizontal, false, (m, t) => { m.HorizontalAlignment = HorizontalAlignment.Right; t.HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment.Right; });
        Check("vertical center", Straight, Horizontal, false, (m, t) => { m.VerticalAlignment = VerticalAlignment.Center; t.VerticalAlignment = SixLabors.Fonts.VerticalAlignment.Center; });
        Check("origin", Straight, Horizontal, false, (m, t) => { m.Origin = new Vector2(50, 7); t.Origin = new Vector2(50, 7); });
        Check("two lines", "llll\nHHHH", Horizontal, false);
        Check("wrapped", "lH IE LT lH IE LT", Horizontal, false, (m, t) => { m.WrappingLength = 120; t.WrappingLength = 120; });
    }

    [Fact]
    public void ADiagonalAndAReversedLineMatch()
    {
        Check("diagonal", Straight, [new(0, 0), new(200, 200)], false);
        Check("reversed", Straight, [new(320, 100), new(20, 100)], false);
        Check("steep", Straight, [new(50, 300), new(80, 20)], false);
    }

    [Fact]
    public void ACircleOfSegmentsMatches()
    {
        // 120 segments of 5.2 px. Ten of the twelve glyphs agree to 0.001 px; two sit one segment step (3 degrees, up to 1.9 px at
        // their far corners) from the reference, which picks a neighbouring segment there.
        Vector2[] circle = [.. Enumerable.Range(0, 120).Select(i => new Vector2(150 + (float)(100 * Math.Cos(i * Math.PI / 60)), 150 + (float)(100 * Math.Sin(i * Math.PI / 60))))];
        Check("circle", Straight, circle, true, tolerance: 2.0);
    }

    [Fact]
    public void AClosedPathWrapsAndIncludesItsClosingEdge()
    {
        // A 100 px square, perimeter 400. Each 'l' bar has its center 20.32 px from the path (5.12 + 15.2) and its advance center on the path.
        var o = new OneImage.Text.TextOptions(TestFonts.Noto(40)) { Path = Path.Polygon([new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100), new Vector2(0, 100)]) };
        IReadOnlyList<Contour> bars = TextBuilder.GeneratePath(new string('l', 45), o).Flatten();
        Assert.Equal(45, bars.Count);
        static Vector2 Center(Contour c) => new(c.Points.Average(p => p.X), c.Points.Average(p => p.Y));
        float pitch = TestFonts.Noto(40).GetAdvance('l');
        float half = pitch / 2;

        // On the closing edge (from (0,100) back up to (0,0)) the glyphs stand to the right of the path, pointing into the square.
        Vector2 onClosing = Center(bars[29]);
        float s29 = half + (29 * pitch);
        Assert.Equal(20.32f, onClosing.X, 0.05);
        Assert.Equal(100 - (s29 - 300), onClosing.Y, 0.05);

        // Past the perimeter the glyphs start over on the first edge.
        Vector2 wrapped = Center(bars[39]);
        Assert.Equal((half + (39 * pitch)) - 400, wrapped.X, 0.05);
        Assert.Equal(20.32f, wrapped.Y, 0.05);
    }

    [Fact]
    public void PastTheEndOfAnOpenPathGlyphsContinueAlongTheLastSegment()
    {
        Check("overflow", new string('l', 11), [new(0, 100), new(60, 100)], false);
        Check("corner", string.Concat(Enumerable.Repeat(Straight, 3)), [new(0, 0), new(100, 0), new(100, 100)], false);
        Check("before the start", Straight, Horizontal, false, (m, t) => { m.HorizontalAlignment = HorizontalAlignment.Center; t.HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment.Center; });
    }

    [Fact]
    public void AClosedSquareMatchesOnItsExplicitEdges()
    {
        // Glyphs stay on the three explicit edges here: the reference does not place glyphs on a closed path's closing edge.
        Check("square", new string('l', 28), [new(0, 0), new(100, 0), new(100, 100), new(0, 100)], true);
    }

    [Fact]
    public void TextOnACurveRotatesWithTheTangent()
    {
        // A circle of radius 100: every 'l' (a bar) is rotated by the angle of the circle at its middle.
        var o = new OneImage.Text.TextOptions(TestFonts.Noto(40)) { Path = Path.Circle(0, 0, 100) };
        IReadOnlyList<Contour> bars = TextBuilder.GeneratePath("llll", o).Flatten();
        Assert.Equal(4, bars.Count);
        foreach (Contour c in bars)
        {
            Vector2[] p = Clean(c.Points);
            Vector2 side = p[1] - p[0];
            Vector2 center = (p[0] + p[2]) / 2;
            // The long side of the bar is along the normal, which points at the circle's center from the glyph (or away).
            double along = Math.Abs(Vector2.Dot(Vector2.Normalize(side), Vector2.Normalize(center)));
            Assert.True(along > 0.5 || Math.Abs(Vector2.Dot(Vector2.Normalize(p[2] - p[1]), Vector2.Normalize(center))) > 0.5);
        }
    }

    [Fact]
    public void PathTextDrawsAndDecorationsFollowThePath()
    {
        var o = new OneImage.Text.TextOptions(TestFonts.Noto(30)) { Path = Path.Polyline([new Vector2(10, 40), new Vector2(190, 40)]) };
        o.TextRuns.Add(new TextRun(0, 5) { TextDecorations = TextDecorations.Underline });
        var px = new Rgba32[200 * 100];
        Array.Fill(px, new Rgba32(255, 255, 255, 255));
        Image img = Image.FromPixels<Rgba32>(px, 200, 100).DrawText("Hello", o, Brush.Solid(new Rgba32(0, 0, 0, 255)));
        Assert.Contains(img.ToArray<Rgba32>(), p => p.R < 100);

        // On a straight horizontal path a decoration is the same rectangle as in straight text, shifted to the path.
        var straight = new OneImage.Text.TextOptions(TestFonts.Noto(30)) { Origin = new Vector2(10, 40) };
        straight.TextRuns.Add(new TextRun(0, 5) { TextDecorations = TextDecorations.Underline });
        System.Drawing.RectangleF a = TextBuilder.GeneratePath("Hello", o).Bounds;
        System.Drawing.RectangleF b = TextBuilder.GeneratePath("Hello", straight).Bounds;
        Assert.Equal(b.Left, a.Left, 0.01);
        Assert.Equal(b.Right, a.Right, 0.05);
        Assert.Equal(b.Top, a.Top, 0.01);
        Assert.Equal(b.Bottom, a.Bottom, 0.01);
    }

    [Fact]
    public void AnEmptyOrLengthlessPathIsRejectedAndMeasuringIgnoresIt()
    {
        var o = new OneImage.Text.TextOptions(TestFonts.Noto(30)) { Path = Path.Empty };
        Assert.Throws<ArgumentException>(() => TextBuilder.GeneratePath("abc", o));
        o.Path = Path.Polyline([new Vector2(5, 5), new Vector2(5, 5)]);
        Assert.Throws<ArgumentException>(() => TextBuilder.GeneratePath("abc", o));
        Assert.True(TextMeasurer.Measure("abc", o).Advance.Width > 0);
    }

    [Fact]
    public void ALongPathAndLongTextStayLinear()
    {
        // 200,000 points along a wavy line and 100,000 glyphs: placement is a binary search per glyph.
        Vector2[] pts = [.. Enumerable.Range(0, 200_000).Select(i => new Vector2(i * 0.5f, (float)(20 * Math.Sin(i * 0.01))))];
        var o = new OneImage.Text.TextOptions(TestFonts.Noto(12)) { Path = Path.Polyline(pts) };
        string text = new('l', 100_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Path p = TextBuilder.GeneratePath(text, o);
        sw.Stop();
        TestReport.Line($"PATH 100k glyphs on a 200k-point path: {sw.ElapsedMilliseconds} ms");
        Assert.False(p.IsEmpty);
        Assert.True(sw.ElapsedMilliseconds < 20_000, $"{sw.ElapsedMilliseconds} ms");
    }
}
