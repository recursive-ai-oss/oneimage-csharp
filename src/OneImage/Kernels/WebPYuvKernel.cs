using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// The lossy WebP output stage: libwebp-compatible "fancy" 4:2:0 chroma upsampling and fixed-point BT.601 YUV to RGB(A)
/// conversion. The vector paths run the same integer steps as the scalar references, in 16-bit lanes, so all paths give identical
/// bytes. Inputs are validated once per call; the vector loops only run where the whole block fits.
/// </summary>
internal static class WebPYuvKernel
{
    // Byte shuffle masks that interleave 16 pixels of separate R, G, B (and A) vectors into 48 or 64 bytes. 0xff selects zero.
    private static readonly Vector128<byte>[] Rgb24Masks = BuildMasks(3);
    private static readonly Vector128<byte>[] Rgba32Masks = BuildMasks(4);

    private static Vector128<byte>[] BuildMasks(int channels)
    {
        // For every 16-byte output vector and every channel: which pixel's byte lands where.
        int vectors = channels;
        var masks = new Vector128<byte>[vectors * channels];
        Span<byte> tmp = stackalloc byte[16];
        for (int s = 0; s < vectors; s++)
        {
            for (int c = 0; c < channels; c++)
            {
                for (int j = 0; j < 16; j++)
                {
                    int k = (16 * s) + j;
                    tmp[j] = k % channels == c ? (byte)(k / channels) : (byte)0xff;
                }

                masks[(s * channels) + c] = Vector128.Create<byte>(tmp);
            }
        }

        return masks;
    }

    // ---- chroma upsampling ----

    /// <summary>
    /// Fancy upsampling of one chroma plane row pair (libwebp's 9-3-3-1 kernel). <paramref name="t"/> is the chroma row nearer the
    /// top output row and <paramref name="c"/> the other; both hold at least (width + 1) / 2 samples. Fills <paramref name="top"/>
    /// and, when <paramref name="bottom"/>, <paramref name="bot"/> with <paramref name="width"/> samples each.
    /// </summary>
    public static void Chroma(ReadOnlySpan<byte> t, ReadOnlySpan<byte> c, int width, Span<byte> top, Span<byte> bot, bool bottom)
    {
        int lastPair = (width - 1) >> 1;
        if (width < 1 || t.Length < lastPair + 1 || c.Length < lastPair + 1 || top.Length < width || (bottom && bot.Length < width))
        {
            throw new ArgumentException("Chroma rows are too short.");
        }

        if (!Vector128.IsHardwareAccelerated)
        {
            ChromaScalar(t, c, width, top, bot, bottom);
            return;
        }

        t = t[..(lastPair + 1)];
        c = c[..(lastPair + 1)];
        int tl = t[0];
        int l = c[0];
        top[0] = (byte)(((3 * tl) + l + 2) >> 2);
        if (bottom)
        {
            bot[0] = (byte)(((3 * l) + tl + 2) >> 2);
        }

        int x = 1;
        ref byte rt = ref MemoryMarshal.GetReference(t);
        ref byte rc = ref MemoryMarshal.GetReference(c);
        ref byte rtop = ref MemoryMarshal.GetReference(top);
        ref byte rbot = ref MemoryMarshal.GetReference(bot);
        // Each block reads 16 bytes at x (8 used) and writes 16 bytes at 2x-1; the loop condition keeps both inside the spans.
        for (; x + 16 <= t.Length && (2 * x) + 15 <= width; x += 8)
        {
            Vector128<ushort> vtl = Vector128.WidenLower(Vector128.LoadUnsafe(ref rt, (nuint)(x - 1)));
            Vector128<ushort> vtc = Vector128.WidenLower(Vector128.LoadUnsafe(ref rt, (nuint)x));
            Vector128<ushort> vl = Vector128.WidenLower(Vector128.LoadUnsafe(ref rc, (nuint)(x - 1)));
            Vector128<ushort> vuc = Vector128.WidenLower(Vector128.LoadUnsafe(ref rc, (nuint)x));
            Vector128<ushort> avg = vtl + vtc + vl + vuc + Vector128.Create((ushort)8);
            Vector128<ushort> d12 = (avg + ((vtc + vl) << 1)) >> 3;
            Vector128<ushort> d03 = (avg + ((vtl + vuc) << 1)) >> 3;
            Vector128<ushort> tOdd = (d12 + vtl) >> 1;
            Vector128<ushort> tEven = (d03 + vtc) >> 1;
            (tOdd | (tEven << 8)).AsByte().StoreUnsafe(ref rtop, (nuint)((2 * x) - 1));
            if (bottom)
            {
                Vector128<ushort> bOdd = (d03 + vl) >> 1;
                Vector128<ushort> bEven = (d12 + vuc) >> 1;
                (bOdd | (bEven << 8)).AsByte().StoreUnsafe(ref rbot, (nuint)((2 * x) - 1));
            }
        }

        ChromaTail(t, c, width, top, bot, bottom, x);
    }

    /// <summary>Scalar reference for <see cref="Chroma"/>.</summary>
    public static void ChromaScalar(ReadOnlySpan<byte> t, ReadOnlySpan<byte> c, int width, Span<byte> top, Span<byte> bot, bool bottom)
    {
        int lastPair = (width - 1) >> 1;
        t = t[..(lastPair + 1)];
        c = c[..(lastPair + 1)];
        top = top[..width];
        int tl = t[0];
        int l = c[0];
        top[0] = (byte)(((3 * tl) + l + 2) >> 2);
        if (bottom)
        {
            bot = bot[..width];
            bot[0] = (byte)(((3 * l) + tl + 2) >> 2);
        }

        ChromaTail(t, c, width, top, bot, bottom, 1);
    }

    // Pairs x..lastPair one at a time, then the last sample of even widths.
    private static void ChromaTail(ReadOnlySpan<byte> t, ReadOnlySpan<byte> c, int width, Span<byte> top, Span<byte> bot, bool bottom, int x)
    {
        int lastPair = (width - 1) >> 1;
        int tl = t[x - 1];
        int l = c[x - 1];
        top = top[..width];
        if (bottom)
        {
            bot = bot[..width];
        }

        for (; x <= lastPair; x++)
        {
            int tc = t[x];
            int uc = c[x];
            int avg = tl + tc + l + uc + 8;
            int d12 = (avg + (2 * (tc + l))) >> 3;
            int d03 = (avg + (2 * (tl + uc))) >> 3;
            top[(2 * x) - 1] = (byte)((d12 + tl) >> 1);
            top[2 * x] = (byte)((d03 + tc) >> 1);
            if (bottom)
            {
                bot[(2 * x) - 1] = (byte)((d03 + l) >> 1);
                bot[2 * x] = (byte)((d12 + uc) >> 1);
            }

            tl = tc;
            l = uc;
        }

        if ((width & 1) == 0)
        {
            top[width - 1] = (byte)(((3 * tl) + l + 2) >> 2);
            if (bottom)
            {
                bot[width - 1] = (byte)(((3 * l) + tl + 2) >> 2);
            }
        }
    }

    // ---- YUV to RGB ----

    // 14-bit fixed-point BT.601 video-range coefficients, 6 fraction bits left before the clip.
    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    private static byte Clip8(int v) => (byte)((v & ~((256 << 6) - 1)) == 0 ? v >> 6 : v < 0 ? 0 : 255);

    /// <summary>
    /// Converts <paramref name="width"/> pixels of luma and full-resolution chroma to RGB (<paramref name="bpp"/> 3) or RGBA
    /// (<paramref name="bpp"/> 4). With <paramref name="alpha"/> empty the alpha byte is 255.
    /// </summary>
    public static void YuvRow(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, ReadOnlySpan<byte> alpha, Span<byte> dst, int width, int bpp)
    {
        if (width < 0 || (bpp != 3 && bpp != 4) || y.Length < width || u.Length < width || v.Length < width
            || dst.Length < (long)width * bpp || (bpp == 4 && !alpha.IsEmpty && alpha.Length < width))
        {
            throw new ArgumentException("Invalid row.");
        }

        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte ry = ref MemoryMarshal.GetReference(y);
            ref byte ru = ref MemoryMarshal.GetReference(u);
            ref byte rv = ref MemoryMarshal.GetReference(v);
            ref byte rd = ref MemoryMarshal.GetReference(dst);
            ref byte ra = ref MemoryMarshal.GetReference(alpha);
            bool haveAlpha = !alpha.IsEmpty;
            // A block converts 16 pixels: it reads 16 bytes of each plane and writes 16 * bpp bytes.
            for (; x + 16 <= width; x += 16)
            {
                Vector128<byte> by = Vector128.LoadUnsafe(ref ry, (nuint)x);
                Vector128<byte> bu = Vector128.LoadUnsafe(ref ru, (nuint)x);
                Vector128<byte> bv = Vector128.LoadUnsafe(ref rv, (nuint)x);
                ConvertHalf(Vector128.WidenLower(by), Vector128.WidenLower(bu), Vector128.WidenLower(bv), out Vector128<ushort> r0, out Vector128<ushort> g0, out Vector128<ushort> b0);
                ConvertHalf(Vector128.WidenUpper(by), Vector128.WidenUpper(bu), Vector128.WidenUpper(bv), out Vector128<ushort> r1, out Vector128<ushort> g1, out Vector128<ushort> b1);
                Vector128<byte> r = Vector128.Narrow(r0, r1);
                Vector128<byte> g = Vector128.Narrow(g0, g1);
                Vector128<byte> b = Vector128.Narrow(b0, b1);
                if (bpp == 3)
                {
                    ref byte o = ref Unsafe.Add(ref rd, x * 3);
                    Interleave(Rgb24Masks, 3, r, g, b, default).CopyTo3(ref o);
                }
                else
                {
                    Vector128<byte> a = haveAlpha ? Vector128.LoadUnsafe(ref ra, (nuint)x) : Vector128.Create((byte)255);
                    ref byte o = ref Unsafe.Add(ref rd, x * 4);
                    Interleave(Rgba32Masks, 4, r, g, b, a).CopyTo4(ref o);
                }
            }
        }

        YuvTail(y, u, v, alpha, dst, width, bpp, x);
    }

    /// <summary>Scalar reference for <see cref="YuvRow"/>.</summary>
    public static void YuvRowScalar(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, ReadOnlySpan<byte> alpha, Span<byte> dst, int width, int bpp) =>
        YuvTail(y, u, v, alpha, dst, width, bpp, 0);

    private static void YuvTail(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, ReadOnlySpan<byte> alpha, Span<byte> dst, int width, int bpp, int x)
    {
        y = y[..width];
        u = u[..width];
        v = v[..width];
        dst = dst[..(width * bpp)];
        int o = x * bpp;
        for (; x < width; x++, o += bpp)
        {
            int yy = MultHi(y[x], 19077);
            int uu = u[x];
            int vv = v[x];
            dst[o] = Clip8(yy + MultHi(vv, 26149) - 14234);
            dst[o + 1] = Clip8(yy - MultHi(uu, 6419) - MultHi(vv, 13320) + 8708);
            dst[o + 2] = Clip8(yy + MultHi(uu, 33050) - 17685);
            if (bpp == 4)
            {
                dst[o + 3] = alpha.IsEmpty ? (byte)255 : alpha[x];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> MulHi(Vector128<ushort> a, ushort coeff)
    {
        // (a * coeff) >> 16 on the 8-bit value moved into the high byte equals (v * coeff) >> 8.
        if (Sse2.IsSupported)
        {
            return Sse2.MultiplyHigh(a, Vector128.Create(coeff));
        }

        (Vector128<uint> lo, Vector128<uint> hi) = Vector128.Widen(a);
        Vector128<uint> c = Vector128.Create((uint)coeff);
        return Vector128.Narrow((lo * c) >> 16, (hi * c) >> 16);
    }

    // Unsigned subtract that stops at zero.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> SubSat(Vector128<ushort> a, Vector128<ushort> b) => Vector128.Max(a, b) - b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertHalf(Vector128<ushort> y8, Vector128<ushort> u8, Vector128<ushort> v8, out Vector128<ushort> r, out Vector128<ushort> g, out Vector128<ushort> b)
    {
        Vector128<ushort> yy = MulHi(y8 << 8, 19077);
        Vector128<ushort> u = u8 << 8;
        Vector128<ushort> v = v8 << 8;
        Vector128<ushort> max = Vector128.Create((ushort)255);
        // Sums stay within 16 bits (largest: 19002 + 32923); subtraction stops at zero, which is what the scalar clip does.
        r = Vector128.Min(SubSat(yy + MulHi(v, 26149), Vector128.Create((ushort)14234)) >> 6, max);
        g = Vector128.Min(SubSat(yy + Vector128.Create((ushort)8708), MulHi(u, 6419) + MulHi(v, 13320)) >> 6, max);
        b = Vector128.Min(SubSat(yy + MulHi(u, 33050), Vector128.Create((ushort)17685)) >> 6, max);
    }

    private readonly struct Packed
    {
        public Packed(Vector128<byte> a, Vector128<byte> b, Vector128<byte> c, Vector128<byte> d)
        {
            A = a;
            B = b;
            C = c;
            D = d;
        }

        public Vector128<byte> A { get; }

        public Vector128<byte> B { get; }

        public Vector128<byte> C { get; }

        public Vector128<byte> D { get; }

        public void CopyTo3(ref byte o)
        {
            A.StoreUnsafe(ref o);
            B.StoreUnsafe(ref o, 16);
            C.StoreUnsafe(ref o, 32);
        }

        public void CopyTo4(ref byte o)
        {
            A.StoreUnsafe(ref o);
            B.StoreUnsafe(ref o, 16);
            C.StoreUnsafe(ref o, 32);
            D.StoreUnsafe(ref o, 48);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuf(Vector128<byte> v, Vector128<byte> mask) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(v, mask) : Vector128.Shuffle(v, mask);

    private static Packed Interleave(Vector128<byte>[] m, int channels, Vector128<byte> r, Vector128<byte> g, Vector128<byte> b, Vector128<byte> a)
    {
        Vector128<byte> s0 = Shuf(r, m[0]) | Shuf(g, m[1]) | Shuf(b, m[2]);
        Vector128<byte> s1 = Shuf(r, m[channels]) | Shuf(g, m[channels + 1]) | Shuf(b, m[channels + 2]);
        Vector128<byte> s2 = Shuf(r, m[2 * channels]) | Shuf(g, m[(2 * channels) + 1]) | Shuf(b, m[(2 * channels) + 2]);
        if (channels == 3)
        {
            return new Packed(s0, s1, s2, default);
        }

        s0 |= Shuf(a, m[3]);
        s1 |= Shuf(a, m[channels + 3]);
        s2 |= Shuf(a, m[(2 * channels) + 3]);
        Vector128<byte> s3 = Shuf(r, m[3 * channels]) | Shuf(g, m[(3 * channels) + 1]) | Shuf(b, m[(3 * channels) + 2]) | Shuf(a, m[(3 * channels) + 3]);
        return new Packed(s0, s1, s2, s3);
    }
}
