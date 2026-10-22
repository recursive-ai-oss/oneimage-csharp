using System.Buffers;
using System.Numerics;
using OneImage.Text.Fonts;
using OneImage.Text.Shaping;
using OneImage.Text.Unicode;

namespace OneImage.Text.Layout;

/// <summary>One shaped glyph. Advances are in font units and already include kerning with the next glyph.</summary>
internal struct ShapedGlyph
{
    public int GlyphId;
    public int CodePoint;
    public int Cluster;
    public int Length;
    public float Advance;

    /// <summary>Where the glyph is drawn relative to its pen position, in font units, y up (marks, cursive joins).</summary>
    public float XOffset;
    public float YOffset;
    public bool IsSpace;
}

/// <summary>One line of the result: a range of glyphs plus the text range it came from.</summary>
internal sealed class LayoutLine
{
    public int GlyphStart;
    public int GlyphEnd;
    public int TextStart;
    public int TextLength;

    /// <summary>Width in font units without trailing spaces.</summary>
    public float Width;

    /// <summary>Absolute x of the line start, in pixels.</summary>
    public float Left;

    /// <summary>Absolute y of the baseline, in pixels.</summary>
    public float Baseline;
}

/// <summary>
/// Lays text out: shaping (one glyph per code point from the character map, plus kerning), Unicode line breaking, greedy line
/// filling (with the word breaking modes), justification and alignment. Line breaking works on the text and reaches the glyphs
/// only through each glyph's text offset, so a different shaping step does not change it.
/// </summary>
internal sealed class TextLayout
{
    public const int MaxTextLength = 1 << 22;

    private readonly FontFace _face;
    private readonly float _scale;
    private readonly float _wrap;
    private readonly FeatureSet _features;
    private readonly uint _language;
    private readonly float _tabUnits;
    private readonly WordBreaking _wordBreaking;
    private readonly TextJustification _justification;

    // Break data of the paragraph being laid out, indexed by UTF-16 offset from its start (rented, returned after each paragraph).
    private byte[] _breaks = [];
    private byte[] _clusters = [];
    private int _paragraphStart;
    private int _firstGlyph = -1;

    private TextLayout(TextOptions o)
    {
        FaceEntry entry = o.Font.Face;
        _face = entry.Data;
        FontMetrics m = entry.Metrics;
        float px = o.Font.Size * o.Dpi / 72f;
        _scale = px / _face.UnitsPerEm;
        _wrap = o.WrappingLength;
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
        _tabUnits = o.TabWidth * _face.GetAdvance(_face.GetGlyphIndex(' '));
        _wordBreaking = o.WordBreaking;
        _justification = o.TextJustification;
        LineHeight = px * o.LineSpacing;
        float natural = m.LineHeight * _scale;

        // The line box is one em (times the spacing) tall; the font's own ascent and descent are centered in it.
        AscentInLine = (m.Ascender * _scale) - ((natural - LineHeight) / 2);
        Face = _face;
        Scale = _scale;
    }

    public FontFace Face { get; }

    public float Scale { get; }

    public float LineHeight { get; }

    public float AscentInLine { get; }

    public List<ShapedGlyph> Glyphs { get; } = [];

    public List<LayoutLine> Lines { get; } = [];

    /// <summary>Whether each glyph starts a grapheme cluster (false for combining marks and other continuations).</summary>
    public List<bool> ClusterStart { get; } = [];

    /// <summary>Pen x of each glyph in font units from its line's start.</summary>
    public List<float> PenX { get; } = [];

    /// <summary>The widest line, in pixels.</summary>
    public float MaxWidth { get; private set; }

    public static TextLayout Create(string text, TextOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        if (text.Length > MaxTextLength)
        {
            throw new ImageLimitException("The text is longer than the supported maximum.");
        }

        var layout = new TextLayout(options);
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

    // Shaping: code points go to the OpenType shaper (character map, GSUB substitutions, GPOS positioning). Tabs and zero width
    // non-joiners end a shaping run: nothing substitutes or kerns across them.
    private void Shape(string text, int start, int end)
    {
        _pending = 0;
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

            if (cp == '	')
            {
                FlushRun(end);
                int gid = _face.GetGlyphIndex(' ');
                Glyphs.Add(new ShapedGlyph { GlyphId = gid, CodePoint = cp, Cluster = i, Length = len, Advance = _tabUnits, IsSpace = true });
            }
            else if (cp == 0x200C)
            {
                FlushRun(end);
            }
            else if (!IsIgnorable(cp))
            {
                if (_pending == _cps.Length)
                {
                    Array.Resize(ref _cps, _pending * 2);
                    Array.Resize(ref _clusters, _pending * 2);
                }

                _cps[_pending] = cp;
                _clusters[_pending++] = i;
            }

            i += len;
        }

        FlushRun(end);
    }

    private readonly List<ShapedRunGlyph> _run = [];
    private int[] _cps = new int[64];
    private int[] _clusters = new int[64];
    private int _pending;

    private void FlushRun(int paragraphEnd)
    {
        if (_pending == 0)
        {
            return;
        }

        _run.Clear();
        TextShaper.Shape(_face, _cps.AsSpan(0, _pending), _clusters.AsSpan(0, _pending), _language, _features, _run);
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
                Advance = r.XAdvance,
                XOffset = r.XOffset,
                YOffset = r.YOffset,
                IsSpace = IsBreakingSpace(r.CodePoint),
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

        int at = Glyphs[k].Cluster - _paragraphStart;
        return _breaks[at] != 0 && _clusters[at] != 0;
    }

    // Computes the break opportunities and cluster starts of one paragraph into pooled arrays.
    private void PrepareBreaks(string text, int start, int end)
    {
        int length = end - start;
        _paragraphStart = start;
        _breaks = ArrayPool<byte>.Shared.Rent(length + 1);
        _clusters = ArrayPool<byte>.Shared.Rent(length + 1);
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
        Shape(text, start, end);
        int g1 = Glyphs.Count;
        if (g1 == g0)
        {
            AddLine(g0, g0, start, end - start);
            return;
        }

        PrepareBreaks(text, start, end);
        _firstGlyph = g0;
        try
        {
            for (int k = g0; k < g1; k++)
            {
                ClusterStart.Add(_clusters[Glyphs[k].Cluster - start] != 0);
            }

            FillLines(g0, g1, start, end);
        }
        finally
        {
            ReleaseBreaks();
        }
    }

    // Greedy line filling over the segments between break opportunities.
    private void FillLines(int g0, int g1, int start, int end)
    {
        int lineStart = g0;
        float lineW = 0; // font units, including spaces accepted so far
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

            if (_wrap > 0 && seg > lineStart && (lineW + trimmed) * _scale > _wrap)
            {
                AddLine(lineStart, seg, TextIndex(lineStart, start), TextEnd(seg, start, end) - TextIndex(lineStart, start));
                lineStart = seg;
                lineW = 0;
            }

            if (_wordBreaking == WordBreaking.BreakWord && _wrap > 0 && trimmed * _scale > _wrap)
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

                    if (i > lineStart && (lineW + cw) * _scale > _wrap)
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

        float width = 0;
        for (int k = g0; k < last; k++)
        {
            width += Glyphs[k].Advance;
        }

        float slack = (_wrap / _scale) - width;
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
        if (_justification != TextJustification.None && _wrap > 0)
        {
            Justify(g0, g1);
        }

        // Pen positions, then the width without trailing spaces (unless the line is only spaces).
        float x = 0;
        for (int k = g0; k < g1; k++)
        {
            PenX.Add(x);
            x += Glyphs[k].Advance;
        }

        int last = g1;
        while (last > g0 && Glyphs[last - 1].IsSpace)
        {
            last--;
        }

        float width = last > g0 ? PenXOf(last - 1) + Glyphs[last - 1].Advance : x;
        Lines.Add(new LayoutLine { GlyphStart = g0, GlyphEnd = g1, TextStart = textStart, TextLength = textLength, Width = width });
        MaxWidth = Math.Max(MaxWidth, width * _scale);
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
        float blockH = Lines.Count * LineHeight;
        float top = o.VerticalAlignment switch
        {
            VerticalAlignment.Center => o.Origin.Y - (blockH / 2),
            VerticalAlignment.Bottom => o.Origin.Y - blockH,
            _ => o.Origin.Y,
        };
        for (int i = 0; i < Lines.Count; i++)
        {
            LayoutLine l = Lines[i];
            float w = l.Width * _scale;
            l.Left = o.TextAlignment switch
            {
                TextAlignment.Center => left - (haFraction * alignW) + ((alignW - w) / 2),
                TextAlignment.End => left - (haFraction * alignW) + (alignW - w),
                _ => left - (haFraction * blockW),
            };
            l.Baseline = top + (i * LineHeight) + AscentInLine;
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
                Advance = l.Width * _scale,
                Ascender = AscentInLine,
                Descender = LineHeight - AscentInLine,
                LineHeight = LineHeight,
            };
            for (int k = l.GlyphStart; k < l.GlyphEnd; k++)
            {
                ShapedGlyph g = Glyphs[k];
                if (g.IsSpace)
                {
                    continue;
                }

                Outline o = _face.GetOutline(g.GlyphId);
                if (o.IsEmpty)
                {
                    continue;
                }

                (float ax, float ay, float bx, float by) = o.TightBox();
                float gx = l.Left + ((PenX[k] + g.XOffset) * _scale);
                x0 = Math.Min(x0, gx + (ax * _scale));
                x1 = Math.Max(x1, gx + (bx * _scale));
                float gy = l.Baseline - (g.YOffset * _scale);
                y0 = Math.Min(y0, gy - (by * _scale));
                y1 = Math.Max(y1, gy - (ay * _scale));
            }
        }

        var advance = new System.Drawing.SizeF(MaxWidth, Lines.Count * LineHeight);
        System.Drawing.RectangleF bounds = x0 <= x1
            ? new System.Drawing.RectangleF(x0, y0, x1 - x0, y1 - y0)
            : new System.Drawing.RectangleF(Lines.Count > 0 ? Lines[0].Left : 0, Lines.Count > 0 ? Lines[0].Baseline - AscentInLine : 0, 0, 0);
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
                float gx = l.Left + ((PenX[k] + g.XOffset) * _scale);
                float gy = l.Baseline - (g.YOffset * _scale);
                System.Drawing.RectangleF ink = default;
                Outline outline = g.IsSpace ? Outline.Empty : _face.GetOutline(g.GlyphId);
                if (!outline.IsEmpty)
                {
                    (float ax, float ay, float bx, float by) = outline.TightBox();
                    ink = new System.Drawing.RectangleF(gx + (ax * _scale), gy - (by * _scale), (bx - ax) * _scale, (by - ay) * _scale);
                }

                var box = new System.Drawing.RectangleF(l.Left + (PenX[k] * _scale), l.Baseline - AscentInLine, g.Advance * _scale, LineHeight);
                if (!ClusterStart[k] && k > l.GlyphStart && list.Count > 0)
                {
                    // A combining mark or other continuation: one box for the whole grapheme cluster, never split.
                    GlyphBounds head = list[^1];
                    list[^1] = head with
                    {
                        Bounds = System.Drawing.RectangleF.Union(head.Bounds, box),
                        Ink = head.Ink.IsEmpty ? ink : ink.IsEmpty ? head.Ink : System.Drawing.RectangleF.Union(head.Ink, ink),
                    };
                    continue;
                }

                list.Add(new GlyphBounds
                {
                    Ink = ink,
                    Index = g.Cluster,
                    CodePoint = g.CodePoint,
                    Line = i,
                    Bounds = box,
                });
            }
        }

        return list;
    }

    /// <summary>Appends the outlines of every glyph, in pixel space, to a path builder.</summary>
    public void AppendTo(OneImage.Drawing.PathBuilder builder)
    {
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

                Outline o = _face.GetOutline(g.GlyphId);
                if (!o.IsEmpty)
                {
                    GlyphPaths.Append(builder, o, new Matrix3x2(_scale, 0, 0, -_scale, l.Left + ((PenX[k] + g.XOffset) * _scale), l.Baseline - (g.YOffset * _scale)));
                }
            }
        }
    }
}
