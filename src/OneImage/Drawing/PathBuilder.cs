using System.Numerics;

namespace OneImage.Drawing;

/// <summary>
/// Builds a <see cref="Path"/> from move, line, Bézier and arc commands, in pixel coordinates (x to the right, y down; pixel
/// (i, j) covers [i, i+1) × [j, j+1)). The builder starts at the origin with no open figure. A segment command with no open
/// figure (at the start, or after <see cref="Close"/>) begins a new figure at the current point. Not thread-safe.
/// </summary>
public sealed class PathBuilder
{
    /// <summary>The most commands a path may hold; beyond it the builder throws <see cref="ImageLimitException"/>.</summary>
    public const int MaxCommands = 1 << 25;

    private byte[] _verbs = new byte[16];
    private float[] _coords = new float[64];
    private int _verbCount;
    private int _coordCount;
    private Vector2 _current;
    private Vector2 _start;
    private bool _open;

    /// <summary>Gets the current point: where the next segment starts.</summary>
    public Vector2 CurrentPoint => _current;

    /// <summary>Starts a new figure at (<paramref name="x"/>, <paramref name="y"/>). A figure that holds only a move is dropped.</summary>
    /// <param name="x">Horizontal position.</param>
    /// <param name="y">Vertical position.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is NaN or infinite.</exception>
    public PathBuilder MoveTo(float x, float y)
    {
        CheckFinite(x, y);
        if (_verbCount > 0 && _verbs[_verbCount - 1] == (byte)Verb.Move)
        {
            // Two moves in a row: the first figure would be empty.
            _coords[_coordCount - 2] = x;
            _coords[_coordCount - 1] = y;
        }
        else
        {
            Add(Verb.Move, x, y);
        }

        _current = _start = new Vector2(x, y);
        _open = true;
        return this;
    }

    /// <inheritdoc cref="MoveTo(float, float)"/>
    /// <param name="point">The new position.</param>
    public PathBuilder MoveTo(Vector2 point) => MoveTo(point.X, point.Y);

    /// <summary>Adds a straight segment from the current point to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <param name="x">Horizontal end position.</param>
    /// <param name="y">Vertical end position.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is NaN or infinite.</exception>
    public PathBuilder LineTo(float x, float y)
    {
        CheckFinite(x, y);
        EnsureFigure();
        Add(Verb.Line, x, y);
        _current = new Vector2(x, y);
        return this;
    }

    /// <inheritdoc cref="LineTo(float, float)"/>
    /// <param name="point">The end position.</param>
    public PathBuilder LineTo(Vector2 point) => LineTo(point.X, point.Y);

    /// <summary>Adds a quadratic Bézier segment with one control point.</summary>
    /// <param name="cx">Control point x.</param>
    /// <param name="cy">Control point y.</param>
    /// <param name="x">End x.</param>
    /// <param name="y">End y.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is NaN or infinite.</exception>
    public PathBuilder QuadraticBezierTo(float cx, float cy, float x, float y)
    {
        CheckFinite(cx, cy);
        CheckFinite(x, y);
        EnsureFigure();
        Add(Verb.Quad, cx, cy, x, y);
        _current = new Vector2(x, y);
        return this;
    }

    /// <summary>Adds a cubic Bézier segment with two control points.</summary>
    /// <param name="c1x">First control point x.</param>
    /// <param name="c1y">First control point y.</param>
    /// <param name="c2x">Second control point x.</param>
    /// <param name="c2y">Second control point y.</param>
    /// <param name="x">End x.</param>
    /// <param name="y">End y.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is NaN or infinite.</exception>
    public PathBuilder CubicBezierTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        CheckFinite(c1x, c1y);
        CheckFinite(c2x, c2y);
        CheckFinite(x, y);
        EnsureFigure();
        Add(Verb.Cubic, c1x, c1y, c2x, c2y, x, y);
        _current = new Vector2(x, y);
        return this;
    }

    /// <summary>
    /// Adds an SVG elliptical arc from the current point to (<paramref name="x"/>, <paramref name="y"/>) (SVG 1.1 appendix F.6): the
    /// arc of an ellipse with radii <paramref name="rx"/>, <paramref name="ry"/>, rotated by <paramref name="rotationDegrees"/>, that
    /// passes through both points. Radii too small to span the points are scaled up; a zero or negative radius gives a straight
    /// line (negative radii use their absolute value); equal end points give nothing. The arc is stored as cubic Béziers of at most 90° each.
    /// </summary>
    /// <param name="rx">Horizontal radius.</param>
    /// <param name="ry">Vertical radius.</param>
    /// <param name="rotationDegrees">Rotation of the ellipse's x axis, in degrees clockwise on screen.</param>
    /// <param name="largeArc">Take the arc longer than 180° when two exist.</param>
    /// <param name="sweep">Draw in the positive-angle direction (clockwise on screen).</param>
    /// <param name="x">End x.</param>
    /// <param name="y">End y.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is NaN or infinite.</exception>
    public PathBuilder ArcTo(float rx, float ry, float rotationDegrees, bool largeArc, bool sweep, float x, float y)
    {
        CheckFinite(rx, ry);
        CheckFinite(rotationDegrees, 0);
        CheckFinite(x, y);
        EnsureFigure();
        double x1 = _current.X, y1 = _current.Y;
        double ar = Math.Abs((double)rx), br = Math.Abs((double)ry);
        if (x1 == x && y1 == y)
        {
            return this;
        }

        if (ar == 0 || br == 0)
        {
            return LineTo(x, y);
        }

        // F.6.5: endpoint to center parameterization.
        double phi = rotationDegrees * (Math.PI / 180.0);
        double cp = Math.Cos(phi), sp = Math.Sin(phi);
        double dx2 = (x1 - x) / 2, dy2 = (y1 - y) / 2;
        double x1p = (cp * dx2) + (sp * dy2);
        double y1p = (-sp * dx2) + (cp * dy2);
        double lambda = (x1p * x1p / (ar * ar)) + (y1p * y1p / (br * br));
        if (lambda > 1)
        {
            // F.6.6: the radii cannot reach; scale them up so exactly one arc fits.
            double s = Math.Sqrt(lambda);
            ar *= s;
            br *= s;
        }

        double num = (ar * ar * br * br) - (ar * ar * y1p * y1p) - (br * br * x1p * x1p);
        double den = (ar * ar * y1p * y1p) + (br * br * x1p * x1p);
        double coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
        if (largeArc == sweep)
        {
            coef = -coef;
        }

        double cxp = coef * (ar * y1p / br);
        double cyp = coef * (-br * x1p / ar);
        double cx = (cp * cxp) - (sp * cyp) + ((x1 + x) / 2);
        double cy = (sp * cxp) + (cp * cyp) + ((y1 + y) / 2);
        double theta1 = Math.Atan2((y1p - cyp) / br, (x1p - cxp) / ar);
        double theta2 = Math.Atan2((-y1p - cyp) / br, (-x1p - cxp) / ar);
        double delta = theta2 - theta1;
        if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }
        else if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }

        int segments = Math.Max(1, (int)Math.Ceiling((Math.Abs(delta) / (Math.PI / 2)) - 1e-9));
        double step = delta / segments;
        double t = 4.0 / 3.0 * Math.Tan(step / 4);
        double a = theta1;
        // Work the whole arc out first: with huge radii the control points can leave the float range, and then the arc is a line.
        Span<float> pts = stackalloc float[6 * 4];
        for (int i = 0; i < segments; i++)
        {
            double a2 = a + step;
            double c1 = Math.Cos(a), s1 = Math.Sin(a), c2 = Math.Cos(a2), s2 = Math.Sin(a2);
            // Unit-circle control points, then scale, rotate and move to the center.
            double p1x = c1 - (t * s1), p1y = s1 + (t * c1);
            double p2x = c2 + (t * s2), p2y = s2 - (t * c2);
            Span<float> seg = pts.Slice(i * 6, 6);
            seg[0] = (float)Map(p1x, p1y, true);
            seg[1] = (float)Map(p1x, p1y, false);
            seg[2] = (float)Map(p2x, p2y, true);
            seg[3] = (float)Map(p2x, p2y, false);
            seg[4] = i == segments - 1 ? x : (float)Map(c2, s2, true);
            seg[5] = i == segments - 1 ? y : (float)Map(c2, s2, false);
            a = a2;
        }

        for (int i = 0; i < segments * 6; i++)
        {
            if (!float.IsFinite(pts[i]))
            {
                return LineTo(x, y);
            }
        }

        for (int i = 0; i < segments; i++)
        {
            Span<float> seg = pts.Slice(i * 6, 6);
            CubicBezierTo(seg[0], seg[1], seg[2], seg[3], seg[4], seg[5]);
        }

        return this;

        double Map(double ux, double uy, bool isX) =>
            isX ? (cp * ar * ux) - (sp * br * uy) + cx : (sp * ar * ux) + (cp * br * uy) + cy;
    }

    /// <summary>Closes the open figure with a straight segment back to its start; the next segment starts a new figure there. Does nothing when no figure is open.</summary>
    /// <returns>This builder.</returns>
    public PathBuilder Close()
    {
        if (_open && _verbCount > 0 && _verbs[_verbCount - 1] != (byte)Verb.Move && _verbs[_verbCount - 1] != (byte)Verb.Close)
        {
            Add(Verb.Close);
            _current = _start;
            _open = false;
        }

        return this;
    }

    /// <summary>Appends all figures of <paramref name="path"/>.</summary>
    /// <param name="path">The path to append.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public PathBuilder AddPath(Path path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Verbs.Length > 0)
        {
            Reserve(path.Verbs.Length, path.Coords.Length);
            path.Verbs.CopyTo(_verbs, _verbCount);
            path.Coords.CopyTo(_coords, _coordCount);
            _verbCount += path.Verbs.Length;
            _coordCount += path.Coords.Length;
            _open = false;
            _current = _start = Vector2.Zero;
        }

        return this;
    }

    /// <summary>Discards everything and returns the builder to the origin.</summary>
    /// <returns>This builder.</returns>
    public PathBuilder Clear()
    {
        _verbCount = 0;
        _coordCount = 0;
        _current = _start = Vector2.Zero;
        _open = false;
        return this;
    }

    /// <summary>Creates the immutable path. The builder keeps its state and can go on.</summary>
    /// <returns>The path; empty figures (a lone move) are dropped.</returns>
    public Path Build()
    {
        int vc = _verbCount;
        int cc = _coordCount;
        if (vc > 0 && _verbs[vc - 1] == (byte)Verb.Move)
        {
            vc--;
            cc -= 2;
        }

        return vc == 0 ? Path.Empty : new Path(_verbs.AsSpan(0, vc).ToArray(), _coords.AsSpan(0, cc).ToArray());
    }

    private static void CheckFinite(float a, float b)
    {
        if (!float.IsFinite(a) || !float.IsFinite(b))
        {
            throw new ArgumentOutOfRangeException(nameof(a), "Path coordinates must be finite numbers.");
        }
    }

    private void EnsureFigure()
    {
        if (!_open)
        {
            MoveTo(_current.X, _current.Y);
        }
    }

    private void Reserve(int verbs, int coords)
    {
        if ((long)_verbCount + verbs > MaxCommands)
        {
            throw new ImageLimitException($"A path may hold at most {MaxCommands} commands.");
        }

        if (_verbCount + verbs > _verbs.Length)
        {
            Array.Resize(ref _verbs, Math.Max(_verbs.Length * 2, _verbCount + verbs));
        }

        if (_coordCount + coords > _coords.Length)
        {
            Array.Resize(ref _coords, Math.Max(_coords.Length * 2, _coordCount + coords));
        }
    }

    private void Add(Verb verb, float a = 0, float b = 0, float c = 0, float d = 0, float e = 0, float f = 0)
    {
        int n = verb switch { Verb.Move or Verb.Line => 2, Verb.Quad => 4, Verb.Cubic => 6, _ => 0 };
        Reserve(1, n);
        _verbs[_verbCount++] = (byte)verb;
        if (n > 0)
        {
            _coords[_coordCount++] = a;
            _coords[_coordCount++] = b;
        }

        if (n > 2)
        {
            _coords[_coordCount++] = c;
            _coords[_coordCount++] = d;
        }

        if (n > 4)
        {
            _coords[_coordCount++] = e;
            _coords[_coordCount++] = f;
        }
    }
}
