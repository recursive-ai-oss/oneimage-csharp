using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using OneImage.Drawing;
using OneImage.Text.Fonts;
using OneImage.Text.Shaping;
using OneImage.Text.Unicode;

using Path = OneImage.Drawing.Path;

namespace OneImage.Text.Layout;

/// <summary>One shaped glyph. Advances are in pixels and already include kerning with the next glyph.</summary>
internal struct ShapedGlyph
{
    public int GlyphId;
    public int CodePoint;
    public int Cluster;
    public int Length;
    public float Advance;

    /// <summary>Where the glyph is drawn relative to its pen position, in pixels, y up (marks, cursive joins, super- and subscript shifts).</summary>
    public float XOffset;
    public float YOffset;
    public bool IsSpace;

    /// <summary>The bidirectional embedding level of the glyph's character (0 when the paragraph has no right-to-left text and is not right to left).</summary>
    public byte Level;

    /// <summary>Index into <see cref="TextLayout.Styles"/>: the face, scale and run the glyph is set in.</summary>
    public int Style;
}

/// <summary>The face, scale and run shared by a stretch of glyphs, with the line and decoration measures derived from them once.</summary>
internal sealed class GlyphStyle
{
    public FontFace Face = null!;
    public int Run;
    public TextDecorations Decorations;

    /// <summary>Pixels per font unit across and down (they differ for super- and subscript).</summary>
    public float Sx;
    public float Sy;

    /// <summary>Raise (positive) or lowering of the baseline for super- and subscript, in pixels.</summary>
    public float Shift;

    /// <summary>The line pitch this style asks for and the part of it above the baseline, in pixels.</summary>
    public float Pitch;
    public float Ascent;
    public float TabAdvance;

    /// <summary>Centers of the decoration lines above the baseline, and their thicknesses, in pixels.</summary>
    public float UnderlineOffset;
    public float StrikeoutOffset;
    public float OverlineOffset;
    public float UnderlineThickness;
    public float StrikeoutThickness;
}

/// <summary>A piece of the output that is painted as one: glyph outlines and filled decorations of runs with the same paint.</summary>
internal sealed class TextPart
{
    public Path Path = null!;
    public Brush? Brush;
    public Pen? Pen;

    /// <summary>Whether a null brush or pen means the one given to the drawing call.</summary>
    public bool InheritBrush;
    public bool InheritPen;
}

/// <summary>One line of the result: a range of glyphs plus the text range it came from.</summary>
internal sealed class LayoutLine
{
    public int GlyphStart;
    public int GlyphEnd;
    public int TextStart;
    public int TextLength;

    /// <summary>Width in pixels without trailing spaces.</summary>
    public float Width;

    /// <summary>The line's height (the largest pitch of its glyphs) and the part of it above the baseline, in pixels.</summary>
    public float Height;
    public float Ascent;

    /// <summary>Absolute x of the line start, in pixels.</summary>
    public float Left;

    /// <summary>Absolute y of the baseline, in pixels.</summary>
    public float Baseline;

    /// <summary>Whether the line belongs to a right-to-left paragraph.</summary>
    public bool Rtl;

    /// <summary>
    /// The glyphs without the white space that hangs past the line's end: it ends the line in reading order, so it sits at the right
    /// of a left-to-right line and at the left of a right-to-left one. Equal to the whole range for a line of only white space.
    /// </summary>
    public int VisStart;
    public int VisEnd;

    /// <summary>Advance of the hanging white space at the left of a right-to-left line, which <see cref="Left"/> already allows for.</summary>
    public float Hang;
}

/// <summary>
/// Lays text out: runs and font fallback, OpenType shaping, Unicode line breaking, greedy line filling (with the word breaking
/// modes), justification and alignment. Line breaking works on the text and reaches the glyphs only through each glyph's text
/// offset, so a different shaping step does not change it. Glyph metrics are pixels: every glyph carries the scale of the face and
/// run it was shaped in, so lines can mix faces and sizes.
/// </summary>
internal sealed class TextLayout
{
    public const int MaxTextLength = 1 << 22;

    private readonly string _text;
    private readonly float _dpi;
    private readonly float _wrap;
    private readonly float _lineSpacing;
    private readonly float _tabWidth;
    private readonly FeatureSet _features;
    private readonly uint _language;
    private readonly WordBreaking _wordBreaking;
    private readonly TextJustification _justification;
    private readonly Font _baseFont;
    private readonly IList<FontFamily> _fallbacks;
    private readonly TextDirection _direction;

    // Bidirectional data of the paragraph being laid out: the level of each UTF-16 offset from its start (null when the paragraph is
    // plain left to right and nothing needs reordering), and the paragraph level.
    private byte[]? _levels;
    private int _paraLevel;

    // Runs: the run that owns each UTF-16 offset (-1 for none), rented while the layout is built.
    private RunInfo[] _runs = [];
    private int[]? _runOf;
    private readonly Dictionary<Font, FaceEntry[]> _faces = [];
    private readonly Dictionary<long, int> _styleIndex = [];

    // Break data of the paragraph being laid out, indexed by UTF-16 offset from its start (rented, returned after each paragraph).
    private byte[] _breaks = [];
    private byte[] _clusters = [];
    private int _paragraphStart;
    private int _firstGlyph = -1;

    private TextLayout(string text, TextOptions o)
    {
        _text = text;
        _baseFont = o.Font;
        _dpi = o.Dpi;
        _wrap = o.WrappingLength;
        _lineSpacing = o.LineSpacing;
        _tabWidth = o.TabWidth;
        _language = Languages.ToTag(o.Language);
        var settings = new List<(uint, int)>();
        if (o.Kerning != KerningMode.Normal)
        {
            settings.Add((Tag.Kern, 0));
        }

        foreach (FontFeature f in o.Features)
        {
            if (f.Tag is not null)
            {
                settings.Add((Fonts.Be.Tag(f.Tag), f.Value));
            }
        }

        _features = FeatureSet.Create(settings);
        _direction = o.TextDirection;
        _wordBreaking = o.WordBreaking;
        _justification = o.TextJustification;
        _fallbacks = o.FallbackFontFamilies;
        if (_fallbacks.Count > TextOptions.MaxFallbackFamilies)
        {
            throw new ImageLimitException($"More than {TextOptions.MaxFallbackFamilies} fallback font families.");
        }

        foreach (FontFamily? f in _fallbacks)
        {
            ArgumentNullException.ThrowIfNull(f, "FallbackFontFamilies");
        }
    }

    public List<GlyphStyle> Styles { get; } = [];

    public List<ShapedGlyph> Glyphs { get; } = [];

    public List<LayoutLine> Lines { get; } = [];

    /// <summary>Whether each glyph starts a grapheme cluster (false for combining marks and other continuations).</summary>
    public List<bool> ClusterStart { get; } = [];

    /// <summary>Pen x of each glyph in pixels from its line's start.</summary>
    public List<float> PenX { get; } = [];

    /// <summary>The widest line, in pixels.</summary>
    public float MaxWidth { get; private set; }

    /// <summary>The height of all lines together, in pixels.</summary>
    public float TotalHeight { get; private set; }

    public static TextLayout Create(string text, TextOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        if (text.Length > MaxTextLength)
        {
            throw new ImageLimitException("The text is longer than the supported maximum.");
        }

        var layout = new TextLayout(text, options);
        try
        {
            layout.ResolveRuns(options);
            int pos = 0;
            while (text.Length > 0)
            {
                int end = FindBreak(text, pos, out int termLen);
                layout.AddParagraph(text, pos, end);
                if (end >= text.Length)
                {
                    break;
                }

                pos = end + termLen;
                if (pos >= text.Length)
                {
                    // A line break at the very end does not open another line.
                    break;
                }
            }
        }
        finally
        {
            layout.ReleaseRuns();
        }

        layout.Position(options);
        return layout;
    }

    private static int FindBreak(string text, int from, out int termLen)
    {
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\v' || c == '\f' || c == '\u0085' || c == '\u2028' || c == '\u2029')
            {
                termLen = 1;
                return i;
            }

            if (c == '\r')
            {
                termLen = i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                return i;
            }
        }

        termLen = 0;
        return text.Length;
    }

    private readonly struct RunInfo(Font font, TextAttributes attributes, TextDecorations decorations, TextRun? source)
    {
        public readonly Font Font = font;
        public readonly TextAttributes Attributes = attributes;
        public readonly TextDecorations Decorations = decorations;
        public readonly TextRun? Source = source;
    }

    private RunInfo InfoOf(int run) => run < 0 ? new RunInfo(_baseFont, TextAttributes.None, TextDecorations.None, null) : _runs[run];

    // Gives every UTF-16 offset to the last run that covers it. Runs are painted from the last to the first, skipping offsets that
    // are already taken through a union-find "next free offset" array, so the work is linear in the text plus the run count however
    // much the runs overlap.
    private void ResolveRuns(TextOptions o)
    {
        IList<TextRun> runs = o.TextRuns;
        if (runs.Count == 0)
        {
            return;
        }

        if (runs.Count > TextOptions.MaxTextRuns)
        {
            throw new ImageLimitException($"More than {TextOptions.MaxTextRuns} text runs.");
        }

        _runs = new RunInfo[runs.Count];
        for (int r = 0; r < runs.Count; r++)
        {
            TextRun tr = runs[r] ?? throw new ArgumentException("TextRuns contains a null run.", nameof(o));
            _runs[r] = new RunInfo(tr.Font ?? _baseFont, tr.TextAttributes, tr.TextDecorations, tr);
        }

        int len = _text.Length;
        if (len == 0)
        {
            return;
        }

        int[] owner = ArrayPool<int>.Shared.Rent(len);
        int[] next = ArrayPool<int>.Shared.Rent(len + 1);
        _runOf = owner;
        try
        {
            Array.Fill(owner, -1, 0, len);
            for (int i = 0; i <= len; i++)
            {
                next[i] = i;
            }

            for (int r = runs.Count - 1; r >= 0; r--)
            {
                TextRun tr = runs[r];
                int end = Math.Min(tr.End, len);
                int i = Find(next, Math.Min(tr.Start, len));
                while (i < end)
                {
                    owner[i] = r;
                    next[i] = i + 1;
                    i = Find(next, i + 1);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(next);
        }
    }

    private static int Find(int[] next, int x)
    {
        int root = x;
        while (next[root] != root)
        {
            root = next[root];
        }

        while (next[x] != root)
        {
            int n = next[x];
            next[x] = root;
            x = n;
        }

        return root;
    }

    private void ReleaseRuns()
    {
        if (_runOf is not null)
        {
            ArrayPool<int>.Shared.Return(_runOf);
            _runOf = null;
        }
    }

    private int RunAt(int index) => _runOf is null ? -1 : _runOf[index];

    // The faces a font can be set in: its own, then one per fallback family (the same style, or the closest).
    private FaceEntry[] FacesOf(Font font)
    {
        if (_faces.TryGetValue(font, out FaceEntry[]? faces))
        {
            return faces;
        }

        faces = new FaceEntry[1 + _fallbacks.Count];
        faces[0] = font.Face;
        for (int i = 0; i < _fallbacks.Count; i++)
        {
            faces[i + 1] = _fallbacks[i].Select(font.Style, out _);
        }

        _faces[font] = faces;
        return faces;
    }

    private int StyleOf(int run, int face)
    {
        long key = ((long)(run + 1) << 8) | (uint)face;
        if (_styleIndex.TryGetValue(key, out int index))
        {
            return index;
        }

        RunInfo ri = InfoOf(run);
        FaceEntry entry = FacesOf(ri.Font)[face];
        FontFace f = entry.Data;
        FontMetrics m = entry.Metrics;
        int upem = f.UnitsPerEm;
        float px = ri.Font.Size * _dpi / 72f;
        float scale = px / upem;
        float sx = scale;
        float sy = scale;
        float shift = 0;
        if (ri.Attributes != TextAttributes.None)
        {
            // The OS/2 recommendations; a font without them gets the usual 65 % by 60 % at +35 % / -7.5 % of the em.
            bool sup = ri.Attributes == TextAttributes.Superscript;
            int xs = sup ? f.SuperscriptXSize : f.SubscriptXSize;
            int ys = sup ? f.SuperscriptYSize : f.SubscriptYSize;
            int yo = sup ? f.SuperscriptYOffset : f.SubscriptYOffset;
            if (xs <= 0 || ys <= 0)
            {
                xs = upem * 65 / 100;
                ys = upem * 60 / 100;
                yo = sup ? upem * 35 / 100 : upem * 75 / 1000;
            }

            sx = scale * xs / upem;
            sy = scale * ys / upem;
            shift = (sup ? yo : -yo) * sy;
        }

        // The decoration measures come from post and OS/2; a font without them gets a tenth of the em below the baseline
        // for the underline, 30 % of the em for the strikeout line, and 1/20 em thick lines.
        int ulThick = f.UnderlineThickness > 0 ? f.UnderlineThickness : upem / 20;
        int ulPos = f.UnderlinePosition != 0 ? f.UnderlinePosition : -(upem / 10);
        int stThick = f.StrikeoutSize > 0 ? f.StrikeoutSize : upem / 20;
        int stPos = f.StrikeoutPosition != 0 ? f.StrikeoutPosition : upem * 3 / 10;

        float pitch = px * (sy / scale) * _lineSpacing;
        float natural = m.LineHeight * sy;
        var st = new GlyphStyle
        {
            Face = f,
            Run = run,
            Decorations = ri.Decorations,
            Sx = sx,
            Sy = sy,
            Shift = shift,
            Pitch = pitch,

            // The font's ascent and descent are centered in the line box.
            Ascent = (m.Ascender * sy) - ((natural - pitch) / 2),
            TabAdvance = _tabWidth * f.GetAdvance(f.GetGlyphIndex(' ')) * sx,
            UnderlineOffset = shift + (ulPos * sy),
            UnderlineThickness = ulThick * sy,
            StrikeoutOffset = shift + (stPos * sy),
            StrikeoutThickness = stThick * sy,
            OverlineOffset = shift + ((upem - ulPos) * sy),
        };
        index = Styles.Count;
        Styles.Add(st);
        _styleIndex[key] = index;
        return index;
    }

    // Shaping: code points go to the OpenType shaper (character map, GSUB substitutions, GPOS positioning). A shaping run is a
    // stretch of one style (run and face), so substitutions and kerning never reach across a change of font or size. Tabs, zero width
    // non-joiners, zero width spaces and soft hyphens also end a shaping run: nothing substitutes or kerns across them. The last two
    // are break opportunities, and a ligature spanning one would swallow it.
    private void Shape(string text, int start, int end)
    {
        _pending = 0;
        _runStyle = -1;
        _runRtl = false;
        int face = 0;
        int i = start;
        while (i < end)
        {
            int cp;
            int len;
            if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                len = 2;
            }
            else
            {
                cp = char.IsSurrogate(text[i]) ? 0xFFFD : text[i];
                len = 1;
            }

            int run = RunAt(i);
            bool ignorable = (IsIgnorable(cp) && !(cp == 0x200D && JoinerMatters(text, i, start, end))) || cp == '\t';
            if (_fallbacks.Count > 0 && !ignorable && _clusters[i - start] != 0)
            {
                // The first code point of a grapheme cluster picks the face for the whole cluster.
                face = PickFace(run, cp);
            }
            else if (_fallbacks.Count == 0)
            {
                face = 0;
            }

            int style = StyleOf(run, face);

            // A shaping run keeps one direction: glyphs are substituted and positioned in reading order, and the run is turned
            // around as a whole when the line is reordered.
            bool rtl = _levels is not null && (_levels[i - start] & 1) == 1;
            if (style != _runStyle || rtl != _runRtl)
            {
                FlushRun(end);
                _runStyle = style;
                _runRtl = rtl;
            }

            if (cp == '\t')
            {
                FlushRun(end);
                GlyphStyle st = Styles[style];
                int gid = st.Face.GetGlyphIndex(' ');
                Glyphs.Add(new ShapedGlyph { GlyphId = gid, CodePoint = cp, Cluster = i, Length = len, Advance = st.TabAdvance, IsSpace = true, Style = style, Level = LevelAt(i - start) });
            }
            else if (cp is 0x200C or 0x200B or 0x00AD)
            {
                FlushRun(end);
            }
            else if (!ignorable)
            {
                if (_pending == _cps.Length)
                {
                    Array.Resize(ref _cps, _pending * 2);
                    Array.Resize(ref _runClusters, _pending * 2);
                }

                _cps[_pending] = cp;
                _runClusters[_pending++] = i;
            }

            i += len;
        }

        FlushRun(end);
    }

    // Font fallback: the first face of the chain with a glyph for the character, else the primary face (its missing-glyph box).
    private int PickFace(int run, int cp)
    {
        FaceEntry[] faces = FacesOf(InfoOf(run).Font);
        for (int k = 0; k < faces.Length; k++)
        {
            if (faces[k].Data.GetGlyphIndex(cp) != 0)
            {
                return k;
            }
        }

        return 0;
    }

    private readonly List<ShapedRunGlyph> _run = [];
    private int[] _cps = new int[64];
    private int[] _runClusters = new int[64];
    private int _pending;
    private int _runStyle = -1;
    private bool _runRtl;

    private byte LevelAt(int offset) => _levels is null ? (byte)0 : _levels[offset];

    // A zero width joiner is kept for the shaper only where it sits next to a letter that can join through it.
    private static bool JoinerMatters(string text, int i, int start, int end)
    {
        int before = 0;
        if (i > start)
        {
            before = text[i - 1];
            if (char.IsLowSurrogate(text[i - 1]) && i - 2 >= start && char.IsHighSurrogate(text[i - 2]))
            {
                before = char.ConvertToUtf32(text[i - 2], text[i - 1]);
            }
        }

        int after = 0;
        if (i + 1 < end)
        {
            after = text[i + 1];
            if (char.IsHighSurrogate(text[i + 1]) && i + 2 < end && char.IsLowSurrogate(text[i + 2]))
            {
                after = char.ConvertToUtf32(text[i + 1], text[i + 2]);
            }
        }

        return (before >= 0x600 && UnicodeBidi.GetJoiningType(before) is JoiningType.Dual or JoiningType.Left)
            || (after >= 0x600 && UnicodeBidi.GetJoiningType(after) is JoiningType.Dual or JoiningType.Right);
    }

    private void FlushRun(int paragraphEnd)
    {
        if (_pending == 0)
        {
            return;
        }

        GlyphStyle st = Styles[_runStyle];
        _run.Clear();
        TextShaper.Shape(st.Face, _cps.AsSpan(0, _pending), _runClusters.AsSpan(0, _pending), _language, _features, _run, _runRtl);
        for (int k = 0; k < _run.Count; k++)
        {
            ShapedRunGlyph r = _run[k];
            int nextCluster = k + 1 < _run.Count ? _run[k + 1].Cluster : paragraphEnd;
            Glyphs.Add(new ShapedGlyph
            {
                GlyphId = r.GlyphId,
                CodePoint = r.CodePoint,
                Cluster = r.Cluster,
                Length = Math.Max(nextCluster - r.Cluster, 1),
                Advance = r.XAdvance * st.Sx,
                XOffset = r.XOffset * st.Sx,
                YOffset = (r.YOffset * st.Sy) + st.Shift,
                IsSpace = IsBreakingSpace(r.CodePoint),
                Level = LevelAt(r.Cluster - _paragraphStart),
                Style = _runStyle,
            });
        }

        _pending = 0;
    }

    private static bool IsIgnorable(int cp) =>
        cp is >= 0x200B and <= 0x200F or 0x2060 or 0xFEFF or 0x00AD or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);

    private static bool IsBreakingSpace(int cp) =>
        cp is 0x20 or 0x09 or 0x1680 or (>= 0x2000 and <= 0x2006) or (>= 0x2008 and <= 0x200A) or 0x205F or 0x3000;

    // Line breaking: a line may end before glyph k when the text offset it starts at is a break opportunity of the Unicode line
    // breaking algorithm (as adjusted by the word breaking mode) and also starts a grapheme cluster.
    private bool CanBreakBefore(int paragraphStart, int k)
    {
        if (k <= paragraphStart)
        {
            return false;
        }

        // A glyph that continues the cluster of the one before it (a mark, or one more glyph of a multiple substitution) is no start.
        int at = Glyphs[k].Cluster - _paragraphStart;
        return _breaks[at] != 0 && ClusterStart[k];
    }

    // Computes the break opportunities and cluster starts of one paragraph into pooled arrays.
    private void PrepareBreaks(string text, int start, int end)
    {
        int length = end - start;
        _paragraphStart = start;
        _breaks = ArrayPool<byte>.Shared.Rent(length + 1);
        _clusters = ArrayPool<byte>.Shared.Rent(length + 1);
        if (length == 0)
        {
            return;
        }

        ReadOnlySpan<char> span = text.AsSpan(start, length);
        LineBreaker.Fill(span, _breaks);
        Graphemes.Fill(span, _clusters);
        switch (_wordBreaking)
        {
            case WordBreaking.BreakAll:
                // Every cluster boundary is an opportunity, except in front of white space, which stays with the word before it.
                for (int i = 1; i < length; i++)
                {
                    if (_clusters[i] != 0 && !char.IsWhiteSpace(span[i]))
                    {
                        _breaks[i] = LineBreaker.Allowed;
                    }
                }

                break;
            case WordBreaking.KeepAll:
                // No opportunity right after an East Asian wide or fullwidth character; spaces still break.
                for (int i = 1; i < length; i++)
                {
                    if (_breaks[i] == LineBreaker.Allowed)
                    {
                        int prev = span[i - 1];
                        if (char.IsLowSurrogate(span[i - 1]) && i >= 2 && char.IsHighSurrogate(span[i - 2]))
                        {
                            prev = char.ConvertToUtf32(span[i - 2], span[i - 1]);
                        }

                        if (LineBreaker.IsEastAsianWide(prev))
                        {
                            _breaks[i] = LineBreaker.None;
                        }
                    }
                }

                break;
        }
    }

    private void ReleaseBreaks()
    {
        ArrayPool<byte>.Shared.Return(_breaks);
        ArrayPool<byte>.Shared.Return(_clusters);
        _breaks = [];
        _clusters = [];
    }

    private void AddParagraph(string text, int start, int end)
    {
        int g0 = Glyphs.Count;
        PrepareBreaks(text, start, end);
        try
        {
            PrepareBidi(text, start, end);
            Shape(text, start, end);
            int g1 = Glyphs.Count;
            if (g1 == g0)
            {
                AddLine(g0, g0, start, end - start);
                return;
            }

            _firstGlyph = g0;
            for (int k = g0; k < g1; k++)
            {
                // A glyph starts a cluster when its first character does and the glyph before it is not from the same character
                // (a character the font turns into several glyphs).
                ClusterStart.Add(_clusters[Glyphs[k].Cluster - start] != 0 && (k == g0 || Glyphs[k - 1].Cluster != Glyphs[k].Cluster));
            }

            FillLines(g0, g1, start, end);
        }
        finally
        {
            ReleaseBreaks();
            ReleaseBidi();
        }
    }

    // ---- Bidirectional text (UAX #9) ----

    // Whether a paragraph can contain anything that is ordered differently from plain left to right: right-to-left letters, Arabic
    // digits, and the explicit direction controls. The ranges are conservative; the algorithm decides.
    private static bool MayNeedBidi(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c < 0x590)
            {
                continue;
            }

            if (c <= 0x8FF || (c >= 0x200E && c <= 0x200F) || (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069)
                || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF) || char.IsSurrogate(c))
            {
                // Surrogates stand for the right-to-left letters of the supplementary planes (Adlam, Hanifi Rohingya and others).
                return true;
            }
        }

        return false;
    }

    // Resolves the embedding levels of the paragraph (rules P2 to I2) into a per-UTF-16-offset table. Rules L1 and L2 are applied
    // per line once the lines are known.
    private void PrepareBidi(string text, int start, int end)
    {
        _levels = null;
        _paraLevel = 0;
        int length = end - start;
        ReadOnlySpan<char> span = text.AsSpan(start, length);
        if (length == 0 || (_direction != TextDirection.RightToLeft && !MayNeedBidi(span)))
        {
            return;
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
                if (char.IsHighSurrogate(span[i]) && i + 1 < length && char.IsLowSurrogate(span[i + 1]))
                {
                    cps[n] = char.ConvertToUtf32(span[i], span[i + 1]);
                    i += 2;
                }
                else
                {
                    cps[n] = char.IsSurrogate(span[i]) ? 0xFFFD : span[i];
                    i++;
                }
            }

            int request = _direction switch { TextDirection.LeftToRight => 0, TextDirection.RightToLeft => 1, _ => -1 };
            _paraLevel = Bidi.ResolveLevels(cps.AsSpan(0, n), request, lev.AsSpan(0, n), lineEndRule: false);
            byte[] levels = ArrayPool<byte>.Shared.Rent(length + 1);
            byte prev = (byte)_paraLevel;
            for (int k = 0; k < n; k++)
            {
                // A character removed by rule X9 (an embedding control) takes the level of the one before it.
                byte l = lev[k] == Bidi.Removed ? prev : lev[k];
                prev = l;
                int to = k + 1 < n ? at[k + 1] : length;
                for (int j = at[k]; j < to; j++)
                {
                    levels[j] = l;
                }
            }

            _levels = levels;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(cps);
            ArrayPool<byte>.Shared.Return(lev);
            ArrayPool<int>.Shared.Return(at);
        }
    }

    private void ReleaseBidi()
    {
        if (_levels is not null)
        {
            ArrayPool<byte>.Shared.Return(_levels);
            _levels = null;
        }
    }

    // Rules L1 and L2 for one line of glyphs [g0, g1): white space at the end goes back to the paragraph level, then the glyphs are put
    // in display order, from the highest level down reversing every run at that level or above. Clusters stay together because a
    // cluster's glyphs share a level; the cluster flags are recomputed for the new order (a reversed cluster starts at its last glyph).
    private void ReorderLine(int g0, int g1)
    {
        int n = g1 - g0;
        if (_levels is null || n == 0)
        {
            return;
        }

        int[] cps = ArrayPool<int>.Shared.Rent(n);
        byte[] lev = ArrayPool<byte>.Shared.Rent(n);
        int[] order = ArrayPool<int>.Shared.Rent(n);
        try
        {
            bool plain = true;
            for (int j = 0; j < n; j++)
            {
                ShapedGlyph g = Glyphs[g0 + j];
                cps[j] = g.CodePoint;
                lev[j] = g.Level;
                plain &= g.Level == 0;
            }

            if (plain)
            {
                return;
            }

            Bidi.ResetTrailingWhitespace(cps.AsSpan(0, n), lev.AsSpan(0, n), _paraLevel);
            int m = Bidi.VisualOrder(lev.AsSpan(0, n), order.AsSpan(0, n));
            bool moved = false;
            for (int j = 0; j < m && !moved; j++)
            {
                moved = order[j] != j;
            }

            if (!moved)
            {
                return;
            }

            // The cluster each glyph belongs to, by the logical index of its first glyph, before anything moves.
            int[] head = ArrayPool<int>.Shared.Rent(n);
            try
            {
                int h = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == 0 || ClusterStart[g0 + j])
                    {
                        h = j;
                    }

                    head[j] = h;
                }

                var glyphs = new ShapedGlyph[n];
                for (int j = 0; j < n; j++)
                {
                    glyphs[j] = Glyphs[g0 + j];
                }

                for (int v = 0; v < n; v++)
                {
                    Glyphs[g0 + v] = glyphs[order[v]];
                    ClusterStart[g0 + v] = v == 0 || head[order[v]] != head[order[v - 1]];
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(head);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(cps);
            ArrayPool<byte>.Shared.Return(lev);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    // Greedy line filling over the segments between break opportunities.
    private void FillLines(int g0, int g1, int start, int end)
    {
        int lineStart = g0;
        float lineW = 0; // pixels, including spaces accepted so far
        int seg = g0;
        while (seg < g1)
        {
            int segEnd = seg + 1;
            while (segEnd < g1 && !CanBreakBefore(g0, segEnd))
            {
                segEnd++;
            }

            float full = 0;
            for (int k = seg; k < segEnd; k++)
            {
                full += Glyphs[k].Advance;
            }

            // Spaces after a word may hang past the limit: only the word itself has to fit.
            float trimmed = full;
            for (int k = segEnd - 1; k >= seg && Glyphs[k].IsSpace; k--)
            {
                trimmed -= Glyphs[k].Advance;
            }

            if (_wrap > 0 && seg > lineStart && lineW + trimmed > _wrap)
            {
                AddLine(lineStart, seg, TextIndex(lineStart, start), TextEnd(seg, start, end) - TextIndex(lineStart, start));
                lineStart = seg;
                lineW = 0;
            }

            if (_wordBreaking == WordBreaking.BreakWord && _wrap > 0 && trimmed > _wrap)
            {
                // The word does not fit even on a line of its own: fill lines cluster by cluster.
                int visibleEnd = segEnd;
                while (visibleEnd > seg && Glyphs[visibleEnd - 1].IsSpace)
                {
                    visibleEnd--;
                }

                int i = seg;
                while (i < visibleEnd)
                {
                    int j = i + 1;
                    float cw = Glyphs[i].Advance;
                    while (j < visibleEnd && !ClusterStart[j])
                    {
                        cw += Glyphs[j].Advance;
                        j++;
                    }

                    if (i > lineStart && lineW + cw > _wrap)
                    {
                        AddLine(lineStart, i, TextIndex(lineStart, start), TextEnd(i, start, end) - TextIndex(lineStart, start));
                        lineStart = i;
                        lineW = 0;
                    }

                    lineW += cw;
                    i = j;
                }

                for (int k = visibleEnd; k < segEnd; k++)
                {
                    lineW += Glyphs[k].Advance;
                }
            }
            else
            {
                lineW += full;
            }

            seg = segEnd;
        }

        AddLine(lineStart, g1, TextIndex(lineStart, start), end - TextIndex(lineStart, start));
    }

    // A paragraph's first line starts at the paragraph's first character even when invisible zero-width characters come before the
    // first glyph; later lines start at their first glyph, which is always at a break opportunity and a cluster start.
    private int TextIndex(int glyph, int paragraphStart) => glyph == _firstGlyph || glyph >= Glyphs.Count ? paragraphStart : Glyphs[glyph].Cluster;

    private int TextEnd(int glyph, int paragraphStart, int paragraphEnd) => glyph < Glyphs.Count ? Glyphs[glyph].Cluster : paragraphEnd;

    // Stretches a line to the wrapping length by widening glyph advances: the spaces between words (InterWord) or the gaps between
    // clusters (InterCharacter). Lines at or beyond the length, and lines with nothing to widen, are left alone.
    private void Justify(int g0, int g1)
    {
        int first = g0;
        while (first < g1 && Glyphs[first].IsSpace)
        {
            first++;
        }

        int last = g1;
        while (last > first && Glyphs[last - 1].IsSpace)
        {
            last--;
        }

        if (last <= first)
        {
            return;
        }

        // White space hanging at the left of a right-to-left line does not count, as white space at the right of the other kind does not.
        float width = 0;
        for (int k = (_paraLevel & 1) == 1 ? first : g0; k < last; k++)
        {
            width += Glyphs[k].Advance;
        }

        float slack = _wrap - width;
        if (!(slack > 0))
        {
            return;
        }

        int count = 0;
        for (int k = first; k < last - 1; k++)
        {
            if (IsWidenable(k))
            {
                count++;
            }
        }

        if (count == 0)
        {
            return;
        }

        float extra = slack / count;
        for (int k = first; k < last - 1; k++)
        {
            if (IsWidenable(k))
            {
                ShapedGlyph g = Glyphs[k];
                g.Advance += extra;
                Glyphs[k] = g;
            }
        }
    }

    private bool IsWidenable(int k) => _justification == TextJustification.InterWord
        ? (Glyphs[k].IsSpace && Glyphs[k].CodePoint != '\t') || Glyphs[k].CodePoint == 0xA0
        : ClusterStart[k + 1];

    private void AddLine(int g0, int g1, int textStart, int textLength)
    {
        bool rtl = (_paraLevel & 1) == 1;
        ReorderLine(g0, g1);
        if (_justification != TextJustification.None && _wrap > 0)
        {
            Justify(g0, g1);
        }

        // Pen positions, then the width without trailing spaces (unless the line is only spaces).
        float x = 0;
        float height = 0;
        float ascent = 0;
        int lastStyle = -1;
        for (int k = g0; k < g1; k++)
        {
            PenX.Add(x);
            ShapedGlyph g = Glyphs[k];
            x += g.Advance;
            if (g.Style != lastStyle)
            {
                lastStyle = g.Style;
                GlyphStyle st = Styles[g.Style];
                height = Math.Max(height, st.Pitch);
                ascent = Math.Max(ascent, st.Ascent);
            }
        }

        if (g1 == g0)
        {
            // A line without glyphs takes the metrics of the style of its first character.
            GlyphStyle st = Styles[StyleOf(textStart < _text.Length ? RunAt(textStart) : -1, 0)];
            height = st.Pitch;
            ascent = st.Ascent;
        }

        // White space that ends the line hangs past it: at the right of a left-to-right line, at the left of a right-to-left one. A line
        // of only white space keeps its full width.
        int vs = g0;
        int last = g1;
        if (rtl)
        {
            while (vs < g1 && Glyphs[vs].IsSpace)
            {
                vs++;
            }

            if (vs == g1)
            {
                vs = g0;
                last = g0;
            }
        }
        else
        {
            while (last > g0 && Glyphs[last - 1].IsSpace)
            {
                last--;
            }
        }

        float hang = rtl && last > g0 ? PenXOf(vs) : 0;
        float width = last > g0 ? PenXOf(last - 1) + Glyphs[last - 1].Advance - hang : x;
        Lines.Add(new LayoutLine
        {
            GlyphStart = g0,
            GlyphEnd = g1,
            TextStart = textStart,
            TextLength = textLength,
            Width = width,
            Height = height,
            Ascent = ascent,
            Rtl = rtl,
            VisStart = last > g0 ? vs : g0,
            VisEnd = last,
            Hang = hang,
        });
        MaxWidth = Math.Max(MaxWidth, width);
        TotalHeight += height;
    }

    // PenX is appended in glyph order, so a glyph's entry is its index; paragraphs and lines are contiguous.
    public float PenXOf(int glyph) => PenX[glyph];

    // Block placement and per-line alignment.
    private void Position(TextOptions o)
    {
        // Horizontal alignment puts a box on the origin: left edge, center or right edge. Start lines share the left edge of a box
        // as wide as the widest line. Center and End place each line inside a box as wide as the wrapping length (the widest line
        // when there is none). The two boxes are the same width unless text wraps, which is how ImageSharp lays it out.
        float blockW = MaxWidth;
        float alignW = _wrap > 0 ? _wrap : MaxWidth;
        float haFraction = o.HorizontalAlignment switch { HorizontalAlignment.Center => 0.5f, HorizontalAlignment.Right => 1f, _ => 0f };
        float left = o.Origin.X;
        float blockH = TotalHeight;
        float top = o.VerticalAlignment switch
        {
            VerticalAlignment.Center => o.Origin.Y - (blockH / 2),
            VerticalAlignment.Bottom => o.Origin.Y - blockH,
            _ => o.Origin.Y,
        };
        for (int i = 0; i < Lines.Count; i++)
        {
            LayoutLine l = Lines[i];
            float w = l.Width;
            // Start and End are relative to the paragraph's direction: a right-to-left line starts at the right edge of its box. The
            // hanging white space of a right-to-left line (at its left) lies outside the box.
            l.Left = o.TextAlignment switch
            {
                TextAlignment.Center => left - (haFraction * alignW) + ((alignW - w) / 2),
                TextAlignment.End => l.Rtl ? left - (haFraction * alignW) : left - (haFraction * alignW) + (alignW - w),
                _ => l.Rtl ? left - (haFraction * blockW) + (blockW - w) : left - (haFraction * blockW),
            } - l.Hang;
            l.Baseline = top + l.Ascent;
            top += l.Height;
        }
    }

    public TextMetrics ToMetrics()
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        var lines = new LineMetrics[Lines.Count];
        for (int i = 0; i < Lines.Count; i++)
        {
            LayoutLine l = Lines[i];
            lines[i] = new LineMetrics
            {
                Start = l.TextStart,
                Length = l.TextLength,
                Left = l.Left,
                Baseline = l.Baseline,
                Advance = l.Width,
                Ascender = l.Ascent,
                Descender = l.Height - l.Ascent,
                LineHeight = l.Height,
            };
            for (int k = l.GlyphStart; k < l.GlyphEnd; k++)
            {
                ShapedGlyph g = Glyphs[k];
                if (g.IsSpace)
                {
                    continue;
                }

                GlyphStyle st = Styles[g.Style];
                Outline o = st.Face.GetOutline(g.GlyphId);
                if (o.IsEmpty)
                {
                    continue;
                }

                (float ax, float ay, float bx, float by) = o.TightBox();
                float gx = l.Left + PenX[k] + g.XOffset;
                x0 = Math.Min(x0, gx + (ax * st.Sx));
                x1 = Math.Max(x1, gx + (bx * st.Sx));
                float gy = l.Baseline - g.YOffset;
                y0 = Math.Min(y0, gy - (by * st.Sy));
                y1 = Math.Max(y1, gy - (ay * st.Sy));
            }
        }

        var advance = new System.Drawing.SizeF(MaxWidth, TotalHeight);
        System.Drawing.RectangleF bounds = x0 <= x1
            ? new System.Drawing.RectangleF(x0, y0, x1 - x0, y1 - y0)
            : new System.Drawing.RectangleF(Lines.Count > 0 ? Lines[0].Left : 0, Lines.Count > 0 ? Lines[0].Baseline - Lines[0].Ascent : 0, 0, 0);
        return new TextMetrics(advance, bounds, lines);
    }

    public IReadOnlyList<GlyphBounds> ToCharacterBounds()
    {
        var list = new List<GlyphBounds>(Glyphs.Count);
        for (int i = 0; i < Lines.Count; i++)
        {
            LayoutLine l = Lines[i];
            for (int k = l.GlyphStart; k < l.GlyphEnd; k++)
            {
                ShapedGlyph g = Glyphs[k];
                GlyphStyle st = Styles[g.Style];
                float gx = l.Left + PenX[k] + g.XOffset;
                float gy = l.Baseline - g.YOffset;
                System.Drawing.RectangleF ink = default;
                Outline outline = g.IsSpace ? Outline.Empty : st.Face.GetOutline(g.GlyphId);
                if (!outline.IsEmpty)
                {
                    (float ax, float ay, float bx, float by) = outline.TightBox();
                    ink = new System.Drawing.RectangleF(gx + (ax * st.Sx), gy - (by * st.Sy), (bx - ax) * st.Sx, (by - ay) * st.Sy);
                }

                var box = new System.Drawing.RectangleF(l.Left + PenX[k], l.Baseline - l.Ascent, g.Advance, l.Height);
                if (!ClusterStart[k] && k > l.GlyphStart && list.Count > 0)
                {
                    // A combining mark or other continuation: one box for the whole grapheme cluster, never split.
                    GlyphBounds head = list[^1];

                    // In a right-to-left run the glyphs of a cluster are reversed: the base comes last, and the cluster is named by
                    // its first character, the one with the lowest offset.
                    bool earlier = g.Cluster < head.Index;
                    list[^1] = head with
                    {
                        Bounds = System.Drawing.RectangleF.Union(head.Bounds, box),
                        Ink = head.Ink.IsEmpty ? ink : ink.IsEmpty ? head.Ink : System.Drawing.RectangleF.Union(head.Ink, ink),
                        Index = earlier ? g.Cluster : head.Index,
                        CodePoint = earlier ? g.CodePoint : head.CodePoint,
                    };
                    continue;
                }

                list.Add(new GlyphBounds
                {
                    Ink = ink,
                    // A dropped zero-width character can leave the first glyph of a paragraph inside its cluster; the box then
                    // starts where the line (and so the cluster) starts.
                    Index = !ClusterStart[k] && k == l.GlyphStart ? l.TextStart : g.Cluster,
                    CodePoint = g.CodePoint,
                    Line = i,
                    Bounds = box,
                });
            }
        }

        return list;
    }

    // ---- Output: outlines and decorations grouped by paint ----

    /// <summary>Identifies the paint of a part by reference, so runs that share a brush and a pen share a path.</summary>
    private readonly struct PaintKey(Brush? brush, Pen? pen, bool inheritBrush, bool inheritPen) : IEquatable<PaintKey>
    {
        public readonly Brush? Brush = brush;
        public readonly Pen? Pen = pen;
        public readonly bool InheritBrush = inheritBrush;
        public readonly bool InheritPen = inheritPen;

        public bool Equals(PaintKey o) => ReferenceEquals(Brush, o.Brush) && ReferenceEquals(Pen, o.Pen) && InheritBrush == o.InheritBrush && InheritPen == o.InheritPen;

        public override bool Equals(object? obj) => obj is PaintKey k && Equals(k);

        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Brush), RuntimeHelpers.GetHashCode(Pen), InheritBrush, InheritPen);
    }

    /// <summary>
    /// Builds the glyph outlines and decorations as paths, one per distinct paint in the order each paint first appears. With a
    /// route every glyph (and decoration piece) is moved onto it; see docs/text.md.
    /// </summary>
    public List<TextPart> BuildParts(PathRoute? route, int colorPalette = -1)
    {
        // Color glyphs (COLR v0, when the palette index is not negative): layer j of every color glyph goes into a builder of its
        // own, keyed by layer index and color (palette color, or the run's paint for the foreground entry), and the layer parts
        // follow the plain glyphs in layer order. A face that has no color data, or glyphs that are not color glyphs, take the
        // plain path, so this works for runs and fallback faces alike.
        var layerBuilders = new Dictionary<(int Layer, bool Foreground, PaintKey Paint, uint Color), PathBuilder>();
        var layerOrder = new List<(int Layer, bool Foreground, PaintKey Paint, uint Color, int Seq)>();
        var builders = new Dictionary<PaintKey, PathBuilder>();
        var order = new List<PaintKey>();

        PathBuilder For(PaintKey key)
        {
            if (!builders.TryGetValue(key, out PathBuilder? b))
            {
                b = new PathBuilder();
                builders[key] = b;
                order.Add(key);
            }

            return b;
        }

        GlyphStyle? lastStyle = null;
        PathBuilder? lastBuilder = null;
        for (int i = 0; i < Lines.Count; i++)
        {
            LayoutLine l = Lines[i];
            for (int k = l.GlyphStart; k < l.GlyphEnd; k++)
            {
                ShapedGlyph g = Glyphs[k];
                if (g.IsSpace)
                {
                    continue;
                }

                GlyphStyle st = Styles[g.Style];
                if (colorPalette >= 0 && st.Face.Color is { } colors && colors.TryGetLayers(g.GlyphId, out int firstLayer, out int layerCount))
                {
                    Matrix3x2 m = GlyphMatrix(l, k, st, route);
                    for (int j = 0; j < layerCount; j++)
                    {
                        (int layerGlyph, int paletteIndex) = colors.GetLayer(firstLayer + j);
                        bool foreground = paletteIndex == ColorGlyphs.ForegroundIndex;
                        Rgba32 color = default;
                        if (!foreground)
                        {
                            // A palette entry that does not exist leaves the layer out.
                            if (colors.GetColor(colorPalette, paletteIndex) is not { } c)
                            {
                                continue;
                            }

                            color = c;
                        }

                        Outline lo = st.Face.GetOutline(layerGlyph);
                        if (lo.IsEmpty)
                        {
                            continue;
                        }

                        PaintKey paint = foreground ? FillKey(st) : default;
                        uint bits = foreground ? 0 : ((uint)color.R << 24) | ((uint)color.G << 16) | ((uint)color.B << 8) | color.A;
                        var lk = (j, foreground, paint, bits);
                        if (!layerBuilders.TryGetValue(lk, out PathBuilder? lb))
                        {
                            lb = new PathBuilder();
                            layerBuilders[lk] = lb;
                            layerOrder.Add((j, foreground, paint, bits, layerOrder.Count));
                        }

                        GlyphPaths.Append(lb, lo, m);
                    }

                    continue;
                }

                Outline o = st.Face.GetOutline(g.GlyphId);
                if (!o.IsEmpty)
                {
                    if (!ReferenceEquals(st, lastStyle))
                    {
                        lastStyle = st;
                        lastBuilder = For(FillKey(st));
                    }

                    GlyphPaths.Append(lastBuilder!, o, GlyphMatrix(l, k, st, route));
                }
            }

            AddDecorations(l, route, For);
        }

        var parts = new List<TextPart>(order.Count);
        foreach (PaintKey key in order)
        {
            Path p = builders[key].Build();
            if (!p.IsEmpty)
            {
                parts.Add(new TextPart { Path = p, Brush = key.Brush, Pen = key.Pen, InheritBrush = key.InheritBrush, InheritPen = key.InheritPen });
            }
        }

        layerOrder.Sort((a, b) => a.Layer != b.Layer ? a.Layer.CompareTo(b.Layer) : a.Seq.CompareTo(b.Seq));
        foreach (var e in layerOrder)
        {
            Path p = layerBuilders[(e.Layer, e.Foreground, e.Paint, e.Color)].Build();
            if (p.IsEmpty)
            {
                continue;
            }

            if (e.Foreground)
            {
                parts.Add(new TextPart { Path = p, Brush = e.Paint.Brush, Pen = e.Paint.Pen, InheritBrush = true, InheritPen = true });
            }
            else
            {
                // A layer with its own color keeps it and is not outlined by the pen.
                var c = new Rgba32((byte)(e.Color >> 24), (byte)(e.Color >> 16), (byte)(e.Color >> 8), (byte)e.Color);
                parts.Add(new TextPart { Path = p, Brush = Brush.Solid(c) });
            }
        }

        return parts;
    }

    private PaintKey FillKey(GlyphStyle st)
    {
        TextRun? r = st.Run < 0 ? null : _runs[st.Run].Source;
        return new PaintKey(r?.Brush, r?.Pen, true, true);
    }

    // Straight text: a scale and flip into pixels at the pen position. On a path: the same, rotated about the middle of the glyph's
    // advance and moved to the point of the path at that distance.
    private Matrix3x2 GlyphMatrix(LayoutLine l, int k, GlyphStyle st, PathRoute? route)
    {
        ShapedGlyph g = Glyphs[k];
        float oy = l.Baseline - g.YOffset;
        if (route is null)
        {
            return new Matrix3x2(st.Sx, 0, 0, -st.Sy, l.Left + PenX[k] + g.XOffset, oy);
        }

        double center = l.Left + PenX[k] + (g.Advance / 2);
        route.Sample(center, out Vector2 p, out float c, out float s);
        float dx = g.XOffset - (g.Advance / 2);
        return new Matrix3x2(c * st.Sx, s * st.Sx, s * st.Sy, -c * st.Sy, p.X + (c * dx) - (s * oy), p.Y + (s * dx) + (c * oy));
    }

    private void AddDecorations(LayoutLine l, PathRoute? route, Func<PaintKey, PathBuilder> builderFor)
    {
        int visibleStart = l.VisStart;
        int visibleEnd = l.VisEnd;
        if (visibleEnd == visibleStart)
        {
            return;
        }

        foreach (TextDecorations kind in (ReadOnlySpan<TextDecorations>)[TextDecorations.Underline, TextDecorations.Strikeout, TextDecorations.Overline])
        {
            bool open = false;
            float x0 = 0;
            float x1 = 0;
            float offset = 0;
            float thick = 0;
            int run = -2;
            for (int k = visibleStart; k < visibleEnd; k++)
            {
                GlyphStyle st = Styles[Glyphs[k].Style];
                if ((st.Decorations & kind) == 0)
                {
                    if (open)
                    {
                        EmitDecoration(l, kind, run, x0, x1, offset, thick, null, builderFor);
                        open = false;
                    }

                    continue;
                }

                (float off, float th) = kind switch
                {
                    TextDecorations.Underline => (st.UnderlineOffset, st.UnderlineThickness),
                    TextDecorations.Strikeout => (st.StrikeoutOffset, st.StrikeoutThickness),
                    _ => (st.OverlineOffset, st.UnderlineThickness),
                };
                float gx0 = l.Left + PenX[k];
                float gx1 = gx0 + Glyphs[k].Advance;
                if (route is not null)
                {
                    EmitDecoration(l, kind, st.Run, gx0, gx1, off, th, route, builderFor);
                    continue;
                }

                // One continuous line over neighbouring glyphs that share the run, position and thickness.
                if (open && run == st.Run && offset == off && thick == th)
                {
                    x1 = gx1;
                    continue;
                }

                if (open)
                {
                    EmitDecoration(l, kind, run, x0, x1, offset, thick, null, builderFor);
                }

                open = true;
                run = st.Run;
                x0 = gx0;
                x1 = gx1;
                offset = off;
                thick = th;
            }

            if (open)
            {
                EmitDecoration(l, kind, run, x0, x1, offset, thick, null, builderFor);
            }
        }
    }

    // A decoration is a filled rectangle painted like its run's glyphs, or, when the run has a pen for it, a stroked line.
    private void EmitDecoration(LayoutLine l, TextDecorations kind, int run, float x0, float x1, float offset, float thick, PathRoute? route, Func<PaintKey, PathBuilder> builderFor)
    {
        TextRun? r = run < 0 ? null : _runs[run].Source;
        Pen? pen = kind switch
        {
            TextDecorations.Underline => r?.UnderlinePen,
            TextDecorations.Strikeout => r?.StrikeoutPen,
            _ => r?.OverlinePen,
        };
        float y = l.Baseline - offset;
        Vector2 a;
        Vector2 b;
        Vector2 c;
        Vector2 d;
        float half = pen is null ? thick / 2 : 0;
        if (route is null)
        {
            a = new Vector2(x0, y - half);
            b = new Vector2(x1, y - half);
            c = new Vector2(x1, y + half);
            d = new Vector2(x0, y + half);
        }
        else
        {
            double center = (x0 + x1) / 2;
            route.Sample(center, out Vector2 p, out float cs, out float sn);
            float hw = (x1 - x0) / 2;
            a = Place(p, cs, sn, -hw, y - half);
            b = Place(p, cs, sn, hw, y - half);
            c = Place(p, cs, sn, hw, y + half);
            d = Place(p, cs, sn, -hw, y + half);
        }

        if (pen is not null)
        {
            // The pen's own width is the thickness: stroke the middle line.
            builderFor(new PaintKey(null, pen, false, false)).MoveTo(a).LineTo(b);
            return;
        }

        builderFor(new PaintKey(r?.Brush, r?.Pen, true, true)).MoveTo(a).LineTo(b).LineTo(c).LineTo(d).Close();
    }

    private static Vector2 Place(Vector2 p, float cos, float sin, float x, float y) => new(p.X + (cos * x) - (sin * y), p.Y + (sin * x) + (cos * y));
}
