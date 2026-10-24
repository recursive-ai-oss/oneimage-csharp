using System.Buffers;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// Integer fast paths for the common 8/16-bit conversions that <see cref="ConvertKernel"/> otherwise does through float32.
/// Each path matches that float reference exactly except where noted (un-premultiply: at most 1 LSB, from
/// rounding at exact ties). Vector code uses <see cref="Vector128"/> shuffles (a single <c>pshufb</c>/<c>tbl</c>); rows
/// shorter than a vector, and the row tails, run the scalar loops, which are the reference for the vector paths.
/// </summary>
internal static class ConvertFast
{
    private const byte Z = 0x80; // out-of-range shuffle index yields zero

    /// <summary>Converts a row if a fast path covers the pair; returns false (touching nothing) otherwise.</summary>
    public static bool TryConvert(ReadOnlySpan<byte> src, PixelFormat sf, Span<byte> dst, PixelFormat df, int width)
    {
        SampleType ss = sf.Sample;
        SampleType ds = df.Sample;
        if (sf.Model != df.Model && !(sf.Model is ColorModel.Gray or ColorModel.Rgb && df.Model is ColorModel.Gray or ColorModel.Rgb))
        {
            return false;
        }

        bool intSrc = ss is SampleType.U8 or SampleType.U16;
        bool intDst = ds is SampleType.U8 or SampleType.U16;
        if (!intSrc || !intDst)
        {
            return false;
        }

        if (sf.Model == ColorModel.Rgb && df.Model == ColorModel.Gray)
        {
            // RGB to gray: integer path that reproduces the float reference exactly (see RgbToGrayKernel).
            return ss == SampleType.U8 && RgbToGrayKernel.TryConvert(src, sf, dst, df, width);
        }

        if (ss != ds && sf.Model == df.Model && sf.Alpha == df.Alpha)
        {
            int n = width * sf.ChannelCount;
            if (ss == SampleType.U8)
            {
                Widen(src, dst, n);
            }
            else
            {
                Narrow(src, dst, n);
            }

            return true;
        }

        if (ss == ds)
        {
            return ss == SampleType.U8 ? Structural8(src, dst, width, sf, df) : Structural16(src, dst, width, sf, df);
        }

        // Different sample size and different layout: change the sample size and the layout in two exact steps. Both steps act per
        // channel (and alpha 255 widens to 65535, which is what the float path produces), so the result equals the float path.
        if (sf.Alpha == AlphaMode.Premultiplied || df.Alpha == AlphaMode.Premultiplied || !LayoutSupported(sf, df))
        {
            return false;
        }

        byte[] scratch = Pools<byte>.Pool.Rent(width * 8);
        try
        {
            if (ss == SampleType.U16)
            {
                Narrow(src, scratch, width * sf.ChannelCount);
                return Structural8(scratch, dst, width, sf, df);
            }

            if (!Structural8(src, scratch, width, sf, df))
            {
                return false;
            }

            Widen(scratch, dst, width * df.ChannelCount);
            return true;
        }
        finally
        {
            Pools<byte>.Pool.Return(scratch);
        }
    }

    /// <summary>Layouts that change only the channel set (add/drop alpha, replicate gray), not the values. RGB to gray is not one of them (see RgbToGrayKernel).</summary>
    private static bool LayoutSupported(PixelFormat sf, PixelFormat df)
    {
        if (sf.Model == ColorModel.Rgb && df.Model == ColorModel.Rgb)
        {
            return sf.HasAlpha != df.HasAlpha;
        }

        if (sf.Model == ColorModel.Gray && df.Model == ColorModel.Rgb)
        {
            return true;
        }

        return sf.Model == ColorModel.Gray && df.Model == ColorModel.Gray && sf.HasAlpha != df.HasAlpha;
    }

    private static bool Structural8(ReadOnlySpan<byte> src, Span<byte> dst, int width, PixelFormat sf, PixelFormat df)
    {
        AlphaMode sa = sf.Alpha;
        AlphaMode da = df.Alpha;
        if (sf.Model == ColorModel.Rgb && df.Model == ColorModel.Rgb)
        {
            if (sa == AlphaMode.Straight && da == AlphaMode.None)
            {
                DropAlpha(src, dst, width);
            }
            else if (sa == AlphaMode.None && da == AlphaMode.Straight)
            {
                AddAlpha(src, dst, width);
            }
            else if (sa == AlphaMode.Straight && da == AlphaMode.Premultiplied && sf.Sample == SampleType.U8 && df.Sample == SampleType.U8)
            {
                Premultiply(src, dst, width);
            }
            else if (sa == AlphaMode.Premultiplied && da == AlphaMode.Straight && sf.Sample == SampleType.U8 && df.Sample == SampleType.U8)
            {
                Unpremultiply(src, dst, width);
            }
            else
            {
                return false;
            }

            return true;
        }

        if (sa == AlphaMode.Premultiplied || da == AlphaMode.Premultiplied)
        {
            return false;
        }

        if (sf.Model == ColorModel.Gray && df.Model == ColorModel.Rgb)
        {
            GrayToRgb(src, dst, width, sf.HasAlpha, df.HasAlpha);
            return true;
        }

        // RGB to gray is RgbToGrayKernel's (it reproduces the float arithmetic exactly, so tone fusion still matches).

        if (sf.Model == ColorModel.Gray && df.Model == ColorModel.Gray && sf.HasAlpha != df.HasAlpha)
        {
            GrayAlphaChange(src, dst, width, df.HasAlpha);
            return true;
        }

        return false;
    }

    // 16-bit layout changes: plain loops over ushort samples (opaque alpha is 65535, the float path's 1.0). Straight alpha only.
    private static bool Structural16(ReadOnlySpan<byte> srcBytes, Span<byte> dstBytes, int width, PixelFormat sf, PixelFormat df)
    {
        if (sf.Alpha == AlphaMode.Premultiplied || df.Alpha == AlphaMode.Premultiplied || !LayoutSupported(sf, df))
        {
            return false;
        }

        ReadOnlySpan<ushort> src = SpanCast.As<ushort>(srcBytes);
        Span<ushort> dst = SpanCast.As<ushort>(dstBytes);
        int sch = sf.ChannelCount;
        int dch = df.ChannelCount;
        bool grayToRgb = sf.Model == ColorModel.Gray && df.Model == ColorModel.Rgb;
        int colors = grayToRgb ? 1 : sf.ColorChannels;
        for (int x = 0; x < width; x++)
        {
            ReadOnlySpan<ushort> s = src.Slice(x * sch, sch);
            Span<ushort> d = dst.Slice(x * dch, dch);
            if (grayToRgb)
            {
                d[0] = d[1] = d[2] = s[0];
            }
            else
            {
                for (int k = 0; k < colors; k++)
                {
                    d[k] = s[k];
                }
            }

            if (df.HasAlpha)
            {
                d[df.ColorChannels] = sf.HasAlpha ? s[sf.ColorChannels] : (ushort)65535;
            }
        }

        return true;
    }

    // Rgba -> Rgb: four pixels per shuffle; the 16-byte store spills 4 bytes the next iteration overwrites, so it stops 16 bytes short.
    private static void DropAlpha(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> idx = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0, 0, 0, 0);
            for (; (x + 4 <= width) && ((x * 3) + 16 <= width * 3); x += 4)
            {
                Vector128.Shuffle(Vector128.Create(src.Slice(x * 4, 16)), idx).CopyTo(dst.Slice(x * 3, 16));
            }
        }

        for (; x < width; x++)
        {
            dst[x * 3] = src[x * 4];
            dst[(x * 3) + 1] = src[(x * 4) + 1];
            dst[(x * 3) + 2] = src[(x * 4) + 2];
        }
    }

    private static void AddAlpha(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> idx = Vector128.Create((byte)0, 1, 2, Z, 3, 4, 5, Z, 6, 7, 8, Z, 9, 10, 11, Z);
            Vector128<byte> opaque = Vector128.Create((byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);
            for (; (x + 4 <= width) && ((x * 3) + 16 <= width * 3); x += 4)
            {
                (Vector128.Shuffle(Vector128.Create(src.Slice(x * 3, 16)), idx) | opaque).CopyTo(dst.Slice(x * 4, 16));
            }
        }

        for (; x < width; x++)
        {
            dst[x * 4] = src[x * 3];
            dst[(x * 4) + 1] = src[(x * 3) + 1];
            dst[(x * 4) + 2] = src[(x * 3) + 2];
            dst[(x * 4) + 3] = 255;
        }
    }

    // c*a/255 rounded half up. Ties cannot occur (2ca is even, 255*odd is odd), so this equals the float reference.
    // x/255 = (x + 1 + (x >> 8)) >> 8 is exact for 0 <= x < 65535.
    private static void Premultiply(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> alphaIdx = Vector128.Create((byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);
            Vector128<byte> alphaLanes = Vector128.Create((byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);
            Vector128<ushort> round = Vector128.Create((ushort)127);
            Vector128<ushort> one = Vector128.Create((ushort)1);
            for (; x + 4 <= width; x += 4)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(x * 4, 16));
                Vector128<byte> a = Vector128.Shuffle(v, alphaIdx);
                Vector128<ushort> lo = Div255((Vector128.WidenLower(v) * Vector128.WidenLower(a)) + round, one);
                Vector128<ushort> hi = Div255((Vector128.WidenUpper(v) * Vector128.WidenUpper(a)) + round, one);
                Vector128.ConditionalSelect(alphaLanes, v, Vector128.Narrow(lo, hi)).CopyTo(dst.Slice(x * 4, 16));
            }
        }

        for (; x < width; x++)
        {
            int o = x * 4;
            int a = src[o + 3];
            for (int k = 0; k < 3; k++)
            {
                int t = (src[o + k] * a) + 127;
                dst[o + k] = (byte)((t + 1 + (t >> 8)) >> 8);
            }

            dst[o + 3] = (byte)a;
        }
    }

    private static Vector128<ushort> Div255(Vector128<ushort> t, Vector128<ushort> one) => (t + one + (t >> 8)) >> 8;

    // round(c*255/a) clamped; the reference divides in float, so exact .5 ties may differ by 1.
    private static void Unpremultiply(ReadOnlySpan<byte> src, Span<byte> dst, int width)
    {
        for (int x = 0; x < width; x++)
        {
            int o = x * 4;
            int a = src[o + 3];
            if (a == 0)
            {
                dst[o] = dst[o + 1] = dst[o + 2] = dst[o + 3] = 0;
                continue;
            }

            for (int k = 0; k < 3; k++)
            {
                int c = src[o + k];
                dst[o + k] = a == 255 ? (byte)c : (byte)Math.Min(255, ((c * 510) + a) / (2 * a));
            }

            dst[o + 3] = (byte)a;
        }
    }

    private static void GrayToRgb(ReadOnlySpan<byte> src, Span<byte> dst, int width, bool srcAlpha, bool dstAlpha)
    {
        int sb = srcAlpha ? 2 : 1;
        int db = dstAlpha ? 4 : 3;
        int x = 0;
        if (Vector128.IsHardwareAccelerated && dstAlpha)
        {
            Vector128<byte> opaque = Vector128.Create((byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);
            if (!srcAlpha)
            {
                Vector128<byte> i0 = Vector128.Create((byte)0, 0, 0, Z, 1, 1, 1, Z, 2, 2, 2, Z, 3, 3, 3, Z);
                Vector128<byte> i1 = Vector128.Create((byte)4, 4, 4, Z, 5, 5, 5, Z, 6, 6, 6, Z, 7, 7, 7, Z);
                Vector128<byte> i2 = Vector128.Create((byte)8, 8, 8, Z, 9, 9, 9, Z, 10, 10, 10, Z, 11, 11, 11, Z);
                Vector128<byte> i3 = Vector128.Create((byte)12, 12, 12, Z, 13, 13, 13, Z, 14, 14, 14, Z, 15, 15, 15, Z);
                for (; x + 16 <= width; x += 16)
                {
                    Vector128<byte> v = Vector128.Create(src.Slice(x, 16));
                    (Vector128.Shuffle(v, i0) | opaque).CopyTo(dst.Slice(x * 4, 16));
                    (Vector128.Shuffle(v, i1) | opaque).CopyTo(dst.Slice((x * 4) + 16, 16));
                    (Vector128.Shuffle(v, i2) | opaque).CopyTo(dst.Slice((x * 4) + 32, 16));
                    (Vector128.Shuffle(v, i3) | opaque).CopyTo(dst.Slice((x * 4) + 48, 16));
                }
            }
            else
            {
                Vector128<byte> i0 = Vector128.Create((byte)0, 0, 0, 1, 2, 2, 2, 3, 4, 4, 4, 5, 6, 6, 6, 7);
                Vector128<byte> i1 = Vector128.Create((byte)8, 8, 8, 9, 10, 10, 10, 11, 12, 12, 12, 13, 14, 14, 14, 15);
                for (; x + 8 <= width; x += 8)
                {
                    Vector128<byte> v = Vector128.Create(src.Slice(x * 2, 16));
                    Vector128.Shuffle(v, i0).CopyTo(dst.Slice(x * 4, 16));
                    Vector128.Shuffle(v, i1).CopyTo(dst.Slice((x * 4) + 16, 16));
                }
            }
        }

        for (; x < width; x++)
        {
            byte g = src[x * sb];
            dst[x * db] = g;
            dst[(x * db) + 1] = g;
            dst[(x * db) + 2] = g;
            if (dstAlpha)
            {
                dst[(x * db) + 3] = srcAlpha ? src[(x * sb) + 1] : (byte)255;
            }
        }
    }

    private static void GrayAlphaChange(ReadOnlySpan<byte> src, Span<byte> dst, int width, bool addAlpha)
    {
        int x = 0;
        if (addAlpha)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<byte> lo = Vector128.Create((byte)0, Z, 1, Z, 2, Z, 3, Z, 4, Z, 5, Z, 6, Z, 7, Z);
                Vector128<byte> hi = Vector128.Create((byte)8, Z, 9, Z, 10, Z, 11, Z, 12, Z, 13, Z, 14, Z, 15, Z);
                Vector128<byte> opaque = Vector128.Create((byte)0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255);
                for (; x + 16 <= width; x += 16)
                {
                    Vector128<byte> v = Vector128.Create(src.Slice(x, 16));
                    (Vector128.Shuffle(v, lo) | opaque).CopyTo(dst.Slice(x * 2, 16));
                    (Vector128.Shuffle(v, hi) | opaque).CopyTo(dst.Slice((x * 2) + 16, 16));
                }
            }

            for (; x < width; x++)
            {
                dst[x * 2] = src[x];
                dst[(x * 2) + 1] = 255;
            }
        }
        else
        {
            for (; x < width; x++)
            {
                dst[x] = src[x * 2];
            }
        }
    }

    // v * 257 is exactly the float reference's v/255*65535 (rounded).
    private static void Widen(ReadOnlySpan<byte> src, Span<byte> dst, int n)
    {
        Span<ushort> d = SpanCast.As<ushort>(dst);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<ushort> k = Vector128.Create((ushort)257);
            for (; i + 16 <= n; i += 16)
            {
                Vector128<byte> v = Vector128.Create(src.Slice(i, 16));
                (Vector128.WidenLower(v) * k).CopyTo(d.Slice(i, 8));
                (Vector128.WidenUpper(v) * k).CopyTo(d.Slice(i + 8, 8));
            }
        }

        for (; i < n; i++)
        {
            d[i] = (ushort)(src[i] * 257);
        }
    }

    // round(v / 257) = floor((v + 128) / 257); 65281 * 257 = 2^24 + 1, so (t * 65281) >> 24 is exact for t < 2^17.
    private static void Narrow(ReadOnlySpan<byte> src, Span<byte> dst, int n)
    {
        ReadOnlySpan<ushort> s = SpanCast.As<ushort>(src);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<uint> bias = Vector128.Create(128u);
            Vector128<uint> k = Vector128.Create(65281u);
            for (; i + 16 <= n; i += 16)
            {
                Vector128<ushort> a = Vector128.Create(s.Slice(i, 8));
                Vector128<ushort> b = Vector128.Create(s.Slice(i + 8, 8));
                Vector128<ushort> lo = Vector128.Narrow(Div257(Vector128.WidenLower(a), bias, k), Div257(Vector128.WidenUpper(a), bias, k));
                Vector128<ushort> hi = Vector128.Narrow(Div257(Vector128.WidenLower(b), bias, k), Div257(Vector128.WidenUpper(b), bias, k));
                Vector128.Narrow(lo, hi).CopyTo(dst.Slice(i, 16));
            }
        }

        for (; i < n; i++)
        {
            dst[i] = (byte)(((uint)(s[i] + 128) * 65281u) >> 24);
        }
    }

    // The float reference reads an 8-bit sample as v / 255f; the table holds those exact quotients.
    internal static readonly float[] ByteToUnit = CreateByteToUnit();

    private static float[] CreateByteToUnit()
    {
        var t = new float[256];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = i / 255f;
        }

        return t;
    }

    /// <summary>
    /// 4-channel RGBA rows between 8-bit and 32-bit float samples, straight or premultiplied (what drawing and layer compositing convert
    /// every row through). Each pixel runs the same float operations in the same order as the reference, so results are identical,
    /// without the temporary float rows. Returns false, touching nothing, for any other pair.
    /// </summary>
    public static bool TryConvertRgbaFloat(ReadOnlySpan<byte> src, PixelFormat sf, Span<byte> dst, PixelFormat df, int width)
    {
        if (sf.Model != ColorModel.Rgb || df.Model != ColorModel.Rgb || sf.ChannelCount != 4 || df.ChannelCount != 4
            || sf.Alpha == AlphaMode.None || df.Alpha == AlphaMode.None
            || (sf.Sample is not (SampleType.U8 or SampleType.F32)) || (df.Sample is not (SampleType.U8 or SampleType.F32)))
        {
            return false;
        }

        bool unPremultiply = sf.Alpha == AlphaMode.Premultiplied;
        bool premultiply = df.Alpha == AlphaMode.Premultiplied;
        if (sf.Sample == SampleType.U8)
        {
            ReadOnlySpan<byte> s8 = src[..(width * 4)];
            if (df.Sample == SampleType.F32)
            {
                Span<float> d32 = SpanCast.As<float>(dst)[..(width * 4)];
                U8ToFloat(s8, d32, unPremultiply, premultiply);
            }
            else
            {
                Span<byte> d8 = dst[..(width * 4)];
                for (int i = 0; i < d8.Length; i += 4)
                {
                    (float r, float g, float b, float a) = Convert4(ByteToUnit[s8[i]], ByteToUnit[s8[i + 1]], ByteToUnit[s8[i + 2]], ByteToUnit[s8[i + 3]], unPremultiply, premultiply);
                    d8[i] = ToByte(r);
                    d8[i + 1] = ToByte(g);
                    d8[i + 2] = ToByte(b);
                    d8[i + 3] = ToByte(a);
                }
            }

            return true;
        }

        ReadOnlySpan<float> s32 = SpanCast.As<float>(src)[..(width * 4)];
        if (df.Sample == SampleType.F32)
        {
            Span<float> d32 = SpanCast.As<float>(dst)[..(width * 4)];
            for (int i = 0; i < d32.Length; i += 4)
            {
                (float r, float g, float b, float a) = Convert4(s32[i], s32[i + 1], s32[i + 2], s32[i + 3], unPremultiply, premultiply);
                d32[i] = r;
                d32[i + 1] = g;
                d32[i + 2] = b;
                d32[i + 3] = a;
            }
        }
        else
        {
            Span<byte> d8 = dst[..(width * 4)];
            for (int i = 0; i < d8.Length; i += 4)
            {
                (float r, float g, float b, float a) = Convert4(s32[i], s32[i + 1], s32[i + 2], s32[i + 3], unPremultiply, premultiply);
                d8[i] = ToByte(r);
                d8[i + 1] = ToByte(g);
                d8[i + 2] = ToByte(b);
                d8[i + 3] = ToByte(a);
            }
        }

        return true;
    }

    private static void U8ToFloat(ReadOnlySpan<byte> s8, Span<float> d32, bool unPremultiply, bool premultiply)
    {
        ReadOnlySpan<float> table = ByteToUnit;
        if (!unPremultiply && premultiply)
        {
            // The common case: straight 8-bit pixels become premultiplied floats.
            for (int i = 0; i < d32.Length; i += 4)
            {
                float a = table[s8[i + 3]];
                d32[i] = table[s8[i]] * a;
                d32[i + 1] = table[s8[i + 1]] * a;
                d32[i + 2] = table[s8[i + 2]] * a;
                d32[i + 3] = a;
            }

            return;
        }

        for (int i = 0; i < d32.Length; i += 4)
        {
            (float r, float g, float b, float a) = Convert4(table[s8[i]], table[s8[i + 1]], table[s8[i + 2]], table[s8[i + 3]], unPremultiply, premultiply);
            d32[i] = r;
            d32[i + 1] = g;
            d32[i + 2] = b;
            d32[i + 3] = a;
        }
    }

    // The reference's per-pixel arithmetic: divide the color by alpha if it is premultiplied (0 where alpha is 0), multiply by alpha if the target is.
    private static (float R, float G, float B, float A) Convert4(float r, float g, float b, float a, bool unPremultiply, bool premultiply)
    {
        if (unPremultiply)
        {
            r = a > 0f ? r / a : 0f;
            g = a > 0f ? g / a : 0f;
            b = a > 0f ? b / a : 0f;
        }

        if (premultiply)
        {
            r *= a;
            g *= a;
            b *= a;
        }

        return (r, g, b, a);
    }

    internal static byte ToByte(float unit)
    {
        float v = (unit * 255f) + 0.5f;
        return !(v > 0f) ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
    }

    private static Vector128<uint> Div257(Vector128<uint> v, Vector128<uint> bias, Vector128<uint> k) => ((v + bias) * k) >> 24;
}
