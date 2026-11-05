using System.Globalization;
using System.Numerics;

namespace OneImage.Metafile;

/// <summary>
/// A command of the drawing stream: one resolved drawing step, with no GDI state left to interpret. A consumer walks the commands in order
/// (<see cref="MetafileDocument.Commands"/>, or <see cref="MetafileDocument.Play"/> with a <see cref="DrawingCommandVisitor"/>) and keeps only
/// a transform and a clip, both set by explicit commands.
/// </summary>
/// <remarks>
/// <para>
/// The stream works like a canvas. <see cref="SaveStateCommand"/> and <see cref="RestoreStateCommand"/> push and pop the pair (transform, clip).
/// <see cref="SetTransformCommand"/> sets the matrix from the <em>logical</em> coordinates of the following commands to the device space of
/// <see cref="MetafileInfo.PictureRectangle"/>; it already contains the mapping mode, window and viewport, and world transform of the file.
/// Geometry, pen widths and text sizes in drawing commands are in logical units. <see cref="ClipCommand"/> shapes are the exception: they are in
/// device space, because a GDI clip region does not follow later changes of the transform.
/// </para>
/// <para>
/// Commands have language-neutral <see cref="Name"/>s and <see cref="Describe"/> their parameters as strings, so a stream can be logged or compared
/// across implementations. Records the interpreter cannot honour appear as <see cref="DiagnosticCommand"/> at the point where they occurred.
/// </para>
/// </remarks>
public abstract record DrawingCommand
{
    internal DrawingCommand()
    {
    }

    /// <summary>Gets the index of the file record this command came from (counting from 0), or -1 for commands that do not come from one.</summary>
    public int RecordIndex { get; init; } = -1;

    /// <summary>Gets the language-neutral name of the command, such as <c>fill-path</c>.</summary>
    public abstract string Name { get; }

    /// <summary>Calls the visitor method for this command's type.</summary>
    /// <param name="visitor">The visitor.</param>
    public abstract void Accept(DrawingCommandVisitor visitor);

    /// <summary>Describes the parameters of the command as name/value strings, in invariant culture.</summary>
    /// <returns>The parameters; empty when there are none.</returns>
    public virtual IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>();

    internal static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    internal static string F(Vector2 v) => F(v.X) + "," + F(v.Y);

    internal static string C(Rgba32 c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2}";
}

/// <summary>Starts the stream. It is always the first command.</summary>
/// <param name="Info">What the header says about the picture.</param>
public sealed record BeginPictureCommand(MetafileInfo Info) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "begin-picture";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitBeginPicture(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["kind"] = Info.Kind.ToString().ToLowerInvariant(),
        ["picture"] = $"{F(Info.PictureRectangle.Left)},{F(Info.PictureRectangle.Top)},{F(Info.PictureRectangle.Right)},{F(Info.PictureRectangle.Bottom)}",
        ["records"] = Info.RecordCount.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Pushes the current transform and clip.</summary>
public sealed record SaveStateCommand : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "save";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitSaveState(this);
}

/// <summary>Pops the transform and clip pushed by the matching <see cref="SaveStateCommand"/>. The interpreter never emits more restores than saves.</summary>
public sealed record RestoreStateCommand : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "restore";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitRestoreState(this);
}

/// <summary>Sets the transform from logical coordinates to the device space of the picture.</summary>
/// <param name="Transform">The matrix, System.Numerics row-vector convention (<c>x' = x*M11 + y*M21 + M31</c>). Identity is the state at the start.</param>
public sealed record SetTransformCommand(Matrix3x2 Transform) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "set-transform";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitSetTransform(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["matrix"] = $"{F(Transform.M11)},{F(Transform.M12)},{F(Transform.M21)},{F(Transform.M22)},{F(Transform.M31)},{F(Transform.M32)}",
    };
}

/// <summary>Changes the clip: the area that later drawing may touch. At the start nothing is clipped.</summary>
/// <param name="Mode">How the shape combines with the current clip.</param>
/// <param name="Path">The shape in <em>device space</em> (not affected by the current transform); <see langword="null"/> for <see cref="ClipMode.Reset"/>. A rectangle clip is a four-point path.</param>
/// <param name="Rule">The fill rule for the shape.</param>
public sealed record ClipCommand(ClipMode Mode, MetafilePath? Path, FillRule Rule = FillRule.NonZero) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "clip";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitClip(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["mode"] = Mode.ToString().ToLowerInvariant(),
        ["rule"] = Rule == FillRule.EvenOdd ? "evenodd" : "nonzero",
        ["path"] = Path?.ToSvgPathData() ?? string.Empty,
    };
}

/// <summary>Moves the whole current clip (GDI <c>OffsetClipRgn</c>).</summary>
/// <param name="Delta">The movement in device units.</param>
public sealed record OffsetClipCommand(Vector2 Delta) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "offset-clip";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitOffsetClip(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string> { ["delta"] = F(Delta) };
}

/// <summary>Fills an outline.</summary>
/// <param name="Path">The outline in logical coordinates.</param>
/// <param name="Rule">The fill rule.</param>
/// <param name="Brush">The brush.</param>
/// <param name="Mix">How the brush combines with the destination; <see cref="MixMode.CopyPen"/> is a plain paint-over.</param>
public sealed record FillPathCommand(MetafilePath Path, FillRule Rule, BrushSpec Brush, MixMode Mix = MixMode.CopyPen) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "fill-path";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitFillPath(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["rule"] = Rule == FillRule.EvenOdd ? "evenodd" : "nonzero",
        ["brush"] = Brush switch
        {
            SolidBrushSpec s => "solid " + C(s.Color),
            HatchBrushSpec h => "hatch " + h.Kind.ToString().ToLowerInvariant(),
            _ => "pattern",
        },
        ["mix"] = Mix.ToString().ToLowerInvariant(),
        ["path"] = Path.ToSvgPathData(),
    };
}

/// <summary>Strokes an outline with a pen.</summary>
/// <param name="Path">The outline in logical coordinates (the stroke is centered on it unless the pen says otherwise).</param>
/// <param name="Pen">The pen.</param>
/// <param name="Mix">How the pen combines with the destination.</param>
public sealed record StrokePathCommand(MetafilePath Path, PenSpec Pen, MixMode Mix = MixMode.CopyPen) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "stroke-path";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitStrokePath(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["width"] = F(Pen.Width),
        ["cosmetic"] = Pen.IsCosmetic ? "true" : "false",
        ["color"] = C(Pen.Color),
        ["cap"] = Pen.Cap.ToString().ToLowerInvariant(),
        ["join"] = Pen.Join.ToString().ToLowerInvariant(),
        ["miter-limit"] = F(Pen.MiterLimit),
        ["dashes"] = Pen.Dashes is null ? string.Empty : string.Join(' ', Pen.Dashes.Select(F)),
        ["mix"] = Mix.ToString().ToLowerInvariant(),
        ["path"] = Path.ToSvgPathData(),
    };
}

/// <summary>What a <see cref="BlitCommand"/> does with its source.</summary>
public enum BlitKind
{
    /// <summary>Combines source, destination and pattern with a ternary raster operation (<see cref="BlitCommand.Rop"/>).</summary>
    RasterOperation = 0,

    /// <summary>Blends the source over the destination with a constant alpha and, optionally, the alpha of each pixel (GDI <c>AlphaBlend</c>).</summary>
    AlphaBlend = 1,

    /// <summary>Copies the source except for pixels of one color (GDI <c>TransparentBlt</c>).</summary>
    TransparentColor = 2,
}

/// <summary>
/// Draws a bitmap, or applies a raster operation to a rectangle: the family of <c>BitBlt</c>, <c>StretchBlt</c>, <c>PlgBlt</c>, <c>MaskBlt</c>,
/// <c>StretchDIBits</c>, <c>SetDIBitsToDevice</c>, <c>PatBlt</c>, <c>AlphaBlend</c> and <c>TransparentBlt</c>.
/// </summary>
/// <param name="Bitmap">The source, or <see langword="null"/> for an operation on the destination and brush only (such as <c>PatBlt</c>).</param>
/// <param name="Source">The part of the bitmap to draw, in bitmap pixels (top row is 0 whatever the bitmap's storage order).</param>
/// <param name="DestTopLeft">Where the top-left corner of the source goes, in logical coordinates.</param>
/// <param name="DestTopRight">Where the top-right corner of the source goes.</param>
/// <param name="DestBottomLeft">Where the bottom-left corner of the source goes. A plain rectangle has the same y as the top-left corner for the first and the same x for the second.</param>
/// <param name="Rop">The ternary raster operation as a 32-bit GDI value (<c>SRCCOPY</c> is 0x00CC0020, <c>PATCOPY</c> 0x00F00021, <c>DSTINVERT</c> 0x00550009, ...).</param>
/// <param name="Brush">The brush used by operations that read a pattern, or <see langword="null"/>.</param>
/// <param name="Stretch">The stretch mode in force.</param>
/// <param name="Kind">What to do with the source.</param>
/// <param name="ConstantAlpha">For <see cref="BlitKind.AlphaBlend"/>: the alpha applied to the whole source, 0 to 255.</param>
/// <param name="UsePerPixelAlpha">For <see cref="BlitKind.AlphaBlend"/>: whether the bitmap's own alpha channel (premultiplied in the file) is used too.</param>
/// <param name="TransparentColor">For <see cref="BlitKind.TransparentColor"/>: the color that is not drawn.</param>
/// <param name="Mask">For a mask blit: a one-bit bitmap whose clear bits keep the destination (when <see langword="null"/> there is no mask).</param>
/// <param name="MaskOrigin">The pixel of the mask that corresponds to the top-left of the source rectangle.</param>
/// <param name="TextColor">The color that set bits of a one-bit source take (the text color).</param>
/// <param name="BackgroundColor">The color that clear bits of a one-bit source take (the background color).</param>
public sealed record BlitCommand(
    DeviceIndependentBitmap? Bitmap,
    RectD Source,
    Vector2 DestTopLeft,
    Vector2 DestTopRight,
    Vector2 DestBottomLeft,
    uint Rop,
    BrushSpec? Brush,
    StretchMode Stretch,
    BlitKind Kind,
    byte ConstantAlpha,
    bool UsePerPixelAlpha,
    Rgba32 TransparentColor,
    DeviceIndependentBitmap? Mask,
    Vector2 MaskOrigin,
    Rgba32 TextColor,
    Rgba32 BackgroundColor) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "blit";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitBlit(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["kind"] = Kind.ToString().ToLowerInvariant(),
        ["rop"] = "0x" + Rop.ToString("x8", CultureInfo.InvariantCulture),
        ["source"] = Bitmap is null ? "none" : $"{Bitmap.Width}x{Bitmap.Height}x{Bitmap.BitCount} {F(Source.Left)},{F(Source.Top)},{F(Source.Width)},{F(Source.Height)}",
        ["dest"] = $"{F(DestTopLeft)} {F(DestTopRight)} {F(DestBottomLeft)}",
        ["stretch"] = Stretch.ToString().ToLowerInvariant(),
    };
}

/// <summary>Fills rectangles or triangles with color gradients (GDI <c>GradientFill</c>).</summary>
/// <param name="Vertices">The vertices, in logical coordinates.</param>
/// <param name="Indices">For rectangles two indices each (opposite corners); for triangles three each.</param>
/// <param name="Mode">The geometry.</param>
public sealed record GradientFillCommand(IReadOnlyList<GradientVertex> Vertices, IReadOnlyList<int> Indices, GradientMode Mode) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "gradient-fill";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitGradientFill(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["mode"] = Mode.ToString().ToLowerInvariant(),
        ["vertices"] = string.Join(' ', Vertices.Select(v => F(v.Position) + ":" + C(v.Color))),
        ["indices"] = string.Join(' ', Indices.Select(i => i.ToString(CultureInfo.InvariantCulture))),
    };
}

/// <summary>Draws a run of text. The interpreter has resolved alignment flags, the current position and the font; positions are in logical units.</summary>
/// <param name="Text">The characters. When <paramref name="IsGlyphIndices"/> is true each UTF-16 unit is a glyph index of the font, not a character.</param>
/// <param name="IsGlyphIndices">Whether <paramref name="Text"/> holds glyph indices (GDI <c>ETO_GLYPH_INDEX</c>).</param>
/// <param name="Origin">The reference point in logical coordinates; how it relates to the text is given by the two alignments.</param>
/// <param name="Horizontal">The horizontal alignment at the reference point.</param>
/// <param name="Vertical">The vertical alignment at the reference point.</param>
/// <param name="Font">The font.</param>
/// <param name="Color">The text color.</param>
/// <param name="Advances">For each character, the distance in logical units from its origin to the next one, or <see langword="null"/> for the font's own advances.</param>
/// <param name="Background">The color painted behind each character cell when the background mode is opaque, or <see langword="null"/> when it is transparent.</param>
/// <param name="OpaqueRectangle">A rectangle (logical coordinates) to fill with <paramref name="OpaqueColor"/> before the text is drawn (GDI <c>ETO_OPAQUE</c>), or <see langword="null"/>.</param>
/// <param name="OpaqueColor">The color for <paramref name="OpaqueRectangle"/>: the background color in force, whatever the background mode.</param>
/// <param name="ClipRectangle">A rectangle (logical coordinates) the text is clipped to (<c>ETO_CLIPPED</c>), or <see langword="null"/>.</param>
/// <param name="RightToLeft">Whether the text is laid out right to left (<c>ETO_RTLREADING</c>).</param>
/// <param name="CharacterExtra">Extra space added after each character, in logical units (<c>SetTextCharacterExtra</c>).</param>
/// <param name="BreakExtra">Extra space added to each of the first <paramref name="BreakCount"/> space characters (<c>SetTextJustification</c>).</param>
/// <param name="BreakCount">The number of spaces <paramref name="BreakExtra"/> applies to.</param>
public sealed record DrawTextCommand(
    string Text,
    bool IsGlyphIndices,
    Vector2 Origin,
    TextHorizontalAlignment Horizontal,
    TextVerticalAlignment Vertical,
    FontSpec Font,
    Rgba32 Color,
    IReadOnlyList<double>? Advances,
    Rgba32? Background,
    RectD? OpaqueRectangle,
    Rgba32 OpaqueColor,
    RectD? ClipRectangle,
    bool RightToLeft = false,
    double CharacterExtra = 0,
    double BreakExtra = 0,
    int BreakCount = 0) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "draw-text";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitDrawText(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["text"] = Text,
        ["origin"] = F(Origin),
        ["align"] = Horizontal.ToString().ToLowerInvariant() + "," + Vertical.ToString().ToLowerInvariant(),
        ["font"] = $"{Font.FaceName} {F(Font.Height)} w{Font.Weight}{(Font.Italic ? " italic" : string.Empty)}{(Font.Underline ? " underline" : string.Empty)}{(Font.StrikeOut ? " strikeout" : string.Empty)} esc{Font.Escapement}",
        ["color"] = C(Color),
    };
}

/// <summary>A comment record (GDI <c>GdiComment</c>) passed through unchanged. EMF+ comments are interpreted into other commands and are not passed through.</summary>
/// <param name="Kind">What kind of comment this is.</param>
/// <param name="Data">The comment data without the record header (for public comments also without the identifier).</param>
public sealed record CommentCommand(CommentKind Kind, ReadOnlyMemory<byte> Data) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "comment";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitComment(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["kind"] = Kind.ToString().ToLowerInvariant(),
        ["bytes"] = Data.Length.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>The kind of a comment record.</summary>
public enum CommentKind
{
    /// <summary>An application's private data.</summary>
    Generic = 0,

    /// <summary>EMF+ records (identifier "EMF+").</summary>
    EmfPlus = 1,

    /// <summary>EMF spool records.</summary>
    Spool = 2,

    /// <summary>A public comment (identifier 0x43494447), such as window or object-level markers.</summary>
    Public = 3,
}

/// <summary>Reports something the interpreter skipped, approximated or repaired. Unsupported records always produce one; nothing is dropped silently.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">A plain-language description.</param>
/// <param name="RecordType">The EMF record type number it concerns, or -1.</param>
public sealed record DiagnosticCommand(DiagnosticSeverity Severity, string Message, int RecordType = -1) : DrawingCommand
{
    /// <inheritdoc />
    public override string Name => "diagnostic";

    /// <inheritdoc />
    public override void Accept(DrawingCommandVisitor visitor) => visitor.VisitDiagnostic(this);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["severity"] = Severity.ToString().ToLowerInvariant(),
        ["message"] = Message,
        ["record-type"] = RecordType.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Receives the commands of a picture. Every method does nothing by default; override those you handle. New command types may be added in
/// future versions, which then call methods that do nothing in your subclass, so a visitor keeps working.
/// </summary>
public abstract partial class DrawingCommandVisitor
{
    /// <summary>Handles <see cref="BeginPictureCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitBeginPicture(BeginPictureCommand command)
    {
    }

    /// <summary>Handles <see cref="SaveStateCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitSaveState(SaveStateCommand command)
    {
    }

    /// <summary>Handles <see cref="RestoreStateCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitRestoreState(RestoreStateCommand command)
    {
    }

    /// <summary>Handles <see cref="SetTransformCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitSetTransform(SetTransformCommand command)
    {
    }

    /// <summary>Handles <see cref="ClipCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitClip(ClipCommand command)
    {
    }

    /// <summary>Handles <see cref="OffsetClipCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitOffsetClip(OffsetClipCommand command)
    {
    }

    /// <summary>Handles <see cref="FillPathCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitFillPath(FillPathCommand command)
    {
    }

    /// <summary>Handles <see cref="StrokePathCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitStrokePath(StrokePathCommand command)
    {
    }

    /// <summary>Handles <see cref="BlitCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitBlit(BlitCommand command)
    {
    }

    /// <summary>Handles <see cref="GradientFillCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitGradientFill(GradientFillCommand command)
    {
    }

    /// <summary>Handles <see cref="DrawTextCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitDrawText(DrawTextCommand command)
    {
    }

    /// <summary>Handles <see cref="CommentCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitComment(CommentCommand command)
    {
    }

    /// <summary>Handles <see cref="DiagnosticCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitDiagnostic(DiagnosticCommand command)
    {
    }
}
