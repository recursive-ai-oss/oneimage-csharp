namespace OneImage;

/// <summary>Which edge of the image a taper pulls in (see <see cref="ProjectiveMatrix.Taper"/>).</summary>
public enum TaperSide
{
    /// <summary>The left edge becomes shorter.</summary>
    Left = 0,

    /// <summary>The top edge becomes shorter.</summary>
    Top = 1,

    /// <summary>The right edge becomes shorter.</summary>
    Right = 2,

    /// <summary>The bottom edge becomes shorter.</summary>
    Bottom = 3,
}

/// <summary>Which end of the tapered edge moves inwards.</summary>
public enum TaperCorner
{
    /// <summary>Both ends move in by the same amount, so the edge stays centered.</summary>
    Both = 0,

    /// <summary>The bottom end of a left or right edge, or the right end of a top or bottom edge, moves in.</summary>
    RightOrBottom = 1,

    /// <summary>The top end of a left or right edge, or the left end of a top or bottom edge, moves in.</summary>
    LeftOrTop = 2,
}

/// <summary>
/// A 3×3 projective (homography) matrix acting on column vectors in pixel-edge coordinates, where pixel (i, j) covers
/// [i, i+1) × [j, j+1): a point (x, y) goes to
/// <c>((M11·x + M12·y + M13) / w, (M21·x + M22·y + M23) / w)</c> with <c>w = M31·x + M32·y + M33</c>.
/// The top two rows of an affine matrix are <c>(a, b, tx)</c> and <c>(c, d, ty)</c> as in <see cref="Image.Affine"/>, with the bottom row (0, 0, 1).
/// A matrix and its non-zero multiples are the same transform.
/// </summary>
/// <param name="M11">Row 1, column 1.</param>
/// <param name="M12">Row 1, column 2.</param>
/// <param name="M13">Row 1, column 3 (x translation in an affine matrix).</param>
/// <param name="M21">Row 2, column 1.</param>
/// <param name="M22">Row 2, column 2.</param>
/// <param name="M23">Row 2, column 3 (y translation in an affine matrix).</param>
/// <param name="M31">Row 3, column 1 (perspective in x).</param>
/// <param name="M32">Row 3, column 2 (perspective in y).</param>
/// <param name="M33">Row 3, column 3 (1 for matrices from the builders).</param>
public readonly record struct ProjectiveMatrix(double M11, double M12, double M13, double M21, double M22, double M23, double M31, double M32, double M33)
{
    /// <summary>The matrix that changes nothing.</summary>
    public static ProjectiveMatrix Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>The determinant. Zero (within rounding) means the transform collapses the plane and cannot be applied to an image.</summary>
    public double Determinant =>
        (M11 * ((M22 * M33) - (M23 * M32))) - (M12 * ((M21 * M33) - (M23 * M31))) + (M13 * ((M21 * M32) - (M22 * M31)));

    /// <summary>A matrix from the six affine values of <see cref="Image.Affine"/>.</summary>
    /// <param name="a">x contribution to the new x.</param>
    /// <param name="b">y contribution to the new x.</param>
    /// <param name="c">x contribution to the new y.</param>
    /// <param name="d">y contribution to the new y.</param>
    /// <param name="tx">Translation added to x.</param>
    /// <param name="ty">Translation added to y.</param>
    /// <returns>The matrix.</returns>
    public static ProjectiveMatrix FromAffine(double a, double b, double c, double d, double tx = 0, double ty = 0) =>
        new(a, b, tx, c, d, ty, 0, 0, 1);

    /// <summary>A translation.</summary>
    /// <param name="dx">Added to x.</param>
    /// <param name="dy">Added to y.</param>
    /// <returns>The matrix.</returns>
    public static ProjectiveMatrix Translation(double dx, double dy) => new(1, 0, dx, 0, 1, dy, 0, 0, 1);

    /// <summary>A scale about a point (the origin, the top-left corner, by default).</summary>
    /// <param name="sx">Horizontal factor.</param>
    /// <param name="sy">Vertical factor.</param>
    /// <param name="originX">x of the fixed point.</param>
    /// <param name="originY">y of the fixed point.</param>
    /// <returns>The matrix.</returns>
    public static ProjectiveMatrix Scale(double sx, double sy, double originX = 0, double originY = 0) =>
        new(sx, 0, originX - (sx * originX), 0, sy, originY - (sy * originY), 0, 0, 1);

    /// <summary>A rotation about a point, clockwise as seen on screen (y points down).</summary>
    /// <param name="degrees">Angle in degrees.</param>
    /// <param name="originX">x of the center of rotation.</param>
    /// <param name="originY">y of the center of rotation.</param>
    /// <returns>The matrix.</returns>
    public static ProjectiveMatrix Rotation(double degrees, double originX = 0, double originY = 0)
    {
        double r = degrees * Math.PI / 180.0;
        double cos = Math.Cos(r);
        double sin = Math.Sin(r);
        return new(cos, -sin, originX - ((cos * originX) - (sin * originY)), sin, cos, originY - ((sin * originX) + (cos * originY)), 0, 0, 1);
    }

    /// <summary>A skew about a point: x moves by <c>tan(degreesX)·(y − originY)</c> and y by <c>tan(degreesY)·(x − originX)</c>.</summary>
    /// <param name="degreesX">Horizontal skew angle in degrees.</param>
    /// <param name="degreesY">Vertical skew angle in degrees.</param>
    /// <param name="originX">x of the fixed point.</param>
    /// <param name="originY">y of the fixed point.</param>
    /// <returns>The matrix.</returns>
    public static ProjectiveMatrix Skew(double degreesX, double degreesY, double originX = 0, double originY = 0)
    {
        double tx = Math.Tan(degreesX * Math.PI / 180.0);
        double ty = Math.Tan(degreesY * Math.PI / 180.0);
        return new(1, tx, -tx * originY, ty, 1, -ty * originX, 0, 0, 1);
    }

    /// <summary>
    /// The transform that pulls one edge of a <paramref name="width"/> × <paramref name="height"/> image in to <paramref name="fraction"/> of its
    /// length, leaving the opposite edge as it is: the image becomes a trapezoid. Same geometry as ImageSharp's
    /// <c>ProjectiveTransformBuilder.AppendTaper</c>.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="side">The edge that shrinks.</param>
    /// <param name="corner">Which end of that edge moves in.</param>
    /// <param name="fraction">The new length of the edge as a fraction of the old, above 0 and at most 1.</param>
    /// <returns>The matrix.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive, <paramref name="fraction"/> is outside (0, 1], or an enum value is undefined.</exception>
    public static ProjectiveMatrix Taper(int width, int height, TaperSide side, TaperCorner corner, double fraction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (!Enum.IsDefined(side))
        {
            throw new ArgumentOutOfRangeException(nameof(side));
        }

        if (!Enum.IsDefined(corner))
        {
            throw new ArgumentOutOfRangeException(nameof(corner));
        }

        if (!(fraction > 0 && fraction <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), "The taper fraction must be above 0 and at most 1.");
        }

        double w = width;
        double h = height;
        bool vertical = side is TaperSide.Left or TaperSide.Right;
        double length = vertical ? h : w;
        double cut = length * (1 - fraction);
        (double near, double far) = corner switch
        {
            TaperCorner.Both => (cut / 2, cut / 2),
            TaperCorner.RightOrBottom => (0.0, cut),
            _ => (cut, 0.0),
        };

        // near moves the top (or left) end of the edge, far the bottom (or right) end. Corners in order: TL, TR, BR, BL.
        (double X, double Y)[] q = [(0, 0), (w, 0), (w, h), (0, h)];
        switch (side)
        {
            case TaperSide.Left:
                q[0].Y += near;
                q[3].Y -= far;
                break;
            case TaperSide.Right:
                q[1].Y += near;
                q[2].Y -= far;
                break;
            case TaperSide.Top:
                q[0].X += near;
                q[1].X -= far;
                break;
            default:
                q[3].X += near;
                q[2].X -= far;
                break;
        }

        return FromRectangle(width, height, q);
    }

    /// <summary>
    /// The transform that sends the corners of the rectangle (0, 0)–(<paramref name="width"/>, <paramref name="height"/>) to four points, in the order
    /// top-left, top-right, bottom-right, bottom-left. This is the usual "pin the corners" perspective.
    /// </summary>
    /// <param name="width">Rectangle width, above 0.</param>
    /// <param name="height">Rectangle height, above 0.</param>
    /// <param name="corners">The four destination points.</param>
    /// <returns>The matrix.</returns>
    /// <exception cref="ArgumentException">There are not four finite points, or they do not form a convex quadrilateral (three on a line, or crossing sides).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    public static ProjectiveMatrix FromRectangle(double width, double height, ReadOnlySpan<(double X, double Y)> corners)
    {
        if (!(width > 0) || !(height > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The rectangle must have a positive size.");
        }

        return FromQuads([(0, 0), (width, 0), (width, height), (0, height)], corners);
    }

    /// <summary>
    /// The transform that sends four source points to four destination points (the unique projective map doing so).
    /// Both quadrilaterals must be convex, with corners listed in the same winding order.
    /// </summary>
    /// <param name="source">The four source points.</param>
    /// <param name="destination">The four destination points.</param>
    /// <returns>The matrix.</returns>
    /// <exception cref="ArgumentException">There are not four finite points in each list, or a quadrilateral is not convex or is degenerate.</exception>
    public static ProjectiveMatrix FromQuads(ReadOnlySpan<(double X, double Y)> source, ReadOnlySpan<(double X, double Y)> destination)
    {
        ProjectiveMatrix a = UnitSquareTo(source, nameof(source));
        ProjectiveMatrix b = UnitSquareTo(destination, nameof(destination));
        // source -> unit square -> destination.
        return a.Inverse().Then(b);
    }

    /// <summary>The matrix that applies this transform first and <paramref name="next"/> second.</summary>
    /// <param name="next">The transform applied afterwards.</param>
    /// <returns>The combined matrix.</returns>
    public ProjectiveMatrix Then(ProjectiveMatrix next) => new(
        (next.M11 * M11) + (next.M12 * M21) + (next.M13 * M31),
        (next.M11 * M12) + (next.M12 * M22) + (next.M13 * M32),
        (next.M11 * M13) + (next.M12 * M23) + (next.M13 * M33),
        (next.M21 * M11) + (next.M22 * M21) + (next.M23 * M31),
        (next.M21 * M12) + (next.M22 * M22) + (next.M23 * M32),
        (next.M21 * M13) + (next.M22 * M23) + (next.M23 * M33),
        (next.M31 * M11) + (next.M32 * M21) + (next.M33 * M31),
        (next.M31 * M12) + (next.M32 * M22) + (next.M33 * M32),
        (next.M31 * M13) + (next.M32 * M23) + (next.M33 * M33));

    /// <summary>The inverse transform (the adjugate, which is the inverse up to a constant factor, so the same transform).</summary>
    /// <returns>The inverse.</returns>
    /// <exception cref="InvalidOperationException">The matrix is singular.</exception>
    public ProjectiveMatrix Inverse()
    {
        double det = Determinant;
        if (!double.IsFinite(det) || det == 0)
        {
            throw new InvalidOperationException("The matrix is singular.");
        }

        return Adjugate(this);
    }

    /// <summary>Applies the transform to a point.</summary>
    /// <param name="x">x in pixel-edge coordinates.</param>
    /// <param name="y">y in pixel-edge coordinates.</param>
    /// <returns>The transformed point; both values are infinite or NaN when the point lies on the horizon (<c>w = 0</c>).</returns>
    public (double X, double Y) Transform(double x, double y)
    {
        double w = (M31 * x) + (M32 * y) + M33;
        return (((M11 * x) + (M12 * y) + M13) / w, ((M21 * x) + (M22 * y) + M23) / w);
    }

    // The adjugate: the inverse times the determinant.
    internal static ProjectiveMatrix Adjugate(ProjectiveMatrix m) => new(
        (m.M22 * m.M33) - (m.M23 * m.M32),
        (m.M13 * m.M32) - (m.M12 * m.M33),
        (m.M12 * m.M23) - (m.M13 * m.M22),
        (m.M23 * m.M31) - (m.M21 * m.M33),
        (m.M11 * m.M33) - (m.M13 * m.M31),
        (m.M13 * m.M21) - (m.M11 * m.M23),
        (m.M21 * m.M32) - (m.M22 * m.M31),
        (m.M12 * m.M31) - (m.M11 * m.M32),
        (m.M11 * m.M22) - (m.M12 * m.M21));

    // The map from the unit square (0,0),(1,0),(1,1),(0,1) to four points (Heckbert's square-to-quad construction).
    private static ProjectiveMatrix UnitSquareTo(ReadOnlySpan<(double X, double Y)> p, string name)
    {
        if (p.Length != 4)
        {
            throw new ArgumentException("Four corner points are required.", name);
        }

        foreach ((double x, double y) in p)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                throw new ArgumentException("Corner coordinates must be finite.", name);
            }
        }

        // Convex with consistent winding: all four cross products of consecutive edges have the same non-zero sign.
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            (double ax, double ay) = p[i];
            (double bx, double by) = p[(i + 1) & 3];
            (double cx, double cy) = p[(i + 2) & 3];
            double cross = ((bx - ax) * (cy - by)) - ((by - ay) * (cx - bx));
            int s = Math.Sign(cross);
            if (s == 0 || (sign != 0 && s != sign))
            {
                throw new ArgumentException("The four points must form a convex quadrilateral.", name);
            }

            sign = s;
        }

        double dx1 = p[1].X - p[2].X;
        double dx2 = p[3].X - p[2].X;
        double sx = p[0].X - p[1].X + p[2].X - p[3].X;
        double dy1 = p[1].Y - p[2].Y;
        double dy2 = p[3].Y - p[2].Y;
        double sy = p[0].Y - p[1].Y + p[2].Y - p[3].Y;
        double g;
        double h;
        if (sx == 0 && sy == 0)
        {
            // A parallelogram: affine.
            g = 0;
            h = 0;
        }
        else
        {
            double den = (dx1 * dy2) - (dx2 * dy1);
            g = ((sx * dy2) - (dx2 * sy)) / den;
            h = ((dx1 * sy) - (sx * dy1)) / den;
        }

        return new(
            p[1].X - p[0].X + (g * p[1].X),
            p[3].X - p[0].X + (h * p[3].X),
            p[0].X,
            p[1].Y - p[0].Y + (g * p[1].Y),
            p[3].Y - p[0].Y + (h * p[3].Y),
            p[0].Y,
            g,
            h,
            1);
    }
}
