namespace OneImage.Layers;

/// <summary>
/// Paints the layer's shape with a gradient, above the layer's pixels (Photoshop's "gradient overlay"). Like <see cref="ColorOverlayEffect"/> it
/// recolors the layer's pixels and keeps their alpha; the gradient's own opacity stops fade the overlay. Defaults: Normal, 100 %. The base
/// <see cref="LayerEffect.Color"/> is not used.
/// </summary>
public sealed class GradientOverlayEffect : LayerEffect
{
    /// <summary>Gets the gradient and its layout. A gradient overlay without it is not drawn.</summary>
    public required EffectGradientFill Fill { get; init; }

    /// <inheritdoc/>
    public override string Name => "gradient-overlay";

    /// <inheritdoc/>
    internal override int Reach => 0;

    /// <inheritdoc/>
    internal override int Order => 1;

    internal override void DescribeKind(List<OpParameter> p, string prefix) => Fill.Describe(p, prefix);
}

/// <summary>
/// Paints the layer's shape with a repeating pattern, above the layer's pixels (Photoshop's "pattern overlay"). It recolors the layer's pixels and
/// keeps their alpha. Defaults: Normal, 100 %. The base <see cref="LayerEffect.Color"/> is not used.
/// </summary>
public sealed class PatternOverlayEffect : LayerEffect
{
    /// <summary>Gets the pattern and its placement.</summary>
    public required EffectPatternFill Fill { get; init; }

    /// <inheritdoc/>
    public override string Name => "pattern-overlay";

    /// <inheritdoc/>
    internal override int Reach => 0;

    /// <inheritdoc/>
    internal override int Order => 2;

    internal override void DescribeKind(List<OpParameter> p, string prefix) => Fill.Describe(p, prefix);
}

/// <summary>The form of a <see cref="BevelEmbossEffect"/>.</summary>
public enum BevelStyle
{
    /// <summary>The slope runs from the shape's edge inward.</summary>
    InnerBevel = 0,

    /// <summary>The slope runs from the shape's edge outward.</summary>
    OuterBevel = 1,

    /// <summary>The slope straddles the edge: half of its size on each side.</summary>
    Emboss = 2,

    /// <summary>The edge sinks into the backdrop: the slope straddles the edge and falls toward it from both sides.</summary>
    PillowEmboss = 3,

    /// <summary>The slope straddles the edges of the layer's strokes (an <see cref="StrokeEffect"/> on the same layer; the shape's own edge if there is none).</summary>
    StrokeEmboss = 4,
}

/// <summary>How the height of a <see cref="BevelEmbossEffect"/> is shaped.</summary>
public enum BevelTechnique
{
    /// <summary>A rounded slope (the shape blurred): soft highlights that follow corners.</summary>
    Smooth = 0,

    /// <summary>A straight 45° slope with a crisp edge.</summary>
    ChiselHard = 1,

    /// <summary>A straight slope with a slightly softened edge.</summary>
    ChiselSoft = 2,
}

/// <summary>
/// Photoshop's "bevel and emboss": the shape is turned into a height field whose slope is lit from <see cref="Angle"/> and
/// <see cref="Altitude"/>; surfaces turned toward the light get the highlight color and those turned away get the shadow color. Defaults:
/// inner bevel, smooth, depth 1 (100 %), up, size 5, soften 0, light from 120° at 30°, highlight Screen white 75 %, shadow Multiply black 75 %.
/// <para>
/// The base <see cref="LayerEffect.Blend"/> and <see cref="LayerEffect.Color"/> are not used; <see cref="LayerEffect.Opacity"/> (default 1)
/// scales both the highlight and the shadow. The bevel contour, texture and "use shape" options are not modelled, and the height is
/// the shape's edge distance, so the shading is a close match to Photoshop's rather than exact.
/// </para>
/// </summary>
public sealed class BevelEmbossEffect : LayerEffect
{
    private readonly float _depth = 1f;
    private readonly float _size = 5f;
    private readonly float _soften;
    private readonly float _angle = 120f;
    private readonly float _altitude = 30f;
    private readonly float _highlightOpacity = 0.75f;
    private readonly float _shadowOpacity = 0.75f;

    /// <summary>The largest depth, as a fraction (1000 %).</summary>
    public const float MaxDepth = 10f;

    /// <summary>The largest soften amount, in pixels.</summary>
    public const float MaxSoften = 16f;

    /// <summary>Creates a bevel with the defaults.</summary>
    public BevelEmbossEffect()
    {
        Blend = BlendMode.Normal;
        Opacity = 1f;
    }

    /// <inheritdoc/>
    public override string Name => "bevel-emboss";

    /// <summary>Gets the form.</summary>
    public BevelStyle Style { get; init; }

    /// <summary>Gets how the height is shaped.</summary>
    public BevelTechnique Technique { get; init; }

    /// <summary>Gets the steepness of the slope as a fraction (1 = 100 %), 0 to 10.</summary>
    public float Depth { get => _depth; init => _depth = CheckRange(value, MaxDepth, nameof(Depth)); }

    /// <summary>Gets a value indicating whether the surface rises toward the middle of the shape (the default, "up") or sinks ("down").</summary>
    public bool Up { get; init; } = true;

    /// <summary>Gets the width of the slope in pixels (0 to 250).</summary>
    public float Size { get => _size; init => _size = CheckRange(value, MaxSize, nameof(Size)); }

    /// <summary>Gets the blur applied to the height field, in pixels (0 to 16), which rounds the shading.</summary>
    public float Soften { get => _soften; init => _soften = CheckRange(value, MaxSoften, nameof(Soften)); }

    /// <summary>Gets the direction the light comes from, in degrees counter-clockwise from the right.</summary>
    public float Angle { get => _angle; init => _angle = CheckAngle(value, nameof(Angle)); }

    /// <summary>Gets the light's height above the layer, in degrees (0 to 90).</summary>
    public float Altitude { get => _altitude; init => _altitude = CheckRange(value, 90f, nameof(Altitude)); }

    /// <summary>Gets the blend mode of the highlight (default Screen).</summary>
    public BlendMode HighlightBlend { get; init; } = BlendMode.Screen;

    /// <summary>Gets the highlight color (default white).</summary>
    public Rgba32 HighlightColor { get; init; } = new(255, 255, 255, 255);

    /// <summary>Gets the highlight opacity from 0 to 1 (default 0.75).</summary>
    public float HighlightOpacity { get => _highlightOpacity; init => _highlightOpacity = CheckUnit(value, nameof(HighlightOpacity)); }

    /// <summary>Gets the blend mode of the shadow (default Multiply).</summary>
    public BlendMode ShadowBlend { get; init; } = BlendMode.Multiply;

    /// <summary>Gets the shadow color (default black).</summary>
    public Rgba32 ShadowColor { get; init; } = new(0, 0, 0, 255);

    /// <summary>Gets the shadow opacity from 0 to 1 (default 0.75).</summary>
    public float ShadowOpacity { get => _shadowOpacity; init => _shadowOpacity = CheckUnit(value, nameof(ShadowOpacity)); }

    /// <summary>Gets the gloss contour, which remaps the lighting from dark (0) through neutral (0.5) to bright (1), or null for linear.</summary>
    public EffectContour? GlossContour { get; init; }

    /// <inheritdoc/>
    internal override int Reach => (int)Math.Ceiling(_size + _soften) + 3;

    /// <inheritdoc/>
    internal override int Order => 6;

    internal override void DescribeKind(List<OpParameter> p, string prefix)
    {
        p.Add(new(prefix + "style", Style switch { BevelStyle.OuterBevel => "outer-bevel", BevelStyle.Emboss => "emboss", BevelStyle.PillowEmboss => "pillow-emboss", BevelStyle.StrokeEmboss => "stroke-emboss", _ => "inner-bevel" }));
        p.Add(new(prefix + "technique", Technique switch { BevelTechnique.ChiselHard => "chisel-hard", BevelTechnique.ChiselSoft => "chisel-soft", _ => "smooth" }));
        p.Add(new(prefix + "depth", (double)_depth));
        p.Add(new(prefix + "direction", Up ? "up" : "down"));
        p.Add(new(prefix + "size", (double)_size));
        p.Add(new(prefix + "soften", (double)_soften));
        p.Add(new(prefix + "angle", (double)_angle));
        p.Add(new(prefix + "altitude", (double)_altitude));
        p.Add(new(prefix + "highlight-blend", BlendNames.Name(HighlightBlend) ?? "screen"));
        p.Add(new(prefix + "highlight-color", Hex(HighlightColor)));
        p.Add(new(prefix + "highlight-opacity", (double)_highlightOpacity));
        p.Add(new(prefix + "shadow-blend", BlendNames.Name(ShadowBlend) ?? "multiply"));
        p.Add(new(prefix + "shadow-color", Hex(ShadowColor)));
        p.Add(new(prefix + "shadow-opacity", (double)_shadowOpacity));
        DescribeContour(p, prefix + "gloss-", GlossContour);
    }

    private static string Hex(Rgba32 c) => "#" + c.R.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + c.G.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + c.B.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
}
