using System.Drawing;
using OneImage.Text.Layout;

namespace OneImage.Text;

/// <summary>Measures text without drawing it. All results are in pixels, y down, and follow the same layout the drawing calls use.</summary>
public static class TextMeasurer
{
    /// <summary>Lays the text out and reports its size, ink bounds and lines.</summary>
    /// <param name="text">The text. Line breaks start new lines; a break at the very end does not.</param>
    /// <param name="options">Font, origin, wrapping and alignment.</param>
    /// <returns>The measurements.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ImageLimitException">The text is longer than 4,194,304 UTF-16 code units.</exception>
    /// <exception cref="FontFormatException">The font's glyph or kerning data is malformed (found lazily, when first needed).</exception>
    public static TextMetrics Measure(string text, TextOptions options) => TextLayout.Create(text, options).ToMetrics();

    /// <summary>Gets the advance size of the text block: widest line (without trailing spaces) by lines times line pitch.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Font, wrapping and spacing.</param>
    /// <returns>The size in pixels; zero for empty text.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static SizeF MeasureAdvance(string text, TextOptions options) => Measure(text, options).Advance;

    /// <summary>Gets the box around the ink of the text, positioned by the origin and alignment.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Font, origin, wrapping and alignment.</param>
    /// <returns>The ink bounds; see <see cref="TextMetrics.Bounds"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static RectangleF MeasureBounds(string text, TextOptions options) => Measure(text, options).Bounds;

    /// <summary>Counts the lines the text occupies after wrapping.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Font and wrapping.</param>
    /// <returns>The line count; 0 for empty text.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static int CountLines(string text, TextOptions options) => TextLayout.Create(text, options).Lines.Count;

    /// <summary>Gets the advance box of every character in layout order. A character is a grapheme cluster: a base with its combining marks, an emoji sequence, a Hangul syllable block, a flag.</summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Font, origin, wrapping and alignment.</param>
    /// <returns>One entry per laid-out grapheme cluster; zero-width format characters have none.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<GlyphBounds> GetCharacterBounds(string text, TextOptions options) => TextLayout.Create(text, options).ToCharacterBounds();
}
