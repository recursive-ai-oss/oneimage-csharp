using System.Globalization;
using System.Numerics;
using System.Text;

namespace OneImage.Drawing;

/// <summary>
/// Shared part of the gradient brushes: the sorted stops, the repetition rule and turning a row of positions into colors. A subclass
/// only says how a pixel maps to a position along the gradient.
/// </summary>
internal abstract class GradientBrush : Brush
{
    private const int MaxStops = 1 << 16;
    private readonly ColorStop[] _stops;      // sorted, as given (for the descriptor)
    private readonly Vector4[] _straight;     // stop colors, straight, 0..1 (after any linear-light mapping)
    private readonly float[] _pos;
    private readonly float[] _inv;            // 1 / (pos[i] - pos[i-1]) where that is positive
    private readonly float[] _col;            // 4 floats per stop, in the interpolation space
    private readonly bool _requestedPremultiplied;
    private readonly bool _premultiply;

    protected GradientBrush(ReadOnlySpan<ColorStop> stops, GradientRepetition repetition, bool premultiplied)
    {
        if (stops.Length == 0)
        {
            throw new ArgumentException("A gradient needs at least one color stop.", nameof(stops));
        }

        if (stops.Length > MaxStops)
        {
            throw new ArgumentException($"A gradient takes at most {MaxStops} color stops.", nameof(stops));
        }

        if (repetition is < GradientRepetition.None or > GradientRepetition.DontFill)
        {
            throw new ArgumentOutOfRangeException(nameof(repetition));
        }

        float[] keys = new float[stops.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = stops[i].Position;
            if (!float.IsFinite(keys[i]))
            {
                throw new ArgumentException("Color stop positions must be finite.", nameof(stops));
            }
        }

        // Stable by position, so two stops at one position keep their order and make a hard edge.
        int[] order = new int[stops.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : a.CompareTo(b));
        _stops = new ColorStop[stops.Length];
        _straight = new Vector4[stops.Length];
        for (int i = 0; i < order.Length; i++)
        {
            _stops[i] = stops[order[i]];
            Rgba32 c = _stops[i].Color;
            _straight[i] = new Vector4(c.R, c.G, c.B, c.A) * (1f / 255f);
        }

        Repetition = repetition;
        _requestedPremultiplied = premultiplied;
        (_pos, _inv, _col, _premultiply) = Prepare(_stops, _straight, premultiplied);
    }

    /// <summary>A copy of <paramref name="source"/> whose stops have the given colors (for linear light).</summary>
    protected GradientBrush(GradientBrush source, Vector4[] colors)
    {
        _stops = source._stops;
        _straight = colors;
        Repetition = source.Repetition;
        _requestedPremultiplied = source._requestedPremultiplied;
        (_pos, _inv, _col, _premultiply) = Prepare(_stops, colors, _requestedPremultiplied);
    }

    protected GradientRepetition Repetition { get; }

    private static (float[] Pos, float[] Inv, float[] Col, bool Premultiply) Prepare(ColorStop[] stops, Vector4[] straight, bool premultiplied)
    {
        bool anyTranslucent = false;
        foreach (Vector4 c in straight)
        {
            anyTranslucent |= c.W < 1f;
        }

        bool premultiply = premultiplied && anyTranslucent;
        var pos = new float[stops.Length];
        var inv = new float[stops.Length];
        var col = new float[stops.Length * 4];
        for (int i = 0; i < stops.Length; i++)
        {
            pos[i] = stops[i].Position;
            if (i > 0 && pos[i] > pos[i - 1])
            {
                inv[i] = 1f / (pos[i] - pos[i - 1]);
            }

            Vector4 c = straight[i];
            float k = premultiply ? c.W : 1f;
            col[i * 4] = c.X * k;
            col[(i * 4) + 1] = c.Y * k;
            col[(i * 4) + 2] = c.Z * k;
            col[(i * 4) + 3] = c.W;
        }

        return (pos, inv, col, premultiply);
    }

    /// <summary>Writes the gradient position of the pixel centers (<paramref name="x"/>+i+0.5, <paramref name="y"/>+0.5), one float per pixel.</summary>
    protected abstract void Positions(int x, int y, Span<float> t);

    /// <summary>The same brush with other stop colors.</summary>
    protected abstract GradientBrush WithColors(Vector4[] colors);

    internal override Brush Linearized(ColorMap map)
    {
        var mapped = new Vector4[_straight.Length];
        for (int i = 0; i < mapped.Length; i++)
        {
            mapped[i] = map.Map(_straight[i]);
        }

        return WithColors(mapped);
    }

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        int n = rgba.Length / 4;
        Span<float> t = rgba[..n];
        Positions(x, y, t);
        switch (Repetition)
        {
            case GradientRepetition.Repeat:
                for (int i = 0; i < t.Length; i++)
                {
                    t[i] -= MathF.Floor(t[i]);
                }

                break;
            case GradientRepetition.Reflect:
                for (int i = 0; i < t.Length; i++)
                {
                    float u = t[i] - (2f * MathF.Floor(t[i] * 0.5f));
                    t[i] = u > 1f ? 2f - u : u;
                }

                break;
            case GradientRepetition.DontFill:
                for (int i = 0; i < t.Length; i++)
                {
                    if (!(t[i] >= 0f && t[i] <= 1f))
                    {
                        t[i] = float.NaN;
                    }
                }

                break;
        }

        Colorize(rgba, n);
    }

    // Positions sit in the first n floats of the row; colors are written from the last pixel backwards, which never overwrites a
    // position that is still to be read (pixel i's position is at index i, its color goes to 4i..4i+3, and i <= 4i).
    private void Colorize(Span<float> rgba, int n)
    {
        float[] pos = _pos, col = _col, inv = _inv;
        int last = pos.Length - 1;
        bool pre = _premultiply;
        for (int i = n - 1; i >= 0; i--)
        {
            float t = rgba[i];
            float r, g, b, a;
            if (float.IsNaN(t))
            {
                r = g = b = a = 0f;
            }
            else if (t <= pos[0])
            {
                r = col[0];
                g = col[1];
                b = col[2];
                a = col[3];
            }
            else if (t >= pos[last])
            {
                int o = last * 4;
                r = col[o];
                g = col[o + 1];
                b = col[o + 2];
                a = col[o + 3];
            }
            else
            {
                int s = 1;
                if (last <= 8)
                {
                    while (pos[s] <= t)
                    {
                        s++;
                    }
                }
                else
                {
                    int lo = 1, hi = last; // the first index with pos > t lies in [lo, hi]
                    while (lo < hi)
                    {
                        int mid = (lo + hi) >> 1;
                        if (pos[mid] <= t)
                        {
                            lo = mid + 1;
                        }
                        else
                        {
                            hi = mid;
                        }
                    }

                    s = lo;
                }

                float f = (t - pos[s - 1]) * inv[s];
                int o1 = (s - 1) * 4, o2 = s * 4;
                r = col[o1] + ((col[o2] - col[o1]) * f);
                g = col[o1 + 1] + ((col[o2 + 1] - col[o1 + 1]) * f);
                b = col[o1 + 2] + ((col[o2 + 2] - col[o1 + 2]) * f);
                a = col[o1 + 3] + ((col[o2 + 3] - col[o1 + 3]) * f);
            }

            if (pre)
            {
                if (a > 0f)
                {
                    float k = 1f / a;
                    r = MathF.Min(r * k, 1f);
                    g = MathF.Min(g * k, 1f);
                    b = MathF.Min(b * k, 1f);
                }
                else
                {
                    r = g = b = 0f;
                }
            }

            int p = i * 4;
            rgba[p] = r;
            rgba[p + 1] = g;
            rgba[p + 2] = b;
            rgba[p + 3] = a;
        }
    }

    protected List<OpParameter> StopParameters(string kind)
    {
        var sb = new StringBuilder();
        foreach (ColorStop s in _stops)
        {
            if (sb.Length > 0)
            {
                sb.Append(';');
            }

            sb.Append(s.Position.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(Hex(s.Color));
        }

        string rep = Repetition switch
        {
            GradientRepetition.Repeat => "repeat",
            GradientRepetition.Reflect => "reflect",
            GradientRepetition.DontFill => "dont-fill",
            _ => "none",
        };
        return [new("brush", kind), new("stops", sb.ToString()), new("repetition", rep), new("premultiplied", _requestedPremultiplied)];
    }

    protected static void CheckFinite(string name, params ReadOnlySpan<float> values)
    {
        foreach (float v in values)
        {
            if (!float.IsFinite(v))
            {
                throw new ArgumentException("Gradient geometry must be finite.", name);
            }
        }
    }

    protected static OpParameter P(string name, float v) => new(name, (double)v);
}

/// <summary>Position = projection of the pixel center on the line from start to end.</summary>
internal sealed class LinearGradientBrush : GradientBrush
{
    private readonly Vector2 _start, _end;
    private readonly float _ax, _ay;

    public LinearGradientBrush(Vector2 start, Vector2 end, ReadOnlySpan<ColorStop> stops, GradientRepetition repetition, bool premultiplied)
        : base(stops, repetition, premultiplied)
    {
        CheckFinite(nameof(start), start.X, start.Y, end.X, end.Y);
        double dx = (double)end.X - start.X, dy = (double)end.Y - start.Y;
        double len2 = (dx * dx) + (dy * dy);
        if (!(len2 >= 1e-12))
        {
            throw new ArgumentException("The start and end of a linear gradient must differ.", nameof(end));
        }

        _start = start;
        _end = end;
        _ax = (float)(dx / len2);
        _ay = (float)(dy / len2);
    }

    private LinearGradientBrush(LinearGradientBrush source, Vector4[] colors)
        : base(source, colors)
    {
        _start = source._start;
        _end = source._end;
        _ax = source._ax;
        _ay = source._ay;
    }

    protected override GradientBrush WithColors(Vector4[] colors) => new LinearGradientBrush(this, colors);

    protected override void Positions(int x, int y, Span<float> t)
    {
        // t(px) = (px + 0.5 - sx) * ax + (py + 0.5 - sy) * ay; the part that does not depend on the column is added once.
        float c = ((0.5f - _start.X) * _ax) + (((y + 0.5f) - _start.Y) * _ay);
        float ax = _ax;
        float fx = x;
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = ((fx + i) * ax) + c;
        }
    }

    public override IReadOnlyList<OpParameter> Describe() =>
        [.. StopParameters("linear-gradient"), P("x1", _start.X), P("y1", _start.Y), P("x2", _end.X), P("y2", _end.Y)];
}

/// <summary>Position = distance from the center divided by the radius.</summary>
internal sealed class RadialGradientBrush : GradientBrush
{
    private readonly Vector2 _center;
    private readonly float _radius;
    private readonly float _invRadius;

    public RadialGradientBrush(Vector2 center, float radius, ReadOnlySpan<ColorStop> stops, GradientRepetition repetition, bool premultiplied)
        : base(stops, repetition, premultiplied)
    {
        CheckFinite(nameof(center), center.X, center.Y);
        if (!(radius > 0f) || !float.IsFinite(radius))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "The radius must be positive and finite.");
        }

        _center = center;
        _radius = radius;
        _invRadius = 1f / radius;
    }

    private RadialGradientBrush(RadialGradientBrush source, Vector4[] colors)
        : base(source, colors)
    {
        _center = source._center;
        _radius = source._radius;
        _invRadius = source._invRadius;
    }

    protected override GradientBrush WithColors(Vector4[] colors) => new RadialGradientBrush(this, colors);

    protected override void Positions(int x, int y, Span<float> t)
    {
        float dy = (y + 0.5f) - _center.Y;
        float dy2 = dy * dy;
        float bx = (x + 0.5f) - _center.X;
        float inv = _invRadius;
        for (int i = 0; i < t.Length; i++)
        {
            float dx = bx + i;
            t[i] = MathF.Sqrt((dx * dx) + dy2) * inv;
        }
    }

    public override IReadOnlyList<OpParameter> Describe() =>
        [.. StopParameters("radial-gradient"), P("cx", _center.X), P("cy", _center.Y), P("radius", _radius)];
}

/// <summary>Position = normalized distance in the frame of an ellipse (optionally squared).</summary>
internal sealed class EllipticGradientBrush : GradientBrush
{
    private readonly Vector2 _center, _axisEnd;
    private readonly float _ratio;
    private readonly bool _squared;
    private readonly float _cos, _sin, _invA, _invB;

    public EllipticGradientBrush(Vector2 center, Vector2 axisEnd, float axisRatio, ReadOnlySpan<ColorStop> stops, GradientRepetition repetition, bool premultiplied, bool squared)
        : base(stops, repetition, premultiplied)
    {
        CheckFinite(nameof(center), center.X, center.Y, axisEnd.X, axisEnd.Y);
        if (!(axisRatio > 0f) || !float.IsFinite(axisRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(axisRatio), "The axis ratio must be positive and finite.");
        }

        Vector2 d = axisEnd - center;
        float major = MathF.Sqrt((d.X * d.X) + (d.Y * d.Y));
        if (!float.IsFinite(major) || major < 1e-6f || !float.IsFinite(major * axisRatio) || major * axisRatio < 1e-6f)
        {
            throw new ArgumentException("The axis of an elliptic gradient must have a length.", nameof(axisEnd));
        }

        _center = center;
        _axisEnd = axisEnd;
        _ratio = axisRatio;
        _squared = squared;
        _cos = d.X / major;
        _sin = d.Y / major;
        _invA = 1f / major;
        _invB = 1f / (major * axisRatio);
    }

    private EllipticGradientBrush(EllipticGradientBrush source, Vector4[] colors)
        : base(source, colors)
    {
        _center = source._center;
        _axisEnd = source._axisEnd;
        _ratio = source._ratio;
        _squared = source._squared;
        _cos = source._cos;
        _sin = source._sin;
        _invA = source._invA;
        _invB = source._invB;
    }

    protected override GradientBrush WithColors(Vector4[] colors) => new EllipticGradientBrush(this, colors);

    protected override void Positions(int x, int y, Span<float> t)
    {
        float dy = (y + 0.5f) - _center.Y;
        float bx = (x + 0.5f) - _center.X;
        float cs = _cos, sn = _sin, ia = _invA, ib = _invB;
        bool squared = _squared;
        for (int i = 0; i < t.Length; i++)
        {
            float dx = bx + i;
            float u = ((dx * cs) + (dy * sn)) * ia;
            float v = ((dy * cs) - (dx * sn)) * ib;
            float q = (u * u) + (v * v);
            t[i] = squared ? q : MathF.Sqrt(q);
        }
    }

    public override IReadOnlyList<OpParameter> Describe() =>
        [.. StopParameters("elliptic-gradient"), P("cx", _center.X), P("cy", _center.Y), P("ax", _axisEnd.X), P("ay", _axisEnd.Y), P("ratio", _ratio), new OpParameter("squared", _squared)];
}
