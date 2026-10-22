using System.Drawing;

namespace OneImage.Text;

/// <summary>Where one line of laid-out text sits. Positions are pixels in the same space as <see cref="TextOptions.Origin"/>, y down.</summary>
public readonly record struct LineMetrics
{
    /// <summary>Gets the index in the text (UTF-16 code units) of the line's first character.</summary>
    public int Start { get; init; }

    /// <summary>Gets the number of UTF-16 code units in the line, without its line break; trailing spaces at a wrap are included.</summary>
    public int Length { get; init; }

    /// <summary>Gets the x of the line's start edge.</summary>
    public float Left { get; init; }

    /// <summary>Gets the y of the baseline.</summary>
    public float Baseline { get; init; }

    /// <summary>Gets the advance width of the line without trailing spaces.</summary>
    public float Advance { get; init; }

    /// <summary>Gets the distance from the top of the line box to the baseline.</summary>
    public float Ascender { get; init; }

    /// <summary>Gets the distance from the baseline to the bottom of the line box.</summary>
    public float Descender { get; init; }

    /// <summary>Gets the line pitch: the font size in pixels times <see cref="TextOptions.LineSpacing"/>.</summary>
    public float LineHeight { get; init; }
}

/// <summary>The advance box of one character (extended grapheme cluster) in laid-out text.</summary>
public readonly record struct GlyphBounds
{
    /// <summary>Gets the index in the text (UTF-16 code units) of the first code point of the character.</summary>
    public int Index { get; init; }

    /// <summary>Gets the Unicode scalar value of the first code point, or U+FFFD for an unpaired surrogate.</summary>
    public int CodePoint { get; init; }

    /// <summary>Gets the zero-based line the character is on.</summary>
    public int Line { get; init; }

    /// <summary>Gets the box from the pen position across the (kerned) advance, over the full line height.</summary>
    public RectangleF Bounds { get; init; }

    /// <summary>Gets the box around the character's ink (exact curve extents); zero-size for characters with no outline, such as spaces.</summary>
    public RectangleF Ink { get; init; }
}

/// <summary>The measured size and position of a piece of text.</summary>
public sealed class TextMetrics
{
    internal TextMetrics(SizeF advance, RectangleF bounds, LineMetrics[] lines)
    {
        Advance = advance;
        Bounds = bounds;
        Lines = lines;
    }

    /// <summary>
    /// Gets the size of the laid-out block: the widest line's advance (without trailing spaces) by the number of lines times the line
    /// pitch. It does not depend on the origin or alignment.
    /// </summary>
    public SizeF Advance { get; }

    /// <summary>
    /// Gets the box around the ink of all glyphs (exact curve extents, unhinted), positioned by the origin and alignment. Text
    /// without ink, such as spaces only, gives a zero-size rectangle at the block's top left.
    /// </summary>
    public RectangleF Bounds { get; }

    /// <summary>Gets the number of lines after wrapping. A line break at the very end of the text does not start another line.</summary>
    public int LineCount => Lines.Count;

    /// <summary>Gets one entry per line.</summary>
    public IReadOnlyList<LineMetrics> Lines { get; }
}
