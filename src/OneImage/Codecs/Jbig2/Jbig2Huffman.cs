namespace OneImage.Codecs.Jbig2;

/// <summary>Reads bits most significant first from a byte range, as the Huffman variants of JBIG2 do (T.88 5.4.1).</summary>
internal sealed class Jbig2BitReader
{
    private readonly byte[] _data;
    private readonly long _endBit;
    private long _bit;

    /// <summary>Creates a reader over <paramref name="data"/>[<paramref name="start"/>..<paramref name="end"/>).</summary>
    public Jbig2BitReader(byte[] data, int start, int end)
    {
        _data = data;
        _bit = (long)start * 8;
        _endBit = (long)end * 8;
    }

    /// <summary>Gets the current byte offset into the underlying array, rounded up to a whole byte.</summary>
    public int BytePosition => (int)((_bit + 7) >> 3);

    /// <summary>Gets the end of the readable range, as a byte offset into the underlying array.</summary>
    public int End => (int)(_endBit >> 3);

    /// <summary>Gets the underlying array.</summary>
    public byte[] Data => _data;

    /// <summary>Reads one bit.</summary>
    /// <exception cref="ImageFormatException">The data ended.</exception>
    public int ReadBit()
    {
        if (_bit >= _endBit)
        {
            throw new ImageFormatException("JBIG2 data ended inside a Huffman-coded value.");
        }

        int v = (_data[(int)(_bit >> 3)] >> (7 - (int)(_bit & 7))) & 1;
        _bit++;
        return v;
    }

    /// <summary>Reads <paramref name="count"/> (0 to 32) bits as an unsigned value.</summary>
    public uint ReadBits(int count)
    {
        ulong v = 0;
        for (int i = 0; i < count; i++)
        {
            v = (v << 1) | (uint)ReadBit();
        }

        return (uint)v;
    }

    /// <summary>Skips to the next byte boundary.</summary>
    public void Align() => _bit = (_bit + 7) & ~7L;

    /// <summary>Moves forward by <paramref name="bytes"/> whole bytes from the current byte boundary.</summary>
    /// <exception cref="ImageFormatException">That passes the end of the data.</exception>
    public void SkipBytes(int bytes)
    {
        Align();
        if (bytes < 0 || _bit + ((long)bytes * 8) > _endBit)
        {
            throw new ImageFormatException("A JBIG2 size field points past the end of the segment.");
        }

        _bit += (long)bytes * 8;
    }
}

/// <summary>
/// A JBIG2 Huffman table (T.88 annex B): a set of lines, each a prefix code followed by a fixed number of
/// offset bits, plus optional lower-range, upper-range and out-of-band lines. Codes are assigned by B.3.
/// </summary>
internal sealed class Jbig2HuffmanTable
{
    private const int KindNormal = 0;
    private const int KindLower = 1;
    private const int KindUpper = 2;
    private const int KindOob = 3;

    private static readonly Lazy<Jbig2HuffmanTable>[] s_standard = BuildStandard();

    private readonly int[] _kind;
    private readonly int[] _rangeLength;
    private readonly long[] _rangeLow;
    private readonly int _maxLength;
    private readonly ulong[] _firstCode;
    private readonly int[] _count;
    private readonly int[] _firstIndex;
    private readonly int[] _order;

    private Jbig2HuffmanTable(List<(int PrefixLength, int RangeLength, long RangeLow, int Kind)> lines)
    {
        int n = lines.Count;
        _kind = new int[n];
        _rangeLength = new int[n];
        _rangeLow = new long[n];
        var prefix = new int[n];
        int max = 0;
        for (int i = 0; i < n; i++)
        {
            (prefix[i], _rangeLength[i], _rangeLow[i], _kind[i]) = lines[i];
            max = Math.Max(max, prefix[i]);
            if (_kind[i] == KindOob)
            {
                HasOutOfBand = true;
            }
        }

        _maxLength = max;
        _firstCode = new ulong[max + 2];
        _count = new int[max + 2];
        _firstIndex = new int[max + 2];
        _order = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (prefix[i] > 0)
            {
                _count[prefix[i]]++;
            }
        }

        // T.88 B.3: codes of one length are consecutive, in table-line order.
        int next = 0;
        ulong first = 0;
        for (int len = 1; len <= max; len++)
        {
            first = (first + (ulong)_count[len - 1]) << 1;
            _firstCode[len] = first;
            _firstIndex[len] = next;
            if (first + (ulong)_count[len] > (1UL << len))
            {
                throw new ImageFormatException("A JBIG2 Huffman table has more codes than its prefix lengths allow.");
            }

            for (int i = 0; i < n; i++)
            {
                if (prefix[i] == len)
                {
                    _order[next++] = i;
                }
            }
        }
    }

    /// <summary>Gets a value indicating whether the table can produce the out-of-band value.</summary>
    public bool HasOutOfBand { get; private set; }

    /// <summary>Returns standard table B.<paramref name="number"/> (1 to 15).</summary>
    public static Jbig2HuffmanTable Standard(int number) => s_standard[number - 1].Value;

    /// <summary>
    /// Builds a table from symbol code lengths, one line per symbol whose value is the symbol's index
    /// (the symbol ID table of T.88 7.4.3.1.7). A length of 0 leaves the symbol without a code.
    /// </summary>
    public static Jbig2HuffmanTable FromCodeLengths(ReadOnlySpan<int> lengths)
    {
        var lines = new List<(int, int, long, int)>(lengths.Length);
        for (int i = 0; i < lengths.Length; i++)
        {
            lines.Add((lengths[i], 0, i, KindNormal));
        }

        return new Jbig2HuffmanTable(lines);
    }

    /// <summary>Parses a code table segment's data (T.88 B.2).</summary>
    /// <param name="data">The array holding the segment data.</param>
    /// <param name="start">First byte of the table data.</param>
    /// <param name="end">End of the table data.</param>
    /// <param name="maxLines">The most table lines to accept.</param>
    public static Jbig2HuffmanTable Parse(byte[] data, int start, int end, int maxLines)
    {
        var r = new Jbig2BitReader(data, start, end);
        int flags = (int)r.ReadBits(8);
        bool oob = (flags & 1) != 0;
        int prefixBits = ((flags >> 1) & 7) + 1;
        int rangeBits = ((flags >> 4) & 7) + 1;
        if ((flags & 0x80) != 0)
        {
            throw new ImageFormatException("A JBIG2 code table has reserved flag bits set.");
        }

        long low = (int)r.ReadBits(32);
        long high = (int)r.ReadBits(32);
        var lines = new List<(int, int, long, int)>();
        long cur = low;
        while (cur < high)
        {
            int pl = (int)r.ReadBits(prefixBits);
            int rl = (int)r.ReadBits(rangeBits);
            if (rl > 32)
            {
                throw new ImageFormatException("A JBIG2 code table line has an offset longer than 32 bits.");
            }

            if (lines.Count >= maxLines)
            {
                throw new ImageLimitException("A JBIG2 code table has more lines than the limit allows.");
            }

            lines.Add((pl, rl, cur, KindNormal));
            cur += 1L << rl;
        }

        lines.Add(((int)r.ReadBits(prefixBits), 32, low - 1, KindLower));
        lines.Add(((int)r.ReadBits(prefixBits), 32, high, KindUpper));
        if (oob)
        {
            lines.Add(((int)r.ReadBits(prefixBits), 0, 0, KindOob));
        }

        return new Jbig2HuffmanTable(lines);
    }

    /// <summary>
    /// Decodes one value (T.88 B.4). Returns false for the out-of-band value.
    /// </summary>
    /// <exception cref="ImageFormatException">The bits are not a code of this table, or the value is out of 32-bit range.</exception>
    public bool Decode(Jbig2BitReader r, out int value)
    {
        ulong code = 0;
        for (int len = 1; len <= _maxLength; len++)
        {
            code = (code << 1) | (uint)r.ReadBit();
            int cnt = _count[len];
            if (cnt > 0 && code >= _firstCode[len] && code - _firstCode[len] < (ulong)cnt)
            {
                int line = _order[_firstIndex[len] + (int)(code - _firstCode[len])];
                int kind = _kind[line];
                if (kind == KindOob)
                {
                    value = 0;
                    return false;
                }

                long offset = _rangeLength[line] == 0 ? 0 : r.ReadBits(_rangeLength[line]);
                long v = kind == KindLower ? _rangeLow[line] - offset : _rangeLow[line] + offset;
                if (v is > int.MaxValue or < int.MinValue)
                {
                    throw new ImageFormatException("A JBIG2 Huffman value is outside the 32-bit range.");
                }

                value = (int)v;
                return true;
            }
        }

        throw new ImageFormatException("Invalid JBIG2 Huffman code.");
    }

    private static Lazy<Jbig2HuffmanTable>[] BuildStandard()
    {
        // Each entry: normal lines as (low, prefix length, range length) triples, then the lower line
        // (prefix length, value), the upper line (prefix length, value) and the out-of-band prefix length; 0 = absent.
        var r = new Lazy<Jbig2HuffmanTable>[15];
        r[0] = Std([0, 1, 4, 16, 2, 8, 272, 3, 16], 0, 0, 3, 65808, 0);
        r[1] = Std([0, 1, 0, 1, 2, 0, 2, 3, 0, 3, 4, 3, 11, 5, 6], 0, 0, 6, 75, 6);
        r[2] = Std([-256, 8, 8, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3, 4, 3, 11, 5, 6], 8, -257, 7, 75, 6);
        r[3] = Std([1, 1, 0, 2, 2, 0, 3, 3, 0, 4, 4, 3, 12, 5, 6], 0, 0, 5, 76, 0);
        r[4] = Std([-255, 7, 8, 1, 1, 0, 2, 2, 0, 3, 3, 0, 4, 4, 3, 12, 5, 6], 7, -256, 6, 76, 0);
        r[5] = Std(
            [-2048, 5, 10, -1024, 4, 9, -512, 4, 8, -256, 4, 7, -128, 5, 6, -64, 5, 5, -32, 4, 5, 0, 2, 7, 128, 3, 7, 256, 3, 8, 512, 4, 9, 1024, 4, 10],
            6, -2049, 6, 2048, 0);
        r[6] = Std(
            [-1024, 4, 9, -512, 3, 8, -256, 4, 7, -128, 5, 6, -64, 5, 5, -32, 4, 5, 0, 4, 5, 32, 5, 5, 64, 5, 6, 128, 4, 7, 256, 3, 8, 512, 3, 9, 1024, 3, 10],
            5, -1025, 5, 2048, 0);
        r[7] = Std(
            [-15, 8, 3, -7, 9, 1, -5, 8, 1, -3, 9, 0, -2, 7, 0, -1, 4, 0, 0, 2, 1, 2, 5, 0, 3, 6, 0, 4, 3, 4, 20, 6, 1, 22, 4, 4, 38, 4, 5, 70, 5, 6, 134, 5, 7, 262, 6, 7, 390, 7, 8, 646, 6, 10],
            9, -16, 9, 1670, 2);
        r[8] = Std(
            [-31, 8, 4, -15, 9, 2, -11, 8, 2, -7, 9, 1, -5, 7, 1, -3, 4, 1, -1, 3, 1, 1, 3, 1, 3, 5, 1, 5, 6, 1, 7, 3, 5, 39, 6, 2, 43, 4, 5, 75, 4, 6, 139, 5, 7, 267, 5, 8, 523, 6, 8, 779, 7, 9, 1291, 6, 11],
            9, -32, 9, 3339, 2);
        r[9] = Std(
            [-21, 7, 4, -5, 8, 0, -4, 7, 0, -3, 5, 0, -2, 2, 2, 2, 5, 0, 3, 6, 0, 4, 7, 0, 5, 8, 0, 6, 2, 6, 70, 5, 5, 102, 6, 5, 134, 6, 6, 198, 6, 7, 326, 6, 8, 582, 6, 9, 1094, 6, 10, 2118, 7, 11],
            8, -22, 8, 4166, 2);
        r[10] = Std([1, 1, 0, 2, 2, 1, 4, 4, 0, 5, 4, 1, 7, 5, 1, 9, 5, 2, 13, 6, 2, 17, 7, 2, 21, 7, 3, 29, 7, 4, 45, 7, 5, 77, 7, 6], 0, 0, 7, 141, 0);
        r[11] = Std([1, 1, 0, 2, 2, 0, 3, 3, 1, 5, 5, 0, 6, 5, 1, 8, 6, 1, 10, 7, 0, 11, 7, 1, 13, 7, 2, 17, 7, 3, 25, 7, 4, 41, 8, 5], 0, 0, 8, 73, 0);
        r[12] = Std([1, 1, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 4, 1, 7, 3, 3, 15, 6, 1, 17, 6, 2, 21, 6, 3, 29, 6, 4, 45, 6, 5, 77, 7, 6], 0, 0, 7, 141, 0);
        r[13] = Std([-2, 3, 0, -1, 3, 0, 0, 1, 0, 1, 3, 0, 2, 3, 0], 0, 0, 0, 0, 0);
        r[14] = Std([-24, 7, 4, -8, 6, 2, -4, 5, 1, -2, 4, 0, -1, 3, 0, 0, 1, 0, 1, 3, 0, 2, 4, 0, 3, 5, 1, 5, 6, 2, 9, 7, 4], 7, -25, 7, 25, 0);
        return r;
    }

    private static Lazy<Jbig2HuffmanTable> Std(int[] normal, int lowerPrefix, int lowerValue, int upperPrefix, int upperValue, int oobPrefix)
    {
        return new Lazy<Jbig2HuffmanTable>(() =>
        {
            var lines = new List<(int, int, long, int)>();
            for (int i = 0; i < normal.Length; i += 3)
            {
                lines.Add((normal[i + 1], normal[i + 2], normal[i], KindNormal));
            }

            if (lowerPrefix > 0)
            {
                lines.Add((lowerPrefix, 32, lowerValue, KindLower));
            }

            if (upperPrefix > 0)
            {
                lines.Add((upperPrefix, 32, upperValue, KindUpper));
            }

            if (oobPrefix > 0)
            {
                lines.Add((oobPrefix, 0, 0, KindOob));
            }

            return new Jbig2HuffmanTable(lines);
        });
    }
}
