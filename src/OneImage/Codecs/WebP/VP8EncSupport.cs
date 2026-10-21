namespace OneImage.Codecs.WebP;

/// <summary>The VP8 boolean entropy encoder (RFC 6386 section 7.3).</summary>
internal sealed class VP8BoolWriter
{
    private byte[] _buf;
    private int _len;
    private uint _range = 255;
    private uint _bottom;
    private int _bitCount = 24;

    public VP8BoolWriter(int capacity = 1024) => _buf = new byte[Math.Max(64, capacity)];

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

    public byte[] Finish()
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

        return _buf.AsSpan(0, _len).ToArray();
    }

    private void AddOne()
    {
        int q = _len;
        while (--q >= 0 && _buf[q] == 255)
        {
            _buf[q] = 0;
        }

        if (q >= 0)
        {
            _buf[q]++;
        }
    }

    private void Append(byte b)
    {
        if (_len == _buf.Length)
        {
            Array.Resize(ref _buf, _buf.Length * 2);
        }

        _buf[_len++] = b;
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

    /// <summary>Forward 4x4 DCT of (src - pred); both are read with stride 32 from the macroblock buffers.</summary>
    public static void Fdct(ReadOnlySpan<byte> src, int srcOff, int srcStride, ReadOnlySpan<byte> pred, int predOff, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int so = srcOff + (i * srcStride);
            int po = predOff + (i * VP8Decoder.Bps);
            int d0 = src[so] - pred[po];
            int d1 = src[so + 1] - pred[po + 1];
            int d2 = src[so + 2] - pred[po + 2];
            int d3 = src[so + 3] - pred[po + 3];
            int a0 = d0 + d3;
            int a1 = d1 + d2;
            int a2 = d1 - d2;
            int a3 = d0 - d3;
            tmp[(i * 4) + 0] = (a0 + a1) * 8;
            tmp[(i * 4) + 1] = ((a2 * 2217) + (a3 * 5352) + 1812) >> 9;
            tmp[(i * 4) + 2] = (a0 - a1) * 8;
            tmp[(i * 4) + 3] = ((a3 * 2217) - (a2 * 5352) + 937) >> 9;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[i] + tmp[12 + i];
            int a1 = tmp[4 + i] + tmp[8 + i];
            int a2 = tmp[4 + i] - tmp[8 + i];
            int a3 = tmp[i] - tmp[12 + i];
            output[i] = (short)((a0 + a1 + 7) >> 4);
            output[4 + i] = (short)((((a2 * 2217) + (a3 * 5352) + 12000) >> 16) + (a3 != 0 ? 1 : 0));
            output[8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[12 + i] = (short)(((a3 * 2217) - (a2 * 5352) + 51000) >> 16);
        }
    }

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
    public readonly int[] ZThresh = new int[16];
    public readonly int[] Sharpen = new int[16];

    public VP8QuantMatrix(int dcStep, int acStep, int biasDc, int biasAc, bool sharpen)
    {
        for (int i = 0; i < 16; i++)
        {
            int q = i == 0 ? dcStep : acStep;
            Q[i] = q;
            IQ[i] = (1 << QFix) / q;
            Bias[i] = (i == 0 ? biasDc : biasAc) << (QFix - 8);
            ZThresh[i] = (((1 << QFix) - 1 - Bias[i]) / IQ[i]) ;
            Sharpen[i] = sharpen ? (Sharpening[i] * q) >> 11 : 0;
        }
    }

    /// <summary>
    /// Quantizes coefficients in raster order. Writes levels in zigzag order to <paramref name="levels"/>, the dequantized
    /// raster coefficients back to <paramref name="coeffs"/>, and returns the zigzag index of the last nonzero level (or -1).
    /// </summary>
    public int Quantize(Span<short> coeffs, Span<short> levels, int first)
    {
        int last = -1;
        for (int n = first; n < 16; n++)
        {
            int j = VP8Fwd.Zigzag[n];
            int v = coeffs[j];
            bool neg = v < 0;
            int c = (neg ? -v : v) + Sharpen[j];
            int level = 0;
            if (c > ZThresh[j])
            {
                level = (int)(((long)c * IQ[j] + Bias[j]) >> QFix);
                if (level > 2047)
                {
                    level = 2047;
                }
            }

            if (level != 0)
            {
                last = n;
                coeffs[j] = (short)((neg ? -level : level) * Q[j]);
                levels[n] = (short)(neg ? -level : level);
            }
            else
            {
                coeffs[j] = 0;
                levels[n] = 0;
            }
        }

        return last;
    }
}
