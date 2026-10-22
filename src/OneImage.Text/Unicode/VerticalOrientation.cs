namespace OneImage.Text.Unicode;

/// <summary>The Vertical_Orientation property (UAX #50): how a character is set in vertical text.</summary>
internal enum VerticalOrientationClass : byte
{
    /// <summary>Rotated a quarter turn clockwise (the default).</summary>
    R = 0,

    /// <summary>Upright.</summary>
    U = 1,

    /// <summary>Rotated unless the font has a vertical form.</summary>
    Tr = 2,

    /// <summary>Upright unless the font has no vertical form.</summary>
    Tu = 3,
}

internal static class VerticalOrientation
{
    /// <summary>The orientation of a code point; code points the data does not list are rotated.</summary>
    public static VerticalOrientationClass Of(int cp)
    {
        ReadOnlySpan<int> first = VerticalOrientationData.First;
        ReadOnlySpan<int> last = VerticalOrientationData.Last;
        int lo = 0;
        int hi = first.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (cp < first[mid])
            {
                hi = mid - 1;
            }
            else if (cp > last[mid])
            {
                lo = mid + 1;
            }
            else
            {
                return (VerticalOrientationClass)VerticalOrientationData.Value[mid];
            }
        }

        return VerticalOrientationClass.R;
    }
}
