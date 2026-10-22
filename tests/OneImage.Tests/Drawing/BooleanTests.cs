using System.Numerics;
using OneImage.Drawing;
using Xunit;
using Path = OneImage.Drawing.Path;

namespace OneImage.Tests.Drawing;

public class BooleanTests
{
    // ---- Helpers ----

    internal static double Area(Path p)
    {
        double total = 0;
        foreach (Contour c in p.Flatten(0.01f))
        {
            double a = 0;
            Vector2[] v = c.Points;
            for (int i = 0; i < v.Length; i++)
            {
                Vector2 q = v[(i + 1) % v.Length];
                a += ((double)v[i].X * q.Y) - ((double)q.X * v[i].Y);
            }

            total += a / 2;
        }

        return Math.Abs(total);
    }

    internal static int Winding(IReadOnlyList<Contour> contours, double x, double y)
    {
        int w = 0;
        foreach (Contour c in contours)
        {
            Vector2[] v = c.Points;
            for (int i = 0; i < v.Length; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % v.Length];
                if ((a.Y <= y) == (b.Y <= y))
                {
                    continue;
                }

                double t = (y - a.Y) / (b.Y - a.Y);
                if (a.X + (t * (b.X - a.X)) > x)
                {
                    w += b.Y > a.Y ? 1 : -1;
                }
            }
        }

        return w;
    }

    internal static bool In(IReadOnlyList<Contour> c, double x, double y, IntersectionRule rule)
    {
        int w = Winding(c, x, y);
        return rule == IntersectionRule.NonZero ? w != 0 : (w & 1) != 0;
    }

    /// <summary>Brute-force reference: the area of {points} where the operation holds, from a fixed grid of samples.</summary>
    private static double Sampled(Path a, Path b, BooleanOperation op, IntersectionRule ra, IntersectionRule rb, RectangleBox box, int n = 700)
    {
        var ca = a.Flatten();
        var cb = b.Flatten();
        long hits = 0;
        for (int j = 0; j < n; j++)
        {
            double y = box.Y0 + ((j + 0.5 + (0.13 * ((j * 7) % 5))) / (n + 1)) * (box.Y1 - box.Y0);
            for (int i = 0; i < n; i++)
            {
                double x = box.X0 + ((i + 0.5 + (0.17 * ((i * 3) % 5))) / (n + 1)) * (box.X1 - box.X0);
                bool ia = In(ca, x, y, ra), ib = In(cb, x, y, rb);
                bool r = op switch
                {
                    BooleanOperation.Union => ia || ib,
                    BooleanOperation.Intersection => ia && ib,
                    BooleanOperation.Difference => ia && !ib,
                    _ => ia ^ ib,
                };
                if (r)
                {
                    hits++;
                }
            }
        }

        return hits / (double)(n * n) * (box.X1 - box.X0) * (box.Y1 - box.Y0);
    }

    internal readonly record struct RectangleBox(double X0, double Y0, double X1, double Y1);

    private static double DistanceToBoundary(IReadOnlyList<Contour> contours, double x, double y)
    {
        double best = double.MaxValue;
        foreach (Contour c in contours)
        {
            Vector2[] v = c.Points;
            for (int i = 0; i < v.Length; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % v.Length];
                double dx = b.X - a.X, dy = b.Y - a.Y, l2 = (dx * dx) + (dy * dy);
                double t = l2 == 0 ? 0 : Math.Clamp((((x - a.X) * dx) + ((y - a.Y) * dy)) / l2, 0, 1);
                double px = a.X + (t * dx) - x, py = a.Y + (t * dy) - y;
                best = Math.Min(best, (px * px) + (py * py));
            }
        }

        return Math.Sqrt(best);
    }

    /// <summary>
    /// Checks the result point by point on a fixed sample grid against the definition of the operation, skipping points within
    /// 0.05 pixel of an input edge (the grid snap moves edges by up to 0.003), and that resolving it again changes nothing.
    /// </summary>
    private static void AssertArea(Path a, Path b, BooleanOperation op, IntersectionRule ra, IntersectionRule rb, RectangleBox box, int n = 260)
    {
        Path r = a.Boolean(op, b, ra, rb);
        var ca = a.Flatten();
        var cb = b.Flatten();
        var cr = r.Flatten();
        int bad = 0, checkedPoints = 0;
        for (int j = 0; j < n; j++)
        {
            double y = box.Y0 + ((j + 0.5 + (0.13 * ((j * 7) % 5))) / (n + 1)) * (box.Y1 - box.Y0);
            for (int i = 0; i < n; i++)
            {
                double x = box.X0 + ((i + 0.5 + (0.17 * ((i * 3) % 5))) / (n + 1)) * (box.X1 - box.X0);
                if (DistanceToBoundary(ca, x, y) < 0.05 || DistanceToBoundary(cb, x, y) < 0.05)
                {
                    continue;
                }

                bool ia = In(ca, x, y, ra), ib = In(cb, x, y, rb);
                bool want = op switch
                {
                    BooleanOperation.Union => ia || ib,
                    BooleanOperation.Intersection => ia && ib,
                    BooleanOperation.Difference => ia && !ib,
                    _ => ia ^ ib,
                };
                checkedPoints++;
                if (In(cr, x, y, IntersectionRule.NonZero) != want || In(cr, x, y, IntersectionRule.EvenOdd) != want)
                {
                    bad++;
                }
            }
        }

        Assert.True(checkedPoints > n * n / 2);
        Assert.True(bad == 0, $"{op}: {bad} of {checkedPoints} sample points disagree");
        double got = Area(r);
        Assert.Equal(got, Area(r.Resolve()), 3);
        Assert.Equal(got, Area(r.Resolve(IntersectionRule.EvenOdd)), 3);
    }

    private static void Near(double expected, double actual, double tolerance) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected} +- {tolerance}, got {actual}");

    // ---- Exact cases ----

    [Fact]
    public void OverlappingRectanglesHaveExactAreas()
    {
        Path a = Path.Rectangle(0, 0, 10, 10), b = Path.Rectangle(5, 5, 10, 10);
        Assert.Equal(175, Area(a.Union(b)), 6);
        Assert.Equal(25, Area(a.Intersect(b)), 6);
        Assert.Equal(75, Area(a.Subtract(b)), 6);
        Assert.Equal(150, Area(a.Xor(b)), 6);
        Assert.Equal(75, Area(b.Subtract(a)), 6);
    }

    [Fact]
    public void AdjacentRectanglesMergeIntoOne()
    {
        Path r = Path.Rectangle(0, 0, 10, 10).Union(Path.Rectangle(10, 0, 10, 10));
        Assert.Equal(200, Area(r), 6);
        IReadOnlyList<Contour> c = r.Flatten();
        Assert.Single(c);
        Assert.Equal(4, c[0].Points.Length); // the shared edge and the collinear vertices are gone
    }

    [Fact]
    public void CollinearOverlappingEdgesAreExact()
    {
        // Share part of the line y = 0 and x = 10.
        Path a = Path.Rectangle(0, 0, 10, 10), b = Path.Rectangle(5, -10, 10, 10);
        Assert.Equal(200, Area(a.Union(b)), 6);
        Assert.Empty(a.Intersect(b).Flatten()); // they only touch along an edge
        Assert.Equal(100, Area(a.Subtract(b)), 6);
        Assert.Equal(200, Area(a.Xor(b)), 6);
    }

    [Fact]
    public void IdenticalShapes()
    {
        Path a = Path.Star(50, 50, 5, 20, 40);
        Near(Area(a), Area(a.Union(a)), 0.5); // vertices move onto the 1/256 grid
        Near(Area(a), Area(a.Intersect(a)), 0.5);
        Assert.Empty(a.Subtract(a).Flatten());
        Assert.Empty(a.Xor(a).Flatten());
    }

    [Fact]
    public void SharedVertexOnlyKeepsTwoPolygons()
    {
        Path r = Path.Rectangle(0, 0, 10, 10).Union(Path.Rectangle(10, 10, 10, 10));
        Assert.Equal(200, Area(r), 6);
        Assert.Equal(2, r.Flatten().Count);
    }

    [Fact]
    public void HolesAreKept()
    {
        Path donut = Path.Combine(Path.Rectangle(0, 0, 100, 100), Path.Rectangle(25, 25, 50, 50).Reverse());
        Assert.Equal(7500, Area(donut.Resolve()), 6);
        Path cut = donut.Subtract(Path.Rectangle(40, -10, 20, 120));
        // The vertical bar removes 20 x 100 of the ring but not what lies inside the hole.
        Assert.Equal(7500 - (20 * 50), Area(cut), 6);
        Assert.Equal(2, cut.Flatten().Count);
        // A shape inside the hole is not touched by a union with the ring, and stays separate.
        Path inner = Path.Rectangle(40, 40, 20, 20);
        Path u = donut.Union(inner);
        Assert.Equal(7500 + 400, Area(u), 6);
        Assert.Equal(3, u.Flatten().Count);
        // Filling the hole exactly closes it.
        Assert.Equal(10000, Area(donut.Union(Path.Rectangle(25, 25, 50, 50))), 6);
        Assert.Single(donut.Union(Path.Rectangle(25, 25, 50, 50)).Flatten());
    }

    [Fact]
    public void BowtieResolvesToTwoTriangles()
    {
        Path bow = Path.Polygon([new(0, 0), new(10, 10), new(10, 0), new(0, 10)]);
        Path r = bow.Resolve();
        Assert.Equal(50, Area(r), 6);
        Assert.Equal(2, r.Flatten().Count);
    }

    [Fact]
    public void PentagramRules()
    {
        Vector2[] pts = Enumerable.Range(0, 5).Select(i => new Vector2(100 + (80 * MathF.Sin(i * 4 * MathF.PI / 5)), 100 - (80 * MathF.Cos(i * 4 * MathF.PI / 5)))).ToArray();
        Path star = Path.Polygon(pts);
        double nz = Area(star.Resolve(IntersectionRule.NonZero));
        double eo = Area(star.Resolve(IntersectionRule.EvenOdd));
        Assert.True(nz > eo + 1000);
        // Pentagram: even-odd leaves the central pentagon out. The regular pentagon of radius r*cos(72)/cos(36)... compare to brute force.
        var box = new RectangleBox(0, 0, 200, 200);
        Near(Sampled(star, Path.Empty, BooleanOperation.Union, IntersectionRule.NonZero, IntersectionRule.NonZero, box), nz, 40);
        Near(Sampled(star, Path.Empty, BooleanOperation.Union, IntersectionRule.EvenOdd, IntersectionRule.NonZero, box), eo, 40);
    }

    [Fact]
    public void OppositeWindingFullyCancelsUnderNonZeroUnion()
    {
        Path a = Path.Rectangle(0, 0, 10, 10);
        Path r = PathBoolean.UnionAll([a, a.Reverse()]);
        Assert.Equal(100, Area(r), 6); // each path is resolved on its own first
    }

    [Fact]
    public void SpikeAndZeroAreaInputVanish()
    {
        Path spike = Path.Polygon([new(0, 0), new(10, 0), new(10, 10), new(10, 0)]);
        Assert.True(spike.Resolve().IsEmpty);
        Assert.True(Path.Polyline([new(0, 0), new(5, 5)]).Resolve().IsEmpty);
        Assert.True(Path.Empty.Union(Path.Empty).IsEmpty);
        Assert.Equal(100, Area(Path.Empty.Union(Path.Rectangle(0, 0, 10, 10))), 6);
        Assert.True(Path.Rectangle(0, 0, 10, 10).Intersect(Path.Empty).IsEmpty);
    }

    // ---- Curved shapes against a brute-force reference ----

    [Fact]
    public void OverlappingCircles()
    {
        Path a = Path.Circle(100, 100, 60), b = Path.Circle(150, 110, 50);
        var box = new RectangleBox(30, 30, 220, 180);
        foreach (BooleanOperation op in Enum.GetValues<BooleanOperation>())
        {
            AssertArea(a, b, op, IntersectionRule.NonZero, IntersectionRule.NonZero, box);
        }

        // Analytic lens area of two circles.
        double d = Math.Sqrt((50 * 50) + (10 * 10)), r1 = 60, r2 = 50;
        double lens = (r1 * r1 * Math.Acos(((d * d) + (r1 * r1) - (r2 * r2)) / (2 * d * r1)))
            + (r2 * r2 * Math.Acos(((d * d) + (r2 * r2) - (r1 * r1)) / (2 * d * r2)))
            - (0.5 * Math.Sqrt((-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2)));
        Near(lens, Area(a.Intersect(b)), 0.005 * lens); // flattening is inscribed, so a little under
    }

    [Fact]
    public void StarMinusRectangle()
    {
        Path star = Path.Star(100, 100, 5, 40, 90);
        Path rect = Path.Rectangle(60, 50, 80, 100);
        var box = new RectangleBox(0, 0, 200, 200);
        AssertArea(star, rect, BooleanOperation.Difference, IntersectionRule.NonZero, IntersectionRule.NonZero, box);
        AssertArea(star, rect, BooleanOperation.Xor, IntersectionRule.NonZero, IntersectionRule.NonZero, box);
    }

    [Fact]
    public void SelfIntersectingInputsUnderBothRules()
    {
        Vector2[] pts = Enumerable.Range(0, 7).Select(i => new Vector2(100 + (85 * MathF.Sin(i * 3 * MathF.PI / 7 * 2)), 100 - (85 * MathF.Cos(i * 3 * MathF.PI / 7 * 2)))).ToArray();
        Path hept = Path.Polygon(pts);
        Path rect = Path.Rectangle(40, 20, 90, 160);
        var box = new RectangleBox(0, 0, 200, 200);
        foreach (IntersectionRule ra in new[] { IntersectionRule.NonZero, IntersectionRule.EvenOdd })
        {
            foreach (BooleanOperation op in Enum.GetValues<BooleanOperation>())
            {
                AssertArea(hept, rect, op, ra, IntersectionRule.NonZero, box);
            }
        }
    }

    [Fact]
    public void RandomPolygonsMatchBruteForce()
    {
        var rng = new Random(12345);
        for (int round = 0; round < 24; round++)
        {
            Path a = RandomPolygon(rng, 3 + rng.Next(14)), b = RandomPolygon(rng, 3 + rng.Next(14));
            var ra = round % 2 == 0 ? IntersectionRule.NonZero : IntersectionRule.EvenOdd;
            var rb = round % 3 == 0 ? IntersectionRule.NonZero : IntersectionRule.EvenOdd;
            foreach (BooleanOperation op in Enum.GetValues<BooleanOperation>())
            {
                AssertArea(a, b, op, ra, rb, new RectangleBox(0, 0, 100, 100));
            }
        }
    }

    [Fact]
    public void GridAlignedShapesWithManyCoincidentEdges()
    {
        // Two checkerboards offset by one cell: every edge is shared or crossed at integer points.
        var a = new List<Path>();
        var b = new List<Path>();
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 12; x++)
            {
                if (((x + y) & 1) == 0)
                {
                    a.Add(Path.Rectangle(x * 8, y * 8, 8, 8));
                }
                else
                {
                    b.Add(Path.Rectangle((x * 8) + 4, (y * 8) + 4, 8, 8));
                }
            }
        }

        Path pa = Path.Combine(a.ToArray()), pb = Path.Combine(b.ToArray());
        Assert.Equal(72 * 64, Area(pa.Resolve()), 6);
        Near(Sampled(pa, pb, BooleanOperation.Intersection, IntersectionRule.NonZero, IntersectionRule.NonZero, new RectangleBox(0, 0, 110, 110), 880), Area(pa.Intersect(pb)), 60);
        double u = Area(pa.Union(pb));
        Assert.Equal((72 * 64) + (72 * 64) - Area(pa.Intersect(pb)), u, 6);
    }

    [Fact]
    public void TinyIntegerGridShapesWithCoincidentEverything()
    {
        // Vertices on a 9 x 9 lattice: edges overlap, touch and cross at lattice points constantly.
        var rng = new Random(99);
        for (int round = 0; round < 250; round++)
        {
            Path a = LatticePolygon(rng, 3 + rng.Next(6)), b = LatticePolygon(rng, 3 + rng.Next(6));
            var ra = round % 2 == 0 ? IntersectionRule.NonZero : IntersectionRule.EvenOdd;
            var rb = round % 4 < 2 ? IntersectionRule.NonZero : IntersectionRule.EvenOdd;
            BooleanOperation op = (BooleanOperation)(round % 4);
            AssertArea(a, b, op, ra, rb, new RectangleBox(-1, -1, 10, 10), 90);
        }
    }

    [Fact]
    public void DenseRandomCrossingsMatchBruteForce()
    {
        var rng = new Random(2024);
        for (int round = 0; round < 3; round++)
        {
            Path a = RandomPolygon(rng, 120), b = RandomPolygon(rng, 150);
            foreach (BooleanOperation op in Enum.GetValues<BooleanOperation>())
            {
                AssertArea(a, b, op, IntersectionRule.EvenOdd, IntersectionRule.NonZero, new RectangleBox(0, 0, 100, 100), 200);
            }
        }
    }

    private static Path LatticePolygon(Random rng, int n)
    {
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            pts[i] = new Vector2(rng.Next(0, 9), rng.Next(0, 9));
        }

        return Path.Polygon(pts);
    }

    private static Path RandomPolygon(Random rng, int n)
    {
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            pts[i] = new Vector2(5 + (float)(rng.NextDouble() * 90), 5 + (float)(rng.NextDouble() * 90));
        }

        return Path.Polygon(pts);
    }

    // ---- Robustness ----

    [Fact]
    public void HugeAndFarCoordinates()
    {
        Path big = Path.Rectangle(-16_000_000, -16_000_000, 32_000_000, 32_000_000);
        Path small = Path.Rectangle(10, 10, 20, 20);
        Assert.Equal(400, Area(big.Intersect(small)), 3);
        Near((32_000_000d * 32_000_000d) - 400, Area(big.Subtract(small)), 1e9);
        Path tri = Path.Polygon([new(-16_000_000, 0), new(16_000_000, 1), new(0, 16_000_000)]);
        Path r = tri.Union(small);
        Assert.False(r.IsEmpty);
    }

    [Fact]
    public void CoordinatesBeyondTheLimitThrowTyped()
    {
        Path huge = Path.Rectangle(0, 0, 1e9f, 10);
        Assert.Throws<ImageLimitException>(() => huge.Union(Path.Rectangle(0, 0, 1, 1)));
    }

    [Fact]
    public void NearlyParallelAndNearlyCoincidentEdges()
    {
        // Long thin slivers that cross at tiny angles, and edges a hair apart.
        Path a = Path.Polygon([new(0, 0), new(1000, 0.001f), new(1000, 1), new(0, 1)]);
        Path b = Path.Polygon([new(0, 0.0005f), new(1000, 0), new(1000, 0.998f), new(0, 1.002f)]);
        foreach (BooleanOperation op in Enum.GetValues<BooleanOperation>())
        {
            Path r = a.Boolean(op, b);
            Assert.True(Area(r) >= 0);
            Assert.Equal(Area(r), Area(r.Resolve()), 2);
        }

        Assert.InRange(Area(a.Union(b)), 990, 1010);
    }

    [Fact]
    public void ManySegmentsFinishQuickly()
    {
        var rng = new Random(7);
        var circles = new Path[400];
        for (int i = 0; i < circles.Length; i++)
        {
            circles[i] = Path.Circle(rng.Next(0, 2000), rng.Next(0, 2000), 20 + rng.Next(80));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Path u = PathBoolean.UnionAll(circles);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
        Assert.False(u.IsEmpty);
        // Union area is at most the sum and at least the biggest circle.
        double sum = circles.Sum(Area);
        double result = Area(u);
        Assert.InRange(result, Math.PI * 20 * 20, sum);
    }

    [Fact]
    public void ResultIsIdenticalOnRepeatedRuns()
    {
        Path a = Path.Star(100, 100, 7, 30, 90), b = Path.Circle(120, 90, 70);
        string s1 = a.Xor(b).ToSvg(), s2 = a.Xor(b).ToSvg();
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Path r = Path.Rectangle(0, 0, 1, 1);
        Assert.Throws<ArgumentNullException>(() => r.Union(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Boolean((BooleanOperation)9, r));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Union(r, tolerance: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Union(r, (IntersectionRule)7));
    }

    [Fact]
    public void OffsetGrowsAndShrinks()
    {
        Path sq = Path.Rectangle(100, 100, 100, 100);
        // Round join: area of a square grown by d is s^2 + 4 s d + pi d^2.
        Near(10000 + (4 * 100 * 10) + (Math.PI * 100), Area(sq.Offset(10)), 15);
        Near(10000 + (4 * 100 * 10) + (4 * 100), Area(sq.Offset(10, StrokeJoin.Miter)), 1);
        Near(80 * 80, Area(sq.Offset(-10)), 1);
        Assert.True(sq.Offset(-60).IsEmpty);
        Assert.Equal(10000, Area(sq.Offset(0)), 6);
    }
}
