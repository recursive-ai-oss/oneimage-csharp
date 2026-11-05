using System.Numerics;

namespace OneImage.Metafile;

/// <summary>The rule that decides which side of a self-crossing outline is inside.</summary>
public enum FillRule
{
    /// <summary>A point is inside when the outline winds around it a non-zero number of times (GDI <c>WINDING</c>).</summary>
    NonZero = 0,

    /// <summary>A point is inside when a ray from it crosses the outline an odd number of times (GDI <c>ALTERNATE</c>, the default fill mode).</summary>
    EvenOdd = 1,
}

/// <summary>How a stroke ends.</summary>
public enum LineCap
{
    /// <summary>A half circle (GDI <c>PS_ENDCAP_ROUND</c>, the default).</summary>
    Round = 0,

    /// <summary>A square that extends half the width past the end (<c>PS_ENDCAP_SQUARE</c>).</summary>
    Square = 1,

    /// <summary>Ends exactly at the end point (<c>PS_ENDCAP_FLAT</c>).</summary>
    Flat = 2,
}

/// <summary>How two stroke segments meet.</summary>
public enum LineJoin
{
    /// <summary>A circular arc (GDI <c>PS_JOIN_ROUND</c>, the default).</summary>
    Round = 0,

    /// <summary>The corner is cut off flat (<c>PS_JOIN_BEVEL</c>).</summary>
    Bevel = 1,

    /// <summary>A sharp corner, cut to a bevel beyond the pen's miter limit (<c>PS_JOIN_MITER</c>).</summary>
    Miter = 2,
}

/// <summary>How a clip command combines its shape with the clip already in force.</summary>
public enum ClipMode
{
    /// <summary>Removes the clip: everything can be drawn. The command carries no shape.</summary>
    Reset = 0,

    /// <summary>The shape becomes the clip (GDI <c>RGN_COPY</c>).</summary>
    Replace = 1,

    /// <summary>The clip becomes the part of it that lies inside the shape (<c>RGN_AND</c>, <c>IntersectClipRect</c>).</summary>
    Intersect = 2,

    /// <summary>The clip becomes the part of it that lies outside the shape (<c>RGN_DIFF</c>, <c>ExcludeClipRect</c>).</summary>
    Exclude = 3,

    /// <summary>The clip becomes what is in it or in the shape (<c>RGN_OR</c>).</summary>
    Union = 4,

    /// <summary>The clip becomes what is in exactly one of the two (<c>RGN_XOR</c>).</summary>
    Xor = 5,
}

/// <summary>
/// How a drawing operation combines the pen or brush with what is already there: the sixteen binary raster operations of GDI
/// (<c>R2_*</c>). The numbers are GDI's.
/// </summary>
public enum MixMode
{
    /// <summary>Black.</summary>
    Black = 1,

    /// <summary>NOT (pen OR destination).</summary>
    NotMergePen = 2,

    /// <summary>NOT pen AND destination.</summary>
    MaskNotPen = 3,

    /// <summary>NOT pen.</summary>
    NotCopyPen = 4,

    /// <summary>Pen AND NOT destination.</summary>
    MaskPenNot = 5,

    /// <summary>NOT destination.</summary>
    Not = 6,

    /// <summary>Pen XOR destination.</summary>
    XorPen = 7,

    /// <summary>NOT (pen AND destination).</summary>
    NotMaskPen = 8,

    /// <summary>Pen AND destination.</summary>
    MaskPen = 9,

    /// <summary>NOT (pen XOR destination).</summary>
    NotXorPen = 10,

    /// <summary>The destination is left alone.</summary>
    Nop = 11,

    /// <summary>NOT pen OR destination.</summary>
    MergeNotPen = 12,

    /// <summary>The pen color replaces the destination (the default).</summary>
    CopyPen = 13,

    /// <summary>Pen OR NOT destination.</summary>
    MergePenNot = 14,

    /// <summary>Pen OR destination.</summary>
    MergePen = 15,

    /// <summary>White.</summary>
    White = 16,
}

/// <summary>The built-in hatch patterns (GDI <c>HS_*</c>): lines one pixel wide, repeating every eight pixels, anchored at the brush origin.</summary>
public enum HatchKind
{
    /// <summary>Horizontal lines.</summary>
    Horizontal = 0,

    /// <summary>Vertical lines.</summary>
    Vertical = 1,

    /// <summary>Lines running from the top left to the bottom right.</summary>
    ForwardDiagonal = 2,

    /// <summary>Lines running from the bottom left to the top right.</summary>
    BackwardDiagonal = 3,

    /// <summary>Horizontal and vertical lines.</summary>
    Cross = 4,

    /// <summary>Both diagonals.</summary>
    DiagonalCross = 5,
}

/// <summary>The kind of file a picture came from.</summary>
public enum MetafileKind
{
    /// <summary>An enhanced metafile (EMF), with or without EMF+ records.</summary>
    Emf = 0,

    /// <summary>A Windows metafile (WMF).</summary>
    Wmf = 1,
}

/// <summary>How serious a <see cref="DiagnosticCommand"/> is.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Information that does not change the picture, such as the presence of EMF+ records that a GDI fallback covers.</summary>
    Info = 0,

    /// <summary>Something was skipped, approximated or repaired, so the picture may differ from the one Windows draws.</summary>
    Warning = 1,

    /// <summary>The rest of the file could not be used (a record claims impossible sizes, or a limit was reached).</summary>
    Error = 2,
}

/// <summary>An axis-aligned rectangle in double precision, given by its edges.</summary>
/// <param name="Left">The x coordinate of the left edge.</param>
/// <param name="Top">The y coordinate of the top edge.</param>
/// <param name="Right">The x coordinate of the right edge.</param>
/// <param name="Bottom">The y coordinate of the bottom edge.</param>
public readonly record struct RectD(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Gets the width (<see cref="Right"/> minus <see cref="Left"/>); negative for a reversed rectangle.</summary>
    public double Width => Right - Left;

    /// <summary>Gets the height (<see cref="Bottom"/> minus <see cref="Top"/>); negative for a reversed rectangle.</summary>
    public double Height => Bottom - Top;

    /// <summary>Gets the rectangle with its edges put in order.</summary>
    public RectD Normalized => new(Math.Min(Left, Right), Math.Min(Top, Bottom), Math.Max(Left, Right), Math.Max(Top, Bottom));

    // The generated printer would include the derived properties, and Normalized would print itself forever.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.FormattableString.Invariant($"Left = {Left}, Top = {Top}, Right = {Right}, Bottom = {Bottom}"));
        return true;
    }
}

/// <summary>What the header of a metafile says about the picture, in the units the drawing commands use.</summary>
/// <remarks>
/// All drawing commands work in the units of the <em>reference device</em> the picture was recorded for (pixels of a display, in
/// practice), after the interpreter has applied mapping modes, window and viewport extents and world transforms. <see cref="PictureRectangle"/>
/// is the part of that space the picture occupies; a renderer scales it to wherever it draws.
/// </remarks>
/// <param name="Kind">The file kind.</param>
/// <param name="DeviceBounds">The bounds of the drawing in device units, as the header records them (inclusive of both edges).</param>
/// <param name="Frame">The picture frame in hundredths of a millimeter, as the header records it.</param>
/// <param name="DeviceWidth">The width of the reference device in pixels.</param>
/// <param name="DeviceHeight">The height of the reference device in pixels.</param>
/// <param name="DeviceWidthMillimeters">The width of the reference device in millimeters.</param>
/// <param name="DeviceHeightMillimeters">The height of the reference device in millimeters.</param>
/// <param name="PictureRectangle">
/// The frame converted to device units: the rectangle that Windows stretches over the destination when it plays the file. When the header
/// has no usable frame it is the device bounds, widened by one pixel on the right and bottom.
/// </param>
/// <param name="Description">The description string of the header (usually the creating application and the picture name), or empty.</param>
/// <param name="RecordCount">The number of records in the file.</param>
/// <param name="HasEmfPlus">Whether the file contains EMF+ records. They are not interpreted yet; see <see cref="DiagnosticCommand"/>.</param>
/// <param name="IsEmfPlusOnly">Whether the file has EMF+ records and no GDI fallback for them (it then draws nothing without them).</param>
public sealed record MetafileInfo(
    MetafileKind Kind,
    RectD DeviceBounds,
    RectD Frame,
    int DeviceWidth,
    int DeviceHeight,
    double DeviceWidthMillimeters,
    double DeviceHeightMillimeters,
    RectD PictureRectangle,
    string Description,
    int RecordCount,
    bool HasEmfPlus,
    bool IsEmfPlusOnly)
{
    /// <summary>Gets the width of the picture in device units.</summary>
    public double Width => PictureRectangle.Width;

    /// <summary>Gets the height of the picture in device units.</summary>
    public double Height => PictureRectangle.Height;
}

/// <summary>A device-independent bitmap, as the file stores it. Decode it with <see cref="ToRgba"/>.</summary>
public sealed class DeviceIndependentBitmap
{
    private readonly ReadOnlyMemory<byte> _bits;
    private readonly uint[] _palette;
    private readonly uint[] _masks;

    internal DeviceIndependentBitmap(int width, int height, bool topDown, int bitCount, int compression, uint[] palette, uint[] masks, ReadOnlyMemory<byte> bits)
    {
        Width = width;
        Height = height;
        IsTopDown = topDown;
        BitCount = bitCount;
        Compression = compression;
        _palette = palette;
        _masks = masks;
        _bits = bits;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels (always positive).</summary>
    public int Height { get; }

    /// <summary>Gets a value indicating whether the first row stored is the top row (a negative height in the header).</summary>
    public bool IsTopDown { get; }

    /// <summary>Gets the number of bits per pixel: 1, 2, 4, 8, 16, 24 or 32.</summary>
    public int BitCount { get; }

    /// <summary>Gets the DIB compression value: 0 (none), 1 (RLE8), 2 (RLE4) or 3 (bit fields).</summary>
    public int Compression { get; }

    /// <summary>Gets the pixel bytes as stored.</summary>
    public ReadOnlyMemory<byte> Bits => _bits;

    /// <summary>Gets the palette entries as 0x00RRGGBB values, empty when the bitmap has none.</summary>
    public IReadOnlyList<uint> Palette => _palette;

    /// <summary>Decodes the bitmap to straight (non-premultiplied) 8-bit RGBA, top row first.</summary>
    /// <param name="useAlphaChannel">
    /// For 32-bit bitmaps: take the fourth byte as alpha, which is premultiplied in the file and is un-premultiplied here. Without it, alpha is 255
    /// (the byte of a plain 32-bit bitmap is unused).
    /// </param>
    /// <returns><c>Width * Height * 4</c> bytes. Pixels the bitmap does not define (cut-off or skipped RLE data) are transparent black.</returns>
    public byte[] ToRgba(bool useAlphaChannel = false) => DibDecoder.Decode(this, useAlphaChannel, _palette, _masks);
}

/// <summary>A brush: what a shape is filled with. Derived types are <see cref="SolidBrushSpec"/>, <see cref="HatchBrushSpec"/> and <see cref="PatternBrushSpec"/>.</summary>
public abstract record BrushSpec
{
    internal BrushSpec()
    {
    }
}

/// <summary>A brush of one color.</summary>
/// <param name="Color">The color; opaque unless the file says otherwise.</param>
public sealed record SolidBrushSpec(Rgba32 Color) : BrushSpec;

/// <summary>A built-in hatch pattern.</summary>
/// <param name="Kind">The pattern.</param>
/// <param name="Foreground">The color of the lines.</param>
/// <param name="Background">The color between the lines, or <see langword="null"/> when the background mode is transparent and the destination shows through.</param>
/// <param name="Origin">The brush origin in device units: the point where the pattern repeats from.</param>
public sealed record HatchBrushSpec(HatchKind Kind, Rgba32 Foreground, Rgba32? Background, Vector2 Origin) : BrushSpec;

/// <summary>A brush made of a bitmap pattern, repeated without scaling in device pixels.</summary>
/// <param name="Bitmap">The pattern.</param>
/// <param name="Foreground">For a one-bit pattern with <paramref name="UseTextColors"/>: the color of the set bits (the text color at the time of use).</param>
/// <param name="Background">For a one-bit pattern with <paramref name="UseTextColors"/>: the color of the clear bits (the background color at the time of use).</param>
/// <param name="Origin">The brush origin in device units.</param>
/// <param name="UseTextColors">Whether a one-bit pattern is painted with <paramref name="Foreground"/> and <paramref name="Background"/> (a pattern made from a device bitmap) instead of its own color table (a device-independent bitmap).</param>
public sealed record PatternBrushSpec(DeviceIndependentBitmap Bitmap, Rgba32 Foreground, Rgba32 Background, Vector2 Origin, bool UseTextColors = false) : BrushSpec;

/// <summary>A pen, with everything resolved: GDI's pen styles, end caps, joins and dashes are given as plain numbers.</summary>
/// <param name="Width">
/// The stroke width in the units of the path. For a pen created as cosmetic it is 1 and <paramref name="IsCosmetic"/> is true.
/// </param>
/// <param name="IsCosmetic">
/// Whether the pen is one device pixel wide whatever the transform (GDI's cosmetic pens, and geometric pens of width 1 or less in
/// compatible mapping). For a cosmetic pen the dash lengths are in device pixels too.
/// </param>
/// <param name="Color">The color; ignored when <paramref name="Brush"/> is set.</param>
/// <param name="Cap">The end cap.</param>
/// <param name="Join">The join.</param>
/// <param name="MiterLimit">The ratio of miter length to line width beyond which a miter join is cut to a bevel (default 10).</param>
/// <param name="Dashes">Alternating on and off lengths, starting with on, or <see langword="null"/> for a solid line.</param>
/// <param name="GapColor">
/// The color the gaps of a dashed line are filled with when the background mode is opaque (the background color), or <see langword="null"/>
/// when the gaps are left alone.
/// </param>
/// <param name="Brush">The brush of a pen made with a hatch or pattern brush, or <see langword="null"/> to paint with <paramref name="Color"/>.</param>
/// <param name="IsInsideFrame">Whether the stroke is kept inside the shape's outline (GDI <c>PS_INSIDEFRAME</c>) instead of centered on it.</param>
public sealed record PenSpec(
    double Width,
    bool IsCosmetic,
    Rgba32 Color,
    LineCap Cap = LineCap.Round,
    LineJoin Join = LineJoin.Round,
    double MiterLimit = 10,
    IReadOnlyList<double>? Dashes = null,
    Rgba32? GapColor = null,
    BrushSpec? Brush = null,
    bool IsInsideFrame = false)
{
    /// <summary>Gets what a GDI+ pen adds to the GDI pen: dash cap and offset, alignment, anchors and the like; <see langword="null"/> for pens from GDI records.</summary>
    public PlusPenData? Plus { get; init; }
}

/// <summary>A font request, resolved from a logical font. The face name is the name Windows would match; the renderer finds the closest font it has.</summary>
/// <param name="FaceName">The family name, such as <c>Arial</c>; empty for the default font.</param>
/// <param name="Height">
/// The character height in logical units, as GDI states it: positive is the cell height (ascent plus descent, as for a pixel-size request),
/// negative is the em height; zero asks for a default size.
/// </param>
/// <param name="Width">The average character width in logical units, or 0 for the font's natural width.</param>
/// <param name="Weight">The weight, 1 to 1000; 400 is regular, 700 bold, 0 means the default (regular).</param>
/// <param name="Italic">Whether the font is italic.</param>
/// <param name="Underline">Whether text is underlined.</param>
/// <param name="StrikeOut">Whether text is struck out.</param>
/// <param name="Escapement">The angle of the text baseline in tenths of a degree, counterclockwise from the x axis.</param>
/// <param name="Orientation">The angle of each glyph in tenths of a degree (equal to the escapement unless the advanced graphics mode separates them).</param>
/// <param name="CharSet">The GDI character set number (1 default, 2 symbol, 128 Shift-JIS, ...).</param>
/// <param name="PitchAndFamily">The GDI pitch and family byte (the low two bits are the pitch, bits 4-7 the family).</param>
/// <param name="Quality">The GDI quality value (0 default, 3 non-antialiased, 4 antialiased, 5 ClearType).</param>
public sealed record FontSpec(
    string FaceName,
    double Height,
    double Width = 0,
    int Weight = 400,
    bool Italic = false,
    bool Underline = false,
    bool StrikeOut = false,
    int Escapement = 0,
    int Orientation = 0,
    int CharSet = 1,
    int PitchAndFamily = 0,
    int Quality = 0);

/// <summary>A vertex of a gradient fill.</summary>
/// <param name="Position">The position in logical units.</param>
/// <param name="Color">The color; its alpha is the vertex alpha of the file (usually 255).</param>
public readonly record struct GradientVertex(Vector2 Position, Rgba32 Color);

/// <summary>The geometry of a gradient fill.</summary>
public enum GradientMode
{
    /// <summary>Rectangles, the color changing from left to right.</summary>
    Horizontal = 0,

    /// <summary>Rectangles, the color changing from top to bottom.</summary>
    Vertical = 1,

    /// <summary>Triangles, colors interpolated between the three vertices.</summary>
    Triangle = 2,
}

/// <summary>How a bitmap is resampled when it is stretched (GDI stretch blt modes).</summary>
public enum StretchMode
{
    /// <summary>Pixels that are dropped when shrinking are combined with AND (<c>BLACKONWHITE</c>, GDI's default).</summary>
    BlackOnWhite = 1,

    /// <summary>Pixels that are dropped when shrinking are combined with OR (<c>WHITEONBLACK</c>).</summary>
    WhiteOnBlack = 2,

    /// <summary>Dropped pixels are discarded (<c>COLORONCOLOR</c>); the nearest source pixel is used.</summary>
    ColorOnColor = 3,

    /// <summary>The source is averaged when shrinking (<c>HALFTONE</c>).</summary>
    Halftone = 4,
}

/// <summary>The horizontal part of a text alignment.</summary>
public enum TextHorizontalAlignment
{
    /// <summary>The reference point is at the left of the text (the default).</summary>
    Left = 0,

    /// <summary>The reference point is at the horizontal center of the text.</summary>
    Center = 1,

    /// <summary>The reference point is at the right of the text.</summary>
    Right = 2,
}

/// <summary>The vertical part of a text alignment.</summary>
public enum TextVerticalAlignment
{
    /// <summary>The reference point is at the top of the character cell (the default).</summary>
    Top = 0,

    /// <summary>The reference point is on the baseline.</summary>
    Baseline = 1,

    /// <summary>The reference point is at the bottom of the character cell.</summary>
    Bottom = 2,
}
