using System.Globalization;
using System.Text;

namespace OneImage.Drawing;

/// <summary>
/// Describes how a line is drawn: what paints it (any <see cref="Brush"/>), how wide it is, how corners and ends look and
/// whether it is dashed. A pen is immutable; the <c>With…</c> methods return changed copies. Pass one to
/// <see cref="Image.Draw(Pen, Path, bool, float, BlendMode, AlphaComposition, bool, System.Numerics.Matrix3x2?, Path, IntersectionRule)"/>.
/// <para>
/// Defaults: width 1, <see cref="StrokeJoin.Miter"/> joins with a miter limit of 4, <see cref="StrokeCap.Butt"/> ends, solid.
/// The width is in path units (pixels, unless the draw call has a transform, which scales it). A dash pattern is a list of
/// alternating on and off lengths in the same units, starting with "on"; an odd-length pattern is repeated once so it
/// alternates (SVG <c>stroke-dasharray</c>). The <c>Dash</c>, <c>Dot</c>, <c>DashDot</c> and <c>DashDotDot</c> presets
/// scale their pattern with the width, as ImageSharp's presets do.
/// </para>
/// </summary>
public sealed class Pen
{
    /// <summary>The largest allowed width.</summary>
    public const float MaxWidth = 1 << 24;

    /// <summary>The longest allowed dash pattern.</summary>
    public const int MaxDashEntries = 1024;

    private readonly float[] _dash;

    /// <summary>Creates a pen.</summary>
    /// <param name="brush">What paints the stroke.</param>
    /// <param name="width">Line width, 0 to <see cref="MaxWidth"/>; 0 draws nothing. The default is 1.</param>
    /// <param name="join">Corner style. The default is <see cref="StrokeJoin.Miter"/>.</param>
    /// <param name="cap">End style. The default is <see cref="StrokeCap.Butt"/>.</param>
    /// <param name="miterLimit">Longest miter as a multiple of the width, at least 1 (SVG meaning: 1 / sin(half the corner angle)). The default is 4.</param>
    /// <param name="dashPattern">Alternating on and off lengths, finite and not negative, adding up to more than 0, at most <see cref="MaxDashEntries"/> entries. Empty or default means solid.</param>
    /// <param name="dashOffset">Distance into the pattern where each figure starts; any finite value, negative too. The default is 0.</param>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A number is NaN, infinite or out of range, or the pattern is not valid.</exception>
    public Pen(Brush brush, float width = 1f, StrokeJoin join = StrokeJoin.Miter, StrokeCap cap = StrokeCap.Butt, float miterLimit = 4f, ReadOnlySpan<float> dashPattern = default, float dashOffset = 0f)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (!(width >= 0f && width <= MaxWidth))
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Width must be between 0 and {MaxWidth}.");
        }

        if (join is not (StrokeJoin.Miter or StrokeJoin.Round or StrokeJoin.Bevel or StrokeJoin.Square))
        {
            throw new ArgumentOutOfRangeException(nameof(join));
        }

        if (cap is not (StrokeCap.Butt or StrokeCap.Round or StrokeCap.Square))
        {
            throw new ArgumentOutOfRangeException(nameof(cap));
        }

        if (!(miterLimit >= 1f && float.IsFinite(miterLimit)))
        {
            throw new ArgumentOutOfRangeException(nameof(miterLimit), "The miter limit must be at least 1.");
        }

        if (!float.IsFinite(dashOffset))
        {
            throw new ArgumentOutOfRangeException(nameof(dashOffset), "The dash offset must be finite.");
        }

        if (dashPattern.Length > MaxDashEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(dashPattern), $"A dash pattern has at most {MaxDashEntries} entries.");
        }

        double sum = 0;
        foreach (float d in dashPattern)
        {
            if (!(d >= 0f && float.IsFinite(d)))
            {
                throw new ArgumentOutOfRangeException(nameof(dashPattern), "Dash lengths must be finite and not negative.");
            }

            sum += d;
        }

        if (dashPattern.Length > 0 && !(sum > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(dashPattern), "A dash pattern must add up to more than 0.");
        }

        Brush = brush;
        Width = width;
        Join = join;
        Cap = cap;
        MiterLimit = miterLimit;
        _dash = dashPattern.ToArray();
        DashOffset = dashOffset;
    }

    /// <summary>Gets what paints the stroke.</summary>
    public Brush Brush { get; }

    /// <summary>Gets the line width.</summary>
    public float Width { get; }

    /// <summary>Gets the corner style.</summary>
    public StrokeJoin Join { get; }

    /// <summary>Gets the end style.</summary>
    public StrokeCap Cap { get; }

    /// <summary>Gets the longest miter as a multiple of the width.</summary>
    public float MiterLimit { get; }

    /// <summary>Gets the dash pattern (alternating on and off lengths); empty for a solid line.</summary>
    public ReadOnlyMemory<float> DashPattern => _dash;

    /// <summary>Gets the distance into the dash pattern at which each figure starts.</summary>
    public float DashOffset { get; }

    /// <summary>A solid pen with one color.</summary>
    /// <param name="color">The color, straight alpha, in the image's color space.</param>
    /// <param name="width">Line width. The default is 1.</param>
    /// <returns>The pen.</returns>
    public static Pen Solid(Rgba32 color, float width = 1f) => new(Brush.Solid(color), width);

    /// <summary>A pen dashed 3 widths on, 1 width off.</summary>
    /// <param name="color">The color.</param>
    /// <param name="width">Line width. The default is 1.</param>
    /// <returns>The pen.</returns>
    public static Pen Dash(Rgba32 color, float width = 1f) => Preset(color, width, 3, 1);

    /// <summary>A pen dashed 1 width on, 1 width off.</summary>
    /// <param name="color">The color.</param>
    /// <param name="width">Line width. The default is 1.</param>
    /// <returns>The pen.</returns>
    public static Pen Dot(Rgba32 color, float width = 1f) => Preset(color, width, 1, 1);

    /// <summary>A pen with the pattern dash, gap, dot, gap: 3, 1, 1, 1 widths.</summary>
    /// <param name="color">The color.</param>
    /// <param name="width">Line width. The default is 1.</param>
    /// <returns>The pen.</returns>
    public static Pen DashDot(Rgba32 color, float width = 1f) => Preset(color, width, 3, 1, 1, 1);

    /// <summary>A pen with the pattern dash, gap, dot, gap, dot, gap: 3, 1, 1, 1, 1, 1 widths.</summary>
    /// <param name="color">The color.</param>
    /// <param name="width">Line width. The default is 1.</param>
    /// <returns>The pen.</returns>
    public static Pen DashDotDot(Rgba32 color, float width = 1f) => Preset(color, width, 3, 1, 1, 1, 1, 1);

    /// <summary>Returns this pen with another brush.</summary>
    /// <param name="brush">The new brush.</param>
    /// <returns>The changed pen.</returns>
    public Pen WithBrush(Brush brush) => new(brush, Width, Join, Cap, MiterLimit, _dash, DashOffset);

    /// <summary>Returns this pen with another width; the dash pattern keeps its own lengths.</summary>
    /// <param name="width">The new width.</param>
    /// <returns>The changed pen.</returns>
    public Pen WithWidth(float width) => new(Brush, width, Join, Cap, MiterLimit, _dash, DashOffset);

    /// <summary>Returns this pen with another corner style.</summary>
    /// <param name="join">The new join.</param>
    /// <param name="miterLimit">The miter limit. The default is 4.</param>
    /// <returns>The changed pen.</returns>
    public Pen WithJoin(StrokeJoin join, float miterLimit = 4f) => new(Brush, Width, join, Cap, miterLimit, _dash, DashOffset);

    /// <summary>Returns this pen with another end style.</summary>
    /// <param name="cap">The new cap.</param>
    /// <returns>The changed pen.</returns>
    public Pen WithCap(StrokeCap cap) => new(Brush, Width, Join, cap, MiterLimit, _dash, DashOffset);

    /// <summary>Returns this pen with a dash pattern in absolute lengths (not scaled by the width).</summary>
    /// <param name="pattern">Alternating on and off lengths; empty makes the pen solid.</param>
    /// <param name="offset">Distance into the pattern where each figure starts. The default is 0.</param>
    /// <returns>The changed pen.</returns>
    public Pen WithDash(ReadOnlySpan<float> pattern, float offset = 0f) => new(Brush, Width, Join, Cap, MiterLimit, pattern, offset);

    private static Pen Preset(Rgba32 color, float width, params ReadOnlySpan<float> unitPattern)
    {
        var p = new float[unitPattern.Length];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = unitPattern[i] * width;
        }

        return new Pen(Brush.Solid(color), width, dashPattern: p);
    }

    /// <summary>The pen's parameters for the op descriptor (the brush is described separately).</summary>
    internal OpParameter[] Describe()
    {
        var dash = new StringBuilder();
        foreach (float d in _dash)
        {
            if (dash.Length > 0)
            {
                dash.Append(',');
            }

            dash.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        return
        [
            new("width", (double)Width),
            new("join", Join switch { StrokeJoin.Round => "round", StrokeJoin.Bevel => "bevel", StrokeJoin.Square => "square", _ => "miter" }),
            new("cap", Cap switch { StrokeCap.Round => "round", StrokeCap.Square => "square", _ => "butt" }),
            new("miter-limit", (double)MiterLimit),
            new("dash", dash.ToString()),
            new("dash-offset", (double)DashOffset),
        ];
    }
}
