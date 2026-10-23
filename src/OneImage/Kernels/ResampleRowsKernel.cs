using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// Single-channel row kernels of the resampler. A one-channel horizontal pass is a chain of dependent adds per output, so
/// it runs eight rows at once: the rows are interleaved (<c>block[x * 8 + row]</c>), every tap is then one 8-lane
/// multiply and add, and the eight outputs of a lane are the same sum, in the same order, that the one-row loop of
/// <see cref="ResampleKernel.HorizontalFilter"/> computes. Results are bit-identical to that loop; no fused multiply-add.
/// </summary>
internal static class ResampleRowsKernel
{
    /// <summary>Rows per block.</summary>
    public const int Rows = 8;

    /// <summary>True when <see cref="HorizontalFilterRows"/> has a vector path on this machine (it is correct everywhere).</summary>
    public static bool IsAccelerated => Avx.IsSupported;

    /// <summary>True when a layout is a single float per pixel, the case the block kernels serve.</summary>
    public static bool Handles(in ResampleLayout l) => l.Stride == 1;

    /// <summary>Copies a row into lane <paramref name="lane"/> of the interleaved block.</summary>
    public static void ScatterRow(ReadOnlySpan<float> row, Span<float> block, int lane)
    {
        if ((uint)lane >= Rows || block.Length < row.Length * Rows)
        {
            throw new ArgumentException("Block is too small.");
        }

        ref float b = ref MemoryMarshal.GetReference(block);
        ref float r = ref MemoryMarshal.GetReference(row);
        for (int x = 0; x < row.Length; x++)
        {
            Unsafe.Add(ref b, (x * Rows) + lane) = Unsafe.Add(ref r, x);
        }
    }

    /// <summary>
    /// <see cref="ResampleKernel.UnpackRow"/> for eight consecutive rows of 8-bit gray into the interleaved block. Eight pixels of each
    /// row are looked up and then transposed in registers, so the block is written sequentially.
    /// </summary>
    /// <param name="src">The first of eight rows; later rows follow at <paramref name="stride"/> bytes.</param>
    /// <param name="stride">Bytes between rows.</param>
    /// <param name="block">Receives <c>width * 8</c> floats.</param>
    /// <param name="width">Pixels per row.</param>
    /// <param name="linear">Decode sRGB to linear light.</param>
    public static unsafe void UnpackGray8Block(ReadOnlySpan<byte> src, int stride, Span<float> block, int width, bool linear)
    {
        if (stride < width || block.Length < width * Rows || ((long)(Rows - 1) * stride) + width > src.Length)
        {
            throw new ArgumentException("Row buffers are too small.");
        }

        float[] lut = linear ? SrgbTables.ToLinear8 : SrgbTables.Identity8;
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref float b = ref MemoryMarshal.GetReference(block);
        ref float t = ref MemoryMarshal.GetArrayDataReference(lut);
        int x = 0;
        if (Avx2.IsSupported)
        {
            fixed (float* table = lut)
            {
                for (; x + 8 <= width; x += 8)
                {
                    Vector256<float> r0 = Look(ref s, table, x);
                    Vector256<float> r1 = Look(ref s, table, stride + x);
                    Vector256<float> r2 = Look(ref s, table, (2 * stride) + x);
                    Vector256<float> r3 = Look(ref s, table, (3 * stride) + x);
                    Vector256<float> r4 = Look(ref s, table, (4 * stride) + x);
                    Vector256<float> r5 = Look(ref s, table, (5 * stride) + x);
                    Vector256<float> r6 = Look(ref s, table, (6 * stride) + x);
                    Vector256<float> r7 = Look(ref s, table, (7 * stride) + x);
                    Transpose(r0, r1, r2, r3, r4, r5, r6, r7, ref b, (nuint)(x * Rows), 8);
                }
            }
        }

        for (; x < width; x++)
        {
            for (int r = 0; r < Rows; r++)
            {
                Unsafe.Add(ref b, (x * Rows) + r) = Unsafe.Add(ref t, Unsafe.Add(ref s, (r * stride) + x));
            }
        }
    }

    // Eight table lookups for eight consecutive bytes (one gather).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<float> Look(ref byte s, float* table, int index)
    {
        ulong eight = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, index));
        return Avx2.GatherVector256(table, Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(eight).AsByte()), 4);
    }

    /// <summary>
    /// Transposes eight vectors of eight floats (row i is <c>ai</c>) and stores column j at <c>d[at + j * step]</c>: pure data
    /// movement, so values are unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(Vector256<float> a0, Vector256<float> a1, Vector256<float> a2, Vector256<float> a3, Vector256<float> a4, Vector256<float> a5, Vector256<float> a6, Vector256<float> a7, ref float d, nuint at, nuint step)
    {
        Vector256<float> t0 = Avx.UnpackLow(a0, a1);
        Vector256<float> t1 = Avx.UnpackHigh(a0, a1);
        Vector256<float> t2 = Avx.UnpackLow(a2, a3);
        Vector256<float> t3 = Avx.UnpackHigh(a2, a3);
        Vector256<float> t4 = Avx.UnpackLow(a4, a5);
        Vector256<float> t5 = Avx.UnpackHigh(a4, a5);
        Vector256<float> t6 = Avx.UnpackLow(a6, a7);
        Vector256<float> t7 = Avx.UnpackHigh(a6, a7);
        Vector256<float> u0 = Avx.Shuffle(t0, t2, 0x44);
        Vector256<float> u1 = Avx.Shuffle(t0, t2, 0xEE);
        Vector256<float> u2 = Avx.Shuffle(t1, t3, 0x44);
        Vector256<float> u3 = Avx.Shuffle(t1, t3, 0xEE);
        Vector256<float> u4 = Avx.Shuffle(t4, t6, 0x44);
        Vector256<float> u5 = Avx.Shuffle(t4, t6, 0xEE);
        Vector256<float> u6 = Avx.Shuffle(t5, t7, 0x44);
        Vector256<float> u7 = Avx.Shuffle(t5, t7, 0xEE);
        Avx.Permute2x128(u0, u4, 0x20).StoreUnsafe(ref d, at);
        Avx.Permute2x128(u1, u5, 0x20).StoreUnsafe(ref d, at + step);
        Avx.Permute2x128(u2, u6, 0x20).StoreUnsafe(ref d, at + (2 * step));
        Avx.Permute2x128(u3, u7, 0x20).StoreUnsafe(ref d, at + (3 * step));
        Avx.Permute2x128(u0, u4, 0x31).StoreUnsafe(ref d, at + (4 * step));
        Avx.Permute2x128(u1, u5, 0x31).StoreUnsafe(ref d, at + (5 * step));
        Avx.Permute2x128(u2, u6, 0x31).StoreUnsafe(ref d, at + (6 * step));
        Avx.Permute2x128(u3, u7, 0x31).StoreUnsafe(ref d, at + (7 * step));
    }

    /// <summary>8-bit gray to working floats: a table lookup per pixel (<see cref="ResampleKernel.UnpackRow"/> for one channel).</summary>
    public static void UnpackGray8(ReadOnlySpan<byte> src, Span<float> dst, int width, bool linear)
    {
        if (src.Length < width || dst.Length < width)
        {
            throw new ArgumentException("Row buffers are too small.");
        }

        float[] lut = linear ? SrgbTables.ToLinear8 : SrgbTables.Identity8;
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref float d = ref MemoryMarshal.GetReference(dst);
        ref float t = ref MemoryMarshal.GetArrayDataReference(lut);
        int x = 0;
        for (; x + 4 <= width; x += 4)
        {
            float v0 = Unsafe.Add(ref t, Unsafe.Add(ref s, x));
            float v1 = Unsafe.Add(ref t, Unsafe.Add(ref s, x + 1));
            float v2 = Unsafe.Add(ref t, Unsafe.Add(ref s, x + 2));
            float v3 = Unsafe.Add(ref t, Unsafe.Add(ref s, x + 3));
            Unsafe.Add(ref d, x) = v0;
            Unsafe.Add(ref d, x + 1) = v1;
            Unsafe.Add(ref d, x + 2) = v2;
            Unsafe.Add(ref d, x + 3) = v3;
        }

        for (; x < width; x++)
        {
            Unsafe.Add(ref d, x) = Unsafe.Add(ref t, Unsafe.Add(ref s, x));
        }
    }

    /// <summary>
    /// Working floats to 8-bit gray: round half up, clamp, and for linear light encode through the 65536-entry table
    /// (<see cref="ResampleKernel.PackRow"/> for one channel; opaque, so there is no alpha division).
    /// </summary>
    public static void PackGray8(ReadOnlySpan<float> src, Span<byte> dst, int width, bool linear)
    {
        if (src.Length < width || dst.Length < width)
        {
            throw new ArgumentException("Row buffers are too small.");
        }

        byte[] enc = SrgbTables.FromLinear8;
        ref float s = ref MemoryMarshal.GetReference(src);
        ref byte d = ref MemoryMarshal.GetReference(dst);
        ref byte e = ref MemoryMarshal.GetArrayDataReference(enc);
        float scale = linear ? 65535f : 255f;
        int max = linear ? 65535 : 255;
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vScale = Vector256.Create(scale);
            var vHalf = Vector256.Create(0.5f);
            var vMax = Vector256.Create(max);
            Span<int> idx = stackalloc int[8];
            for (; x + 8 <= width; x += 8)
            {
                Vector256<int> i = Vector256.Min(Vector256.Max(Vector256.ConvertToInt32((Vector256.LoadUnsafe(ref s, (nuint)x) * vScale) + vHalf), Vector256<int>.Zero), vMax);
                i.CopyTo(idx);
                if (linear)
                {
                    Unsafe.Add(ref d, x) = Unsafe.Add(ref e, idx[0]);
                    Unsafe.Add(ref d, x + 1) = Unsafe.Add(ref e, idx[1]);
                    Unsafe.Add(ref d, x + 2) = Unsafe.Add(ref e, idx[2]);
                    Unsafe.Add(ref d, x + 3) = Unsafe.Add(ref e, idx[3]);
                    Unsafe.Add(ref d, x + 4) = Unsafe.Add(ref e, idx[4]);
                    Unsafe.Add(ref d, x + 5) = Unsafe.Add(ref e, idx[5]);
                    Unsafe.Add(ref d, x + 6) = Unsafe.Add(ref e, idx[6]);
                    Unsafe.Add(ref d, x + 7) = Unsafe.Add(ref e, idx[7]);
                }
                else
                {
                    for (int k = 0; k < 8; k++)
                    {
                        Unsafe.Add(ref d, x + k) = (byte)idx[k];
                    }
                }
            }
        }

        for (; x < width; x++)
        {
            int code = Math.Clamp((int)((Unsafe.Add(ref s, x) * scale) + 0.5f), 0, max);
            Unsafe.Add(ref d, x) = linear ? Unsafe.Add(ref e, code) : (byte)code;
        }
    }

    /// <summary>
    /// Horizontal pass over eight interleaved rows: <c>dst[r * dstStride + i]</c> is the weighted sum of
    /// <c>count[i]</c> block pixels starting at <c>start[i]</c> for lane r, taps in ascending order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void HorizontalFilterRows(
        ReadOnlySpan<float> block,
        Span<float> dst,
        int dstStride,
        ReadOnlySpan<int> start,
        ReadOnlySpan<int> count,
        ReadOnlySpan<int> offset,
        ReadOnlySpan<float> weights)
    {
        int n = start.Length;
        if (count.Length < n || offset.Length < n || dstStride < n || (n > 0 && dst.Length < ((Rows - 1) * (long)dstStride) + n))
        {
            throw new ArgumentException("Output buffers do not match the weight table.");
        }

        for (int i = 0; i < n; i++)
        {
            if (start[i] < 0 || count[i] <= 0 || ((long)(start[i] + count[i]) * Rows) > block.Length || (long)offset[i] + count[i] > weights.Length)
            {
                throw new ArgumentException("Weight table reads outside the source row.");
            }
        }

        if (!Avx.IsSupported)
        {
            HorizontalFilterRowsReference(block, dst, dstStride, start, count, offset, weights);
            return;
        }

        ref float s0 = ref MemoryMarshal.GetReference(block);
        ref float w0 = ref MemoryMarshal.GetReference(weights);
        ref float d0 = ref MemoryMarshal.GetReference(dst);
        int i0 = 0;
        for (; i0 + 8 <= n; i0 += 8)
        {
            // Eight outputs side by side, each its own chain of adds, so the add latency is hidden.
            int f0 = start[i0] * Rows;
            int f1 = start[i0 + 1] * Rows;
            int f2 = start[i0 + 2] * Rows;
            int f3 = start[i0 + 3] * Rows;
            int f4 = start[i0 + 4] * Rows;
            int f5 = start[i0 + 5] * Rows;
            int f6 = start[i0 + 6] * Rows;
            int f7 = start[i0 + 7] * Rows;
            int k0 = count[i0];
            int k1 = count[i0 + 1];
            int k2 = count[i0 + 2];
            int k3 = count[i0 + 3];
            int k4 = count[i0 + 4];
            int k5 = count[i0 + 5];
            int k6 = count[i0 + 6];
            int k7 = count[i0 + 7];
            ref float p0 = ref Unsafe.Add(ref w0, offset[i0]);
            ref float p1 = ref Unsafe.Add(ref w0, offset[i0 + 1]);
            ref float p2 = ref Unsafe.Add(ref w0, offset[i0 + 2]);
            ref float p3 = ref Unsafe.Add(ref w0, offset[i0 + 3]);
            ref float p4 = ref Unsafe.Add(ref w0, offset[i0 + 4]);
            ref float p5 = ref Unsafe.Add(ref w0, offset[i0 + 5]);
            ref float p6 = ref Unsafe.Add(ref w0, offset[i0 + 6]);
            ref float p7 = ref Unsafe.Add(ref w0, offset[i0 + 7]);
            Vector256<float> a0 = Vector256.LoadUnsafe(ref s0, (nuint)f0) * p0;
            Vector256<float> a1 = Vector256.LoadUnsafe(ref s0, (nuint)f1) * p1;
            Vector256<float> a2 = Vector256.LoadUnsafe(ref s0, (nuint)f2) * p2;
            Vector256<float> a3 = Vector256.LoadUnsafe(ref s0, (nuint)f3) * p3;
            Vector256<float> a4 = Vector256.LoadUnsafe(ref s0, (nuint)f4) * p4;
            Vector256<float> a5 = Vector256.LoadUnsafe(ref s0, (nuint)f5) * p5;
            Vector256<float> a6 = Vector256.LoadUnsafe(ref s0, (nuint)f6) * p6;
            Vector256<float> a7 = Vector256.LoadUnsafe(ref s0, (nuint)f7) * p7;
            int common = Math.Min(Math.Min(Math.Min(k0, k1), Math.Min(k2, k3)), Math.Min(Math.Min(k4, k5), Math.Min(k6, k7)));
            int t = 1;
            for (; t < common; t++)
            {
                int o = t * Rows;
                a0 += Vector256.LoadUnsafe(ref s0, (nuint)(f0 + o)) * Unsafe.Add(ref p0, t);
                a1 += Vector256.LoadUnsafe(ref s0, (nuint)(f1 + o)) * Unsafe.Add(ref p1, t);
                a2 += Vector256.LoadUnsafe(ref s0, (nuint)(f2 + o)) * Unsafe.Add(ref p2, t);
                a3 += Vector256.LoadUnsafe(ref s0, (nuint)(f3 + o)) * Unsafe.Add(ref p3, t);
                a4 += Vector256.LoadUnsafe(ref s0, (nuint)(f4 + o)) * Unsafe.Add(ref p4, t);
                a5 += Vector256.LoadUnsafe(ref s0, (nuint)(f5 + o)) * Unsafe.Add(ref p5, t);
                a6 += Vector256.LoadUnsafe(ref s0, (nuint)(f6 + o)) * Unsafe.Add(ref p6, t);
                a7 += Vector256.LoadUnsafe(ref s0, (nuint)(f7 + o)) * Unsafe.Add(ref p7, t);
            }

            for (int u = t; u < k0; u++)
            {
                a0 += Vector256.LoadUnsafe(ref s0, (nuint)(f0 + (u * Rows))) * Unsafe.Add(ref p0, u);
            }

            for (int u = t; u < k1; u++)
            {
                a1 += Vector256.LoadUnsafe(ref s0, (nuint)(f1 + (u * Rows))) * Unsafe.Add(ref p1, u);
            }

            for (int u = t; u < k2; u++)
            {
                a2 += Vector256.LoadUnsafe(ref s0, (nuint)(f2 + (u * Rows))) * Unsafe.Add(ref p2, u);
            }

            for (int u = t; u < k3; u++)
            {
                a3 += Vector256.LoadUnsafe(ref s0, (nuint)(f3 + (u * Rows))) * Unsafe.Add(ref p3, u);
            }

            for (int u = t; u < k4; u++)
            {
                a4 += Vector256.LoadUnsafe(ref s0, (nuint)(f4 + (u * Rows))) * Unsafe.Add(ref p4, u);
            }

            for (int u = t; u < k5; u++)
            {
                a5 += Vector256.LoadUnsafe(ref s0, (nuint)(f5 + (u * Rows))) * Unsafe.Add(ref p5, u);
            }

            for (int u = t; u < k6; u++)
            {
                a6 += Vector256.LoadUnsafe(ref s0, (nuint)(f6 + (u * Rows))) * Unsafe.Add(ref p6, u);
            }

            for (int u = t; u < k7; u++)
            {
                a7 += Vector256.LoadUnsafe(ref s0, (nuint)(f7 + (u * Rows))) * Unsafe.Add(ref p7, u);
            }

            // 8x8 transpose: output i of lane r goes to row r. Pure data movement, so the values are unchanged.
            Transpose(a0, a1, a2, a3, a4, a5, a6, a7, ref d0, (nuint)i0, (nuint)dstStride);
        }

        for (; i0 < n; i0++)
        {
            int f = start[i0] * Rows;
            int taps = count[i0];
            ref float p = ref Unsafe.Add(ref w0, offset[i0]);
            Vector256<float> acc = Vector256.LoadUnsafe(ref s0, (nuint)f) * p;
            for (int t = 1; t < taps; t++)
            {
                acc += Vector256.LoadUnsafe(ref s0, (nuint)(f + (t * Rows))) * Unsafe.Add(ref p, t);
            }

            for (int r = 0; r < Rows; r++)
            {
                Unsafe.Add(ref d0, (r * dstStride) + i0) = acc.GetElement(r);
            }
        }
    }

    /// <summary>The plain loops <see cref="HorizontalFilterRows"/> must equal, and the path without AVX.</summary>
    public static void HorizontalFilterRowsReference(
        ReadOnlySpan<float> block,
        Span<float> dst,
        int dstStride,
        ReadOnlySpan<int> start,
        ReadOnlySpan<int> count,
        ReadOnlySpan<int> offset,
        ReadOnlySpan<float> weights)
    {
        for (int i = 0; i < start.Length; i++)
        {
            for (int r = 0; r < Rows; r++)
            {
                float acc = block[(start[i] * Rows) + r] * weights[offset[i]];
                for (int t = 1; t < count[i]; t++)
                {
                    acc += block[((start[i] + t) * Rows) + r] * weights[offset[i] + t];
                }

                dst[(r * dstStride) + i] = acc;
            }
        }
    }
}
