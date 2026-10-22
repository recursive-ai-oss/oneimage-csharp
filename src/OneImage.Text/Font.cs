using System.Numerics;
using OneImage.Drawing;
using OneImage.Text.Fonts;
using Path = OneImage.Drawing.Path;

namespace OneImage.Text;

/// <summary>A face of a <see cref="FontFamily"/> at a size. Immutable and thread-safe; cheap to create.</summary>
public sealed class Font
{
    private readonly FontMetrics _metrics;

    /// <summary>Creates a font from a family. Same as <see cref="FontFamily.CreateFont(float, FontStyle)"/>.</summary>
    /// <param name="family">The family.</param>
    /// <param name="size">The size in points; at the default 72 dpi one point is one pixel.</param>
    /// <param name="style">The style. When the family has no such face the closest one is used and <see cref="Style"/> reports what was chosen.</param>
    /// <exception cref="ArgumentNullException"><paramref name="family"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not a positive finite number.</exception>
    public Font(FontFamily family, float size, FontStyle style = FontStyle.Regular)
    {
        ArgumentNullException.ThrowIfNull(family);
        if (!(size > 0) || !float.IsFinite(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The font size must be a positive finite number.");
        }

        Family = family;
        Size = size;
        Face = family.Select(style, out FontStyle actual);
        Style = actual;
        _metrics = Face.Metrics;
    }

    /// <summary>Gets the family.</summary>
    public FontFamily Family { get; }

    /// <summary>Gets the size in points. Pixels = points × <see cref="TextOptions.Dpi"/> / 72.</summary>
    public float Size { get; }

    /// <summary>Gets the style of the face in use.</summary>
    public FontStyle Style { get; }

    /// <summary>Gets the face's full name from its name table, or the family and style when it has none.</summary>
    public string Name => Face.Data.FullName ?? (Family.Name + " " + Face.Data.SubfamilyName);

    /// <summary>Gets the face's metrics in design units.</summary>
    public FontMetrics Metrics => _metrics;

    /// <summary>Gets the number of color palettes (CPAL) the face has; 0 when it has no color glyphs.</summary>
    public int ColorPaletteCount => Face.Data.Color?.PaletteCount ?? 0;

    /// <summary>Tells whether the face draws a code point as layered color glyphs (a COLR version 0 base glyph).</summary>
    /// <param name="codePoint">The Unicode code point.</param>
    /// <returns><see langword="true"/> when the code point maps to a glyph with color layers.</returns>
    public bool HasColorGlyph(int codePoint) =>
        Face.Data.Color is { } c && c.TryGetLayers(Face.Data.GetGlyphIndex(codePoint), out _, out _);

    internal FaceEntry Face { get; }

    /// <summary>Returns the same face at another size.</summary>
    /// <param name="size">The size in points.</param>
    /// <returns>A new font.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not a positive finite number.</exception>
    public Font WithSize(float size) => new(Family, size, Style);

    /// <summary>Tells whether the face maps a code point to a glyph.</summary>
    /// <param name="codePoint">A Unicode scalar value.</param>
    /// <returns><see langword="true"/> when the face has a glyph for it (not the missing-glyph box).</returns>
    public bool HasGlyph(int codePoint) => Face.Data.GetGlyphIndex(codePoint) != 0;

    /// <summary>Gets the horizontal advance of a code point's glyph, unkerned, in pixels at 72 dpi.</summary>
    /// <param name="codePoint">A Unicode scalar value.</param>
    /// <returns>The advance; the missing-glyph advance when the face has no glyph.</returns>
    public float GetAdvance(int codePoint)
    {
        FontFace f = Face.Data;
        return f.GetAdvance(f.GetGlyphIndex(codePoint)) * Size / f.UnitsPerEm;
    }

    /// <summary>
    /// Gets the outline of a code point's glyph in pixels at 72 dpi, unhinted, positioned with the pen origin at (0, 0) on the
    /// baseline and y pointing down.
    /// </summary>
    /// <param name="codePoint">A Unicode scalar value.</param>
    /// <returns>The outline; empty for glyphs without one, such as the space. A code point the face lacks gives the missing-glyph outline.</returns>
    /// <exception cref="FontFormatException">The glyph data is malformed.</exception>
    public Path GetGlyphPath(int codePoint)
    {
        FontFace f = Face.Data;
        Outline o = f.GetOutline(f.GetGlyphIndex(codePoint));
        var b = new PathBuilder();
        float s = Size / f.UnitsPerEm;
        GlyphPaths.Append(b, o, new Matrix3x2(s, 0, 0, -s, 0, 0));
        return b.Build();
    }
}
