using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>
/// Quantization of one 4x4 block of VP8 coefficients. The vector path quantizes all 16 coefficients in 32-bit lanes with the
/// same integer steps as <see cref="Scalar"/>, so both give identical levels. It needs reciprocals of at most 2^15 (steps of
/// 4 or more), which keeps every product below 2^31; <see cref="Scalar"/> serves smaller steps.
/// </summary>
internal static class WebPQuantKernel
{
    private const int QFix = 17;
    private const int MaxLevel = 2047;

    /// <summary>The largest reciprocal the vector path accepts.</summary>
    public const int MaxVectorReciprocal = 1 << 15;

    private static readonly byte[] Zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    /// <summary>
    /// Quantizes <paramref name="coeffs"/> (raster order). Levels go to <paramref name="levels"/> in zigzag order, the
    /// dequantized values replace the coefficients, and the result is the zigzag index of the last nonzero level or -1.
    /// Positions before <paramref name="first"/> (in zigzag order) are left untouched.
    /// </summary>
    /// <param name="coeffs">16 coefficients.</param>
    /// <param name="levels">16 levels.</param>
    /// <param name="first">0, or 1 to skip the DC position.</param>
    /// <param name="q">Step per raster position.</param>
    /// <param name="iq">Reciprocal per raster position (2^17 / step).</param>
    /// <param name="bias">Rounding bias per raster position.</param>
    /// <param name="sharpen">Value added to the magnitude before quantizing, per raster position.</param>
    /// <param name="vectorSafe">Whether every reciprocal is at most <see cref="MaxVectorReciprocal"/>.</param>
    public static int Quantize(Span<short> coeffs, Span<short> levels, int first, ReadOnlySpan<int> q, ReadOnlySpan<int> iq, ReadOnlySpan<int> bias, ReadOnlySpan<int> sharpen, bool vectorSafe)
    {
        if (coeffs.Length < 16 || levels.Length < 16 || q.Length < 16 || iq.Length < 16 || bias.Length < 16 || sharpen.Length < 16 || (uint)first > 1)
        {
            throw new ArgumentException("Invalid block.");
        }

        if (vectorSafe && Vector128.IsHardwareAccelerated)
        {
            return QuantizeVector(coeffs, levels, first, q, iq, bias, sharpen);
        }

        return Scalar(coeffs, levels, first, q, iq, bias, sharpen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Lane(Vector128<int> v, ReadOnlySpan<int> q, ReadOnlySpan<int> iq, ReadOnlySpan<int> bias, ReadOnlySpan<int> sharpen, int at, out Vector128<int> level)
    {
        Vector128<int> neg = Vector128.LessThan(v, Vector128<int>.Zero);
        Vector128<int> c = Vector128.Abs(v) + Vector128.Create(sharpen.Slice(at, 4));
        Vector128<int> l = Vector128.Min(Vector128.ShiftRightLogical((c * Vector128.Create(iq.Slice(at, 4))) + Vector128.Create(bias.Slice(at, 4)), QFix), Vector128.Create(MaxLevel));
        level = (l ^ neg) - neg;
        return level * Vector128.Create(q.Slice(at, 4));
    }

    private static int QuantizeVector(Span<short> coeffs, Span<short> levels, int first, ReadOnlySpan<int> q, ReadOnlySpan<int> iq, ReadOnlySpan<int> bias, ReadOnlySpan<int> sharpen)
    {
        short savedDc = coeffs[0];
        (Vector128<int> v0, Vector128<int> v1) = Vector128.Widen(Vector128.Create<short>(coeffs));
        (Vector128<int> v2, Vector128<int> v3) = Vector128.Widen(Vector128.Create<short>(coeffs[8..]));
        Vector128<int> d0 = Lane(v0, q, iq, bias, sharpen, 0, out Vector128<int> l0);
        Vector128<int> d1 = Lane(v1, q, iq, bias, sharpen, 4, out Vector128<int> l1);
        Vector128<int> d2 = Lane(v2, q, iq, bias, sharpen, 8, out Vector128<int> l2);
        Vector128<int> d3 = Lane(v3, q, iq, bias, sharpen, 12, out Vector128<int> l3);
        Vector128.Narrow(d0, d1).CopyTo(coeffs);
        Vector128.Narrow(d2, d3).CopyTo(coeffs[8..]);
        Span<short> raster = stackalloc short[16];
        Vector128.Narrow(l0, l1).CopyTo(raster);
        Vector128.Narrow(l2, l3).CopyTo(raster[8..]);
        if (first == 1)
        {
            // The DC position belongs to another block (the luma DC transform); leave it as it was.
            coeffs[0] = savedDc;
        }

        int last = -1;
        for (int n = first; n < 16; n++)
        {
            short l = raster[Zigzag[n]];
            levels[n] = l;
            if (l != 0)
            {
                last = n;
            }
        }

        return last;
    }

    /// <summary>The reference implementation.</summary>
    public static int Scalar(Span<short> coeffs, Span<short> levels, int first, ReadOnlySpan<int> q, ReadOnlySpan<int> iq, ReadOnlySpan<int> bias, ReadOnlySpan<int> sharpen)
    {
        int last = -1;
        for (int n = first; n < 16; n++)
        {
            int j = Zigzag[n];
            int v = coeffs[j];
            bool neg = v < 0;
            int c = (neg ? -v : v) + sharpen[j];
            int level = (int)Math.Min(((long)c * iq[j] + bias[j]) >> QFix, MaxLevel);
            if (level != 0)
            {
                last = n;
                coeffs[j] = (short)((neg ? -level : level) * q[j]);
                levels[n] = (short)(neg ? -level : level);
            }
            else
            {
                coeffs[j] = 0;
                levels[n] = 0;
            }
        }

        return last;
    }
}
