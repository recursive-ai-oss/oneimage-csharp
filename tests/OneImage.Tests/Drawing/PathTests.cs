using System.Numerics;
using OneImage.Drawing;
using Xunit;
using IntersectionRule = OneImage.Drawing.IntersectionRule;
using Path = OneImage.Drawing.Path;
using PathBuilder = OneImage.Drawing.PathBuilder;

namespace OneImage.Tests.Drawing;

public class PathTests
{
    [Fact]
    public void BuilderMakesFiguresAndDropsLoneMoves()
    {
        Path p = new PathBuilder().MoveTo(1, 1).MoveTo(5, 5).LineTo(9, 5).LineTo(9, 9).Close().MoveTo(20, 20).Build();
        Assert.Equal(1, p.FigureCount);
        Assert.Equal(new System.Drawing.RectangleF(5, 5, 4, 4), p.Bounds);
        Assert.True(new PathBuilder().Build().IsEmpty);
        Assert.True(new PathBuilder().MoveTo(3, 3).Build().IsEmpty);
    }

    [Fact]
    public void SegmentAfterCloseStartsANewFigureAtTheStart()
    {
        Path p = new PathBuilder().MoveTo(0, 0).LineTo(10, 0).LineTo(10, 10).Close().LineTo(0, 10).LineTo(5, 12).Build();
        Assert.Equal(2, p.FigureCount);
        IReadOnlyList<Contour> c = p.Flatten();
        Assert.Equal(new Vector2(0, 0), c[1].Points[0]);
    }

    [Fact]
    public void NonFiniteCoordinatesAreRefused()
    {
        var b = new PathBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => b.MoveTo(float.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.LineTo(0, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.CubicBezierTo(0, 0, float.NegativeInfinity, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.ArcTo(1, 1, float.NaN, false, false, 5, 5));
    }

    [Fact]
    public void LengthAndBoundsOfShapes()
    {
        Assert.Equal(2 * (30 + 20), Path.Rectangle(5, 5, 30, 20).Length, 1e-3);
        Assert.Equal(2 * MathF.PI * 50, Path.Circle(100, 100, 50).Length, 0.35);
        System.Drawing.RectangleF b = Path.Circle(100, 100, 50).Bounds;
        Assert.InRange(b.Left, 49.9f, 50.01f);
        Assert.InRange(b.Right, 149.99f, 150.1f);
        Assert.Equal(new System.Drawing.RectangleF(10, 20, 5, 7), Path.Rectangle(15, 27, -5, -7).Bounds);
    }

    [Fact]
    public void FlattenStaysWithinTolerance()
    {
        Path circle = Path.Circle(0, 0, 200);
        foreach (float tol in new[] { 0.5f, 0.1f, 0.01f })
        {
            IReadOnlyList<Contour> c = circle.Flatten(tol);
            // The ellipse's cubic approximation deviates from the true circle by under 0.03% of the radius (0.06 here); the flattening adds at most tol.
            double worst = 0;
            foreach (Contour f in c)
            {
                for (int i = 0; i < f.Points.Length; i++)
                {
                    Vector2 a = f.Points[i], b = f.Points[(i + 1) % f.Points.Length];
                    Vector2 mid = (a + b) / 2;
                    worst = Math.Max(worst, Math.Abs(200 - mid.Length()));
                }
            }

            Assert.True(worst <= tol + 0.07, $"tolerance {tol}: midpoint distance {worst}");
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => circle.Flatten(0));
    }

    [Fact]
    public void TransformsMoveBoundsAndReverseFlipsDirection()
    {
        Path r = Path.Rectangle(0, 0, 10, 10);
        Assert.Equal(new System.Drawing.RectangleF(5, 7, 10, 10), r.Translate(5, 7).Bounds);
        Assert.Equal(new System.Drawing.RectangleF(0, 0, 20, 30), r.Scale(2, 3).Bounds);
        System.Drawing.RectangleF rot = r.Rotate(90, 5, 5).Bounds;
        Assert.Equal(0, rot.X, 1e-4);
        Assert.Equal(10, rot.Width, 1e-4);

        Path rev = r.Reverse();
        IReadOnlyList<Contour> a = r.Flatten(), b = rev.Flatten();
        Assert.Equal(a[0].Points[^1], b[0].Points[0]); // the reversed figure starts where the original ended
        Assert.Equal(a[0].Points[0], b[0].Points[^1]);
        Assert.True(b[0].IsClosed);
        Path curvy = new PathBuilder().MoveTo(0, 0).CubicBezierTo(5, -5, 10, 5, 20, 0).QuadraticBezierTo(25, 10, 20, 20).Close().Build();
        Assert.Equal(curvy.ToSvg(), curvy.Reverse().Reverse().ToSvg());
        Assert.Equal(curvy.Length, curvy.Reverse().Length, 1e-3);
    }

    [Fact]
    public void ReverseFlipsTheWindingSoANonZeroHoleAppears()
    {
        Path outer = Path.Rectangle(2, 2, 20, 20);
        Path inner = Path.Rectangle(8, 8, 8, 8);
        Rgba32[] same = Draw(Path.Combine(outer, inner), IntersectionRule.NonZero, 24, 24);
        Rgba32[] hole = Draw(Path.Combine(outer, inner.Reverse()), IntersectionRule.NonZero, 24, 24);
        Assert.Equal(255, same[(12 * 24) + 12].A);
        Assert.Equal(0, hole[(12 * 24) + 12].A);
        Assert.Equal(255, hole[(4 * 24) + 4].A);
    }

    [Fact]
    public void EvenOddMakesHolesWithoutReversing()
    {
        Path both = Path.Combine(Path.Rectangle(2, 2, 20, 20), Path.Rectangle(8, 8, 8, 8));
        Assert.Equal(0, Draw(both, IntersectionRule.EvenOdd, 24, 24)[(12 * 24) + 12].A);
    }

    [Fact]
    public void ShapesFill()
    {
        foreach (Path p in new[] { Path.RegularPolygon(32, 32, 6, 20), Path.Star(32, 32, 5, 8, 22, 15), Path.RoundedRectangle(4, 4, 56, 40, 10), Path.Ellipse(32, 32, 28, 12) })
        {
            Rgba32[] px = Draw(p, IntersectionRule.NonZero, 64, 64);
            Assert.Equal(255, px[(32 * 64) + 32].A);
            Assert.Equal(0, px[0].A);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => Path.RegularPolygon(0, 0, 2, 5));
        Assert.True(Path.Polygon([new Vector2(1, 1)]).IsEmpty);
    }

    [Theory]
    [InlineData("M10 10 L 50 10 L 50 50 Z")]
    [InlineData("m10,10 l40,0 0,40 z")]
    [InlineData("M10 10 H50 V50 H10 z")]
    [InlineData("M 10 10 C 20 0 40 0 50 10 S 70 30 50 50 Q 30 60 10 50 T 0 20 Z")]
    [InlineData("M10 10 a20 20 0 1 1 40 0 a20 20 0 1 1 -40 0")]
    [InlineData("M10 10a1 1 0 00.5.5")]
    [InlineData("M1e1,1E+1l1.5.5.5-2")]
    public void ValidSvgParses(string d)
    {
        Assert.True(Path.TryParseSvg(d, out Path? p));
        Assert.False(p!.IsEmpty);
        // The text form reads back to the same path.
        Assert.True(Path.TryParseSvg(p.ToSvg(), out Path? again));
        Assert.Equal(p.ToSvg(), again!.ToSvg());
    }

    [Fact]
    public void SvgRelativeAndAbsoluteAgree()
    {
        Assert.Equal(Path.ParseSvg("M10 10 L50 10 L50 50 Z").ToSvg(), Path.ParseSvg("m10 10 l40 0 l0 40 z").ToSvg());
        Assert.Equal(Path.ParseSvg("M10 10 L50 10 L50 50 L10 50").ToSvg(), Path.ParseSvg("M10 10 H50 V50 H10").ToSvg());
        Assert.Equal(Path.ParseSvg("M0 0 L5 5 L10 10").ToSvg(), Path.ParseSvg("M0 0 5 5 10 10").ToSvg());
        Assert.True(Path.ParseSvg("  ").IsEmpty);
    }

    [Theory]
    [InlineData("L 1 2")]
    [InlineData("M 1")]
    [InlineData("M 1 2 L")]
    [InlineData("M 1 2 L 3")]
    [InlineData("M 1 2 X 3 4")]
    [InlineData("M 1 2 Z 5 5")]
    [InlineData("M 1e999 2")]
    [InlineData("M NaN 1")]
    [InlineData("M 1 2 A 1 1 0 2 0 5 5")]
    [InlineData("M 1 2 A 1 1 0 0")]
    [InlineData("M 1,,2")]
    [InlineData("M . 2")]
    [InlineData("M 1e 2")]
    [InlineData("M 1 2 L 1e39 4")]
    [InlineData("M 3e38 0 l 3e38 0")]
    public void InvalidSvgFailsWithoutThrowing(string d)
    {
        Assert.False(Path.TryParseSvg(d, out Path? p));
        Assert.Null(p);
        Assert.Throws<ArgumentException>(() => Path.ParseSvg(d));
    }

    [Fact]
    public void ArcsWithHugeRadiiNeverThrow()
    {
        foreach (string d in new[] { "M 0 0 a 3e38 3e38 0 1 1 1e38 1e38", "M 0 0 A 1e-30 1e30 45 0 1 5 5", "M 0 0 A 3e38 1 77 1 0 -3e38 3e38" })
        {
            if (Path.TryParseSvg(d, out Path? p))
            {
                _ = p!.Bounds;
                _ = Draw(p, IntersectionRule.NonZero, 32, 32);
            }
        }
    }

    [Fact]
    public void HostileSvgIsBoundedAndNeverThrows()
    {
        var rnd = new Random(5);
        const string alphabet = "MmLlHhVvCcSsQqTtAaZz0123456789.-+eE ,\t\n";
        for (int i = 0; i < 20_000; i++)
        {
            char[] s = new char[rnd.Next(0, 60)];
            for (int k = 0; k < s.Length; k++)
            {
                s[k] = alphabet[rnd.Next(alphabet.Length)];
            }

            if (Path.TryParseSvg(s, out Path? p))
            {
                Assert.NotNull(p);
                _ = p.Bounds;
            }
        }

        // A very long run of digits and a very long command list are linear work and finish.
        Assert.False(Path.TryParseSvg("M " + new string('9', 400_000) + " 1", out _));
        Assert.True(Path.TryParseSvg("M0 0" + string.Concat(Enumerable.Repeat(" L1 1 L2 0", 200_000)), out Path? long1));
        Assert.Equal(1, long1!.FigureCount);
    }

    [Fact]
    public void ArcsEndWhereTheyShouldAndCoverTheRightSide()
    {
        // Half circle from (10, 50) to (90, 50) through the top (sweep = clockwise on screen).
        Path top = new PathBuilder().MoveTo(10, 50).ArcTo(40, 40, 0, false, true, 90, 50).Close().Build();
        System.Drawing.RectangleF b = top.Bounds;
        Assert.InRange(b.Top, 9.9f, 10.2f);
        Assert.InRange(b.Bottom, 49.99f, 50.01f);
        Path bottom = new PathBuilder().MoveTo(10, 50).ArcTo(40, 40, 0, false, false, 90, 50).Close().Build();
        Assert.InRange(bottom.Bounds.Bottom, 89.8f, 90.1f);
        // Too-small radii scale up to a half circle.
        Path scaled = new PathBuilder().MoveTo(10, 50).ArcTo(1, 1, 0, false, true, 90, 50).Build();
        Assert.InRange(scaled.Bounds.Top, 9.9f, 10.2f);
        // Zero radius is a line; equal end points add nothing.
        Assert.Equal(2, new PathBuilder().MoveTo(0, 0).ArcTo(0, 5, 0, false, true, 10, 10).Build().Flatten()[0].Points.Length);
        Assert.True(new PathBuilder().MoveTo(0, 0).ArcTo(5, 5, 0, false, true, 0, 0).Build().IsEmpty);
    }

    [Fact]
    public void SeedFilesBehave()
    {
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "svgpath");
        string[] files = Directory.GetFiles(dir, "*.txt");
        Assert.NotEmpty(files);
        foreach (string f in files)
        {
            bool ok = Path.TryParseSvg(File.ReadAllText(f), out Path? p);
            Assert.Equal(!System.IO.Path.GetFileName(f).StartsWith("bad-", StringComparison.Ordinal), ok);
            if (ok)
            {
                Assert.True(Path.TryParseSvg(p!.ToSvg(), out Path? again), f);
                Assert.Equal(p.ToSvg(), again!.ToSvg());
                _ = Draw(p!, IntersectionRule.NonZero, 64, 64);
            }
        }
    }

    internal static Rgba32[] Draw(Path p, IntersectionRule rule, int w, int h, bool aa = true) =>
        Image.FromPixels<Rgba32>(new Rgba32[w * h], w, h).Fill(new Rgba32(255, 255, 255, 255), p, aa, rule).ToArray<Rgba32>();
}
