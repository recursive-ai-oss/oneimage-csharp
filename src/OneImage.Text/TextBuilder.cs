using OneImage.Drawing;
using OneImage.Text.Layout;
using Path = OneImage.Drawing.Path;

namespace OneImage.Text;

/// <summary>Turns text into vector outlines you can fill, stroke, transform or clip with yourself.</summary>
public static class TextBuilder
{
    /// <summary>
    /// Lays the text out and returns the outlines of all its glyphs as one path in pixel coordinates (y down). Fill it with the
    /// default non-zero rule. Spaces and glyphs without an outline add nothing. Runs, font fallback and a path in the options apply;
    /// decorations are included as rectangles (a decoration with its own pen is left out, since it has no outline to fill).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">Font, origin, wrapping and alignment.</param>
    /// <returns>The path; empty when there is nothing to draw.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><see cref="TextOptions.Path"/> has no length, or a run is null.</exception>
    /// <exception cref="ImageLimitException">The text is too long, or the outlines exceed the path size limit.</exception>
    /// <exception cref="FontFormatException">The font's glyph data is malformed (found lazily, when first needed).</exception>
    public static Path GeneratePath(string text, TextOptions options)
    {
        List<TextPart> parts = GenerateParts(text, options, color: false);
        if (parts.Count == 1 && (parts[0].InheritBrush || parts[0].Brush is not null))
        {
            return parts[0].Path;
        }

        var builder = new PathBuilder();
        foreach (TextPart part in parts)
        {
            // A decoration stroked with its own pen is a line, not an outline to fill.
            if (part.InheritBrush || part.Brush is not null)
            {
                builder.AddPath(part.Path);
            }
        }

        return builder.Build();
    }

    internal static List<TextPart> GenerateParts(string text, TextOptions options, bool color = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        PathRoute? route = options.Path is { } p ? PathRoute.Create(p) : null;
        return TextLayout.Create(text, options).BuildParts(route, color && (options.ColorFontSupport & ColorFontSupport.MicrosoftColrFormat) != 0 ? options.ColorPaletteIndex : -1);
    }
}
