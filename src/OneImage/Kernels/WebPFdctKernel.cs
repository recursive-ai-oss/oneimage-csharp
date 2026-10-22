using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// The VP8 forward 4x4 DCT of (source - prediction), with the exact integer steps libwebp uses. The vector path transforms the
/// four rows (then the four columns) together in 32-bit lanes and does the same arithmetic as <see cref="Scalar"/>, so both
/// give identical coefficients.
/// </summary>
internal static class WebPFdctKernel
{
    /// <summary>Writes the 16 raster-order coefficients of the transform of source minus prediction.</summary>
    public static void Forward(ReadOnlySpan<byte> src, int srcOff, int srcStride, ReadOnlySpan<byte> pred, int predOff, int predStride, Span<short> output)
    {
        if (output.Length < 16 || srcOff < 0 || predOff < 0 || srcStride < 4 || predStride < 4
            || (long)srcOff + (3L * srcStride) + 4 > src.Length || (long)predOff + (3L * predStride) + 4 > pred.Length)
        {
            throw new ArgumentException("Invalid block.");
        }

        if (Sse2.IsSupported)
        {
            ForwardVector(src, srcOff, srcStride, pred, predOff, predStride, output);
        }
        else
        {
            Scalar(src, srcOff, srcStride, pred, predOff, predStride, output);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Diff(ReadOnlySpan<byte> src, int so, ReadOnlySpan<byte> pred, int po)
    {
        Vector128<int> s = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(so, 4))).AsByte())).AsInt32();
        Vector128<int> p = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(BinaryPrimitives.ReadUInt32LittleEndian(pred.Slice(po, 4))).AsByte())).AsInt32();
        return s - p;
    }

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

    private static void ForwardVector(ReadOnlySpan<byte> src, int so, int ss, ReadOnlySpan<byte> pred, int po, int ps, Span<short> output)
    {
        // Rows of differences, then transposed so that lane i of vector j holds column j of row i.
        Vector128<int> c0 = Diff(src, so, pred, po);
        Vector128<int> c1 = Diff(src, so + ss, pred, po + ps);
        Vector128<int> c2 = Diff(src, so + (2 * ss), pred, po + (2 * ps));
        Vector128<int> c3 = Diff(src, so + (3 * ss), pred, po + (3 * ps));
        Transpose(ref c0, ref c1, ref c2, ref c3);

        // First pass (per row): c0..c3 are the four columns.
        Vector128<int> a0 = c0 + c3;
        Vector128<int> a1 = c1 + c2;
        Vector128<int> a2 = c1 - c2;
        Vector128<int> a3 = c0 - c3;
        Vector128<int> t0 = (a0 + a1) * 8;
        Vector128<int> t1 = Vector128.ShiftRightArithmetic((a2 * 2217) + (a3 * 5352) + Vector128.Create(1812), 9);
        Vector128<int> t2 = (a0 - a1) * 8;
        Vector128<int> t3 = Vector128.ShiftRightArithmetic((a3 * 2217) - (a2 * 5352) + Vector128.Create(937), 9);

        // Second pass (per column): lane i of the transposed vectors is column i.
        Transpose(ref t0, ref t1, ref t2, ref t3);
        a0 = t0 + t3;
        a1 = t1 + t2;
        a2 = t1 - t2;
        a3 = t0 - t3;
        Vector128<int> seven = Vector128.Create(7);
        Vector128<int> o0 = Vector128.ShiftRightArithmetic(a0 + a1 + seven, 4);

        // One more when a3 is nonzero: Equals gives -1 for zero lanes.
        Vector128<int> o1 = Vector128.ShiftRightArithmetic((a2 * 2217) + (a3 * 5352) + Vector128.Create(12000), 16) + Vector128.Create(1) + Vector128.Equals(a3, Vector128<int>.Zero);
        Vector128<int> o2 = Vector128.ShiftRightArithmetic(a0 - a1 + seven, 4);
        Vector128<int> o3 = Vector128.ShiftRightArithmetic((a3 * 2217) - (a2 * 5352) + Vector128.Create(51000), 16);
        Vector128.Narrow(o0, o1).CopyTo(output);
        Vector128.Narrow(o2, o3).CopyTo(output[8..]);
    }

    /// <summary>The reference implementation.</summary>
    public static void Scalar(ReadOnlySpan<byte> src, int srcOff, int srcStride, ReadOnlySpan<byte> pred, int predOff, int predStride, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int so = srcOff + (i * srcStride);
            int po = predOff + (i * predStride);
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
}
