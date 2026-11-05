using System.Numerics;

namespace OneImage.Metafile;

/// <summary>One step of a <see cref="MetafilePath"/>.</summary>
public enum PathVerb : byte
{
    /// <summary>Starts a new figure at one point.</summary>
    MoveTo = 0,

    /// <summary>A straight line to one point.</summary>
    LineTo = 1,

    /// <summary>A cubic Bézier curve; takes three points: two control points and the end point.</summary>
    CubicTo = 2,

    /// <summary>Closes the current figure with a straight line back to its start; takes no point.</summary>
    Close = 3,
}

/// <summary>
/// An immutable outline made of move, line, cubic Bézier and close steps. Arcs, ellipses and rounded rectangles in the file are already
/// turned into cubics. Coordinates are in the space of the command that carries the path (see the command's documentation).
/// </summary>
public sealed class MetafilePath
{
    private readonly PathVerb[] _verbs;
    private readonly Vector2[] _points;

    /// <summary>Gets the path with no steps.</summary>
    public static MetafilePath Empty { get; } = new([], []);

    internal MetafilePath(PathVerb[] verbs, Vector2[] points)
    {
        _verbs = verbs;
        _points = points;
    }

    /// <summary>Gets the steps in order.</summary>
    public IReadOnlyList<PathVerb> Verbs => _verbs;

    /// <summary>Gets the points the steps use, in order: one for each move and line, three for each cubic, none for a close.</summary>
    public IReadOnlyList<Vector2> Points => _points;

    /// <summary>Gets a value indicating whether the path has no steps.</summary>
    public bool IsEmpty => _verbs.Length == 0;

    /// <summary>Gets the smallest rectangle that contains all points (control points included), or an empty rectangle for an empty path.</summary>
    public RectD Bounds
    {
        get
        {
            if (_points.Length == 0)
            {
                return default;
            }

            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Vector2 p in _points)
            {
                x0 = Math.Min(x0, p.X);
                y0 = Math.Min(y0, p.Y);
                x1 = Math.Max(x1, p.X);
                y1 = Math.Max(y1, p.Y);
            }

            return new RectD(x0, y0, x1, y1);
        }
    }

    /// <summary>Returns the path moved and transformed by a matrix.</summary>
    /// <param name="matrix">The matrix, in System.Numerics row-vector convention.</param>
    /// <returns>A new path.</returns>
    public MetafilePath Transform(Matrix3x2 matrix)
    {
        var pts = new Vector2[_points.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = Vector2.Transform(_points[i], matrix);
        }

        return new MetafilePath(_verbs, pts);
    }

    /// <summary>Returns the path as SVG path data, for display and tests.</summary>
    /// <returns>Path data such as <c>M 1 2 L 3 4 Z</c>.</returns>
    public string ToSvgPathData()
    {
        var sb = new System.Text.StringBuilder();
        int p = 0;
        foreach (PathVerb v in _verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"M {_points[p].X:0.###} {_points[p].Y:0.###} ");
                    p++;
                    break;
                case PathVerb.LineTo:
                    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"L {_points[p].X:0.###} {_points[p].Y:0.###} ");
                    p++;
                    break;
                case PathVerb.CubicTo:
                    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"C {_points[p].X:0.###} {_points[p].Y:0.###} {_points[p + 1].X:0.###} {_points[p + 1].Y:0.###} {_points[p + 2].X:0.###} {_points[p + 2].Y:0.###} ");
                    p += 3;
                    break;
                default:
                    sb.Append("Z ");
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    internal OneImage.Drawing.Path ToDrawingPath()
    {
        var b = new OneImage.Drawing.PathBuilder();
        foreach (Vector2 pt in _points)
        {
            if (!float.IsFinite(pt.X) || !float.IsFinite(pt.Y))
            {
                // EMF+ carries floats; a path with a NaN or infinite point has no drawable shape.
                return b.Build();
            }
        }

        int p = 0;
        bool open = false;
        foreach (PathVerb v in _verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                    b.MoveTo(_points[p++]);
                    open = true;
                    break;
                case PathVerb.LineTo:
                    if (!open)
                    {
                        b.MoveTo(_points[p]);
                        open = true;
                    }
                    else
                    {
                        b.LineTo(_points[p]);
                    }

                    p++;
                    break;
                case PathVerb.CubicTo:
                    if (!open)
                    {
                        b.MoveTo(_points[p]);
                        open = true;
                    }

                    b.CubicBezierTo(_points[p].X, _points[p].Y, _points[p + 1].X, _points[p + 1].Y, _points[p + 2].X, _points[p + 2].Y);
                    p += 3;
                    break;
                default:
                    if (open)
                    {
                        b.Close();
                    }

                    break;
            }
        }

        return b.Build();
    }
}

/// <summary>Builds a <see cref="MetafilePath"/>.</summary>
public sealed class MetafilePathBuilder
{
    private readonly List<PathVerb> _verbs = [];
    private readonly List<Vector2> _points = [];
    private readonly int _maxPoints;
    private Vector2 _start;
    private Vector2 _current;
    private bool _hasCurrent;

    /// <summary>Initializes an empty builder.</summary>
    /// <param name="maxPoints">The most points the path may hold; a longer path fails with <see cref="ImageLimitException"/>.</param>
    public MetafilePathBuilder(int maxPoints = 1 << 24) => _maxPoints = maxPoints;

    /// <summary>Gets the number of points so far.</summary>
    public int PointCount => _points.Count;

    /// <summary>Gets the current point, or the origin when nothing has been added.</summary>
    public Vector2 CurrentPoint => _current;

    /// <summary>Gets a value indicating whether a current point exists.</summary>
    public bool HasCurrentPoint => _hasCurrent;

    /// <summary>Gets the first point of the current figure.</summary>
    public Vector2 FigureStart => _start;

    private void Check(int add)
    {
        if (_points.Count + add > _maxPoints)
        {
            throw new ImageLimitException($"A path has more than {_maxPoints} points.");
        }
    }

    /// <summary>Starts a new figure.</summary>
    /// <param name="p">The start point.</param>
    /// <returns>This builder.</returns>
    public MetafilePathBuilder MoveTo(Vector2 p)
    {
        Check(1);
        _verbs.Add(PathVerb.MoveTo);
        _points.Add(p);
        _start = _current = p;
        _hasCurrent = true;
        return this;
    }

    /// <summary>Adds a line; without a current point it starts a figure.</summary>
    /// <param name="p">The end point.</param>
    /// <returns>This builder.</returns>
    public MetafilePathBuilder LineTo(Vector2 p)
    {
        if (!_hasCurrent)
        {
            return MoveTo(p);
        }

        Check(1);
        _verbs.Add(PathVerb.LineTo);
        _points.Add(p);
        _current = p;
        return this;
    }

    /// <summary>Adds a cubic Bézier curve; without a current point the first control point starts a figure.</summary>
    /// <param name="c1">The first control point.</param>
    /// <param name="c2">The second control point.</param>
    /// <param name="end">The end point.</param>
    /// <returns>This builder.</returns>
    public MetafilePathBuilder CubicTo(Vector2 c1, Vector2 c2, Vector2 end)
    {
        if (!_hasCurrent)
        {
            MoveTo(c1);
        }

        Check(3);
        _verbs.Add(PathVerb.CubicTo);
        _points.Add(c1);
        _points.Add(c2);
        _points.Add(end);
        _current = end;
        return this;
    }

    /// <summary>Closes the current figure; the current point returns to the figure's start.</summary>
    /// <returns>This builder.</returns>
    public MetafilePathBuilder Close()
    {
        if (_hasCurrent && (_verbs.Count == 0 || _verbs[^1] != PathVerb.Close))
        {
            _verbs.Add(PathVerb.Close);
            _current = _start;
        }

        return this;
    }

    /// <summary>Adds a closed rectangle.</summary>
    /// <param name="left">The left edge.</param>
    /// <param name="top">The top edge.</param>
    /// <param name="right">The right edge.</param>
    /// <param name="bottom">The bottom edge.</param>
    /// <returns>This builder.</returns>
    public MetafilePathBuilder AddRectangle(float left, float top, float right, float bottom)
    {
        MoveTo(new Vector2(left, top));
        LineTo(new Vector2(right, top));
        LineTo(new Vector2(right, bottom));
        LineTo(new Vector2(left, bottom));
        return Close();
    }

    /// <summary>Appends the steps of another path.</summary>
    /// <param name="path">The path to append.</param>
    /// <returns>This builder.</returns>
    public MetafilePathBuilder AddPath(MetafilePath path)
    {
        int p = 0;
        foreach (PathVerb v in path.Verbs)
        {
            switch (v)
            {
                case PathVerb.MoveTo:
                    MoveTo(path.Points[p++]);
                    break;
                case PathVerb.LineTo:
                    LineTo(path.Points[p++]);
                    break;
                case PathVerb.CubicTo:
                    CubicTo(path.Points[p], path.Points[p + 1], path.Points[p + 2]);
                    p += 3;
                    break;
                default:
                    Close();
                    break;
            }
        }

        return this;
    }

    /// <summary>Gets a value indicating whether nothing has been added.</summary>
    public bool IsEmpty => _verbs.Count == 0;

    /// <summary>Creates the path.</summary>
    /// <returns>An immutable path.</returns>
    public MetafilePath Build() => _verbs.Count == 0 ? MetafilePath.Empty : new MetafilePath([.. _verbs], [.. _points]);

    /// <summary>Empties the builder.</summary>
    public void Clear()
    {
        _verbs.Clear();
        _points.Clear();
        _hasCurrent = false;
        _current = _start = default;
    }
}
