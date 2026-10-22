using System.Numerics;

namespace OneImage.Drawing;

/// <summary>
/// Boolean operations on the regions paths enclose: union, intersection, difference, xor, plus simplification of
/// self-overlapping paths and offsetting.
/// <para>
/// <b>What is computed.</b> Every figure is treated as closed, as a fill does. The paths are flattened (curves within
/// <c>tolerance</c> pixels), so the result consists of straight segments only. Each coordinate is rounded to a grid of 1/256
/// pixel before the work starts, and intersection points are rounded to the same grid, so results are exact on that grid and
/// never depend on thread count or on floating-point luck. Collinear and coincident edges, touching vertices, spikes and
/// self-intersections are all handled exactly; segments that cancel (two shapes sharing an edge on opposite sides) vanish.
/// </para>
/// <para>
/// <b>Result.</b> A new <see cref="Path"/> of closed polygons. Outer contours and holes run in opposite directions, nothing
/// overlaps, and no two edges cross, so the path fills the same under <see cref="IntersectionRule.NonZero"/> and
/// <see cref="IntersectionRule.EvenOdd"/>. Collinear vertices are removed. The result of an empty region is
/// <see cref="Path.Empty"/>. Shapes that only touch at a point stay separate polygons.
/// </para>
/// <para>
/// <b>Limits.</b> Coordinates must lie within ±33554432 pixels and a call accepts at most 2<sup>22</sup> flattened segments;
/// otherwise <see cref="ImageLimitException"/> is thrown. Splitting at crossings may create at most 2<sup>23</sup> segments and
/// takes a bounded number of comparisons; beyond that a typed exception is thrown rather than running on.
/// </para>
/// </summary>
public static class PathBoolean
{
    /// <summary>Combines two paths with a set operation.</summary>
    /// <param name="subject">The first operand.</param>
    /// <param name="operation">The operation. <see cref="BooleanOperation.Difference"/> removes <paramref name="clip"/> from <paramref name="subject"/>.</param>
    /// <param name="clip">The second operand.</param>
    /// <param name="subjectRule">What counts as inside the subject where its parts overlap. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="clipRule">What counts as inside the clip. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="tolerance">Flattening tolerance in pixels. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The resulting polygons; see <see cref="PathBoolean"/> for their form.</returns>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The operation or a rule is undefined, or <paramref name="tolerance"/> is not positive.</exception>
    /// <exception cref="ImageLimitException">A coordinate is beyond ±33554432, or the input or its intermediate result is too large.</exception>
    /// <exception cref="ImageOperationException">The geometry could not be settled (extremely degenerate input).</exception>
    public static Path Boolean(this Path subject, BooleanOperation operation, Path clip, IntersectionRule subjectRule = IntersectionRule.NonZero, IntersectionRule clipRule = IntersectionRule.NonZero, float tolerance = Path.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(clip);
        Check(operation, subjectRule, clipRule, tolerance);
        return BooleanEngine.Run([subject], [clip], operation, subjectRule, clipRule, tolerance);
    }

    /// <summary>Everything inside either path; see <see cref="Boolean"/>.</summary>
    /// <param name="subject">The first operand.</param>
    /// <param name="other">The second operand.</param>
    /// <param name="subjectRule">Inside rule of the first operand. The default is non-zero.</param>
    /// <param name="otherRule">Inside rule of the second operand. The default is non-zero.</param>
    /// <param name="tolerance">Flattening tolerance in pixels. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The union.</returns>
    public static Path Union(this Path subject, Path other, IntersectionRule subjectRule = IntersectionRule.NonZero, IntersectionRule otherRule = IntersectionRule.NonZero, float tolerance = Path.DefaultTolerance) =>
        subject.Boolean(BooleanOperation.Union, other, subjectRule, otherRule, tolerance);

    /// <summary>Only what is inside both paths; see <see cref="Boolean"/>.</summary>
    /// <param name="subject">The first operand.</param>
    /// <param name="other">The second operand.</param>
    /// <param name="subjectRule">Inside rule of the first operand. The default is non-zero.</param>
    /// <param name="otherRule">Inside rule of the second operand. The default is non-zero.</param>
    /// <param name="tolerance">Flattening tolerance in pixels. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The intersection.</returns>
    public static Path Intersect(this Path subject, Path other, IntersectionRule subjectRule = IntersectionRule.NonZero, IntersectionRule otherRule = IntersectionRule.NonZero, float tolerance = Path.DefaultTolerance) =>
        subject.Boolean(BooleanOperation.Intersection, other, subjectRule, otherRule, tolerance);

    /// <summary>What is inside <paramref name="subject"/> but not inside <paramref name="other"/>; see <see cref="Boolean"/>.</summary>
    /// <param name="subject">The path to cut from.</param>
    /// <param name="other">The path to cut away.</param>
    /// <param name="subjectRule">Inside rule of the first operand. The default is non-zero.</param>
    /// <param name="otherRule">Inside rule of the second operand. The default is non-zero.</param>
    /// <param name="tolerance">Flattening tolerance in pixels. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The difference.</returns>
    public static Path Subtract(this Path subject, Path other, IntersectionRule subjectRule = IntersectionRule.NonZero, IntersectionRule otherRule = IntersectionRule.NonZero, float tolerance = Path.DefaultTolerance) =>
        subject.Boolean(BooleanOperation.Difference, other, subjectRule, otherRule, tolerance);

    /// <summary>What is inside exactly one of the two paths; see <see cref="Boolean"/>.</summary>
    /// <param name="subject">The first operand.</param>
    /// <param name="other">The second operand.</param>
    /// <param name="subjectRule">Inside rule of the first operand. The default is non-zero.</param>
    /// <param name="otherRule">Inside rule of the second operand. The default is non-zero.</param>
    /// <param name="tolerance">Flattening tolerance in pixels. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The symmetric difference.</returns>
    public static Path Xor(this Path subject, Path other, IntersectionRule subjectRule = IntersectionRule.NonZero, IntersectionRule otherRule = IntersectionRule.NonZero, float tolerance = Path.DefaultTolerance) =>
        subject.Boolean(BooleanOperation.Xor, other, subjectRule, otherRule, tolerance);

    /// <summary>
    /// Rewrites a path as non-overlapping polygons that enclose the same region: self-intersections and overlaps are resolved by
    /// <paramref name="rule"/>, and the result fills identically under either rule.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">What counts as inside. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="tolerance">Flattening tolerance in pixels. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The simplified path; see <see cref="PathBoolean"/> for its form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ImageLimitException">See <see cref="Boolean"/>.</exception>
    public static Path Resolve(this Path path, IntersectionRule rule = IntersectionRule.NonZero, float tolerance = Path.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(path);
        Check(BooleanOperation.Union, rule, rule, tolerance);
        return BooleanEngine.Run([path], [], BooleanOperation.Union, rule, IntersectionRule.NonZero, tolerance);
    }

    /// <summary>
    /// The union of any number of paths. Each path is first resolved on its own by <paramref name="rule"/> (so a path wound the
    /// other way, or with holes, does not cancel its neighbors), then all are merged.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="rule">What counts as inside each path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="tolerance">Flattening tolerance in pixels. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The union; <see cref="Path.Empty"/> when there are no paths.</returns>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ImageLimitException">See <see cref="Boolean"/>.</exception>
    public static Path UnionAll(ReadOnlySpan<Path> paths, IntersectionRule rule = IntersectionRule.NonZero, float tolerance = Path.DefaultTolerance)
    {
        Check(BooleanOperation.Union, rule, rule, tolerance);
        if (paths.Length == 0)
        {
            return Path.Empty;
        }

        var resolved = new Path[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(paths[i]);
            resolved[i] = BooleanEngine.Run([paths[i]], [], BooleanOperation.Union, rule, IntersectionRule.NonZero, tolerance);
        }

        return BooleanEngine.Run(resolved, [], BooleanOperation.Union, IntersectionRule.NonZero, IntersectionRule.NonZero, tolerance);
    }

    /// <summary>
    /// Grows (positive <paramref name="distance"/>) or shrinks (negative) the region a path encloses by moving its boundary
    /// outward or inward. It is the union of the region with the band of points within the distance of its boundary (growing)
    /// or the region minus that band (shrinking), so corners follow <paramref name="join"/> and holes behave as boundaries too.
    /// Parts thinner than twice the distance disappear when shrinking.
    /// </summary>
    /// <param name="path">The path; every figure is treated as closed, overlaps resolved by <paramref name="rule"/>.</param>
    /// <param name="distance">How far the boundary moves, in pixels. Zero returns the resolved path.</param>
    /// <param name="join">The corner style of the band; when shrinking, the corners of the result are cut by the band's outline. The default is <see cref="StrokeJoin.Round"/>.</param>
    /// <param name="miterLimit">The miter limit for <see cref="StrokeJoin.Miter"/>; see <see cref="Pen"/>. The default is 4.</param>
    /// <param name="rule">What counts as inside the path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="tolerance">Flattening tolerance in pixels. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The offset polygons; see <see cref="PathBoolean"/> for their form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="distance"/> is NaN or infinite, or another argument is invalid.</exception>
    /// <exception cref="ImageLimitException">See <see cref="Boolean"/>.</exception>
    public static Path Offset(this Path path, float distance, StrokeJoin join = StrokeJoin.Round, float miterLimit = 4f, IntersectionRule rule = IntersectionRule.NonZero, float tolerance = Path.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!float.IsFinite(distance))
        {
            throw new ArgumentOutOfRangeException(nameof(distance));
        }

        Path shape = path.Resolve(rule, tolerance);
        if (distance == 0 || shape.IsEmpty)
        {
            return shape;
        }

        Path band = BooleanOffset.Pieces(shape, Math.Abs(distance), join, miterLimit, tolerance);
        return shape.Boolean(distance > 0 ? BooleanOperation.Union : BooleanOperation.Difference, band, IntersectionRule.NonZero, IntersectionRule.NonZero, tolerance);
    }

    private static void Check(BooleanOperation op, IntersectionRule a, IntersectionRule b, float tolerance)
    {
        if (op is < BooleanOperation.Union or > BooleanOperation.Xor)
        {
            throw new ArgumentOutOfRangeException(nameof(op));
        }

        if (a is not (IntersectionRule.NonZero or IntersectionRule.EvenOdd) || b is not (IntersectionRule.NonZero or IntersectionRule.EvenOdd))
        {
            throw new ArgumentOutOfRangeException(nameof(a), "Undefined intersection rule.");
        }

        if (!(tolerance > 0) || !float.IsFinite(tolerance))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be positive.");
        }
    }
}
