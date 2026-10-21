using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// Row kernels for the tone pipeline: lookup-table application on 8- and 16-bit samples, the 8-bit luminance conversion, and the
/// plane pack/unpack/round helpers of the generic float path. The table kernels index tables whose length the caller built
/// (256 or 65536 entries), so the unchecked reads are in range for every index a sample can take.
/// </summary>
internal static class ToneKernel
{
    private static readonly uint[] s_weightR = BuildWeights(0.2126);
    private static readonly uint[] s_weightG = BuildWeights(0.7152);
    private static readonly uint[] s_weightB = BuildWeights(0.0722);
    private static readonly byte[] s_encode16 = BuildEncode16();

    // ---- lookup tables -------------------------------------------------------------------------------------------

    /// <summary>Applies per-color-channel 256-entry tables to interleaved 8-bit pixels; alpha is copied.</summary>
    public static void ApplyLut8(ReadOnlySpan<byte> src, Span<byte> dst, int width, int channels, int colors, byte[][] tables)
    {
        if (channels < colors || colors is not (1 or 3) || src.Length < width * channels || dst.Length < width * channels || tables.Length < colors)
        {
            throw new ArgumentException("Invalid lookup arguments.");
        }

        foreach (byte[] t in tables)
        {
            if (t.Length != 256)
            {
                throw new ArgumentException("Tables must have 256 entries.");
            }
        }

        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte d = ref MemoryMarshal.GetReference(dst);
        if (colors == 3)
        {
            ref byte t0 = ref MemoryMarshal.GetArrayDataReference(tables[0]);
            ref byte t1 = ref MemoryMarshal.GetArrayDataReference(tables[1]);
            ref byte t2 = ref MemoryMarshal.GetArrayDataReference(tables[2]);
            if (channels == 4)
            {
                // One 32-bit load and store per pixel: three table reads, alpha passes through untouched.
                ref uint s4 = ref Unsafe.As<byte, uint>(ref s);
                ref uint d4 = ref Unsafe.As<byte, uint>(ref d);
                for (int x = 0; x < width; x++)
                {
                    uint p = Unsafe.Add(ref s4, x);
                    uint cr = Unsafe.Add(ref t0, (int)(p & 0xFF));
                    uint cg = Unsafe.Add(ref t1, (int)((p >> 8) & 0xFF));
                    uint cb = Unsafe.Add(ref t2, (int)((p >> 16) & 0xFF));
                    Unsafe.Add(ref d4, x) = (p & 0xFF000000u) | cr | (cg << 8) | (cb << 16);
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    int o = x * 3;
                    Unsafe.Add(ref d, o) = Unsafe.Add(ref t0, Unsafe.Add(ref s, o));
                    Unsafe.Add(ref d, o + 1) = Unsafe.Add(ref t1, Unsafe.Add(ref s, o + 1));
                    Unsafe.Add(ref d, o + 2) = Unsafe.Add(ref t2, Unsafe.Add(ref s, o + 2));
                }
            }

            return;
        }

        ref byte g = ref MemoryMarshal.GetArrayDataReference(tables[0]);
        if (channels == 1)
        {
            for (int x = 0; x < width; x++)
            {
                Unsafe.Add(ref d, x) = Unsafe.Add(ref g, Unsafe.Add(ref s, x));
            }
        }
        else
        {
            for (int x = 0; x < width; x++)
            {
                int o = x * channels;
                Unsafe.Add(ref d, o) = Unsafe.Add(ref g, Unsafe.Add(ref s, o));
                for (int k = 1; k < channels; k++)
                {
                    Unsafe.Add(ref d, o + k) = Unsafe.Add(ref s, o + k);
                }
            }
        }
    }

    /// <summary>Applies per-color-channel 65536-entry tables to interleaved 16-bit pixels; alpha is copied.</summary>
    public static void ApplyLut16(ReadOnlySpan<byte> src, Span<byte> dst, int width, int channels, int colors, ushort[][] tables)
    {
        if (channels < colors || colors < 1 || colors > 3 || src.Length < width * channels * 2 || dst.Length < width * channels * 2 || tables.Length < colors)
        {
            throw new ArgumentException("Invalid lookup arguments.");
        }

        foreach (ushort[] t in tables)
        {
            if (t.Length != 65536)
            {
                throw new ArgumentException("Tables must have 65536 entries.");
            }
        }

        ReadOnlySpan<ushort> s = SpanCast.As<ushort>(src);
        Span<ushort> d = SpanCast.As<ushort>(dst);
        for (int x = 0; x < width; x++)
        {
            int o = x * channels;
            for (int k = 0; k < colors; k++)
            {
                ushort v = s[o + k];
                d[o + k] = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(tables[k]), v);
            }

            for (int k = colors; k < channels; k++)
            {
                d[o + k] = s[o + k];
            }
        }
    }

    // ---- 8-bit luminance ------------------------------------------------------------------------------------------

    /// <summary>
    /// Rec. 709 luminance of 8-bit sRGB values computed in linear light, re-encoded to 8 bits. Each channel's weighted linear value
    /// comes from a table at 20-bit precision; the sum is rounded to 16 bits before the encode table, which is under 0.05 of an
    /// 8-bit step of error in the encoded result. All integer arithmetic, so every hardware path agrees.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Luma8(int r, int g, int b)
    {
        uint sum = s_weightR[r & 255] + s_weightG[g & 255] + s_weightB[b & 255];
        uint i = (sum + 8) >> 4;
        return s_encode16[i > 65535 ? 65535 : i];
    }

    /// <summary>
    /// Rec. 709 luminance of the stored 8-bit values, weights 13933/46871/4732 over 65536 (0.2126, 0.7152, 0.0722; they sum to exactly 1),
    /// rounded half up.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LumaEncoded8(int r, int g, int b) => (byte)(((r * 13933) + (g * 46871) + (b * 4732) + 32768) >> 16);

    /// <summary>Grayscale of an interleaved 8-bit RGB(A) row, in linear light or on the stored values. With <paramref name="toGray"/> the result is a gray(+alpha) row, otherwise R=G=B. Alpha is copied.</summary>
    public static void GrayscaleRow8(ReadOnlySpan<byte> src, Span<byte> dst, int width, int channels, bool toGray, bool linear)
    {
        int outCh = toGray ? channels - 2 : channels;
        if (channels is not (3 or 4) || src.Length < width * channels || dst.Length < width * outCh)
        {
            throw new ArgumentException("Invalid grayscale arguments.");
        }

        int x = 0;
        if (!linear && !toGray && channels == 4 && Vector256.IsHardwareAccelerated)
        {
            ReadOnlySpan<uint> s4 = SpanCast.As<uint>(src);
            Span<uint> d4 = SpanCast.As<uint>(dst);
            Vector256<uint> mask = Vector256.Create(0xFFu);
            for (; x + 8 <= width; x += 8)
            {
                Vector256<uint> p = Vector256.Create(s4.Slice(x, 8));
                Vector256<uint> r = p & mask;
                Vector256<uint> g = (p >> 8) & mask;
                Vector256<uint> b = (p >> 16) & mask;
                Vector256<uint> y = ((r * 13933u) + (g * 46871u) + (b * 4732u) + Vector256.Create(32768u)) >> 16;
                ((p & Vector256.Create(0xFF000000u)) | y | (y << 8) | (y << 16)).CopyTo(d4.Slice(x, 8));
            }
        }

        if (linear && !toGray && channels == 4)
        {
            // The common case as a tight loop: one 32-bit load and store per pixel, four table reads.
            ref uint s4 = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(src));
            ref uint d4 = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(dst));
            ref uint wr = ref MemoryMarshal.GetArrayDataReference(s_weightR);
            ref uint wg = ref MemoryMarshal.GetArrayDataReference(s_weightG);
            ref uint wb = ref MemoryMarshal.GetArrayDataReference(s_weightB);
            ref byte enc = ref MemoryMarshal.GetArrayDataReference(s_encode16);
            for (; x < width; x++)
            {
                uint p = Unsafe.Add(ref s4, x);
                uint sum = Unsafe.Add(ref wr, (int)(p & 0xFF)) + Unsafe.Add(ref wg, (int)((p >> 8) & 0xFF)) + Unsafe.Add(ref wb, (int)((p >> 16) & 0xFF));
                uint i = Math.Min((sum + 8) >> 4, 65535u);
                uint y = Unsafe.Add(ref enc, (int)i);
                Unsafe.Add(ref d4, x) = (p & 0xFF000000u) | (y * 0x010101u);
            }
        }

        for (; x < width; x++)
        {
            int i = x * channels;
            byte y = linear ? Luma8(src[i], src[i + 1], src[i + 2]) : LumaEncoded8(src[i], src[i + 1], src[i + 2]);
            if (toGray)
            {
                int o = x * outCh;
                dst[o] = y;
                if (channels == 4)
                {
                    dst[o + 1] = src[i + 3];
                }
            }
            else
            {
                dst[i] = y;
                dst[i + 1] = y;
                dst[i + 2] = y;
                if (channels == 4)
                {
                    dst[i + 3] = src[i + 3];
                }
            }
        }
    }

    // ---- plane helpers of the generic float path -----------------------------------------------------------------

    /// <summary>Reads a pixel row into straight-alpha planes.</summary>
    public static void UnpackRow(ReadOnlySpan<byte> src, PixelFormat f, ref ToneRow row)
    {
        int ch = f.ChannelCount;
        int colors = f.ColorChannels;
        int w = row.Width;
        Span<float> tmp = stackalloc float[ch * w];
        ConvertKernel.Unpack(src[..(w * f.BytesPerPixel)], f.Sample, tmp);
        for (int k = 0; k < colors; k++)
        {
            Span<float> plane = row.Plane(k);
            for (int x = 0; x < plane.Length; x++)
            {
                plane[x] = tmp[(x * ch) + k];
            }
        }

        if (f.HasAlpha)
        {
            Span<float> a = row.Plane(3);
            for (int x = 0; x < a.Length; x++)
            {
                a[x] = tmp[(x * ch) + colors];
            }

            if (f.Alpha == AlphaMode.Premultiplied)
            {
                Unpremultiply(ref row, colors);
            }
        }

        row.Colors = colors;
        row.HasAlpha = f.HasAlpha;
        row.Sample = f.Sample;
    }

    /// <summary>Writes planes (which must match the layout of <paramref name="f"/>) as a pixel row, rounding to the sample type.</summary>
    public static void PackRow(ref ToneRow row, PixelFormat f, Span<byte> dst)
    {
        int ch = f.ChannelCount;
        int colors = f.ColorChannels;
        int w = row.Width;
        if (row.Colors != colors || row.HasAlpha != f.HasAlpha)
        {
            throw new InvalidOperationException("Row layout does not match the output format.");
        }

        Span<float> tmp = stackalloc float[ch * w];
        bool premultiply = f.Alpha == AlphaMode.Premultiplied;
        Span<float> alpha = f.HasAlpha ? row.Plane(3) : default;
        for (int k = 0; k < colors; k++)
        {
            Span<float> plane = row.Plane(k);
            for (int x = 0; x < plane.Length; x++)
            {
                tmp[(x * ch) + k] = premultiply ? plane[x] * alpha[x] : plane[x];
            }
        }

        if (f.HasAlpha)
        {
            for (int x = 0; x < alpha.Length; x++)
            {
                tmp[(x * ch) + colors] = alpha[x];
            }
        }

        ConvertKernel.Pack(tmp, f.Sample, dst[..(w * f.BytesPerPixel)]);
    }

    /// <summary>Rounds the planes to the grid of format <paramref name="f"/>, as storing and re-reading the pixels would.</summary>
    public static void QuantizeRow(ref ToneRow row, PixelFormat f, bool alphaChanged)
    {
        bool premultiplied = f.Alpha == AlphaMode.Premultiplied && row.HasAlpha;
        if (f.Sample == SampleType.F32 && !premultiplied)
        {
            return;
        }

        if (premultiplied)
        {
            Span<float> a = row.Plane(3);
            for (int x = 0; x < a.Length; x++)
            {
                a[x] = Round(a[x], f.Sample);
            }

            for (int k = 0; k < row.Colors; k++)
            {
                Span<float> plane = row.Plane(k);
                for (int x = 0; x < plane.Length; x++)
                {
                    // The stored value is color * alpha (with the unrounded alpha, as Pack does); reading it back divides by the rounded alpha.
                    plane[x] = a[x] > 0f ? Round(plane[x] * a[x], f.Sample) / a[x] : 0f;
                }
            }

            return;
        }

        for (int k = 0; k < row.Colors; k++)
        {
            Span<float> plane = row.Plane(k);
            for (int x = 0; x < plane.Length; x++)
            {
                plane[x] = Round(plane[x], f.Sample);
            }
        }

        if (row.HasAlpha && alphaChanged)
        {
            Span<float> a = row.Plane(3);
            for (int x = 0; x < a.Length; x++)
            {
                a[x] = Round(a[x], f.Sample);
            }
        }
    }

    private static void Unpremultiply(ref ToneRow row, int colors)
    {
        Span<float> a = row.Plane(3);
        for (int k = 0; k < colors; k++)
        {
            Span<float> plane = row.Plane(k);
            for (int x = 0; x < plane.Length; x++)
            {
                plane[x] = a[x] > 0f ? plane[x] / a[x] : 0f;
            }
        }
    }

    /// <summary>Rounds every value to the grid of the sample type (see <see cref="Round"/>).</summary>
    public static void RoundSpan(Span<float> values, SampleType sample)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Round(values[i], sample);
        }
    }

    /// <summary>The 8-bit store of <see cref="ConvertKernel.Pack"/>: round half up, clamp, NaN to 0.</summary>
    public static byte StoreU8(float x)
    {
        float v = (x * 255f) + 0.5f;
        return !(v > 0f) ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
    }

    /// <summary>The 16-bit store of <see cref="ConvertKernel.Pack"/>.</summary>
    public static ushort StoreU16(float x)
    {
        float v = (x * 65535f) + 0.5f;
        return !(v > 0f) ? (ushort)0 : v >= 65535f ? (ushort)65535 : (ushort)v;
    }

    /// <summary>Round-trips a value through the sample type exactly as <see cref="ConvertKernel.Pack"/> then <see cref="ConvertKernel.Unpack"/> would.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Round(float x, SampleType sample)
    {
        switch (sample)
        {
            case SampleType.U8:
                {
                    float v = (x * 255f) + 0.5f;
                    byte b = !(v > 0f) ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
                    return b / 255f;
                }

            case SampleType.U16:
                {
                    float v = (x * 65535f) + 0.5f;
                    ushort u = !(v > 0f) ? (ushort)0 : v >= 65535f ? (ushort)65535 : (ushort)v;
                    return u / 65535f;
                }

            case SampleType.F16:
                return (float)(Half)x;
            default:
                return x;
        }
    }

    private static uint[] BuildWeights(double weight)
    {
        var t = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            double lin = i / 255.0;
            lin = lin <= 0.04045 ? lin / 12.92 : Math.Pow((lin + 0.055) / 1.055, 2.4);
            t[i] = (uint)Math.Floor((weight * lin * 65535.0 * 16.0) + 0.5);
        }

        return t;
    }

    private static byte[] BuildEncode16()
    {
        var t = new byte[65536];
        for (int i = 0; i < t.Length; i++)
        {
            double lin = i / 65535.0;
            double enc = lin <= 0.0031308 ? lin * 12.92 : (1.055 * Math.Pow(lin, 1.0 / 2.4)) - 0.055;
            t[i] = (byte)Math.Floor((enc * 255.0) + 0.5);
        }

        return t;
    }
}
