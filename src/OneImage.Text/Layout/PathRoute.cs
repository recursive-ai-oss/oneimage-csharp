using System.Numerics;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage.Text.Layout;

/// <summary>
/// A flattened path that can be asked for the point and direction at a distance along it. Open paths continue straight past
/// their ends (backwards before the start along the first segment, forwards after the end along the last); closed paths wrap.
/// </summary>
internal sealed class PathRoute
{
    private readonly Vector2[] _points;     // segment i runs from _points[i] to _points[(i + 1) % count] (the last wraps only when closed)
    private readonly double[] _start;       // distance at the start of each segment
    private readonly double[] _length;
    private readonly int _segments;

    private PathRoute(Vector2[] points, double[] start, double[] length, int segments, bool closed, double total)
    {
        _points = points;
        _start = start;
        _length = length;
        _segments = segments;
        IsClosed = closed;
        TotalLength = total;
    }

    public bool IsClosed { get; }

    public double TotalLength { get; }

    /// <summary>Builds a route from the first contour of the path that has a length.</summary>
    /// <exception cref="ArgumentException">The path has no contour with a length.</exception>
    public static PathRoute Create(Path path)
    {
        foreach (Contour c in path.Flatten())
        {
            PathRoute? r = FromContour(c);
            if (r is not null)
            {
                return r;
            }
        }

        throw new ArgumentException("The path to lay text along has no length.", nameof(path));
    }

    private static PathRoute? FromContour(Contour c)
    {
        Vector2[] raw = c.Points;
        var pts = new List<Vector2>(raw.Length + 1);
        foreach (Vector2 p in raw)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
            {
                continue;
            }

            if (pts.Count == 0 || pts[^1] != p)
            {
                pts.Add(p);
            }
        }

        bool closed = c.IsClosed;
        if (closed && pts.Count > 1 && pts[0] == pts[^1])
        {
            pts.RemoveAt(pts.Count - 1);
        }

        if (pts.Count < 2)
        {
            return null;
        }

        int segs = closed ? pts.Count : pts.Count - 1;
        var start = new double[segs];
        var len = new double[segs];
        double total = 0;
        for (int i = 0; i < segs; i++)
        {
            Vector2 a = pts[i];
            Vector2 b = pts[(i + 1) % pts.Count];
            double dx = (double)b.X - a.X;
            double dy = (double)b.Y - a.Y;
            start[i] = total;
            len[i] = Math.Sqrt((dx * dx) + (dy * dy));
            total += len[i];
        }

        return total > 0 ? new PathRoute([.. pts], start, len, segs, closed, total) : null;
    }

    /// <summary>Gets the point at <paramref name="distance"/> along the route and the unit tangent there.</summary>
    public void Sample(double distance, out Vector2 point, out float cos, out float sin)
    {
        if (IsClosed)
        {
            distance %= TotalLength;
            if (distance < 0)
            {
                distance += TotalLength;
            }
        }

        // The last segment starting at or before the distance (the first one for negative distances).
        int lo = 0;
        int hi = _segments - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_start[mid] <= distance)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Skip a zero-length segment (cannot happen after de-duplication, but a guard costs nothing).
        Vector2 a = _points[lo];
        Vector2 b = _points[(lo + 1) % _points.Length];
        double t = (distance - _start[lo]) / _length[lo];
        double dx = (double)b.X - a.X;
        double dy = (double)b.Y - a.Y;
        point = new Vector2((float)(a.X + (dx * t)), (float)(a.Y + (dy * t)));
        cos = (float)(dx / _length[lo]);
        sin = (float)(dy / _length[lo]);
    }
}
