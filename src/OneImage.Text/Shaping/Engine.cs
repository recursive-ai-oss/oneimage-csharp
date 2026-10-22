using OneImage.Text.Fonts;

namespace OneImage.Text.Shaping;

/// <summary>
/// Applies GSUB or GPOS lookups to a glyph run. Lookup processing follows the OpenType specification: lookups run in the order the
/// plan lists them; each walks the run left to right (type 8 right to left), skipping glyphs its flags exclude, and tries its
/// subtables in order until one applies. Contexts run nested lookups at the positions they matched.
/// </summary>
/// <remarks>
/// Fonts are untrusted. Every read goes through the bounds-checked <see cref="Be"/> helpers, a subtable that cannot be read is
/// skipped for that glyph, nesting is limited to <see cref="MaxDepth"/>, and the work done per run (<see cref="Exhausted"/>) and
/// the size the run may grow to are bounded, so a hostile table cannot hang or exhaust memory; it only stops being applied.
/// </remarks>
internal sealed partial class Engine
{
    /// <summary>How deep contexts may nest lookups inside lookups.</summary>
    public const int MaxDepth = 6;

    // Subtable attempts are the unit of work. A subtable that fails to read costs a lot more than one that does not, so it is
    // charged more: a table made only of broken subtables runs out of budget quickly.
    private const int BrokenSubtableCost = 2048;

    private readonly LayoutTable _table;
    private readonly Gdef? _gdef;
    private readonly GlyphBuffer _buf;
    private readonly int _numGlyphs;
    private readonly bool _gpos;
    private readonly long _maxOps;
    private readonly int _maxGlyphs;
    private readonly int[][] _match = new int[MaxDepth + 2][];
    private readonly bool _rtl;
    private uint _mask;
    private int _flags;
    private int _markSet;
    private int _value;
    private int _depth;
    private long _ops;
    private ushort _ligCounter;

    public Engine(LayoutTable table, Gdef? gdef, GlyphBuffer buffer, int numGlyphs, int textLength, bool rtl = false)
    {
        _rtl = rtl;
        _table = table;
        _gdef = gdef;
        _buf = buffer;
        _numGlyphs = numGlyphs;
        _gpos = table.IsGpos;
        _maxOps = Math.Min(65536L + (1024L * Math.Max(textLength, buffer.Count)), 1L << 26);
        _maxGlyphs = (int)Math.Min((8L * Math.Max(textLength, 1)) + 256, 1 << 24);
    }

    /// <summary>When set, zero width joiners and non-joiners are never skipped, whatever the lookup flags say, so they block a match.</summary>
    public bool KeepJoiners { get; set; }

    /// <summary>True once the run used up its work budget; the lookups after that point were not applied.</summary>
    public bool Exhausted { get; private set; }

    /// <summary>Runs the planned lookups in order.</summary>
    public void Run(PlannedLookup[] lookups)
    {
        foreach (PlannedLookup pl in lookups)
        {
            if (Exhausted)
            {
                return;
            }

            Lookup? lk = _table.GetLookup(pl.Index);
            if (lk is null)
            {
                continue;
            }

            _value = pl.Value;
            _mask = pl.Mask;
            _flags = lk.Flags;
            _markSet = lk.MarkSet;
            _depth = 0;
            try
            {
                RunLookup(lk);
            }
            catch (FontFormatException)
            {
                // Unreadable class data or the like: the rest of this lookup is skipped.
                _ops += BrokenSubtableCost;
            }
        }
    }

    private void RunLookup(Lookup lk)
    {
        bool reverse = !_gpos && lk.Count > 0 && lk.Types[0] == 8;
        if (reverse)
        {
            for (int i = _buf.Count - 1; i >= 0 && !Exhausted; i--)
            {
                if (i < _buf.Count && !Skip(i) && !Masked(i))
                {
                    TrySubtables(lk, i, out _);
                }
            }

            return;
        }

        for (int i = 0; i < _buf.Count && !Exhausted;)
        {
            // A rule moves on past what it matched; only a deletion leaves the position where it was, and that shrinks the run.
            i = !Skip(i) && !Masked(i) && TrySubtables(lk, i, out int next) ? next : i + 1;
        }
    }

    // A planned lookup with a mask only starts at glyphs that carry one of its bits; contexts and nested lookups are not masked.
    private bool Masked(int i) => _mask != 0 && (_buf.Info[i].Mask & _mask) == 0;

    private bool TrySubtables(Lookup lk, int i, out int next)
    {
        next = i + 1;
        for (int s = 0; s < lk.Count; s++)
        {
            byte type = lk.Types[s];
            if (type == 0 || (!_gpos && type == 8 && _depth > 0))
            {
                continue;
            }

            if (++_ops > _maxOps)
            {
                Exhausted = true;
                return false;
            }

            try
            {
                if (ApplySubtable(type, lk.Offsets[s], i, out next))
                {
                    return true;
                }
            }
            catch (FontFormatException)
            {
                _ops += BrokenSubtableCost;
            }
        }

        return false;
    }

    private bool ApplySubtable(int type, int sub, int i, out int next)
    {
        next = i + 1;
        if (_gpos)
        {
            return type switch
            {
                1 => SinglePos(sub, i),
                2 => PairPos(sub, i, out next),
                3 => CursivePos(sub, i),
                4 => MarkToBase(sub, i),
                5 => MarkToLigature(sub, i),
                6 => MarkToMark(sub, i),
                7 => Context(sub, i, chained: false, out next),
                8 => Context(sub, i, chained: true, out next),
                _ => false,
            };
        }

        return type switch
        {
            1 => SingleSubst(sub, i),
            2 => MultipleSubst(sub, i, out next),
            3 => AlternateSubst(sub, i),
            4 => LigatureSubst(sub, i),
            5 => Context(sub, i, chained: false, out next),
            6 => Context(sub, i, chained: true, out next),
            8 => ReverseChainSubst(sub, i),
            _ => false,
        };
    }

    // ---- Glyph skipping (lookup flags, OpenType "LookupFlag") ----

    private bool Skip(int i)
    {
        int f = _flags;
        if (f == 0)
        {
            return false;
        }

        ref GlyphInfo g = ref _buf.Info[i];
        if (KeepJoiners && g.CodePoint is 0x200C or 0x200D)
        {
            return false;
        }

        switch (g.Class)
        {
            case Gdef.ClassBase:
                return (f & 0x2) != 0;
            case Gdef.ClassLigature:
                return (f & 0x4) != 0;
            case Gdef.ClassMark:
                if ((f & 0x8) != 0)
                {
                    return true;
                }

                if ((f & 0x10) != 0)
                {
                    // A mark filtering set takes precedence over the mark attachment type.
                    return _gdef is not null && !_gdef.InMarkSet(_markSet, g.Glyph);
                }

                if ((f & 0xFF00) != 0)
                {
                    return _gdef is not null && _gdef.MarkAttachClass(g.Glyph) != (f >> 8);
                }

                return false;
            default:
                return false;
        }
    }

    private int NextUnskipped(int i)
    {
        int j = i + 1;
        while (j < _buf.Count && Skip(j))
        {
            j++;
        }

        return j;
    }

    private int PrevUnskipped(int i)
    {
        int j = i - 1;
        while (j >= 0 && Skip(j))
        {
            j--;
        }

        return j;
    }

    private byte ClassFor(int glyph, byte fallback) =>
        _gdef is { HasGlyphClasses: true } gdef ? (byte)Math.Min(gdef.GlyphClass(glyph), 255) : fallback;

    private int[] MatchArray(int needed)
    {
        int[]? slot = _match[_depth];
        if (slot is null || slot.Length < needed)
        {
            slot = new int[Math.Max(needed, 8)];
            _match[_depth] = slot;
        }

        return slot;
    }

    // ---- Contexts and chained contexts (GSUB 5/6, GPOS 7/8) ----

    // How one element of a rule is compared with a glyph.
    private const int ByGlyph = 0;
    private const int ByClass = 1;
    private const int ByCoverage = 2;

    private bool Context(int sub, int i, bool chained, out int next)
    {
        next = i + 1;
        ReadOnlySpan<byte> d = _table.Data.Span;
        int format = Be.U16(d, sub);
        int glyph = _buf.Info[i].Glyph;
        if (format == 1 || format == 2)
        {
            int ci = OtCommon.CoverageIndex(d, OtCommon.Offset16(d, sub, sub + 2), glyph);
            if (ci < 0)
            {
                return false;
            }

            int btCd = 0, inCd = 0, laCd = 0, setsField, setIndex, kind;
            if (format == 1)
            {
                kind = ByGlyph;
                setIndex = ci;
                setsField = sub + 4;
            }
            else
            {
                kind = ByClass;
                if (chained)
                {
                    btCd = OtCommon.Offset16(d, sub, sub + 4);
                    inCd = OtCommon.Offset16(d, sub, sub + 6);
                    laCd = OtCommon.Offset16(d, sub, sub + 8);
                    setsField = sub + 10;
                }
                else
                {
                    inCd = OtCommon.Offset16(d, sub, sub + 4);
                    setsField = sub + 6;
                }

                setIndex = OtCommon.ClassOf(d, inCd, glyph);
            }

            if (setIndex >= Be.U16(d, setsField))
            {
                return false;
            }

            int set = OtCommon.Offset16(d, sub, setsField + 2 + (2 * setIndex));
            if (set == 0)
            {
                return false;
            }

            int ruleCount = Be.U16(d, set);
            for (int r = 0; r < ruleCount; r++)
            {
                int rule = OtCommon.Offset16(d, set, set + 2 + (2 * r));
                if (rule != 0 && TryRule(d, sub, rule, chained, kind, btCd, inCd, laCd, i, out next))
                {
                    return true;
                }

                if (Exhausted)
                {
                    return false;
                }
            }

            return false;
        }

        if (format == 3)
        {
            int btArr = 0, btCount = 0, laArr = 0, laCount = 0, inCount, inArr, p, substCount, rec;
            if (chained)
            {
                btCount = Be.U16(d, sub + 2);
                btArr = sub + 4;
                p = btArr + (2 * btCount);
                inCount = Be.U16(d, p);
                inArr = p + 2;
                p = inArr + (2 * inCount);
                laCount = Be.U16(d, p);
                laArr = p + 2;
                p = laArr + (2 * laCount);
                substCount = Be.U16(d, p);
                rec = p + 2;
            }
            else
            {
                inCount = Be.U16(d, sub + 2);
                substCount = Be.U16(d, sub + 4);
                inArr = sub + 6;
                rec = inArr + (2 * inCount);
            }

            if (inCount == 0 || !MatchAt(d, ByCoverage, inArr, 0, 0, sub, glyph))
            {
                return false;
            }

            return MatchAndRun(d, sub, ByCoverage, btArr, btCount, 0, inArr, inCount, 0, laArr, laCount, 0, rec, substCount, i, out next);
        }

        return false;
    }

    // A rule of a format 1 or 2 context: input elements after the first, optional backtrack and lookahead, then the lookup records.
    private bool TryRule(ReadOnlySpan<byte> d, int sub, int rule, bool chained, int kind, int btCd, int inCd, int laCd, int i, out int next)
    {
        next = i + 1;
        int p = rule;
        int btCount = 0, btArr = 0, laCount = 0, laArr = 0, inCount, inArr, substCount, rec;
        if (chained)
        {
            btCount = Be.U16(d, p);
            btArr = p + 2;
            p = btArr + (2 * btCount);
            inCount = Be.U16(d, p);
            if (inCount == 0)
            {
                return false;
            }

            inArr = p + 2;
            p = inArr + (2 * (inCount - 1));
            laCount = Be.U16(d, p);
            laArr = p + 2;
            p = laArr + (2 * laCount);
            substCount = Be.U16(d, p);
            rec = p + 2;
        }
        else
        {
            inCount = Be.U16(d, p);
            substCount = Be.U16(d, p + 2);
            if (inCount == 0)
            {
                return false;
            }

            inArr = p + 4;
            rec = inArr + (2 * (inCount - 1));
        }

        // Element k (k >= 1) of the input sits at inArr + 2 * (k - 1): shift the base so MatchAt can index it by k.
        return MatchAndRun(d, sub, kind, btArr, btCount, btCd, inArr - 2, inCount, inCd, laArr, laCount, laCd, rec, substCount, i, out next);
    }

    private static bool MatchAt(ReadOnlySpan<byte> d, int kind, int arr, int k, int classDef, int sub, int glyph)
    {
        int v = Be.U16(d, arr + (2 * k));
        switch (kind)
        {
            case ByGlyph:
                return glyph == v;
            case ByClass:
                return OtCommon.ClassOf(d, classDef, glyph) == v;
            default:
                return v != 0 && OtCommon.CoverageIndex(d, sub + v, glyph) >= 0;
        }
    }

    private bool MatchAndRun(
        ReadOnlySpan<byte> d, int sub, int kind, int btArr, int btCount, int btCd, int inArr, int inCount, int inCd,
        int laArr, int laCount, int laCd, int rec, int substCount, int i, out int next)
    {
        next = i + 1;
        if (inCount > _buf.Count || (long)rec + (4L * substCount) > d.Length)
        {
            return false;
        }

        int[] mp = MatchArray(inCount);
        mp[0] = i;
        int pos = i;
        for (int k = 1; k < inCount; k++)
        {
            pos = NextUnskipped(pos);
            if (pos >= _buf.Count || !MatchAt(d, kind, inArr, k, inCd, sub, _buf.Info[pos].Glyph))
            {
                return false;
            }

            mp[k] = pos;
        }

        int last = pos;
        pos = i;
        for (int k = 0; k < btCount; k++)
        {
            pos = PrevUnskipped(pos);
            if (pos < 0 || !MatchAt(d, kind, btArr, k, btCd, sub, _buf.Info[pos].Glyph))
            {
                return false;
            }
        }

        pos = last;
        for (int k = 0; k < laCount; k++)
        {
            pos = NextUnskipped(pos);
            if (pos >= _buf.Count || !MatchAt(d, kind, laArr, k, laCd, sub, _buf.Info[pos].Glyph))
            {
                return false;
            }
        }

        // Nested lookups may insert or delete glyphs: later matched positions move with them.
        int totalDelta = 0;
        for (int r = 0; r < substCount; r++)
        {
            int seq = Be.U16(d, rec + (4 * r));
            int li = Be.U16(d, rec + (4 * r) + 2);
            if (seq >= inCount || mp[seq] < 0)
            {
                continue;
            }

            int at = mp[seq];
            int before = _buf.Count;
            ApplyNested(li, at);
            int delta = _buf.Count - before;
            if (delta != 0)
            {
                totalDelta += delta;
                for (int k = 0; k < inCount; k++)
                {
                    if (k != seq && mp[k] > at)
                    {
                        mp[k] = mp[k] + delta <= at ? -1 : mp[k] + delta;
                    }
                }
            }

            if (Exhausted)
            {
                break;
            }
        }

        next = Math.Max(last + totalDelta + 1, i + 1);
        return true;
    }

    private void ApplyNested(int lookupIndex, int at)
    {
        if (_depth >= MaxDepth || at >= _buf.Count)
        {
            return;
        }

        Lookup? lk = _table.GetLookup(lookupIndex);
        if (lk is null)
        {
            return;
        }

        int savedFlags = _flags, savedSet = _markSet;
        _depth++;
        _flags = lk.Flags;
        _markSet = lk.MarkSet;
        try
        {
            TrySubtables(lk, at, out _);
        }
        finally
        {
            _depth--;
            _flags = savedFlags;
            _markSet = savedSet;
        }
    }
}
