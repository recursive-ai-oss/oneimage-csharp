namespace OneImage.Text.Unicode;

/// <summary>Script, Indic_Syllabic_Category, Indic_Positional_Category and canonical decompositions for the Indic and Universal Shaping Engine scripts.</summary>
internal static class IndicProps
{
    /// <summary>Looks a code point up; returns <see cref="ShapedScript.None"/> (and Other/NA categories) for anything outside the tables.</summary>
    public static ShapedScript Lookup(int cp, out IndicSyllabicCategory isc, out IndicPositionalCategory ipc)
    {
        ReadOnlySpan<int> first = IndicData.RangeFirst;
        int lo = 0;
        int hi = first.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (first[mid] > cp)
            {
                hi = mid - 1;
            }
            else if (IndicData.RangeLast[mid] < cp)
            {
                lo = mid + 1;
            }
            else
            {
                int v = IndicData.RangeValue[mid];
                isc = (IndicSyllabicCategory)((v >> 8) & 0xFF);
                ipc = (IndicPositionalCategory)((v >> 16) & 0xFF);
                return (ShapedScript)(v & 0xFF);
            }
        }

        isc = IndicSyllabicCategory.Other;
        ipc = IndicPositionalCategory.NA;
        return ShapedScript.None;
    }

    /// <summary>The script of a code point, or <see cref="ShapedScript.None"/>.</summary>
    public static ShapedScript ScriptOf(int cp) => Lookup(cp, out _, out _);

    /// <summary>Splits a code point with a two-part canonical decomposition (a split vowel sign, a nukta consonant).</summary>
    public static bool Decompose(int cp, out int first, out int second)
    {
        ReadOnlySpan<int> chars = IndicData.DecompositionChar;
        int lo = 0;
        int hi = chars.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (chars[mid] < cp)
            {
                lo = mid + 1;
            }
            else if (chars[mid] > cp)
            {
                hi = mid - 1;
            }
            else
            {
                first = IndicData.DecompositionFirst[mid];
                second = IndicData.DecompositionSecond[mid];
                return true;
            }
        }

        first = second = 0;
        return false;
    }
}
