namespace OneImage.Text;

/// <summary>
/// Vertical and general metrics of a face, in font design units (divide by <see cref="UnitsPerEm"/> and multiply by the size in
/// pixels to scale). Read-only.
/// </summary>
public sealed class FontMetrics
{
    internal FontMetrics(global::OneFont.FontMetrics m)
    {
        UnitsPerEm = m.UnitsPerEm;
        Ascender = m.Ascender;
        Descender = m.Descender;
        LineGap = m.LineGap;
        UnderlinePosition = m.UnderlinePosition;
        UnderlineThickness = m.UnderlineThickness;
        StrikeoutPosition = m.StrikeoutPosition;
        StrikeoutSize = m.StrikeoutSize;
        XHeight = m.XHeight;
        CapHeight = m.CapHeight;
        GlyphCount = m.GlyphCount;
    }

    /// <summary>Gets the number of design units per em square.</summary>
    public int UnitsPerEm { get; }

    /// <summary>Gets the distance from the baseline to the top of the line, in design units. Positive.</summary>
    public int Ascender { get; }

    /// <summary>Gets the distance from the baseline to the bottom of the line, in design units. Negative (below the baseline).</summary>
    public int Descender { get; }

    /// <summary>Gets the extra spacing the font asks for between lines, in design units.</summary>
    public int LineGap { get; }

    /// <summary>Gets the natural line height: <see cref="Ascender"/> minus <see cref="Descender"/> plus <see cref="LineGap"/>, in design units.</summary>
    public int LineHeight => Ascender - Descender + LineGap;

    /// <summary>Gets the underline position relative to the baseline (negative is below), in design units.</summary>
    public int UnderlinePosition { get; }

    /// <summary>Gets the underline thickness, in design units.</summary>
    public int UnderlineThickness { get; }

    /// <summary>Gets the strikeout position relative to the baseline, in design units.</summary>
    public int StrikeoutPosition { get; }

    /// <summary>Gets the strikeout thickness, in design units.</summary>
    public int StrikeoutSize { get; }

    /// <summary>Gets the height of lowercase x in design units, or 0 when the font does not say.</summary>
    public int XHeight { get; }

    /// <summary>Gets the height of capital letters in design units, or 0 when the font does not say.</summary>
    public int CapHeight { get; }

    /// <summary>Gets the number of glyphs in the face.</summary>
    public int GlyphCount { get; }
}
