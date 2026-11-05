using System.Globalization;
using System.Numerics;

namespace OneImage.Metafile;

// The part of the command model that only GDI+ (EMF+) records need. Everything here is additive: the GDI side of the model is unchanged.

/// <summary>A bitmap decoded to straight (non-premultiplied) 8-bit RGBA, top row first. EMF+ images and texture brushes use it.</summary>
public sealed class RasterImage
{
    internal RasterImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the pixels: <c>Width * Height * 4</c> bytes, red first, alpha last, straight alpha.</summary>
    public ReadOnlyMemory<byte> Pixels => Rgba;

    internal byte[] Rgba { get; }
}

/// <summary>How a pixel of an EMF+ image or texture brush is looked up (GDI+ <c>InterpolationMode</c>), reduced to what a renderer can honour.</summary>
public enum ImageInterpolation
{
    /// <summary>Nearest pixel (GDI+ <c>NearestNeighbor</c> and <c>HighSpeed</c>).</summary>
    Nearest = 0,

    /// <summary>Linear interpolation between the four nearest pixels (GDI+ default, <c>Bilinear</c>, <c>HighQualityBilinear</c>, and the bicubic modes, which are drawn bilinear).</summary>
    Bilinear = 1,
}

/// <summary>How an image or texture brush continues past its edge (GDI+ <c>WrapMode</c>).</summary>
public enum EdgeWrap
{
    /// <summary>The picture repeats.</summary>
    Tile = 0,

    /// <summary>The picture repeats, every other copy mirrored horizontally.</summary>
    TileFlipX = 1,

    /// <summary>The picture repeats, every other copy mirrored vertically.</summary>
    TileFlipY = 2,

    /// <summary>The picture repeats, mirrored in both directions on alternate copies.</summary>
    TileFlipXY = 3,

    /// <summary>The picture does not repeat; outside it the clamp color (or nothing) shows.</summary>
    Clamp = 4,
}

/// <summary>Fills the whole drawing surface (inside the clip) with a color, replacing what is there, as GDI+ <c>Graphics.Clear</c> does.</summary>
/// <param name="Color">The color, with its alpha: the result is exactly this color (not blended).</param>
public sealed record ClearCommand(Rgba32 Color) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "clear";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitClear(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string> { ["color"] = C(Color) };
}

/// <summary>
/// Sets the GDI+ rendering hints. They are part of the state that <see cref="SaveStateCommand"/> keeps. A renderer is free to ignore them; the
/// built-in raster renderer honours <see cref="Smoothing"/> (anti-aliasing), <see cref="SourceCopy"/> and <see cref="PixelOffsetHalf"/>.
/// </summary>
/// <param name="Smoothing">Whether shapes are anti-aliased (GDI+ <c>SmoothingMode</c> other than None and HighSpeed).</param>
/// <param name="SourceCopy">Whether fills replace the destination instead of blending over it (<c>CompositingMode.SourceCopy</c>).</param>
/// <param name="PixelOffsetHalf">Whether integer coordinates lie on pixel corners (<c>PixelOffsetMode.Half</c> / <c>HighQuality</c>) instead of pixel centers.</param>
/// <param name="Interpolation">How images are resampled.</param>
/// <param name="TextHint">The GDI+ <c>TextRenderingHint</c> number (0 system default, 1 single bit per pixel grid fit, 2 single bit per pixel, 3 anti-alias grid fit, 4 anti-alias, 5 ClearType grid fit).</param>
/// <param name="TextContrast">The text gamma times 1000 (GDI+ default 4000).</param>
/// <param name="CompositingQuality">The GDI+ <c>CompositingQuality</c> number (0 default, 1 high speed, 2 high quality, 3 gamma corrected, 4 assume linear). Recorded; blending is not changed by it.</param>
/// <param name="RenderingOrigin">The dither origin; recorded only.</param>
public sealed record RenderingHintsCommand(
    bool Smoothing,
    bool SourceCopy,
    bool PixelOffsetHalf,
    ImageInterpolation Interpolation,
    int TextHint,
    int TextContrast,
    int CompositingQuality,
    Vector2 RenderingOrigin) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "rendering-hints";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitRenderingHints(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["smoothing"] = Smoothing ? "true" : "false",
        ["compositing"] = SourceCopy ? "source-copy" : "source-over",
        ["pixel-offset"] = PixelOffsetHalf ? "half" : "none",
        ["interpolation"] = Interpolation.ToString().ToLowerInvariant(),
        ["text-hint"] = TextHint.ToString(CultureInfo.InvariantCulture),
        ["text-contrast"] = TextContrast.ToString(CultureInfo.InvariantCulture),
        ["compositing-quality"] = CompositingQuality.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>How the lines of a GDI+ string are laid out in its rectangle (GDI+ <c>StringFormat</c>).</summary>
/// <param name="Alignment">Horizontal placement of each line in the rectangle (0 near, 1 center, 2 far); for vertical text this is the placement along the text direction.</param>
/// <param name="LineAlignment">Placement of the block of lines across the rectangle (0 near, 1 center, 2 far).</param>
/// <param name="NoWrap">Whether lines are not broken at word boundaries.</param>
/// <param name="Vertical">Whether lines run top to bottom (GDI+ <c>DirectionVertical</c>).</param>
/// <param name="RightToLeft">Whether the reading order is right to left.</param>
/// <param name="NoClip">Whether text may show outside the rectangle.</param>
/// <param name="Trimming">The GDI+ trimming number (0 none, 1 character, 2 word, 3-5 ellipsis).</param>
/// <param name="HasFormat">Whether the file has a string format object for the string; without one GDI+ defaults apply (near alignment, wrapping, a margin of one sixth of an em on each side).</param>
public sealed record StringFormatSpec(int Alignment, int LineAlignment, bool NoWrap, bool Vertical, bool RightToLeft, bool NoClip, int Trimming, bool HasFormat);

/// <summary>Draws a string in a layout rectangle (GDI+ <c>DrawString</c>). The renderer breaks the lines.</summary>
/// <param name="Text">The characters.</param>
/// <param name="Font">The font: <see cref="FontSpec.Height"/> is minus the em size in logical units (the em height convention of GDI).</param>
/// <param name="Layout">The layout rectangle in logical units.</param>
/// <param name="Format">The string format.</param>
/// <param name="Brush">The brush the glyphs are filled with.</param>
public sealed record DrawStringCommand(string Text, FontSpec Font, RectD Layout, StringFormatSpec Format, BrushSpec Brush) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "draw-string";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitDrawString(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["text"] = Text,
        ["font"] = $"{Font.FaceName} {F(-Font.Height)} w{Font.Weight}{(Font.Italic ? " italic" : string.Empty)}{(Font.Underline ? " underline" : string.Empty)}{(Font.StrikeOut ? " strikeout" : string.Empty)}",
        ["layout"] = $"{F(Layout.Left)},{F(Layout.Top)},{F(Layout.Width)},{F(Layout.Height)}",
        ["align"] = $"{Format.Alignment},{Format.LineAlignment}",
        ["wrap"] = Format.NoWrap ? "false" : "true",
        ["vertical"] = Format.Vertical ? "true" : "false",
    };
}

/// <summary>Draws glyphs at given positions (GDI+ <c>DrawDriverString</c>).</summary>
/// <param name="Text">The characters, or glyph indices when <paramref name="IsGlyphIndices"/> is true (one UTF-16 unit each).</param>
/// <param name="IsGlyphIndices">Whether <paramref name="Text"/> holds glyph indices of the font instead of characters.</param>
/// <param name="Positions">The origin of each glyph in logical units, or <see langword="null"/> when the glyphs follow each other with the font's advances starting at <paramref name="Origin"/>.</param>
/// <param name="Origin">The origin of the first glyph (the first position).</param>
/// <param name="Font">The font (see <see cref="DrawStringCommand.Font"/> for the height convention).</param>
/// <param name="Brush">The brush.</param>
/// <param name="Vertical">Whether glyphs are laid out top to bottom.</param>
/// <param name="Transform">An extra transform applied to the glyph positions and shapes (in logical space), or <see langword="null"/>.</param>
public sealed record DrawGlyphsCommand(
    string Text,
    bool IsGlyphIndices,
    IReadOnlyList<Vector2>? Positions,
    Vector2 Origin,
    FontSpec Font,
    BrushSpec Brush,
    bool Vertical,
    Matrix3x2? Transform) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "draw-glyphs";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitDrawGlyphs(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["text"] = Text,
        ["glyph-indices"] = IsGlyphIndices ? "true" : "false",
        ["origin"] = F(Origin),
        ["positions"] = Positions is null ? string.Empty : string.Join(' ', Positions.Select(F)),
        ["font"] = $"{Font.FaceName} {F(-Font.Height)}",
        ["vertical"] = Vertical ? "true" : "false",
    };
}

/// <summary>Draws a picture (GDI+ <c>DrawImage</c>) into a parallelogram. Embedded metafiles are not carried here: the interpreter plays them as commands.</summary>
/// <param name="Image">The picture.</param>
/// <param name="Source">The part of the picture to draw, in picture pixels.</param>
/// <param name="DestTopLeft">Where the top-left corner of <paramref name="Source"/> goes, in logical units.</param>
/// <param name="DestTopRight">Where its top-right corner goes.</param>
/// <param name="DestBottomLeft">Where its bottom-left corner goes.</param>
/// <param name="Interpolation">How pixels are resampled.</param>
/// <param name="Wrap">What shows when <paramref name="Source"/> reaches past the picture.</param>
/// <param name="ClampColor">For <see cref="EdgeWrap.Clamp"/>: the color outside the picture.</param>
/// <param name="Opacity">A constant factor for the alpha of the picture (from an image attributes color matrix), 1 when there is none.</param>
public sealed record DrawImageCommand(
    RasterImage Image,
    RectD Source,
    Vector2 DestTopLeft,
    Vector2 DestTopRight,
    Vector2 DestBottomLeft,
    ImageInterpolation Interpolation,
    EdgeWrap Wrap,
    Rgba32 ClampColor,
    double Opacity = 1) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "draw-image";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitDrawImage(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["image"] = $"{Image.Width}x{Image.Height}",
        ["source"] = $"{F(Source.Left)},{F(Source.Top)},{F(Source.Width)},{F(Source.Height)}",
        ["dest"] = $"{F(DestTopLeft)} {F(DestTopRight)} {F(DestBottomLeft)}",
        ["interpolation"] = Interpolation.ToString().ToLowerInvariant(),
        ["wrap"] = Wrap.ToString().ToLowerInvariant(),
        ["opacity"] = F(Opacity),
    };
}

/// <summary>A gradient brush between two points or along a rectangle's axis, with any number of color stops (GDI+ <c>LinearGradientBrush</c>).</summary>
/// <param name="Start">The point where the gradient has its first color, in logical units.</param>
/// <param name="End">The point where it has its last color. Colors are constant along lines perpendicular to <c>End - Start</c>.</param>
/// <param name="Stops">Color stops with positions from 0 to 1, in ascending order; at least two.</param>
/// <param name="Wrap">What happens before <paramref name="Start"/> and after <paramref name="End"/>.</param>
/// <param name="Transform">Maps the brush's own space (where <paramref name="Start"/> and <paramref name="End"/> are given) to logical units; identity when the file has no brush transform.</param>
public sealed record LinearGradientBrushSpec(Vector2 Start, Vector2 End, IReadOnlyList<(float Position, Rgba32 Color)> Stops, EdgeWrap Wrap, Matrix3x2 Transform) : BrushSpec;

/// <summary>A gradient from a center color to colors on a boundary (GDI+ <c>PathGradientBrush</c>).</summary>
/// <param name="Boundary">The boundary as a polygon, in the brush's own space.</param>
/// <param name="BoundaryColors">The color at each boundary point (the same length as <paramref name="Boundary"/>).</param>
/// <param name="CenterColor">The color at <paramref name="Center"/>.</param>
/// <param name="Center">The center point.</param>
/// <param name="Wrap">The wrap mode recorded in the file.</param>
/// <param name="Transform">Maps the brush's own space to logical units; identity when the file has no brush transform.</param>
public sealed record PathGradientBrushSpec(IReadOnlyList<Vector2> Boundary, IReadOnlyList<Rgba32> BoundaryColors, Rgba32 CenterColor, Vector2 Center, EdgeWrap Wrap, Matrix3x2 Transform) : BrushSpec;

/// <summary>A brush made of a picture, placed in logical space by a matrix (GDI+ <c>TextureBrush</c>).</summary>
/// <param name="Image">The picture.</param>
/// <param name="Wrap">How it repeats.</param>
/// <param name="Transform">Maps picture pixels to logical units (the brush transform of the file).</param>
public sealed record TextureBrushSpec(RasterImage Image, EdgeWrap Wrap, Matrix3x2 Transform) : BrushSpec;

/// <summary>A GDI+ hatch brush (<c>HatchBrush</c>): one of 54 patterns on an eight-by-eight cell, anchored at the corner of the output.</summary>
/// <param name="Style">The GDI+ <c>HatchStyle</c> number (0 horizontal ... 53).</param>
/// <param name="Foreground">The color of the pattern's set cells.</param>
/// <param name="Background">The color of the clear cells; may be transparent.</param>
public sealed record StyleHatchBrushSpec(int Style, Rgba32 Foreground, Rgba32 Background) : BrushSpec;

/// <summary>Where the stroke of a GDI+ pen lies relative to the geometry.</summary>
public enum PenAlignment
{
    /// <summary>Centered on the outline.</summary>
    Center = 0,

    /// <summary>Inside the outline (shapes) or centered (open lines).</summary>
    Inset = 1,
}

/// <summary>Extra data of a GDI+ pen. A <see cref="PenSpec"/> made from EMF+ carries it in <see cref="PenSpec.Plus"/>.</summary>
/// <param name="DashOffset">The distance into the dash pattern where the line starts, in pen widths.</param>
/// <param name="DashCap">The shape of each dash end (flat, square or round).</param>
/// <param name="Alignment">Where the stroke lies.</param>
/// <param name="StartCap">The cap at the start of an open figure, when it is not one of the plain caps (3 triangle, 0x10-0x14 anchors, 0xFF custom); otherwise -1.</param>
/// <param name="EndCap">The cap at the end of an open figure, with the same encoding as <paramref name="StartCap"/>.</param>
/// <param name="Compound">The compound line positions (pairs of start and end in pen widths, 0 to 1), or <see langword="null"/>; recorded only.</param>
/// <param name="Transform">The pen's own transform (maps the pen's circular nib to an ellipse), or <see langword="null"/>; recorded only.</param>
public sealed record PlusPenData(double DashOffset, LineCap DashCap, PenAlignment Alignment, int StartCap, int EndCap, IReadOnlyList<float>? Compound, Matrix3x2? Transform);
