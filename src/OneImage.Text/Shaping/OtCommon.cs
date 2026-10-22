using OneImage.Text.Fonts;

namespace OneImage.Text.Shaping;

/// <summary>
/// Coverage tables and class definitions shared by GDEF, GSUB and GPOS (OpenType "Common Table Formats"). Offsets are absolute
/// positions in the table; 0 stands for a null offset. Every read is bounds-checked, so a hostile table can only throw
/// <see cref="FontFormatException"/>.
/// </summary>
internal static class OtCommon
{
    /// <summary>The position of <paramref name="glyph"/> in the coverage table at <paramref name="cov"/>, or -1.</summary>
    public static int CoverageIndex(ReadOnlySpan<byte> d, int cov, int glyph)
    {
        if (cov == 0 || glyph < 0)
        {
            return -1;
        }

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

    /// <summary>The class of <paramref name="glyph"/> in the class definition at <paramref name="cd"/>; 0 when absent or null.</summary>
    public static int ClassOf(ReadOnlySpan<byte> d, int cd, int glyph)
    {
        if (cd == 0)
        {
            return 0;
        }

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

    /// <summary>An absolute offset from a base and a 16-bit relative offset stored at <paramref name="field"/>; 0 when the stored offset is null.</summary>
    public static int Offset16(ReadOnlySpan<byte> d, int baseOffset, int field)
    {
        int rel = Be.U16(d, field);
        return rel == 0 ? 0 : baseOffset + rel;
    }
}

/// <summary>
/// The Glyph Definition table: glyph classes (base, ligature, mark, component), mark attachment classes and mark glyph sets, which
/// lookup flags use to skip glyphs. A table whose header is unusable is treated as absent.
/// </summary>
internal sealed class Gdef
{
    public const int ClassBase = 1;
    public const int ClassLigature = 2;
    public const int ClassMark = 3;
    public const int ClassComponent = 4;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _classDef;
    private readonly int _markAttachClassDef;
    private readonly int[] _markSets;

    private Gdef(ReadOnlyMemory<byte> data, int classDef, int markAttach, int[] markSets)
    {
        _data = data;
        _classDef = classDef;
        _markAttachClassDef = markAttach;
        _markSets = markSets;
    }

    /// <summary>Whether the table defines glyph classes at all. When it does not, the shaper derives them from the characters.</summary>
    public bool HasGlyphClasses => _classDef != 0;

    public static Gdef? Create(ReadOnlyMemory<byte> data)
    {
        try
        {
            ReadOnlySpan<byte> d = data.Span;
            if (d.Length < 12 || Be.U16(d, 0) != 1)
            {
                return null;
            }

            int minor = Be.U16(d, 2);
            int classDef = OtCommon.Offset16(d, 0, 4);
            int markAttach = OtCommon.Offset16(d, 0, 10);
            int[] sets = [];
            if (minor >= 2 && d.Length >= 14)
            {
                int ms = OtCommon.Offset16(d, 0, 12);
                if (ms != 0 && Be.U16(d, ms) == 1)
                {
                    int n = Be.U16(d, ms + 2);
                    if (ms + 4L + (4L * n) > d.Length)
                    {
                        throw Be.Truncated();
                    }

                    sets = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        long off = ms + (long)Be.U32(d, ms + 4 + (4 * i));
                        sets[i] = off < d.Length ? (int)off : 0;
                    }
                }
            }

            return new Gdef(data, classDef, markAttach, sets);
        }
        catch (FontFormatException)
        {
            return null;
        }
    }

    /// <summary>The GDEF class of a glyph, 0 when it has none.</summary>
    public int GlyphClass(int glyph) => OtCommon.ClassOf(_data.Span, _classDef, glyph);

    public int MarkAttachClass(int glyph) => OtCommon.ClassOf(_data.Span, _markAttachClassDef, glyph);

    public bool InMarkSet(int set, int glyph) =>
        (uint)set < (uint)_markSets.Length && OtCommon.CoverageIndex(_data.Span, _markSets[set], glyph) >= 0;
}
