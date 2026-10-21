namespace OneImage.Codecs.WebP;

/// <summary>
/// Builds the two-level lookup tables used to decode VP8L prefix codes (RFC 9649 section 3.7.2.1). Codes are canonical
/// and packed least-significant-bit first, so table keys are bit-reversed codes. An entry is <c>(bits &lt;&lt; 16) | value</c>.
/// A root entry whose bit count exceeds <see cref="RootBits"/> points to a second-level table: its value is the offset
/// from the root entry to that table and <c>bits - RootBits</c> is the second-level index width.
/// </summary>
internal static class HuffmanTable
{
    public const int RootBits = 8;
    public const int MaxCodeLength = 15;

    /// <summary>Upper bound for the table memory of all prefix codes of one image, in entries (a guard against crafted headers).</summary>
    public const int MaxArenaEntries = 1 << 24;

    /// <summary>Builds a table for the given code lengths at <paramref name="baseOffset"/>, growing <paramref name="arena"/> when needed.</summary>
    /// <param name="lengths">Code length per symbol, 0 for unused symbols.</param>
    /// <param name="arena">Table memory.</param>
    /// <param name="baseOffset">Where the table starts in the arena.</param>
    /// <returns>The number of entries written, or 0 when the lengths do not describe a complete prefix code.</returns>
    public static int Build(ReadOnlySpan<byte> lengths, ref uint[] arena, int baseOffset)
    {
        Span<int> count = stackalloc int[MaxCodeLength + 1];
        Span<int> offset = stackalloc int[MaxCodeLength + 2];
        foreach (byte len in lengths)
        {
            if (len > MaxCodeLength)
            {
                return 0;
            }

            count[len]++;
        }

        int nsym = lengths.Length - count[0];
        if (nsym == 0)
        {
            return 0;
        }

        EnsureCapacity(ref arena, baseOffset + (1 << RootBits));
        if (nsym == 1)
        {
            // A single symbol is coded with zero bits whatever its nominal length is.
            int only = 0;
            while (lengths[only] == 0)
            {
                only++;
            }

            arena.AsSpan(baseOffset, 1 << RootBits).Fill((uint)only);
            return 1 << RootBits;
        }

        // The tree must be complete: neither over-subscribed nor with unused code space.
        int open = 1;
        for (int len = 1; len <= MaxCodeLength; len++)
        {
            open <<= 1;
            open -= count[len];
            if (open < 0)
            {
                return 0;
            }
        }

        if (open != 0)
        {
            return 0;
        }

        offset[1] = 0;
        for (int len = 1; len <= MaxCodeLength; len++)
        {
            offset[len + 1] = offset[len] + count[len];
        }

        Span<ushort> sorted = stackalloc ushort[nsym];
        for (int s = 0; s < lengths.Length; s++)
        {
            int len = lengths[s];
            if (len > 0)
            {
                sorted[offset[len]++] = (ushort)s;
            }
        }

        int key = 0;
        int symbol = 0;
        int step = 2;
        uint[] t = arena;
        for (int len = 1; len <= RootBits; len++, step <<= 1)
        {
            for (; count[len] > 0; count[len]--)
            {
                uint code = ((uint)len << 16) | sorted[symbol++];
                Replicate(t, baseOffset + key, step, 1 << RootBits, code);
                key = NextKey(key, len);
            }
        }

        int mask = (1 << RootBits) - 1;
        int low = -1;
        int tableOff = baseOffset;
        int tableSize = 1 << RootBits;
        int total = 1 << RootBits;
        step = 2;
        for (int len = RootBits + 1; len <= MaxCodeLength; len++, step <<= 1)
        {
            for (; count[len] > 0; count[len]--)
            {
                if ((key & mask) != low)
                {
                    tableOff += tableSize;
                    int bits = NextTableBitSize(count, len);
                    tableSize = 1 << bits;
                    total += tableSize;
                    low = key & mask;
                    EnsureCapacity(ref arena, tableOff + tableSize);
                    t = arena;
                    t[baseOffset + low] = ((uint)(bits + RootBits) << 16) | (uint)(tableOff - baseOffset - low);
                }

                uint code = ((uint)(len - RootBits) << 16) | sorted[symbol++];
                Replicate(t, tableOff + (key >> RootBits), step, tableSize, code);
                key = NextKey(key, len);
            }
        }

        return total;
    }

    private static void EnsureCapacity(ref uint[] arena, int needed)
    {
        if (needed <= arena.Length)
        {
            return;
        }

        if (needed > MaxArenaEntries)
        {
            throw new ImageLimitException("The image declares more prefix-code table memory than the decoder allows.");
        }

        Array.Resize(ref arena, Math.Min(MaxArenaEntries, Math.Max(needed, arena.Length * 2)));
    }

    private static void Replicate(uint[] table, int start, int step, int end, uint code)
    {
        do
        {
            end -= step;
            table[start + end] = code;
        }
        while (end > 0);
    }

    // Next bit-reversed canonical code of the same length.
    private static int NextKey(int key, int len)
    {
        int step = 1 << (len - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return step != 0 ? (key & (step - 1)) + step : key;
    }

    // Width of the second-level table starting at a code of length len, given the remaining counts.
    private static int NextTableBitSize(ReadOnlySpan<int> count, int len)
    {
        int left = 1 << (len - RootBits);
        while (len < MaxCodeLength)
        {
            left -= count[len];
            if (left <= 0)
            {
                break;
            }

            len++;
            left <<= 1;
        }

        return len - RootBits;
    }
}
