using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// Sum of squared byte differences over a square block, the distortion measure of the lossy WebP encoder. The vector path takes
/// the absolute difference of 16 bytes at a time, widens it to 16 bits and squares and adds neighbors in 32-bit lanes. Sums of
/// integers are exact, so it matches <see cref="Scalar"/> bit for bit.
/// </summary>
internal static class WebPSseKernel
{
    /// <summary>Returns the sum of squared differences of two <paramref name="size"/> by <paramref name="size"/> blocks (size 4, 8 or 16).</summary>
    public static long Sse(ReadOnlySpan<byte> a, int ao, int astride, ReadOnlySpan<byte> b, int bo, int bstride, int size)
    {
        if (size is not (4 or 8 or 16) || ao < 0 || bo < 0 || astride < size || bstride < size
            || (long)ao + ((long)(size - 1) * astride) + size > a.Length || (long)bo + ((long)(size - 1) * bstride) + size > b.Length)
        {
            throw new ArgumentException("Invalid block.");
        }

        if (!Vector128.IsHardwareAccelerated)
        {
            return Scalar(a, ao, astride, b, bo, bstride, size);
        }

        Vector128<int> acc = Vector128<int>.Zero;
        if (size == 16)
        {
            for (int j = 0; j < 16; j++)
            {
                acc += Square(Vector128.Create(a.Slice(ao + (j * astride), 16)), Vector128.Create(b.Slice(bo + (j * bstride), 16)));
            }
        }
        else if (size == 8)
        {
            for (int j = 0; j < 8; j += 2)
            {
                Vector128<byte> va = Vector128.Create(BinaryPrimitives.ReadUInt64LittleEndian(a.Slice(ao + (j * astride), 8)), BinaryPrimitives.ReadUInt64LittleEndian(a.Slice(ao + ((j + 1) * astride), 8))).AsByte();
                Vector128<byte> vb = Vector128.Create(BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(bo + (j * bstride), 8)), BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(bo + ((j + 1) * bstride), 8))).AsByte();
                acc += Square(va, vb);
            }
        }
        else
        {
            Vector128<byte> va = Vector128.Create(
                BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(ao, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(ao + astride, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(ao + (2 * astride), 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(ao + (3 * astride), 4))).AsByte();
            Vector128<byte> vb = Vector128.Create(
                BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(bo, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(bo + bstride, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(bo + (2 * bstride), 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(bo + (3 * bstride), 4))).AsByte();
            acc = Square(va, vb);
        }

        return Vector128.Sum(acc);
    }

    // Squares the absolute differences of 16 byte pairs and adds neighbors: four 32-bit partial sums.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Square(Vector128<byte> a, Vector128<byte> b)
    {
        Vector128<byte> d = Vector128.Max(a, b) - Vector128.Min(a, b);
        Vector128<short> lo = Vector128.WidenLower(d).AsInt16();
        Vector128<short> hi = Vector128.WidenUpper(d).AsInt16();
        if (Sse2.IsSupported)
        {
            return Sse2.MultiplyAddAdjacent(lo, lo) + Sse2.MultiplyAddAdjacent(hi, hi);
        }

        // Without pmaddwd: widen to 32 bits and square (values stay below 65026, so the products fit).
        (Vector128<int> l0, Vector128<int> l1) = Vector128.Widen(lo);
        (Vector128<int> h0, Vector128<int> h1) = Vector128.Widen(hi);
        return (l0 * l0) + (l1 * l1) + (h0 * h0) + (h1 * h1);
    }

    /// <summary>The reference implementation.</summary>
    public static long Scalar(ReadOnlySpan<byte> a, int ao, int astride, ReadOnlySpan<byte> b, int bo, int bstride, int size)
    {
        long sum = 0;
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int d = a[ao + (j * astride) + i] - b[bo + (j * bstride) + i];
                sum += d * d;
            }
        }

        return sum;
    }
}
