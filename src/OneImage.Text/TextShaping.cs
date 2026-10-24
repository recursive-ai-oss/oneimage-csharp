using System.Numerics;
using OneImage.Drawing;
using OneImage.Text.Fonts;
using OneImage.Text.Unicode;
using OneFontShaping = global::OneFont.TextShaping;
using OneShapedClusterRange = global::OneFont.ShapedClusterRange;
using OneShapedGlyph = global::OneFont.ShapedGlyph;
using OneShapedRun = global::OneFont.ShapedRun;
using Path = OneImage.Drawing.Path;

namespace OneImage.Text;

/// <summary>Options for <see cref="TextShaping.ShapeRun"/>.</summary>
public sealed class ShapingOptions
{
    /// <summary>
    /// Gets or sets the BCP 47 language tag the font's language-specific features are chosen for (for example <c>tr</c> or <c>sr</c>).
    /// <see langword="null"/> (the default) selects the font's default language system.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets whether pair kerning is applied. The default is <see cref="KerningMode.Normal"/>.</summary>
    public KerningMode Kerning { get; set; }

    /// <summary>
    /// Gets the OpenType feature settings applied on top of the script's default features (<see cref="FontFeature"/> with a four-letter tag
    /// and a value; 0 switches a feature off). The list is empty by default.
    /// </summary>
    public IList<FontFeature> Features { get; } = [];

    /// <summary>
    /// Gets or sets whether the run is shaped to stand upright in a vertical column: pair kerning is off and the font's <c>vert</c> and
    /// <c>vrt2</c> forms are on, as in the vertical <see cref="LayoutMode"/>s. <see cref="Features"/> are applied after these and win. The
    /// default is <see langword="false"/>. Clusters report <see cref="ShapedCluster.VerticalAdvance"/> and
    /// <see cref="ShapedCluster.GetUprightOutline"/> either way; this only changes which glyphs are chosen.
    /// </summary>
    public bool Vertical { get; set; }
}

/// <summary>
/// One shaped cluster of a run: the glyphs that stand for one or more characters and cannot be positioned apart (a letter with its
/// combining marks, a ligature, a syllable). It knows its advance and outline, so a caller that lays text out by itself (SVG text with
/// a position for every character, text along a path) can place each cluster separately.
/// </summary>
public sealed class ShapedCluster
{
    private readonly Font _font;
    private readonly ShapedGlyphRef[] _glyphs;
    private Path? _outline;
    private Path? _upright;
    private readonly int _codePoint;

    internal ShapedCluster(Font font, int start, int length, float advance, bool missing, ShapedGlyphRef[] glyphs, int codePoint)
    {
        _codePoint = codePoint;
        _font = font;
        Start = start;
        Length = length;
        Advance = advance;
        HasMissingGlyph = missing;
        _glyphs = glyphs;
    }

    /// <summary>Gets the offset of the cluster's first character in the shaped text, in UTF-16 code units.</summary>
    public int Start { get; }

    /// <summary>Gets how many UTF-16 code units of the text the cluster covers (at least 1).</summary>
    public int Length { get; }

    /// <summary>
    /// Gets the horizontal distance from the cluster's pen position to the next one, in pixels at the font's size (the font size is taken
    /// as pixels per em, not points). It includes pair kerning with the cluster that follows in logical order.
    /// </summary>
    public float Advance { get; }

    /// <summary>
    /// Gets the distance the cluster takes up in a vertical column when it stands upright, in pixels at the font's size: the sum of its
    /// glyphs' <c>vmtx</c> advance heights, or the font's line height for a font without vertical metrics.
    /// </summary>
    public float VerticalAdvance
    {
        get
        {
            FontFace face = _font.Face.Data;
            float s = _font.Size / face.UnitsPerEm;
            if (face.Vertical is not { } v)
            {
                return _font.Metrics.LineHeight * s;
            }

            int sum = 0;
            foreach (ShapedGlyphRef g in _glyphs)
            {
                sum += v.GetAdvance(g.GlyphId);
            }

            return sum * s;
        }
    }

    /// <summary>Gets whether any glyph of the cluster is the font's missing-glyph box because the font lacks the character.</summary>
    public bool HasMissingGlyph { get; }

    /// <summary>
    /// Gets the outline of the cluster with its pen position at (0, 0) on the baseline and y pointing down, in pixels at the font's size,
    /// unhinted, with the glyphs' mark and cursive offsets applied. The path is empty for clusters without ink such as spaces. The same
    /// instance is returned on every call; do not modify what you derive from it without copying.
    /// </summary>
    /// <exception cref="FontFormatException">The glyph data is malformed.</exception>
    public Path GetOutline()
    {
        Path? o = Volatile.Read(ref _outline);
        if (o is not null)
        {
            return o;
        }

        FontFace face = _font.Face.Data;
        float s = _font.Size / face.UnitsPerEm;
        var b = new PathBuilder();
        int pen = 0;
        foreach (ShapedGlyphRef g in _glyphs)
        {
            Outline ol = face.GetOutline(g.GlyphId);
            GlyphPaths.Append(b, ol, new Matrix3x2(s, 0, 0, -s, (pen + g.XOffset) * s, -g.YOffset * s));
            pen += g.XAdvance;
        }

        o = b.Build();
        Volatile.Write(ref _outline, o);
        return o;
    }

    /// <summary>
    /// Gets the outline of the cluster standing upright in a vertical column, in pixels at the font's size, unhinted, y pointing down. The
    /// origin is the middle of the column at the top of the cluster's vertical cell, so the cluster occupies y from 0 to
    /// <see cref="VerticalAdvance"/>. Ideographs, kana and other characters that stand upright by nature (Unicode Vertical_Orientation U
    /// and Tu) are centered by their em square; the rest are centered by their ink. The glyph sits on the baseline the font's <c>VORG</c>
    /// table or top side bearing gives, else one ascender below the top of the cell. The same instance is returned on every call.
    /// </summary>
    /// <exception cref="FontFormatException">The glyph data is malformed.</exception>
    public Path GetUprightOutline()
    {
        Path? o = Volatile.Read(ref _upright);
        if (o is not null)
        {
            return o;
        }

        FontFace face = _font.Face.Data;
        float s = _font.Size / face.UnitsPerEm;
        var b = new PathBuilder();
        float yAdvance = 0;
        foreach (ShapedGlyphRef g in _glyphs)
        {
            Outline ol = face.GetOutline(g.GlyphId);
            (float ax, _, float bx, float by) = ol.IsEmpty ? default : ol.TightBox();
            VerticalOrientationClass cls = VerticalOrientation.Of(_codePoint);
            float dx = ol.IsEmpty || cls is VerticalOrientationClass.U or VerticalOrientationClass.Tu
                ? -face.UnitsPerEm * s / 2
                : (-((bx - ax) * s) / 2) - (ax * s);
            float down = (face.Vertical is { } v ? v.GetOriginY(g.GlyphId, (int)MathF.Round(by)) : _font.Metrics.Ascender) * s;
            GlyphPaths.Append(b, ol, new Matrix3x2(s, 0, 0, -s, dx + (g.XOffset * s), yAdvance + down - (g.YOffset * s)));
            yAdvance += face.Vertical is { } vm ? vm.GetAdvance(g.GlyphId) * s : 0;
        }

        o = b.Build();
        Volatile.Write(ref _upright, o);
        return o;
    }
}

internal readonly record struct ShapedGlyphRef(int GlyphId, int XAdvance, int XOffset, int YOffset);

/// <summary>
/// Shaping and bidirectional ordering for callers that do their own line layout. <see cref="TextBuilder"/> and <see cref="TextMeasurer"/> lay out
/// whole paragraphs; this class exposes the two steps underneath them: the Unicode bidirectional algorithm's levels and display order, and the
/// shaping of one run of text in one font and one direction into clusters.
/// </summary>
public static class TextShaping
{
    /// <summary>The longest text the bidi and shaping helpers take, in UTF-16 code units.</summary>
    public const int MaxRunLength = 1 << 24;

    /// <summary>
    /// Resolves the embedding level of every UTF-16 code unit of <paramref name="text"/> as one paragraph (Unicode Bidirectional Algorithm,
    /// rules P2 to I2). Level 0 is left-to-right, 1 right-to-left, higher levels are nested embeddings; the second half of a surrogate pair
    /// has its first half's level, and a character the algorithm removes (an embedding control) takes the level of the one before it.
    /// </summary>
    /// <param name="text">The paragraph.</param>
    /// <param name="direction">The paragraph direction; <see cref="TextDirection.Auto"/> takes it from the first strong character.</param>
    /// <param name="paragraphLevel">Receives the paragraph's base level, 0 or 1.</param>
    /// <returns>One level per UTF-16 code unit. Empty text gives an empty array.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The text is longer than <see cref="MaxRunLength"/>.</exception>
    public static byte[] GetBidiLevels(string text, TextDirection direction, out int paragraphLevel)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxRunLength)
        {
            throw new ArgumentException("The text is too long.", nameof(text));
        }

        return OneFontShaping.GetBidiLevels(text, ToOneFont(direction), out paragraphLevel);
    }

    private static global::OneFont.TextDirection ToOneFont(TextDirection d) => d switch
    {
        TextDirection.LeftToRight => global::OneFont.TextDirection.LeftToRight,
        TextDirection.RightToLeft => global::OneFont.TextDirection.RightToLeft,
        _ => global::OneFont.TextDirection.Auto,
    };

    /// <summary>
    /// Gives the display order of items that have embedding levels (Unicode Bidirectional Algorithm, rule L2): from the highest level down to
    /// the lowest odd one, every sequence of items at that level or above is reversed. Pass one level per cluster to order clusters.
    /// </summary>
    /// <param name="levels">The levels in logical order, for example the level of each cluster's first character.</param>
    /// <returns>The logical indices in left-to-right display order; every index appears once.</returns>
    public static int[] GetVisualOrder(ReadOnlySpan<byte> levels)
    {
        return OneFontShaping.GetVisualOrder(levels);
    }

    /// <summary>
    /// Returns whether a character stands upright in vertical text set in mixed orientation: Unicode Vertical_Orientation (UAX #50) U or
    /// Tu, which covers ideographs, kana, Hangul and the like. Everything else (Latin letters, digits, Arabic, most punctuation) lies
    /// rotated a quarter turn clockwise. <c>Tr</c> counts as rotated.
    /// </summary>
    /// <param name="codePoint">The Unicode scalar value.</param>
    public static bool IsUprightInVerticalText(int codePoint) =>
        VerticalOrientation.Of(codePoint) is VerticalOrientationClass.U or VerticalOrientationClass.Tu;

    /// <summary>
    /// Shapes one run of text, set in one font in one direction, into clusters in logical order. The run is split into stretches of one
    /// script and each is shaped with that script's rules (Arabic joining, Indic and Southeast Asian syllables, ligatures, kerning, marks).
    /// Characters the font lacks give clusters with <see cref="ShapedCluster.HasMissingGlyph"/>; choosing a fallback font is up to the caller.
    /// </summary>
    /// <param name="text">The run. Lone surrogates are shaped as U+FFFD.</param>
    /// <param name="font">The font; its size is pixels per em.</param>
    /// <param name="rightToLeft">Whether the run is right to left (all of it at one odd level).</param>
    /// <param name="options">Language, kerning and features; the defaults when <see langword="null"/>.</param>
    /// <returns>The clusters in logical order, covering the text without gaps. Empty text gives an empty list.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="font"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The text is longer than <see cref="MaxRunLength"/>.</exception>
    /// <exception cref="FontFormatException">The font data is malformed.</exception>
    public static IReadOnlyList<ShapedCluster> ShapeRun(ReadOnlySpan<char> text, Font font, bool rightToLeft = false, ShapingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (text.Length > MaxRunLength)
        {
            throw new ArgumentException("The text is too long.", nameof(text));
        }

        var clusters = new List<ShapedCluster>();
        int length = text.Length;
        if (length == 0)
        {
            return clusters;
        }

        FontFace face = font.Face.Data;
        OneShapedRun run = Scratch ??= new OneShapedRun();
        try
        {
            OneFontShaping.ShapeRun(text, face.Inner, run, rightToLeft, FontBridge.Options(options?.Language, options?.Kerning ?? KerningMode.Normal, options?.Features, options?.Vertical ?? false));
            float scale = font.Size / face.UnitsPerEm;
            ReadOnlySpan<OneShapedGlyph> all = run.Glyphs;
            foreach (OneShapedClusterRange r in run.Clusters)
            {
                ReadOnlySpan<OneShapedGlyph> glyphs = all.Slice(r.GlyphStart, r.GlyphCount);
                var refs = new ShapedGlyphRef[glyphs.Length];
                int advance = r.Advance;
                bool missing = r.HasMissingGlyph;
                for (int k = 0; k < glyphs.Length; k++)
                {
                    OneShapedGlyph g = glyphs[k];
                    if ((uint)g.GlyphId >= (uint)face.NumGlyphs)
                    {
                        // A damaged substitution table named a glyph the font does not have: it is the missing glyph.
                        int notdef = g.XAdvance - face.GetAdvance(g.GlyphId) + face.GetAdvance(0);
                        advance += notdef - g.XAdvance;
                        g = g with { GlyphId = 0, XAdvance = notdef };
                        missing = true;
                    }

                    refs[k] = new ShapedGlyphRef(g.GlyphId, g.XAdvance, g.XOffset, g.YOffset);
                }

                int first = r.Start;
                int cp0 = char.IsHighSurrogate(text[first]) && first + 1 < length && char.IsLowSurrogate(text[first + 1]) ? char.ConvertToUtf32(text[first], text[first + 1]) : text[first];
                clusters.Add(new ShapedCluster(font, first, Math.Max(r.Length, 1), advance * scale, missing, refs, cp0));
            }

            return clusters;
        }
        catch (global::OneFont.FontFormatException ex)
        {
            throw new FontFormatException(ex.Message);
        }
        finally
        {
            run.Clear();
        }
    }

    [ThreadStatic]
    private static OneShapedRun? Scratch;
}

/// <summary>Reads descriptive properties of a font's face that <see cref="Font"/> does not expose.</summary>
public static class FontFaceInfo
{
    /// <summary>Gets the face's weight class (OS/2 <c>usWeightClass</c>, 100 to 900; 400 regular, 700 bold; 400 or 700 when the font has no OS/2 table).</summary>
    /// <param name="font">The font.</param>
    /// <returns>The weight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="font"/> is <see langword="null"/>.</exception>
    public static int GetWeight(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);
        return font.Face.Data.WeightClass;
    }

    /// <summary>Gets how far the font recommends raising superscript text above the baseline (OS/2 <c>ySuperscriptYOffset</c>), in pixels at the font's size.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The raise; 0 when the font has no OS/2 table or recommends none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="font"/> is <see langword="null"/>.</exception>
    public static float GetSuperscriptRaise(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);
        FontFace f = font.Face.Data;
        return f.SuperscriptYOffset * font.Size / f.UnitsPerEm;
    }

    /// <summary>Gets how far the font recommends lowering subscript text below the baseline (OS/2 <c>ySubscriptYOffset</c>), in pixels at the font's size.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The drop as a positive number; 0 when the font has no OS/2 table or recommends none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="font"/> is <see langword="null"/>.</exception>
    public static float GetSubscriptDrop(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);
        FontFace f = font.Face.Data;
        return f.SubscriptYOffset * font.Size / f.UnitsPerEm;
    }

    /// <summary>Gets whether the face is italic or oblique.</summary>
    /// <param name="font">The font.</param>
    /// <returns><see langword="true"/> for an italic or oblique face.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="font"/> is <see langword="null"/>.</exception>
    public static bool GetIsItalic(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);
        return font.Face.Data.IsItalic;
    }
}
