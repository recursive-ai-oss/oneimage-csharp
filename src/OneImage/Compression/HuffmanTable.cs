using System;

namespace OneImage.Compression;

/// <summary>
/// Builds table-driven Huffman decoding tables for DEFLATE (RFC 1951 3.2.2) in the layout the inflater
/// reads. Entry layout (uint):
/// bits 0-7 total code length in bits; bits 8-11 entry type; bits 12-15 extra-bit count (or subtable
/// index width for a subtable pointer); bits 16-31 value (literal, base, or subtable offset).
/// Codes longer than the root width go through second-level tables.
/// </summary>
internal static class HuffmanTable
{
    public const uint TypeLiteral = 0x000;
    public const uint TypeBase = 0x100;
    public const uint TypeEndOfBlock = 0x200;
    public const uint TypeSub = 0x300;
    public const uint TypeInvalid = 0x400;
    public const uint TypeMask = 0xF00;

    public const int LitRootBits = 10;
    public const int DistRootBits = 8;
    public const int ClenRootBits = 7;

    // Upper bounds on table size (root plus all subtables) for complete codes of 288 / 32 / 19 symbols.
    public const int LitTableSize = 2342;
    public const int DistTableSize = 600;
    public const int ClenTableSize = 1 << ClenRootBits;

    /// <summary>Symbol entries for the literal/length alphabet (288 symbols).</summary>
    public static readonly uint[] LitSymbols = BuildLitSymbols();

    /// <summary>Symbol entries for the distance alphabet (32 symbols; 30 and 31 are invalid).</summary>
    public static readonly uint[] DistSymbols = BuildDistSymbols();

    /// <summary>Symbol entries for the 19-symbol code-length alphabet (value = symbol).</summary>
    public static readonly uint[] ClenSymbols = BuildIdentity(19);

    /// <summary>The fixed literal/length table (RFC 1951 3.2.6), shared and read-only.</summary>
    public static readonly uint[] FixedLit = BuildFixedLit();

    /// <summary>The fixed distance table, shared and read-only.</summary>
    public static readonly uint[] FixedDist = BuildFixedDist();

    /// <summary>
    /// Builds a decoding table from code lengths. Returns null on success or a message describing why
    /// the lengths are not a valid prefix code. Incomplete codes are accepted only when
    /// <paramref name="allowIncomplete"/> is set and the code is empty or a single 1-bit code, as zlib does.
    /// </summary>
    public static string? Build(ReadOnlySpan<byte> lengths, ReadOnlySpan<uint> symbols, int rootBits, uint[] table, bool allowIncomplete)
    {
        Span<int> count = stackalloc int[16];
        int max = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int l = lengths[i];
            count[l]++;
            if (l > max)
            {
                max = l;
            }
        }

        count[0] = 0;
        int rootSize = 1 << rootBits;
        Array.Fill(table, TypeInvalid | 1u, 0, rootSize);
        if (max == 0)
        {
            return allowIncomplete ? null : "no codes";
        }

        int left = 1;
        for (int l = 1; l <= 15; l++)
        {
            left <<= 1;
            left -= count[l];
            if (left < 0)
            {
                return "oversubscribed code lengths";
            }
        }

        if (left > 0 && !(allowIncomplete && max == 1))
        {
            return "incomplete code lengths";
        }

        Span<int> offs = stackalloc int[17];
        for (int l = 1; l < 16; l++)
        {
            offs[l + 1] = offs[l] + count[l];
        }

        Span<ushort> sorted = stackalloc ushort[lengths.Length];
        for (int s = 0; s < lengths.Length; s++)
        {
            if (lengths[s] != 0)
            {
                sorted[offs[lengths[s]]++] = (ushort)s;
            }
        }

        // Walk codes in canonical order. 'code' is the MSB-first code value for the current length.
        int idx = 0;
        int code = 0;
        int next = rootSize;
        int curPrefix = -1;
        int subBase = 0;
        int subBits = 0;
        int rootMask = rootSize - 1;
        for (int len = 1; len <= max; len++)
        {
            int n = count[len];
            for (; n > 0; n--)
            {
                int sym = sorted[idx++];
                int rev = Reverse(code, len);
                if (len <= rootBits)
                {
                    uint entry = symbols[sym] | (uint)len;
                    for (int i = rev; i < rootSize; i += 1 << len)
                    {
                        table[i] = entry;
                    }
                }
                else
                {
                    int prefix = rev & rootMask;
                    if (prefix != curPrefix)
                    {
                        // Size the second-level table for all remaining codes sharing this prefix.
                        int curr = len - rootBits;
                        int room = 1 << curr;
                        int remainingAt = n;
                        // 'count' for the current length has been consumed down to n; lengths above are untouched.
                        int used = remainingAt;
                        while (curr + rootBits < max)
                        {
                            room -= used;
                            if (room <= 0)
                            {
                                break;
                            }

                            curr++;
                            room <<= 1;
                            used = count[curr + rootBits];
                        }

                        subBits = curr;
                        subBase = next;
                        if (subBase + (1 << subBits) > table.Length)
                        {
                            return "table overflow";
                        }

                        Array.Fill(table, TypeInvalid | 1u, subBase, 1 << subBits);
                        table[prefix] = TypeSub | ((uint)subBits << 12) | ((uint)subBase << 16);
                        next += 1 << subBits;
                        curPrefix = prefix;
                    }

                    uint entry = symbols[sym] | (uint)len;
                    int step = 1 << (len - rootBits);
                    for (int i = rev >> rootBits; i < (1 << subBits); i += step)
                    {
                        table[subBase + i] = entry;
                    }
                }

                code++;
            }

            code <<= 1;
        }

        return null;
    }

    private static int Reverse(int code, int len)
    {
        int r = 0;
        for (int i = 0; i < len; i++)
        {
            r = (r << 1) | (code & 1);
            code >>= 1;
        }

        return r;
    }

    private static uint[] BuildIdentity(int n)
    {
        var t = new uint[n];
        for (int i = 0; i < n; i++)
        {
            t[i] = TypeLiteral | ((uint)i << 16);
        }

        return t;
    }

    private static uint[] BuildLitSymbols()
    {
        var t = new uint[288];
        for (int i = 0; i < 256; i++)
        {
            t[i] = TypeLiteral | ((uint)i << 16);
        }

        t[256] = TypeEndOfBlock;
        for (int i = 0; i < 29; i++)
        {
            t[257 + i] = TypeBase | ((uint)DeflateTables.LengthExtra[i] << 12) | ((uint)DeflateTables.LengthBase[i] << 16);
        }

        t[286] = TypeInvalid;
        t[287] = TypeInvalid;
        return t;
    }

    private static uint[] BuildDistSymbols()
    {
        var t = new uint[32];
        for (int i = 0; i < 30; i++)
        {
            t[i] = TypeBase | ((uint)DeflateTables.DistExtra[i] << 12) | ((uint)DeflateTables.DistBase[i] << 16);
        }

        t[30] = TypeInvalid;
        t[31] = TypeInvalid;
        return t;
    }

    private static uint[] BuildFixedLit()
    {
        var lens = new byte[288];
        for (int i = 0; i < 288; i++)
        {
            lens[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
        }

        var table = new uint[LitTableSize];
        string? err = Build(lens, LitSymbols, LitRootBits, table, false);
        return err is null ? table : throw new InvalidOperationException(err);
    }

    private static uint[] BuildFixedDist()
    {
        var lens = new byte[32];
        Array.Fill(lens, (byte)5);
        var table = new uint[DistTableSize];
        string? err = Build(lens, DistSymbols, DistRootBits, table, false);
        return err is null ? table : throw new InvalidOperationException(err);
    }
}
