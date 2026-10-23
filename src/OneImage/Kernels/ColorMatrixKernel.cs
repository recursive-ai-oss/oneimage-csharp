using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// The 8-bit row kernel of the color-matrix op. One pixel is one <see cref="Vector128{T}"/> of (R, G, B, A): the output is
/// <c>(((R*row1 + G*row2) + B*row3) + A*row4) + row5</c>, a multiply and an add per step in that order (never fused), which is exactly
/// the arithmetic of the float-plane path, so both give the same bytes. Rounding and clamping are those of <see cref="ToneKernel.StoreU8"/>.
/// The caller validates that the buffers hold <c>width</c> pixels of the stated channel counts.
/// </summary>
internal static class ColorMatrixKernel
{
    private static readonly float[] s_unit = BuildUnit();

    /// <summary>
    /// Transforms interleaved 8-bit straight pixels. <paramref name="inChannels"/> and <paramref name="outChannels"/> are 3 (RGB) or 4 (RGBA).
    /// With <paramref name="computeAlpha"/> the output alpha comes from the matrix (an input without alpha counts as 1); otherwise a 4-channel
    /// output copies the input alpha byte, which is what the matrix would give for an identity alpha column.
    /// </summary>
    public static void Apply8(ReadOnlySpan<byte> src, Span<byte> dst, int width, int inChannels, int outChannels, bool computeAlpha, ReadOnlySpan<float> matrix)
    {
        if (inChannels is not (3 or 4) || outChannels is not (3 or 4) || matrix.Length < 20 || width < 0
            || src.Length < width * inChannels || dst.Length < width * outChannels || (outChannels == 4 && !computeAlpha && inChannels != 4))
        {
            throw new ArgumentException("Invalid color matrix arguments.");
        }

        Vector128<float> row1 = Vector128.Create(matrix[0], matrix[1], matrix[2], matrix[3]);
        Vector128<float> row2 = Vector128.Create(matrix[4], matrix[5], matrix[6], matrix[7]);
        Vector128<float> row3 = Vector128.Create(matrix[8], matrix[9], matrix[10], matrix[11]);
        Vector128<float> row4 = Vector128.Create(matrix[12], matrix[13], matrix[14], matrix[15]);
        Vector128<float> row5 = Vector128.Create(matrix[16], matrix[17], matrix[18], matrix[19]);
        Vector128<float> v255 = Vector128.Create(255f);
        Vector128<float> half = Vector128.Create(0.5f);
        Vector128<float> zero = Vector128<float>.Zero;
        ref float unit = ref MemoryMarshal.GetArrayDataReference(s_unit);
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte d = ref MemoryMarshal.GetReference(dst);
        int start = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            start = Wide8(ref s, ref d, width, inChannels, outChannels, computeAlpha, matrix);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            start = Wide4(ref s, ref d, width, inChannels, outChannels, computeAlpha, matrix);
        }

        for (int x = start; x < width; x++)
        {
            int si = x * inChannels;
            Vector128<float> acc = row1 * Vector128.Create(Unsafe.Add(ref unit, Unsafe.Add(ref s, si)));
            acc += row2 * Vector128.Create(Unsafe.Add(ref unit, Unsafe.Add(ref s, si + 1)));
            acc += row3 * Vector128.Create(Unsafe.Add(ref unit, Unsafe.Add(ref s, si + 2)));
            acc += row4 * Vector128.Create(inChannels == 4 ? Unsafe.Add(ref unit, Unsafe.Add(ref s, si + 3)) : 1f);
            acc += row5;
            Vector128<float> v = Vector128.Min(Vector128.Max((acc * v255) + half, zero), v255);
            Vector128<int> iv = Vector128.ConvertToInt32(v);
            uint packed = Vector128.Narrow(Vector128.Narrow(iv.AsUInt32(), iv.AsUInt32()), Vector128<ushort>.Zero).AsUInt32().ToScalar();
            int di = x * outChannels;
            if (outChannels == 3)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, di), (ushort)packed);
                Unsafe.Add(ref d, di + 2) = (byte)(packed >> 16);
            }
            else
            {
                if (!computeAlpha)
                {
                    packed = (packed & 0x00FFFFFFu) | ((uint)Unsafe.Add(ref s, si + 3) << 24);
                }

                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, di), packed);
            }
        }
    }

    /// <summary>Eight pixels per step for any mix of 3- and 4-channel input and output; returns how many pixels it did (a multiple of eight).</summary>
    private static int Wide8(ref byte s, ref byte d, int width, int inChannels, int outChannels, bool computeAlpha, ReadOnlySpan<float> m)
    {
        // Terms with a zero weight add an exact zero, so skipping them changes no value (only the sign of a zero, which rounds away).
        bool hasA = inChannels == 4;
        bool useA = !hasA || m[12] != 0f || m[13] != 0f || m[14] != 0f || (computeAlpha && m[15] != 0f);
        bool useG = m[4] != 0f || m[5] != 0f || m[6] != 0f || (computeAlpha && m[7] != 0f);
        bool useB = m[8] != 0f || m[9] != 0f || m[10] != 0f || (computeAlpha && m[11] != 0f);
        Vector256<float> v255 = Vector256.Create(255f);
        Vector256<float> half = Vector256.Create(0.5f);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<float> one = Vector256.Create(1f);

        // The 3-byte layouts load and store 16 bytes at a time (the second half 12 bytes in), so they stop two pixels early to stay inside the row.
        bool packed = inChannels == 3 || outChannels == 3;
        int limit = packed ? width - 10 : width - 8;
        Vector128<byte> inMask = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
        Vector128<byte> outMask = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
        int x = 0;
        for (; x <= limit; x += 8)
        {
            Vector256<uint> p;
            if (inChannels == 4)
            {
                p = Vector256.LoadUnsafe(ref Unsafe.As<byte, uint>(ref s), (nuint)x);
            }
            else
            {
                Vector128<byte> v0 = Vector128.LoadUnsafe(ref s, (nuint)(x * 3));
                Vector128<byte> v1 = Vector128.LoadUnsafe(ref s, (nuint)((x * 3) + 12));
                p = Vector256.Create(Vector128.Shuffle(v0, inMask).AsUInt32(), Vector128.Shuffle(v1, inMask).AsUInt32());
            }

            Vector256<float> r = Vector256.ConvertToSingle((p & mask).AsInt32()) / v255;
            Vector256<float> g = useG ? Vector256.ConvertToSingle(((p >> 8) & mask).AsInt32()) / v255 : default;
            Vector256<float> b = useB ? Vector256.ConvertToSingle(((p >> 16) & mask).AsInt32()) / v255 : default;
            Vector256<float> a = !hasA ? one : useA ? Vector256.ConvertToSingle((p >> 24).AsInt32()) / v255 : default;
            Vector256<uint> o = Channel(r, g, b, a, m[0], m[4], m[8], m[12], m[16], v255, half)
                | (Channel(r, g, b, a, m[1], m[5], m[9], m[13], m[17], v255, half) << 8)
                | (Channel(r, g, b, a, m[2], m[6], m[10], m[14], m[18], v255, half) << 16);
            if (outChannels == 4)
            {
                o |= computeAlpha ? Channel(r, g, b, a, m[3], m[7], m[11], m[15], m[19], v255, half) << 24 : p & Vector256.Create(0xFF000000u);
                Vector256.StoreUnsafe(o, ref Unsafe.As<byte, uint>(ref d), (nuint)x);
            }
            else
            {
                Vector128.Shuffle(o.GetLower().AsByte(), outMask).StoreUnsafe(ref d, (nuint)(x * 3));
                Vector128.Shuffle(o.GetUpper().AsByte(), outMask).StoreUnsafe(ref d, (nuint)((x * 3) + 12));
            }
        }

        return x;
    }

    /// <summary>The same as <see cref="Wide8"/> with four pixels per step, for hardware with 128-bit vectors only; returns how many pixels it did (a multiple of four).</summary>
    private static int Wide4(ref byte s, ref byte d, int width, int inChannels, int outChannels, bool computeAlpha, ReadOnlySpan<float> m)
    {
        bool hasA = inChannels == 4;
        bool useA = !hasA || m[12] != 0f || m[13] != 0f || m[14] != 0f || (computeAlpha && m[15] != 0f);
        bool useG = m[4] != 0f || m[5] != 0f || m[6] != 0f || (computeAlpha && m[7] != 0f);
        bool useB = m[8] != 0f || m[9] != 0f || m[10] != 0f || (computeAlpha && m[11] != 0f);
        Vector128<float> v255 = Vector128.Create(255f);
        Vector128<float> half = Vector128.Create(0.5f);
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<float> one = Vector128.Create(1f);

        // The 3-byte layouts load and store 16 bytes for four pixels, so they stop two pixels early to stay inside the row.
        int reach = inChannels == 3 || outChannels == 3 ? 6 : 4;
        Vector128<byte> inMask = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
        Vector128<byte> outMask = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
        int x = 0;
        for (; x + reach <= width; x += 4)
        {
            Vector128<uint> p = inChannels == 4
                ? Vector128.LoadUnsafe(ref Unsafe.As<byte, uint>(ref s), (nuint)x)
                : Vector128.Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(x * 3)), inMask).AsUInt32();
            Vector128<float> r = Vector128.ConvertToSingle((p & mask).AsInt32()) / v255;
            Vector128<float> g = useG ? Vector128.ConvertToSingle(((p >> 8) & mask).AsInt32()) / v255 : default;
            Vector128<float> b = useB ? Vector128.ConvertToSingle(((p >> 16) & mask).AsInt32()) / v255 : default;
            Vector128<float> a = !hasA ? one : useA ? Vector128.ConvertToSingle((p >> 24).AsInt32()) / v255 : default;
            Vector128<uint> o = Channel(r, g, b, a, m[0], m[4], m[8], m[12], m[16], v255, half)
                | (Channel(r, g, b, a, m[1], m[5], m[9], m[13], m[17], v255, half) << 8)
                | (Channel(r, g, b, a, m[2], m[6], m[10], m[14], m[18], v255, half) << 16);
            if (outChannels == 4)
            {
                o |= computeAlpha ? Channel(r, g, b, a, m[3], m[7], m[11], m[15], m[19], v255, half) << 24 : p & Vector128.Create(0xFF000000u);
                Vector128.StoreUnsafe(o, ref Unsafe.As<byte, uint>(ref d), (nuint)x);
            }
            else
            {
                Vector128.Shuffle(o.AsByte(), outMask).StoreUnsafe(ref d, (nuint)(x * 3));
            }
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Channel(Vector256<float> r, Vector256<float> g, Vector256<float> b, Vector256<float> a, float w1, float w2, float w3, float w4, float offset, Vector256<float> v255, Vector256<float> half)
    {
        // Same order as the float-plane path: ((((R*w1 + G*w2) + B*w3) + A*w4) + offset).
        Vector256<float> acc = r * Vector256.Create(w1);
        if (w2 != 0f)
        {
            acc += g * Vector256.Create(w2);
        }

        if (w3 != 0f)
        {
            acc += b * Vector256.Create(w3);
        }

        if (w4 != 0f)
        {
            acc += a * Vector256.Create(w4);
        }

        acc += Vector256.Create(offset);
        Vector256<float> v = Vector256.Min(Vector256.Max((acc * v255) + half, Vector256<float>.Zero), v255);
        return Vector256.ConvertToInt32Native(v).AsUInt32() & Vector256.Create(0xFFu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Channel(Vector128<float> r, Vector128<float> g, Vector128<float> b, Vector128<float> a, float w1, float w2, float w3, float w4, float offset, Vector128<float> v255, Vector128<float> half)
    {
        // Same order as the float-plane path: ((((R*w1 + G*w2) + B*w3) + A*w4) + offset).
        Vector128<float> acc = r * Vector128.Create(w1);
        if (w2 != 0f)
        {
            acc += g * Vector128.Create(w2);
        }

        if (w3 != 0f)
        {
            acc += b * Vector128.Create(w3);
        }

        if (w4 != 0f)
        {
            acc += a * Vector128.Create(w4);
        }

        acc += Vector128.Create(offset);
        Vector128<float> v = Vector128.Min(Vector128.Max((acc * v255) + half, Vector128<float>.Zero), v255);
        return Vector128.ConvertToInt32Native(v).AsUInt32() & Vector128.Create(0xFFu);
    }

    private static float[] BuildUnit()
    {
        // Same conversion as ConvertKernel.Unpack, so the kernel sees the floats the plane path sees.
        var t = new float[256];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = i / 255f;
        }

        return t;
    }
}
