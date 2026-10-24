using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// 8-bit RGB / RGBA rows to gray (8 or 16 bits, with or without alpha), byte-identical to the float32 reference in
/// <see cref="ConvertKernel.ConvertViaFloat"/>, which computes <c>0.2126 r + 0.7152 g + 0.0722 b</c> on <c>v / 255f</c> samples in float and rounds.
/// <para>
/// The integer path uses 16-bit fixed-point weights, so it differs from the float result only when the exact value lies close to a
/// rounding boundary (about 1% of pixels at 8 bits, a few percent at 16). Those pixels, found per lane, are recomputed with the very
/// float expression of the reference. Away from a boundary the two roundings agree because the fixed-point error (at most 383/65536 of
/// an output level at 8 bits) and the float error (a few hundred thousandths of a level) are far smaller than the margin.
/// The tests compare all 2^24 RGB triples (and every alpha) against the reference.
/// </para>
/// The scalar loops are the reference for the vector loops; rows shorter than a vector and the row tails run them.
/// </summary>
internal static class RgbToGrayKernel
{
    // 8-bit output: weights are 0.2126, 0.7152, 0.0722 times 65536 (they sum to exactly 65536).
    private const uint R8 = 13933;
    private const uint G8 = 46871;
    private const uint B8 = 4732;

    // 16-bit output: the same weights times 257 (255 -> 65535) times 65536. The largest sum, 255 * 16842753 + 32768, stays below 2^32.
    private const uint R16 = (uint)((0.2126 * 16842752.0) + 0.5);
    private const uint G16 = (uint)((0.7152 * 16842752.0) + 0.5);
    private const uint B16 = (uint)((0.0722 * 16842752.0) + 0.5);

    // Distance from a rounding boundary (in 1/65536 of an output level) inside which the float expression decides.
    private const uint Margin8 = 512;
    private const uint Margin16 = 2048;

    /// <summary>Converts a row if the pair is 8-bit RGB(A) to 8/16-bit gray (straight or no alpha on the target); returns false, touching nothing, otherwise.</summary>
    public static bool TryConvert(ReadOnlySpan<byte> src, PixelFormat sf, Span<byte> dst, PixelFormat df, int width)
    {
        if (sf.Model != ColorModel.Rgb || sf.Sample != SampleType.U8 || df.Model != ColorModel.Gray
            || df.Sample is not (SampleType.U8 or SampleType.U16) || df.Alpha == AlphaMode.Premultiplied)
        {
            return false;
        }

        bool wide = df.Sample == SampleType.U16;
        if (sf.Alpha == AlphaMode.Premultiplied || df.HasAlpha)
        {
            WithAlpha(src, sf, dst, df, width);
            return true;
        }

        if (wide)
        {
            Gray16Row(src, sf.HasAlpha, SpanCast.As<ushort>(dst), width);
        }
        else
        {
            Gray8Row(src, sf.HasAlpha, dst, width);
        }

        return true;
    }

    /// <summary>Gray8 of one pixel by the integer path with the float fallback near a rounding boundary (the scalar reference of the vector loops).</summary>
    public static byte Gray8Pixel(int r, int g, int b)
    {
        uint y = (uint)((r * (int)R8) + (g * (int)G8) + (b * (int)B8)) + 32768u;
        return NearBoundary(y, Margin8) ? Exact8(r, g, b) : (byte)(y >> 16);
    }

    /// <summary>Gray16 of one pixel, as <see cref="Gray8Pixel"/>.</summary>
    public static ushort Gray16Pixel(int r, int g, int b)
    {
        uint y = (uint)((uint)r * R16 + (uint)g * G16 + (uint)b * B16) + 32768u;
        return NearBoundary(y, Margin16) ? Exact16(r, g, b) : (ushort)(y >> 16);
    }

    private static bool NearBoundary(uint y, uint margin) => (((y & 0xFFFF) + margin) & 0xFFFF) < 2 * margin;

    // The reference expression, operation for operation.
    private static float Luma(int r, int g, int b)
    {
        float[] t = ConvertFast.ByteToUnit;
        return (0.2126f * t[r]) + (0.7152f * t[g]) + (0.0722f * t[b]);
    }

    private static byte Exact8(int r, int g, int b) => ConvertFast.ToByte(Luma(r, g, b));

    private static ushort Exact16(int r, int g, int b) => ToU16(Luma(r, g, b));

    private static ushort ToU16(float unit)
    {
        float v = (unit * 65535f) + 0.5f;
        return !(v > 0f) ? (ushort)0 : v >= 65535f ? (ushort)65535 : (ushort)v;
    }

    // Gray plus alpha and premultiplied sources: one scalar loop that follows the reference per pixel without the float rows.
    private static void WithAlpha(ReadOnlySpan<byte> src, PixelFormat sf, Span<byte> dstBytes, PixelFormat df, int width)
    {
        int sch = sf.ChannelCount;
        bool premul = sf.Alpha == AlphaMode.Premultiplied;
        bool hasSrcAlpha = sf.HasAlpha;
        bool wide = df.Sample == SampleType.U16;
        Span<ushort> d16 = wide ? SpanCast.As<ushort>(dstBytes) : default;
        int dch = df.ChannelCount;
        float[] table = ConvertFast.ByteToUnit;
        for (int x = 0; x < width; x++)
        {
            ReadOnlySpan<byte> p = src.Slice(x * sch, sch);
            int o = x * dch;
            if (!premul)
            {
                if (wide)
                {
                    d16[o] = Gray16Pixel(p[0], p[1], p[2]);
                    if (df.HasAlpha)
                    {
                        d16[o + 1] = hasSrcAlpha ? (ushort)(p[3] * 257) : (ushort)65535;
                    }
                }
                else
                {
                    dstBytes[o] = Gray8Pixel(p[0], p[1], p[2]);
                    if (df.HasAlpha)
                    {
                        dstBytes[o + 1] = hasSrcAlpha ? p[3] : (byte)255;
                    }
                }

                continue;
            }

            // Premultiplied source: divide the color by alpha in float (0 where alpha is 0) as the reference does.
            float a = table[p[3]];
            float r = a > 0f ? table[p[0]] / a : 0f;
            float g = a > 0f ? table[p[1]] / a : 0f;
            float b = a > 0f ? table[p[2]] / a : 0f;
            float gray = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
            if (wide)
            {
                d16[o] = ToU16(gray);
                if (df.HasAlpha)
                {
                    d16[o + 1] = ToU16(a);
                }
            }
            else
            {
                dstBytes[o] = ConvertFast.ToByte(gray);
                if (df.HasAlpha)
                {
                    dstBytes[o + 1] = ConvertFast.ToByte(a);
                }
            }
        }
    }

    // ---- Gray8 --------------------------------------------------------------------------------------------------------------

    /// <summary>Gray8 of a row of 3- or 4-channel 8-bit pixels. Public to the kernel tests so each width class can be driven.</summary>
    public static void Gray8Row(ReadOnlySpan<byte> src, bool alpha, Span<byte> dst, int width)
    {
        int bpp = alpha ? 4 : 3;
        src = src[..(width * bpp)];
        dst = dst[..width];
        int x = 0;
        if (Vector256.IsHardwareAccelerated && Vector128.IsHardwareAccelerated)
        {
            x = alpha ? Gray8Rgba256(src, dst, width) : Gray8Rgb256(src, dst, width);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            x = alpha ? Gray8Rgba128(src, dst, width) : Gray8Rgb128(src, dst, width);
        }

        for (; x < width; x++)
        {
            int o = x * bpp;
            dst[x] = Gray8Pixel(src[o], src[o + 1], src[o + 2]);
        }
    }

    private static Vector128<uint> Luma8(Vector128<uint> px, out Vector128<uint> near)
    {
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<uint> y = ((px & mask) * Vector128.Create(R8)) + (((px >> 8) & mask) * Vector128.Create(G8))
            + (((px >> 16) & mask) * Vector128.Create(B8)) + Vector128.Create(32768u);
        near = Vector128.LessThan(((y & Vector128.Create(0xFFFFu)) + Vector128.Create(Margin8)) & Vector128.Create(0xFFFFu), Vector128.Create(2 * Margin8));
        return y >> 16;
    }

    private static Vector256<uint> Luma8(Vector256<uint> px, out Vector256<uint> near)
    {
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<uint> y = ((px & mask) * Vector256.Create(R8)) + (((px >> 8) & mask) * Vector256.Create(G8))
            + (((px >> 16) & mask) * Vector256.Create(B8)) + Vector256.Create(32768u);
        near = Vector256.LessThan(((y & Vector256.Create(0xFFFFu)) + Vector256.Create(Margin8)) & Vector256.Create(0xFFFFu), Vector256.Create(2 * Margin8));
        return y >> 16;
    }

    // Rgb24 -> one RGBA-like uint per pixel (alpha lane zero): 4 pixels from 12 of the 16 bytes loaded. A method, not a static field,
    // so the shuffle indices are JIT-time constants and compile to a single byte shuffle.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> RgbToLanes(Vector128<byte> v) =>
        Vector128.Shuffle(v, Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80));

    private static int Gray8Rgba256(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int x = 0;
        for (; x + 16 <= width; x += 16)
        {
            Vector256<uint> q0 = Luma8(Vector256.Create(src.Slice(x * 4, 32)).AsUInt32(), out Vector256<uint> n0);
            Vector256<uint> q1 = Luma8(Vector256.Create(src.Slice((x * 4) + 32, 32)).AsUInt32(), out Vector256<uint> n1);
            StoreGray8(q0, q1, dst.Slice(x, 16));
            Fix8(src, 4, dst, x, n0, n1);
        }

        return x;
    }

    private static int Gray8Rgb256(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int x = 0;
        // Each 8 pixels read two 16-byte loads 12 bytes apart; the last load (at +24, +12, 16 wide) must stay inside the row.
        for (; x + 16 <= width && (x * 3) + 52 <= src.Length; x += 16)
        {
            Vector256<uint> q0 = Luma8(Load8Rgb(src, x * 3), out Vector256<uint> n0);
            Vector256<uint> q1 = Luma8(Load8Rgb(src, (x * 3) + 24), out Vector256<uint> n1);
            StoreGray8(q0, q1, dst.Slice(x, 16));
            Fix8(src, 3, dst, x, n0, n1);
        }

        return x;
    }

    private static Vector256<uint> Load8Rgb(ReadOnlySpan<byte> src, int offset) =>
        Vector256.Create(
            RgbToLanes(Vector128.Create(src.Slice(offset, 16))),
            RgbToLanes(Vector128.Create(src.Slice(offset + 12, 16)))).AsUInt32();

    private static void StoreGray8(Vector256<uint> q0, Vector256<uint> q1, Span<byte> dst)
    {
        Vector256<ushort> w = Vector256.Narrow(q0, q1);
        Vector128.Narrow(w.GetLower(), w.GetUpper()).CopyTo(dst);
    }

    // Recompute the lanes flagged as near a rounding boundary with the float expression (rare: about 1% of pixels).
    private static void Fix8(ReadOnlySpan<byte> src, int bpp, Span<byte> dst, int x, Vector256<uint> n0, Vector256<uint> n1)
    {
        if ((n0 | n1) == Vector256<uint>.Zero)
        {
            return;
        }

        uint m0 = n0.ExtractMostSignificantBits();
        uint m1 = n1.ExtractMostSignificantBits();
        FixBits8(src, bpp, dst, x, m0);
        FixBits8(src, bpp, dst, x + 8, m1);
    }

    private static void FixBits8(ReadOnlySpan<byte> src, int bpp, Span<byte> dst, int x, uint bits)
    {
        while (bits != 0)
        {
            int i = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            int o = (x + i) * bpp;
            dst[x + i] = Exact8(src[o], src[o + 1], src[o + 2]);
        }
    }

    private static int Gray8Rgba128(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int x = 0;
        for (; x + 8 <= width; x += 8)
        {
            Vector128<uint> q0 = Luma8(Vector128.Create(src.Slice(x * 4, 16)).AsUInt32(), out Vector128<uint> n0);
            Vector128<uint> q1 = Luma8(Vector128.Create(src.Slice((x * 4) + 16, 16)).AsUInt32(), out Vector128<uint> n1);
            StoreGray8(q0, q1, dst.Slice(x, 8));
            Fix8(src, 4, dst, x, n0, n1);
        }

        return x;
    }

    private static int Gray8Rgb128(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int x = 0;
        for (; x + 8 <= width && (x * 3) + 28 <= src.Length; x += 8)
        {
            Vector128<uint> q0 = Luma8(RgbToLanes(Vector128.Create(src.Slice(x * 3, 16))).AsUInt32(), out Vector128<uint> n0);
            Vector128<uint> q1 = Luma8(RgbToLanes(Vector128.Create(src.Slice((x * 3) + 12, 16))).AsUInt32(), out Vector128<uint> n1);
            StoreGray8(q0, q1, dst.Slice(x, 8));
            Fix8(src, 3, dst, x, n0, n1);
        }

        return x;
    }

    private static void StoreGray8(Vector128<uint> q0, Vector128<uint> q1, Span<byte> dst)
    {
        Vector128<byte> b = Vector128.Narrow(Vector128.Narrow(q0, q1), Vector128<ushort>.Zero);
        BinaryPrimitives.WriteUInt64LittleEndian(dst, b.AsUInt64().ToScalar());
    }

    private static void Fix8(ReadOnlySpan<byte> src, int bpp, Span<byte> dst, int x, Vector128<uint> n0, Vector128<uint> n1)
    {
        if ((n0 | n1) == Vector128<uint>.Zero)
        {
            return;
        }

        FixBits8(src, bpp, dst, x, n0.ExtractMostSignificantBits());
        FixBits8(src, bpp, dst, x + 4, n1.ExtractMostSignificantBits());
    }

    // ---- Gray16 -------------------------------------------------------------------------------------------------------------

    /// <summary>Gray16 of a row of 3- or 4-channel 8-bit pixels.</summary>
    public static void Gray16Row(ReadOnlySpan<byte> src, bool alpha, Span<ushort> dst, int width)
    {
        int bpp = alpha ? 4 : 3;
        src = src[..(width * bpp)];
        dst = dst[..width];
        int x = 0;
        if (Vector256.IsHardwareAccelerated && Vector128.IsHardwareAccelerated)
        {
            for (; x + 8 <= width && (alpha || (x * 3) + 28 <= src.Length); x += 8)
            {
                Vector256<uint> px = alpha ? Vector256.Create(src.Slice(x * 4, 32)).AsUInt32() : Load8Rgb(src, x * 3);
                Vector256<uint> q = Luma16(px, out Vector256<uint> near);
                Vector256.Narrow(q, q).GetLower().CopyTo(dst.Slice(x, 8));
                if (near != Vector256<uint>.Zero)
                {
                    FixBits16(src, bpp, dst, x, near.ExtractMostSignificantBits());
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; x + 4 <= width && (alpha || (x * 3) + 16 <= src.Length); x += 4)
            {
                Vector128<uint> px = alpha ? Vector128.Create(src.Slice(x * 4, 16)).AsUInt32() : RgbToLanes(Vector128.Create(src.Slice(x * 3, 16))).AsUInt32();
                Vector128<uint> q = Luma16(px, out Vector128<uint> near);
                Vector128.Narrow(q, q).GetLower().CopyTo(dst.Slice(x, 4));
                if (near != Vector128<uint>.Zero)
                {
                    FixBits16(src, bpp, dst, x, near.ExtractMostSignificantBits());
                }
            }
        }

        for (; x < width; x++)
        {
            int o = x * bpp;
            dst[x] = Gray16Pixel(src[o], src[o + 1], src[o + 2]);
        }
    }

    private static Vector128<uint> Luma16(Vector128<uint> px, out Vector128<uint> near)
    {
        Vector128<uint> mask = Vector128.Create(0xFFu);
        Vector128<uint> y = ((px & mask) * Vector128.Create(R16)) + (((px >> 8) & mask) * Vector128.Create(G16))
            + (((px >> 16) & mask) * Vector128.Create(B16)) + Vector128.Create(32768u);
        near = Vector128.LessThan(((y & Vector128.Create(0xFFFFu)) + Vector128.Create(Margin16)) & Vector128.Create(0xFFFFu), Vector128.Create(2 * Margin16));
        return y >> 16;
    }

    private static Vector256<uint> Luma16(Vector256<uint> px, out Vector256<uint> near)
    {
        Vector256<uint> mask = Vector256.Create(0xFFu);
        Vector256<uint> y = ((px & mask) * Vector256.Create(R16)) + (((px >> 8) & mask) * Vector256.Create(G16))
            + (((px >> 16) & mask) * Vector256.Create(B16)) + Vector256.Create(32768u);
        near = Vector256.LessThan(((y & Vector256.Create(0xFFFFu)) + Vector256.Create(Margin16)) & Vector256.Create(0xFFFFu), Vector256.Create(2 * Margin16));
        return y >> 16;
    }

    private static void FixBits16(ReadOnlySpan<byte> src, int bpp, Span<ushort> dst, int x, uint bits)
    {
        while (bits != 0)
        {
            int i = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            int o = (x + i) * bpp;
            dst[x + i] = Exact16(src[o], src[o + 1], src[o + 2]);
        }
    }
}
