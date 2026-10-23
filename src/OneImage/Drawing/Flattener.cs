using System.Numerics;

namespace OneImage.Drawing;

/// <summary>Straight-segment figures: interleaved x, y floats, the end index (in points) of each figure, and whether each was closed.</summary>
internal sealed class FlatPath
{
    private readonly int _limit;
    private int _figureCount;

    public FlatPath(int limit) => _limit = limit;

    /// <summary>Empties the path, keeping its buffers, so one instance can flatten many paths in turn.</summary>
    public void Reset()
    {
        _figureCount = 0;
        PointCount = 0;
    }

    public float[] Xy { get; private set; } = new float[64];

    public int PointCount { get; private set; }

    public int[] Ends { get; private set; } = new int[4];

    public bool[] Closed { get; private set; } = new bool[4];

    public int FigureCount => _figureCount;

    public int CurrentFigureStart => _figureCount == 0 ? 0 : Ends[_figureCount - 1];

    public void AddPoint(double x, double y)
    {
        int n = PointCount;
        if (n > CurrentFigureStart && Xy[(2 * n) - 2] == (float)x && Xy[(2 * n) - 1] == (float)y)
        {
            return; // a repeated vertex adds nothing
        }

        if (n >= _limit)
        {
            throw new ImageLimitException($"The path flattens to more than {_limit} points.");
        }

        if (2 * (n + 1) > Xy.Length)
        {
            Xy = Grow(Xy, 2 * (n + 1));
        }

        Xy[2 * n] = (float)x;
        Xy[(2 * n) + 1] = (float)y;
        PointCount = n + 1;
    }

    public void EndFigure(bool closed)
    {
        if (_figureCount == Ends.Length)
        {
            Ends = Grow(Ends, _figureCount + 1);
            Closed = Grow(Closed, _figureCount + 1);
        }

        Ends[_figureCount] = PointCount;
        Closed[_figureCount] = closed;
        _figureCount++;
    }

    private static T[] Grow<T>(T[] a, int min)
    {
        var r = new T[Math.Max(min, a.Length * 2)];
        a.AsSpan().CopyTo(r);
        return r;
    }
}

/// <summary>
/// Turns a path into straight segments. Curves are cut into equal parameter steps, enough that the chord strays from the curve
/// by at most the tolerance (for a cubic the bound is 3/4·d/n², d being the larger second difference of the control points).
/// </summary>
internal static class Flattener
{
    private const int MaxStepsPerCurve = 4096;

    /// <summary>
    /// Flattens <paramref name="path"/> after applying <paramref name="matrix"/>. When <paramref name="clip"/> is given (device
    /// coordinates), a curve whose control polygon lies entirely outside it is replaced by its chord: the curve cannot enter the
    /// box, and the chord keeps the same winding, so work stays bounded however far away or large a curve is.
    /// </summary>
    public static FlatPath Flatten(Path path, Matrix3x2 matrix, double tolerance, (double X0, double Y0, double X1, double Y1)? clip) =>
        Flatten(path, matrix, tolerance, clip, new FlatPath(Path.MaxFlattenedPoints));

    /// <summary>Like the overload without a target, appending into an empty <paramref name="flat"/> (see <see cref="FlatPath.Reset"/>).</summary>
    public static FlatPath Flatten(Path path, Matrix3x2 matrix, double tolerance, (double X0, double Y0, double X1, double Y1)? clip, FlatPath flat)
    {
        byte[] verbs = path.Verbs;
        float[] coords = path.Coords;
        bool identity = matrix.IsIdentity;
        int c = 0;
        double cx = 0, cy = 0, sx = 0, sy = 0;
        bool inFigure = false;
        for (int v = 0; v < verbs.Length; v++)
        {
            switch ((Verb)verbs[v])
            {
                case Verb.Move:
                    if (inFigure)
                    {
                        flat.EndFigure(false);
                    }

                    Read(coords, ref c, matrix, identity, out cx, out cy);
                    sx = cx;
                    sy = cy;
                    flat.AddPoint(cx, cy);
                    inFigure = true;
                    break;
                case Verb.Line:
                    Read(coords, ref c, matrix, identity, out double lx, out double ly);
                    flat.AddPoint(lx, ly);
                    cx = lx;
                    cy = ly;
                    break;
                case Verb.Quad:
                    Read(coords, ref c, matrix, identity, out double qx, out double qy);
                    Read(coords, ref c, matrix, identity, out double ex, out double ey);
                    Quad(flat, cx, cy, qx, qy, ex, ey, tolerance, clip);
                    cx = ex;
                    cy = ey;
                    break;
                case Verb.Cubic:
                    Read(coords, ref c, matrix, identity, out double ax, out double ay);
                    Read(coords, ref c, matrix, identity, out double bx, out double by);
                    Read(coords, ref c, matrix, identity, out double dx, out double dy);
                    Cubic(flat, cx, cy, ax, ay, bx, by, dx, dy, tolerance, clip);
                    cx = dx;
                    cy = dy;
                    break;
                default:
                    flat.EndFigure(true);
                    inFigure = false;
                    cx = sx;
                    cy = sy;
                    break;
            }
        }

        if (inFigure)
        {
            flat.EndFigure(false);
        }

        return flat;
    }

    private static void Read(float[] coords, ref int c, Matrix3x2 m, bool identity, out double x, out double y)
    {
        float fx = coords[c], fy = coords[c + 1];
        c += 2;
        if (identity)
        {
            x = fx;
            y = fy;
            return;
        }

        // Double arithmetic: a large scale cannot overflow float here, and non-finite results are rejected below.
        x = ((double)fx * m.M11) + ((double)fy * m.M21) + m.M31;
        y = ((double)fx * m.M12) + ((double)fy * m.M22) + m.M32;
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 3e38 || Math.Abs(y) > 3e38)
        {
            throw new ImageOperationException("The transform moves a path coordinate out of the finite range.");
        }
    }

    private static bool Outside(double minX, double minY, double maxX, double maxY, (double X0, double Y0, double X1, double Y1)? clip) =>
        clip is { } k && (maxX < k.X0 - 1 || minX > k.X1 + 1 || maxY < k.Y0 - 1 || minY > k.Y1 + 1);

    private static void Quad(FlatPath flat, double x0, double y0, double x1, double y1, double x2, double y2, double tol, (double X0, double Y0, double X1, double Y1)? clip)
    {
        if (Outside(Math.Min(x0, Math.Min(x1, x2)), Math.Min(y0, Math.Min(y1, y2)), Math.Max(x0, Math.Max(x1, x2)), Math.Max(y0, Math.Max(y1, y2)), clip))
        {
            flat.AddPoint(x2, y2);
            return;
        }

        double ddx = x0 - (2 * x1) + x2, ddy = y0 - (2 * y1) + y2;
        double dd = Math.Sqrt((ddx * ddx) + (ddy * ddy));
        int n = Steps(Math.Sqrt(dd / (4 * tol)));
        for (int i = 1; i < n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            flat.AddPoint((u * u * x0) + (2 * u * t * x1) + (t * t * x2), (u * u * y0) + (2 * u * t * y1) + (t * t * y2));
        }

        flat.AddPoint(x2, y2);
    }

    private static void Cubic(FlatPath flat, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, double tol, (double X0, double Y0, double X1, double Y1)? clip)
    {
        double minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)), maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        double minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)), maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        if (Outside(minX, minY, maxX, maxY, clip))
        {
            flat.AddPoint(x3, y3);
            return;
        }

        double ax = x0 - (2 * x1) + x2, ay = y0 - (2 * y1) + y2;
        double bx = x1 - (2 * x2) + x3, by = y1 - (2 * y2) + y3;
        double dd = Math.Max(Math.Sqrt((ax * ax) + (ay * ay)), Math.Sqrt((bx * bx) + (by * by)));
        int n = Steps(Math.Sqrt(0.75 * dd / tol));
        for (int i = 1; i < n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
            flat.AddPoint((w0 * x0) + (w1 * x1) + (w2 * x2) + (w3 * x3), (w0 * y0) + (w1 * y1) + (w2 * y2) + (w3 * y3));
        }

        flat.AddPoint(x3, y3);
    }

    private static int Steps(double s) => s >= MaxStepsPerCurve ? MaxStepsPerCurve : Math.Max(1, (int)Math.Ceiling(s));
}
