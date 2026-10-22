namespace OneImage.Text.Fonts;

/// <summary>Character to glyph mapping (<c>cmap</c>). Reads the best Unicode subtable in place; formats 0, 4, 6, 12 and 13.</summary>
internal sealed class CmapTable
{
    private readonly ReadOnlyMemory<byte> _sub;
    private readonly int _format;
    private readonly bool _symbol;

    private CmapTable(ReadOnlyMemory<byte> sub, int format, bool symbol)
    {
        _sub = sub;
        _format = format;
        _symbol = symbol;
    }

    public static CmapTable? Create(ReadOnlyMemory<byte> table)
    {
        ReadOnlySpan<byte> d = table.Span;
        if (d.Length < 4)
        {
            return null;
        }

        int n = Be.U16(d, 2);
        if (4L + (8L * n) > d.Length)
        {
            throw Be.Truncated();
        }

        int best = -1;
        int bestScore = -1;
        bool symbol = false;
        for (int i = 0; i < n; i++)
        {
            int platform = Be.U16(d, 4 + (8 * i));
            int encoding = Be.U16(d, 6 + (8 * i));
            uint off = Be.U32(d, 8 + (8 * i));
            int score = (platform, encoding) switch
            {
                (3, 10) => 6,
                (0, 4) or (0, 6) => 5,
                (3, 1) => 4,
                (0, _) => 3,
                (3, 0) => 2,
                (1, 0) => 1,
                _ => -1,
            };
            if (score > bestScore && off < d.Length - 2U)
            {
                int fmt = Be.U16(d, (int)off);
                if (fmt is 0 or 4 or 6 or 12 or 13)
                {
                    best = (int)off;
                    bestScore = score;
                    symbol = platform == 3 && encoding == 0;
                }
            }
        }

        if (best < 0)
        {
            return null;
        }

        return new CmapTable(table[best..], Be.U16(d, best), symbol);
    }

    /// <summary>Returns the glyph for a code point, or 0 when the font has none.</summary>
    public int Lookup(int cp)
    {
        int g = LookupExact(cp);
        if (g == 0 && _symbol && cp < 0x100)
        {
            // Symbol fonts keep their characters in the private-use block F000..F0FF.
            g = LookupExact(0xF000 + cp);
        }

        return g;
    }

    private int LookupExact(int cp)
    {
        ReadOnlySpan<byte> d = _sub.Span;
        switch (_format)
        {
            case 0:
                return cp is >= 0 and < 256 ? Be.U8(d, 6 + cp) : 0;
            case 4:
                return Format4(d, cp);
            case 6:
            {
                int first = Be.U16(d, 6);
                int count = Be.U16(d, 8);
                int i = cp - first;
                return i >= 0 && i < count ? Be.U16(d, 10 + (2 * i)) : 0;
            }

            case 12:
            case 13:
                return Format12(d, cp, _format == 13);
            default:
                return 0;
        }
    }

    private static int Format4(ReadOnlySpan<byte> d, int cp)
    {
        if (cp > 0xFFFF || cp < 0)
        {
            return 0;
        }

        int segX2 = Be.U16(d, 6);
        int seg = segX2 / 2;
        int endPos = 14;
        int startPos = endPos + segX2 + 2;
        int deltaPos = startPos + segX2;
        int rangePos = deltaPos + segX2;
        if ((long)rangePos + segX2 > d.Length)
        {
            throw Be.Truncated();
        }

        int lo = 0, hi = seg - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Be.U16(d, endPos + (2 * mid)) < cp)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        if (seg == 0 || Be.U16(d, endPos + (2 * lo)) < cp)
        {
            return 0;
        }

        int start = Be.U16(d, startPos + (2 * lo));
        if (start > cp)
        {
            return 0;
        }

        int delta = Be.U16(d, deltaPos + (2 * lo));
        int ro = Be.U16(d, rangePos + (2 * lo));
        if (ro == 0)
        {
            return (cp + delta) & 0xFFFF;
        }

        // The range offset is relative to its own position in the idRangeOffset array.
        int g = Be.U16(d, rangePos + (2 * lo) + ro + (2 * (cp - start)));
        return g == 0 ? 0 : (g + delta) & 0xFFFF;
    }

    private static int Format12(ReadOnlySpan<byte> d, int cp, bool constant)
    {
        uint n = Be.U32(d, 12);
        if (16L + (12L * n) > d.Length)
        {
            throw Be.Truncated();
        }

        int lo = 0, hi = (int)n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int p = 16 + (12 * mid);
            uint s = Be.U32(d, p);
            uint e = Be.U32(d, p + 4);
            if (cp < s)
            {
                hi = mid - 1;
            }
            else if (cp > e)
            {
                lo = mid + 1;
            }
            else
            {
                uint g = Be.U32(d, p + 8);
                long r = constant ? g : g + (cp - (long)s);
                return r is > 0 and <= 0xFFFF ? (int)r : 0;
            }
        }

        return 0;
    }
}

/// <summary>Reads strings from the <c>name</c> table.</summary>
internal static class NameTable
{
    public const int Copyright = 0;
    public const int Family = 1;
    public const int Subfamily = 2;
    public const int FullName = 4;
    public const int PostScriptName = 6;
    public const int TypographicFamily = 16;
    public const int TypographicSubfamily = 17;

    /// <summary>Finds a name, preferring Windows English, then any Windows or Unicode record, then Macintosh Roman.</summary>
    public static string? Get(ReadOnlyMemory<byte> table, int nameId)
    {
        ReadOnlySpan<byte> d = table.Span;
        if (d.Length < 6)
        {
            return null;
        }

        int count = Be.U16(d, 2);
        int strings = Be.U16(d, 4);
        if (6L + (12L * count) > d.Length)
        {
            count = (d.Length - 6) / 12;
        }

        int bestScore = -1;
        string? best = null;
        for (int i = 0; i < count; i++)
        {
            int r = 6 + (12 * i);
            if (Be.U16(d, r + 6) != nameId)
            {
                continue;
            }

            int platform = Be.U16(d, r);
            int lang = Be.U16(d, r + 4);
            int score = platform switch
            {
                3 => lang == 0x409 ? 4 : 3,
                0 => 2,
                1 => 1,
                _ => -1,
            };
            if (score <= bestScore)
            {
                continue;
            }

            int len = Be.U16(d, r + 8);
            int off = strings + Be.U16(d, r + 10);
            if (off < 0 || (long)off + len > d.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> s = d.Slice(off, len);
            best = platform == 1 ? System.Text.Encoding.Latin1.GetString(s) : System.Text.Encoding.BigEndianUnicode.GetString(s[..(len & ~1)]);
            bestScore = score;
        }

        return best;
    }
}

/// <summary>Legacy <c>kern</c> table, horizontal format 0 subtables (the Microsoft and Apple headers).</summary>
internal sealed class KernTable
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly (int Start, int Count)[] _subtables;

    private KernTable(ReadOnlyMemory<byte> data, (int Start, int Count)[] subtables)
    {
        _data = data;
        _subtables = subtables;
    }

    public static KernTable? Create(ReadOnlyMemory<byte> table)
    {
        ReadOnlySpan<byte> d = table.Span;
        if (d.Length < 4)
        {
            return null;
        }

        var list = new List<(int, int)>();
        bool apple = Be.U16(d, 0) == 1;
        long n;
        int p;
        if (apple)
        {
            n = Be.U32(d, 4);
            p = 8;
        }
        else
        {
            n = Be.U16(d, 2);
            p = 4;
        }

        for (long i = 0; i < n && p < d.Length && list.Count < 16; i++)
        {
            int length, format;
            bool horizontal, crossStream;
            int body;
            if (apple)
            {
                length = (int)Be.U32(d, p);
                int cov = Be.U8(d, p + 4);
                format = Be.U8(d, p + 5);
                horizontal = (cov & 0x80) == 0 && (cov & 0x20) == 0;
                crossStream = (cov & 0x40) != 0;
                body = p + 8;
            }
            else
            {
                length = Be.U16(d, p + 2);
                int cov = Be.U16(d, p + 4);
                format = cov >> 8;
                horizontal = (cov & 1) != 0;
                crossStream = (cov & 4) != 0;
                body = p + 6;
            }

            if (format == 0 && horizontal && !crossStream)
            {
                int pairs = Be.U16(d, body);
                int start = body + 8;
                if (start + (6L * pairs) <= d.Length)
                {
                    list.Add((start, pairs));
                }
            }

            if (length < 6)
            {
                break;
            }

            p += length;
        }

        return list.Count == 0 ? null : new KernTable(table, [.. list]);
    }

    /// <summary>Returns the adjustment to the first glyph's advance, in font units.</summary>
    public int Get(int left, int right)
    {
        ReadOnlySpan<byte> d = _data.Span;
        uint key = ((uint)left << 16) | (uint)right;
        int total = 0;
        foreach ((int start, int count) in _subtables)
        {
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                uint k = Be.U32(d, start + (6 * mid));
                if (k == key)
                {
                    total += Be.I16(d, start + (6 * mid) + 4);
                    break;
                }

                if (k < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
        }

        return total;
    }
}
