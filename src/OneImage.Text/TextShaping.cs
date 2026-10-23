using System.Buffers;
using System.Numerics;
using OneImage.Drawing;
using OneImage.Text.Fonts;
using OneImage.Text.Shaping;
using OneImage.Text.Unicode;
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

    internal ShapedCluster(Font font, int start, int length, float advance, bool missing, ShapedGlyphRef[] glyphs)
    {
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

        paragraphLevel = 0;
        int length = text.Length;
        var result = new byte[length];
        if (length == 0)
        {
            return result;
        }

        int[] cps = ArrayPool<int>.Shared.Rent(length);
        byte[] lev = ArrayPool<byte>.Shared.Rent(length);
        int[] at = ArrayPool<int>.Shared.Rent(length);
        try
        {
            int n = 0;
            for (int i = 0; i < length; n++)
            {
                at[n] = i;
                if (char.IsHighSurrogate(text[i]) && i + 1 < length && char.IsLowSurrogate(text[i + 1]))
                {
                    cps[n] = char.ConvertToUtf32(text[i], text[i + 1]);
                    i += 2;
                }
                else
                {
                    cps[n] = char.IsSurrogate(text[i]) ? 0xFFFD : text[i];
                    i++;
                }
            }

            int request = direction switch { TextDirection.LeftToRight => 0, TextDirection.RightToLeft => 1, _ => -1 };
            paragraphLevel = Bidi.ResolveLevels(cps.AsSpan(0, n), request, lev.AsSpan(0, n), lineEndRule: false);
            byte prev = (byte)paragraphLevel;
            for (int k = 0; k < n; k++)
            {
                byte l = lev[k] == Bidi.Removed ? prev : lev[k];
                prev = l;
                int to = k + 1 < n ? at[k + 1] : length;
                for (int j = at[k]; j < to; j++)
                {
                    result[j] = l;
                }
            }

            return result;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(cps);
            ArrayPool<byte>.Shared.Return(lev);
            ArrayPool<int>.Shared.Return(at);
        }
    }

    /// <summary>
    /// Gives the display order of items that have embedding levels (Unicode Bidirectional Algorithm, rule L2): from the highest level down to
    /// the lowest odd one, every sequence of items at that level or above is reversed. Pass one level per cluster to order clusters.
    /// </summary>
    /// <param name="levels">The levels in logical order, for example the level of each cluster's first character.</param>
    /// <returns>The logical indices in left-to-right display order; every index appears once.</returns>
    public static int[] GetVisualOrder(ReadOnlySpan<byte> levels)
    {
        var order = new int[levels.Length];
        int shown = Bidi.VisualOrder(levels, order);
        return shown == order.Length ? order : order.AsSpan(0, shown).ToArray();
    }

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
        uint language = Languages.ToTag(options?.Language);
        var settings = new List<(uint, int)>();
        if (options is not null)
        {
            if (options.Kerning != KerningMode.Normal)
            {
                settings.Add((Tag.Kern, 0));
            }

            foreach (FontFeature f in options.Features)
            {
                if (f.Tag is not null)
                {
                    settings.Add((Be.Tag(f.Tag), f.Value));
                }
            }
        }

        FeatureSet features = FeatureSet.Create(settings);

        // Code points with the UTF-16 offset of the grapheme cluster each belongs to: marks and the like shape with their base.
        byte[] starts = ArrayPool<byte>.Shared.Rent(length + 1);
        int[] cps = ArrayPool<int>.Shared.Rent(length);
        int[] cl = ArrayPool<int>.Shared.Rent(length);
        try
        {
            Graphemes.Fill(text, starts);
            int n = 0;
            int cluster = 0;
            for (int i = 0; i < length; n++)
            {
                if (starts[i] != 0)
                {
                    cluster = i;
                }

                int cp;
                if (char.IsHighSurrogate(text[i]) && i + 1 < length && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i += 2;
                }
                else
                {
                    cp = char.IsSurrogate(text[i]) ? 0xFFFD : text[i];
                    i++;
                }

                cps[n] = cp;
                cl[n] = cluster;
            }

            var run = new List<ShapedRunGlyph>(n + 4);
            var pending = new List<(int Start, float Advance, bool Missing, ShapedGlyphRef[] Glyphs)>();
            TextShaping_Shape(face, cps.AsSpan(0, n), cl.AsSpan(0, n), language, features, run, rightToLeft);

            float scale = font.Size / face.UnitsPerEm;
            int g = 0;
            while (g < run.Count)
            {
                int c = run[g].Cluster;
                int e = g;
                while (e < run.Count && run[e].Cluster == c)
                {
                    e++;
                }

                var refs = new ShapedGlyphRef[e - g];
                int advance = 0;
                bool missing = false;
                for (int k = g; k < e; k++)
                {
                    ShapedRunGlyph r = run[k];
                    refs[k - g] = new ShapedGlyphRef(r.GlyphId, r.XAdvance, r.XOffset, r.YOffset);
                    advance += r.XAdvance;
                    missing |= r.GlyphId == 0 && !IsInvisible(r.CodePoint);
                }

                pending.Add((c, advance * scale, missing, refs));
                g = e;
            }

            // Lengths run to the next cluster's start (the last to the end of the text); clusters come out in logical order.
            pending.Sort(static (x, y) => x.Start.CompareTo(y.Start));
            for (int k = 0; k < pending.Count; k++)
            {
                int end = k + 1 < pending.Count ? pending[k + 1].Start : length;
                clusters.Add(new ShapedCluster(font, pending[k].Start, Math.Max(end - pending[k].Start, 1), pending[k].Advance, pending[k].Missing, pending[k].Glyphs));
            }

            return clusters;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(starts);
            ArrayPool<int>.Shared.Return(cps);
            ArrayPool<int>.Shared.Return(cl);
        }
    }

    private static void TextShaping_Shape(FontFace face, ReadOnlySpan<int> cps, ReadOnlySpan<int> clusters, uint language, FeatureSet features, List<ShapedRunGlyph> output, bool rtl) =>
        Shaping.TextShaper.Shape(face, cps, clusters, language, features, output, rtl);

    private static bool IsInvisible(int cp) =>
        cp is >= 0x200B and <= 0x200F or 0x2060 or 0xFEFF or 0x00AD or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or < 0x20;
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
