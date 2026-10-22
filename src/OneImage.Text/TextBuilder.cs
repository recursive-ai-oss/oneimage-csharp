using OneImage.Drawing;
using OneImage.Text.Layout;
using Path = OneImage.Drawing.Path;

namespace OneImage.Text;

/// <summary>Turns text into vector outlines you can fill, stroke, transform or clip with yourself.</summary>
public static class TextBuilder
{
    /// <summary>
    /// Lays the text out and returns the outlines of all its glyphs as one path in pixel coordinates (y down). Fill it with the
    /// default non-zero rule. Spaces and glyphs without an outline add nothing.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Font, origin, wrapping and alignment.</param>
    /// <returns>The path; empty when there is nothing to draw.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ImageLimitException">The text is too long, or the outlines exceed the path size limit.</exception>
    /// <exception cref="FontFormatException">The font's glyph data is malformed (found lazily, when first needed).</exception>
    public static Path GeneratePath(string text, TextOptions options)
    {
        TextLayout layout = TextLayout.Create(text, options);
        var builder = new PathBuilder();
        layout.AppendTo(builder);
        return builder.Build();
    }
}
