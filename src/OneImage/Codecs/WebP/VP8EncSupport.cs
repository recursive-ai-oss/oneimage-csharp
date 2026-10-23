namespace OneImage.Codecs.WebP;

/// <summary>
/// The VP8 boolean entropy encoder (RFC 6386 section 7.3). The output grows in 64 KiB chunks instead of one array that doubles,
/// so a large partition never leaves a trail of discarded half-size copies behind; <see cref="CopyTo"/> joins the chunks once.
/// For small and medium frames the full-size chunks are rented from the shared array pool and go back in <see cref="Release"/>,
/// so repeated encodes reuse them.
/// </summary>
internal sealed class VP8BoolWriter
{
    private const int ChunkSize = 1 << 16;
    private readonly List<byte[]> _full = [];
    private readonly List<int> _fullLengths = [];
    private byte[] _cur;
    private int _pos;
    private int _limit;
    private int _fullBytes;
    private uint _range = 255;
    private uint _bottom;
    private int _bitCount = 24;

    private readonly bool _pooled;

    /// <param name="capacity">Size of the first chunk, clamped to 64 bytes to 64 KiB.</param>
    /// <param name="pooled">Whether later chunks come from the shared array pool (and go back in <see cref="Release"/>) or are plain arrays.</param>
    public VP8BoolWriter(int capacity = 1024, bool pooled = false)
    {
        _pooled = pooled;
        _cur = new byte[Math.Clamp(capacity, 64, ChunkSize)];
        _limit = _cur.Length;
    }

    /// <summary>Returns the rented chunks to the pool; the writer holds no data afterwards.</summary>
    public void Release()
    {
        // The first chunk is a plain array (its size follows the caller's estimate); every later one is rented when pooled.
        if (_pooled)
        {
            for (int i = 1; i < _full.Count; i++)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(_full[i]);
            }

            if (_full.Count > 0)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(_cur);
            }
        }

        _full.Clear();
        _fullLengths.Clear();
        _cur = [];
        _limit = 0;
        _pos = 0;
        _fullBytes = 0;
    }

    /// <summary>Gets the number of bytes written so far.</summary>
    public int Length => _fullBytes + _pos;

    public void PutBit(int bit, int prob)
    {
        uint split = 1 + (((_range - 1) * (uint)prob) >> 8);
        if (bit != 0)
        {
            _bottom += split;
            _range -= split;
        }
        else
        {
            _range = split;
        }

        while (_range < 128)
        {
            _range <<= 1;
            if ((_bottom & (1u << 31)) != 0)
            {
                AddOne();
            }

            _bottom <<= 1;
            if (--_bitCount == 0)
            {
                Append((byte)(_bottom >> 24));
                _bottom &= (1u << 24) - 1;
                _bitCount = 8;
            }
        }
    }

    public void PutLiteral(int value, int bits)
    {
        while (bits-- > 0)
        {
            PutBit((value >> bits) & 1, 128);
        }
    }

    public void PutSignedLiteral(int value, int bits)
    {
        PutLiteral(Math.Abs(value), bits);
        PutBit(value < 0 ? 1 : 0, 128);
    }

    /// <summary>Flushes the coder state and returns the total length of the partition.</summary>
    public int Finish()
    {
        int c = _bitCount;
        uint v = _bottom;
        if ((v & (1u << (32 - c))) != 0)
        {
            AddOne();
        }

        v <<= c & 7;
        c >>= 3;
        while (--c >= 0)
        {
            v <<= 8;
        }

        for (int i = 0; i < 4; i++)
        {
            Append((byte)(v >> 24));
            v <<= 8;
        }

        return Length;
    }

    /// <summary>Copies the bytes written (after <see cref="Finish"/>) to <paramref name="destination"/>, which must hold <see cref="Length"/> bytes.</summary>
    public void CopyTo(Span<byte> destination)
    {
        for (int i = 0; i < _full.Count; i++)
        {
            int len = _fullLengths[i];
            _full[i].AsSpan(0, len).CopyTo(destination);
            destination = destination[len..];
        }

        _cur.AsSpan(0, _pos).CopyTo(destination);
    }

    // Carries a one into the bytes already written: 255s become 0 and the first byte below 255 grows, possibly in an earlier chunk.
    private void AddOne()
    {
        byte[] buf = _cur;
        int q = _pos;
        int chunk = _full.Count;
        while (true)
        {
            if (q == 0)
            {
                if (chunk == 0)
                {
                    return;
                }

                chunk--;
                buf = _full[chunk];
                q = _fullLengths[chunk];
            }

            q--;
            if (buf[q] != 255)
            {
                buf[q]++;
                return;
            }

            buf[q] = 0;
        }
    }

    private void Append(byte b)
    {
        if (_pos == _limit)
        {
            _full.Add(_cur);
            _fullLengths.Add(_pos);
            _fullBytes += _pos;
            _cur = _pooled ? System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize) : new byte[ChunkSize];
            _limit = Math.Min(_cur.Length, ChunkSize);
            _pos = 0;
        }

        _cur[_pos++] = b;
    }
}

/// <summary>Forward transforms, quantization and bit-cost tables for the VP8 encoder (same integer arithmetic as the decoder's inverse).</summary>
internal static class VP8Fwd
{
    public static readonly byte[] Zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];
    public static readonly byte[] Bands = [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];

    /// <summary>Cost in 1/256 bit of coding a 0 bit (index 0) or a 1 bit (index 1) with probability p of a zero.</summary>
    public static readonly ushort[] BitCost0 = BuildBitCost(false);

    public static readonly ushort[] BitCost1 = BuildBitCost(true);

    private static ushort[] BuildBitCost(bool one)
    {
        ushort[] t = new ushort[256];
        for (int p = 1; p < 256; p++)
        {
            double prob = one ? 1.0 - (p / 256.0) : p / 256.0;
            t[p] = (ushort)Math.Round(-Math.Log2(prob) * 256);
        }

        t[0] = t[1];
        return t;
    }

    public static int BitCost(int bit, int prob) => bit != 0 ? BitCost1[prob] : BitCost0[prob];

    /// <summary>Forward 4x4 DCT of (src - pred); the prediction is read with the macroblock work buffer's stride.</summary>
    public static void Fdct(ReadOnlySpan<byte> src, int srcOff, int srcStride, ReadOnlySpan<byte> pred, int predOff, Span<short> output) =>
        Kernels.WebPFdctKernel.Forward(src, srcOff, srcStride, pred, predOff, VP8Decoder.Bps, output);

    /// <summary>Forward Walsh-Hadamard transform of the 16 luma DC values (<paramref name="dc"/> holds them in block order).</summary>
    public static void Wht(ReadOnlySpan<short> dc, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = dc[(i * 4) + 0] + dc[(i * 4) + 2];
            int a1 = dc[(i * 4) + 1] + dc[(i * 4) + 3];
            int a2 = dc[(i * 4) + 1] - dc[(i * 4) + 3];
            int a3 = dc[(i * 4) + 0] - dc[(i * 4) + 2];
            tmp[(i * 4) + 0] = a0 + a1;
            tmp[(i * 4) + 1] = a3 + a2;
            tmp[(i * 4) + 2] = a3 - a2;
            tmp[(i * 4) + 3] = a0 - a1;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[i] + tmp[8 + i];
            int a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i];
            int a3 = tmp[i] - tmp[8 + i];
            output[i] = (short)((a0 + a1) >> 1);
            output[4 + i] = (short)((a3 + a2) >> 1);
            output[8 + i] = (short)((a3 - a2) >> 1);
            output[12 + i] = (short)((a0 - a1) >> 1);
        }
    }
}

/// <summary>A quantizer for one coefficient class (luma, luma DC block or chroma): steps, reciprocals and rounding biases.</summary>
internal sealed class VP8QuantMatrix
{
    private const int QFix = 17;
    private static readonly byte[] Sharpening = [0, 30, 60, 90, 30, 60, 90, 90, 60, 90, 90, 90, 90, 90, 90, 90];

    public readonly int[] Q = new int[16];
    public readonly int[] IQ = new int[16];
    public readonly int[] Bias = new int[16];
    public readonly int[] Sharpen = new int[16];
    private readonly bool _vectorSafe;

    public VP8QuantMatrix(int dcStep, int acStep, int biasDc, int biasAc, bool sharpen)
    {
        for (int i = 0; i < 16; i++)
        {
            int q = i == 0 ? dcStep : acStep;
            Q[i] = q;
            IQ[i] = (1 << QFix) / q;
            Bias[i] = (i == 0 ? biasDc : biasAc) << (QFix - 8);
            Sharpen[i] = sharpen ? (Sharpening[i] * q) >> 11 : 0;
        }

        _vectorSafe = IQ.Max() <= Kernels.WebPQuantKernel.MaxVectorReciprocal;
    }

    /// <summary>
    /// Quantizes coefficients in raster order. Writes levels in zigzag order to <paramref name="levels"/>, the dequantized
    /// raster coefficients back to <paramref name="coeffs"/>, and returns the zigzag index of the last nonzero level (or -1).
    /// </summary>
    public int Quantize(Span<short> coeffs, Span<short> levels, int first) =>
        Kernels.WebPQuantKernel.Quantize(coeffs, levels, first, Q, IQ, Bias, Sharpen, _vectorSafe);
}
