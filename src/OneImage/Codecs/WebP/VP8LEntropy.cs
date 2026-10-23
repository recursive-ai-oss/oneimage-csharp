using System.Numerics;

namespace OneImage.Codecs.WebP;

/// <summary>Least-significant-bit-first bit writer for VP8L streams.</summary>
internal sealed class VP8LBitWriter
{
    // Output grows by whole chunks instead of by doubling one array, so a large stream never exists twice at once
    // except for the final copy.
    private const int MaxChunk = 1 << 22;

    private readonly List<byte[]> _full = [];
    private byte[] _buf = new byte[4096];
    private int _len;
    private long _fullBytes;
    private ulong _acc;
    private int _nacc;

    /// <summary>Appends the low <paramref name="nbits"/> bits (at most 32) of <paramref name="value"/>.</summary>
    public void Put(uint value, int nbits)
    {
        _acc |= (ulong)value << _nacc;
        _nacc += nbits;
        if (_nacc >= 32)
        {
            if (_len + 4 > _buf.Length)
            {
                NextChunk();
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_len), (uint)_acc);
            _len += 4;
            _acc >>= 32;
            _nacc -= 32;
        }
    }

    // Chunk lengths are multiples of 4, so a word never straddles two chunks.
    private void NextChunk()
    {
        byte[] done = _len == _buf.Length ? _buf : _buf.AsSpan(0, _len).ToArray();
        _full.Add(done);
        _fullBytes += done.Length;
        _buf = new byte[Math.Min(MaxChunk, _buf.Length * 2)];
        _len = 0;
    }

    /// <summary>
    /// Makes the output so far, plus <paramref name="moreBits"/> more, fit one array of exactly its final size, so that
    /// <see cref="Finish"/> hands it back without another copy. A wrong count only costs that copy.
    /// </summary>
    public void Reserve(long moreBits)
    {
        long bits = ((_fullBytes + _len) * 8) + _nacc + moreBits;
        long bytes = (bits + 7) >> 3;
        if (bytes > Array.MaxLength)
        {
            return;
        }

        byte[] big = GC.AllocateUninitializedArray<byte>((int)bytes);
        int at = 0;
        foreach (byte[] chunk in _full)
        {
            chunk.CopyTo(big, at);
            at += chunk.Length;
        }

        _buf.AsSpan(0, _len).CopyTo(big.AsSpan(at));
        _len += at;
        _full.Clear();
        _fullBytes = 0;
        _buf = big;
    }

    /// <summary>Pads to a byte boundary and returns the bytes.</summary>
    public byte[] Finish()
    {
        int tail = (_nacc + 7) >> 3;
        if (_full.Count == 0 && _len + tail == _buf.Length)
        {
            for (int i = 0; i < tail; i++)
            {
                _buf[_len + i] = (byte)(_acc >> (8 * i));
            }

            return _buf;
        }

        byte[] result = new byte[checked(_fullBytes + _len + tail)];
        int at = 0;
        foreach (byte[] chunk in _full)
        {
            chunk.CopyTo(result, at);
            at += chunk.Length;
        }

        _full.Clear();
        _buf.AsSpan(0, _len).CopyTo(result.AsSpan(at));
        at += _len;
        for (int i = 0; i < tail; i++)
        {
            result[at + i] = (byte)(_acc >> (8 * i));
        }

        return result;
    }
}

/// <summary>A canonical prefix code: lengths, bit-reversed codes and whether it has a single symbol (coded in zero bits).</summary>
internal sealed class HuffmanCode
{
    public HuffmanCode(int size)
    {
        Lengths = new byte[size];
        Codes = new ushort[size];
    }

    public byte[] Lengths { get; }

    public ushort[] Codes { get; }

    public int SymbolCount { get; set; }

    public int Symbol0 { get; set; } = -1;

    public int Symbol1 { get; set; } = -1;

    /// <summary>Builds a length-limited code from symbol counts.</summary>
    public static HuffmanCode Build(ReadOnlySpan<uint> counts, int maxBits)
    {
        var code = new HuffmanCode(counts.Length);
        int n = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] != 0)
            {
                if (n == 0)
                {
                    code.Symbol0 = i;
                }
                else if (n == 1)
                {
                    code.Symbol1 = i;
                }

                n++;
            }
        }

        code.SymbolCount = n;
        if (n == 0)
        {
            return code;
        }

        if (n == 1)
        {
            // Zero bits per symbol; the stream stores the symbol itself.
            return code;
        }

        ComputeLengths(counts, maxBits, code.Lengths);
        AssignCodes(code.Lengths, code.Codes);
        return code;
    }

    private static void ComputeLengths(ReadOnlySpan<uint> counts, int maxBits, byte[] lengths)
    {
        int n = 0;
        foreach (uint c in counts)
        {
            if (c != 0)
            {
                n++;
            }
        }

        uint countMin = 1;
        long[] nodeWeight = new long[2 * n];
        int[] nodeParent = new int[2 * n];
        int[] leafSymbol = new int[n];
        byte[] depths = new byte[n];
        int[] depthOf = new int[2 * n];
        while (true)
        {
            // Leaves sorted by weight (stable by symbol), then merged with the two-queue method.
            int k = 0;
            var order = new (long w, int sym)[n];
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] != 0)
                {
                    order[k++] = (Math.Max(counts[i], countMin), i);
                }
            }

            Array.Sort(order, static (a, b) => a.w != b.w ? a.w.CompareTo(b.w) : a.sym.CompareTo(b.sym));
            for (int i = 0; i < n; i++)
            {
                nodeWeight[i] = order[i].w;
                leafSymbol[i] = order[i].sym;
            }

            int leafPos = 0;
            int intPos = n;
            int intEnd = n;
            int total = n;
            for (int m = 0; m < n - 1; m++)
            {
                int a = PickSmallest(nodeWeight, ref leafPos, n, ref intPos, intEnd);
                int b = PickSmallest(nodeWeight, ref leafPos, n, ref intPos, intEnd);
                nodeWeight[total] = nodeWeight[a] + nodeWeight[b];
                nodeParent[a] = total;
                nodeParent[b] = total;
                total++;
                intEnd = total;
            }

            int root = total - 1;
            nodeParent[root] = -1;
            int maxDepth = 0;
            // Internal node depths are computed from the root down (parents always have larger indices).
            depthOf[root] = 0;
            for (int i = root - 1; i >= 0; i--)
            {
                depthOf[i] = depthOf[nodeParent[i]] + 1;
            }

            for (int i = 0; i < n; i++)
            {
                maxDepth = Math.Max(maxDepth, depthOf[i]);
                depths[i] = (byte)Math.Min(depthOf[i], 255);
            }

            if (maxDepth <= maxBits)
            {
                for (int i = 0; i < n; i++)
                {
                    lengths[leafSymbol[i]] = depths[i];
                }

                return;
            }

            // Too deep: flatten the smallest counts and retry.
            countMin *= 2;
        }
    }

    private static int PickSmallest(long[] w, ref int leafPos, int leafEnd, ref int intPos, int intEnd)
    {
        if (leafPos < leafEnd && (intPos >= intEnd || w[leafPos] <= w[intPos]))
        {
            return leafPos++;
        }

        return intPos++;
    }

    private static void AssignCodes(byte[] lengths, ushort[] codes)
    {
        Span<int> count = stackalloc int[16];
        foreach (byte l in lengths)
        {
            count[l]++;
        }

        count[0] = 0;
        Span<int> next = stackalloc int[16];
        int code = 0;
        for (int len = 1; len < 16; len++)
        {
            code = (code + count[len - 1]) << 1;
            next[len] = code;
        }

        for (int s = 0; s < lengths.Length; s++)
        {
            int len = lengths[s];
            if (len > 0)
            {
                int c = next[len]++;
                // Reverse the bits: codes are written starting from their most significant bit.
                ushort r = 0;
                for (int i = 0; i < len; i++)
                {
                    r = (ushort)((r << 1) | ((c >> i) & 1));
                }

                codes[s] = r;
            }
        }
    }

    /// <summary>Emits the bits of one symbol (nothing for a single-symbol code).</summary>
    public void WriteSymbol(VP8LBitWriter bw, int symbol)
    {
        int len = Lengths[symbol];
        if (len > 0)
        {
            bw.Put(Codes[symbol], len);
        }
    }

    /// <summary>Estimated cost in bits of coding the given counts with this code.</summary>
    public long Cost(ReadOnlySpan<uint> counts)
    {
        long bits = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            bits += (long)counts[i] * Lengths[i];
        }

        return bits;
    }

    /// <summary>Writes the code description to the stream (RFC 9649 section 3.7.2.1).</summary>
    public void Store(VP8LBitWriter bw)
    {
        if (SymbolCount == 0)
        {
            bw.Put(1, 4); // simple code, one symbol, symbol 0
            return;
        }

        if (SymbolCount <= 2 && Symbol0 < 256 && (SymbolCount == 1 || Symbol1 < 256))
        {
            bw.Put(1, 1);
            bw.Put((uint)(SymbolCount - 1), 1);
            if (Symbol0 <= 1)
            {
                bw.Put(0, 1);
                bw.Put((uint)Symbol0, 1);
            }
            else
            {
                bw.Put(1, 1);
                bw.Put((uint)Symbol0, 8);
            }

            if (SymbolCount == 2)
            {
                bw.Put((uint)Symbol1, 8);
            }

            return;
        }

        StoreFull(bw);
    }

    private static readonly byte[] StorageOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private void StoreFull(VP8LBitWriter bw)
    {
        // A lone symbol longer than the simple code can carry still needs a (degenerate) full code: give it length 1,
        // which the decoder treats as a zero-bit single-symbol code.
        byte[] lengths = Lengths;
        if (SymbolCount == 1)
        {
            lengths = (byte[])Lengths.Clone();
            lengths[Symbol0] = 1;
        }

        // Run-length tokens of the code lengths: (code, extra).
        var tokens = new List<(byte code, byte extra)>();
        int prev = 8;
        int i = 0;
        while (i < lengths.Length)
        {
            int value = lengths[i];
            int k = i + 1;
            while (k < lengths.Length && lengths[k] == value)
            {
                k++;
            }

            int runs = k - i;
            i = k;
            if (value == 0)
            {
                while (runs >= 1)
                {
                    if (runs < 3)
                    {
                        for (int j = 0; j < runs; j++)
                        {
                            tokens.Add((0, 0));
                        }

                        break;
                    }
                    else if (runs < 11)
                    {
                        tokens.Add((17, (byte)(runs - 3)));
                        break;
                    }
                    else if (runs < 139)
                    {
                        tokens.Add((18, (byte)(runs - 11)));
                        break;
                    }
                    else
                    {
                        tokens.Add((18, 127));
                        runs -= 138;
                    }
                }
            }
            else
            {
                if (value != prev)
                {
                    tokens.Add(((byte)value, 0));
                    runs--;
                }

                while (runs >= 1)
                {
                    if (runs < 3)
                    {
                        for (int j = 0; j < runs; j++)
                        {
                            tokens.Add(((byte)value, 0));
                        }

                        break;
                    }
                    else if (runs < 7)
                    {
                        tokens.Add((16, (byte)(runs - 3)));
                        break;
                    }
                    else
                    {
                        tokens.Add((16, 3));
                        runs -= 6;
                    }
                }

                prev = value;
            }
        }

        uint[] hist = new uint[19];
        foreach ((byte code, byte _) in tokens)
        {
            hist[code]++;
        }

        HuffmanCode cl = Build(hist, 7);
        // Trailing zero tokens may be left out when that saves enough bits.
        int trimmed = tokens.Count;
        int trailingBits = 0;
        for (int t = tokens.Count - 1; t >= 0; t--)
        {
            int ix = tokens[t].code;
            if (ix is 0 or 17 or 18)
            {
                trimmed--;
                trailingBits += cl.Lengths[ix];
                if (ix == 17)
                {
                    trailingBits += 3;
                }
                else if (ix == 18)
                {
                    trailingBits += 7;
                }
            }
            else
            {
                break;
            }
        }

        bool writeTrimmed = trimmed > 1 && trailingBits > 12;
        int length = writeTrimmed ? trimmed : tokens.Count;

        bw.Put(0, 1);
        int toStore = 19;
        while (toStore > 4 && cl.Lengths[StorageOrder[toStore - 1]] == 0)
        {
            toStore--;
        }

        bw.Put((uint)(toStore - 4), 4);
        for (int s = 0; s < toStore; s++)
        {
            // A single-symbol code-length code is written with length 1 and costs zero bits per token.
            int l = cl.Lengths[StorageOrder[s]];
            if (cl.SymbolCount == 1 && StorageOrder[s] == cl.Symbol0)
            {
                l = 1;
            }

            bw.Put((uint)l, 3);
        }

        bw.Put(writeTrimmed ? 1u : 0u, 1);
        if (writeTrimmed)
        {
            int nbits = length == 2 ? 0 : BitOperations.Log2((uint)(length - 2));
            int nbitpairs = (nbits / 2) + 1;
            bw.Put((uint)(nbitpairs - 1), 3);
            bw.Put((uint)(length - 2), nbitpairs * 2);
        }

        for (int t = 0; t < length; t++)
        {
            (byte code, byte extra) = tokens[t];
            cl.WriteSymbol(bw, code);
            if (code == 16)
            {
                bw.Put(extra, 2);
            }
            else if (code == 17)
            {
                bw.Put(extra, 3);
            }
            else if (code == 18)
            {
                bw.Put(extra, 7);
            }
        }
    }
}
