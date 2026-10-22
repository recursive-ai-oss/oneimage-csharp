namespace OneImage.Text.Fonts;

/// <summary>
/// The pair-positioning part of <c>GPOS</c> that kerning needs: the lookups of the <c>kern</c> feature for one script, applied to
/// adjacent glyph pairs. Reads the tables in place. Lookup type 2 (pair adjustment, formats 1 and 2) and type 9 (extension) are
/// used; only the first glyph's horizontal advance adjustment is returned. A later shaping stage replaces this with the full engine.
/// </summary>
internal sealed class GposKerning
{
    // A font that lists many lookups with many subtables can only make kerning slow, never wrong; these bounds keep one pair lookup cheap.
    private const int MaxLookups = 32;
    private const int MaxSubtables = 64;

    private readonly ReadOnlyMemory<byte> _gpos;
    private readonly int _lookupList;
    private readonly int[] _lookups;

    private GposKerning(ReadOnlyMemory<byte> gpos, int lookupList, int[] lookups)
    {
        _gpos = gpos;
        _lookupList = lookupList;
        _lookups = lookups;
    }

    public static GposKerning? Create(ReadOnlyMemory<byte> gpos)
    {
        ReadOnlySpan<byte> d = gpos.Span;
        if (d.Length < 10 || Be.U16(d, 0) != 1)
        {
            return null;
        }

        int scriptList = Be.U16(d, 4);
        int featureList = Be.U16(d, 6);
        int lookupList = Be.U16(d, 8);
        if (scriptList == 0 || featureList == 0 || lookupList == 0)
        {
            return null;
        }

        int langSys = FindLangSys(d, scriptList);
        if (langSys < 0)
        {
            return null;
        }

        var wanted = new SortedSet<int>();
        int featureCount = Be.U16(d, featureList);
        int required = Be.U16(d, langSys + 2);
        int count = Be.U16(d, langSys + 4);
        if (langSys + 6L + (2L * count) > d.Length)
        {
            throw Be.Truncated();
        }

        for (int i = -1; i < count; i++)
        {
            int fi = i < 0 ? required : Be.U16(d, langSys + 6 + (2 * i));
            if (fi == 0xFFFF || fi >= featureCount)
            {
                continue;
            }

            int rec = featureList + 2 + (6 * fi);
            if (Be.U32(d, rec) != Be.Tag("kern"))
            {
                continue;
            }

            int feature = featureList + Be.U16(d, rec + 4);
            int nLookups = Be.U16(d, feature + 2);
            if (feature + 4L + (2L * nLookups) > d.Length)
            {
                throw Be.Truncated();
            }

            for (int k = 0; k < nLookups && wanted.Count < MaxLookups; k++)
            {
                wanted.Add(Be.U16(d, feature + 4 + (2 * k)));
            }
        }

        return wanted.Count == 0 ? null : new GposKerning(gpos, lookupList, [.. wanted]);
    }

    // Picks the Latin script's default language system, else the default script's, else the first script that has one.
    private static int FindLangSys(ReadOnlySpan<byte> d, int scriptList)
    {
        int n = Be.U16(d, scriptList);
        if (scriptList + 2L + (6L * n) > d.Length)
        {
            throw Be.Truncated();
        }

        int found = -1;
        int foundRank = int.MaxValue;
        for (int i = 0; i < n; i++)
        {
            uint tag = Be.U32(d, scriptList + 2 + (6 * i));
            int script = scriptList + Be.U16(d, scriptList + 6 + (6 * i));
            int def = Be.U16(d, script);
            if (def == 0)
            {
                continue;
            }

            int rank = tag == Be.Tag("latn") ? 0 : (tag == Be.Tag("DFLT") ? 1 : 2);
            if (rank < foundRank)
            {
                found = script + def;
                foundRank = rank;
            }
        }

        return found;
    }

    /// <summary>The horizontal advance adjustment for <paramref name="left"/> followed by <paramref name="right"/>, in font units.</summary>
    public int Get(int left, int right)
    {
        ReadOnlySpan<byte> d = _gpos.Span;
        int total = 0;
        int lookupCount = Be.U16(d, _lookupList);
        foreach (int li in _lookups)
        {
            if (li >= lookupCount)
            {
                continue;
            }

            int lookup = _lookupList + Be.U16(d, _lookupList + 2 + (2 * li));
            int type = Be.U16(d, lookup);
            int subCount = Math.Min((int)Be.U16(d, lookup + 4), MaxSubtables);
            if (lookup + 6L + (2L * subCount) > d.Length)
            {
                throw Be.Truncated();
            }

            for (int s = 0; s < subCount; s++)
            {
                int sub = lookup + Be.U16(d, lookup + 6 + (2 * s));
                int t = type;
                if (t == 9)
                {
                    if (Be.U16(d, sub) != 1)
                    {
                        continue;
                    }

                    t = Be.U16(d, sub + 2);
                    long ext = sub + (long)Be.U32(d, sub + 4);
                    if (ext >= d.Length)
                    {
                        throw Be.Truncated();
                    }

                    sub = (int)ext;
                }

                if (t != 2)
                {
                    continue;
                }

                if (TryPair(d, sub, left, right, out int adj))
                {
                    total += adj;

                    // The first subtable that covers the pair decides this lookup.
                    break;
                }
            }
        }

        return total;
    }

    private static bool TryPair(ReadOnlySpan<byte> d, int sub, int left, int right, out int xAdvance)
    {
        xAdvance = 0;
        int format = Be.U16(d, sub);
        int coverage = sub + Be.U16(d, sub + 2);
        int vf1 = Be.U16(d, sub + 4);
        int vf2 = Be.U16(d, sub + 6);
        int ci = CoverageIndex(d, coverage, left);
        if (ci < 0)
        {
            return false;
        }

        int size1 = ValueSize(vf1);
        int size2 = ValueSize(vf2);
        if (format == 1)
        {
            int setCount = Be.U16(d, sub + 8);
            if (ci >= setCount)
            {
                return false;
            }

            int set = sub + Be.U16(d, sub + 10 + (2 * ci));
            int n = Be.U16(d, set);
            int rec = 2 + size1 + size2;
            if (set + 2L + ((long)rec * n) > d.Length)
            {
                throw Be.Truncated();
            }

            int lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int p = set + 2 + (rec * mid);
                int g = Be.U16(d, p);
                if (g == right)
                {
                    xAdvance = XAdvance(d, p + 2, vf1);
                    return true;
                }

                if (g < right)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return false;
        }

        if (format == 2)
        {
            int cd1 = sub + Be.U16(d, sub + 8);
            int cd2 = sub + Be.U16(d, sub + 10);
            int n1 = Be.U16(d, sub + 12);
            int n2 = Be.U16(d, sub + 14);
            int c1 = ClassOf(d, cd1, left);
            int c2 = ClassOf(d, cd2, right);
            if (c1 >= n1 || c2 >= n2)
            {
                return false;
            }

            long rec = size1 + size2;
            long p = sub + 16L + (((c1 * (long)n2) + c2) * rec);
            if (p + rec > d.Length)
            {
                throw Be.Truncated();
            }

            xAdvance = XAdvance(d, (int)p, vf1);
            return true;
        }

        return false;
    }

    private static int ValueSize(int format) => System.Numerics.BitOperations.PopCount((uint)(format & 0xFF)) * 2;

    // XAdvance is the third field when present; skip the placement fields before it.
    private static int XAdvance(ReadOnlySpan<byte> d, int p, int format)
    {
        if ((format & 0x4) == 0)
        {
            return 0;
        }

        int skip = ((format & 1) != 0 ? 2 : 0) + ((format & 2) != 0 ? 2 : 0);
        return Be.I16(d, p + skip);
    }

    private static int CoverageIndex(ReadOnlySpan<byte> d, int cov, int glyph)
    {
        int fmt = Be.U16(d, cov);
        int n = Be.U16(d, cov + 2);
        if (fmt == 1)
        {
            if (cov + 4L + (2L * n) > d.Length)
            {
                throw Be.Truncated();
            }

            int lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int g = Be.U16(d, cov + 4 + (2 * mid));
                if (g == glyph)
                {
                    return mid;
                }

                if (g < glyph)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return -1;
        }

        if (fmt == 2)
        {
            if (cov + 4L + (6L * n) > d.Length)
            {
                throw Be.Truncated();
            }

            int lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int p = cov + 4 + (6 * mid);
                int s = Be.U16(d, p);
                int e = Be.U16(d, p + 2);
                if (glyph < s)
                {
                    hi = mid - 1;
                }
                else if (glyph > e)
                {
                    lo = mid + 1;
                }
                else
                {
                    return Be.U16(d, p + 4) + (glyph - s);
                }
            }
        }

        return -1;
    }

    private static int ClassOf(ReadOnlySpan<byte> d, int cd, int glyph)
    {
        int fmt = Be.U16(d, cd);
        if (fmt == 1)
        {
            int start = Be.U16(d, cd + 2);
            int n = Be.U16(d, cd + 4);
            int i = glyph - start;
            return i >= 0 && i < n ? Be.U16(d, cd + 6 + (2 * i)) : 0;
        }

        if (fmt == 2)
        {
            int n = Be.U16(d, cd + 2);
            if (cd + 4L + (6L * n) > d.Length)
            {
                throw Be.Truncated();
            }

            int lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int p = cd + 4 + (6 * mid);
                int s = Be.U16(d, p);
                int e = Be.U16(d, p + 2);
                if (glyph < s)
                {
                    hi = mid - 1;
                }
                else if (glyph > e)
                {
                    lo = mid + 1;
                }
                else
                {
                    return Be.U16(d, p + 4);
                }
            }
        }

        return 0;
    }
}
