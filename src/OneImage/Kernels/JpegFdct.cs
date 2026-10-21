using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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

    /// <summary>
    /// Transforms and quantizes one block: <see cref="Forward"/> followed by <see cref="Quantize"/>, with an AVX2 path that
    /// produces exactly the same integers.
    /// </summary>
    public static void ForwardQuantize(ReadOnlySpan<byte> samples, int stride, ReadOnlySpan<int> quant, ReadOnlySpan<uint> reciprocal, ReadOnlySpan<byte> zigzag, Span<short> output)
    {
        if (stride < 8 || samples.Length < (7 * stride) + 8 || quant.Length < 64 || reciprocal.Length < 64 || zigzag.Length < 64 || output.Length < 64)
        {
            throw new ArgumentException("FDCT buffer too small.");
        }

        Span<int> coef = stackalloc int[64];
        if (Avx2.IsSupported)
        {
            ForwardAvx2(samples, stride, coef);
            Span<short> natural = stackalloc short[64];
            QuantizeAvx2(coef, quant, reciprocal, natural);
            for (int k = 0; k < 64; k++)
            {
                output[k] = natural[zigzag[k] & 63];
            }

            return;
        }

        Forward(samples, stride, coef);
        Quantize(coef, quant, reciprocal, zigzag, output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector256<int> r0, ref Vector256<int> r1, ref Vector256<int> r2, ref Vector256<int> r3, ref Vector256<int> r4, ref Vector256<int> r5, ref Vector256<int> r6, ref Vector256<int> r7)
    {
        Vector256<int> t0 = Avx2.UnpackLow(r0, r1), t1 = Avx2.UnpackHigh(r0, r1);
        Vector256<int> t2 = Avx2.UnpackLow(r2, r3), t3 = Avx2.UnpackHigh(r2, r3);
        Vector256<int> t4 = Avx2.UnpackLow(r4, r5), t5 = Avx2.UnpackHigh(r4, r5);
        Vector256<int> t6 = Avx2.UnpackLow(r6, r7), t7 = Avx2.UnpackHigh(r6, r7);
        Vector256<long> u0 = Avx2.UnpackLow(t0.AsInt64(), t2.AsInt64()), u1 = Avx2.UnpackHigh(t0.AsInt64(), t2.AsInt64());
        Vector256<long> u2 = Avx2.UnpackLow(t1.AsInt64(), t3.AsInt64()), u3 = Avx2.UnpackHigh(t1.AsInt64(), t3.AsInt64());
        Vector256<long> u4 = Avx2.UnpackLow(t4.AsInt64(), t6.AsInt64()), u5 = Avx2.UnpackHigh(t4.AsInt64(), t6.AsInt64());
        Vector256<long> u6 = Avx2.UnpackLow(t5.AsInt64(), t7.AsInt64()), u7 = Avx2.UnpackHigh(t5.AsInt64(), t7.AsInt64());
        r0 = Avx2.Permute2x128(u0, u4, 0x20).AsInt32();
        r1 = Avx2.Permute2x128(u1, u5, 0x20).AsInt32();
        r2 = Avx2.Permute2x128(u2, u6, 0x20).AsInt32();
        r3 = Avx2.Permute2x128(u3, u7, 0x20).AsInt32();
        r4 = Avx2.Permute2x128(u0, u4, 0x31).AsInt32();
        r5 = Avx2.Permute2x128(u1, u5, 0x31).AsInt32();
        r6 = Avx2.Permute2x128(u2, u6, 0x31).AsInt32();
        r7 = Avx2.Permute2x128(u3, u7, 0x31).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Mul(Vector256<int> a, int k) => Avx2.MultiplyLow(a, Vector256.Create(k));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Descale(Vector256<int> x, int n) => Avx2.ShiftRightArithmetic(Avx2.Add(x, Vector256.Create(1 << (n - 1))), Vector128.CreateScalar(n));

    /// <summary>One 1-D pass over eight vectors (the same arithmetic as <see cref="Even"/> and <see cref="Odd"/>, on eight lanes at once).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass(ref Vector256<int> v0, ref Vector256<int> v1, ref Vector256<int> v2, ref Vector256<int> v3, ref Vector256<int> v4, ref Vector256<int> v5, ref Vector256<int> v6, ref Vector256<int> v7, bool rowPass)
    {
        int rot = rowPass ? ConstBits - Pass1Bits : ConstBits + Pass1Bits;
        Vector256<int> tmp0 = Avx2.Add(v0, v7), tmp7 = Avx2.Subtract(v0, v7);
        Vector256<int> tmp1 = Avx2.Add(v1, v6), tmp6 = Avx2.Subtract(v1, v6);
        Vector256<int> tmp2 = Avx2.Add(v2, v5), tmp5 = Avx2.Subtract(v2, v5);
        Vector256<int> tmp3 = Avx2.Add(v3, v4), tmp4 = Avx2.Subtract(v3, v4);

        Vector256<int> tmp10 = Avx2.Add(tmp0, tmp3), tmp13 = Avx2.Subtract(tmp0, tmp3);
        Vector256<int> tmp11 = Avx2.Add(tmp1, tmp2), tmp12 = Avx2.Subtract(tmp1, tmp2);
        Vector256<int> o0, o4;
        if (rowPass)
        {
            o0 = Avx2.ShiftLeftLogical(Avx2.Subtract(Avx2.Add(tmp10, tmp11), Vector256.Create(1024)), Pass1Bits);
            o4 = Avx2.ShiftLeftLogical(Avx2.Subtract(tmp10, tmp11), Pass1Bits);
        }
        else
        {
            o0 = Descale(Avx2.Add(tmp10, tmp11), Pass1Bits);
            o4 = Descale(Avx2.Subtract(tmp10, tmp11), Pass1Bits);
        }

        Vector256<int> z1 = Mul(Avx2.Add(tmp12, tmp13), F0541);
        Vector256<int> o2 = Descale(Avx2.Add(z1, Mul(tmp13, F0765)), rot);
        Vector256<int> o6 = Descale(Avx2.Add(z1, Mul(tmp12, -F1847)), rot);

        Vector256<int> a1 = Avx2.Add(tmp4, tmp7);
        Vector256<int> a2 = Avx2.Add(tmp5, tmp6);
        Vector256<int> a3 = Avx2.Add(tmp4, tmp6);
        Vector256<int> a4 = Avx2.Add(tmp5, tmp7);
        Vector256<int> z5 = Mul(Avx2.Add(a3, a4), F1175);
        tmp4 = Mul(tmp4, F0298);
        tmp5 = Mul(tmp5, F2053);
        tmp6 = Mul(tmp6, F3072);
        tmp7 = Mul(tmp7, F1501);
        a1 = Mul(a1, -F0899);
        a2 = Mul(a2, -F2562);
        a3 = Avx2.Add(Mul(a3, -F1961), z5);
        a4 = Avx2.Add(Mul(a4, -F0390), z5);
        Vector256<int> o7 = Descale(Avx2.Add(Avx2.Add(tmp4, a1), a3), rot);
        Vector256<int> o5 = Descale(Avx2.Add(Avx2.Add(tmp5, a2), a4), rot);
        Vector256<int> o3 = Descale(Avx2.Add(Avx2.Add(tmp6, a2), a3), rot);
        Vector256<int> o1 = Descale(Avx2.Add(Avx2.Add(tmp7, a1), a4), rot);
        v0 = o0;
        v1 = o1;
        v2 = o2;
        v3 = o3;
        v4 = o4;
        v5 = o5;
        v6 = o6;
        v7 = o7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> LoadRow(ReadOnlySpan<byte> samples, int offset) =>
        Avx2.ConvertToVector256Int32(Vector128.CreateScalar(BinaryPrimitives.ReadUInt64LittleEndian(samples.Slice(offset, 8))).AsByte());

    /// <summary>AVX2 form of <see cref="Forward"/>: identical output, one block in eight-lane vectors.</summary>
    public static void ForwardAvx2(ReadOnlySpan<byte> samples, int stride, Span<int> output)
    {
        Vector256<int> r0 = LoadRow(samples, 0), r1 = LoadRow(samples, stride), r2 = LoadRow(samples, 2 * stride), r3 = LoadRow(samples, 3 * stride);
        Vector256<int> r4 = LoadRow(samples, 4 * stride), r5 = LoadRow(samples, 5 * stride), r6 = LoadRow(samples, 6 * stride), r7 = LoadRow(samples, 7 * stride);
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Pass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, true);
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Pass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, false);
        r0.CopyTo(output.Slice(0, 8));
        r1.CopyTo(output.Slice(8, 8));
        r2.CopyTo(output.Slice(16, 8));
        r3.CopyTo(output.Slice(24, 8));
        r4.CopyTo(output.Slice(32, 8));
        r5.CopyTo(output.Slice(40, 8));
        r6.CopyTo(output.Slice(48, 8));
        r7.CopyTo(output.Slice(56, 8));
    }

    /// <summary>AVX2 form of the quantizer without the zigzag reorder: natural-order shorts, same rounding as <see cref="Quantize"/>.</summary>
    public static void QuantizeAvx2(ReadOnlySpan<int> coefficients, ReadOnlySpan<int> quant, ReadOnlySpan<uint> reciprocal, Span<short> natural)
    {
        for (int i = 0; i < 64; i += 16)
        {
            Vector256<int> a = QuantizeEight(coefficients.Slice(i, 8), quant.Slice(i, 8), reciprocal.Slice(i, 8));
            Vector256<int> b = QuantizeEight(coefficients.Slice(i + 8, 8), quant.Slice(i + 8, 8), reciprocal.Slice(i + 8, 8));
            Vector256<short> packed = Avx2.PackSignedSaturate(a, b);
            Avx2.Permute4x64(packed.AsInt64(), 0xD8).AsInt16().CopyTo(natural.Slice(i, 16));
        }
    }

    private static Vector256<int> QuantizeEight(ReadOnlySpan<int> c, ReadOnlySpan<int> q, ReadOnlySpan<uint> r)
    {
        Vector256<int> coef = Vector256.Create(c);
        Vector256<uint> half = Avx2.ShiftLeftLogical(Vector256.Create(q).AsUInt32(), 2);
        Vector256<uint> n = Avx2.Add(Vector256.Abs(coef), half.AsInt32()).AsUInt32();
        Vector256<uint> rec = Vector256.Create(r);
        Vector256<ulong> even = Avx2.Multiply(n, rec);
        Vector256<ulong> odd = Avx2.Multiply(Avx2.ShiftRightLogical(n.AsUInt64(), 32).AsUInt32(), Avx2.ShiftRightLogical(rec.AsUInt64(), 32).AsUInt32());
        Vector256<uint> res = Avx2.Or(Avx2.ShiftRightLogical(even, 32).AsUInt32(), odd.AsUInt32() & Vector256.Create(0xFFFFFFFF00000000UL).AsUInt32());
        return Avx2.Sign(res.AsInt32(), coef);
    }
}
