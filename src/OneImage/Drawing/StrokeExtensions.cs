using System.Numerics;

namespace OneImage.Drawing;

/// <summary>Stroke outlines: the shape a pen paints, as a path.</summary>
public static class PathStrokeExtensions
{
    /// <summary>
    /// Returns the outline of <paramref name="path"/> stroked with <paramref name="pen"/> (its width, joins, caps and dashes; the brush is
    /// not used): a path of closed contours that, filled with the non-zero rule, covers exactly the stroke. Open figures become one
    /// contour each (one per dash when dashed); closed figures become two, the outer and the inner edge, so a fill leaves their inside open.
    /// <para>
    /// <b>Geometry.</b> Curves are flattened within <paramref name="tolerance"/> before offsetting; round joins and caps are exact cubic
    /// arcs. Inside a turn the edges are cut at their intersection when the segments reach it, otherwise they pass through the vertex.
    /// A figure of one point (or only repeated points) draws nothing, except a closed one (<c>M p L p Z</c>), which draws a dot for round
    /// and square caps. A dash of zero length draws a dot the same way. Dashes restart at every figure with the pen's offset; a
    /// closed figure that is dashed at both its start and its end gets one dash across the start.
    /// </para>
    /// <para>The result is deterministic. Parts of the stroke that overlap itself are left as overlapping contours.</para>
    /// </summary>
    /// <param name="path">The center line.</param>
    /// <param name="pen">Width, join, cap, miter limit and dash pattern.</param>
    /// <param name="tolerance">Flattening tolerance in path units. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The outline; empty when the pen's width is 0 or the path has nothing to stroke.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="pen"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is not positive and finite.</exception>
    /// <exception cref="ImageLimitException">The path flattens to too many points, the outline gets too many segments, or the dash pattern cuts the path into more than 2²² dashes.</exception>
    public static Path GenerateOutline(this Path path, Pen pen, float tolerance = Path.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pen);
        return GenerateOutline(path, pen.Width, pen.Join, pen.Cap, pen.MiterLimit, pen.DashPattern.Span, pen.DashOffset, tolerance);
    }

    /// <summary>The outline of <paramref name="path"/> stroked with the given line style; see <see cref="GenerateOutline(Path, Pen, float)"/> for the geometry.</summary>
    /// <param name="path">The center line.</param>
    /// <param name="width">Line width, 0 to <see cref="Pen.MaxWidth"/>.</param>
    /// <param name="join">Corner style. The default is <see cref="StrokeJoin.Miter"/>.</param>
    /// <param name="cap">End style. The default is <see cref="StrokeCap.Butt"/>.</param>
    /// <param name="miterLimit">Longest miter as a multiple of the width, at least 1. The default is 4.</param>
    /// <param name="dashPattern">Alternating on and off lengths in path units; empty or default for a solid line.</param>
    /// <param name="dashOffset">Distance into the pattern where each figure starts. The default is 0.</param>
    /// <param name="tolerance">Flattening tolerance in path units. The default is <see cref="Path.DefaultTolerance"/>.</param>
    /// <returns>The outline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A number is out of range or not finite, or the dash pattern is invalid.</exception>
    /// <exception cref="ImageLimitException">The stroke is too complex.</exception>
    public static Path GenerateOutline(this Path path, float width, StrokeJoin join = StrokeJoin.Miter, StrokeCap cap = StrokeCap.Butt, float miterLimit = 4f, ReadOnlySpan<float> dashPattern = default, float dashOffset = 0f, float tolerance = Path.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!(tolerance > 0) || !float.IsFinite(tolerance))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be positive.");
        }

        // The pen constructor owns the argument checks.
        var pen = new Pen(Brush.Solid(default), width, join, cap, miterLimit, dashPattern, dashOffset);
        return StrokeBuilder.Build(path, pen.Width, pen.Join, pen.Cap, pen.MiterLimit, pen.DashPattern.Span, pen.DashOffset, tolerance);
    }

    /// <summary>The path-unit tolerance that keeps flattening within 0.1 pixel after <paramref name="transform"/>.</summary>
    internal static float ToleranceFor(Matrix3x2? transform)
    {
        if (transform is not { } m)
        {
            return Path.DefaultTolerance;
        }

        double scale = Math.Sqrt(((double)m.M11 * m.M11) + ((double)m.M12 * m.M12) + ((double)m.M21 * m.M21) + ((double)m.M22 * m.M22));
        return (float)Math.Clamp(Path.DefaultTolerance / Math.Max(scale, 1e-6), 1e-5, 1e3);
    }
}
