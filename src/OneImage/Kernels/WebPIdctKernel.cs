using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// The VP8 4x4 inverse DCT added to a block of predicted pixels (RFC 6386 section 14.3). The vector path computes all four
/// columns at once in 32-bit lanes with exactly the integer steps of <see cref="AddScalar"/>, so both give identical bytes.
/// </summary>
internal static class WebPIdctKernel
{
    /// <summary>Adds the inverse transform of <paramref name="input"/> (16 raster coefficients) to the 4x4 block at <paramref name="off"/> in <paramref name="dst"/> with the given row stride.</summary>
    public static void Add(ReadOnlySpan<short> input, byte[] dst, int off, int stride)
    {
        if (input.Length < 16 || off < 0 || stride < 4 || (long)off + (3L * stride) + 4 > dst.Length)
        {
            throw new ArgumentException("Invalid block.");
        }

        if (Sse2.IsSupported)
        {
            AddVector(input, dst, off, stride);
        }
        else
        {
            AddScalar(input, dst, off, stride);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul1(Vector128<int> a) => Vector128.ShiftRightArithmetic(a * 20091, 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Mul2(Vector128<int> a) => Vector128.ShiftRightArithmetic(a * 35468, 16);

    // 4x4 transpose of 32-bit lanes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector128<int> r0, ref Vector128<int> r1, ref Vector128<int> r2, ref Vector128<int> r3)
    {
        Vector128<int> t0 = Sse2.UnpackLow(r0, r1);
        Vector128<int> t1 = Sse2.UnpackLow(r2, r3);
        Vector128<int> t2 = Sse2.UnpackHigh(r0, r1);
        Vector128<int> t3 = Sse2.UnpackHigh(r2, r3);
        r0 = Sse2.UnpackLow(t0.AsInt64(), t1.AsInt64()).AsInt32();
        r1 = Sse2.UnpackHigh(t0.AsInt64(), t1.AsInt64()).AsInt32();
        r2 = Sse2.UnpackLow(t2.AsInt64(), t3.AsInt64()).AsInt32();
        r3 = Sse2.UnpackHigh(t2.AsInt64(), t3.AsInt64()).AsInt32();
    }

    private static Vector128<int> Row(ReadOnlySpan<short> input, int row) =>
        Vector128.WidenLower(Vector128.CreateScalarUnsafe(BinaryPrimitives.ReadUInt64LittleEndian(System.Runtime.InteropServices.MemoryMarshal.AsBytes(input.Slice(row * 4, 4)))).AsInt16());

    private static void AddVector(ReadOnlySpan<short> input, byte[] dst, int off, int stride)
    {
        // First pass: along the columns, lane i = column i.
        Vector128<int> in0 = Row(input, 0);
        Vector128<int> in1 = Row(input, 1);
        Vector128<int> in2 = Row(input, 2);
        Vector128<int> in3 = Row(input, 3);
        Vector128<int> a = in0 + in2;
        Vector128<int> b = in0 - in2;
        Vector128<int> c = Mul2(in1) - Mul1(in3);
        Vector128<int> d = Mul1(in1) + Mul2(in3);
        Vector128<int> t0 = a + d;
        Vector128<int> t1 = b + c;
        Vector128<int> t2 = b - c;
        Vector128<int> t3 = a - d;

        // Second pass: along the rows of the transposed intermediate; lane i = output row i.
        Transpose(ref t0, ref t1, ref t2, ref t3);
        Vector128<int> dc = t0 + Vector128.Create(4);
        a = dc + t2;
        b = dc - t2;
        c = Mul2(t1) - Mul1(t3);
        d = Mul1(t1) + Mul2(t3);
        Vector128<int> w0 = Vector128.ShiftRightArithmetic(a + d, 3);
        Vector128<int> w1 = Vector128.ShiftRightArithmetic(b + c, 3);
        Vector128<int> w2 = Vector128.ShiftRightArithmetic(b - c, 3);
        Vector128<int> w3 = Vector128.ShiftRightArithmetic(a - d, 3);
        Transpose(ref w0, ref w1, ref w2, ref w3);

        Span<byte> s = dst;
        Vector128<int> zero = Vector128<int>.Zero;
        Vector128<int> max = Vector128.Create(255);
        for (int r = 0; r < 4; r++)
        {
            Vector128<int> w = r == 0 ? w0 : r == 1 ? w1 : r == 2 ? w2 : w3;
            int o = off + (r * stride);
            uint px = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o, 4));
            Vector128<int> p = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(px).AsByte())).AsInt32();
            Vector128<int> sum = Vector128.Min(Vector128.Max(p + w, zero), max);
            Vector128<ushort> n16 = Vector128.Narrow(sum.AsUInt32(), sum.AsUInt32());
            Vector128<byte> n8 = Vector128.Narrow(n16, n16);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o, 4), n8.AsUInt32().ToScalar());
        }
    }

    /// <summary>The scalar reference.</summary>
    internal static void AddScalar(ReadOnlySpan<short> input, byte[] dst, int off, int stride)
    {
        static int M1(int a) => ((a * 20091) >> 16) + a;
        static int M2(int a) => (a * 35468) >> 16;
        static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        Span<int> c = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a = input[i] + input[8 + i];
            int b = input[i] - input[8 + i];
            int cc = M2(input[4 + i]) - M1(input[12 + i]);
            int d = M1(input[4 + i]) + M2(input[12 + i]);
            c[(i * 4) + 0] = a + d;
            c[(i * 4) + 1] = b + cc;
            c[(i * 4) + 2] = b - cc;
            c[(i * 4) + 3] = a - d;
        }

        for (int i = 0; i < 4; i++)
        {
            int dc = c[i] + 4;
            int a = dc + c[8 + i];
            int b = dc - c[8 + i];
            int cc = M2(c[4 + i]) - M1(c[12 + i]);
            int d = M1(c[4 + i]) + M2(c[12 + i]);
            int o = off + (i * stride);
            dst[o] = Clip(dst[o] + ((a + d) >> 3));
            dst[o + 1] = Clip(dst[o + 1] + ((b + cc) >> 3));
            dst[o + 2] = Clip(dst[o + 2] + ((b - cc) >> 3));
            dst[o + 3] = Clip(dst[o + 3] + ((a - d) >> 3));
        }
    }
}
