using OneImage.Drawing;

namespace OneImage.Text;

/// <summary>
/// A stretch of the text that is set in its own font, size, attributes, decorations and paint. Add runs to
/// <see cref="TextOptions.TextRuns"/>; layout, measuring, outlines and drawing all honor them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Start"/> and <see cref="End"/> are UTF-16 offsets into the text passed to the call, End exclusive. They are clamped to
/// the text, an empty range does nothing, and a character outside every run uses the options' own font and no decoration. Runs may
/// overlap: where they do, the one added later wins for each character. Properties left at their default (null font, null brush)
/// fall back to the options' font and to the brush and pen of the drawing call.
/// </para>
/// <para>A run is a plain mutable object read when the layout or drawing call runs.</para>
/// </remarks>
public sealed class TextRun
{
    private int _start;
    private int _end;

    /// <summary>Creates a run over a range of the text.</summary>
    /// <param name="start">The first UTF-16 offset.</param>
    /// <param name="end">One past the last UTF-16 offset.</param>
    /// <exception cref="ArgumentOutOfRangeException">An offset is negative.</exception>
    public TextRun(int start, int end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Gets or sets the first UTF-16 offset of the run.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Start
    {
        get => _start;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _start = value;
        }
    }

    /// <summary>Gets or sets the offset one past the last UTF-16 offset of the run.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int End
    {
        get => _end;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _end = value;
        }
    }

    /// <summary>Gets or sets the font of the run, or null for <see cref="TextOptions.Font"/> (the default). Its size is the run's size.</summary>
    public Font? Font { get; set; }

    /// <summary>Gets or sets the shift and shrink of the run. The default is <see cref="TextAttributes.None"/>.</summary>
    public TextAttributes TextAttributes { get; set; }

    /// <summary>Gets or sets the lines drawn along the run. The default is <see cref="TextDecorations.None"/>.</summary>
    public TextDecorations TextDecorations { get; set; }

    /// <summary>Gets or sets the fill of the run's glyphs (and of its decorations without their own pen), or null for the brush of the drawing call (the default).</summary>
    public Brush? Brush { get; set; }

    /// <summary>Gets or sets the outline of the run's glyphs (and of its filled decorations), or null for the pen of the drawing call (the default).</summary>
    public Pen? Pen { get; set; }

    /// <summary>Gets or sets a pen that strokes the underline instead of filling a rectangle; the pen's width is the line's thickness. The default, null, fills a rectangle of the font's underline thickness.</summary>
    public Pen? UnderlinePen { get; set; }

    /// <summary>Gets or sets a pen that strokes the strikeout line instead of filling a rectangle. The default is null.</summary>
    public Pen? StrikeoutPen { get; set; }

    /// <summary>Gets or sets a pen that strokes the overline instead of filling a rectangle. The default is null.</summary>
    public Pen? OverlinePen { get; set; }

    internal TextRun Copy() => (TextRun)MemberwiseClone();
}
