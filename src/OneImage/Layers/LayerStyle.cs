using System.Globalization;

namespace OneImage.Layers;

/// <summary>
/// A tone curve for a layer effect: it remaps the effect's edge falloff (0 = outside the falloff, 1 = at the shape) the way Photoshop's
/// "contour" does. The curve is the piecewise-linear line through its points, which are sorted by <see cref="ContourPoint.X"/>; the
/// first point should start at x = 0 and the last end at x = 1, and values between them are interpolated linearly. A contour is immutable.
/// </summary>
public sealed class EffectContour
{
    /// <summary>The identity curve: the falloff is used as it is. It is also what a null contour on an effect means.</summary>
    public static EffectContour Linear { get; } = new([new ContourPoint(0, 0), new ContourPoint(1, 1)]);

    /// <summary>Creates a contour from at least two points with strictly increasing x in [0, 1] and y in [0, 1].</summary>
    /// <param name="points">The control points.</param>
    /// <exception cref="ArgumentException">There are fewer than two points, more than 256, or they are out of range or not increasing.</exception>
    public EffectContour(IEnumerable<ContourPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        ContourPoint[] list = [.. points];
        if (list.Length < 2 || list.Length > 256)
        {
            throw new ArgumentException("A contour needs between 2 and 256 points.", nameof(points));
        }

        for (int i = 0; i < list.Length; i++)
        {
            ContourPoint p = list[i];
            if (!(p.X >= 0 && p.X <= 1 && p.Y >= 0 && p.Y <= 1) || (i > 0 && !(p.X > list[i - 1].X)))
            {
                throw new ArgumentException("Contour points need x in [0, 1], strictly increasing, and y in [0, 1].", nameof(points));
            }
        }

        Points = list;
    }

    /// <summary>Gets the control points.</summary>
    public IReadOnlyList<ContourPoint> Points { get; }

    /// <summary>True when the curve is the identity (two points on the diagonal).</summary>
    public bool IsLinear => Points.Count == 2 && Points[0] == new ContourPoint(0, 0) && Points[1] == new ContourPoint(1, 1);

    /// <summary>Evaluates the curve at <paramref name="x"/>; values outside the first and last point take that point's y.</summary>
    /// <param name="x">The input, usually in [0, 1].</param>
    /// <returns>The remapped value.</returns>
    public float Evaluate(float x)
    {
        IReadOnlyList<ContourPoint> p = Points;
        if (x <= p[0].X)
        {
            return p[0].Y;
        }

        for (int i = 1; i < p.Count; i++)
        {
            if (x <= p[i].X)
            {
                float t = (x - p[i - 1].X) / (p[i].X - p[i - 1].X);
                return p[i - 1].Y + (t * (p[i].Y - p[i - 1].Y));
            }
        }

        return p[^1].Y;
    }

    internal string Label() => string.Join(';', Points.Select(static q => Layer.Num(q.X) + "," + Layer.Num(q.Y)));
}

/// <summary>A control point of an <see cref="EffectContour"/>.</summary>
/// <param name="X">The input, 0 to 1.</param>
/// <param name="Y">The output, 0 to 1.</param>
public readonly record struct ContourPoint(float X, float Y);

/// <summary>Where a <see cref="StrokeEffect"/> sits relative to the edge of the layer's shape.</summary>
public enum StrokePosition
{
    /// <summary>Outside the shape: the stroke starts at the edge and grows away from the shape.</summary>
    Outside = 0,

    /// <summary>Inside the shape: the stroke starts at the edge and grows toward the middle.</summary>
    Inside = 1,

    /// <summary>Centered on the edge: half of the size on each side.</summary>
    Center = 2,
}

/// <summary>Where an <see cref="InnerGlowEffect"/> starts.</summary>
public enum GlowSource
{
    /// <summary>The glow starts at the edge of the shape and fades toward the middle.</summary>
    Edge = 0,

    /// <summary>The glow starts in the middle and fades toward the edge.</summary>
    Center = 1,
}

/// <summary>
/// One effect of a <see cref="LayerStyle"/>: a drop shadow, glow, stroke, overlay or satin that is drawn from the layer's own shape
/// (its alpha, after its mask) when the document is composited, not stored in the layer's pixels. Effects are immutable. Every effect has a
/// <see cref="Blend"/> mode that blends it with whatever is below the layer, an <see cref="Opacity"/>, and a <see cref="Color"/>.
/// <para>
/// Sizes and distances are in document pixels. Angles are in degrees, counter-clockwise from the right, and name the direction the
/// light comes from (a drop shadow falls the other way). Strength values such as spread and choke are fractions from 0 to 1.
/// </para>
/// </summary>
public abstract class LayerEffect
{
    /// <summary>The largest size, in pixels, an effect accepts.</summary>
    public const float MaxSize = 250;

    /// <summary>The largest distance, in pixels, an effect accepts.</summary>
    public const float MaxDistance = 30000;

    private float _opacity = 1f;

    internal LayerEffect()
    {
    }

    /// <summary>Gets the language-neutral name of the effect kind, for example <c>drop-shadow</c>.</summary>
    public abstract string Name { get; }

    /// <summary>Gets a value indicating whether the effect is drawn. Disabled effects are kept in the style but skipped.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the blend mode used against the backdrop. The default depends on the effect (Multiply for shadows, Screen for glows, Normal otherwise).</summary>
    public BlendMode Blend { get; init; }

    /// <summary>Gets the effect's opacity from 0 to 1. The default is 1 for strokes and overlays, 0.75 for shadows and glows, and 0.5 for satin.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0..1.</exception>
    public float Opacity
    {
        get => _opacity;
        init => _opacity = value >= 0f && value <= 1f ? value : throw new ArgumentOutOfRangeException(nameof(value), "Opacity must be between 0 and 1.");
    }

    /// <summary>Gets the effect's color, in the document's color space. Its alpha is ignored; use <see cref="Opacity"/>.</summary>
    public Rgba32 Color { get; init; } = new(0, 0, 0, 255);

    /// <summary>Gets how far, in pixels, the effect can reach beyond the layer's bounds (rounded up, including edge antialiasing).</summary>
    internal abstract int Reach { get; }

    /// <summary>How far past the layer's edge its shape matters to pixels inside it (an inside stroke or an inner shadow looks at the edge up to this far away).</summary>
    internal virtual int Footprint => Reach;

    /// <summary>True when the effect is drawn below the layer's own pixels.</summary>
    internal virtual bool BelowLayer => false;

    /// <summary>The drawing order among effects drawn on the same side of the layer: lower first.</summary>
    internal abstract int Order { get; }

    internal virtual void DescribeKind(List<OpParameter> parameters, string prefix)
    {
    }

    internal void Describe(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "kind", Name));
        p.Add(new(prefix + "enabled", Enabled));
        p.Add(new(prefix + "blend", BlendNames.Name(Blend) ?? "normal"));
        p.Add(new(prefix + "opacity", (double)Opacity));
        p.Add(new(prefix + "color", "#" + Color.R.ToString("x2", CultureInfo.InvariantCulture) + Color.G.ToString("x2", CultureInfo.InvariantCulture) + Color.B.ToString("x2", CultureInfo.InvariantCulture)));
        DescribeKind(p, prefix);
    }

    internal static float CheckRange(float value, float max, string name) =>
        value >= 0f && value <= max ? value : throw new ArgumentOutOfRangeException(name, $"{name} must be between 0 and {max.ToString(CultureInfo.InvariantCulture)}.");

    internal static float CheckUnit(float value, string name) => CheckRange(value, 1f, name);

    internal static float CheckAngle(float value, string name) =>
        float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, $"{name} must be a finite number.");

    internal static void DescribeContour(List<OpParameter> p, string prefix, EffectContour? contour)
    {
        if (contour != null && !contour.IsLinear)
        {
            p.Add(new(prefix + "contour", contour.Label()));
        }
    }
}

/// <summary>
/// A shadow cast by the layer onto what is below it: the layer's shape, moved away from the light by <see cref="Distance"/>, grown by
/// <see cref="Spread"/>, blurred by <see cref="Size"/> and filled with <see cref="LayerEffect.Color"/>. Defaults: Multiply, 75 %, black,
/// light from 120°, distance 5, spread 0, size 5.
/// </summary>
public sealed class DropShadowEffect : LayerEffect
{
    private readonly float _distance = 5;
    private readonly float _spread;
    private readonly float _size = 5;
    private readonly float _angle = 120;

    /// <summary>Creates a drop shadow with the defaults.</summary>
    public DropShadowEffect()
    {
        Blend = BlendMode.Multiply;
        Opacity = 0.75f;
    }

    /// <inheritdoc/>
    public override string Name => "drop-shadow";

    /// <summary>Gets the direction the light comes from, in degrees counter-clockwise from the right. The shadow falls the opposite way.</summary>
    public float Angle { get => _angle; init => _angle = CheckAngle(value, nameof(Angle)); }

    /// <summary>Gets how far the shadow is moved from the shape, in pixels (0 to 30000).</summary>
    public float Distance { get => _distance; init => _distance = CheckRange(value, MaxDistance, nameof(Distance)); }

    /// <summary>Gets how much the shape grows before it is blurred, from 0 to 1 (a fraction of <see cref="Size"/>).</summary>
    public float Spread { get => _spread; init => _spread = CheckUnit(value, nameof(Spread)); }

    /// <summary>Gets the blur size in pixels (0 to 250).</summary>
    public float Size { get => _size; init => _size = CheckRange(value, MaxSize, nameof(Size)); }

    /// <summary>Gets the falloff curve, or null for linear.</summary>
    public EffectContour? Contour { get; init; }

    /// <summary>
    /// Gets a value indicating whether the shadow is hidden where the layer's shape is (so a layer with a lowered fill opacity shows no shadow through
    /// itself). The default is true, as in Photoshop.
    /// </summary>
    public bool KnockedOut { get; init; } = true;

    /// <inheritdoc/>
    internal override int Reach => (int)Math.Ceiling(_distance + _size) + 2;

    /// <inheritdoc/>
    internal override bool BelowLayer => true;

    /// <inheritdoc/>
    internal override int Order => 0;

    internal override void DescribeKind(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "angle", (double)_angle));
        p.Add(new(prefix + "distance", (double)_distance));
        p.Add(new(prefix + "spread", (double)_spread));
        p.Add(new(prefix + "size", (double)_size));
        p.Add(new(prefix + "knocked-out", KnockedOut));
        DescribeContour(p, prefix, Contour);
    }
}

/// <summary>
/// A shadow cast inside the layer's shape by its own edge: the area outside the shape, moved by <see cref="Distance"/>, shrunk by
/// <see cref="Choke"/> and blurred by <see cref="Size"/>, kept only inside the shape. Defaults: Multiply, 75 %, black, light from 120°,
/// distance 5, choke 0, size 5.
/// </summary>
public sealed class InnerShadowEffect : LayerEffect
{
    private readonly float _distance = 5;
    private readonly float _choke;
    private readonly float _size = 5;
    private readonly float _angle = 120;

    /// <summary>Creates an inner shadow with the defaults.</summary>
    public InnerShadowEffect()
    {
        Blend = BlendMode.Multiply;
        Opacity = 0.75f;
    }

    /// <inheritdoc/>
    public override string Name => "inner-shadow";

    /// <summary>Gets the direction the light comes from, in degrees counter-clockwise from the right.</summary>
    public float Angle { get => _angle; init => _angle = CheckAngle(value, nameof(Angle)); }

    /// <summary>Gets how far the shadow is moved, in pixels (0 to 30000).</summary>
    public float Distance { get => _distance; init => _distance = CheckRange(value, MaxDistance, nameof(Distance)); }

    /// <summary>Gets how much the shadow edge is pushed inward before it is blurred, from 0 to 1 (a fraction of <see cref="Size"/>).</summary>
    public float Choke { get => _choke; init => _choke = CheckUnit(value, nameof(Choke)); }

    /// <summary>Gets the blur size in pixels (0 to 250).</summary>
    public float Size { get => _size; init => _size = CheckRange(value, MaxSize, nameof(Size)); }

    /// <summary>Gets the falloff curve, or null for linear.</summary>
    public EffectContour? Contour { get; init; }

    /// <inheritdoc/>
    internal override int Reach => 0;

    /// <inheritdoc/>
    internal override int Footprint => (int)Math.Ceiling(_distance + _size) + 2;

    /// <inheritdoc/>
    internal override int Order => 3;

    internal override void DescribeKind(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "angle", (double)_angle));
        p.Add(new(prefix + "distance", (double)_distance));
        p.Add(new(prefix + "choke", (double)_choke));
        p.Add(new(prefix + "size", (double)_size));
        DescribeContour(p, prefix, Contour);
    }
}

/// <summary>
/// A glow around the outside of the layer's shape: the shape grown by <see cref="Spread"/> and blurred by <see cref="Size"/>, drawn below the
/// layer. Defaults: Screen, 75 %, light yellow, spread 0, size 5.
/// </summary>
public sealed class OuterGlowEffect : LayerEffect
{
    private readonly float _spread;
    private readonly float _size = 5;
    private readonly float _range = 0.5f;

    /// <summary>Creates an outer glow with the defaults.</summary>
    public OuterGlowEffect()
    {
        Blend = BlendMode.Screen;
        Opacity = 0.75f;
        Color = new Rgba32(255, 255, 190, 255);
    }

    /// <inheritdoc/>
    public override string Name => "outer-glow";

    /// <summary>Gets how much the shape grows before it is blurred, from 0 to 1 (a fraction of <see cref="Size"/>).</summary>
    public float Spread { get => _spread; init => _spread = CheckUnit(value, nameof(Spread)); }

    /// <summary>Gets the blur size in pixels (0 to 250).</summary>
    public float Size { get => _size; init => _size = CheckRange(value, MaxSize, nameof(Size)); }

    /// <summary>
    /// Gets the range from 0 to 1 (the default is 0.5, as in Photoshop): the falloff is stretched so that this fraction of it reaches full strength before the
    /// <see cref="Contour"/> is applied. With 1 the falloff is used as it is; with 0.5 the glow is twice as strong near the shape.
    /// </summary>
    public float Range { get => _range; init => _range = CheckUnit(value, nameof(Range)); }

    /// <summary>Gets the falloff curve, or null for linear.</summary>
    public EffectContour? Contour { get; init; }

    /// <inheritdoc/>
    internal override int Reach => (int)Math.Ceiling(_size) + 2;

    /// <inheritdoc/>
    internal override bool BelowLayer => true;

    /// <inheritdoc/>
    internal override int Order => 1;

    internal override void DescribeKind(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "spread", (double)_spread));
        p.Add(new(prefix + "size", (double)_size));
        p.Add(new(prefix + "range", (double)_range));
        DescribeContour(p, prefix, Contour);
    }
}

/// <summary>
/// A glow inside the layer's shape, from its edge toward the middle (or from the middle outward with <see cref="GlowSource.Center"/>),
/// drawn above the layer's pixels. Defaults: Screen, 75 %, light yellow, from the edge, choke 0, size 5.
/// </summary>
public sealed class InnerGlowEffect : LayerEffect
{
    private readonly float _choke;
    private readonly float _size = 5;
    private readonly float _range = 0.5f;

    /// <summary>Creates an inner glow with the defaults.</summary>
    public InnerGlowEffect()
    {
        Blend = BlendMode.Screen;
        Opacity = 0.75f;
        Color = new Rgba32(255, 255, 190, 255);
    }

    /// <inheritdoc/>
    public override string Name => "inner-glow";

    /// <summary>Gets where the glow starts.</summary>
    public GlowSource Source { get; init; }

    /// <summary>Gets how much the glow edge is pushed inward before it is blurred, from 0 to 1 (a fraction of <see cref="Size"/>).</summary>
    public float Choke { get => _choke; init => _choke = CheckUnit(value, nameof(Choke)); }

    /// <summary>Gets the blur size in pixels (0 to 250).</summary>
    public float Size { get => _size; init => _size = CheckRange(value, MaxSize, nameof(Size)); }

    /// <summary>Gets the range from 0 to 1 (the default is 0.5): see <see cref="OuterGlowEffect.Range"/>.</summary>
    public float Range { get => _range; init => _range = CheckUnit(value, nameof(Range)); }

    /// <summary>Gets the falloff curve, or null for linear.</summary>
    public EffectContour? Contour { get; init; }

    /// <inheritdoc/>
    internal override int Reach => 0;

    /// <inheritdoc/>
    internal override int Footprint => (int)Math.Ceiling(_size) + 2;

    /// <inheritdoc/>
    internal override int Order => 2;

    internal override void DescribeKind(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "source", Source == GlowSource.Edge ? "edge" : "center"));
        p.Add(new(prefix + "choke", (double)_choke));
        p.Add(new(prefix + "size", (double)_size));
        p.Add(new(prefix + "range", (double)_range));
        DescribeContour(p, prefix, Contour);
    }
}

/// <summary>
/// A solid-color outline of the layer's shape, drawn above everything else of the layer. Defaults: Normal, 100 %, black, 3 px outside.
/// The edge is antialiased from the shape's own alpha, so a stroke follows soft edges.
/// </summary>
public sealed class StrokeEffect : LayerEffect
{
    private readonly float _size = 3;

    /// <summary>Gets the position of the stroke relative to the edge.</summary>
    public StrokePosition Position { get; init; }

    /// <summary>Gets the stroke width in pixels (0 to 250).</summary>
    public float Size { get => _size; init => _size = CheckRange(value, MaxSize, nameof(Size)); }

    /// <inheritdoc/>
    public override string Name => "stroke";

    /// <inheritdoc/>
    internal override int Reach => Position == StrokePosition.Inside ? 0 : (int)Math.Ceiling(Position == StrokePosition.Center ? _size / 2 : _size) + 2;

    /// <inheritdoc/>
    internal override int Footprint => (int)Math.Ceiling(_size) + 2;

    /// <inheritdoc/>
    internal override bool BelowLayer => Position == StrokePosition.Outside;

    /// <inheritdoc/>
    internal override int Order => 4;

    internal override void DescribeKind(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "position", Position switch { StrokePosition.Inside => "inside", StrokePosition.Center => "center", _ => "outside" }));
        p.Add(new(prefix + "size", (double)_size));
    }
}

/// <summary>
/// Paints the layer's shape with one color, above the layer's pixels. With Normal blend and full opacity the layer takes the color
/// of the overlay. Defaults: Normal, 100 %, black.
/// </summary>
public sealed class ColorOverlayEffect : LayerEffect
{
    /// <inheritdoc/>
    public override string Name => "color-overlay";

    /// <inheritdoc/>
    internal override int Reach => 0;

    /// <inheritdoc/>
    internal override int Order => 0;
}

/// <summary>
/// The silky interior shading of Photoshop's "satin": two copies of the shape, moved <see cref="Distance"/> apart in opposite directions,
/// blurred by <see cref="Size"/>; where they differ the color is drawn inside the shape. Defaults: Multiply, 50 %, black, angle 19°,
/// distance 11, size 14, inverted.
/// </summary>
public sealed class SatinEffect : LayerEffect
{
    private readonly float _distance = 11;
    private readonly float _size = 14;
    private readonly float _angle = 19;

    /// <summary>Creates a satin effect with the defaults.</summary>
    public SatinEffect()
    {
        Blend = BlendMode.Multiply;
        Opacity = 0.5f;
    }

    /// <inheritdoc/>
    public override string Name => "satin";

    /// <summary>Gets the direction of the offset, in degrees counter-clockwise from the right.</summary>
    public float Angle { get => _angle; init => _angle = CheckAngle(value, nameof(Angle)); }

    /// <summary>Gets the offset of each of the two copies, in pixels (0 to 30000).</summary>
    public float Distance { get => _distance; init => _distance = CheckRange(value, MaxDistance, nameof(Distance)); }

    /// <summary>Gets the blur size in pixels (0 to 250).</summary>
    public float Size { get => _size; init => _size = CheckRange(value, MaxSize, nameof(Size)); }

    /// <summary>Gets a value indicating whether the shading is inverted (the default), so it is drawn where the copies agree rather than where they differ.</summary>
    public bool Inverted { get; init; } = true;

    /// <summary>Gets the falloff curve, or null for linear.</summary>
    public EffectContour? Contour { get; init; }

    /// <inheritdoc/>
    internal override int Reach => 0;

    /// <inheritdoc/>
    internal override int Order => 1;

    internal override void DescribeKind(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "angle", (double)_angle));
        p.Add(new(prefix + "distance", (double)_distance));
        p.Add(new(prefix + "size", (double)_size));
        p.Add(new(prefix + "inverted", Inverted));
        DescribeContour(p, prefix, Contour);
    }
}

/// <summary>
/// The effects of a layer, an immutable list. Effects are drawn from the layer's shape (its alpha, after its mask) when the document is
/// composited. Bottom to top the drawing order is fixed by kind: drop shadows, outer glows, the layer's pixels, color overlays, satin,
/// inner glows, inner shadows, strokes; effects of the same kind are drawn in list order. The layer's opacity scales every effect, while its fill
/// opacity scales the layer's own pixels and the overlays (which replace its fill), not shadows, glows, satin or strokes. A layer's blend mode applies to its pixels only; each effect has its own.
/// <para>
/// Effects reach beyond the layer's bounds (a shadow by its distance plus size), and the document recomputes the tiles an edit can reach
/// through them. Styles are supported on pixel and fill layers; a style on a group, an adjustment layer or a layer clipped to the layer below is kept
/// but not drawn.
/// </para>
/// </summary>
public sealed class LayerStyle
{
    /// <summary>The most effects one style holds.</summary>
    public const int MaxEffects = 64;

    /// <summary>Creates a style from a list of effects.</summary>
    /// <param name="effects">The effects, in drawing order within each kind.</param>
    /// <exception cref="ArgumentException">There are more than <see cref="MaxEffects"/>, or an entry is null.</exception>
    public LayerStyle(IEnumerable<LayerEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        LayerEffect[] list = [.. effects];
        if (list.Length > MaxEffects || list.Any(static e => e == null))
        {
            throw new ArgumentException($"A style holds at most {MaxEffects} effects and none may be null.", nameof(effects));
        }

        Effects = list;
        int reach = 0;
        int footprint = 0;
        foreach (LayerEffect e in list)
        {
            if (e.Enabled)
            {
                reach = Math.Max(reach, e.Reach);
                footprint = Math.Max(footprint, e.Footprint);
            }
        }

        Reach = reach;
        Footprint = footprint;
    }

    /// <summary>Gets the effects.</summary>
    public IReadOnlyList<LayerEffect> Effects { get; }

    /// <summary>
    /// Gets a value indicating whether the effects follow the layer's mask alone and ignore the transparency of the layer's own pixels, as the outline of a
    /// shape layer does (the fill, including a gradient that fades out, is not part of the shape). It has an effect only on a layer that has a mask; a fill
    /// layer always behaves this way. The default is false: the shape is the layer's alpha times its mask.
    /// </summary>
    public bool ShapeFromMask { get; init; }

    /// <summary>Gets how far, in pixels, enabled effects reach beyond the layer's bounds.</summary>
    internal int Reach { get; }

    /// <summary>Gets how far past the layer's bounds its shape must be known for the effects inside it.</summary>
    internal int Footprint { get; }

    /// <summary>True when at least one effect is enabled.</summary>
    internal bool Any
    {
        get
        {
            foreach (LayerEffect e in Effects)
            {
                if (e.Enabled)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal void Describe(List<OpParameter> parameters)
    {
        parameters.Add(new("style.count", (long)Effects.Count));
        parameters.Add(new("style.shape-from-mask", ShapeFromMask));
        for (int i = 0; i < Effects.Count; i++)
        {
            Effects[i].Describe(parameters, "style." + i.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }
}
