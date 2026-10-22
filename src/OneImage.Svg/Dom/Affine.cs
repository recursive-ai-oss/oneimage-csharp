using System.Numerics;

namespace OneImage.Svg.Dom;

/// <summary>
/// A 2D affine transform in double precision, in the SVG matrix layout: x' = a·x + c·y + e, y' = b·x + d·y + f.
/// Documents compose many transforms, so the build keeps double precision and only converts to <see cref="Matrix3x2"/> at draw time.
/// </summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static Affine Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public bool IsIdentity => this == Identity;

    public double Determinant => (A * D) - (B * C);

    /// <summary>Returns <c>this × other</c> in SVG's sense: the argument is applied first, then this transform.</summary>
    public Affine Multiply(in Affine o) => new(
        (A * o.A) + (C * o.B),
        (B * o.A) + (D * o.B),
        (A * o.C) + (C * o.D),
        (B * o.C) + (D * o.D),
        (A * o.E) + (C * o.F) + E,
        (B * o.E) + (D * o.F) + F);

    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

    /// <summary>Gets whether the transform can be inverted (a non-degenerate, finite matrix).</summary>
    public bool IsInvertible => IsFinite && Math.Abs(Determinant) > 1e-12;

    public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>The largest factor by which the transform stretches a length (the larger singular value).</summary>
    public double MaxScale
    {
        get
        {
            double s1 = (A * A) + (B * B);
            double s2 = (C * C) + (D * D);
            double dot = (A * C) + (B * D);
            double half = (s1 + s2) / 2;
            double diff = Math.Sqrt((((s1 - s2) / 2) * ((s1 - s2) / 2)) + (dot * dot));
            return Math.Sqrt(half + diff);
        }
    }

    /// <summary>The factor a stroke width is scaled by: sqrt(|det|), the geometric mean of the two axis scales.</summary>
    public double MeanScale => Math.Sqrt(Math.Abs(Determinant));

    public Matrix3x2 ToMatrix3x2() => new((float)A, (float)B, (float)C, (float)D, (float)E, (float)F);

    /// <summary>Bounds of the rectangle after the transform.</summary>
    public (double X0, double Y0, double X1, double Y1) TransformRect(double x0, double y0, double x1, double y1)
    {
        (double ax, double ay) = Apply(x0, y0);
        (double bx, double by) = Apply(x1, y0);
        (double cx, double cy) = Apply(x1, y1);
        (double dx, double dy) = Apply(x0, y1);
        return (Math.Min(Math.Min(ax, bx), Math.Min(cx, dx)), Math.Min(Math.Min(ay, by), Math.Min(cy, dy)),
            Math.Max(Math.Max(ax, bx), Math.Max(cx, dx)), Math.Max(Math.Max(ay, by), Math.Max(cy, dy)));
    }
}

/// <summary>Conversions that keep document numbers inside the range the drawing code accepts.</summary>
internal static class Coord
{
    /// <summary>The largest magnitude a coordinate, length or transform component may have; larger values are clamped (or the element dropped).</summary>
    public const double Limit = 1e15;

    /// <summary>Narrows a document number to a finite float; numbers beyond <see cref="Limit"/> are clamped so the drawing code never sees infinity.</summary>
    public static float F(double v) => (float)Math.Clamp(v, -Limit, Limit);
}
