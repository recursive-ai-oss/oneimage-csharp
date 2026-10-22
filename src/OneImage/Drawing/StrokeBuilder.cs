using System.Numerics;

namespace OneImage.Drawing;

/// <summary>
/// Turns a path into the outline of its stroke: closed contours that, filled non-zero, cover exactly the stroked line.
/// <para>
/// Each figure is flattened (curves within the tolerance), cut into dashes if asked, and every dash or figure becomes one
/// contour: the left edge walked forward, the end cap, the right edge walked back, the start cap. A closed figure becomes two
/// contours, its outer and inner edge. Every contour has the stroke on the same side of its direction, so overlapping
/// contours add up under the non-zero rule and never cancel. Outer corners follow the join style; inner corners use the
/// intersection of the two offset edges when both segments are long enough to reach it, and otherwise pass through the
/// vertex, which keeps the winding right (the body of the stroke is covered by both segments' rectangles anyway). Round joins and
/// caps are cubic arcs of at most 90°; a corner too slight to show a difference from a bevel is emitted as one.
/// </para>
/// </summary>
internal sealed class StrokeBuilder : IDashSink
{
    private const double Kappa4 = 4.0 / 3.0;
    private const int MaxVerbs = 1 << 23;
    private const int MaxCoords = 1 << 25;
    private const double Eps = 1e-9;

    private readonly double _r;
    private readonly StrokeJoin _join;
    private readonly StrokeCap _cap;
    private readonly double _miterLimitSq;
    private readonly double _flatSagitta;

    private byte[] _verbs = new byte[64];
    private float[] _coords = new float[256];
    private int _vn, _cn;
    private bool _pendingMove;

    private double[] _pts = new double[64];
    private double[] _ux = new double[32], _uy = new double[32], _len = new double[32];
    private double[] _rev = new double[64];

    private StrokeBuilder(double width, StrokeJoin join, StrokeCap cap, double miterLimit, double tolerance)
    {
        _r = width / 2;
        _join = join;
        _cap = cap;
        _miterLimitSq = miterLimit * miterLimit;
        _flatSagitta = tolerance * 0.1;
    }

    /// <summary>Builds the outline. <paramref name="width"/> 0 gives an empty path.</summary>
    public static Path Build(Path path, double width, StrokeJoin join, StrokeCap cap, double miterLimit, ReadOnlySpan<float> dash, double dashOffset, double tolerance)
    {
        if (!(width > 0) || path.IsEmpty)
        {
            return Path.Empty;
        }

        var b = new StrokeBuilder(width, join, cap, miterLimit, tolerance);
        FlatPath flat = Flattener.Flatten(path, Matrix3x2.Identity, tolerance, null);
        DashSplitter? dasher = dash.Length > 0 ? new DashSplitter(dash, dashOffset) : null;
        int begin = 0;
        for (int f = 0; f < flat.FigureCount; f++)
        {
            int end = flat.Ends[f];
            int n = end - begin;
            if (n > 0)
            {
                b.Figure(flat.Xy.AsSpan(2 * begin, 2 * n), n, flat.Closed[f], dasher);
            }

            begin = end;
        }

        return new Path(b._verbs.AsSpan(0, b._vn).ToArray(), b._coords.AsSpan(0, b._cn).ToArray());
    }

    void IDashSink.Dash(double[] xy, int count, bool closed, double dirX, double dirY)
    {
        if (closed)
        {
            Contour(xy, count, true, true, 1, 0);
            return;
        }

        // A zero-length dash is a dot: it needs a direction for a square cap, and shows nothing with a butt cap.
        Contour(xy, count, false, true, dirX, dirY);
    }

    private void Figure(ReadOnlySpan<float> xyf, int n, bool closed, DashSplitter? dasher)
    {
        double[] p = Grow(ref _pts, 2 * n);
        for (int i = 0; i < 2 * n; i++)
        {
            p[i] = xyf[i];
        }

        n = Dedupe(p, n, closed);
        if (n == 1)
        {
            // M p L p Z is a dot; a lone M p draws nothing (the flattener cannot tell M p L p from M p, so open ones draw nothing).
            if (closed && dasher == null)
            {
                Dot(p[0], p[1], 1, 0);
            }

            return;
        }

        if (dasher == null)
        {
            Contour(p, n, closed, false, 1, 0);
        }
        else
        {
            dasher.Split(p, n, closed, this);
        }
    }

    // Removes repeated points (and, for a closed figure, a last point equal to the first). Returns the new count.
    private static int Dedupe(double[] p, int n, bool closed)
    {
        int m = 1;
        for (int i = 1; i < n; i++)
        {
            double dx = p[2 * i] - p[2 * (m - 1)], dy = p[(2 * i) + 1] - p[(2 * (m - 1)) + 1];
            if ((dx * dx) + (dy * dy) > 1e-18)
            {
                p[2 * m] = p[2 * i];
                p[(2 * m) + 1] = p[(2 * i) + 1];
                m++;
            }
        }

        if (closed && m > 1)
        {
            double dx = p[2 * (m - 1)] - p[0], dy = p[(2 * (m - 1)) + 1] - p[1];
            if ((dx * dx) + (dy * dy) <= 1e-18)
            {
                m--;
            }
        }

        return m;
    }

    /// <summary>Strokes one polyline (already free of repeats unless <paramref name="piece"/>, which dashes may produce).</summary>
    private void Contour(double[] p, int n, bool closed, bool piece, double dirX, double dirY)
    {
        if (piece && !closed)
        {
            n = Dedupe(p, n, false);
            if (n == 1)
            {
                if (_cap != StrokeCap.Butt)
                {
                    Dot(p[0], p[1], dirX, dirY);
                }

                return;
            }
        }

        if (closed && n < 2)
        {
            return;
        }

        int m = closed ? n : n - 1;
        EnsureDirs(m);
        Directions(p, n, m);
        _pendingMove = true;
        Side(p, n, closed, hairpinOuter: true);
        if (closed)
        {
            Close();
            // The inner (or outer) edge: the same figure walked backwards.
            double[] q = Reverse(p, n);
            Directions(q, n, m);
            _pendingMove = true;
            Side(q, n, true, hairpinOuter: false);
            Close();
            return;
        }

        double ex = p[2 * (n - 1)], ey = p[(2 * (n - 1)) + 1];
        Cap(ex, ey, _ux[m - 1], _uy[m - 1]);
        double[] r = Reverse(p, n);
        Directions(r, n, m);
        Side(r, n, false, hairpinOuter: false);
        Cap(r[2 * (n - 1)], r[(2 * (n - 1)) + 1], _ux[m - 1], _uy[m - 1]);
        Close();
    }

    private double[] Reverse(double[] p, int n)
    {
        double[] q = Grow(ref _rev, 2 * n);
        for (int i = 0; i < n; i++)
        {
            q[2 * i] = p[2 * (n - 1 - i)];
            q[(2 * i) + 1] = p[(2 * (n - 1 - i)) + 1];
        }

        return q;
    }

    private void EnsureDirs(int m)
    {
        if (m > _ux.Length)
        {
            int size = Math.Max(m, _ux.Length * 2);
            Array.Resize(ref _ux, size);
            Array.Resize(ref _uy, size);
            Array.Resize(ref _len, size);
        }
    }

    private void Directions(double[] p, int n, int m)
    {
        for (int i = 0; i < m; i++)
        {
            int j = (i + 1) % n;
            double dx = p[2 * j] - p[2 * i], dy = p[(2 * j) + 1] - p[(2 * i) + 1];
            double l = Math.Sqrt((dx * dx) + (dy * dy));
            _len[i] = l;
            _ux[i] = dx / l;
            _uy[i] = dy / l;
        }
    }

    // Walks the left edge of the polyline in its direction. Directions must be filled in for p.
    private void Side(double[] p, int n, bool closed, bool hairpinOuter)
    {
        int m = closed ? n : n - 1;
        if (!closed)
        {
            To(p[0] - (_uy[0] * _r), p[1] + (_ux[0] * _r));
            for (int i = 1; i < n - 1; i++)
            {
                Join(p[2 * i], p[(2 * i) + 1], i - 1, i, hairpinOuter);
            }

            To(p[2 * (n - 1)] - (_uy[m - 1] * _r), p[(2 * (n - 1)) + 1] + (_ux[m - 1] * _r));
            return;
        }

        for (int i = 0; i < n; i++)
        {
            Join(p[2 * i], p[(2 * i) + 1], (i + m - 1) % m, i, hairpinOuter);
        }
    }

    // The corner at (vx, vy) between segment a (arriving) and segment b (leaving); emits points of the left edge.
    private void Join(double vx, double vy, int a, int b, bool hairpinOuter)
    {
        double u1x = _ux[a], u1y = _uy[a], u2x = _ux[b], u2y = _uy[b];
        double n1x = -u1y * _r, n1y = u1x * _r, n2x = -u2y * _r, n2y = u2x * _r;
        double cross = (u1x * u2y) - (u1y * u2x);
        double dot = (u1x * u2x) + (u1y * u2y);
        bool straight = Math.Abs(cross) < Eps;
        if (straight && dot > 0)
        {
            To(vx + n2x, vy + n2y);
            return;
        }

        bool inner = straight ? !hairpinOuter : cross > 0;
        double k = 1 + dot;
        if (inner)
        {
            // The offset edges meet at distance r·tan(φ/2) back along both segments.
            double t = _r * Math.Abs(cross) / k;
            if (!straight && k > Eps && t <= Math.Min(_len[a], _len[b]))
            {
                To(vx + ((n1x + n2x) / k), vy + ((n1y + n2y) / k));
            }
            else
            {
                To(vx + n1x, vy + n1y);
                To(vx, vy);
                To(vx + n2x, vy + n2y);
            }

            return;
        }

        switch (_join)
        {
            case StrokeJoin.Miter:
                // Miter length over half-width is 1 / cos(φ/2) = sqrt(2 / (1 + cos φ)); SVG cuts to a bevel past the limit.
                if (!straight && k > Eps && 2 / k <= _miterLimitSq)
                {
                    To(vx + ((n1x + n2x) / k), vy + ((n1y + n2y) / k));
                    return;
                }

                break;
            case StrokeJoin.Square:
                {
                    double phi = Math.Atan2(Math.Abs(cross), dot);
                    double e = _r * Math.Tan(phi / 4);
                    To(vx + n1x + (u1x * e), vy + n1y + (u1y * e));
                    To(vx + n2x - (u2x * e), vy + n2y - (u2y * e));
                    return;
                }

            case StrokeJoin.Round:
                {
                    double phi = Math.Atan2(Math.Abs(cross), dot);
                    if (_r * (1 - Math.Cos(phi / 2)) >= _flatSagitta)
                    {
                        To(vx + n1x, vy + n1y);
                        Arc(vx, vy, n1x, n1y, -phi);
                        return;
                    }

                    break;
                }
        }

        To(vx + n1x, vy + n1y);
        To(vx + n2x, vy + n2y);
    }

    // The end of the left edge is at E + r·left(d); finish the stroke end so the right edge can start at E − r·left(d).
    private void Cap(double ex, double ey, double dx, double dy)
    {
        double lx = -dy * _r, ly = dx * _r;
        switch (_cap)
        {
            case StrokeCap.Square:
                To(ex + lx + (dx * _r), ey + ly + (dy * _r));
                To(ex - lx + (dx * _r), ey - ly + (dy * _r));
                break;
            case StrokeCap.Round:
                Arc(ex, ey, lx, ly, -Math.PI);
                break;
        }
    }

    private void Dot(double x, double y, double dx, double dy)
    {
        double r = _r;
        if (_cap == StrokeCap.Round)
        {
            _pendingMove = true;
            To(x + r, y);
            Arc(x, y, r, 0, 2 * Math.PI);
            Close();
        }
        else if (_cap == StrokeCap.Square)
        {
            double lx = -dy * r, ly = dx * r, fx = dx * r, fy = dy * r;
            _pendingMove = true;
            To(x - lx - fx, y - ly - fy);
            To(x + lx - fx, y + ly - fy);
            To(x + lx + fx, y + ly + fy);
            To(x - lx + fx, y - ly + fy);
            Close();
        }
    }

    // Appends cubic pieces of at most 90° from the point center + v, turning by sweep (radians, positive is x toward y).
    private void Arc(double cx, double cy, double vx, double vy, double sweep)
    {
        int pieces = Math.Max(1, (int)Math.Ceiling((Math.Abs(sweep) / (Math.PI / 2)) - 1e-9));
        double da = sweep / pieces;
        double k = Kappa4 * Math.Tan(da / 4);
        double cos = Math.Cos(da), sin = Math.Sin(da);
        for (int i = 0; i < pieces; i++)
        {
            double wx = (vx * cos) - (vy * sin), wy = (vx * sin) + (vy * cos);
            Cubic(cx + vx - (vy * k), cy + vy + (vx * k), cx + wx + (wy * k), cy + wy - (wx * k), cx + wx, cy + wy);
            vx = wx;
            vy = wy;
        }
    }

    private void To(double x, double y)
    {
        if (_pendingMove)
        {
            Add(0, 2);
            _pendingMove = false;
        }
        else
        {
            Add(1, 2);
        }

        _coords[_cn++] = (float)x;
        _coords[_cn++] = (float)y;
    }

    private void Cubic(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        Add(3, 6);
        _coords[_cn++] = (float)x1;
        _coords[_cn++] = (float)y1;
        _coords[_cn++] = (float)x2;
        _coords[_cn++] = (float)y2;
        _coords[_cn++] = (float)x3;
        _coords[_cn++] = (float)y3;
    }

    private void Close() => Add(4, 0);

    private void Add(byte verb, int coords)
    {
        if (_vn >= MaxVerbs || _cn + coords > MaxCoords)
        {
            throw new ImageLimitException("The stroke outline has too many segments.");
        }

        if (_vn == _verbs.Length)
        {
            Array.Resize(ref _verbs, _verbs.Length * 2);
        }

        if (_cn + coords > _coords.Length)
        {
            Array.Resize(ref _coords, Math.Max(_coords.Length * 2, _cn + coords));
        }

        _verbs[_vn++] = verb;
    }

    private static double[] Grow(ref double[] a, int min)
    {
        if (a.Length < min)
        {
            a = new double[Math.Max(min, a.Length * 2)];
        }

        return a;
    }
}
