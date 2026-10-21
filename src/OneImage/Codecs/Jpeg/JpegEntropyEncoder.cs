using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Numerics;

namespace OneImage.Codecs.Jpeg;

/// <summary>A Huffman table prepared for encoding, with symbol statistics for building optimal tables (T.81 Annex C and K.2).</summary>
internal sealed class JpegHuffmanEncodeTable
{
    public readonly uint[] Code = new uint[256];
    public readonly byte[] Size = new byte[256];
    public readonly long[] Freq = new long[257];
    public readonly byte[] Bits = new byte[17];
    public readonly byte[] Values = new byte[256];
    public int Count;

    public void Build(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
    {
        Array.Clear(Size);
        Array.Clear(Code);
        int total = 0;
        for (int l = 1; l <= 16; l++)
        {
            Bits[l] = counts[l - 1];
            total += counts[l - 1];
        }

        Count = total;
        values[..total].CopyTo(Values);
        uint code = 0;
        int k = 0;
        for (int l = 1; l <= 16; l++)
        {
            for (int i = 0; i < Bits[l]; i++)
            {
                Code[Values[k]] = code++;
                Size[Values[k]] = (byte)l;
                k++;
            }

            code <<= 1;
        }
    }

    /// <summary>Builds the table from <see cref="Freq"/>: Huffman code lengths limited to 16 bits, with one code point reserved so no code is all ones (K.2).</summary>
    public void BuildOptimal()
    {
        long[] freq = (long[])Freq.Clone();
        bool any = false;
        for (int i = 0; i < 256; i++)
        {
            any |= freq[i] != 0;
        }

        if (!any)
        {
            freq[0] = 1;
        }

        freq[256] = 1;
        int[] codeSize = new int[257];
        int[] others = new int[257];
        Array.Fill(others, -1);
        while (true)
        {
            int c1 = -1;
            long v = long.MaxValue;
            for (int i = 0; i <= 256; i++)
            {
                if (freq[i] != 0 && freq[i] <= v)
                {
                    v = freq[i];
                    c1 = i;
                }
            }

            int c2 = -1;
            v = long.MaxValue;
            for (int i = 0; i <= 256; i++)
            {
                if (freq[i] != 0 && freq[i] <= v && i != c1)
                {
                    v = freq[i];
                    c2 = i;
                }
            }

            if (c2 < 0)
            {
                break;
            }

            freq[c1] += freq[c2];
            freq[c2] = 0;
            codeSize[c1]++;
            while (others[c1] >= 0)
            {
                c1 = others[c1];
                codeSize[c1]++;
            }

            others[c1] = c2;
            codeSize[c2]++;
            while (others[c2] >= 0)
            {
                c2 = others[c2];
                codeSize[c2]++;
            }
        }

        int[] bits = new int[34];
        for (int i = 0; i <= 256; i++)
        {
            if (codeSize[i] > 0)
            {
                bits[Math.Min(codeSize[i], 33)]++;
            }
        }

        // Limit code lengths to 16 (K.2 Figure K.3).
        for (int i = 32; i > 16; i--)
        {
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0)
                {
                    j--;
                }

                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }

        int top = 16;
        while (bits[top] == 0)
        {
            top--;
        }

        bits[top]--;
        Array.Clear(Bits);
        for (int i = 1; i <= 16; i++)
        {
            Bits[i] = (byte)bits[i];
        }

        int n = 0;
        Span<byte> vals = stackalloc byte[256];
        for (int len = 1; len <= 32; len++)
        {
            for (int sym = 0; sym < 256; sym++)
            {
                if (codeSize[sym] == len)
                {
                    vals[n++] = (byte)sym;
                }
            }
        }

        Span<byte> counts = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            counts[i] = Bits[i + 1];
        }

        Build(counts, vals[..n]);
    }
}

/// <summary>
/// Bit-level output for entropy-coded data: MSB-first, 0xFF stuffed with a zero byte. In counting mode nothing is written,
/// so the same encoding code can gather symbol statistics.
/// </summary>
internal sealed class JpegBitWriter : IDisposable
{
    private readonly Stream? _stream;
    private byte[] _buf;
    private int _pos;
    /// <summary>Pending bits, right-aligned; the encoder's inner loop keeps a local copy of these two and stores them back.</summary>
    public ulong Acc;

    /// <summary>Number of valid bits in <see cref="Acc"/> (below 32 between calls).</summary>
    public int Bits;

    public JpegBitWriter(Stream stream)
    {
        _stream = stream;
        _buf = ArrayPool<byte>.Shared.Rent(1 << 16);
    }

    /// <summary>
    /// Creates a writer that collects raw bits in memory (no byte stuffing, whole 32-bit words in <see cref="RawWords"/>, the
    /// remaining bits in <see cref="Acc"/>/<see cref="Bits"/>). Segments coded this way in parallel are appended to the real
    /// writer in order with <see cref="AppendRaw"/>.
    /// </summary>
    public JpegBitWriter()
    {
        _buf = ArrayPool<byte>.Shared.Rent(1 << 16);
    }

    public bool Counting { get; set; }

    /// <summary>The 32-bit words written so far by a raw-mode writer, big-endian.</summary>
    public ReadOnlySpan<byte> RawWords => _buf.AsSpan(0, _pos);

    /// <summary>Appends everything a raw-mode writer collected: its whole words, then its remaining bits.</summary>
    public void AppendRaw(JpegBitWriter raw)
    {
        ReadOnlySpan<byte> words = raw.RawWords;
        for (int i = 0; i + 4 <= words.Length; i += 4)
        {
            PutBits(BinaryPrimitives.ReadUInt32BigEndian(words.Slice(i, 4)), 32);
        }

        if (raw.Bits > 0)
        {
            PutBits((uint)(raw.Acc & ((1UL << raw.Bits) - 1)), raw.Bits);
        }
    }

    /// <summary>Appends <paramref name="size"/> (0..32) bits of <paramref name="code"/>; nothing is written while <see cref="Counting"/>.</summary>
    public void PutBits(uint code, int size)
    {
        if (Counting || size == 0)
        {
            return;
        }

        Acc = (Acc << size) | code;
        Bits += size;
        if (Bits >= 32)
        {
            WriteWord();
        }
    }

    /// <summary>Moves the oldest 32 pending bits to the output, stuffing a zero after every 0xFF byte (T.81 F.1.2.3).</summary>
    public void WriteWord()
    {
        if (_pos + 8 > _buf.Length)
        {
            if (_stream is null)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(checked(_buf.Length * 2));
                _buf.AsSpan(0, _pos).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(_buf);
                _buf = bigger;
            }
            else
            {
                Flush();
            }
        }

        Bits -= 32;
        uint w = (uint)(Acc >> Bits);
        if (_stream is null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_buf.AsSpan(_pos), w);
            _pos += 4;
            return;
        }

        uint inv = ~w;
        if (((inv - 0x01010101u) & ~inv & 0x80808080u) == 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_buf.AsSpan(_pos), w);
            _pos += 4;
            return;
        }

        for (int shift = 24; shift >= 0; shift -= 8)
        {
            byte b = (byte)(w >> shift);
            _buf[_pos++] = b;
            if (b == 0xFF)
            {
                _buf[_pos++] = 0;
            }
        }
    }

    /// <summary>Pads the last byte with 1 bits (T.81 F.1.2.3) and moves every pending byte to the output.</summary>
    public void AlignWithOnes()
    {
        if (Counting)
        {
            return;
        }

        int pad = (8 - (Bits & 7)) & 7;
        if (pad > 0)
        {
            Acc = (Acc << pad) | ((1u << pad) - 1);
            Bits += pad;
        }

        if (_pos + 16 > _buf.Length)
        {
            Flush();
        }

        while (Bits > 0)
        {
            Bits -= 8;
            byte b = (byte)(Acc >> Bits);
            _buf[_pos++] = b;
            if (b == 0xFF)
            {
                _buf[_pos++] = 0;
            }
        }
    }

    public void WriteByte(int b)
    {
        if (_pos + 1 > _buf.Length)
        {
            Flush();
        }

        _buf[_pos++] = (byte)b;
    }

    public void WriteMarker(int marker)
    {
        WriteByte(0xFF);
        WriteByte(marker);
    }

    public void WriteUInt16(int v)
    {
        WriteByte(v >> 8);
        WriteByte(v & 0xFF);
    }

    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_pos >= _buf.Length)
            {
                Flush();
            }

            int n = Math.Min(data.Length, _buf.Length - _pos);
            data[..n].CopyTo(_buf.AsSpan(_pos));
            _pos += n;
            data = data[n..];
        }
    }

    public void Flush()
    {
        if (_pos > 0)
        {
            _stream!.Write(_buf, 0, _pos);
            _pos = 0;
        }
    }

    public void Dispose()
    {
        byte[] b = _buf;
        _buf = [];
        if (b.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }
}

/// <summary>Huffman coding of quantized blocks (zigzag order) for sequential and progressive scans (T.81 F.1.2 and G.1.2).</summary>
internal sealed class JpegEntropyEncoder
{
    private readonly JpegBitWriter _w;
    private int _eobRun;
    private int _bufferedBitCount;
    private readonly byte[] _correctionBits = new byte[1024];

    public JpegEntropyEncoder(JpegBitWriter writer) => _w = writer;

    public bool Counting
    {
        get => _w.Counting;
        set => _w.Counting = value;
    }

    public void Reset()
    {
        _eobRun = 0;
        _bufferedBitCount = 0;
    }

    private void Symbol(JpegHuffmanEncodeTable t, int sym)
    {
        if (_w.Counting)
        {
            t.Freq[sym]++;
        }
        else
        {
            int size = t.Size[sym];
            if (size == 0)
            {
                throw new ImageOperationException("JPEG encoder produced a symbol without a Huffman code.");
            }

            _w.PutBits(t.Code[sym], size);
        }
    }

    private static int BitLength(int v) => 32 - BitOperations.LeadingZeroCount((uint)v);

    /// <summary>One bit per coefficient, set where the coefficient is not zero.</summary>
    private static ulong NonZeroMask(ReadOnlySpan<short> block)
    {
        ulong m = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < 4; i++)
            {
                uint zero = Vector256.Equals(Vector256.Create(block.Slice(i * 16, 16)), Vector256<short>.Zero).ExtractMostSignificantBits();
                m |= (ulong)(~zero & 0xFFFFu) << (i * 16);
            }

            return m;
        }

        for (int k = 0; k < 64; k++)
        {
            if (block[k] != 0)
            {
                m |= 1UL << k;
            }
        }

        return m;
    }

    /// <summary>While counting, skip the AC symbols (they were counted elsewhere, in parallel).</summary>
    public bool DcOnly { get; set; }

    /// <summary>Adds the AC symbols a baseline block produces to <paramref name="freq"/>; <paramref name="nonZero"/> is the mask of its non-zero AC coefficients.</summary>
    public static void CountAc(ReadOnlySpan<short> block, ulong nonZero, long[] freq)
    {
        int last = 0;
        while (nonZero != 0)
        {
            int idx = BitOperations.TrailingZeroCount(nonZero);
            nonZero &= nonZero - 1;
            int run = idx - last - 1;
            while (run > 15)
            {
                freq[0xF0]++;
                run -= 16;
            }

            int v = block[idx];
            freq[(run << 4) | BitLength(v < 0 ? -v : v)]++;
            last = idx;
        }

        if (last < 63)
        {
            freq[0]++;
        }
    }

    /// <summary>Counts one whole block's AC symbols.</summary>
    public static void CountAc(ReadOnlySpan<short> block, long[] freq) => CountAc(block[..64], NonZeroMask(block) & ~1UL, freq);

    public void EncodeBaseline(ReadOnlySpan<short> block, ref int pred, JpegHuffmanEncodeTable dc, JpegHuffmanEncodeTable ac)
    {
        block = block[..64];
        int diff = block[0] - pred;
        pred = block[0];
        int mag = diff < 0 ? -diff : diff;
        int nbits = BitLength(mag);
        ulong m = NonZeroMask(block) & ~1UL;
        bool counting = _w.Counting;
        if (counting)
        {
            dc.Freq[nbits]++;
            if (!DcOnly)
            {
                CountAc(block, m, ac.Freq);
            }

            return;
        }

        int dsize = dc.Size[nbits];
        if (dsize == 0)
        {
            throw new ImageOperationException("JPEG encoder produced a symbol without a Huffman code.");
        }

        uint[] codes = ac.Code;
        byte[] sizes = ac.Size;
        ulong acc = (_w.Acc << dsize + nbits) | (dc.Code[nbits] << nbits) | ((uint)(diff < 0 ? diff - 1 : diff) & ((1u << nbits) - 1));
        int bits = _w.Bits + dsize + nbits;
        if (bits >= 32)
        {
            Store(acc, bits);
            acc = _w.Acc;
            bits = _w.Bits;
        }

        int prev = 0;
        while (m != 0)
        {
            int idx = BitOperations.TrailingZeroCount(m);
            m &= m - 1;
            int run = idx - prev - 1;
            while (run > 15)
            {
                int zs = sizes[0xF0];
                if (zs == 0)
                {
                    throw new ImageOperationException("JPEG encoder produced a symbol without a Huffman code.");
                }

                acc = (acc << zs) | codes[0xF0];
                bits += zs;
                if (bits >= 32)
                {
                    Store(acc, bits);
                    acc = _w.Acc;
                    bits = _w.Bits;
                }

                run -= 16;
            }

            int v = block[idx];
            int nb = BitLength(v < 0 ? -v : v);
            int sym = (run << 4) | nb;
            int size = sizes[sym];
            if (size == 0)
            {
                throw new ImageOperationException("JPEG encoder produced a symbol without a Huffman code.");
            }

            acc = (acc << (size + nb)) | (codes[sym] << nb) | ((uint)(v < 0 ? v - 1 : v) & ((1u << nb) - 1));
            bits += size + nb;
            if (bits >= 32)
            {
                Store(acc, bits);
                acc = _w.Acc;
                bits = _w.Bits;
            }

            prev = idx;
        }

        if (prev < 63)
        {
            int size = sizes[0];
            if (size == 0)
            {
                throw new ImageOperationException("JPEG encoder produced a symbol without a Huffman code.");
            }

            acc = (acc << size) | codes[0];
            bits += size;
        }

        _w.Acc = acc;
        _w.Bits = bits;
        if (bits >= 32)
        {
            _w.WriteWord();
        }
    }

    /// <summary>Hands the inner loop's pending bits to the writer, which moves whole words out and returns the remainder in its fields.</summary>
    private void Store(ulong acc, int bits)
    {
        _w.Acc = acc;
        _w.Bits = bits;
        _w.WriteWord();
    }

    public void EncodeDcFirst(ReadOnlySpan<short> block, ref int pred, JpegHuffmanEncodeTable dc, int al)
    {
        int v = block[0] >> al;
        int diff = v - pred;
        pred = v;
        int mag = diff < 0 ? -diff : diff;
        int nbits = BitLength(mag);
        Symbol(dc, nbits);
        if (nbits > 0)
        {
            _w.PutBits((uint)(diff < 0 ? diff - 1 : diff) & ((1u << nbits) - 1), nbits);
        }
    }

    public void EncodeDcRefine(ReadOnlySpan<short> block, int al) => _w.PutBits((uint)((block[0] >> al) & 1), 1);

    public void EncodeAcFirst(ReadOnlySpan<short> block, JpegHuffmanEncodeTable ac, int ss, int se, int al)
    {
        int run = 0;
        for (int k = ss; k <= se; k++)
        {
            int t = block[k];
            int bits;
            if (t < 0)
            {
                t = -t >> al;
                bits = ~t;
            }
            else
            {
                t >>= al;
                bits = t;
            }

            if (t == 0)
            {
                run++;
                continue;
            }

            if (_eobRun > 0)
            {
                EmitEobRun(ac);
            }

            while (run > 15)
            {
                Symbol(ac, 0xF0);
                run -= 16;
            }

            int nb = BitLength(t);
            Symbol(ac, (run << 4) | nb);
            _w.PutBits((uint)bits & ((1u << nb) - 1), nb);
            run = 0;
        }

        if (run > 0)
        {
            _eobRun++;
            if (_eobRun == 0x7FFF)
            {
                EmitEobRun(ac);
            }
        }
    }

    public void EncodeAcRefine(ReadOnlySpan<short> block, JpegHuffmanEncodeTable ac, int ss, int se, int al)
    {
        Span<int> abs = stackalloc int[64];
        int eob = 0;
        for (int k = ss; k <= se; k++)
        {
            int t = block[k];
            if (t < 0)
            {
                t = -t;
            }

            t >>= al;
            abs[k] = t;
            if (t == 1)
            {
                eob = k;
            }
        }

        int run = 0;
        int pending = 0;
        int pendingStart = _bufferedBitCount;
        for (int k = ss; k <= se; k++)
        {
            int t = abs[k];
            if (t == 0)
            {
                run++;
                continue;
            }

            while (run > 15 && k <= eob)
            {
                EmitEobRun(ac);
                Symbol(ac, 0xF0);
                run -= 16;
                EmitBufferedBits(pendingStart, pending);
                pending = 0;
                pendingStart = _bufferedBitCount;
            }

            if (t > 1)
            {
                _correctionBits[pendingStart + pending++] = (byte)(t & 1);
                continue;
            }

            EmitEobRun(ac);
            Symbol(ac, (run << 4) | 1);
            _w.PutBits(block[k] < 0 ? 0u : 1u, 1);
            EmitBufferedBits(pendingStart, pending);
            pending = 0;
            pendingStart = _bufferedBitCount;
            run = 0;
        }

        if (run > 0 || pending > 0)
        {
            _eobRun++;
            _bufferedBitCount += pending;
            if (_eobRun == 0x7FFF || _bufferedBitCount > _correctionBits.Length - 64)
            {
                EmitEobRun(ac);
            }
        }
    }

    private void EmitBufferedBits(int start, int count)
    {
        if (_w.Counting)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            _w.PutBits(_correctionBits[start + i], 1);
        }
    }

    /// <summary>Writes a pending end-of-band run and the correction bits that belong to it.</summary>
    public void EmitEobRun(JpegHuffmanEncodeTable ac)
    {
        if (_eobRun > 0)
        {
            int nbits = BitLength(_eobRun) - 1;
            Symbol(ac, nbits << 4);
            if (nbits > 0)
            {
                _w.PutBits((uint)_eobRun & ((1u << nbits) - 1), nbits);
            }

            _eobRun = 0;
            EmitBufferedBits(0, _bufferedBitCount);
            _bufferedBitCount = 0;
        }
    }

    public bool HasPendingEobRun => _eobRun > 0;
}
