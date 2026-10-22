using System.Numerics;
using OneImage.Drawing;
using Xunit;
using Path = OneImage.Drawing.Path;
using PathBuilder = OneImage.Drawing.PathBuilder;

namespace OneImage.Tests.Drawing;

public class StrokeTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);

    private static byte[] Alpha(Path path, Pen pen, int w = 100, int h = 100, float opacity = 1f, Matrix3x2? transform = null) =>
        Image.FromPixels<Rgba32>(new Rgba32[w * h], w, h).Draw(pen, path, opacity: opacity, transform: transform).ToArray<Rgba32>().Select(p => p.A).ToArray();

    private static Pen P(float width, StrokeJoin join = StrokeJoin.Miter, StrokeCap cap = StrokeCap.Butt, float miter = 4f, float[]? dash = null, float offset = 0f) =>
        new(Brush.Solid(White), width, join, cap, miter, dash, offset);

    private static byte At(byte[] a, int x, int y, int w = 100) => a[(y * w) + x];

    [Fact]
    public void ButtLineCoversExactlyItsRectangle()
    {
        byte[] a = Alpha(Path.Polyline([new(10, 20), new(50, 20)]), P(4));
        Assert.Equal(255, At(a, 30, 18));
        Assert.Equal(255, At(a, 30, 21));
        Assert.Equal(0, At(a, 30, 17));
        Assert.Equal(0, At(a, 30, 22));
        Assert.Equal(255, At(a, 10, 20));
        Assert.Equal(0, At(a, 9, 20));
        Assert.Equal(255, At(a, 49, 20));
        Assert.Equal(0, At(a, 50, 20));
    }

    [Fact]
    public void LineBetweenPixelCentersHasHalfCoverageEdges()
    {
        byte[] a = Alpha(Path.Polyline([new(10, 20), new(50, 20)]), P(1));
        Assert.Equal(128, At(a, 30, 19));
        Assert.Equal(128, At(a, 30, 20));
        Assert.Equal(0, At(a, 30, 21));
    }

    [Fact]
    public void Caps()
    {
        Path line = Path.Polyline([new(10, 20), new(50, 20)]);
        byte[] square = Alpha(line, P(4, cap: StrokeCap.Square));
        Assert.Equal(255, At(square, 8, 20));
        Assert.Equal(255, At(square, 51, 21));
        Assert.Equal(0, At(square, 7, 20));
        byte[] round = Alpha(line, P(4, cap: StrokeCap.Round));
        Assert.Equal(255, At(round, 9, 19));
        Assert.Equal(0, At(round, 8, 17));
        Assert.True(At(round, 8, 20) is > 100 and < 255);
        Assert.Equal(0, At(round, 7, 20));
    }

    [Fact]
    public void JoinsAtARightAngle()
    {
        Path corner = Path.Polyline([new(10, 10), new(50, 10), new(50, 50)]);
        Assert.Equal(255, At(Alpha(corner, P(6)), 52, 7));
        Assert.Equal(0, At(Alpha(corner, P(6, StrokeJoin.Bevel)), 52, 7));
        Assert.Equal(0, At(Alpha(corner, P(6, miter: 1.2f)), 52, 7));
        Assert.Equal(255, At(Alpha(corner, P(6, miter: 1.5f)), 52, 7));
        byte[] round = Alpha(corner, P(6, StrokeJoin.Round));
        Assert.True(At(round, 52, 7) < 255);
        Assert.Equal(255, At(round, 51, 9));
        Assert.Equal(255, At(Alpha(corner, P(6, StrokeJoin.Bevel)), 49, 11));
        Assert.True(At(Alpha(corner, P(6, StrokeJoin.Square)), 52, 7) > 0); // squared off: between a bevel and a miter
    }

    [Fact]
    public void ClosedFigureHasAHoleAndMiterCorners()
    {
        byte[] a = Alpha(Path.Rectangle(20, 20, 40, 30), P(4));
        Assert.Equal(0, At(a, 40, 35));
        Assert.Equal(255, At(a, 19, 35));
        Assert.Equal(255, At(a, 21, 35));
        Assert.Equal(0, At(a, 17, 35));
        Assert.Equal(0, At(a, 22, 35));
        Assert.Equal(255, At(a, 18, 18));
        Assert.Equal(255, At(a, 61, 51));
    }

    [Fact]
    public void DashesAndOffsets()
    {
        Path line = Path.Polyline([new(0, 10.5f), new(100, 10.5f)]);
        byte[] a = Alpha(line, P(1, dash: [10, 10]));
        Assert.Equal(255, At(a, 5, 10));
        Assert.Equal(0, At(a, 15, 10));
        Assert.Equal(255, At(a, 25, 10));
        byte[] b = Alpha(line, P(1, dash: [10, 10], offset: 10));
        Assert.Equal(0, At(b, 5, 10));
        Assert.Equal(255, At(b, 15, 10));
        Assert.Equal(b, Alpha(line, P(1, dash: [10, 10], offset: -10)));
        Assert.Equal(b, Alpha(line, P(1, dash: [10, 10], offset: 30)));
        // An odd pattern repeats once so on and off alternate: 5 on, 10 off, 5 on, 5 off, 10 on, 5 off.
        byte[] c = Alpha(line, P(1, dash: [5, 10, 5]));
        Assert.Equal(255, At(c, 2, 10));
        Assert.Equal(0, At(c, 7, 10));
        Assert.Equal(255, At(c, 17, 10));
        Assert.Equal(0, At(c, 22, 10));
        Assert.Equal(255, At(c, 30, 10));
        Assert.Equal(0, At(c, 37, 10));
        Assert.Equal(255, At(c, 42, 10));
    }

    [Fact]
    public void DashedCapsExtendEachDash()
    {
        byte[] a = Alpha(Path.Polyline([new(0, 50), new(100, 50)]), P(6, cap: StrokeCap.Square, dash: [10, 20]));
        Assert.Equal(255, At(a, 11, 49)); // the first dash runs 0..10, its cap to 13
        Assert.Equal(255, At(a, 12, 49));
        Assert.Equal(0, At(a, 15, 49));
        Assert.Equal(255, At(a, 28, 49)); // the next begins at 30 and reaches back to 27
    }

    [Fact]
    public void ADashedClosedFigureJoinsAcrossItsStart()
    {
        // The perimeter is 140: one dash covers 0..100 and the next starts at 120, running through the start corner.
        byte[] merged = Alpha(Path.Rectangle(20, 20, 40, 30), P(4, dash: [100, 20]));
        Assert.Equal(255, At(merged, 18, 18)); // a mitered corner, not two butt ends meeting
        // Starting the pattern elsewhere (offset 20) puts a gap at the corner instead of a join.
        byte[] cut = Alpha(Path.Rectangle(20, 20, 40, 30), P(4, dash: [100, 20], offset: 110));
        Assert.Equal(0, At(cut, 18, 18));
    }

    [Fact]
    public void ZeroLengthDashesAreDotsWithRoundCaps()
    {
        Path line = Path.Polyline([new(5, 50), new(95, 50)]);
        byte[] a = Alpha(line, P(6, cap: StrokeCap.Round, dash: [0, 10]));
        Assert.Equal(255, At(a, 15, 49));
        Assert.Equal(255, At(a, 25, 50));
        Assert.Equal(0, At(a, 20, 49));
        Assert.All(Alpha(line, P(6, dash: [0, 10])), v => Assert.Equal(0, v));
    }

    [Fact]
    public void SinglePointFigures()
    {
        Path dot = new PathBuilder().MoveTo(50, 50).LineTo(50, 50).Close().Build();
        Assert.Equal(255, At(Alpha(dot, P(10, cap: StrokeCap.Round)), 50, 50));
        Assert.Equal(255, At(Alpha(dot, P(10, cap: StrokeCap.Square)), 46, 54));
        Assert.All(Alpha(dot, P(10)), v => Assert.Equal(0, v));
        Assert.True(new PathBuilder().MoveTo(50, 50).Build().GenerateOutline(10, cap: StrokeCap.Round).IsEmpty);
        Assert.True(Path.Empty.GenerateOutline(3).IsEmpty);
        Assert.True(Path.Circle(50, 50, 20).GenerateOutline(0).IsEmpty);
    }

    [Fact]
    public void OverlapsArePaintedOnce()
    {
        Path cross = Path.Combine(Path.Polyline([new(10, 10), new(90, 90)]), Path.Polyline([new(10, 90), new(90, 10)]));
        byte[] a = Alpha(cross, P(10), opacity: 0.5f);
        Assert.Equal(128, At(a, 50, 50));
        Assert.True(a.Max() <= 128);
        // One path doubling back on itself (a hairpin) and a figure eight too.
        Path hairpin = Path.Polyline([new(10, 50), new(80, 50), new(10, 52)]);
        Assert.True(Alpha(hairpin, P(8, StrokeJoin.Round), opacity: 0.5f).Max() <= 128);
        Path eight = new PathBuilder().MoveTo(10, 10).LineTo(60, 60).LineTo(60, 10).LineTo(10, 60).Close().Build();
        byte[] e = Alpha(eight, P(6), opacity: 0.5f);
        Assert.True(e.Max() <= 129);
    }

    [Fact]
    public void HairpinsAndTightTurnsLeaveNoHoles()
    {
        byte[] a = Alpha(Path.Polyline([new(10, 50), new(60, 50), new(10, 50)]), P(6, StrokeJoin.Round));
        Assert.Equal(255, At(a, 30, 49));
        Assert.Equal(255, At(a, 61, 49));
        byte[] two = Alpha(Path.Polygon([new(10, 50), new(60, 50)]), P(6, StrokeJoin.Square));
        Assert.Equal(255, At(two, 30, 49));
        Assert.Equal(255, At(two, 62, 49));
        Assert.Equal(255, At(two, 7, 50));
        // Wider than the zigzag is long: the inside of the turns must stay solid.
        byte[] zig = Alpha(Path.Polyline([new(10, 10), new(14, 60), new(18, 10)]), P(20));
        Assert.Equal(255, At(zig, 14, 35));
        Assert.Equal(255, At(zig, 14, 20));
        byte[] closed = Alpha(Path.Circle(50, 50, 5), P(30));
        Assert.Equal(255, At(closed, 50, 50));
        Assert.Equal(255, At(closed, 50, 66));
    }

    [Fact]
    public void TransformScalesTheStroke()
    {
        Path line = Path.Polyline([new(10, 20), new(30, 20)]);
        byte[] a = Alpha(line, P(2), transform: Matrix3x2.CreateScale(2));
        Assert.Equal(255, At(a, 40, 38));
        Assert.Equal(255, At(a, 40, 41));
        Assert.Equal(0, At(a, 40, 37));
        Assert.Equal(0, At(a, 40, 42));
    }

    [Fact]
    public void CurvesStayOnTheirCenterLine()
    {
        Path arc = Path.Circle(50, 50, 30);
        byte[] a = Alpha(arc, P(4));
        Assert.Equal(255, At(a, 50 + 30, 50));
        Assert.Equal(255, At(a, 50, 50 - 30));
        Assert.Equal(0, At(a, 50, 50));
        Assert.Equal(0, At(a, 50 + 33, 50));
        Assert.Equal(0, At(a, 50 + 26, 50));
        Assert.True(At(a, 50 + 27, 50) < 30);
    }

    [Fact]
    public void ImageAndCanvasAreByteIdentical()
    {
        Path scene = Path.Combine(
            Path.Star(150, 120, 6, 40, 90, 10),
            Path.ParseSvg("M10 200 C 60 120 120 300 290 230 L 270 250 Q 150 280 40 245"),
            Path.Polyline([new(5, 5), new(290, 30), new(20, 40)]));
        Pen[] pens =
        [
            P(7, StrokeJoin.Round, StrokeCap.Round),
            P(3, StrokeJoin.Miter, StrokeCap.Square, dash: [9, 4, 2, 4], offset: 3),
            new Pen(Brush.Solid(new Rgba32(10, 200, 90, 150)), 12, StrokeJoin.Bevel),
        ];
        foreach (PixelFormat format in new[] { PixelFormat.Rgba8, PixelFormat.Rgb8 })
        {
            foreach (bool linear in new[] { false, true })
            {
                foreach (Pen pen in pens)
                {
                    using var c = new Canvas(300, 260, format);
                    c.Clear(new Rgba32(30, 60, 90, 255));
                    Image before = c.Snapshot();
                    Rgba32[] viaImage = before.Draw(pen, scene, linear: linear).ToArray<Rgba32>();
                    c.Draw(pen, scene, linear: linear);
                    Assert.Equal(viaImage, c.Snapshot().ToArray<Rgba32>());
                    Assert.Equal(viaImage, before.Draw(pen, scene, linear: linear).ToArray<Rgba32>(maxDegree: 1));
                    Assert.Equal(viaImage, before.Draw(pen, scene, linear: linear).ToArray<Rgba32>(maxDegree: 8));
                }
            }
        }
    }

    [Fact]
    public void OutlineIsDeterministicAndFillsLikeTheStroke()
    {
        Path p = Path.ParseSvg("M10 10 C 40 90 90 -20 120 60 S 10 120 100 100");
        Pen pen = P(5, StrokeJoin.Round, StrokeCap.Round, dash: [8, 3]);
        Assert.Equal(p.GenerateOutline(pen).ToSvg(), p.GenerateOutline(pen).ToSvg());
        Rgba32[] direct = Image.FromPixels<Rgba32>(new Rgba32[140 * 130], 140, 130).Draw(pen, p).ToArray<Rgba32>();
        Rgba32[] viaFill = Image.FromPixels<Rgba32>(new Rgba32[140 * 130], 140, 130).Fill(White, p.GenerateOutline(pen)).ToArray<Rgba32>();
        Assert.Equal(direct, viaFill);
    }

    [Fact]
    public void PenValidation()
    {
        Brush b = Brush.Solid(White);
        Assert.Throws<ArgumentNullException>(() => new Pen(null!));
        foreach (float w in new[] { -1f, float.NaN, float.PositiveInfinity, Pen.MaxWidth * 2 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, w));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, miterLimit: 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, miterLimit: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, join: (StrokeJoin)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, cap: (StrokeCap)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, dashPattern: [1, float.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, dashPattern: [1, -1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, dashPattern: [0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, dashPattern: [1, float.PositiveInfinity]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, dashPattern: new float[Pen.MaxDashEntries + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(b, 1, dashPattern: [1, 1], dashOffset: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Path.Circle(5, 5, 3).GenerateOutline(1, tolerance: 0));
        Assert.Throws<ArgumentNullException>(() => ((Path)null!).GenerateOutline(1));
        Assert.Throws<ArgumentException>(() => Image.FromPixels<Rgba32>(new Rgba32[100], 10, 10).DrawBeziers(P(1), [new(1, 1), new(2, 2), new(3, 3)]));
        Assert.Throws<ArgumentNullException>(() => Image.FromPixels<Rgba32>(new Rgba32[100], 10, 10).Draw((Pen)null!, Path.Empty));
    }

    [Fact]
    public void PresetsScaleTheirPatternWithTheWidth()
    {
        Assert.Equal(new float[] { 9, 3 }, Pen.Dash(White, 3).DashPattern.ToArray());
        Assert.Equal(new float[] { 2, 2 }, Pen.Dot(White, 2).DashPattern.ToArray());
        Assert.Equal(new float[] { 3, 1, 1, 1 }, Pen.DashDot(White).DashPattern.ToArray());
        Assert.Equal(6, Pen.DashDotDot(White, 2).DashPattern.Length);
        Pen p = Pen.Solid(White, 2).WithJoin(StrokeJoin.Round, 2).WithCap(StrokeCap.Square).WithDash([4, 2], 1).WithWidth(5);
        Assert.Equal((5f, StrokeJoin.Round, StrokeCap.Square, 2f, 1f), (p.Width, p.Join, p.Cap, p.MiterLimit, p.DashOffset));
        Assert.Equal(new float[] { 4, 2 }, p.DashPattern.ToArray());
    }

    [Fact]
    public void HostileGeometryIsBoundedOrTyped()
    {
        // A huge width on a small image runs and covers it.
        byte[] huge = Alpha(Path.Polyline([new(40, 40), new(60, 60)]), P(Pen.MaxWidth, StrokeJoin.Round, StrokeCap.Round));
        Assert.All(huge, v => Assert.Equal(255, v));
        Assert.Equal(255, At(Alpha(Path.Polyline([new(40, 40), new(60, 60)]), P(Pen.MaxWidth)), 50, 50));

        // Far-away and huge coordinates.
        Alpha(Path.Polyline([new(-1e9f, -1e9f), new(1e9f, 1e9f), new(1e9f, -1e9f)]), P(3, StrokeJoin.Round, StrokeCap.Round));
        Alpha(Path.Polyline([new(1e30f, 1e30f), new(-1e30f, 5)]), P(3, StrokeJoin.Round));

        // Zero-length segments everywhere.
        var rep = new Vector2[200];
        Array.Fill(rep, new Vector2(30, 30));
        Assert.All(Alpha(Path.Polyline(rep), P(5, cap: StrokeCap.Round)), v => Assert.Equal(0, v));
        Vector2[] stutter = Enumerable.Range(0, 300).Select(i => new Vector2(10 + (i / 3), 10 + (i / 3 % 7))).ToArray();
        Alpha(Path.Polyline(stutter), P(3, StrokeJoin.Round, StrokeCap.Square, dash: [2, 1]));

        // Dashes too small for the length fail typed, not by running out of memory.
        Path longLine = Path.Polyline([new(0, 5), new(1e7f, 5)]);
        Assert.Throws<ImageLimitException>(() => longLine.GenerateOutline(2, dashPattern: [0.001f, 0.001f]));
        Assert.Throws<ImageLimitException>(() => Image.FromPixels<Rgba32>(new Rgba32[100], 10, 10).Draw(P(1, dash: [0.0001f, 0.0001f]), longLine));
        // A zero-length pattern period cannot be built.
        Assert.Throws<ArgumentOutOfRangeException>(() => P(1, dash: [0, 0]));
    }

    [Fact]
    public void MillionsOfSegmentsRunBounded()
    {
        var pts = new Vector2[1_000_000];
        for (int i = 0; i < pts.Length; i++)
        {
            double a = i * 0.01;
            pts[i] = new Vector2((float)(50000 + (i * 0.02) * Math.Cos(a)), (float)(50000 + (i * 0.02) * Math.Sin(a)));
        }

        Path huge = Path.Polyline(pts);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Path outline = huge.GenerateOutline(2.5f, StrokeJoin.Round);
        Assert.False(outline.IsEmpty);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), sw.Elapsed.ToString());
        // 20 million segments: the outline would exceed its limit and fail typed.
        var big = new PathBuilder().MoveTo(0, 0);
        for (int i = 0; i < 4_000_000; i++)
        {
            big.CubicBezierTo(i % 7, 3, 5, i % 11, i % 13, 1);
        }

        Assert.Throws<ImageLimitException>(() => big.Build().GenerateOutline(1, StrokeJoin.Round, StrokeCap.Round));
    }

    [Fact]
    public void ShapeHelpersDrawTheirShapes()
    {
        Image blank = Image.FromPixels<Rgba32>(new Rgba32[100 * 100], 100, 100);
        Pen pen = P(2);
        Assert.Equal(Alpha(Path.Polyline([new(10, 10), new(80, 40)]), pen), blank.DrawLine(pen, new(10, 10), new(80, 40)).ToArray<Rgba32>().Select(p => p.A).ToArray());
        Assert.Equal(Alpha(Path.Rectangle(10, 12, 50, 30), pen), blank.DrawRectangle(pen, 10, 12, 50, 30).ToArray<Rgba32>().Select(p => p.A).ToArray());
        Assert.Equal(Alpha(Path.Ellipse(50, 50, 30, 20), pen), blank.DrawEllipse(pen, 50, 50, 30, 20).ToArray<Rgba32>().Select(p => p.A).ToArray());
        Vector2[] tri = [new(10, 10), new(80, 20), new(40, 80)];
        Assert.Equal(Alpha(Path.Polygon(tri), pen), blank.DrawPolygon(pen, tri).ToArray<Rgba32>().Select(p => p.A).ToArray());
        Assert.Equal(Alpha(Path.Polyline(tri), pen), blank.DrawPolyline(pen, tri).ToArray<Rgba32>().Select(p => p.A).ToArray());
        Vector2[] bez = [new(10, 50), new(30, 0), new(60, 100), new(90, 50)];
        Path bp = new PathBuilder().MoveTo(10, 50).CubicBezierTo(30, 0, 60, 100, 90, 50).Build();
        Assert.Equal(Alpha(bp, pen), blank.DrawBeziers(pen, bez).ToArray<Rgba32>().Select(p => p.A).ToArray());
        Assert.Equal(blank.Draw(pen, bp).ToArray<Rgba32>(), blank.Draw(White, 2, bp).ToArray<Rgba32>());
        using var canvas = new Canvas(100, 100, PixelFormat.Rgba8);
        canvas.DrawLine(pen, new(10, 10), new(80, 40));
        canvas.DrawRectangle(pen, 10, 12, 50, 30);
        canvas.DrawEllipse(pen, 50, 50, 30, 20);
        canvas.DrawPolygon(pen, tri);
        canvas.DrawPolyline(pen, tri);
        canvas.DrawBeziers(pen, bez);
        canvas.Draw(White, 2, bp);
        Assert.Equal(blank.DrawLine(pen, new(10, 10), new(80, 40)).DrawRectangle(pen, 10, 12, 50, 30).DrawEllipse(pen, 50, 50, 30, 20).DrawPolygon(pen, tri).DrawPolyline(pen, tri).DrawBeziers(pen, bez).Draw(White, 2, bp).ToArray<Rgba32>(), canvas.Snapshot().ToArray<Rgba32>());
    }

    [Fact]
    public void GradientsAndOtherBrushesCanPaintAPen()
    {
        // Any Brush goes through WithBrush; here the solid brush stands in for the others.
        Pen red = Pen.Solid(new Rgba32(255, 0, 0, 255), 4).WithBrush(Brush.Solid(new Rgba32(0, 0, 255, 255)));
        Rgba32[] px = Image.FromPixels<Rgba32>(new Rgba32[100 * 100], 100, 100).Draw(red, Path.Polyline([new(10, 50), new(90, 50)])).ToArray<Rgba32>();
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[(50 * 100) + 50]);
    }
}
