using System.Runtime.CompilerServices;

namespace OneImage.Kernels;

/// <summary>
/// Forward DCT and quantization for JPEG encoding. The transform is the accurate integer algorithm of Loeffler,
/// Ligtenberg and Moschytz in 13-bit fixed point; its output is the true DCT scaled up by 8, which the
/// quantizer divides out. Results are bit-comparable with the common reference encoder.
/// </summary>
internal static class JpegFdct
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;

    private const int F0298 = 2446;
    private const int F0390 = 3196;
    private const int F0541 = 4433;
    private const int F0765 = 6270;
    private const int F0899 = 7373;
    private const int F1175 = 9633;
    private const int F1501 = 12299;
    private const int F1847 = 15137;
    private const int F1961 = 16069;
    private const int F2053 = 16819;
    private const int F2562 = 20995;
    private const int F3072 = 25172;

    /// <summary>Transforms one 8x8 block of 8-bit samples (level-shifted by -128) into <paramref name="output"/> (natural order, scaled by 8).</summary>
    /// <param name="samples">Plane memory starting at the block's top-left sample.</param>
    /// <param name="stride">Plane row stride.</param>
    /// <param name="output">64 ints.</param>
    public static void Forward(ReadOnlySpan<byte> samples, int stride, Span<int> output)
    {
        if (stride < 8 || samples.Length < (7 * stride) + 8 || output.Length < 64)
        {
            throw new ArgumentException("FDCT buffer too small.");
        }

        for (int r = 0; r < 8; r++)
        {
            ReadOnlySpan<byte> s = samples.Slice(r * stride, 8);
            Span<int> o = output.Slice(r * 8, 8);
            int tmp0 = s[0] + s[7];
            int tmp7 = s[0] - s[7];
            int tmp1 = s[1] + s[6];
            int tmp6 = s[1] - s[6];
            int tmp2 = s[2] + s[5];
            int tmp5 = s[2] - s[5];
            int tmp3 = s[3] + s[4];
            int tmp4 = s[3] - s[4];
            Even(tmp0, tmp1, tmp2, tmp3, o, true, ConstBits - Pass1Bits);
            Odd(tmp4, tmp5, tmp6, tmp7, o, ConstBits - Pass1Bits);
        }

        Span<int> col = stackalloc int[8];
        for (int c = 0; c < 8; c++)
        {
            int tmp0 = output[c] + output[56 + c];
            int tmp7 = output[c] - output[56 + c];
            int tmp1 = output[8 + c] + output[48 + c];
            int tmp6 = output[8 + c] - output[48 + c];
            int tmp2 = output[16 + c] + output[40 + c];
            int tmp5 = output[16 + c] - output[40 + c];
            int tmp3 = output[24 + c] + output[32 + c];
            int tmp4 = output[24 + c] - output[32 + c];
            Even(tmp0, tmp1, tmp2, tmp3, col, false, ConstBits + Pass1Bits);
            Odd(tmp4, tmp5, tmp6, tmp7, col, ConstBits + Pass1Bits);
            for (int i = 0; i < 8; i++)
            {
                output[(i * 8) + c] = col[i];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Descale(int x, int n) => (x + (1 << (n - 1))) >> n;

    // The row pass scales up by Pass1Bits and removes the +128 level shift (the sum of 8 samples carries 1024 of it);
    // the column pass scales back down.
    private static void Even(int tmp0, int tmp1, int tmp2, int tmp3, Span<int> o, bool rowPass, int rotShift)
    {
        int tmp10 = tmp0 + tmp3;
        int tmp13 = tmp0 - tmp3;
        int tmp11 = tmp1 + tmp2;
        int tmp12 = tmp1 - tmp2;
        if (rowPass)
        {
            o[0] = ((tmp10 + tmp11) - 1024) << Pass1Bits;
            o[4] = (tmp10 - tmp11) << Pass1Bits;
        }
        else
        {
            o[0] = Descale(tmp10 + tmp11, Pass1Bits);
            o[4] = Descale(tmp10 - tmp11, Pass1Bits);
        }

        int z1 = (tmp12 + tmp13) * F0541;
        o[2] = Descale(z1 + (tmp13 * F0765), rotShift);
        o[6] = Descale(z1 + (tmp12 * -F1847), rotShift);
    }

    private static void Odd(int tmp4, int tmp5, int tmp6, int tmp7, Span<int> o, int rotShift)
    {
        int z1 = tmp4 + tmp7;
        int z2 = tmp5 + tmp6;
        int z3 = tmp4 + tmp6;
        int z4 = tmp5 + tmp7;
        int z5 = (z3 + z4) * F1175;
        tmp4 *= F0298;
        tmp5 *= F2053;
        tmp6 *= F3072;
        tmp7 *= F1501;
        z1 *= -F0899;
        z2 *= -F2562;
        z3 *= -F1961;
        z4 *= -F0390;
        z3 += z5;
        z4 += z5;
        o[7] = Descale(tmp4 + z1 + z3, rotShift);
        o[5] = Descale(tmp5 + z2 + z4, rotShift);
        o[3] = Descale(tmp6 + z2 + z3, rotShift);
        o[1] = Descale(tmp7 + z1 + z4, rotShift);
    }

    /// <summary>
    /// Builds a divisor/reciprocal pair for exact rounded division by <c>8 * quant</c>: <c>(n * Reciprocal) &gt;&gt; 32</c> equals
    /// <c>n / divisor</c> for every n the transform can produce.
    /// </summary>
    public static uint Reciprocal(int quant) => (uint)(((1UL << 32) + (uint)(quant * 8) - 1) / (uint)(quant * 8));

    /// <summary>Quantizes scaled coefficients (natural order) into zigzag order, rounding half away from zero.</summary>
    /// <param name="coefficients">64 transform outputs, natural order.</param>
    /// <param name="quant">64 quantization factors, natural order.</param>
    /// <param name="reciprocal">Values from <see cref="Reciprocal"/>, natural order.</param>
    /// <param name="zigzag">The zigzag-to-natural index table.</param>
    /// <param name="output">64 quantized values in zigzag order.</param>
    public static void Quantize(ReadOnlySpan<int> coefficients, ReadOnlySpan<int> quant, ReadOnlySpan<uint> reciprocal, ReadOnlySpan<byte> zigzag, Span<short> output)
    {
        if (coefficients.Length < 64 || quant.Length < 64 || reciprocal.Length < 64 || zigzag.Length < 64 || output.Length < 64)
        {
            throw new ArgumentException("Quantizer buffer too small.");
        }

        for (int k = 0; k < 64; k++)
        {
            int n = zigzag[k] & 63;
            int c = coefficients[n];
            int half = quant[n] * 4;
            uint m = reciprocal[n];
            if (c < 0)
            {
                output[k] = (short)(-(int)(((ulong)(uint)(-c + half) * m) >> 32));
            }
            else
            {
                output[k] = (short)(int)(((ulong)(uint)(c + half) * m) >> 32);
            }
        }
    }
}
