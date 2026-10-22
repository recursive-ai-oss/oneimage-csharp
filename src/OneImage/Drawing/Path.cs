using System.Globalization;
using System.Numerics;
using System.Text;
using System.Drawing;

namespace OneImage.Drawing;

/// <summary>The kind of one path command; the coordinates each takes follow it in <see cref="Path.Coords"/>.</summary>
internal enum Verb : byte
{
    Move = 0,
    Line = 1,
    Quad = 2,
    Cubic = 3,
    Close = 4,
}

/// <summary>One flattened figure of a path: straight segments through <see cref="Points"/>.</summary>
/// <param name="Points">The vertices in order.</param>
/// <param name="IsClosed">Whether the figure was closed; a closed figure has an implicit segment from the last point back to the first.</param>
public sealed record Contour(Vector2[] Points, bool IsClosed);

/// <summary>
/// An immutable vector outline: any number of figures, each a chain of straight lines and quadratic or cubic Bézier curves,
/// open or closed. Coordinates are pixel coordinates (x right, y down; pixel (i, j) covers [i, i+1) × [j, j+1)). Create paths
/// with <see cref="PathBuilder"/>, the shape factories here, or <see cref="TryParseSvg"/>. Fills treat every figure as closed.
/// </summary>
public sealed class Path
{
    /// <summary>Control-point curvature error, in pixels, that flattening allows unless told otherwise.</summary>
    public const float DefaultTolerance = 0.1f;

    /// <summary>The most points flattening a path (or filling it) may produce; beyond it an <see cref="ImageLimitException"/> is thrown.</summary>
    public const int MaxFlattenedPoints = 1 << 24;

    private const float Kappa = 0.5522847498307936f;

    private RectangleF? _bounds;

    internal Path(byte[] verbs, float[] coords)
    {
        Verbs = verbs;
        Coords = coords;
    }

    /// <summary>Gets the path with no figures.</summary>
    public static Path Empty { get; } = new([], []);

    internal byte[] Verbs { get; }

    internal float[] Coords { get; }

    /// <summary>Gets a value indicating whether the path has no figures.</summary>
    public bool IsEmpty => Verbs.Length == 0;

    /// <summary>Gets the number of figures (each starts with a move).</summary>
    public int FigureCount
    {
        get
        {
            int n = 0;
            foreach (byte v in Verbs)
            {
                if (v == (byte)Verb.Move)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>
    /// Gets the bounding box of the outline, from the path flattened at <see cref="DefaultTolerance"/> (so a curve's bulge is
    /// included, within that tolerance). An empty path has an empty box at the origin.
    /// </summary>
    /// <exception cref="ImageLimitException">The path flattens to more than <see cref="MaxFlattenedPoints"/> points.</exception>
    public RectangleF Bounds => _bounds ??= ComputeBounds();

    /// <summary>
    /// Gets the length of the outline in pixels, flattened at a tolerance of 0.01: the sum of segment lengths, including the closing
    /// segment of closed figures.
    /// </summary>
    /// <exception cref="ImageLimitException">The path flattens to more than <see cref="MaxFlattenedPoints"/> points.</exception>
    public float Length
    {
        get
        {
            FlatPath flat = Flattener.Flatten(this, Matrix3x2.Identity, 0.01, null);
            double total = 0;
            int begin = 0;
            for (int f = 0; f < flat.FigureCount; f++)
            {
                int end = flat.Ends[f];
                for (int i = begin + 1; i < end; i++)
                {
                    total += Math.Sqrt(Sq(flat.Xy[2 * i] - flat.Xy[2 * (i - 1)]) + Sq(flat.Xy[(2 * i) + 1] - flat.Xy[(2 * (i - 1)) + 1]));
                }

                if (flat.Closed[f] && end - begin > 2)
                {
                    total += Math.Sqrt(Sq(flat.Xy[2 * begin] - flat.Xy[2 * (end - 1)]) + Sq(flat.Xy[(2 * begin) + 1] - flat.Xy[(2 * (end - 1)) + 1]));
                }

                begin = end;
            }

            return (float)total;
        }
    }

    // ---- Shapes ----

    /// <summary>A closed rectangle, drawn clockwise on screen starting at the top-left corner. Negative sizes are normalized.</summary>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is NaN or infinite.</exception>
    public static Path Rectangle(float x, float y, float width, float height)
    {
        if (width < 0)
        {
            x += width;
            width = -width;
        }

        if (height < 0)
        {
            y += height;
            height = -height;
        }

        return new PathBuilder().MoveTo(x, y).LineTo(x + width, y).LineTo(x + width, y + height).LineTo(x, y + height).Close().Build();
    }

    /// <summary>A closed rectangle with rounded corners, drawn clockwise from the top edge. The radius is limited to half the shorter side.</summary>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width, positive.</param>
    /// <param name="height">Height, positive.</param>
    /// <param name="radius">Corner radius.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is NaN or infinite, or the size is not positive.</exception>
    public static Path RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        if (!(width > 0) || !(height > 0) || !float.IsFinite(width) || !float.IsFinite(height))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A rounded rectangle needs a positive size.");
        }

        float r = Math.Clamp(radius, 0f, Math.Min(width, height) / 2);
        var b = new PathBuilder().MoveTo(x + r, y).LineTo(x + width - r, y);
        b.ArcTo(r, r, 0, false, true, x + width, y + r).LineTo(x + width, y + height - r);
        b.ArcTo(r, r, 0, false, true, x + width - r, y + height).LineTo(x + r, y + height);
        b.ArcTo(r, r, 0, false, true, x, y + height - r).LineTo(x, y + r);
        b.ArcTo(r, r, 0, false, true, x + r, y);
        return b.Close().Build();
    }

    /// <summary>A closed ellipse of four cubic Béziers (error under 0.03% of the radius), starting at its right-most point and running clockwise on screen.</summary>
    /// <param name="cx">Center x.</param>
    /// <param name="cy">Center y.</param>
    /// <param name="rx">Horizontal radius.</param>
    /// <param name="ry">Vertical radius.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is NaN or infinite.</exception>
    public static Path Ellipse(float cx, float cy, float rx, float ry)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        float kx = rx * Kappa, ky = ry * Kappa;
        return new PathBuilder()
            .MoveTo(cx + rx, cy)
            .CubicBezierTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry)
            .CubicBezierTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy)
            .CubicBezierTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry)
            .CubicBezierTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy)
            .Close()
            .Build();
    }

    /// <summary>A closed circle; see <see cref="Ellipse"/>.</summary>
    /// <param name="cx">Center x.</param>
    /// <param name="cy">Center y.</param>
    /// <param name="radius">Radius.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is NaN or infinite.</exception>
    public static Path Circle(float cx, float cy, float radius) => Ellipse(cx, cy, radius, radius);

    /// <summary>A closed polygon through <paramref name="points"/> (the last point joins back to the first). Fewer than two points give an empty path.</summary>
    /// <param name="points">The vertices.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is NaN or infinite.</exception>
    /// <exception cref="ImageLimitException">There are more than <see cref="PathBuilder.MaxCommands"/> points.</exception>
    public static Path Polygon(ReadOnlySpan<Vector2> points) => FromPoints(points, close: true);

    /// <summary>An open chain of straight segments through <paramref name="points"/>. A fill closes it implicitly. Fewer than two points give an empty path.</summary>
    /// <param name="points">The vertices.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is NaN or infinite.</exception>
    /// <exception cref="ImageLimitException">There are more than <see cref="PathBuilder.MaxCommands"/> points.</exception>
    public static Path Polyline(ReadOnlySpan<Vector2> points) => FromPoints(points, close: false);

    /// <summary>A regular polygon with its first vertex straight up (before rotation), the others clockwise on screen.</summary>
    /// <param name="cx">Center x.</param>
    /// <param name="cy">Center y.</param>
    /// <param name="sides">Number of sides, at least 3.</param>
    /// <param name="radius">Distance from the center to a vertex.</param>
    /// <param name="rotationDegrees">Clockwise rotation about the center. The default is 0.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sides"/> is under 3 or over 100000, or a value is NaN or infinite.</exception>
    public static Path RegularPolygon(float cx, float cy, int sides, float radius, float rotationDegrees = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sides, 100_000);
        return Star(cx, cy, sides, radius, radius, rotationDegrees, plain: true);
    }

    /// <summary>A star with <paramref name="prongs"/> points alternating between the outer and inner radius; the first outer point is straight up (before rotation).</summary>
    /// <param name="cx">Center x.</param>
    /// <param name="cy">Center y.</param>
    /// <param name="prongs">Number of points, at least 3.</param>
    /// <param name="innerRadius">Radius of the notches.</param>
    /// <param name="outerRadius">Radius of the tips.</param>
    /// <param name="rotationDegrees">Clockwise rotation about the center. The default is 0.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="prongs"/> is under 3 or over 100000, or a value is NaN or infinite.</exception>
    public static Path Star(float cx, float cy, int prongs, float innerRadius, float outerRadius, float rotationDegrees = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(prongs, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prongs, 100_000);
        return Star(cx, cy, prongs, innerRadius, outerRadius, rotationDegrees, plain: false);
    }

    private static Path Star(float cx, float cy, int n, float inner, float outer, float rotationDegrees, bool plain)
    {
        int count = plain ? n : 2 * n;
        var pts = new Vector2[count];
        double rot = (rotationDegrees - 90.0) * (Math.PI / 180.0);
        for (int i = 0; i < count; i++)
        {
            double a = rot + (i * 2 * Math.PI / count);
            double r = plain || (i & 1) == 0 ? outer : inner;
            pts[i] = new Vector2((float)(cx + (r * Math.Cos(a))), (float)(cy + (r * Math.Sin(a))));
        }

        return FromPoints(pts, close: true);
    }

    /// <summary>Joins paths into one whose figures are all of theirs, in order.</summary>
    /// <param name="paths">The paths.</param>
    /// <returns>The combined path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or one of its items is null.</exception>
    /// <exception cref="ImageLimitException">The result would hold more than <see cref="PathBuilder.MaxCommands"/> commands.</exception>
    public static Path Combine(params ReadOnlySpan<Path> paths)
    {
        var b = new PathBuilder();
        foreach (Path p in paths)
        {
            b.AddPath(p);
        }

        return b.Build();
    }

    private static Path FromPoints(ReadOnlySpan<Vector2> points, bool close)
    {
        if (points.Length < 2)
        {
            return Empty;
        }

        var b = new PathBuilder().MoveTo(points[0]);
        for (int i = 1; i < points.Length; i++)
        {
            b.LineTo(points[i]);
        }

        return (close ? b.Close() : b).Build();
    }

    // ---- SVG path data ----

    /// <summary>
    /// Parses SVG path data (the <c>d</c> attribute: M, L, H, V, C, S, Q, T, A, Z in absolute and relative forms; SVG 1.1 section 8.3).
    /// Parsing is strict: the data must start with a move, and the first malformed command makes the whole parse fail. Input over
    /// 64 Mi characters, or producing more than <see cref="PathBuilder.MaxCommands"/> commands, fails. Numbers are read with the invariant culture.
    /// </summary>
    /// <param name="data">The path data.</param>
    /// <param name="path">The parsed path; <see cref="Empty"/> for empty data, <see langword="null"/> on failure.</param>
    /// <returns>Whether the data was valid.</returns>
    public static bool TryParseSvg(ReadOnlySpan<char> data, out Path? path) => SvgPathParser.TryParse(data, out path);

    /// <summary>Parses SVG path data as <see cref="TryParseSvg"/> does and throws when it is invalid.</summary>
    /// <param name="data">The path data.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is not valid path data.</exception>
    public static Path ParseSvg(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return TryParseSvg(data, out Path? p) ? p! : throw new ArgumentException("Not valid SVG path data.", nameof(data));
    }

    /// <summary>Writes the path as SVG path data (absolute M, L, Q, C, Z) that <see cref="TryParseSvg"/> reads back to the same path, with round-trip float formatting.</summary>
    /// <returns>The path data.</returns>
    public string ToSvg()
    {
        var sb = new StringBuilder();
        int c = 0;
        foreach (byte v in Verbs)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            switch ((Verb)v)
            {
                case Verb.Move: sb.Append('M'); Num(sb, 2, ref c); break;
                case Verb.Line: sb.Append('L'); Num(sb, 2, ref c); break;
                case Verb.Quad: sb.Append('Q'); Num(sb, 4, ref c); break;
                case Verb.Cubic: sb.Append('C'); Num(sb, 6, ref c); break;
                default: sb.Append('Z'); break;
            }
        }

        return sb.ToString();

        void Num(StringBuilder s, int count, ref int at)
        {
            for (int i = 0; i < count; i++)
            {
                float v = Coords[at++];
                // Negative zero is written as 0: reading "-0" back through relative arithmetic would not keep its sign anyway.
                s.Append(' ').Append((v == 0f ? 0f : v).ToString("R", CultureInfo.InvariantCulture));
            }
        }
    }

    // ---- Transforms ----

    /// <summary>Applies an affine matrix to every point (a row-vector matrix as in <see cref="Matrix3x2"/>: x' = x·M11 + y·M21 + M31).</summary>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed path.</returns>
    /// <exception cref="ImageOperationException">A transformed coordinate is not finite.</exception>
    public Path Transform(Matrix3x2 matrix)
    {
        if (matrix.IsIdentity || IsEmpty)
        {
            return this;
        }

        var c = new float[Coords.Length];
        for (int i = 0; i < c.Length; i += 2)
        {
            var p = Vector2.Transform(new Vector2(Coords[i], Coords[i + 1]), matrix);
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
            {
                throw new ImageOperationException("The transform moves a path coordinate out of the finite range.");
            }

            c[i] = p.X;
            c[i + 1] = p.Y;
        }

        return new Path(Verbs, c);
    }

    /// <summary>Moves the path.</summary>
    /// <param name="dx">Horizontal offset.</param>
    /// <param name="dy">Vertical offset.</param>
    /// <returns>The moved path.</returns>
    public Path Translate(float dx, float dy) => Transform(Matrix3x2.CreateTranslation(dx, dy));

    /// <summary>Scales about the origin.</summary>
    /// <param name="sx">Horizontal factor.</param>
    /// <param name="sy">Vertical factor.</param>
    /// <returns>The scaled path.</returns>
    public Path Scale(float sx, float sy) => Transform(Matrix3x2.CreateScale(sx, sy));

    /// <summary>Rotates clockwise on screen about (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    /// <param name="degrees">The angle in degrees.</param>
    /// <param name="cx">Pivot x. The default is 0.</param>
    /// <param name="cy">Pivot y. The default is 0.</param>
    /// <returns>The rotated path.</returns>
    public Path Rotate(float degrees, float cx = 0, float cy = 0) =>
        Transform(Matrix3x2.CreateRotation(degrees * (MathF.PI / 180f), new Vector2(cx, cy)));

    /// <summary>Reverses the direction of every figure, which flips the sign of its winding. Closed figures stay closed.</summary>
    /// <returns>The reversed path.</returns>
    public Path Reverse()
    {
        var verbs = new byte[Verbs.Length];
        var coords = new float[Coords.Length];
        int vi = 0, ci = 0;
        int v0 = 0, c0 = 0;
        while (v0 < Verbs.Length)
        {
            // One figure: Move, segments, optional Close.
            int v1 = v0 + 1, c1 = c0 + 2;
            while (v1 < Verbs.Length && Verbs[v1] != (byte)Verb.Move)
            {
                c1 += CoordCount(Verbs[v1]);
                v1++;
            }

            bool closed = Verbs[v1 - 1] == (byte)Verb.Close;
            int segEnd = closed ? v1 - 1 : v1;
            int cSegEnd = closed ? c1 : c1;

            // Walk the segments backwards. Each segment ends at its last point; the reversed figure starts at the final end point.
            int lastPt = cSegEnd - 2;
            verbs[vi++] = (byte)Verb.Move;
            coords[ci++] = Coords[lastPt];
            coords[ci++] = Coords[lastPt + 1];
            int cEnd = cSegEnd;
            for (int v = segEnd - 1; v >= v0 + 1; v--)
            {
                int n = CoordCount(Verbs[v]);
                int cStart = cEnd - n;
                // The segment's start point is the previous segment's end (or the move's point).
                int prev = cStart - 2;
                verbs[vi++] = Verbs[v];
                // Control points in reverse order, then the previous end point.
                for (int k = n - 4; k >= 0; k -= 2)
                {
                    coords[ci++] = Coords[cStart + k];
                    coords[ci++] = Coords[cStart + k + 1];
                }

                coords[ci++] = Coords[prev];
                coords[ci++] = Coords[prev + 1];
                cEnd = cStart;
            }

            if (closed)
            {
                verbs[vi++] = (byte)Verb.Close;
            }

            v0 = v1;
            c0 = c1;
        }

        return new Path(verbs, coords);
    }

    /// <summary>Flattens the path to straight segments.</summary>
    /// <param name="tolerance">The largest distance in pixels a segment may stray from the true curve. The default is <see cref="DefaultTolerance"/>.</param>
    /// <returns>One polyline per figure with at least two points.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is not positive.</exception>
    /// <exception cref="ImageLimitException">The result would exceed <see cref="MaxFlattenedPoints"/> points.</exception>
    public IReadOnlyList<Contour> Flatten(float tolerance = DefaultTolerance)
    {
        if (!(tolerance > 0) || !float.IsFinite(tolerance))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be positive.");
        }

        FlatPath flat = Flattener.Flatten(this, Matrix3x2.Identity, tolerance, null);
        var result = new List<Contour>(flat.FigureCount);
        int begin = 0;
        for (int f = 0; f < flat.FigureCount; f++)
        {
            int end = flat.Ends[f];
            if (end - begin >= 2)
            {
                var pts = new Vector2[end - begin];
                for (int i = 0; i < pts.Length; i++)
                {
                    pts[i] = new Vector2(flat.Xy[2 * (begin + i)], flat.Xy[(2 * (begin + i)) + 1]);
                }

                result.Add(new Contour(pts, flat.Closed[f]));
            }

            begin = end;
        }

        return result;
    }

    internal static int CoordCount(byte verb) => (Verb)verb switch
    {
        Verb.Move or Verb.Line => 2,
        Verb.Quad => 4,
        Verb.Cubic => 6,
        _ => 0,
    };

    private RectangleF ComputeBounds()
    {
        FlatPath flat = Flattener.Flatten(this, Matrix3x2.Identity, DefaultTolerance, null);
        if (flat.PointCount == 0)
        {
            return default;
        }

        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int i = 0; i < flat.PointCount; i++)
        {
            float x = flat.Xy[2 * i], y = flat.Xy[(2 * i) + 1];
            x0 = Math.Min(x0, x);
            y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x);
            y1 = Math.Max(y1, y);
        }

        return new RectangleF(x0, y0, x1 - x0, y1 - y0);
    }

    private static double Sq(double v) => v * v;
}
