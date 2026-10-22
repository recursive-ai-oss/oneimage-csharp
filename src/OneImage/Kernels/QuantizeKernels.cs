using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>Scan helpers for RGBA8 rows used by the quantizer and the GIF encoder. Callers pass spans whose length is a multiple of 4.</summary>
internal static class QuantizeKernels
{
    /// <summary>Returns whether any pixel has alpha below <paramref name="threshold"/> (1 to 255).</summary>
    public static bool HasAlphaBelow(ReadOnlySpan<byte> rgba, byte threshold)
    {
        int n = rgba.Length & ~3;
        int i = 0;
        if (Vector256.IsHardwareAccelerated && n >= 32)
        {
            ref byte r = ref MemoryMarshal.GetReference(rgba);
            Vector256<byte> limit = Vector256.Create(threshold);
            Vector256<byte> alphaOnly = Vector256.Create(0xFF000000u).AsByte(); // byte 3 of every pixel
            for (; i <= n - 32; i += 32)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref r, (nuint)i);
                if ((Vector256.LessThan(v, limit) & alphaOnly) != Vector256<byte>.Zero)
                {
                    return true;
                }
            }
        }

        return HasAlphaBelowScalar(rgba.Slice(i, n - i), threshold);
    }

    /// <summary>Reference implementation of <see cref="HasAlphaBelow"/>.</summary>
    public static bool HasAlphaBelowScalar(ReadOnlySpan<byte> rgba, byte threshold)
    {
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] < threshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The value padding entries of a palette table carry, so that they are never the nearest.</summary>
    internal const int PaddingValue = 16384;

    /// <summary>Rounds a palette size up to the multiple the table arrays are padded to.</summary>
    internal static int PaddedLength(int count) => (count + 7) & ~7;

    /// <summary>
    /// Returns the index of the palette entry nearest to a color by squared distance over four channels; ties go to the lower index.
    /// Entries <paramref name="count"/> up to the padded length must hold <see cref="PaddingValue"/>. Returns 0 when no entry qualifies.
    /// </summary>
    /// <param name="pr">Red of each entry, padded to <see cref="PaddedLength"/>.</param>
    /// <param name="pg">Green of each entry.</param>
    /// <param name="pb">Blue of each entry.</param>
    /// <param name="pa">Alpha of each entry.</param>
    /// <param name="count">Number of real entries.</param>
    /// <param name="skip">An entry that must not be chosen, or -1.</param>
    /// <param name="r">Red of the color.</param>
    /// <param name="g">Green of the color.</param>
    /// <param name="b">Blue of the color.</param>
    /// <param name="a">Alpha of the color.</param>
    public static int Nearest(ReadOnlySpan<int> pr, ReadOnlySpan<int> pg, ReadOnlySpan<int> pb, ReadOnlySpan<int> pa, int count, int skip, int r, int g, int b, int a)
    {
        int n = PaddedLength(count);
        if (Vector256.IsHardwareAccelerated && n >= 8 && pr.Length >= n && pg.Length >= n && pb.Length >= n && pa.Length >= n)
        {
            return Nearest256(pr, pg, pb, pa, n, count, skip, r, g, b, a);
        }

        return NearestScalar(pr, pg, pb, pa, count, skip, r, g, b, a);
    }

    private static int Nearest256(ReadOnlySpan<int> pr, ReadOnlySpan<int> pg, ReadOnlySpan<int> pb, ReadOnlySpan<int> pa, int n, int count, int skip, int r, int g, int b, int a)
    {
        ref int rr = ref MemoryMarshal.GetReference(pr);
        ref int gg = ref MemoryMarshal.GetReference(pg);
        ref int bb = ref MemoryMarshal.GetReference(pb);
        ref int aa = ref MemoryMarshal.GetReference(pa);
        Vector256<int> vr = Vector256.Create(r);
        Vector256<int> vg = Vector256.Create(g);
        Vector256<int> vb = Vector256.Create(b);
        Vector256<int> va = Vector256.Create(a);
        Vector256<int> skipV = Vector256.Create(skip);
        Vector256<int> maxV = Vector256.Create(int.MaxValue);
        Vector256<int> eight = Vector256.Create(8);
        Vector256<int> idx = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        Vector256<int> bestD = maxV;
        Vector256<int> bestI = Vector256<int>.Zero;
        for (int i = 0; i < n; i += 8)
        {
            Vector256<int> dr = Vector256.LoadUnsafe(ref rr, (nuint)i) - vr;
            Vector256<int> dg = Vector256.LoadUnsafe(ref gg, (nuint)i) - vg;
            Vector256<int> db = Vector256.LoadUnsafe(ref bb, (nuint)i) - vb;
            Vector256<int> da = Vector256.LoadUnsafe(ref aa, (nuint)i) - va;
            Vector256<int> d = (dr * dr) + (dg * dg) + (db * db) + (da * da);
            d = Vector256.ConditionalSelect(Vector256.Equals(idx, skipV), maxV, d);
            Vector256<int> lt = Vector256.LessThan(d, bestD);
            bestD = Vector256.ConditionalSelect(lt, d, bestD);
            bestI = Vector256.ConditionalSelect(lt, idx, bestI);
            idx += eight;
        }

        // Lowest distance wins; among equal distances the lowest index, which is what a front-to-back scan with a strict comparison picks.
        int best = -1;
        int bestDist = int.MaxValue;
        for (int l = 0; l < 8; l++)
        {
            int dist = bestD.GetElement(l);
            int index = bestI.GetElement(l);
            if (dist < bestDist || (dist == bestDist && best >= 0 && index < best))
            {
                bestDist = dist;
                best = index;
            }
        }

        return best < 0 || best >= count ? 0 : best;
    }

    /// <summary>Reference implementation of <see cref="Nearest"/>.</summary>
    public static int NearestScalar(ReadOnlySpan<int> pr, ReadOnlySpan<int> pg, ReadOnlySpan<int> pb, ReadOnlySpan<int> pa, int count, int skip, int r, int g, int b, int a)
    {
        int best = -1;
        long bestD = long.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (i == skip)
            {
                continue;
            }

            long dr = pr[i] - r;
            long dg = pg[i] - g;
            long db = pb[i] - b;
            long da = pa[i] - a;
            long d = (dr * dr) + (dg * dg) + (db * db) + (da * da);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best < 0 ? 0 : best;
    }
}
