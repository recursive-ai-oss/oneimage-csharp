namespace OneImage.Codecs.Tiff;

/// <summary>
/// CCITT Group 3 (T.4, one- and two-dimensional) and Group 4 (T.6) bilevel decoder, plus the TIFF "CCITT RLE" variant
/// (Modified Huffman, compression 2). Output rows are packed most significant bit first with 1 for black.
/// </summary>
internal static class TiffCcitt
{
    // T.4 tables 2 and 3. Terminating codes for runs 0..63, then make-up codes for 64..1728 in steps of 64.
    private static readonly string[] s_whiteTerm =
    [
        "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111", "10011", "10100", "00111", "01000", "001000", "000011", "110100", "110101",
        "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100", "0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010",
        "00011011", "00010010", "00010011", "00010100", "00010101", "00010110", "00010111", "00101000", "00101001", "00101010", "00101011", "00101100", "00101101", "00000100", "00000101", "00001010",
        "00001011", "01010010", "01010011", "01010100", "01010101", "00100100", "00100101", "01011000", "01011001", "01011010", "01011011", "01001010", "01001011", "00110010", "00110011", "00110100",
    ];

    private static readonly string[] s_whiteMakeup =
    [
        "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101", "01101000", "01100111", "011001100", "011001101", "011010010", "011010011", "011010100", "011010101",
        "011010110", "011010111", "011011000", "011011001", "011011010", "011011011", "010011000", "010011001", "010011010", "011000", "010011011",
    ];

    private static readonly string[] s_blackTerm =
    [
        "0000110111", "010", "11", "10", "011", "0011", "0010", "00011", "000101", "000100", "0000100", "0000101", "0000111", "00000100", "00000111", "000011000",
        "0000010111", "0000011000", "0000001000", "00001100111", "00001101000", "00001101100", "00000110111", "00000101000", "00000010111", "00000011000", "000011001010", "000011001011", "000011001100", "000011001101", "000001101000", "000001101001",
        "000001101010", "000001101011", "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111", "000001101100", "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
        "000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111", "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110", "000001100111",
    ];

    private static readonly string[] s_blackMakeup =
    [
        "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101", "0000001101100", "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010", "0000001110011", "0000001110100",
        "0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010", "0000001011011", "0000001100100", "0000001100101",
    ];

    // Make-up codes 1792..2560 are shared by both colors.
    private static readonly string[] s_extMakeup =
    [
        "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101", "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
    ];

    private const int PeekBits = 13;
    private static readonly short[] s_whiteRun = new short[1 << PeekBits];
    private static readonly byte[] s_whiteLen = new byte[1 << PeekBits];
    private static readonly short[] s_blackRun = new short[1 << PeekBits];
    private static readonly byte[] s_blackLen = new byte[1 << PeekBits];

    static TiffCcitt()
    {
        for (int i = 0; i < 64; i++)
        {
            Add(s_whiteRun, s_whiteLen, s_whiteTerm[i], i);
            Add(s_blackRun, s_blackLen, s_blackTerm[i], i);
        }

        for (int i = 0; i < s_whiteMakeup.Length; i++)
        {
            Add(s_whiteRun, s_whiteLen, s_whiteMakeup[i], 64 * (i + 1));
            Add(s_blackRun, s_blackLen, s_blackMakeup[i], 64 * (i + 1));
        }

        for (int i = 0; i < s_extMakeup.Length; i++)
        {
            Add(s_whiteRun, s_whiteLen, s_extMakeup[i], 1792 + (64 * i));
            Add(s_blackRun, s_blackLen, s_extMakeup[i], 1792 + (64 * i));
        }
    }

    /// <summary>Gets the code table for checks: the code strings of all runs of the given color.</summary>
    internal static IEnumerable<string> AllCodes(bool black)
    {
        foreach (string s in black ? s_blackTerm : s_whiteTerm)
        {
            yield return s;
        }

        foreach (string s in black ? s_blackMakeup : s_whiteMakeup)
        {
            yield return s;
        }

        foreach (string s in s_extMakeup)
        {
            yield return s;
        }
    }

    private static void Add(short[] runs, byte[] lens, string code, int run)
    {
        int len = code.Length;
        int value = 0;
        foreach (char c in code)
        {
            value = (value << 1) | (c - '0');
        }

        int first = value << (PeekBits - len);
        int n = 1 << (PeekBits - len);
        for (int k = 0; k < n; k++)
        {
            runs[first + k] = (short)run;
            lens[first + k] = (byte)len;
        }
    }

    private ref struct Bits
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _pos; // in bits

        public Bits(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public readonly long BitsLeft => ((long)_data.Length * 8) - _pos;

        public long Position
        {
            readonly get => _pos;
            set => _pos = value;
        }

        /// <summary>Returns the next <paramref name="n"/> (at most 24) bits, zero-padded past the end, without consuming them.</summary>
        public readonly int Peek(int n)
        {
            int byteIdx = (int)(_pos >> 3);
            int shift = (int)(_pos & 7);
            uint window = 0;
            for (int k = 0; k < 4; k++)
            {
                window <<= 8;
                if (byteIdx + k < _data.Length)
                {
                    window |= _data[byteIdx + k];
                }
            }

            return (int)((window << shift) >> (32 - n));
        }

        public void Skip(int n) => _pos += n;

        public void AlignToByte() => _pos = (_pos + 7) & ~7L;
    }

    /// <summary>Decodes <paramref name="rows"/> rows of <paramref name="width"/> pixels into packed <paramref name="output"/>.</summary>
    /// <param name="data">The compressed strip, in most-significant-bit-first order.</param>
    /// <param name="width">Row width in pixels.</param>
    /// <param name="rows">Number of rows.</param>
    /// <param name="codec">CCITT RLE, Group 3 or Group 4.</param>
    /// <param name="t4Options">TIFF T4Options (bit 0: two-dimensional coding).</param>
    /// <param name="output">Receives <c>rows * ((width + 7) / 8)</c> bytes.</param>
    /// <param name="cancellationToken">Observed once per row.</param>
    public static void Decode(ReadOnlySpan<byte> data, int width, int rows, TiffCodec codec, int t4Options, Span<byte> output, CancellationToken cancellationToken)
    {
        int rowBytes = (width + 7) >> 3;
        output[..(rowBytes * rows)].Clear();
        int cap = width + 8;
        int[] refLine = new int[cap];
        int[] curLine = new int[cap];
        int refCount = 0;
        FillSentinels(refLine, ref refCount, width);
        var bits = new Bits(data);
        bool twoD = codec == TiffCodec.CcittG4;
        for (int y = 0; y < rows; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool rowIs2D = twoD;
            if (codec == TiffCodec.CcittG3)
            {
                rowIs2D = false;
                if (!SkipEol(ref bits, out bool consecutive))
                {
                    if ((t4Options & 1) != 0)
                    {
                        throw new ImageFormatException("CCITT Group 3 data is missing a line start code.");
                    }
                }
                else if (consecutive)
                {
                    throw new ImageFormatException("CCITT data ended before all rows were decoded.");
                }

                if ((t4Options & 1) != 0)
                {
                    if (bits.BitsLeft < 1)
                    {
                        throw new ImageFormatException("CCITT data ended before all rows were decoded.");
                    }

                    rowIs2D = bits.Peek(1) == 0;
                    bits.Skip(1);
                }
            }
            else if (bits.BitsLeft <= 0)
            {
                throw new ImageFormatException("CCITT data ended before all rows were decoded.");
            }

            Span<byte> row = output.Slice(y * rowBytes, rowBytes);
            int curCount = rowIs2D
                ? DecodeRow2D(ref bits, width, refLine, refCount, curLine, row)
                : DecodeRow1D(ref bits, width, curLine, row);
            if (codec == TiffCodec.CcittRle)
            {
                bits.AlignToByte();
            }

            (refLine, curLine) = (curLine, refLine);
            refCount = curCount;
            FillSentinels(refLine, ref refCount, width);
        }
    }

    private static void FillSentinels(int[] line, ref int count, int width)
    {
        for (int i = 0; i < 4; i++)
        {
            line[count + i] = width;
        }
    }

    /// <summary>Looks for an end-of-line code (at least eleven zeros and a one) at the current position, allowing fill zeros before it.</summary>
    private static bool SkipEol(ref Bits bits, out bool consecutive)
    {
        consecutive = false;
        long start = bits.Position;
        long p = start;
        long end = start + bits.BitsLeft;
        var probe = bits;
        int zeros = 0;
        while (p < end)
        {
            probe.Position = p;
            int chunk = probe.Peek(1);
            if (chunk == 1)
            {
                break;
            }

            zeros++;
            p++;
        }

        if (p >= end || zeros < 11)
        {
            return false;
        }

        bits.Position = p + 1;

        // Another end-of-line right behind the first (the return-to-control sequence) means the data is over.
        probe.Position = bits.Position;
        consecutive = probe.Peek(12) == 1 || probe.Peek(13) == 0x1001;

        return true;
    }

    private static int ReadRun(ref Bits bits, bool black, int width, int a0)
    {
        short[] runs = black ? s_blackRun : s_whiteRun;
        byte[] lens = black ? s_blackLen : s_whiteLen;
        int total = 0;
        while (true)
        {
            int code = bits.Peek(PeekBits);
            int len = lens[code];
            if (len == 0)
            {
                throw new ImageFormatException("Invalid CCITT code.");
            }

            if (bits.BitsLeft < len)
            {
                throw new ImageFormatException("CCITT data is truncated.");
            }

            bits.Skip(len);
            int run = runs[code];
            total += run;
            if (a0 + total > width)
            {
                throw new ImageFormatException("A CCITT run extends past the end of the row.");
            }

            if (run < 64)
            {
                return total;
            }
        }
    }

    private static void FillBlack(Span<byte> row, int from, int to)
    {
        for (int x = from; x < to;)
        {
            if ((x & 7) == 0 && to - x >= 8)
            {
                row[x >> 3] = 0xFF;
                x += 8;
            }
            else
            {
                row[x >> 3] |= (byte)(0x80 >> (x & 7));
                x++;
            }
        }
    }

    private static int DecodeRow1D(ref Bits bits, int width, int[] cur, Span<byte> row)
    {
        int a0 = 0;
        int n = 0;
        bool black = false;
        while (a0 < width)
        {
            int run = ReadRun(ref bits, black, width, a0);
            if (black)
            {
                FillBlack(row, a0, a0 + run);
            }

            a0 += run;
            black = !black;
            Append(cur, ref n, a0, width);
        }

        return n;
    }

    private static int DecodeRow2D(ref Bits bits, int width, int[] refLine, int refCount, int[] cur, Span<byte> row)
    {
        _ = refCount;
        int a0 = -1;
        int n = 0;
        bool black = false;
        int bi = 0;
        while (a0 < width)
        {
            if (bits.BitsLeft <= 0)
            {
                throw new ImageFormatException("CCITT data is truncated.");
            }

            // b1: the first changing element on the reference line right of a0 whose color change is opposite to the current color.
            while (bi > 0 && refLine[bi - 1] > a0)
            {
                bi--;
            }

            while (refLine[bi] <= a0 && refLine[bi] < width)
            {
                bi++;
            }

            if (((bi & 1) == 1) != black)
            {
                bi++;
            }

            int b1 = refLine[bi];
            int code = bits.Peek(7);
            int a1;
            if ((code & 0x40) != 0)
            {
                bits.Skip(1);
                a1 = b1;
            }
            else if ((code >> 4) == 3)
            {
                bits.Skip(3);
                a1 = b1 + 1;
            }
            else if ((code >> 4) == 2)
            {
                bits.Skip(3);
                a1 = b1 - 1;
            }
            else if ((code >> 4) == 1)
            {
                // Horizontal mode: two runs, current color first.
                bits.Skip(3);
                int start = Math.Max(a0, 0);
                int r1 = ReadRun(ref bits, black, width, start);
                int r2 = ReadRun(ref bits, !black, width, start + r1);
                if (black)
                {
                    FillBlack(row, start, start + r1);
                }
                else
                {
                    FillBlack(row, start + r1, start + r1 + r2);
                }

                a0 = start + r1 + r2;
                Append(cur, ref n, start + r1, width);
                Append(cur, ref n, a0, width);
                continue;
            }
            else if ((code >> 3) == 1)
            {
                // Pass mode: the run continues past b2.
                bits.Skip(4);
                int b2 = refLine[bi + 1];
                if (black)
                {
                    FillBlack(row, Math.Max(a0, 0), b2);
                }

                a0 = b2;
                continue;
            }
            else if ((code >> 1) == 3)
            {
                bits.Skip(6);
                a1 = b1 + 2;
            }
            else if ((code >> 1) == 2)
            {
                bits.Skip(6);
                a1 = b1 - 2;
            }
            else if (code == 3)
            {
                bits.Skip(7);
                a1 = b1 + 3;
            }
            else if (code == 2)
            {
                bits.Skip(7);
                a1 = b1 - 3;
            }
            else
            {
                throw new ImageFormatException("Invalid CCITT two-dimensional code.");
            }

            if (a1 < Math.Max(a0, 0) || a1 > width)
            {
                throw new ImageFormatException("A CCITT vertical code points outside the row.");
            }

            if (black)
            {
                FillBlack(row, Math.Max(a0, 0), a1);
            }

            a0 = a1;
            Append(cur, ref n, a1, width);
            black = !black;
        }

        return n;
    }

    private static void Append(int[] cur, ref int n, int pos, int width)
    {
        if (n >= width + 4)
        {
            throw new ImageFormatException("Invalid CCITT data: too many color changes in a row.");
        }

        cur[n++] = pos;
    }
}
