using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OneImage.Kernels;

/// <summary>
/// Weighted sums over float rows, the one primitive the neighbourhood filters are built from. Every output value is
/// <c>sum over taps of weight * source[offset + j]</c> accumulated in tap order with a separate multiply and add (never fused), and
/// vector lanes are independent, so the vector and scalar paths give bit-identical results at any vector width.
/// Callers pass tap offsets that index into <c>src</c>; each kernel checks once, before the loops, that every tap stays inside
/// the source for the whole destination length.
/// </summary>
internal static class FilterKernel
{
    /// <summary>Limits every value to [0, 1] in place (NaN becomes 0).</summary>
    public static void Clamp01(Span<float> values)
    {
        int vc = Vector<float>.Count;
        int i = 0;
        for (; i + vc <= values.Length; i += vc)
        {
            Vector.Min(Vector.Max(new Vector<float>(values.Slice(i, vc)), Vector<float>.Zero), Vector<float>.One).CopyTo(values.Slice(i, vc));
        }

        for (; i < values.Length; i++)
        {
            float v = values[i];
            values[i] = v > 0f ? (v < 1f ? v : 1f) : 0f;
        }
    }

    /// <summary><c>dst[j] = sum_t weights[t] * src[offsets[t] + j]</c>, taps in ascending order.</summary>
    public static void SumTaps(ReadOnlySpan<float> src, ReadOnlySpan<int> offsets, ReadOnlySpan<float> weights, Span<float> dst)
    {
        Check(src.Length, offsets, weights.Length, dst.Length);
        if (!Vector.IsHardwareAccelerated || dst.Length < Vector<float>.Count)
        {
            SumTapsScalar(src, offsets, weights, dst);
            return;
        }

        int vc = Vector<float>.Count;
        int n = dst.Length;
        int taps = offsets.Length;
        ref float s = ref MemoryMarshal.GetReference(src);
        ref float d = ref MemoryMarshal.GetReference(dst);
        ref int o = ref MemoryMarshal.GetReference(offsets);
        ref float w = ref MemoryMarshal.GetReference(weights);
        int j = 0;
        for (; j + (4 * vc) <= n; j += 4 * vc)
        {
            Vector<float> a0 = default;
            Vector<float> a1 = default;
            Vector<float> a2 = default;
            Vector<float> a3 = default;
            for (int t = 0; t < taps; t++)
            {
                var wv = new Vector<float>(Unsafe.Add(ref w, t));
                ref float p = ref Unsafe.Add(ref s, Unsafe.Add(ref o, t) + j);
                a0 += Vector.LoadUnsafe(ref p) * wv;
                a1 += Vector.LoadUnsafe(ref p, (nuint)vc) * wv;
                a2 += Vector.LoadUnsafe(ref p, (nuint)(2 * vc)) * wv;
                a3 += Vector.LoadUnsafe(ref p, (nuint)(3 * vc)) * wv;
            }

            a0.StoreUnsafe(ref d, (nuint)j);
            a1.StoreUnsafe(ref d, (nuint)(j + vc));
            a2.StoreUnsafe(ref d, (nuint)(j + (2 * vc)));
            a3.StoreUnsafe(ref d, (nuint)(j + (3 * vc)));
        }

        for (; j + vc <= n; j += vc)
        {
            Vector<float> a = default;
            for (int t = 0; t < taps; t++)
            {
                a += Vector.LoadUnsafe(ref Unsafe.Add(ref s, Unsafe.Add(ref o, t) + j)) * new Vector<float>(Unsafe.Add(ref w, t));
            }

            a.StoreUnsafe(ref d, (nuint)j);
        }

        for (; j < n; j++)
        {
            float a = 0f;
            for (int t = 0; t < taps; t++)
            {
                a += Unsafe.Add(ref s, Unsafe.Add(ref o, t) + j) * Unsafe.Add(ref w, t);
            }

            Unsafe.Add(ref d, j) = a;
        }
    }

    /// <summary>The scalar reference of <see cref="SumTaps"/>.</summary>
    public static void SumTapsScalar(ReadOnlySpan<float> src, ReadOnlySpan<int> offsets, ReadOnlySpan<float> weights, Span<float> dst)
    {
        Check(src.Length, offsets, weights.Length, dst.Length);
        for (int j = 0; j < dst.Length; j++)
        {
            float a = 0f;
            for (int t = 0; t < offsets.Length; t++)
            {
                a += src[offsets[t] + j] * weights[t];
            }

            dst[j] = a;
        }
    }

    /// <summary>
    /// A symmetric kernel: <c>dst[j] = centerWeight * src[center + j]</c>, then for each tap pair
    /// <c>+= (src[offsetsA[t] + j] + src[offsetsB[t] + j]) * weights[t]</c> in ascending t. Halves the multiplications.
    /// </summary>
    public static void SumPairs(ReadOnlySpan<float> src, int center, float centerWeight, ReadOnlySpan<int> offsetsA, ReadOnlySpan<int> offsetsB, ReadOnlySpan<float> weights, Span<float> dst)
    {
        if (offsetsA.Length != offsetsB.Length)
        {
            throw new ArgumentException("Offset lists differ in length.");
        }

        Check(src.Length, offsetsA, weights.Length, dst.Length);
        Check(src.Length, offsetsB, weights.Length, dst.Length);
        Check(src.Length, [center], 1, dst.Length);
        if (!Vector.IsHardwareAccelerated || dst.Length < Vector<float>.Count)
        {
            SumPairsScalar(src, center, centerWeight, offsetsA, offsetsB, weights, dst);
            return;
        }

        int vc = Vector<float>.Count;
        int n = dst.Length;
        int taps = offsetsA.Length;
        ref float s = ref MemoryMarshal.GetReference(src);
        ref float d = ref MemoryMarshal.GetReference(dst);
        ref int oa = ref MemoryMarshal.GetReference(offsetsA);
        ref int ob = ref MemoryMarshal.GetReference(offsetsB);
        ref float w = ref MemoryMarshal.GetReference(weights);
        var cw = new Vector<float>(centerWeight);
        int j = 0;
        for (; j + (4 * vc) <= n; j += 4 * vc)
        {
            ref float c = ref Unsafe.Add(ref s, center + j);
            Vector<float> a0 = Vector.LoadUnsafe(ref c) * cw;
            Vector<float> a1 = Vector.LoadUnsafe(ref c, (nuint)vc) * cw;
            Vector<float> a2 = Vector.LoadUnsafe(ref c, (nuint)(2 * vc)) * cw;
            Vector<float> a3 = Vector.LoadUnsafe(ref c, (nuint)(3 * vc)) * cw;
            for (int t = 0; t < taps; t++)
            {
                var wv = new Vector<float>(Unsafe.Add(ref w, t));
                ref float pa = ref Unsafe.Add(ref s, Unsafe.Add(ref oa, t) + j);
                ref float pb = ref Unsafe.Add(ref s, Unsafe.Add(ref ob, t) + j);
                a0 += (Vector.LoadUnsafe(ref pa) + Vector.LoadUnsafe(ref pb)) * wv;
                a1 += (Vector.LoadUnsafe(ref pa, (nuint)vc) + Vector.LoadUnsafe(ref pb, (nuint)vc)) * wv;
                a2 += (Vector.LoadUnsafe(ref pa, (nuint)(2 * vc)) + Vector.LoadUnsafe(ref pb, (nuint)(2 * vc))) * wv;
                a3 += (Vector.LoadUnsafe(ref pa, (nuint)(3 * vc)) + Vector.LoadUnsafe(ref pb, (nuint)(3 * vc))) * wv;
            }

            a0.StoreUnsafe(ref d, (nuint)j);
            a1.StoreUnsafe(ref d, (nuint)(j + vc));
            a2.StoreUnsafe(ref d, (nuint)(j + (2 * vc)));
            a3.StoreUnsafe(ref d, (nuint)(j + (3 * vc)));
        }

        for (; j + vc <= n; j += vc)
        {
            Vector<float> a = Vector.LoadUnsafe(ref Unsafe.Add(ref s, center + j)) * cw;
            for (int t = 0; t < taps; t++)
            {
                a += (Vector.LoadUnsafe(ref Unsafe.Add(ref s, Unsafe.Add(ref oa, t) + j)) + Vector.LoadUnsafe(ref Unsafe.Add(ref s, Unsafe.Add(ref ob, t) + j))) * new Vector<float>(Unsafe.Add(ref w, t));
            }

            a.StoreUnsafe(ref d, (nuint)j);
        }

        for (; j < n; j++)
        {
            float a = Unsafe.Add(ref s, center + j) * centerWeight;
            for (int t = 0; t < taps; t++)
            {
                a += (Unsafe.Add(ref s, Unsafe.Add(ref oa, t) + j) + Unsafe.Add(ref s, Unsafe.Add(ref ob, t) + j)) * Unsafe.Add(ref w, t);
            }

            Unsafe.Add(ref d, j) = a;
        }
    }

    /// <summary>The scalar reference of <see cref="SumPairs"/>.</summary>
    public static void SumPairsScalar(ReadOnlySpan<float> src, int center, float centerWeight, ReadOnlySpan<int> offsetsA, ReadOnlySpan<int> offsetsB, ReadOnlySpan<float> weights, Span<float> dst)
    {
        Check(src.Length, offsetsA, weights.Length, dst.Length);
        Check(src.Length, offsetsB, weights.Length, dst.Length);
        Check(src.Length, [center], 1, dst.Length);
        for (int j = 0; j < dst.Length; j++)
        {
            float a = src[center + j] * centerWeight;
            for (int t = 0; t < offsetsA.Length; t++)
            {
                a += (src[offsetsA[t] + j] + src[offsetsB[t] + j]) * weights[t];
            }

            dst[j] = a;
        }
    }

    private static void Check(int sourceLength, ReadOnlySpan<int> offsets, int weightCount, int count)
    {
        if (offsets.Length != weightCount)
        {
            throw new ArgumentException("Offsets and weights differ in length.");
        }

        long limit = (long)sourceLength - count;
        foreach (int off in offsets)
        {
            if (off < 0 || off > limit)
            {
                throw new ArgumentException("A tap reaches outside the source row.");
            }
        }
    }
}
