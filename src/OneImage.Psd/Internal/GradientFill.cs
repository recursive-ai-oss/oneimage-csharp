using System.Drawing;
using System.Numerics;
using OneImage.Drawing;

namespace OneImage.Psd;

/// <summary>The gradient styles of a gradient fill layer (<c>Type</c> in the layer's descriptor).</summary>
internal enum GradientStyle
{
    Linear,
    Radial,
    Angle,
    Reflected,
    Diamond,
}

/// <summary>A color stop of a Photoshop gradient: where it sits (0..1), the midpoint toward the next stop (0..1) and its color (straight, 0..1, in the document's space).</summary>
internal readonly record struct PsdColorStop(float Location, float Midpoint, float R, float G, float B);

/// <summary>An opacity stop of a Photoshop gradient (location 0..1, midpoint 0..1, opacity 0..1).</summary>
internal readonly record struct PsdOpacityStop(float Location, float Midpoint, float Opacity);

/// <summary>The settings of a gradient fill layer that the renderer reproduces.</summary>
internal sealed class GradientFillSpec
{
    public GradientStyle Style { get; init; }

    /// <summary>The angle in degrees, counterclockwise from the +x direction as Photoshop shows it.</summary>
    public double Angle { get; init; }

    public bool Reverse { get; init; }

    /// <summary>Align with layer: the gradient is laid out on the layer's bounds; otherwise on the whole document.</summary>
    public bool Align { get; init; } = true;

    /// <summary>The scale as a fraction (1 = 100%).</summary>
    public double Scale { get; init; } = 1;

    /// <summary>The offset of the gradient's center as fractions of the box width and height.</summary>
    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    /// <summary>Whether colors blend in linear light (the file's "linear" interpolation) instead of in the encoded values (classic).</summary>
    public bool LinearLight { get; init; }

    /// <summary>The gradient's smoothness (0..1): how far the blend between two stops follows an ease curve instead of a straight line.</summary>
    public double Smoothness { get; init; } = 1;

    public required PsdColorStop[] Colors { get; init; }

    public required PsdOpacityStop[] Opacities { get; init; }
}

/// <summary>
/// Paints a Photoshop gradient on a box: the layer's bounds, or the document when the fill is not aligned with the layer. The position
/// along the gradient is computed from the pixel center; colors come from a table of the whole gradient (stops with their midpoints,
/// opacity stops merged in), sampled with linear interpolation. Photoshop's dither is not reproduced: it adds noise of about one level.
/// </summary>
internal sealed class PsdGradientBrush : Brush
{
    /// <summary>Gradients on boxes with a side shorter than this many pixels keep their stored pixels.</summary>
    public const int MinimumSide = 16;

    private const int TableSize = 4096;
    private readonly GradientFillSpec _spec;
    private readonly float _cx, _cy;      // center of the gradient
    private readonly float _dx, _dy;      // unit vector of the gradient direction (screen coordinates, y down)
    private readonly float _invLength;    // 1 / full length along the direction (linear, reflected)
    private readonly float _invRadius;    // 1 / radius (radial, reflected, diamond)
    private readonly float _angle;        // the gradient angle in radians (angle style)
    private readonly float[] _table;      // (TableSize + 1) straight RGBA entries

    public PsdGradientBrush(GradientFillSpec spec, RectangleF box)
    {
        _spec = spec;
        double w = Math.Max(box.Width, 1e-3);
        double h = Math.Max(box.Height, 1e-3);
        double a = spec.Angle * Math.PI / 180.0;
        double cos = Math.Cos(a);
        double sin = Math.Sin(a);
        _dx = (float)cos;
        _dy = (float)-sin;
        _cx = (float)(box.X + (w / 2) + (spec.OffsetX * w));
        _cy = (float)(box.Y + (h / 2) + (spec.OffsetY * h));
        // The gradient runs along a line through the center of the box, from one edge to the other; Scl stretches that line. For Radial,
        // Reflected and Diamond the radius is half of it.
        double scale = Math.Max(spec.Scale, 1e-6);
        double chord = Math.Min(Math.Abs(cos) > 1e-9 ? w / Math.Abs(cos) : double.MaxValue, Math.Abs(sin) > 1e-9 ? h / Math.Abs(sin) : double.MaxValue);
        double length = scale * chord;
        _invLength = (float)(1.0 / Math.Max(length, 1e-6));
        _invRadius = (float)(2.0 / Math.Max(length, 1e-6));
        _angle = (float)a;
        _table = BuildTable(spec);
    }

    public GradientFillSpec Spec => _spec;

    public override IReadOnlyList<OpParameter> Describe() =>
    [
        new("brush", "psd-gradient"),
        new("style", _spec.Style.ToString().ToLowerInvariant()),
        new("angle", _spec.Angle),
        new("reverse", _spec.Reverse),
        new("scale", _spec.Scale),
        new("align", _spec.Align),
        new("center-x", (double)_cx),
        new("center-y", (double)_cy),
        new("stops", _spec.Colors.Length),
    ];

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        int n = rgba.Length / 4;
        // Photoshop evaluates the position at the pixel's own integer coordinates, not at its center.
        float py = y - _cy;
        GradientStyle style = _spec.Style;
        bool reverse = _spec.Reverse;
        for (int i = 0; i < n; i++)
        {
            float px = x + i - _cx;
            float u = (px * _dx) + (py * _dy);
            float t;
            switch (style)
            {
                case GradientStyle.Linear:
                    t = 0.5f + (u * _invLength);
                    break;
                case GradientStyle.Reflected:
                    t = MathF.Abs(u) * _invRadius;
                    break;
                case GradientStyle.Radial:
                    t = MathF.Sqrt((px * px) + (py * py)) * _invRadius;
                    break;
                case GradientStyle.Diamond:
                    {
                        float v = (-px * _dy) + (py * _dx);
                        t = (MathF.Abs(u) + MathF.Abs(v)) * _invRadius;
                        break;
                    }

                default:
                    {
                        // Angle: a sweep around the center that starts at the gradient's direction and runs clockwise.
                        float turn = (_angle - MathF.Atan2(-py, px)) / (2f * MathF.PI);
                        t = turn - MathF.Floor(turn);
                        break;
                    }
            }

            if (reverse)
            {
                t = 1f - t;
            }

            Lookup(t, rgba.Slice(i * 4, 4));
        }
    }

    private void Lookup(float t, Span<float> o)
    {
        if (!(t > 0f))
        {
            t = 0f;
        }
        else if (t > 1f)
        {
            t = 1f;
        }

        float f = t * TableSize;
        int i = (int)f;
        if (i >= TableSize)
        {
            i = TableSize - 1;
        }

        float k = f - i;
        int a = i * 4;
        int b = a + 4;
        float[] tb = _table;
        o[0] = tb[a] + ((tb[b] - tb[a]) * k);
        o[1] = tb[a + 1] + ((tb[b + 1] - tb[a + 1]) * k);
        o[2] = tb[a + 2] + ((tb[b + 2] - tb[a + 2]) * k);
        o[3] = tb[a + 3] + ((tb[b + 3] - tb[a + 3]) * k);
    }

    private static float[] BuildTable(GradientFillSpec spec)
    {
        float[] table = new float[(TableSize + 1) * 4];
        PsdColorStop[] cs = spec.Colors;
        PsdOpacityStop[] os = spec.Opacities;
        for (int i = 0; i <= TableSize; i++)
        {
            float t = (float)i / TableSize;
            (float r, float g, float b) = ColorAt(cs, t, spec.LinearLight, (float)spec.Smoothness);
            float a = OpacityAt(os, t, (float)spec.Smoothness);
            table[i * 4] = r;
            table[(i * 4) + 1] = g;
            table[(i * 4) + 2] = b;
            table[(i * 4) + 3] = a;
        }

        return table;
    }

    // Finds the stop pair around t and the position inside it, with the midpoint bending the blend: the halfway color sits at the
    // midpoint instead of the middle of the span.
    private static float Bend(float s, float mid)
    {
        mid = Math.Clamp(mid, 1e-4f, 1f - 1e-4f);
        return s <= mid ? 0.5f * s / mid : 0.5f + (0.5f * (s - mid) / (1f - mid));
    }

    private static (float R, float G, float B) ColorAt(PsdColorStop[] s, float t, bool linear, float smooth)
    {
        if (t <= s[0].Location)
        {
            return (s[0].R, s[0].G, s[0].B);
        }

        for (int i = 1; i < s.Length; i++)
        {
            if (t <= s[i].Location)
            {
                PsdColorStop p = s[i - 1];
                PsdColorStop q = s[i];
                float span = q.Location - p.Location;
                float k = span > 0f ? Ease(Bend((t - p.Location) / span, MidpointOf(s, i - 1, i)), smooth) : 1f;
                if (linear)
                {
                    return (
                        Encode(Decode(p.R) + ((Decode(q.R) - Decode(p.R)) * k)),
                        Encode(Decode(p.G) + ((Decode(q.G) - Decode(p.G)) * k)),
                        Encode(Decode(p.B) + ((Decode(q.B) - Decode(p.B)) * k)));
                }

                return (p.R + ((q.R - p.R) * k), p.G + ((q.G - p.G) * k), p.B + ((q.B - p.B) * k));
            }
        }

        PsdColorStop last = s[^1];
        return (last.R, last.G, last.B);
    }

    private static float OpacityAt(PsdOpacityStop[] s, float t, float smooth)
    {
        if (t <= s[0].Location)
        {
            return s[0].Opacity;
        }

        for (int i = 1; i < s.Length; i++)
        {
            if (t <= s[i].Location)
            {
                PsdOpacityStop p = s[i - 1];
                PsdOpacityStop q = s[i];
                float span = q.Location - p.Location;
                float k = span > 0f ? Ease(Bend((t - p.Location) / span, q.Midpoint), smooth) : 1f;
                return p.Opacity + ((q.Opacity - p.Opacity) * k);
            }
        }

        return s[^1].Opacity;
    }

    // Between two stops the blend is the straight line mixed with a smoothstep (zero slope at the stops). Full smoothness (4096 in the file) is
    // an even mix of the two, and smaller values scale the smoothstep's share down in proportion.
    private static float Ease(float s, float smooth) => s + (0.5f * smooth * ((s * s * (3f - (2f * s))) - s));

    private static float MidpointOf(PsdColorStop[] s, int from, int to) => s[to].Midpoint;

    private static float Decode(float v) => v <= 0.04045f ? v / 12.92f : DeterministicMath.PowF((v + 0.055f) / 1.055f, 2.4f);

    private static float Encode(float v) => v <= 0.0031308f ? v * 12.92f : (1.055f * DeterministicMath.PowF(v, 1f / 2.4f)) - 0.055f;
}
