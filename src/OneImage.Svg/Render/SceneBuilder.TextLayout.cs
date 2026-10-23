using System.Globalization;
using System.Numerics;
using OneImage.Drawing;
using OneImage.Svg.Dom;
using OneImage.Text;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

// Text layout: chunks, shaping with font fallback and bidi, spacing, textLength, anchoring, text on a path.
internal sealed partial class SceneBuilder
{
    private const double SmallCapsScale = 0.8;

    private static bool MayNeedBidi(string text, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            char c = text[i];
            if (c >= 0x590 && (c <= 0x8FF || (c >= 0x200E && c <= 0x200F) || (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069)
                || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF) || char.IsSurrogate(c)))
            {
                return true;
            }
        }

        return false;
    }

    private List<Placed> LayoutText(TextModel m, Viewport vp)
    {
        var all = new List<Placed>();
        var chunks = new List<(int First, int Count)>();
        string text = m.Text.ToString();
        int n = text.Length;
        int ci = 0;
        while (ci < n)
        {
            TSpan? owner = m.Spans[m.CharSpan[ci]].PathSpan;
            int cj = ci + 1;
            while (cj < n && !TextModel.IsSet(m.X, cj) && !TextModel.IsSet(m.Y, cj) && ReferenceEquals(m.Spans[m.CharSpan[cj]].PathSpan, owner))
            {
                cj++;
            }

            int first = all.Count;
            ShapeChunk(m, text, ci, cj, all, chunks.Count);
            if (all.Count > first)
            {
                chunks.Add((first, all.Count - first));
            }

            ci = cj;
        }

        ApplyTextLengths(m, all);

        double curX = 0, curY = 0;
        foreach ((int first, int count) in chunks)
        {
            PositionChunk(m, all, first, count, ref curX, ref curY);
        }

        return all;
    }

    private void ShapeChunk(TextModel m, string text, int c0, int c1, List<Placed> all, int chunkId)
    {
        TSpan firstSpan = m.Spans[m.CharSpan[c0]];
        byte[]? lv = null;
        bool paraRtl = firstSpan.Ts.Rtl;
        bool overrides = false;
        for (int i = c0; i < c1 && !overrides; i++)
        {
            overrides = m.Spans[m.CharSpan[i]].Ts.BidiOverride;
        }

        // Writing mode applies to the text element only; right-to-left runs are reordered down the column as they would be along a line.
        bool vertical = m.Spans[0].Ts.Vertical;
        if (paraRtl || overrides || MayNeedBidi(text, c0, c1))
        {
            lv = TextShaping.GetBidiLevels(text.Substring(c0, c1 - c0), paraRtl ? TextDirection.RightToLeft : TextDirection.LeftToRight, out int para);
            if (overrides)
            {
                for (int i = c0; i < c1; i++)
                {
                    TextStyle ts = m.Spans[m.CharSpan[i]].Ts;
                    if (ts.BidiOverride)
                    {
                        lv[i - c0] = (byte)(ts.Rtl ? 1 : para == 1 ? 2 : 0);
                    }
                }
            }
        }

        // Small capitals use the font's own small-capital glyphs (the smcp feature) when it has them; otherwise lowercase letters are shown as
        // capitals at 0.8 of the size.
        char[] chars = text.ToCharArray();
        (Font? Font, int Option) Resolve(int i)
        {
            TSpan sp = m.Spans[m.CharSpan[i]];
            int cp = CodePointAt(text, i, c1);
            Font? f = sp.Chain!.Pick(cp);
            int kern = (sp.Ts.Kerning ? 0 : 1) | (vertical && TextShaping.IsUprightInVerticalText(cp) ? 4 : 0);
            if (f is null || !sp.Ts.SmallCaps || (kern & 4) != 0)
            {
                return (f, kern);
            }

            // The feature changes only lowercase letters, so a whole run can carry it and keep its kerning across capitals.
            if (_fonts.HasSmallCaps(f))
            {
                return (f, kern | 2);
            }

            if (cp > 0xFFFF || !char.IsLower((char)cp) || char.ToUpperInvariant((char)cp) == (char)cp)
            {
                return (f, kern);
            }

            chars[i] = char.ToUpperInvariant((char)cp);
            Font? upper = sp.Chain.Pick(chars[i]) ?? f;
            return (_fonts.Scaled(upper, SmallCapsScale), kern);
        }

        int pos = c0;
        Font? prevFont = null;
        int prevOpt = 0;
        byte prevLevel = 0;
        while (pos < c1)
        {
            int cp = CodePointAt(text, pos, c1);
            byte level = lv is null ? (byte)0 : lv[pos - c0];
            (Font? font, int optIndex) = Resolve(pos);
            int end = pos + (cp > 0xFFFF ? 2 : 1);
            if (font is null)
            {
                pos = end;
                continue;
            }

            while (end < c1)
            {
                int cpn = CodePointAt(text, end, c1);
                (Font? fn, int optNext) = Resolve(end);
                if (optNext != optIndex || (lv is null ? (byte)0 : lv[end - c0]) != level || !ReferenceEquals(fn, font) || SplitsLigature(m, text, end))
                {
                    break;
                }

                end += cpn > 0xFFFF ? 2 : 1;
            }

            if (prevFont is not null && level == 0 && prevLevel == 0 && (optIndex & 5) == 0 && prevOpt == optIndex && all.Count > 0 && all[^1].Index + all[^1].Length == pos)
            {
                KernAcrossSizes(all[^1], chars, pos, prevFont, font, optIndex);
            }

            prevFont = font;
            prevOpt = optIndex;
            prevLevel = level;
            IReadOnlyList<ShapedCluster> clusters = TextShaping.ShapeRun(chars.AsSpan(pos, end - pos), font, (level & 1) == 1, ShapeOptions[optIndex]);
            foreach (ShapedCluster c in clusters)
            {
                int gi = pos + c.Start;
                TSpan owner = m.Spans[m.CharSpan[gi]];
                bool upright = (optIndex & 4) != 0;
                double adv = (upright ? c.VerticalAdvance : c.Advance) + owner.Ts.LetterSpacing;
                if (text[gi] is ' ' or '\u00a0')
                {
                    adv += owner.Ts.WordSpacing;
                }

                Count();
                all.Add(new Placed { Cluster = c, Span = owner, Font = font, Index = gi, Length = c.Length, Level = lv is null ? (byte)0 : lv[gi - c0], Chunk = chunkId, Advance = adv, Upright = upright });
            }

            if ((all.Count & 0x3FF) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            pos = end;
        }
    }

    // Two runs of one family that differ in size or weight still kern as one text: the pair adjustment of the last cluster before the break
    // and the first character after it is taken in the first run's font and added to that cluster's advance.
    private static void KernAcrossSizes(Placed last, char[] chars, int pos, Font before, Font after, int optIndex)
    {
        if (before.Family.Name != after.Family.Name || last.Length != 1)
        {
            return;
        }

        char[] pair = [chars[last.Index], chars[pos]];
        IReadOnlyList<ShapedCluster> two = TextShaping.ShapeRun(pair, before, false, ShapeOptions[optIndex]);
        if (two.Count != 2)
        {
            return;
        }

        last.Advance += two[0].Advance - last.Cluster.Advance;
    }

    // Two characters that are positioned apart (an own dx or dy, or a rotation that differs from the one before) cannot share a ligature, so the
    // run of the shaper is cut between them. A combining mark stays with its base, and so does a joiner.
    private static bool SplitsLigature(TextModel m, string text, int i)
    {
        double dx = TextModel.Get(m.Dx, i);
        double dy = TextModel.Get(m.Dy, i);
        double rot = TextModel.Get(m.Rot, i);
        double prev = TextModel.Get(m.Rot, i - 1);
        bool moved = (!double.IsNaN(dx) && dx != 0) || (!double.IsNaN(dy) && dy != 0);
        bool turned = !double.IsNaN(rot) != !double.IsNaN(prev) || (!double.IsNaN(rot) && rot != prev);
        if (!moved && !turned)
        {
            return false;
        }

        UnicodeCategory cat = char.GetUnicodeCategory(text, i);
        return cat is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format or UnicodeCategory.Surrogate)
            && text[i] is not ('\u200D' or '\u200C');
    }

    private static int CodePointAt(string text, int i, int limit) =>
        char.IsHighSurrogate(text[i]) && i + 1 < limit && char.IsLowSurrogate(text[i + 1]) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];

    // The first cluster at or after a character index (clusters are in logical order).
    private static int LowerBound(List<Placed> all, int charIndex)
    {
        int lo = 0, hi = all.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (all[mid].Index < charIndex)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // textLength: stretch the element's text to the length, by spacing between the clusters or by scaling them. Inner elements first.
    private static void ApplyTextLengths(TextModel m, List<Placed> all)
    {
        for (int si = m.Spans.Count - 1; si >= 0; si--)
        {
            TSpan sp = m.Spans[si];
            if (double.IsNaN(sp.TextLength))
            {
                continue;
            }

            int a = LowerBound(all, sp.Start);
            int b = LowerBound(all, sp.End);
            int n = b - a;

            // Text of one element that is set in several chunks (a coordinate restarts it) is not stretched as a whole.
            if (n <= 0 || all[a].Chunk != all[b - 1].Chunk)
            {
                continue;
            }

            double total = 0;
            for (int i = a; i < b; i++)
            {
                total += all[i].Advance;
            }

            if (sp.StretchGlyphs)
            {
                if (total > 0)
                {
                    double k = sp.TextLength / total;
                    for (int i = a; i < b; i++)
                    {
                        all[i].XScale *= k;
                        all[i].Advance *= k;
                    }
                }
            }
            else if (n > 1)
            {
                double delta = (sp.TextLength - total) / (n - 1);
                for (int i = a; i < b - 1; i++)
                {
                    all[i].Advance += delta;
                }
            }
        }
    }

    private static Affine Place(double x, double y, double rotDegrees, double xScale)
    {
        double rad = rotDegrees * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        return new Affine(cos * xScale, sin * xScale, -sin, cos, x, y);
    }

    private static void PositionChunk(TextModel m, List<Placed> all, int first, int count, ref double curX, ref double curY)
    {
        Placed head = all[first];
        TSpan? pathSpan = head.Span.PathSpan;

        int[] order;
        bool plain = true;
        for (int i = 0; i < count && plain; i++)
        {
            plain = all[first + i].Level == 0;
        }

        if (plain)
        {
            order = new int[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }
        }
        else
        {
            var levels = new byte[count];
            for (int i = 0; i < count; i++)
            {
                levels[i] = all[first + i].Level;
            }

            order = TextShaping.GetVisualOrder(levels);
        }

        TextAnchor anchor = head.Span.Ts.Anchor;
        bool rtl = head.Span.Ts.Rtl;
        if (pathSpan?.Route is { } route)
        {
            PositionOnPath(m, all, first, order, pathSpan, route, anchor, rtl, ref curX, ref curY);
            return;
        }

        if (m.Spans[0].Ts.Vertical)
        {
            PositionVertical(m, all, first, count, order, anchor, ref curX, ref curY);
            return;
        }

        double x0 = TextModel.IsSet(m.X, head.Index) ? m.X![head.Index] : curX;
        double y0 = TextModel.IsSet(m.Y, head.Index) ? m.Y![head.Index] : curY;
        double cx = x0, cy = y0;
        foreach (int oi in order)
        {
            Placed p = all[first + oi];
            double dx = TextModel.Get(m.Dx, p.Index);
            double dy = TextModel.Get(m.Dy, p.Index);
            if (!double.IsNaN(dx))
            {
                cx += dx;
            }

            if (!double.IsNaN(dy))
            {
                cy += dy;
            }

            double rot = TextModel.Get(m.Rot, p.Index);
            p.Matrix = Place(cx, cy + p.Span.ShiftDown, double.IsNaN(rot) ? 0 : rot, p.XScale);
            cx += p.Advance;
        }

        // Anchoring works on the extent of the chunk's glyphs (they need not run in order once dx is negative); the spacing after the
        // last character does not count as part of the text.
        double minX = double.MaxValue, maxX = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            Placed p = all[first + i];
            double left = p.Matrix.E;
            double right = left + p.Advance;
            minX = Math.Min(minX, Math.Min(left, right));
            maxX = Math.Max(maxX, Math.Max(left, right));
        }

        maxX -= all[first + order[^1]].Span.Ts.LetterSpacing;

        // A dx on the first character moves the whole chunk: the anchor point is where the text would start with it.
        double firstDx = TextModel.Get(m.Dx, head.Index);
        double at = double.IsNaN(firstDx) ? x0 : x0 + firstDx;
        double shift = anchor switch
        {
            TextAnchor.Middle => at - ((minX + maxX) / 2),
            TextAnchor.End => rtl ? at - minX : at - maxX,
            _ => rtl ? at - maxX : at - minX,
        };

        if (shift != 0)
        {
            for (int i = 0; i < count; i++)
            {
                Placed p = all[first + i];
                Affine a = p.Matrix;
                p.Matrix = a with { E = a.E + shift };
            }
        }

        curX = cx + shift;
        curY = cy;
    }

    // Vertical text: the characters run down a column. x is the column's middle line and y where the chunk starts; dy moves along the column and
    // dx across it. Text-anchor works on the extent along y. Characters that lie down are rotated a quarter turn clockwise about their pen
    // position on the baseline, which sits so that the font's ascent and descent box is centered on the column's line; upright ones are
    // drawn from their upright outline, centered on the line. Underlines and the like are bars on the rotated baseline, to its left.
    private static void PositionVertical(TextModel m, List<Placed> all, int first, int count, int[] order, TextAnchor anchor, ref double curX, ref double curY)
    {
        Placed head = all[first];
        double x0 = TextModel.IsSet(m.X, head.Index) ? m.X![head.Index] : curX;
        double y0 = TextModel.IsSet(m.Y, head.Index) ? m.Y![head.Index] : curY;
        double cx = x0, cy = y0;
        double top = double.MaxValue, bottom = double.MinValue;
        foreach (int oi in order)
        {
            Placed p = all[first + oi];
            double dx = TextModel.Get(m.Dx, p.Index);
            double dy = TextModel.Get(m.Dy, p.Index);
            if (!double.IsNaN(dx))
            {
                cx += dx;
            }

            if (!double.IsNaN(dy))
            {
                cy += dy;
            }

            FontMetrics fm = p.Font.Metrics;
            double scale = p.Font.Size / (double)fm.UnitsPerEm;
            double baseX = cx - ((Math.Abs(fm.Ascender) - Math.Abs(fm.Descender)) * scale / 2);
            double rot = TextModel.Get(m.Rot, p.Index);
            double r = double.IsNaN(rot) ? 0 : rot;
            p.Frame = Place(baseX, cy, 90 + r, 1);
            p.Matrix = p.Upright ? Place(cx, cy, r, 1) : p.Frame.Value;
            top = Math.Min(top, Math.Min(cy, cy + p.Advance));
            bottom = Math.Max(bottom, Math.Max(cy, cy + p.Advance));
            cy += p.Advance;
        }

        bottom -= all[first + order[^1]].Span.Ts.LetterSpacing;
        double firstDy = TextModel.Get(m.Dy, head.Index);
        double at = double.IsNaN(firstDy) ? y0 : y0 + firstDy;
        double shift = anchor switch
        {
            TextAnchor.Middle => at - ((top + bottom) / 2),
            TextAnchor.End => at - bottom,
            _ => at - top,
        };

        if (shift != 0)
        {
            for (int i = 0; i < count; i++)
            {
                Placed p = all[first + i];
                p.Matrix = p.Matrix with { F = p.Matrix.F + shift };
                p.Frame = p.Frame!.Value with { F = p.Frame.Value.F + shift };
            }
        }

        curX = cx;
        curY = cy + shift;
    }

    private static void PositionOnPath(TextModel m, List<Placed> all, int first, int[] order, TSpan pathSpan, PathRoute route, TextAnchor anchor, bool rtl, ref double curX, ref double curY)
    {
        double len = route.Length;
        double start = pathSpan.StartOffsetPercent ? pathSpan.StartOffset * len : pathSpan.StartOffset;
        double total = 0;
        foreach (int oi in order)
        {
            total += all[first + oi].Advance;
        }

        start += anchor switch
        {
            TextAnchor.Middle => -total / 2,
            TextAnchor.End => rtl ? 0 : -total,
            _ => rtl ? -total : 0,
        };

        // An absolute x on the first character moves the text along the path by that distance (y has no meaning on a path).
        if (TextModel.IsSet(m.X, all[first].Index))
        {
            start += m.X![all[first].Index];
        }

        double s = start;
        double offset = 0;
        double lastX = curX, lastY = curY;
        foreach (int oi in order)
        {
            Placed p = all[first + oi];
            double dx = TextModel.Get(m.Dx, p.Index);
            double dy = TextModel.Get(m.Dy, p.Index);
            if (!double.IsNaN(dx))
            {
                s += dx;
            }

            if (!double.IsNaN(dy))
            {
                offset += dy;
            }

            double mid = s + (p.Advance / 2);
            if (mid < 0 || mid > len)
            {
                p.Skip = true;
            }
            else
            {
                // The glyph sits with its middle on the path and turns with the chord it spans (the tangent at the middle of a long glyph on a curve would tilt it away from the line).
                (double px, double py, _) = route.At(mid);
                (double sx0, double sy0, _) = route.At(s);
                (double sx1, double sy1, _) = route.At(s + p.Advance);
                double angle = Math.Atan2(sy1 - sy0, sx1 - sx0);
                double shiftX = -p.Advance / 2;
                double rot = TextModel.Get(m.Rot, p.Index);
                Affine along = Affine.Translate(px, py).Multiply(Place(0, 0, angle * 180 / Math.PI, 1));
                p.Matrix = along.Multiply(Affine.Translate(shiftX, offset + p.Span.ShiftDown)).Multiply(Place(0, 0, double.IsNaN(rot) ? 0 : rot, p.XScale));
                (lastX, lastY, _) = route.At(Math.Min(s + p.Advance, len));
            }

            s += p.Advance;
        }

        curX = lastX;
        curY = lastY;
    }
}
