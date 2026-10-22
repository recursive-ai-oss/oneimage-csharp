namespace OneImage.Text.Unicode;

/// <summary>Joining_Type values (ArabicShaping.txt). The numeric values index <see cref="BidiData.JoiningRanges"/>.</summary>
internal enum JoiningType : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Dual = 3,
    Causing = 4,
    Transparent = 5,
}

/// <summary>Per-character properties for right-to-left text beyond the algorithm itself: mirroring and Arabic joining.</summary>
internal static class UnicodeBidi
{
    /// <summary>Gets the Bidi_Mirroring_Glyph of a code point, or the code point itself when it has none.</summary>
    public static int Mirror(int cp)
    {
        if (cp < 0x28)
        {
            return cp;
        }

        ReadOnlySpan<int> t = BidiData.Mirroring;
        int lo = 0, hi = (t.Length / 2) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int a = t[mid * 2];
            if (cp < a)
            {
                hi = mid - 1;
            }
            else if (cp > a)
            {
                lo = mid + 1;
            }
            else
            {
                return t[(mid * 2) + 1];
            }
        }

        return cp;
    }

    /// <summary>Gets the Joining_Type of a code point.</summary>
    public static JoiningType GetJoiningType(int cp)
    {
        if (cp < 0x300)
        {
            return cp == 0xAD ? JoiningType.Transparent : JoiningType.None;
        }

        ReadOnlySpan<int> t = BidiData.JoiningRanges;
        int lo = 0, hi = (t.Length / 3) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < t[mid * 3])
            {
                hi = mid - 1;
            }
            else if (cp > t[(mid * 3) + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return (JoiningType)t[(mid * 3) + 2];
            }
        }

        return JoiningType.None;
    }
}
