using OneImage.Drawing;
using OneImage.Layers;

namespace OneImage.Psd;

/// <summary>The color mode a PSD or PSB file stores its pixels in.</summary>
public enum PsdColorMode
{
    /// <summary>1 bit per pixel; 1 is black.</summary>
    Bitmap = 0,

    /// <summary>Gray.</summary>
    Grayscale = 1,

    /// <summary>Indexed color; the palette is in the color mode data section.</summary>
    Indexed = 2,

    /// <summary>Red, green, blue.</summary>
    Rgb = 3,

    /// <summary>Cyan, magenta, yellow, black (stored inverted: 255 is no ink).</summary>
    Cmyk = 4,

    /// <summary>Spot or custom channels with no color model. Not supported.</summary>
    Multichannel = 7,

    /// <summary>One or two gray channels with ink curves; read as gray.</summary>
    Duotone = 8,

    /// <summary>CIE L*a*b*.</summary>
    Lab = 9,
}

/// <summary>What a layer of a PSD file is, from the tagged blocks it carries.</summary>
public enum PsdLayerKind
{
    /// <summary>An ordinary raster layer (or a layer type this library draws from its stored pixels).</summary>
    Pixel = 0,

    /// <summary>A layer group.</summary>
    Group,

    /// <summary>A solid color fill layer, modelled as a <see cref="FillLayer"/>.</summary>
    SolidColorFill,

    /// <summary>An adjustment layer that the model reproduces (levels, curves, hue/saturation, ...).</summary>
    Adjustment,

    /// <summary>An adjustment layer the model cannot reproduce: it is kept and written back, and has no effect on a render.</summary>
    UnsupportedAdjustment,

    /// <summary>A type layer, drawn from its stored pixels; the text data is kept as a block.</summary>
    Text,

    /// <summary>A smart object, drawn from its stored pixels; the embedded document is kept as blocks.</summary>
    SmartObject,

    /// <summary>A shape layer, drawn from its stored pixels; the vector data is kept as blocks.</summary>
    Shape,

    /// <summary>A gradient or pattern fill layer. It is a <see cref="FillLayer"/> drawn from its settings when they can be reproduced, and otherwise a pixel layer of the stored raster; the fill settings are kept as a block either way.</summary>
    GradientOrPatternFill,
}

/// <summary>An uninterpreted tagged block: a four-character key and its data, kept exactly as read and written back unchanged.</summary>
/// <param name="Key">The four-character key, for example <c>TySh</c> or <c>lfx2</c>.</param>
/// <param name="Data">The data, without the signature, key and length.</param>
/// <param name="Large">Whether the block was written with the <c>8B64</c> signature.</param>
public sealed record PsdBlock(string Key, ReadOnlyMemory<byte> Data, bool Large = false);

/// <summary>An image resource block (resolution, thumbnail, paths, guides, ...), kept as read.</summary>
/// <param name="Id">The resource id.</param>
/// <param name="Name">The Pascal-string name, usually empty.</param>
/// <param name="Data">The data.</param>
public sealed record PsdResource(int Id, string Name, ReadOnlyMemory<byte> Data);

/// <summary>
/// What a PSD file says about a layer beyond what <see cref="Layer"/> models: the kind, the layer id, and the tagged blocks that are kept
/// uninterpreted (text, smart object, vector mask, layer styles, ...).
/// </summary>
public sealed class PsdLayerInfo
{
    /// <summary>Gets or sets what kind of layer this is.</summary>
    public PsdLayerKind Kind { get; set; }

    /// <summary>Gets or sets the layer id (<c>lyid</c>), or <see langword="null"/> to let the writer number the layers.</summary>
    public int? LayerId { get; set; }

    /// <summary>Gets or sets whether a group is shown open in the layers panel (only meaningful for groups).</summary>
    public bool GroupOpen { get; set; } = true;

    /// <summary>Gets the blocks that are written back unchanged after the ones the model regenerates. The list may be edited.</summary>
    public List<PsdBlock> Blocks { get; } = [];

    /// <summary>
    /// Gets or sets the blend key the file used when it is not one of the 24 modes (<c>diss</c>, <c>dkCl</c>, <c>lgCl</c>). It is written
    /// back only while the layer's <see cref="Layer.BlendMode"/> still equals what the key was mapped to.
    /// </summary>
    public string? SourceBlendKey { get; set; }

    internal BlendMode SourceBlendMode { get; set; }

    internal Adjustment? SourceAdjustment { get; set; }

    internal PsdBlock? SourceAdjustmentBlock { get; set; }

    internal PsdBlock? SourceFillBlock { get; set; }

    internal Brush? SourceFillBrush { get; set; }

    internal int SourceFlags { get; set; } = 8;

    internal bool WasEmpty { get; set; }

    /// <summary>
    /// The style read from the layer's effects block, so a save can tell an untouched style (the block is written back as it was) from an edited one.
    /// </summary>
    internal LayerStyle? SourceStyle { get; set; }

    /// <summary>
    /// How many enabled effects of the layer are not drawn (gradient or pattern overlays and strokes, bevel and emboss, gradient glows, effects past
    /// the per-layer limit). They stay in the file and are written back untouched.
    /// </summary>
    public int EffectsNotDrawn { get; internal set; }

    /// <summary>How many drawn effects leave out a detail (the random grain of a shadow or glow).</summary>
    public int EffectsApproximated { get; internal set; }
}
