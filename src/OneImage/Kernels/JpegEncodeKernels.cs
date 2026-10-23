using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// Pixel preparation for JPEG encoding: RGB to YCbCr conversion and chroma averaging. The scalar loops are the reference
/// and the vector paths produce the same bytes. Callers validate span lengths; kernels never parse.
/// </summary>
internal static class JpegEncodeKernels
{
    // Deinterleave masks: for output channel c, input vector k (0..2) of a 48-byte run of 16 RGB pixels.
    private static readonly Vector128<byte> s_r0 = Mask(0, 0), s_r1 = Mask(1, 0), s_r2 = Mask(2, 0);
    private static readonly Vector128<byte> s_g0 = Mask(0, 1), s_g1 = Mask(1, 1), s_g2 = Mask(2, 1);
    private static readonly Vector128<byte> s_b0 = Mask(0, 2), s_b1 = Mask(1, 2), s_b2 = Mask(2, 2);

    private static Vector128<byte> Mask(int part, int channel)
    {
        Span<byte> m = stackalloc byte[16];
        for (int j = 0; j < 16; j++)
        {
            int p = (3 * j) + channel;
            m[j] = p / 16 == part ? (byte)(p % 16) : (byte)0x80;
        }

        return Vector128.Create((ReadOnlySpan<byte>)m);
    }

    private static bool UseSsse3 => Ssse3.IsSupported && Sse41.IsSupported;

    /// <summary>
    /// Converts interleaved RGB to full-range YCbCr (the 16-bit fixed-point JFIF matrix with rounding) for
    /// <paramref name="count"/> pixels; the outputs are planar.
    /// </summary>
    public static void RgbToYCbCr(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr, int count)
    {
        if (rgb.Length < 3 * count || y.Length < count || cb.Length < count || cr.Length < count)
        {
            throw new ArgumentException("RGB conversion buffers too small.");
        }

        int x = 0;
        if (UseSsse3)
        {
            x = RgbToYCbCrSse(rgb, y, cb, cr, count);
        }

        RgbToYCbCrScalar(rgb, y, cb, cr, x, count);
    }

    internal static void RgbToYCbCrScalar(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr, int from, int count)
    {
        for (int x = from; x < count; x++)
        {
            int r = rgb[3 * x];
            int g = rgb[(3 * x) + 1];
            int b = rgb[(3 * x) + 2];
            y[x] = (byte)(((19595 * r) + (38470 * g) + (7471 * b) + 32768) >> 16);
            cb[x] = (byte)(((-11059 * r) - (21709 * g) + (32768 * b) + (128 << 16) + 32767) >> 16);
            cr[x] = (byte)(((32768 * r) - (27439 * g) - (5329 * b) + (128 << 16) + 32767) >> 16);
        }
    }

    /// <summary>
    /// Sixteen pixels per step with 16-bit multiply-adds. Coefficients above 32767 are split so each fits a signed 16-bit
    /// lane: g*38470 = g*(-27066) + (g &lt;&lt; 16), b*32768 = b*(-32768) + (b &lt;&lt; 16), r*32768 = r*(-32768) + (r &lt;&lt; 16).
    /// The shifted parts are multiples of 65536, so they commute with the final arithmetic shift and are added after it.
    /// </summary>
    private static int RgbToYCbCrSse(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr, int count)
    {
        // (r,g) pair coefficients and (b,0) coefficients, as interleaved int16 lanes.
        Vector128<short> yRg = Vector128.Create((short)19595, (short)-27066, 19595, -27066, 19595, -27066, 19595, -27066);
        Vector128<short> yB = Vector128.Create((short)7471, 0, 7471, 0, 7471, 0, 7471, 0);
        Vector128<short> cbRg = Vector128.Create((short)-11059, (short)-21709, -11059, -21709, -11059, -21709, -11059, -21709);
        Vector128<short> cbB = Vector128.Create((short)-32768, 0, -32768, 0, -32768, 0, -32768, 0);
        Vector128<short> crRg = Vector128.Create((short)-32768, (short)-27439, -32768, -27439, -32768, -27439, -32768, -27439);
        Vector128<short> crB = Vector128.Create((short)-5329, 0, -5329, 0, -5329, 0, -5329, 0);
        Vector128<int> yK = Vector128.Create(32768);
        Vector128<int> cK = Vector128.Create((128 << 16) + 32767);
        Vector128<int> low16 = Vector128.Create(0xFFFF);
        Vector128<short> zero = Vector128<short>.Zero;

        ref byte src = ref MemoryMarshal.GetReference(rgb);
        ref byte py = ref MemoryMarshal.GetReference(y);
        ref byte pcb = ref MemoryMarshal.GetReference(cb);
        ref byte pcr = ref MemoryMarshal.GetReference(cr);
        int x = 0;
        for (; x + 16 <= count; x += 16)
        {
            Vector128<byte> v0 = Vector128.LoadUnsafe(ref src, (nuint)(3 * x));
            Vector128<byte> v1 = Vector128.LoadUnsafe(ref src, (nuint)((3 * x) + 16));
            Vector128<byte> v2 = Vector128.LoadUnsafe(ref src, (nuint)((3 * x) + 32));
            Vector128<byte> r = Vector128.Shuffle(v0, s_r0) | Vector128.Shuffle(v1, s_r1) | Vector128.Shuffle(v2, s_r2);
            Vector128<byte> g = Vector128.Shuffle(v0, s_g0) | Vector128.Shuffle(v1, s_g1) | Vector128.Shuffle(v2, s_g2);
            Vector128<byte> b = Vector128.Shuffle(v0, s_b0) | Vector128.Shuffle(v1, s_b1) | Vector128.Shuffle(v2, s_b2);

            Vector128<short> rLo = Vector128.WidenLower(r).AsInt16();
            Vector128<short> rHi = Vector128.WidenUpper(r).AsInt16();
            Vector128<short> gLo = Vector128.WidenLower(g).AsInt16();
            Vector128<short> gHi = Vector128.WidenUpper(g).AsInt16();
            Vector128<short> bLo = Vector128.WidenLower(b).AsInt16();
            Vector128<short> bHi = Vector128.WidenUpper(b).AsInt16();

            Convert4(Sse2.UnpackLow(rLo, gLo), Sse2.UnpackLow(bLo, zero), yRg, yB, cbRg, cbB, crRg, crB, yK, cK, low16, out Vector128<int> y0, out Vector128<int> cb0, out Vector128<int> cr0);
            Convert4(Sse2.UnpackHigh(rLo, gLo), Sse2.UnpackHigh(bLo, zero), yRg, yB, cbRg, cbB, crRg, crB, yK, cK, low16, out Vector128<int> y1, out Vector128<int> cb1, out Vector128<int> cr1);
            Convert4(Sse2.UnpackLow(rHi, gHi), Sse2.UnpackLow(bHi, zero), yRg, yB, cbRg, cbB, crRg, crB, yK, cK, low16, out Vector128<int> y2, out Vector128<int> cb2, out Vector128<int> cr2);
            Convert4(Sse2.UnpackHigh(rHi, gHi), Sse2.UnpackHigh(bHi, zero), yRg, yB, cbRg, cbB, crRg, crB, yK, cK, low16, out Vector128<int> y3, out Vector128<int> cb3, out Vector128<int> cr3);

            // The results are in 0..255, so the saturating packs lose nothing.
            Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(y0, y1), Sse2.PackSignedSaturate(y2, y3)).StoreUnsafe(ref py, (nuint)x);
            Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(cb0, cb1), Sse2.PackSignedSaturate(cb2, cb3)).StoreUnsafe(ref pcb, (nuint)x);
            Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(cr0, cr1), Sse2.PackSignedSaturate(cr2, cr3)).StoreUnsafe(ref pcr, (nuint)x);
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Convert4(
        Vector128<short> rg, Vector128<short> b0,
        Vector128<short> yRg, Vector128<short> yB, Vector128<short> cbRg, Vector128<short> cbB, Vector128<short> crRg, Vector128<short> crB,
        Vector128<int> yK, Vector128<int> cK, Vector128<int> low16,
        out Vector128<int> y, out Vector128<int> cb, out Vector128<int> cr)
    {
        // rg holds (r,g) per 32-bit lane and b0 holds (b,0): read as ints they are r + (g << 16) and b.
        Vector128<int> g = Vector128.ShiftRightLogical(rg.AsInt32(), 16);
        Vector128<int> r = rg.AsInt32() & low16;
        Vector128<int> b = b0.AsInt32();
        y = Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(rg, yRg) + Sse2.MultiplyAddAdjacent(b0, yB) + yK, 16) + g;
        cb = Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(rg, cbRg) + Sse2.MultiplyAddAdjacent(b0, cbB) + cK, 16) + b;
        cr = Vector128.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(rg, crRg) + Sse2.MultiplyAddAdjacent(b0, crB) + cK, 16) + r;
    }

    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Averages 2x2 blocks of two source rows into <paramref name="dst"/> (<paramref name="count"/> outputs), rounding with
    /// the alternating 1,2 bias that keeps the result unbiased. <paramref name="a"/> and <paramref name="b"/> hold 2*count samples.
    /// </summary>
    public static void Average2x2(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst, int count)
    {
        if (a.Length < 2 * count || b.Length < 2 * count || dst.Length < count)
        {
            throw new ArgumentException("Downsample buffers too small.");
        }

        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte pa = ref MemoryMarshal.GetReference(a);
            ref byte pb = ref MemoryMarshal.GetReference(b);
            ref byte pd = ref MemoryMarshal.GetReference(dst);
            Vector128<ushort> bias = Vector128.Create((ushort)1, 2, 1, 2, 1, 2, 1, 2);
            Vector128<ushort> lowByte = Vector128.Create((ushort)0x00FF);
            for (; x + 16 <= count; x += 16)
            {
                Vector128<ushort> a0 = Vector128.LoadUnsafe(ref pa, (nuint)(2 * x)).AsUInt16();
                Vector128<ushort> a1 = Vector128.LoadUnsafe(ref pa, (nuint)((2 * x) + 16)).AsUInt16();
                Vector128<ushort> b0 = Vector128.LoadUnsafe(ref pb, (nuint)(2 * x)).AsUInt16();
                Vector128<ushort> b1 = Vector128.LoadUnsafe(ref pb, (nuint)((2 * x) + 16)).AsUInt16();
                Vector128<ushort> s0 = (a0 & lowByte) + Vector128.ShiftRightLogical(a0, 8) + (b0 & lowByte) + Vector128.ShiftRightLogical(b0, 8) + bias;
                Vector128<ushort> s1 = (a1 & lowByte) + Vector128.ShiftRightLogical(a1, 8) + (b1 & lowByte) + Vector128.ShiftRightLogical(b1, 8) + bias;
                Vector128.Narrow(Vector128.ShiftRightLogical(s0, 2), Vector128.ShiftRightLogical(s1, 2)).StoreUnsafe(ref pd, (nuint)x);
            }
        }

        for (; x < count; x++)
        {
            // Even output positions use bias 1, odd ones 2 (the pattern restarts at 0 for each row).
            int bi = (x & 1) == 0 ? 1 : 2;
            dst[x] = (byte)((a[2 * x] + a[(2 * x) + 1] + b[2 * x] + b[(2 * x) + 1] + bi) >> 2);
        }
    }

    /// <summary>Averages horizontal pairs of <paramref name="a"/> into <paramref name="dst"/>, rounding with the alternating 0,1 bias.</summary>
    public static void Average2x1(ReadOnlySpan<byte> a, Span<byte> dst, int count)
    {
        if (a.Length < 2 * count || dst.Length < count)
        {
            throw new ArgumentException("Downsample buffers too small.");
        }

        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte pa = ref MemoryMarshal.GetReference(a);
            ref byte pd = ref MemoryMarshal.GetReference(dst);
            Vector128<ushort> bias = Vector128.Create((ushort)0, 1, 0, 1, 0, 1, 0, 1);
            Vector128<ushort> lowByte = Vector128.Create((ushort)0x00FF);
            for (; x + 16 <= count; x += 16)
            {
                Vector128<ushort> a0 = Vector128.LoadUnsafe(ref pa, (nuint)(2 * x)).AsUInt16();
                Vector128<ushort> a1 = Vector128.LoadUnsafe(ref pa, (nuint)((2 * x) + 16)).AsUInt16();
                Vector128<ushort> s0 = (a0 & lowByte) + Vector128.ShiftRightLogical(a0, 8) + bias;
                Vector128<ushort> s1 = (a1 & lowByte) + Vector128.ShiftRightLogical(a1, 8) + bias;
                Vector128.Narrow(Vector128.ShiftRightLogical(s0, 1), Vector128.ShiftRightLogical(s1, 1)).StoreUnsafe(ref pd, (nuint)x);
            }
        }

        for (; x < count; x++)
        {
            dst[x] = (byte)((a[2 * x] + a[(2 * x) + 1] + (x & 1)) >> 1);
        }
    }

    /// <summary>Averages vertical pairs of two rows into <paramref name="dst"/>, rounding up.</summary>
    public static void Average1x2(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst, int count)
    {
        if (a.Length < count || b.Length < count || dst.Length < count)
        {
            throw new ArgumentException("Downsample buffers too small.");
        }

        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte pa = ref MemoryMarshal.GetReference(a);
            ref byte pb = ref MemoryMarshal.GetReference(b);
            ref byte pd = ref MemoryMarshal.GetReference(dst);
            Vector128<ushort> one = Vector128.Create((ushort)1);
            for (; x + 16 <= count; x += 16)
            {
                Vector128<byte> va = Vector128.LoadUnsafe(ref pa, (nuint)x);
                Vector128<byte> vb = Vector128.LoadUnsafe(ref pb, (nuint)x);
                Vector128<ushort> lo = Vector128.ShiftRightLogical(Vector128.WidenLower(va) + Vector128.WidenLower(vb) + one, 1);
                Vector128<ushort> hi = Vector128.ShiftRightLogical(Vector128.WidenUpper(va) + Vector128.WidenUpper(vb) + one, 1);
                Vector128.Narrow(lo, hi).StoreUnsafe(ref pd, (nuint)x);
            }
        }

        for (; x < count; x++)
        {
            dst[x] = (byte)((a[x] + b[x] + 1) >> 1);
        }
    }
}
