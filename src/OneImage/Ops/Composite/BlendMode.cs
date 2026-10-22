namespace OneImage;

/// <summary>
/// How the colors of an overlay combine with the colors below it in <see cref="Image.Composite(Image, int, int, float, bool, BlendMode, AlphaComposition)"/>.
/// The formulas are the ones in the W3C Compositing and Blending Level 1 specification (separable modes in section 10.1,
/// non-separable modes in 10.2), applied to straight (non-premultiplied) colors in 0..1. The modes that the specification
/// does not list (Add, Subtract, Linear Burn, Divide, Vivid Light, Linear Light, Pin Light, Hard Mix) use the definitions
/// editors such as Photoshop use. Where the overlay or the backdrop is partly transparent the colors are mixed by the spec's
/// general rule, so a transparent pixel never contributes its (meaningless) color.
/// </summary>
public enum BlendMode
{
    /// <summary>The overlay color replaces the backdrop color (weighted by alpha).</summary>
    Normal = 0,

    /// <summary><c>cb · cs</c>: darkens; white leaves the backdrop unchanged.</summary>
    Multiply,

    /// <summary><c>cb + cs − cb·cs</c>: lightens; black leaves the backdrop unchanged.</summary>
    Screen,

    /// <summary>Multiply or Screen depending on the backdrop (Hard Light with the layers swapped).</summary>
    Overlay,

    /// <summary>The darker of the two, per channel.</summary>
    Darken,

    /// <summary>The lighter of the two, per channel.</summary>
    Lighten,

    /// <summary>Brightens the backdrop to reflect the overlay: <c>min(1, cb / (1 − cs))</c>.</summary>
    ColorDodge,

    /// <summary>Darkens the backdrop to reflect the overlay: <c>1 − min(1, (1 − cb) / cs)</c>.</summary>
    ColorBurn,

    /// <summary>Multiply or Screen depending on the overlay.</summary>
    HardLight,

    /// <summary>A gentler Overlay-like darkening or lightening (W3C formula).</summary>
    SoftLight,

    /// <summary><c>|cb − cs|</c>.</summary>
    Difference,

    /// <summary><c>cb + cs − 2·cb·cs</c>: like Difference with lower contrast.</summary>
    Exclusion,

    /// <summary><c>min(1, cb + cs)</c> (ImageSharp's Add; Photoshop's Linear Dodge).</summary>
    Add,

    /// <summary><c>max(0, cb − cs)</c>.</summary>
    Subtract,

    /// <summary><c>max(0, cb + cs − 1)</c>.</summary>
    LinearBurn,

    /// <summary>The same as <see cref="Add"/>, under Photoshop's name.</summary>
    LinearDodge = Add,

    /// <summary>Color Burn below mid-gray overlay values, Color Dodge above.</summary>
    VividLight = 16,

    /// <summary><c>clamp(cb + 2·cs − 1)</c>.</summary>
    LinearLight,

    /// <summary>Darken or Lighten against the overlay depending on which side of mid-gray it is.</summary>
    PinLight,

    /// <summary>1 where <c>cb + cs ≥ 1</c>, else 0, per channel.</summary>
    HardMix,

    /// <summary><c>min(1, cb / cs)</c>; dividing by zero gives 1 (0 when the backdrop is also 0).</summary>
    Divide,

    /// <summary>Hue of the overlay with the saturation and luminosity of the backdrop. On gray images the result is the backdrop.</summary>
    Hue,

    /// <summary>Saturation of the overlay with the hue and luminosity of the backdrop. On gray images the result is the backdrop.</summary>
    Saturation,

    /// <summary>Hue and saturation of the overlay with the luminosity of the backdrop. On gray images the result is the backdrop.</summary>
    Color,

    /// <summary>Luminosity of the overlay with the hue and saturation of the backdrop. On gray images the result is the overlay.</summary>
    Luminosity,
}

/// <summary>
/// How the overlay's and the backdrop's coverage (alpha) combine: the twelve Porter–Duff operators. In each, <c>S</c> is the
/// overlay and <c>D</c> the backdrop; the result is <c>S·Fa + D·Fb</c> on premultiplied values, with the factors given below.
/// </summary>
public enum AlphaComposition
{
    /// <summary>The overlay over the backdrop: <c>Fa = 1</c>, <c>Fb = 1 − αs</c>. The default.</summary>
    SrcOver = 0,

    /// <summary>Nothing: <c>Fa = 0</c>, <c>Fb = 0</c>.</summary>
    Clear,

    /// <summary>Only the overlay: <c>Fa = 1</c>, <c>Fb = 0</c>.</summary>
    Src,

    /// <summary>Only the backdrop: <c>Fa = 0</c>, <c>Fb = 1</c>.</summary>
    Dest,

    /// <summary>The backdrop over the overlay: <c>Fa = 1 − αb</c>, <c>Fb = 1</c>.</summary>
    DestOver,

    /// <summary>The overlay where the backdrop is: <c>Fa = αb</c>, <c>Fb = 0</c>.</summary>
    SrcIn,

    /// <summary>The backdrop where the overlay is: <c>Fa = 0</c>, <c>Fb = αs</c>.</summary>
    DestIn,

    /// <summary>The overlay where the backdrop is not: <c>Fa = 1 − αb</c>, <c>Fb = 0</c>.</summary>
    SrcOut,

    /// <summary>The backdrop where the overlay is not: <c>Fa = 0</c>, <c>Fb = 1 − αs</c>.</summary>
    DestOut,

    /// <summary>The overlay over the backdrop, but only where the backdrop is: <c>Fa = αb</c>, <c>Fb = 1 − αs</c>.</summary>
    SrcAtop,

    /// <summary>The backdrop over the overlay, but only where the overlay is: <c>Fa = 1 − αb</c>, <c>Fb = αs</c>.</summary>
    DestAtop,

    /// <summary>Either one, but not both: <c>Fa = 1 − αb</c>, <c>Fb = 1 − αs</c>.</summary>
    Xor,
}

/// <summary>
/// The language-neutral names of the blend modes and compositions (the strings an op descriptor carries) and the
/// Porter–Duff factor table. A layer engine reads the same tables.
/// </summary>
internal static class BlendNames
{
    private static readonly (BlendMode Mode, string Name)[] Modes =
    [
        (BlendMode.Normal, "normal"), (BlendMode.Multiply, "multiply"), (BlendMode.Screen, "screen"), (BlendMode.Overlay, "overlay"),
        (BlendMode.Darken, "darken"), (BlendMode.Lighten, "lighten"), (BlendMode.ColorDodge, "color-dodge"), (BlendMode.ColorBurn, "color-burn"),
        (BlendMode.HardLight, "hard-light"), (BlendMode.SoftLight, "soft-light"), (BlendMode.Difference, "difference"), (BlendMode.Exclusion, "exclusion"),
        (BlendMode.Add, "linear-dodge"), (BlendMode.Subtract, "subtract"), (BlendMode.LinearBurn, "linear-burn"), (BlendMode.VividLight, "vivid-light"),
        (BlendMode.LinearLight, "linear-light"), (BlendMode.PinLight, "pin-light"), (BlendMode.HardMix, "hard-mix"), (BlendMode.Divide, "divide"),
        (BlendMode.Hue, "hue"), (BlendMode.Saturation, "saturation"), (BlendMode.Color, "color"), (BlendMode.Luminosity, "luminosity"),
    ];

    private static readonly string[] Compositions =
    [
        "src-over", "clear", "src", "dest", "dest-over", "src-in", "dest-in", "src-out", "dest-out", "src-atop", "dest-atop", "xor",
    ];

    public static bool IsDefined(BlendMode mode) => Name(mode) is not null;

    public static bool IsDefined(AlphaComposition alpha) => (uint)alpha < (uint)Compositions.Length;

    public static string? Name(BlendMode mode)
    {
        foreach ((BlendMode m, string n) in Modes)
        {
            if (m == mode)
            {
                return n;
            }
        }

        return null;
    }

    public static string Name(AlphaComposition alpha) => Compositions[(int)alpha];

    /// <summary>
    /// The factors as <c>Fa = a0 + a1·αb</c> and <c>Fb = b0 + b1·αs</c>; every Porter–Duff factor is 0, 1, α or 1 − α.
    /// </summary>
    public static (float A0, float A1, float B0, float B1) Factors(AlphaComposition alpha) => alpha switch
    {
        AlphaComposition.SrcOver => (1f, 0f, 1f, -1f),
        AlphaComposition.Clear => (0f, 0f, 0f, 0f),
        AlphaComposition.Src => (1f, 0f, 0f, 0f),
        AlphaComposition.Dest => (0f, 0f, 1f, 0f),
        AlphaComposition.DestOver => (1f, -1f, 1f, 0f),
        AlphaComposition.SrcIn => (0f, 1f, 0f, 0f),
        AlphaComposition.DestIn => (0f, 0f, 0f, 1f),
        AlphaComposition.SrcOut => (1f, -1f, 0f, 0f),
        AlphaComposition.DestOut => (0f, 0f, 1f, -1f),
        AlphaComposition.SrcAtop => (0f, 1f, 1f, -1f),
        AlphaComposition.DestAtop => (1f, -1f, 0f, 1f),
        _ => (1f, -1f, 1f, -1f), // Xor
    };

    /// <summary>True when an opaque backdrop stays opaque, so the result still fits a format without alpha.</summary>
    public static bool KeepsOpaqueBackdropOpaque(AlphaComposition alpha) =>
        alpha is AlphaComposition.SrcOver or AlphaComposition.Dest or AlphaComposition.DestOver or AlphaComposition.SrcAtop;

    /// <summary>True when a fully transparent overlay leaves the backdrop untouched.</summary>
    public static bool EmptyOverlayKeepsBackdrop(AlphaComposition alpha) => KeepsOpaqueBackdropOpaque(alpha);
}
