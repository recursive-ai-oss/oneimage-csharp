using OneImage.Text.Fonts;

namespace OneImage.Text.Shaping;

// GSUB lookup types 1-4 and 8 (types 5 and 6 are the shared contexts; 7 is resolved when lookups are read).
internal sealed partial class Engine
{
    // Type 1: replace one glyph with another, by a fixed delta (format 1) or from a table (format 2).
    private bool SingleSubst(int sub, int i)
    {
        ReadOnlySpan<byte> d = _table.Data.Span;
        int format = Be.U16(d, sub);
        int glyph = _buf.Info[i].Glyph;
        int ci = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), glyph);
        if (ci < 0)
        {
            return false;
        }

        int replacement;
        if (format == 1)
        {
            replacement = (glyph + Be.I16(d, sub + 4)) & 0xFFFF;
        }
        else if (format == 2)
        {
            if (ci >= Be.U16(d, sub + 4))
            {
                return false;
            }

            replacement = Be.U16(d, sub + 6 + (2 * ci));
        }
        else
        {
            return false;
        }

        if (replacement >= _numGlyphs)
        {
            return false;
        }

        SetGlyph(i, replacement);
        return true;
    }

    private void SetGlyph(int i, int glyph)
    {
        ref GlyphInfo g = ref _buf.Info[i];
        g.Glyph = glyph;
        g.Class = ClassFor(glyph, g.Class);
    }

    // Type 2: one glyph becomes a sequence. All the new glyphs belong to the original character.
    private bool MultipleSubst(int sub, int i, out int next)
    {
        next = i + 1;
        ReadOnlySpan<byte> d = _table.Data.Span;
        if (Be.U16(d, sub) != 1)
        {
            return false;
        }

        int ci = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), _buf.Info[i].Glyph);
        if (ci < 0 || ci >= Be.U16(d, sub + 4))
        {
            return false;
        }

        int seq = OtCommon.Offset16(d, sub, sub + 6 + (2 * ci));
        if (seq == 0)
        {
            return false;
        }

        int n = Be.U16(d, seq);
        if (seq + 2L + (2L * n) > d.Length)
        {
            throw Be.Truncated();
        }

        for (int k = 0; k < n; k++)
        {
            if (Be.U16(d, seq + 2 + (2 * k)) >= _numGlyphs)
            {
                return false;
            }
        }

        if (n == 0)
        {
            // Deleting the glyph is what an empty sequence is for (a font may use it to drop a placeholder).
            _buf.RemoveAt(i);
            next = i;
            return true;
        }

        if (_buf.Count + n - 1 > _maxGlyphs)
        {
            return false;
        }

        GlyphInfo orig = _buf.Info[i];
        if (n > 1)
        {
            _buf.Insert(i + 1, n - 1);
        }

        for (int k = 0; k < n; k++)
        {
            int g = Be.U16(d, seq + 2 + (2 * k));
            GlyphInfo info = orig;
            info.Glyph = g;
            info.Class = ClassFor(g, orig.Class);
            _buf.Info[i + k] = info;
        }

        next = i + n;
        return true;
    }

    // Type 3: pick one of several alternates; the feature's value (1-based) says which. Off the end of the list nothing changes.
    private bool AlternateSubst(int sub, int i)
    {
        ReadOnlySpan<byte> d = _table.Data.Span;
        if (Be.U16(d, sub) != 1)
        {
            return false;
        }

        int ci = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), _buf.Info[i].Glyph);
        if (ci < 0 || ci >= Be.U16(d, sub + 4))
        {
            return false;
        }

        int set = OtCommon.Offset16(d, sub, sub + 6 + (2 * ci));
        if (set == 0)
        {
            return false;
        }

        int n = Be.U16(d, set);
        if (_value < 1 || _value > n)
        {
            return false;
        }

        int g = Be.U16(d, set + 2 + (2 * (_value - 1)));
        if (g >= _numGlyphs)
        {
            return false;
        }

        SetGlyph(i, g);
        return true;
    }

    // Type 4: a run of glyphs (not necessarily adjacent: ignored glyphs between them are stepped over) becomes one ligature glyph.
    private bool LigatureSubst(int sub, int i)
    {
        ReadOnlySpan<byte> d = _table.Data.Span;
        if (Be.U16(d, sub) != 1)
        {
            return false;
        }

        int ci = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), _buf.Info[i].Glyph);
        if (ci < 0 || ci >= Be.U16(d, sub + 4))
        {
            return false;
        }

        int set = OtCommon.Offset16(d, sub, sub + 6 + (2 * ci));
        if (set == 0)
        {
            return false;
        }

        int count = Be.U16(d, set);
        for (int k = 0; k < count; k++)
        {
            int lig = OtCommon.Offset16(d, set, set + 2 + (2 * k));
            if (lig == 0)
            {
                continue;
            }

            int ligGlyph = Be.U16(d, lig);
            int comps = Be.U16(d, lig + 2);
            if (comps == 0 || comps > _buf.Count || ligGlyph >= _numGlyphs)
            {
                continue;
            }

            int[] mp = MatchArray(comps);
            mp[0] = i;
            int pos = i;
            bool ok = true;
            for (int c = 1; c < comps; c++)
            {
                pos = NextUnskipped(pos);
                if (pos >= _buf.Count || _buf.Info[pos].Glyph != Be.U16(d, lig + 4 + (2 * (c - 1))))
                {
                    ok = false;
                    break;
                }

                mp[c] = pos;
            }

            if (!ok)
            {
                continue;
            }

            Ligate(ligGlyph, comps, mp);
            return true;
        }

        return false;
    }

    private void Ligate(int ligGlyph, int comps, int[] mp)
    {
        int first = mp[0];
        bool allMarks = true;
        for (int c = 0; c < comps; c++)
        {
            allMarks &= _buf.Info[mp[c]].Class == Gdef.ClassMark;
        }

        if (comps > 1)
        {
            // Marks that sat between the components stay, after the ligature, and remember which component they followed so
            // mark-to-ligature positioning can put them on the right part of it.
            _ligCounter++;
            if (_ligCounter == 0)
            {
                _ligCounter = 1;
            }

            ushort id = _ligCounter;
            int passed = 1; // components met so far
            for (int j = first + 1; j < mp[comps - 1]; j++)
            {
                if (j == mp[passed])
                {
                    passed++;
                    continue;
                }

                _buf.Info[j].LigId = id;
                _buf.Info[j].LigComp = (byte)Math.Min(passed, 255);
            }

            _buf.Info[first].LigId = id;
            _buf.Info[first].LigComp = 0;
        }

        byte flags = 0;
        for (int c = 0; c < comps; c++)
        {
            flags |= _buf.Info[mp[c]].Flags;
        }

        ref GlyphInfo g = ref _buf.Info[first];
        g.Flags = flags;
        g.Glyph = ligGlyph;
        g.Class = ClassFor(ligGlyph, allMarks ? (byte)Gdef.ClassMark : (byte)Gdef.ClassLigature);
        for (int c = comps - 1; c >= 1; c--)
        {
            _buf.RemoveAt(mp[c]);
        }
    }

    // Type 8: contextual single substitution that is applied from the end of the run backwards, so each decision sees the
    // original glyphs after it and the already-substituted glyphs before it.
    private bool ReverseChainSubst(int sub, int i)
    {
        ReadOnlySpan<byte> d = _table.Data.Span;
        if (Be.U16(d, sub) != 1)
        {
            return false;
        }

        int glyph = _buf.Info[i].Glyph;
        int ci = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), glyph);
        if (ci < 0)
        {
            return false;
        }

        int btCount = Be.U16(d, sub + 4);
        int btArr = sub + 6;
        int p = btArr + (2 * btCount);
        int laCount = Be.U16(d, p);
        int laArr = p + 2;
        p = laArr + (2 * laCount);
        int substCount = Be.U16(d, p);
        if (ci >= substCount)
        {
            return false;
        }

        int replacement = Be.U16(d, p + 2 + (2 * ci));
        if (replacement >= _numGlyphs)
        {
            return false;
        }

        int pos = i;
        for (int k = 0; k < btCount; k++)
        {
            pos = PrevUnskipped(pos);
            if (pos < 0 || !MatchAt(d, ByCoverage, btArr, k, 0, sub, _buf.Info[pos].Glyph))
            {
                return false;
            }
        }

        pos = i;
        for (int k = 0; k < laCount; k++)
        {
            pos = NextUnskipped(pos);
            if (pos >= _buf.Count || !MatchAt(d, ByCoverage, laArr, k, 0, sub, _buf.Info[pos].Glyph))
            {
                return false;
            }
        }

        SetGlyph(i, replacement);
        return true;
    }
}
