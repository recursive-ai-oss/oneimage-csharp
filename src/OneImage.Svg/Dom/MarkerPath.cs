namespace OneImage.Svg.Dom;

/// <summary>A place where a marker can be drawn: the vertex and the direction of the path there, in degrees.</summary>
/// <param name="X">The vertex x in the path's own coordinates.</param>
/// <param name="Y">The vertex y.</param>
/// <param name="Angle">The direction <c>orient="auto"</c> turns the marker to: the bisector of the incoming and outgoing directions, or the one that exists at the ends of an open subpath.</param>
internal readonly record struct MarkerVertex(double X, double Y, double Angle);

/// <summary>
/// Collects the segments of a path as the path data parser produces them and works out the marker vertices from them (SVG 1.1 section
/// 11.6.2, SVG 2 section 13.4.1): one vertex at the start of every subpath and one at the end of every segment, where a closepath
/// counts as a straight segment back to the start. The direction of a segment at its ends comes from the first and last control
/// point that differs from the end; a segment without any (zero length) takes the direction of its neighbor.
/// </summary>
internal sealed class MarkerPath
{
    private const double Epsilon = 1e-12;

    // One segment: its end point and the tangent directions at its start and end (zero vectors when it has no direction).
    private readonly record struct Seg(double X, double Y, double Sx, double Sy, double Ex, double Ey);

    // A subpath: where it starts and its segments, in the shared list from First, Count of them.
    private readonly record struct Sub(double X, double Y, int First, int Count, bool Closed);

    private readonly List<Seg> _segs = [];
    private readonly List<Sub> _subs = [];
    private double _startX;
    private double _startY;
    private double _curX;
    private double _curY;
    private int _first;
    private bool _open;

    /// <summary>Gets the number of segments recorded (the parser budget for what a path can hold is the document size limit).</summary>
    public int SegmentCount => _segs.Count;

    public void MoveTo(double x, double y)
    {
        Finish(closed: false);
        _startX = _curX = x;
        _startY = _curY = y;
        _first = _segs.Count;
        _open = true;
    }

    public void LineTo(double x, double y)
    {
        Begin();
        Add(x, y, x - _curX, y - _curY, x - _curX, y - _curY);
    }

    public void QuadTo(double cx, double cy, double x, double y)
    {
        Begin();
        Add(x, y, Pick(cx - _curX, cy - _curY, x - _curX, y - _curY), Pick(x - cx, y - cy, x - _curX, y - _curY));
    }

    public void CubicTo(double c1x, double c1y, double c2x, double c2y, double x, double y)
    {
        Begin();
        (double sx, double sy) = Pick3(c1x - _curX, c1y - _curY, c2x - _curX, c2y - _curY, x - _curX, y - _curY);
        (double ex, double ey) = Pick3(x - c2x, y - c2y, x - c1x, y - c1y, x - _curX, y - _curY);
        Add(x, y, sx, sy, ex, ey);
    }

    /// <summary>An elliptical arc in the endpoint form of SVG 1.1 appendix F.6; its tangents come from the center form.</summary>
    public void ArcTo(double rx, double ry, double rotationDegrees, bool large, bool sweep, double x, double y)
    {
        Begin();
        double dx = x - _curX;
        double dy = y - _curY;
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx < Epsilon || ry < Epsilon || (dx == 0 && dy == 0))
        {
            Add(x, y, dx, dy, dx, dy);
            return;
        }

        double phi = rotationDegrees * Math.PI / 180;
        double cos = Math.Cos(phi);
        double sin = Math.Sin(phi);

        // F.6.5.1: the start point relative to the midpoint, in the ellipse's own axes.
        double hx = dx / 2;
        double hy = dy / 2;
        double x1 = (cos * -hx) + (sin * -hy);
        double y1 = (-sin * -hx) + (cos * -hy);
        double lambda = (x1 * x1 / (rx * rx)) + (y1 * y1 / (ry * ry));
        if (lambda > 1)
        {
            double s = Math.Sqrt(lambda);
            rx *= s;
            ry *= s;
        }

        // F.6.5.2: the center, then the start and end angles.
        double num = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
        double den = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
        double coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
        double cxp = coef * rx * y1 / ry;
        double cyp = -coef * ry * x1 / rx;
        double t1 = Math.Atan2((y1 - cyp) / ry, (x1 - cxp) / rx);
        double t2 = Math.Atan2((-y1 - cyp) / ry, (-x1 - cxp) / rx);

        // The sweep, in the direction the sweep flag gives, split into pieces of at most a quarter turn (arcs are curves of that size, so
        // their joints are vertices too).
        double delta = t2 - t1;
        if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }
        else if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }

        int pieces = Math.Clamp((int)Math.Ceiling((Math.Abs(delta) / (Math.PI / 2)) - 1e-4), 1, 4);
        double ccx = (cos * cxp) - (sin * cyp) + ((_curX + x) / 2);
        double ccy = (sin * cxp) + (cos * cyp) + ((_curY + y) / 2);

        // The derivative of the ellipse at angle t, turned back by the rotation; the sweep flag sets the direction of travel.
        double dir = sweep ? 1 : -1;
        (double, double) Tangent(double t)
        {
            double ex = -rx * Math.Sin(t) * dir;
            double ey = ry * Math.Cos(t) * dir;
            return ((cos * ex) - (sin * ey), (sin * ex) + (cos * ey));
        }

        for (int k = 1; k <= pieces; k++)
        {
            double ta = t1 + (delta * (k - 1) / pieces);
            double tb = t1 + (delta * k / pieces);
            (double sx, double sy) = Tangent(ta);
            (double ex, double ey) = Tangent(tb);
            double px = k == pieces ? x : ccx + (cos * rx * Math.Cos(tb)) - (sin * ry * Math.Sin(tb));
            double py = k == pieces ? y : ccy + (sin * rx * Math.Cos(tb)) + (cos * ry * Math.Sin(tb));
            Add(px, py, sx, sy, ex, ey);
        }
    }

    public void Close()
    {
        if (!_open)
        {
            return;
        }

        if (_implicit)
        {
            // A closepath right after a closepath closes nothing and makes no vertex.
            return;
        }

        // The closing segment is a line back to the start, possibly of zero length.
        double dx = _startX - _curX;
        double dy = _startY - _curY;
        _segs.Add(new Seg(_startX, _startY, dx, dy, dx, dy));
        Finish(closed: true);
        _curX = _startX;
        _curY = _startY;

        // Drawing on after a closepath continues from the start in a new subpath.
        _first = _segs.Count;
        _open = true;
        _implicit = true;
    }

    private bool _implicit;

    /// <summary>Works out every vertex in path order.</summary>
    public List<MarkerVertex> Vertices()
    {
        Finish(closed: false);
        var result = new List<MarkerVertex>(_segs.Count + _subs.Count);
        for (int si = 0; si < _subs.Count; si++)
        {
            Sub sub = _subs[si];
            if (sub.Count == 0)
            {
                continue;
            }

            int end = sub.First + sub.Count;

            // The vertex a closepath makes at the start is left out when another subpath follows: the next subpath's first vertex is the one that counts there.
            int vertices = sub.Closed && si < _subs.Count - 1 ? sub.Count - 1 : sub.Count;
            for (int i = 0; i <= vertices; i++)
            {
                // Vertex i sits at the end of segment i-1 (vertex 0 at the start of the subpath).
                // A closed subpath is not looked at as a loop: its first vertex has only an outgoing direction and its last only an incoming one.
                bool hasIn = i > 0;
                bool hasOut = i < sub.Count;
                double x = i == 0 ? sub.X : _segs[sub.First + i - 1].X;
                double y = i == 0 ? sub.Y : _segs[sub.First + i - 1].Y;
                double inAngle = 0;
                double outAngle = 0;
                if (hasIn)
                {
                    inAngle = InDirection(sub.First, end, sub.First + i - 1);
                }

                if (hasOut)
                {
                    outAngle = OutDirection(sub.First, end, sub.First + i);
                }

                double angle = !hasIn ? outAngle : !hasOut ? inAngle : Bisect(inAngle, outAngle);
                result.Add(new MarkerVertex(x, y, angle));
            }
        }

        return result;
    }

    // The direction a segment arrives with; a zero-length one borrows the nearest earlier direction in its subpath, else a later one.
    private double InDirection(int first, int end, int s)
    {
        for (int k = s; k >= first; k--)
        {
            if (Angle(_segs[k].Ex, _segs[k].Ey, out double a))
            {
                return a;
            }
        }

        for (int k = s + 1; k < end; k++)
        {
            if (Angle(_segs[k].Sx, _segs[k].Sy, out double a))
            {
                return a;
            }
        }

        return 0;
    }

    private double OutDirection(int first, int end, int s)
    {
        for (int k = s; k < end; k++)
        {
            if (Angle(_segs[k].Sx, _segs[k].Sy, out double a))
            {
                return a;
            }
        }

        for (int k = s - 1; k >= first; k--)
        {
            if (Angle(_segs[k].Ex, _segs[k].Ey, out double a))
            {
                return a;
            }
        }

        return 0;
    }

    private static bool Angle(double x, double y, out double degrees)
    {
        degrees = 0;
        if (Math.Abs(x) < Epsilon && Math.Abs(y) < Epsilon)
        {
            return false;
        }

        degrees = Math.Atan2(y, x) * 180 / Math.PI;
        return true;
    }

    // The angle halfway between two directions, in 0..360: the plain average, or the average the short way round when the two
    // are more than half a turn apart. Exactly opposite directions give the average, which is what the reference renderings show.
    private static double Bisect(double a, double b)
    {
        a = Normalize(a);
        b = Normalize(b);
        return Math.Abs(a - b) > 180 ? (a + b + 360) / 2 : (a + b) / 2;
    }

    private static double Normalize(double degrees)
    {
        double d = degrees % 360;
        return d < 0 ? d + 360 : d;
    }

    private void Begin()
    {
        if (_open && _implicit)
        {
            _implicit = false;
            return;
        }

        if (!_open)
        {
            // Path data that does not start with a move yields no subpath; the parser never gets here.
            _startX = _startY = _curX = _curY = 0;
            _first = _segs.Count;
            _open = true;
        }
    }

    private void Add(double x, double y, double sx, double sy, double ex, double ey)
    {
        _segs.Add(new Seg(x, y, sx, sy, ex, ey));
        _curX = x;
        _curY = y;
    }

    private void Add(double x, double y, (double X, double Y) start, (double X, double Y) end) => Add(x, y, start.X, start.Y, end.X, end.Y);

    private void Finish(bool closed)
    {
        if (_open && _segs.Count > _first)
        {
            _subs.Add(new Sub(_startX, _startY, _first, _segs.Count - _first, closed));
        }

        _open = false;
        _implicit = false;
    }

    // A control-point direction: the first vector that is not zero.
    private static (double, double) Pick(double ax, double ay, double bx, double by) =>
        Math.Abs(ax) < Epsilon && Math.Abs(ay) < Epsilon ? (bx, by) : (ax, ay);

    private static (double, double) Pick3(double ax, double ay, double bx, double by, double cx, double cy)
    {
        if (Math.Abs(ax) >= Epsilon || Math.Abs(ay) >= Epsilon)
        {
            return (ax, ay);
        }

        return Pick(bx, by, cx, cy);
    }
}
