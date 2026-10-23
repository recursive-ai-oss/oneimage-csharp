using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Kernels;

/// <summary>
/// Pixel conversions of the SVG filter buffers (premultiplied float RGBA, sRGB or linear light) with AVX2: two pixels per step, the sRGB transfer
/// curve looked up in a 4097-entry table with linear interpolation (gathers). Each step performs the same IEEE operations in the same order as the
/// scalar code that calls it, so results are bit for bit the scalar results. A kernel stops at the first pair of pixels that holds a NaN where
/// the scalar code's NaN rules matter and returns how many pixels it finished; the caller does the rest with its scalar code.
/// </summary>
internal static unsafe class FilterColorKernel
{
    /// <summary>Entries a transfer table must have (4096 steps, the last repeated).</summary>
    public const int TableLength = 4098;

    /// <summary>Whether the kernels can run on this machine.</summary>
    public static bool IsSupported => Avx2.IsSupported;

    /// <summary>
    /// Converts premultiplied pixels between sRGB and linear light: un-premultiply, clamp to 1, transfer curve, premultiply again; pixels whose
    /// alpha is not above zero become transparent black. Returns the number of pixels done (a multiple of 2).
    /// </summary>
    public static int Convert(ReadOnlySpan<float> src, Span<float> dst, int pixels, ReadOnlySpan<float> table)
    {
        Check(src.Length / 4, dst.Length / 4, pixels, table.Length);
        fixed (float* s = src)
        fixed (float* d = dst)
        fixed (float* t = table)
        {
            Vector256<float> one = Vector256.Create(1f);
            int p = 0;
            for (; p + 2 <= pixels; p += 2)
            {
                Vector256<float> v = Avx.LoadVector256(s + (p * 4));
                Vector256<float> a = Avx.Shuffle(v, v, 0xFF);
                Vector256<float> pos = Avx.Compare(a, Vector256<float>.Zero, FloatComparisonMode.OrderedGreaterThanNonSignaling);
                Vector256<float> x = Avx.Multiply(v, Avx.Divide(one, a));
                if (HasNaN(x, pos))
                {
                    break;
                }

                Vector256<float> y = Lookup(Avx.Min(x, one), t);
                Vector256<float> o = Avx.Blend(Avx.Multiply(y, a), a, 0x88);
                Avx.Store(d + (p * 4), Avx.And(o, pos));
            }

            return p;
        }
    }

    /// <summary>
    /// Writes premultiplied pixels as straight 8-bit RGBA: alpha clamped to 1, un-premultiplied, through the transfer curve when
    /// <paramref name="table"/> is not empty, rounded once. Pixels whose alpha is not above zero become zero. Returns the number of pixels done (a multiple of 2).
    /// </summary>
    public static int ToRgba(ReadOnlySpan<float> src, Span<Rgba32> dst, int pixels, ReadOnlySpan<float> table)
    {
        Check(src.Length / 4, dst.Length, pixels, table.IsEmpty ? TableLength : table.Length);
        bool curve = !table.IsEmpty;
        fixed (float* s = src)
        fixed (Rgba32* d = dst)
        fixed (float* t = table)
        {
            Vector256<float> one = Vector256.Create(1f);
            Vector256<float> k255 = Vector256.Create(255f);
            Vector256<float> half = Vector256.Create(0.5f);
            int p = 0;
            for (; p + 2 <= pixels; p += 2)
            {
                Vector256<float> v = Avx.LoadVector256(s + (p * 4));
                Vector256<float> a = Avx.Shuffle(v, v, 0xFF);
                Vector256<float> pos = Avx.Compare(a, Vector256<float>.Zero, FloatComparisonMode.OrderedGreaterThanNonSignaling);
                Vector256<float> ac = Avx.Min(a, one);
                Vector256<float> x = Avx.Multiply(v, Avx.Divide(one, ac));
                if (HasNaN(x, pos))
                {
                    break;
                }

                Vector256<float> c = Avx.Min(x, one);
                if (curve)
                {
                    c = Lookup(c, t);
                }

                c = Avx.Blend(c, ac, 0x88);
                c = Avx.Min(Avx.Max(c, Vector256<float>.Zero), one);
                Vector256<int> i = Avx.ConvertToVector256Int32WithTruncation(Avx.Add(Avx.Multiply(c, k255), half));
                i = Avx2.And(i, pos.AsInt32());
                Vector256<short> w = Avx2.PackSignedSaturate(i, i);
                Vector256<uint> b = Avx2.PackUnsignedSaturate(w, w).AsUInt32();
                Sse2.StoreScalar((double*)(d + p), Sse2.UnpackLow(b.GetLower(), b.GetUpper()).AsDouble());
            }

            return p;
        }
    }

    /// <summary>
    /// Reads straight 8-bit RGBA as premultiplied float: each channel divided by 255, color through the transfer curve when
    /// <paramref name="table"/> is not empty, then multiplied by alpha. Returns the number of pixels done (a multiple of 2).
    /// </summary>
    public static int FromRgba(ReadOnlySpan<Rgba32> src, Span<float> dst, int pixels, ReadOnlySpan<float> table)
    {
        Check(src.Length, dst.Length / 4, pixels, table.IsEmpty ? TableLength : table.Length);
        bool curve = !table.IsEmpty;
        fixed (Rgba32* s = src)
        fixed (float* d = dst)
        fixed (float* t = table)
        {
            Vector256<float> k255 = Vector256.Create(255f);
            int p = 0;
            for (; p + 2 <= pixels; p += 2)
            {
                Vector256<float> v = Avx.Divide(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32((byte*)(s + p))), k255);
                Vector256<float> a = Avx.Shuffle(v, v, 0xFF);
                Vector256<float> c = curve ? Lookup(v, t) : v;
                Avx.Store(d + (p * 4), Avx.Blend(Avx.Multiply(c, a), a, 0x88));
            }

            return p;
        }
    }

    /// <summary>
    /// <see cref="FromRgba"/> without a curve followed by <see cref="Convert"/> in one step, with the same roundings: the premultiplied value is
    /// rounded to float, divided by alpha again, clamped, put through <paramref name="table"/> and premultiplied. The 8-bit input cannot make a NaN.
    /// Returns the number of pixels done (a multiple of 2).
    /// </summary>
    public static int FromRgbaConvert(ReadOnlySpan<Rgba32> src, Span<float> dst, int pixels, ReadOnlySpan<float> table)
    {
        Check(src.Length, dst.Length / 4, pixels, table.Length);
        fixed (Rgba32* s = src)
        fixed (float* d = dst)
        fixed (float* t = table)
        {
            Vector256<float> one = Vector256.Create(1f);
            Vector256<float> k255 = Vector256.Create(255f);
            int p = 0;
            for (; p + 2 <= pixels; p += 2)
            {
                Vector256<float> v = Avx.Divide(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32((byte*)(s + p))), k255);
                Vector256<float> a = Avx.Shuffle(v, v, 0xFF);
                Vector256<float> pos = Avx.Compare(a, Vector256<float>.Zero, FloatComparisonMode.OrderedGreaterThanNonSignaling);
                Vector256<float> premul = Avx.Multiply(v, a);
                Vector256<float> x = Avx.Multiply(premul, Avx.Divide(one, a));
                Vector256<float> y = Lookup(Avx.Min(x, one), t);
                Vector256<float> o = Avx.Blend(Avx.Multiply(y, a), a, 0x88);
                Avx.Store(d + (p * 4), Avx.And(o, pos));
            }

            return p;
        }
    }

    private static void Check(int srcPixels, int dstPixels, int pixels, int tableLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pixels);
        if (srcPixels < pixels || dstPixels < pixels || tableLength != TableLength)
        {
            throw new ArgumentException("The buffers do not fit the pixel count.");
        }
    }

    // True when a pixel that will be converted (its alpha is above zero) has a NaN in its un-premultiplied color: the scalar code's
    // Math.Min and Math.Clamp pass NaN through where the vector Min and Max return their second operand.
    private static bool HasNaN(Vector256<float> x, Vector256<float> pos) =>
        Avx.MoveMask(Avx.And(Avx.Compare(x, x, FloatComparisonMode.UnorderedNonSignaling), pos)) != 0;

    // The table lookup of the scalar code for eight values: below or at 0 (and NaN) gives 0, from 1 up gives 1, otherwise interpolates.
    private static Vector256<float> Lookup(Vector256<float> v, float* table)
    {
        Vector256<float> zero = Vector256<float>.Zero;
        Vector256<float> one = Vector256.Create(1f);
        Vector256<float> gt0 = Avx.Compare(v, zero, FloatComparisonMode.OrderedGreaterThanNonSignaling);
        Vector256<float> ge1 = Avx.Compare(v, one, FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling);
        Vector256<float> steps = Vector256.Create(4096f);
        Vector256<float> f = Avx.Multiply(v, steps);
        Vector256<int> i = Avx.ConvertToVector256Int32WithTruncation(Avx.Min(Avx.Max(f, zero), steps));
        Vector256<float> frac = Avx.Subtract(f, Avx.ConvertToVector256Single(i));

        // Each entry and its successor are one 8-byte element, so two 4-element gathers fetch what 16 single-float loads would: the first
        // takes red and green of both pixels, the second blue and alpha, and the shuffles split every element back into the two entries.
        Vector128<int> lo = i.GetLower();
        Vector128<int> hi = i.GetUpper();
        Vector256<float> ga = Avx2.GatherVector256((double*)table, Sse2.UnpackLow(lo.AsInt64(), hi.AsInt64()).AsInt32(), 4).AsSingle();
        Vector256<float> gb = Avx2.GatherVector256((double*)table, Sse2.UnpackHigh(lo.AsInt64(), hi.AsInt64()).AsInt32(), 4).AsSingle();
        Vector256<float> t0 = Avx.Shuffle(ga, gb, 0x88);
        Vector256<float> t1 = Avx.Shuffle(ga, gb, 0xDD);
        Vector256<float> r = Avx.Add(t0, Avx.Multiply(Avx.Subtract(t1, t0), frac));
        return Avx.And(Avx.BlendVariable(r, one, ge1), gt0);
    }
}
