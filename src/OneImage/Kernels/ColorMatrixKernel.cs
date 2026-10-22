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
        if (inChannels == 4 && outChannels == 4 && Vector256.IsHardwareAccelerated)
        {
            start = Rgba8Wide(ref s, ref d, width, computeAlpha, matrix);
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

    /// <summary>RGBA to RGBA, eight pixels per step; returns how many pixels it did (a multiple of eight).</summary>
    private static int Rgba8Wide(ref byte s, ref byte d, int width, bool computeAlpha, ReadOnlySpan<float> m)
    {
        // Terms with a zero weight add an exact zero, so skipping them changes no value (only the sign of a zero, which rounds away).
        bool useA = m[12] != 0f || m[13] != 0f || m[14] != 0f || (computeAlpha && m[15] != 0f);
        bool useG = m[4] != 0f || m[5] != 0f || m[6] != 0f || (computeAlpha && m[7] != 0f);
        bool useB = m[8] != 0f || m[9] != 0f || m[10] != 0f || (computeAlpha && m[11] != 0f);
        Vector256<float> v255 = Vector256.Create(255f);
        Vector256<float> half = Vector256.Create(0.5f);
        Vector256<uint> mask = Vector256.Create(0xFFu);
        int n = width & ~7;
        for (int x = 0; x < n; x += 8)
        {
            Vector256<uint> p = Vector256.LoadUnsafe(ref Unsafe.As<byte, uint>(ref s), (nuint)x);
            Vector256<float> r = Vector256.ConvertToSingle((p & mask).AsInt32()) / v255;
            Vector256<float> g = useG ? Vector256.ConvertToSingle(((p >> 8) & mask).AsInt32()) / v255 : default;
            Vector256<float> b = useB ? Vector256.ConvertToSingle(((p >> 16) & mask).AsInt32()) / v255 : default;
            Vector256<float> a = useA ? Vector256.ConvertToSingle((p >> 24).AsInt32()) / v255 : default;
            Vector256<uint> o0 = Channel(r, g, b, a, m[0], m[4], m[8], m[12], m[16], v255, half);
            Vector256<uint> o1 = Channel(r, g, b, a, m[1], m[5], m[9], m[13], m[17], v255, half) << 8;
            Vector256<uint> o2 = Channel(r, g, b, a, m[2], m[6], m[10], m[14], m[18], v255, half) << 16;
            Vector256<uint> o3 = computeAlpha ? Channel(r, g, b, a, m[3], m[7], m[11], m[15], m[19], v255, half) << 24 : p & Vector256.Create(0xFF000000u);
            Vector256.StoreUnsafe(o0 | o1 | o2 | o3, ref Unsafe.As<byte, uint>(ref d), (nuint)x);
        }

        return n;
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
        return Vector256.ConvertToInt32(v).AsUInt32();
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
