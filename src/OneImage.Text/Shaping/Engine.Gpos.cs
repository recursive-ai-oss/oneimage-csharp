using OneImage.Text.Fonts;

namespace OneImage.Text.Shaping;

// GPOS lookup types 1-6 (types 7 and 8 are the shared contexts; 9 is resolved when lookups are read).
internal sealed partial class Engine
{
    // A value record's size: two bytes per set bit among the eight format flags.
    private static int ValueSize(int format) => 2 * int.PopCount(format & 0xFF);

    // Adds a value record to a glyph's placement. Device and variation-index tables (flags 0x10 to 0x80) depend on the pixel size
    // or on a variable-font instance; text here is unhinted and a variable font draws its default instance, so they are skipped.
    private void ApplyValue(ReadOnlySpan<byte> d, int p, int format, int i)
    {
        if ((format & 0xF) == 0)
        {
            return;
        }

        ref GlyphPos pos = ref _buf.Pos[i];
        if ((format & 1) != 0)
        {
            pos.XOffset += Be.I16(d, p);
            p += 2;
        }

        if ((format & 2) != 0)
        {
            pos.YOffset += Be.I16(d, p);
            p += 2;
        }

        if ((format & 4) != 0)
        {
            pos.XAdvance += Be.I16(d, p);
            p += 2;
        }

        if ((format & 8) != 0)
        {
            pos.YAdvance += Be.I16(d, p);
        }
    }

    private static void ReadAnchor(ReadOnlySpan<byte> d, int anchor, out int x, out int y)
    {
        // Formats 1 to 3 all start with the design-unit coordinates; format 2's contour point and format 3's device tables are not used.
        int format = Be.U16(d, anchor);
        if (format < 1 || format > 3)
        {
            throw new FontFormatException("Unknown anchor format.");
        }

        x = Be.I16(d, anchor + 2);
        y = Be.I16(d, anchor + 4);
    }

    // Type 1
    private bool SinglePos(int sub, int i)
    {
        ReadOnlySpan<byte> d = _table.Data.Span;
        int format = Be.U16(d, sub);
        int ci = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), _buf.Info[i].Glyph);
        if (ci < 0)
        {
            return false;
        }

        int vf = Be.U16(d, sub + 4);
        if (format == 1)
        {
            ApplyValue(d, sub + 6, vf, i);
            return true;
        }

        if (format == 2)
        {
            int size = ValueSize(vf);
            if (ci >= Be.U16(d, sub + 6))
            {
                return false;
            }

            ApplyValue(d, sub + 8 + (size * ci), vf, i);
            return true;
        }

        return false;
    }

    // Type 2: the first glyph's value record goes to it, the second's to the next glyph that is not skipped. When the second
    // record is empty that glyph is free to start the next pair; otherwise it has been consumed.
    private bool PairPos(int sub, int i, out int next)
    {
        next = i + 1;
        ReadOnlySpan<byte> d = _table.Data.Span;
        int format = Be.U16(d, sub);
        int ci = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), _buf.Info[i].Glyph);
        if (ci < 0)
        {
            return false;
        }

        int j = NextUnskipped(i);
        if (j >= _buf.Count)
        {
            return false;
        }

        int second = _buf.Info[j].Glyph;
        int vf1 = Be.U16(d, sub + 4);
        int vf2 = Be.U16(d, sub + 6);
        int s1 = ValueSize(vf1);
        int s2 = ValueSize(vf2);
        int rec;
        if (format == 1)
        {
            if (ci >= Be.U16(d, sub + 8))
            {
                return false;
            }

            int set = OtCommon.Offset16(d, sub, sub + 10 + (2 * ci));
            if (set == 0)
            {
                return false;
            }

            int n = Be.U16(d, set);
            int size = 2 + s1 + s2;
            if (set + 2L + ((long)size * n) > d.Length)
            {
                throw Be.Truncated();
            }

            rec = -1;
            int lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int p = set + 2 + (size * mid);
                int g = Be.U16(d, p);
                if (g == second)
                {
                    rec = p + 2;
                    break;
                }

                if (g < second)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (rec < 0)
            {
                return false;
            }
        }
        else if (format == 2)
        {
            int cd1 = OtCommon.Offset16(d, sub, sub + 8);
            int cd2 = OtCommon.Offset16(d, sub, sub + 10);
            int n1 = Be.U16(d, sub + 12);
            int n2 = Be.U16(d, sub + 14);
            int c1 = OtCommon.ClassOf(d, cd1, _buf.Info[i].Glyph);
            int c2 = OtCommon.ClassOf(d, cd2, second);
            if (c1 >= n1 || c2 >= n2)
            {
                return false;
            }

            rec = sub + 16 + (((c1 * n2) + c2) * (s1 + s2));
        }
        else
        {
            return false;
        }

        ApplyValue(d, rec, vf1, i);
        ApplyValue(d, rec + s1, vf2, j);
        next = vf2 != 0 ? j + 1 : j;
        return true;
    }

    // Type 3: join glyphs along a baseline. The exit anchor of one glyph meets the entry anchor of the next. Left to right only.
    private bool CursivePos(int sub, int i)
    {
        ReadOnlySpan<byte> d = _table.Data.Span;
        if (Be.U16(d, sub) != 1)
        {
            return false;
        }

        int cov = OtCommon.Offset16(d, sub, sub + 2);
        int count = Be.U16(d, sub + 4);
        int ci = OtCommon.CoverageIndex(d, cov, _buf.Info[i].Glyph);
        if (ci < 0 || ci >= count)
        {
            return false;
        }

        int entry = OtCommon.Offset16(d, sub, sub + 6 + (4 * ci));
        if (entry == 0)
        {
            return false;
        }

        int j = PrevUnskipped(i);
        if (j < 0)
        {
            return false;
        }

        int cj = OtCommon.CoverageIndex(d, cov, _buf.Info[j].Glyph);
        if (cj < 0 || cj >= count)
        {
            return false;
        }

        int exit = OtCommon.Offset16(d, sub, sub + 6 + (4 * cj) + 2);
        if (exit == 0)
        {
            return false;
        }

        ReadAnchor(d, entry, out int entryX, out int entryY);
        ReadAnchor(d, exit, out int exitX, out int exitY);
        ref GlyphPos prev = ref _buf.Pos[j];
        ref GlyphPos cur = ref _buf.Pos[i];

        // The pen moves to the exit anchor of the first glyph; the second glyph is shifted back by its entry anchor, and its own
        // advance shrinks by the same amount so what follows it is not displaced.
        prev.XAdvance = exitX + prev.XOffset;
        cur.XAdvance -= entryX + cur.XOffset;
        cur.XOffset -= entryX;
        cur.AttachTo = j;
        cur.AttachY = exitY - entryY;
        cur.AttachX = 0;
        cur.Cursive = true;
        return true;
    }

    // Type 4
    private bool MarkToBase(int sub, int i)
    {
        ReadOnlySpan<byte> d = _table.Data.Span;
        if (Be.U16(d, sub) != 1)
        {
            return false;
        }

        int mi = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), _buf.Info[i].Glyph);
        if (mi < 0)
        {
            return false;
        }

        // The base is the closest preceding glyph that is not a mark (and not excluded by the lookup flags).
        int j = i - 1;
        while (j >= 0 && (_buf.Info[j].Class == Gdef.ClassMark || Skip(j)))
        {
            j--;
        }

        if (j < 0)
        {
            return false;
        }

        int bi = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 4), _buf.Info[j].Glyph);
        if (bi < 0)
        {
            return false;
        }

        int classCount = Be.U16(d, sub + 6);
        int markArray = OtCommon.Offset16(d, sub, sub + 8);
        int baseArray = OtCommon.Offset16(d, sub, sub + 10);
        if (!MarkAnchor(d, markArray, mi, classCount, out int mx, out int my, out int mclass))
        {
            return false;
        }

        if (bi >= Be.U16(d, baseArray))
        {
            return false;
        }

        int anchor = OtCommon.Offset16(d, baseArray, baseArray + 2 + (2 * ((bi * classCount) + mclass)));
        if (anchor == 0)
        {
            return false;
        }

        ReadAnchor(d, anchor, out int bx, out int by);
        Attach(i, j, bx - mx, by - my);
        return true;
    }

    private static bool MarkAnchor(ReadOnlySpan<byte> d, int markArray, int mi, int classCount, out int x, out int y, out int mclass)
    {
        x = y = mclass = 0;
        if (markArray == 0 || mi >= Be.U16(d, markArray))
        {
            return false;
        }

        int rec = markArray + 2 + (4 * mi);
        mclass = Be.U16(d, rec);
        if (mclass >= classCount)
        {
            return false;
        }

        int anchor = OtCommon.Offset16(d, markArray, rec + 2);
        if (anchor == 0)
        {
            return false;
        }

        ReadAnchor(d, anchor, out x, out y);
        return true;
    }

    // Type 5: a mark on one component of a ligature. A mark that was between the ligature's components belongs to the component it
    // followed; any other mark goes on the last component.
    private bool MarkToLigature(int sub, int i)
    {
        ReadOnlySpan<byte> d = _table.Data.Span;
        if (Be.U16(d, sub) != 1)
        {
            return false;
        }

        int mi = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), _buf.Info[i].Glyph);
        if (mi < 0)
        {
            return false;
        }

        int j = i - 1;
        while (j >= 0 && (_buf.Info[j].Class == Gdef.ClassMark || Skip(j)))
        {
            j--;
        }

        if (j < 0)
        {
            return false;
        }

        int li = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 4), _buf.Info[j].Glyph);
        if (li < 0)
        {
            return false;
        }

        int classCount = Be.U16(d, sub + 6);
        int markArray = OtCommon.Offset16(d, sub, sub + 8);
        int ligArray = OtCommon.Offset16(d, sub, sub + 10);
        if (!MarkAnchor(d, markArray, mi, classCount, out int mx, out int my, out int mclass))
        {
            return false;
        }

        if (ligArray == 0 || li >= Be.U16(d, ligArray))
        {
            return false;
        }

        int attach = OtCommon.Offset16(d, ligArray, ligArray + 2 + (2 * li));
        if (attach == 0)
        {
            return false;
        }

        int comps = Be.U16(d, attach);
        if (comps == 0)
        {
            return false;
        }

        GlyphInfo mark = _buf.Info[i];
        GlyphInfo lig = _buf.Info[j];
        int comp = mark.LigId != 0 && mark.LigId == lig.LigId && mark.LigComp > 0 ? Math.Min(mark.LigComp, comps) - 1 : comps - 1;
        int anchor = OtCommon.Offset16(d, attach, attach + 2 + (2 * ((comp * classCount) + mclass)));
        if (anchor == 0)
        {
            return false;
        }

        ReadAnchor(d, anchor, out int bx, out int by);
        Attach(i, j, bx - mx, by - my);
        return true;
    }

    // Type 6: a mark on the mark just before it (for stacked accents).
    private bool MarkToMark(int sub, int i)
    {
        ReadOnlySpan<byte> d = _table.Data.Span;
        if (Be.U16(d, sub) != 1)
        {
            return false;
        }

        int mi = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), _buf.Info[i].Glyph);
        if (mi < 0)
        {
            return false;
        }

        int j = PrevUnskipped(i);
        if (j < 0)
        {
            return false;
        }

        // Both marks must belong to the same ligature component (or to none).
        GlyphInfo a = _buf.Info[i];
        GlyphInfo b = _buf.Info[j];
        if (a.LigId == b.LigId)
        {
            if (a.LigId != 0 && a.LigComp != b.LigComp)
            {
                return false;
            }
        }
        else if ((a.LigId != 0 && a.LigComp != 0) || (b.LigId != 0 && b.LigComp != 0))
        {
            return false;
        }

        int bi = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 4), b.Glyph);
        if (bi < 0)
        {
            return false;
        }

        int classCount = Be.U16(d, sub + 6);
        int markArray = OtCommon.Offset16(d, sub, sub + 8);
        int baseArray = OtCommon.Offset16(d, sub, sub + 10);
        if (!MarkAnchor(d, markArray, mi, classCount, out int mx, out int my, out int mclass))
        {
            return false;
        }

        if (baseArray == 0 || bi >= Be.U16(d, baseArray))
        {
            return false;
        }

        int anchor = OtCommon.Offset16(d, baseArray, baseArray + 2 + (2 * ((bi * classCount) + mclass)));
        if (anchor == 0)
        {
            return false;
        }

        ReadAnchor(d, anchor, out int bx, out int by);
        Attach(i, j, bx - mx, by - my);
        return true;
    }

    private void Attach(int mark, int target, int dx, int dy)
    {
        ref GlyphPos p = ref _buf.Pos[mark];
        p.AttachTo = target;
        p.AttachX = dx;
        p.AttachY = dy;
        p.Cursive = false;
    }

    /// <summary>
    /// Turns attachments into offsets once all positioning lookups have run. Glyphs are visited left to right, so a mark on a mark
    /// finds its base already placed. A mark sits at its base's offset plus the anchor difference, minus the pen travel between the two
    /// (the mark is drawn from the pen position it would have had, not from the base's).
    /// </summary>
    public static void ResolveAttachments(GlyphBuffer buffer)
    {
        GlyphPos[] pos = buffer.Pos;
        long[]? prefix = null;
        for (int i = 0; i < buffer.Count; i++)
        {
            int t = pos[i].AttachTo;
            if (t < 0 || t >= buffer.Count || t == i)
            {
                continue;
            }

            if (pos[i].Cursive)
            {
                pos[i].YOffset += pos[t].YOffset + pos[i].AttachY;
                continue;
            }

            if (t > i)
            {
                continue;
            }

            if (prefix is null)
            {
                prefix = new long[buffer.Count + 1];
                for (int k = 0; k < buffer.Count; k++)
                {
                    prefix[k + 1] = prefix[k] + pos[k].XAdvance;
                }
            }

            long travel = prefix[i] - prefix[t];
            pos[i].XOffset = (int)Math.Clamp(pos[t].XOffset + pos[i].AttachX - travel + pos[i].XOffset, int.MinValue / 2, int.MaxValue / 2);
            pos[i].YOffset += pos[t].YOffset + pos[i].AttachY;
        }
    }
}
