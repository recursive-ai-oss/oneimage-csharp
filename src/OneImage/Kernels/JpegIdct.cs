using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// Inverse DCT kernels for JPEG decoding. The 8x8 transform is the accurate integer algorithm of
/// Loeffler, Ligtenberg and Moschytz in 13-bit fixed point (the same arithmetic as the widely used reference
/// decoder, so results are bit-comparable): the scalar version is the reference, and the AVX2 and 128-bit
/// versions run the identical arithmetic on eight or four columns at a time. Reduced transforms (4x4, 2x2, 1x1) implement DCT scaling:
/// each output sample is the average of a 2x2, 4x4 or 8x8 group of samples of the full inverse transform.
/// Callers validate buffer sizes; kernels never parse.
/// </summary>
internal static class JpegIdct
{
    private const int ConstBits = 13;

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

    // Constants of the reduced transforms, derived as round(x * 2^13).
    private const int F0211 = 1730;
    private const int F0509 = 4176;
    private const int F0601 = 4926;
    private const int F1061 = 8697;
    private const int F1451 = 11893;
    private const int F2172 = 17799;
    private const int F0720 = 5906;
    private const int F0850 = 6967;
    private const int F1272 = 10426;
    private const int F3624 = 29692;

    /// <summary>Gets whether the vectorized 8x8 transform is used on this machine.</summary>
    public static bool UsesVectorPath => UsesAvx2 || UsesVector128;

    private static bool UsesAvx2 => Avx2.IsSupported && Vector256.IsHardwareAccelerated;

    /// <summary>Gets whether the 128-bit path (SSE2 or AdvSimd) is usable: it needs hardware 128-bit vectors and a native 4x4 transpose.</summary>
    internal static bool UsesVector128 => Vector128.IsHardwareAccelerated && (Sse2.IsSupported || AdvSimd.Arm64.IsSupported);

    /// <summary>Gets the number of fractional bits of the intermediate rows for a sample precision.</summary>
    public static int Pass1Bits(int precision) => precision == 8 ? 2 : 1;

    /// <summary>
    /// Inverse DCT of one block into a sample plane, at the output size <paramref name="size"/> (8, 4, 2 or 1 samples per side).
    /// </summary>
    /// <param name="coef">64 coefficients, natural order.</param>
    /// <param name="quant">64 dequantization factors, natural order.</param>
    /// <param name="dst">Plane memory starting at the block's top-left sample.</param>
    /// <param name="stride">Plane row stride in samples.</param>
    /// <param name="size">Output block size.</param>
    /// <param name="precision">Sample precision in bits (8 selects the byte fast paths).</param>
    public static void Block<T>(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<T> dst, int stride, int size, int precision)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(byte))
        {
            Span<byte> d = MemoryMarshal.Cast<T, byte>(dst);
            if (size == 8)
            {
                Idct8(coef, quant, d, stride);
                return;
            }

            if (size == 1)
            {
                d[0] = (byte)Idct1(coef[0], quant[0], precision);
                return;
            }

            if (UsesVector128 && size == 4)
            {
                Idct4Vector128(coef, quant, d, stride, precision);
                return;
            }

            if (UsesVector128 && size == 2)
            {
                Idct2Vector128(coef, quant, d, stride, precision);
                return;
            }

            Span<ushort> tmp = stackalloc ushort[16];
            Reduced(coef, quant, tmp, size, precision);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    d[(y * stride) + x] = (byte)tmp[(y * size) + x];
                }
            }
        }
        else
        {
            Span<ushort> d = MemoryMarshal.Cast<T, ushort>(dst);
            if (size == 8)
            {
                Idct8Wide(coef, quant, d, stride, precision);
                return;
            }

            if (UsesVector128 && size == 4)
            {
                Idct4Vector128(coef, quant, d, stride, precision);
                return;
            }

            if (UsesVector128 && size == 2)
            {
                Idct2Vector128(coef, quant, d, stride, precision);
                return;
            }

            Span<ushort> tmp = stackalloc ushort[16];
            Reduced(coef, quant, tmp, size, precision);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    d[(y * stride) + x] = tmp[(y * size) + x];
                }
            }
        }
    }

    private static void Reduced(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<ushort> tmp, int size, int precision)
    {
        switch (size)
        {
            case 4:
                Idct4(coef, quant, tmp, 4, precision);
                break;
            case 2:
                Idct2(coef, quant, tmp, 2, precision);
                break;
            default:
                tmp[0] = Idct1(coef[0], quant[0], precision);
                break;
        }
    }

    /// <summary>Full 8x8 inverse DCT to 8-bit samples; picks the vector path when available.</summary>
    /// <param name="coef">64 coefficients, natural (row-major) order.</param>
    /// <param name="quant">64 dequantization factors, natural order.</param>
    /// <param name="dst">Output samples; row i starts at <c>i * stride</c>.</param>
    /// <param name="stride">Distance between output rows in elements.</param>
    public static void Idct8(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<byte> dst, int stride)
    {
        CheckArgs(coef, quant, dst.Length, stride, 8);
        if (UsesAvx2)
        {
            Idct8Avx2(coef, quant, dst, stride);
        }
        else if (UsesVector128)
        {
            Idct8Vector128(coef, quant, dst, stride);
        }
        else
        {
            Idct8Scalar(coef, quant, dst, stride);
        }
    }

    /// <summary>Scalar reference of <see cref="Idct8"/>.</summary>
    public static void Idct8Scalar(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<byte> dst, int stride)
    {
        CheckArgs(coef, quant, dst.Length, stride, 8);
        Span<int> o = stackalloc int[64];
        Core8(coef, quant, o, 2);
        for (int y = 0; y < 8; y++)
        {
            Span<byte> row = dst.Slice(y * stride, 8);
            for (int x = 0; x < 8; x++)
            {
                row[x] = (byte)Math.Clamp(o[(y * 8) + x] + 128, 0, 255);
            }
        }
    }

    /// <summary>Scalar 8x8 inverse DCT to samples of the given precision (9..12 bits use the wide intermediate).</summary>
    public static void Idct8Wide(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<ushort> dst, int stride, int precision)
    {
        CheckArgs(coef, quant, dst.Length, stride, 8);
        Span<int> o = stackalloc int[64];
        Core8(coef, quant, o, Pass1Bits(precision));
        int center = 1 << (precision - 1);
        int max = (1 << precision) - 1;
        for (int y = 0; y < 8; y++)
        {
            Span<ushort> row = dst.Slice(y * stride, 8);
            for (int x = 0; x < 8; x++)
            {
                row[x] = (ushort)Math.Clamp(o[(y * 8) + x] + center, 0, max);
            }
        }
    }

    /// <summary>Reduced 4x4 inverse DCT (DCT scaling 1/2).</summary>
    public static void Idct4(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<ushort> dst, int stride, int precision)
    {
        CheckArgs(coef, quant, dst.Length, stride, 4);
        int p1 = Pass1Bits(precision);
        Span<int> ws = stackalloc int[32];
        for (int c = 0; c < 8; c++)
        {
            if (c == 4)
            {
                continue;
            }

            int tmp0 = (coef[c] * quant[c]) << (ConstBits + 1);
            int z2 = coef[16 + c] * quant[16 + c];
            int z3 = coef[48 + c] * quant[48 + c];
            int tmp2 = (z2 * F1847) - (z3 * F0765);
            int tmp10 = tmp0 + tmp2;
            int tmp12 = tmp0 - tmp2;
            int z1 = coef[56 + c] * quant[56 + c];
            z2 = coef[40 + c] * quant[40 + c];
            z3 = coef[24 + c] * quant[24 + c];
            int z4 = coef[8 + c] * quant[8 + c];
            tmp0 = (-z1 * F0211) + (z2 * F1451) - (z3 * F2172) + (z4 * F1061);
            tmp2 = (-z1 * F0509) - (z2 * F0601) + (z3 * F0899) + (z4 * F2562);
            int sh = ConstBits - p1 + 1;
            ws[c] = Descale(tmp10 + tmp2, sh);
            ws[24 + c] = Descale(tmp10 - tmp2, sh);
            ws[8 + c] = Descale(tmp12 + tmp0, sh);
            ws[16 + c] = Descale(tmp12 - tmp0, sh);
        }

        int center = 1 << (precision - 1);
        int max = (1 << precision) - 1;
        for (int r = 0; r < 4; r++)
        {
            ReadOnlySpan<int> w = ws.Slice(r * 8, 8);
            int tmp0 = w[0] << (ConstBits + 1);
            int tmp2 = (w[2] * F1847) - (w[6] * F0765);
            int tmp10 = tmp0 + tmp2;
            int tmp12 = tmp0 - tmp2;
            int z1 = w[7];
            int z2 = w[5];
            int z3 = w[3];
            int z4 = w[1];
            tmp0 = (-z1 * F0211) + (z2 * F1451) - (z3 * F2172) + (z4 * F1061);
            tmp2 = (-z1 * F0509) - (z2 * F0601) + (z3 * F0899) + (z4 * F2562);
            int sh = ConstBits + p1 + 3 + 1;
            Span<ushort> o = dst.Slice(r * stride, 4);
            o[0] = (ushort)Math.Clamp(Descale(tmp10 + tmp2, sh) + center, 0, max);
            o[3] = (ushort)Math.Clamp(Descale(tmp10 - tmp2, sh) + center, 0, max);
            o[1] = (ushort)Math.Clamp(Descale(tmp12 + tmp0, sh) + center, 0, max);
            o[2] = (ushort)Math.Clamp(Descale(tmp12 - tmp0, sh) + center, 0, max);
        }
    }

    /// <summary>Reduced 2x2 inverse DCT (DCT scaling 1/4).</summary>
    public static void Idct2(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<ushort> dst, int stride, int precision)
    {
        CheckArgs(coef, quant, dst.Length, stride, 2);
        int p1 = Pass1Bits(precision);
        Span<int> ws = stackalloc int[16];
        for (int c = 0; c < 8; c++)
        {
            if (c is 2 or 4 or 6)
            {
                continue;
            }

            int tmp10 = (coef[c] * quant[c]) << (ConstBits + 2);
            int tmp0 = (-(coef[56 + c] * quant[56 + c]) * F0720) + (coef[40 + c] * quant[40 + c] * F0850)
                - (coef[24 + c] * quant[24 + c] * F1272) + (coef[8 + c] * quant[8 + c] * F3624);
            int sh = ConstBits - p1 + 2;
            ws[c] = Descale(tmp10 + tmp0, sh);
            ws[8 + c] = Descale(tmp10 - tmp0, sh);
        }

        int center = 1 << (precision - 1);
        int max = (1 << precision) - 1;
        for (int r = 0; r < 2; r++)
        {
            ReadOnlySpan<int> w = ws.Slice(r * 8, 8);
            int tmp10 = w[0] << (ConstBits + 2);
            int tmp0 = (-w[7] * F0720) + (w[5] * F0850) - (w[3] * F1272) + (w[1] * F3624);
            int sh = ConstBits + p1 + 3 + 2;
            Span<ushort> o = dst.Slice(r * stride, 2);
            o[0] = (ushort)Math.Clamp(Descale(tmp10 + tmp0, sh) + center, 0, max);
            o[1] = (ushort)Math.Clamp(Descale(tmp10 - tmp0, sh) + center, 0, max);
        }
    }

    /// <summary>Reduced 1x1 inverse DCT (DCT scaling 1/8): the block mean.</summary>
    public static ushort Idct1(short dc, int q, int precision)
    {
        int v = Descale(dc * q, 3) + (1 << (precision - 1));
        return (ushort)Math.Clamp(v, 0, (1 << precision) - 1);
    }

    /// <summary>
    /// The 1/8-size transform of a whole row of blocks: one sample per block, from the DC coefficient alone
    /// (<paramref name="blocks"/> holds 64 coefficients per block, of which only the first is read).
    /// </summary>
    public static void DcRow<T>(ReadOnlySpan<short> blocks, int count, int quant, Span<T> dst, int precision)
        where T : unmanaged, IBinaryInteger<T>
    {
        blocks = blocks[..(count * 64)];
        dst = dst[..count];
        int max = (1 << precision) - 1;
        int bias = 1 << (precision - 1);
        for (int i = 0; i < dst.Length; i++)
        {
            int v = Descale(blocks[i * 64] * quant, 3) + bias;
            dst[i] = T.CreateTruncating(v < 0 ? 0 : v > max ? max : v);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Descale(int x, int n) => (x + (1 << (n - 1))) >> n;

    private static void CheckArgs(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, int dstLength, int stride, int size)
    {
        if (coef.Length < 64 || quant.Length < 64 || stride < size || dstLength < ((size - 1) * stride) + size)
        {
            throw new ArgumentException("IDCT buffer too small.");
        }
    }

    /// <summary>Scalar two-pass transform; output is the descaled, uncentered samples.</summary>
    private static void Core8(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<int> output, int pass1Bits)
    {
        // A block with no AC coefficients is flat. Taking this shortcut (as the reference decoder does) also keeps huge
        // DC values from overflowing the 32-bit intermediate, identically in the scalar and vector paths.
        bool flat = true;
        for (int i = 1; i < 64 && flat; i++)
        {
            flat = coef[i] == 0;
        }

        if (flat)
        {
            output[..64].Fill(Descale((coef[0] * quant[0]) << pass1Bits, pass1Bits + 3));
            return;
        }

        Span<int> ws = stackalloc int[64];
        for (int c = 0; c < 8; c++)
        {
            int z2 = coef[16 + c] * quant[16 + c];
            int z3 = coef[48 + c] * quant[48 + c];
            int z1 = (z2 + z3) * F0541;
            int tmp2 = z1 + (z3 * -F1847);
            int tmp3 = z1 + (z2 * F0765);
            z2 = coef[c] * quant[c];
            z3 = coef[32 + c] * quant[32 + c];
            int tmp0 = (z2 + z3) << ConstBits;
            int tmp1 = (z2 - z3) << ConstBits;
            int tmp10 = tmp0 + tmp3;
            int tmp13 = tmp0 - tmp3;
            int tmp11 = tmp1 + tmp2;
            int tmp12 = tmp1 - tmp2;
            tmp0 = coef[56 + c] * quant[56 + c];
            tmp1 = coef[40 + c] * quant[40 + c];
            tmp2 = coef[24 + c] * quant[24 + c];
            tmp3 = coef[8 + c] * quant[8 + c];
            z1 = tmp0 + tmp3;
            z2 = tmp1 + tmp2;
            z3 = tmp0 + tmp2;
            int z4 = tmp1 + tmp3;
            int z5 = (z3 + z4) * F1175;
            tmp0 *= F0298;
            tmp1 *= F2053;
            tmp2 *= F3072;
            tmp3 *= F1501;
            z1 *= -F0899;
            z2 *= -F2562;
            z3 *= -F1961;
            z4 *= -F0390;
            z3 += z5;
            z4 += z5;
            tmp0 += z1 + z3;
            tmp1 += z2 + z4;
            tmp2 += z2 + z3;
            tmp3 += z1 + z4;
            int sh = ConstBits - pass1Bits;
            ws[c] = Descale(tmp10 + tmp3, sh);
            ws[56 + c] = Descale(tmp10 - tmp3, sh);
            ws[8 + c] = Descale(tmp11 + tmp2, sh);
            ws[48 + c] = Descale(tmp11 - tmp2, sh);
            ws[16 + c] = Descale(tmp12 + tmp1, sh);
            ws[40 + c] = Descale(tmp12 - tmp1, sh);
            ws[24 + c] = Descale(tmp13 + tmp0, sh);
            ws[32 + c] = Descale(tmp13 - tmp0, sh);
        }

        for (int r = 0; r < 8; r++)
        {
            ReadOnlySpan<int> w = ws.Slice(r * 8, 8);
            int z2 = w[2];
            int z3 = w[6];
            int z1 = (z2 + z3) * F0541;
            int tmp2 = z1 + (z3 * -F1847);
            int tmp3 = z1 + (z2 * F0765);
            int tmp0 = (w[0] + w[4]) << ConstBits;
            int tmp1 = (w[0] - w[4]) << ConstBits;
            int tmp10 = tmp0 + tmp3;
            int tmp13 = tmp0 - tmp3;
            int tmp11 = tmp1 + tmp2;
            int tmp12 = tmp1 - tmp2;
            tmp0 = w[7];
            tmp1 = w[5];
            tmp2 = w[3];
            tmp3 = w[1];
            z1 = tmp0 + tmp3;
            z2 = tmp1 + tmp2;
            z3 = tmp0 + tmp2;
            int z4 = tmp1 + tmp3;
            int z5 = (z3 + z4) * F1175;
            tmp0 *= F0298;
            tmp1 *= F2053;
            tmp2 *= F3072;
            tmp3 *= F1501;
            z1 *= -F0899;
            z2 *= -F2562;
            z3 *= -F1961;
            z4 *= -F0390;
            z3 += z5;
            z4 += z5;
            tmp0 += z1 + z3;
            tmp1 += z2 + z4;
            tmp2 += z2 + z3;
            tmp3 += z1 + z4;
            int sh = ConstBits + pass1Bits + 3;
            Span<int> o = output.Slice(r * 8, 8);
            o[0] = Descale(tmp10 + tmp3, sh);
            o[7] = Descale(tmp10 - tmp3, sh);
            o[1] = Descale(tmp11 + tmp2, sh);
            o[6] = Descale(tmp11 - tmp2, sh);
            o[2] = Descale(tmp12 + tmp1, sh);
            o[5] = Descale(tmp12 - tmp1, sh);
            o[3] = Descale(tmp13 + tmp0, sh);
            o[4] = Descale(tmp13 - tmp0, sh);
        }
    }

    // ---- AVX2 path: the same arithmetic on eight lanes ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Mul(Vector256<int> a, int k) => Vector256.Multiply(a, Vector256.Create(k));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> DescaleV(Vector256<int> x, int n) =>
        Vector256.ShiftRightArithmetic(x + Vector256.Create(1 << (n - 1)), n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass(
        Vector256<int> i0, Vector256<int> i1, Vector256<int> i2, Vector256<int> i3,
        Vector256<int> i4, Vector256<int> i5, Vector256<int> i6, Vector256<int> i7,
        int shift,
        out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3,
        out Vector256<int> o4, out Vector256<int> o5, out Vector256<int> o6, out Vector256<int> o7)
    {
        Vector256<int> z2 = i2;
        Vector256<int> z3 = i6;
        Vector256<int> z1 = Mul(z2 + z3, F0541);
        Vector256<int> tmp2 = z1 + Mul(z3, -F1847);
        Vector256<int> tmp3 = z1 + Mul(z2, F0765);
        Vector256<int> tmp0 = Vector256.ShiftLeft(i0 + i4, ConstBits);
        Vector256<int> tmp1 = Vector256.ShiftLeft(i0 - i4, ConstBits);
        Vector256<int> tmp10 = tmp0 + tmp3;
        Vector256<int> tmp13 = tmp0 - tmp3;
        Vector256<int> tmp11 = tmp1 + tmp2;
        Vector256<int> tmp12 = tmp1 - tmp2;
        tmp0 = i7;
        tmp1 = i5;
        tmp2 = i3;
        tmp3 = i1;
        z1 = tmp0 + tmp3;
        z2 = tmp1 + tmp2;
        z3 = tmp0 + tmp2;
        Vector256<int> z4 = tmp1 + tmp3;
        Vector256<int> z5 = Mul(z3 + z4, F1175);
        tmp0 = Mul(tmp0, F0298);
        tmp1 = Mul(tmp1, F2053);
        tmp2 = Mul(tmp2, F3072);
        tmp3 = Mul(tmp3, F1501);
        z1 = Mul(z1, -F0899);
        z2 = Mul(z2, -F2562);
        z3 = Mul(z3, -F1961) + z5;
        z4 = Mul(z4, -F0390) + z5;
        tmp0 += z1 + z3;
        tmp1 += z2 + z4;
        tmp2 += z2 + z3;
        tmp3 += z1 + z4;
        o0 = DescaleV(tmp10 + tmp3, shift);
        o7 = DescaleV(tmp10 - tmp3, shift);
        o1 = DescaleV(tmp11 + tmp2, shift);
        o6 = DescaleV(tmp11 - tmp2, shift);
        o2 = DescaleV(tmp12 + tmp1, shift);
        o5 = DescaleV(tmp12 - tmp1, shift);
        o3 = DescaleV(tmp13 + tmp0, shift);
        o4 = DescaleV(tmp13 - tmp0, shift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(
        ref Vector256<int> r0, ref Vector256<int> r1, ref Vector256<int> r2, ref Vector256<int> r3,
        ref Vector256<int> r4, ref Vector256<int> r5, ref Vector256<int> r6, ref Vector256<int> r7)
    {
        Vector256<int> t0 = Avx2.UnpackLow(r0, r1);
        Vector256<int> t1 = Avx2.UnpackHigh(r0, r1);
        Vector256<int> t2 = Avx2.UnpackLow(r2, r3);
        Vector256<int> t3 = Avx2.UnpackHigh(r2, r3);
        Vector256<int> t4 = Avx2.UnpackLow(r4, r5);
        Vector256<int> t5 = Avx2.UnpackHigh(r4, r5);
        Vector256<int> t6 = Avx2.UnpackLow(r6, r7);
        Vector256<int> t7 = Avx2.UnpackHigh(r6, r7);
        Vector256<long> u0 = Avx2.UnpackLow(t0.AsInt64(), t2.AsInt64());
        Vector256<long> u1 = Avx2.UnpackHigh(t0.AsInt64(), t2.AsInt64());
        Vector256<long> u2 = Avx2.UnpackLow(t1.AsInt64(), t3.AsInt64());
        Vector256<long> u3 = Avx2.UnpackHigh(t1.AsInt64(), t3.AsInt64());
        Vector256<long> u4 = Avx2.UnpackLow(t4.AsInt64(), t6.AsInt64());
        Vector256<long> u5 = Avx2.UnpackHigh(t4.AsInt64(), t6.AsInt64());
        Vector256<long> u6 = Avx2.UnpackLow(t5.AsInt64(), t7.AsInt64());
        Vector256<long> u7 = Avx2.UnpackHigh(t5.AsInt64(), t7.AsInt64());
        r0 = Avx2.Permute2x128(u0, u4, 0x20).AsInt32();
        r4 = Avx2.Permute2x128(u0, u4, 0x31).AsInt32();
        r1 = Avx2.Permute2x128(u1, u5, 0x20).AsInt32();
        r5 = Avx2.Permute2x128(u1, u5, 0x31).AsInt32();
        r2 = Avx2.Permute2x128(u2, u6, 0x20).AsInt32();
        r6 = Avx2.Permute2x128(u2, u6, 0x31).AsInt32();
        r3 = Avx2.Permute2x128(u3, u7, 0x20).AsInt32();
        r7 = Avx2.Permute2x128(u3, u7, 0x31).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Load(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, int row)
    {
        Vector128<short> c = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(coef), (nuint)(row * 8));
        Vector256<int> wide = Avx2.ConvertToVector256Int32(c);
        return Vector256.Multiply(wide, Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(quant), (nuint)(row * 8)));
    }

    private static void Idct8Avx2(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<byte> dst, int stride)
    {
        Vector256<int> a0 = Load(coef, quant, 0);
        Vector256<int> a1 = Load(coef, quant, 1);
        Vector256<int> a2 = Load(coef, quant, 2);
        Vector256<int> a3 = Load(coef, quant, 3);
        Vector256<int> a4 = Load(coef, quant, 4);
        Vector256<int> a5 = Load(coef, quant, 5);
        Vector256<int> a6 = Load(coef, quant, 6);
        Vector256<int> a7 = Load(coef, quant, 7);

        // Flat blocks (no AC at all) are common; the full transform of such a block is exactly this constant.
        Vector256<int> ac = a1 | a2 | a3 | a4 | a5 | a6 | a7 | (a0 & Vector256.Create(0, -1, -1, -1, -1, -1, -1, -1));
        if (ac == Vector256<int>.Zero)
        {
            int dc = a0.GetElement(0);
            byte v = (byte)Math.Clamp((((dc << 2) + 16) >> 5) + 128, 0, 255);
            ulong fill = 0x0101010101010101UL * v;
            ref byte d0 = ref MemoryMarshal.GetReference(dst);
            for (int y = 0; y < 8; y++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d0, y * stride), fill);
            }

            return;
        }

        Pass(a0, a1, a2, a3, a4, a5, a6, a7, ConstBits - 2, out a0, out a1, out a2, out a3, out a4, out a5, out a6, out a7);
        Transpose(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
        Pass(a0, a1, a2, a3, a4, a5, a6, a7, ConstBits + 2 + 3, out a0, out a1, out a2, out a3, out a4, out a5, out a6, out a7);
        Transpose(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);

        ref byte d = ref MemoryMarshal.GetReference(dst);
        Store4(ref d, stride, a0, a1, a2, a3);
        Store4(ref Unsafe.Add(ref d, 4 * stride), stride, a4, a5, a6, a7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Level(Vector256<int> v)
    {
        Vector256<int> c = Vector256.Min(Vector256.Max(v + Vector256.Create(128), Vector256<int>.Zero), Vector256.Create(255));
        return c.AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4(ref byte d, int stride, Vector256<int> r0, Vector256<int> r1, Vector256<int> r2, Vector256<int> r3)
    {
        Vector256<ushort> lo = Vector256.Narrow(Level(r0), Level(r1));
        Vector256<ushort> hi = Vector256.Narrow(Level(r2), Level(r3));
        Vector256<ulong> b = Vector256.Narrow(lo, hi).AsUInt64();
        Unsafe.WriteUnaligned(ref d, b.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, stride), b.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 2 * stride), b.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 3 * stride), b.GetElement(3));
    }

    // ---- 128-bit path (SSE2/SSE4.1 on x64, AdvSimd on ARM64): the same arithmetic on four lanes ----
    // A block row is two vectors (columns 0..3 and 4..7); the two halves run the same pass independently.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul(Vector128<int> a, int k) => Vector128.Multiply(a, Vector128.Create(k));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> DescaleV(Vector128<int> x, int n) =>
        Vector128.ShiftRightArithmetic(x + Vector128.Create(1 << (n - 1)), n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass(
        Vector128<int> i0, Vector128<int> i1, Vector128<int> i2, Vector128<int> i3,
        Vector128<int> i4, Vector128<int> i5, Vector128<int> i6, Vector128<int> i7,
        int shift,
        out Vector128<int> o0, out Vector128<int> o1, out Vector128<int> o2, out Vector128<int> o3,
        out Vector128<int> o4, out Vector128<int> o5, out Vector128<int> o6, out Vector128<int> o7)
    {
        Vector128<int> z2 = i2;
        Vector128<int> z3 = i6;
        Vector128<int> z1 = Mul(z2 + z3, F0541);
        Vector128<int> tmp2 = z1 + Mul(z3, -F1847);
        Vector128<int> tmp3 = z1 + Mul(z2, F0765);
        Vector128<int> tmp0 = Vector128.ShiftLeft(i0 + i4, ConstBits);
        Vector128<int> tmp1 = Vector128.ShiftLeft(i0 - i4, ConstBits);
        Vector128<int> tmp10 = tmp0 + tmp3;
        Vector128<int> tmp13 = tmp0 - tmp3;
        Vector128<int> tmp11 = tmp1 + tmp2;
        Vector128<int> tmp12 = tmp1 - tmp2;
        tmp0 = i7;
        tmp1 = i5;
        tmp2 = i3;
        tmp3 = i1;
        z1 = tmp0 + tmp3;
        z2 = tmp1 + tmp2;
        z3 = tmp0 + tmp2;
        Vector128<int> z4 = tmp1 + tmp3;
        Vector128<int> z5 = Mul(z3 + z4, F1175);
        tmp0 = Mul(tmp0, F0298);
        tmp1 = Mul(tmp1, F2053);
        tmp2 = Mul(tmp2, F3072);
        tmp3 = Mul(tmp3, F1501);
        z1 = Mul(z1, -F0899);
        z2 = Mul(z2, -F2562);
        z3 = Mul(z3, -F1961) + z5;
        z4 = Mul(z4, -F0390) + z5;
        tmp0 += z1 + z3;
        tmp1 += z2 + z4;
        tmp2 += z2 + z3;
        tmp3 += z1 + z4;
        o0 = DescaleV(tmp10 + tmp3, shift);
        o7 = DescaleV(tmp10 - tmp3, shift);
        o1 = DescaleV(tmp11 + tmp2, shift);
        o6 = DescaleV(tmp11 - tmp2, shift);
        o2 = DescaleV(tmp12 + tmp1, shift);
        o5 = DescaleV(tmp12 - tmp1, shift);
        o3 = DescaleV(tmp13 + tmp0, shift);
        o4 = DescaleV(tmp13 - tmp0, shift);
    }

    /// <summary>Transposes a 4x4 block of 32-bit lanes held in four vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose4(ref Vector128<int> a, ref Vector128<int> b, ref Vector128<int> c, ref Vector128<int> d)
    {
        if (Sse2.IsSupported)
        {
            Vector128<int> t0 = Sse2.UnpackLow(a, b);
            Vector128<int> t1 = Sse2.UnpackHigh(a, b);
            Vector128<int> t2 = Sse2.UnpackLow(c, d);
            Vector128<int> t3 = Sse2.UnpackHigh(c, d);
            a = Sse2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32();
            b = Sse2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
            c = Sse2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32();
            d = Sse2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
        }
        else
        {
            Vector128<int> t0 = AdvSimd.Arm64.ZipLow(a, b);
            Vector128<int> t1 = AdvSimd.Arm64.ZipHigh(a, b);
            Vector128<int> t2 = AdvSimd.Arm64.ZipLow(c, d);
            Vector128<int> t3 = AdvSimd.Arm64.ZipHigh(c, d);
            a = AdvSimd.Arm64.ZipLow(t0.AsInt64(), t2.AsInt64()).AsInt32();
            b = AdvSimd.Arm64.ZipHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
            c = AdvSimd.Arm64.ZipLow(t1.AsInt64(), t3.AsInt64()).AsInt32();
            d = AdvSimd.Arm64.ZipHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
        }
    }

    /// <summary>Transposes an 8x8 block of 32-bit values stored as low (columns 0..3) and high (columns 4..7) halves of eight rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose8(
        ref Vector128<int> l0, ref Vector128<int> l1, ref Vector128<int> l2, ref Vector128<int> l3,
        ref Vector128<int> l4, ref Vector128<int> l5, ref Vector128<int> l6, ref Vector128<int> l7,
        ref Vector128<int> h0, ref Vector128<int> h1, ref Vector128<int> h2, ref Vector128<int> h3,
        ref Vector128<int> h4, ref Vector128<int> h5, ref Vector128<int> h6, ref Vector128<int> h7)
    {
        Transpose4(ref l0, ref l1, ref l2, ref l3);
        Transpose4(ref h0, ref h1, ref h2, ref h3);
        Transpose4(ref l4, ref l5, ref l6, ref l7);
        Transpose4(ref h4, ref h5, ref h6, ref h7);

        // The off-diagonal 4x4 quadrants trade places.
        (h0, l4) = (l4, h0);
        (h1, l5) = (l5, h1);
        (h2, l6) = (l6, h2);
        (h3, l7) = (l7, h3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, int row, out Vector128<int> lo, out Vector128<int> hi)
    {
        Vector128<short> c = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(coef), (nuint)(row * 8));
        ref int q = ref MemoryMarshal.GetReference(quant);
        lo = Vector128.WidenLower(c) * Vector128.LoadUnsafe(ref q, (nuint)(row * 8));
        hi = Vector128.WidenUpper(c) * Vector128.LoadUnsafe(ref q, (nuint)((row * 8) + 4));
    }

    /// <summary>128-bit form of <see cref="Idct8"/>: identical output, four columns per vector.</summary>
    public static void Idct8Vector128(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<byte> dst, int stride)
    {
        CheckArgs(coef, quant, dst.Length, stride, 8);
        Load(coef, quant, 0, out Vector128<int> l0, out Vector128<int> h0);
        Load(coef, quant, 1, out Vector128<int> l1, out Vector128<int> h1);
        Load(coef, quant, 2, out Vector128<int> l2, out Vector128<int> h2);
        Load(coef, quant, 3, out Vector128<int> l3, out Vector128<int> h3);
        Load(coef, quant, 4, out Vector128<int> l4, out Vector128<int> h4);
        Load(coef, quant, 5, out Vector128<int> l5, out Vector128<int> h5);
        Load(coef, quant, 6, out Vector128<int> l6, out Vector128<int> h6);
        Load(coef, quant, 7, out Vector128<int> l7, out Vector128<int> h7);

        // Flat blocks (no AC at all): the full transform is exactly this constant.
        Vector128<int> ac = l1 | l2 | l3 | l4 | l5 | l6 | l7 | h0 | h1 | h2 | h3 | h4 | h5 | h6 | h7
            | (l0 & Vector128.Create(0, -1, -1, -1));
        if (ac == Vector128<int>.Zero)
        {
            int dc = l0.GetElement(0);
            byte v = (byte)Math.Clamp((((dc << 2) + 16) >> 5) + 128, 0, 255);
            ulong fill = 0x0101010101010101UL * v;
            ref byte d0 = ref MemoryMarshal.GetReference(dst);
            for (int y = 0; y < 8; y++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d0, y * stride), fill);
            }

            return;
        }

        Pass(l0, l1, l2, l3, l4, l5, l6, l7, ConstBits - 2, out l0, out l1, out l2, out l3, out l4, out l5, out l6, out l7);
        Pass(h0, h1, h2, h3, h4, h5, h6, h7, ConstBits - 2, out h0, out h1, out h2, out h3, out h4, out h5, out h6, out h7);
        Transpose8(ref l0, ref l1, ref l2, ref l3, ref l4, ref l5, ref l6, ref l7, ref h0, ref h1, ref h2, ref h3, ref h4, ref h5, ref h6, ref h7);
        Pass(l0, l1, l2, l3, l4, l5, l6, l7, ConstBits + 2 + 3, out l0, out l1, out l2, out l3, out l4, out l5, out l6, out l7);
        Pass(h0, h1, h2, h3, h4, h5, h6, h7, ConstBits + 2 + 3, out h0, out h1, out h2, out h3, out h4, out h5, out h6, out h7);
        Transpose8(ref l0, ref l1, ref l2, ref l3, ref l4, ref l5, ref l6, ref l7, ref h0, ref h1, ref h2, ref h3, ref h4, ref h5, ref h6, ref h7);

        ref byte d = ref MemoryMarshal.GetReference(dst);
        Store2(ref d, stride, l0, h0, l1, h1);
        Store2(ref Unsafe.Add(ref d, 2 * stride), stride, l2, h2, l3, h3);
        Store2(ref Unsafe.Add(ref d, 4 * stride), stride, l4, h4, l5, h5);
        Store2(ref Unsafe.Add(ref d, 6 * stride), stride, l6, h6, l7, h7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Level(Vector128<int> lo, Vector128<int> hi)
    {
        Vector128<int> bias = Vector128.Create(128);
        Vector128<int> max = Vector128.Create(255);
        Vector128<int> a = Vector128.Min(Vector128.Max(lo + bias, Vector128<int>.Zero), max);
        Vector128<int> b = Vector128.Min(Vector128.Max(hi + bias, Vector128<int>.Zero), max);
        return Vector128.Narrow(a.AsUInt32(), b.AsUInt32());
    }

    // Two rows of eight samples: one 16-byte narrow, then two 8-byte stores.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store2(ref byte d, int stride, Vector128<int> aLo, Vector128<int> aHi, Vector128<int> bLo, Vector128<int> bHi)
    {
        Vector128<ulong> b = Vector128.Narrow(Level(aLo, aHi), Level(bLo, bHi)).AsUInt64();
        Unsafe.WriteUnaligned(ref d, b.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, stride), b.GetElement(1));
    }

    // ---- Reduced transforms on 128-bit vectors ----
    // Pass 1 runs on columns (four per vector, as in the 8x8 path); pass 2 runs with one row per lane, so the
    // intermediate is transposed between the passes. The arithmetic is the scalar Idct4/Idct2 term for term
    // (32-bit wrapping multiplies, same shifts), so results are identical.

    // One pass of the 4-point reduced transform: odd part from inputs 1, 3, 5, 7, even part from 0, 2, 6; input 4 is unused.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reduced4(
        Vector128<int> i0, Vector128<int> i1, Vector128<int> i2, Vector128<int> i3,
        Vector128<int> i5, Vector128<int> i6, Vector128<int> i7, int shift,
        out Vector128<int> o0, out Vector128<int> o1, out Vector128<int> o2, out Vector128<int> o3)
    {
        Vector128<int> t0 = Vector128.ShiftLeft(i0, ConstBits + 1);
        Vector128<int> t2 = Mul(i2, F1847) - Mul(i6, F0765);
        Vector128<int> t10 = t0 + t2;
        Vector128<int> t12 = t0 - t2;
        Vector128<int> u0 = Mul(i5, F1451) + Mul(i1, F1061) - Mul(i7, F0211) - Mul(i3, F2172);
        Vector128<int> u2 = Mul(i3, F0899) + Mul(i1, F2562) - Mul(i7, F0509) - Mul(i5, F0601);
        o0 = DescaleV(t10 + u2, shift);
        o3 = DescaleV(t10 - u2, shift);
        o1 = DescaleV(t12 + u0, shift);
        o2 = DescaleV(t12 - u0, shift);
    }

    // One pass of the 2-point reduced transform from inputs 0, 1, 3, 5, 7.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reduced2(
        Vector128<int> i0, Vector128<int> i1, Vector128<int> i3, Vector128<int> i5, Vector128<int> i7, int shift,
        out Vector128<int> o0, out Vector128<int> o1)
    {
        Vector128<int> t10 = Vector128.ShiftLeft(i0, ConstBits + 2);
        Vector128<int> t0 = Mul(i5, F0850) + Mul(i1, F3624) - Mul(i7, F0720) - Mul(i3, F1272);
        o0 = DescaleV(t10 + t0, shift);
        o1 = DescaleV(t10 - t0, shift);
    }

    // Lanes a0, b0, a1, b1: the two output columns of rows 0 and 1 side by side.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Interleave(Vector128<int> a, Vector128<int> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Clamp(Vector128<int> v, int center, int max) =>
        Vector128.Min(Vector128.Max(v + Vector128.Create(center), Vector128<int>.Zero), Vector128.Create(max));

    /// <summary>128-bit form of <see cref="Idct4"/> writing samples of type <typeparamref name="T"/> (byte or ushort): identical output.</summary>
    public static void Idct4Vector128<T>(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<T> dst, int stride, int precision)
        where T : unmanaged, IBinaryInteger<T>
    {
        CheckArgs(coef, quant, dst.Length, stride, 4);
        int p1 = Pass1Bits(precision);
        Load(coef, quant, 0, out Vector128<int> l0, out Vector128<int> h0);
        Load(coef, quant, 1, out Vector128<int> l1, out Vector128<int> h1);
        Load(coef, quant, 2, out Vector128<int> l2, out Vector128<int> h2);
        Load(coef, quant, 3, out Vector128<int> l3, out Vector128<int> h3);
        Load(coef, quant, 5, out Vector128<int> l5, out Vector128<int> h5);
        Load(coef, quant, 6, out Vector128<int> l6, out Vector128<int> h6);
        Load(coef, quant, 7, out Vector128<int> l7, out Vector128<int> h7);

        int sh1 = ConstBits - p1 + 1;
        Reduced4(l0, l1, l2, l3, l5, l6, l7, sh1, out Vector128<int> a0, out Vector128<int> a1, out Vector128<int> a2, out Vector128<int> a3);
        Reduced4(h0, h1, h2, h3, h5, h6, h7, sh1, out Vector128<int> b0, out Vector128<int> b1, out Vector128<int> b2, out Vector128<int> b3);
        Transpose4(ref a0, ref a1, ref a2, ref a3); // columns 0..3, one row per lane
        Transpose4(ref b0, ref b1, ref b2, ref b3); // columns 4..7 (4 is unused)

        int sh2 = ConstBits + p1 + 3 + 1;
        Reduced4(a0, a1, a2, a3, b1, b2, b3, sh2, out Vector128<int> o0, out Vector128<int> o1, out Vector128<int> o2, out Vector128<int> o3);
        Transpose4(ref o0, ref o1, ref o2, ref o3); // back to rows

        int center = 1 << (precision - 1);
        int max = (1 << precision) - 1;
        o0 = Clamp(o0, center, max);
        o1 = Clamp(o1, center, max);
        o2 = Clamp(o2, center, max);
        o3 = Clamp(o3, center, max);
        Vector128<ushort> n01 = Vector128.Narrow(o0.AsUInt32(), o1.AsUInt32());
        Vector128<ushort> n23 = Vector128.Narrow(o2.AsUInt32(), o3.AsUInt32());
        ref T d = ref MemoryMarshal.GetReference(dst);
        if (typeof(T) == typeof(byte))
        {
            Vector128<uint> b = Vector128.Narrow(n01, n23).AsUInt32();
            ref byte db = ref Unsafe.As<T, byte>(ref d);
            Unsafe.WriteUnaligned(ref db, b.GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref db, stride), b.GetElement(1));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref db, 2 * stride), b.GetElement(2));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref db, 3 * stride), b.GetElement(3));
        }
        else
        {
            ref ushort dw = ref Unsafe.As<T, ushort>(ref d);
            Vector128<ulong> p = n01.AsUInt64();
            Vector128<ulong> q = n23.AsUInt64();
            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref dw), p.GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref dw, stride)), p.GetElement(1));
            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref dw, 2 * stride)), q.GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref dw, 3 * stride)), q.GetElement(1));
        }
    }

    /// <summary>128-bit form of <see cref="Idct2"/> writing samples of type <typeparamref name="T"/> (byte or ushort): identical output.</summary>
    public static void Idct2Vector128<T>(ReadOnlySpan<short> coef, ReadOnlySpan<int> quant, Span<T> dst, int stride, int precision)
        where T : unmanaged, IBinaryInteger<T>
    {
        CheckArgs(coef, quant, dst.Length, stride, 2);
        int p1 = Pass1Bits(precision);
        Load(coef, quant, 0, out Vector128<int> l0, out Vector128<int> h0);
        Load(coef, quant, 1, out Vector128<int> l1, out Vector128<int> h1);
        Load(coef, quant, 3, out Vector128<int> l3, out Vector128<int> h3);
        Load(coef, quant, 5, out Vector128<int> l5, out Vector128<int> h5);
        Load(coef, quant, 7, out Vector128<int> l7, out Vector128<int> h7);

        int sh1 = ConstBits - p1 + 2;
        Reduced2(l0, l1, l3, l5, l7, sh1, out Vector128<int> a0, out Vector128<int> a1);
        Reduced2(h0, h1, h3, h5, h7, sh1, out Vector128<int> b0, out Vector128<int> b1);
        Vector128<int> a2 = Vector128<int>.Zero;
        Vector128<int> a3 = Vector128<int>.Zero;
        Vector128<int> b2 = Vector128<int>.Zero;
        Vector128<int> b3 = Vector128<int>.Zero;
        Transpose4(ref a0, ref a1, ref a2, ref a3); // columns 0..3, one row per lane
        Transpose4(ref b0, ref b1, ref b2, ref b3); // columns 4..7: only 5 and 7 are used

        int sh2 = ConstBits + p1 + 3 + 2;
        Reduced2(a0, a1, a3, b1, b3, sh2, out Vector128<int> o0, out Vector128<int> o1);
        int center = 1 << (precision - 1);
        int max = (1 << precision) - 1;
        o0 = Clamp(o0, center, max); // output column 0, one row per lane
        o1 = Clamp(o1, center, max);
        ref T d = ref MemoryMarshal.GetReference(dst);
        if (typeof(T) == typeof(byte))
        {
            Vector128<ushort> w = Vector128.Narrow(Interleave(o0, o1).AsUInt32(), Vector128<uint>.Zero);
            Vector128<byte> bytes = Vector128.Narrow(w, Vector128<ushort>.Zero);
            ref byte db = ref Unsafe.As<T, byte>(ref d);
            Unsafe.WriteUnaligned(ref db, bytes.AsUInt16().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref db, stride), bytes.AsUInt16().GetElement(1));
        }
        else
        {
            Vector128<ushort> w = Vector128.Narrow(Interleave(o0, o1).AsUInt32(), Vector128<uint>.Zero);
            Vector128<uint> pairs = w.AsUInt32();
            ref byte db = ref Unsafe.As<T, byte>(ref d);
            Unsafe.WriteUnaligned(ref db, pairs.GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref db, 2 * stride), pairs.GetElement(1));
        }
    }
}
