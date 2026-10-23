using System.Buffers;
using System.Numerics;

namespace OneImage.Codecs.Jpx;

/// <summary>
/// The inverse discrete wavelet transform of T.800 Annex F, applied in place to a tile-component laid out with the low-pass
/// band at the top left and each level's HL, LH and HH bands to its right, below and diagonally. Lifting steps work on the
/// interleaved signal with symmetric reflection at both ends, which equals the periodic symmetric extension of F.3.7.
/// </summary>
/// <remarks>
/// The 9-7 filter runs in single-precision floating point, one operation at a time (no fused multiply-add), so every hardware
/// path gives the same bits. The reversible 5-3 filter is exact integer arithmetic.
/// </remarks>
internal static class JpxDwt
{
    // Table F.4.
    private const float Alpha = -1.586134342059924f;
    private const float Beta = -0.052980118572961f;
    private const float Gamma = 0.882911075530934f;
    private const float Delta = 0.443506852043971f;
    private const float K = 1.230174104914001f;
    private const float InvK = 1f / K;

    private const int ColumnBlock = 16;

    /// <summary>Reconstructs resolutions 1 .. <c>kept - 1</c> of an integer (reversible) tile-component.</summary>
    public static void Inverse53(int[] a, int stride, JpxResolution[] res, int kept, int degree, CancellationToken ct)
    {
        // Serial runs share one pair of scratch buffers across all levels; parallel runs give every worker its own.
        int[]? rowScratch = null;
        int[]? colScratch = null;
        if (degree <= 1)
        {
            int maxW = 1;
            int maxH = 1;
            for (int r = 1; r < kept; r++)
            {
                maxW = Math.Max(maxW, res[r].Width);
                maxH = Math.Max(maxH, res[r].Height);
            }

            rowScratch = ArrayPool<int>.Shared.Rent(maxW);
            colScratch = ArrayPool<int>.Shared.Rent(maxH * ColumnBlock);
        }

        try
        {
            Inverse53Levels(a, stride, res, kept, degree, rowScratch, colScratch, ct);
        }
        finally
        {
            if (rowScratch is not null)
            {
                ArrayPool<int>.Shared.Return(rowScratch);
                ArrayPool<int>.Shared.Return(colScratch!);
            }
        }
    }

    private static void Inverse53Levels(int[] a, int stride, JpxResolution[] res, int kept, int degree, int[]? rowScratch, int[]? colScratch, CancellationToken ct)
    {
        for (int r = 1; r < kept; r++)
        {
            JpxResolution cur = res[r];
            JpxResolution prev = res[r - 1];
            int w = cur.Width;
            int h = cur.Height;
            if (w == 0 || h == 0)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            int wLL = prev.Width;
            int hLL = prev.Height;
            int pe = cur.X0 & 1;
            RunRows(h, w, degree, rowScratch, () => new int[w], (j, tmp) => HorizontalInt(a.AsSpan(j * stride, w), tmp, wLL, pe));
            int pv = cur.Y0 & 1;
            RunBlocks(w, h, degree, colScratch, () => new int[h * ColumnBlock], (c0, bw, tmp) => VerticalInt(a, stride, c0, bw, h, hLL, pv, tmp));
        }
    }

    /// <summary>Reconstructs resolutions 1 .. <c>kept - 1</c> of a floating-point (irreversible) tile-component.</summary>
    public static void Inverse97(float[] a, int stride, JpxResolution[] res, int kept, int degree, CancellationToken ct)
    {
        // Serial runs share one pair of scratch buffers across all levels; parallel runs give every worker its own.
        float[]? rowScratch = null;
        float[]? colScratch = null;
        if (degree <= 1)
        {
            int maxW = 1;
            int maxH = 1;
            for (int r = 1; r < kept; r++)
            {
                maxW = Math.Max(maxW, res[r].Width);
                maxH = Math.Max(maxH, res[r].Height);
            }

            rowScratch = ArrayPool<float>.Shared.Rent(maxW);
            colScratch = ArrayPool<float>.Shared.Rent(maxH * ColumnBlock);
        }

        try
        {
            Inverse97Levels(a, stride, res, kept, degree, rowScratch, colScratch, ct);
        }
        finally
        {
            if (rowScratch is not null)
            {
                ArrayPool<float>.Shared.Return(rowScratch);
                ArrayPool<float>.Shared.Return(colScratch!);
            }
        }
    }

    private static void Inverse97Levels(float[] a, int stride, JpxResolution[] res, int kept, int degree, float[]? rowScratch, float[]? colScratch, CancellationToken ct)
    {
        for (int r = 1; r < kept; r++)
        {
            JpxResolution cur = res[r];
            JpxResolution prev = res[r - 1];
            int w = cur.Width;
            int h = cur.Height;
            if (w == 0 || h == 0)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            int wLL = prev.Width;
            int hLL = prev.Height;
            int pe = cur.X0 & 1;
            RunRows(h, w, degree, rowScratch, () => new float[w], (j, tmp) => HorizontalFloat(a.AsSpan(j * stride, w), tmp, wLL, pe));
            int pv = cur.Y0 & 1;
            RunBlocks(w, h, degree, colScratch, () => new float[h * ColumnBlock], (c0, bw, tmp) => VerticalFloat(a, stride, c0, bw, h, hLL, pv, tmp));
        }
    }

    private static void RunRows<T>(int rows, int width, int degree, T[]? shared, Func<T[]> scratch, Action<int, T[]> body)
    {
        if (degree <= 1 || (long)rows * width < 1 << 16)
        {
            T[] tmp = shared ?? scratch();
            for (int j = 0; j < rows; j++)
            {
                body(j, tmp);
            }

            return;
        }

        int chunk = Math.Max(1, rows / (degree * 4));
        int chunks = (rows + chunk - 1) / chunk;
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = degree }, () => scratch(), (ci, _, tmp) =>
        {
            int end = Math.Min(rows, (ci + 1) * chunk);
            for (int j = ci * chunk; j < end; j++)
            {
                body(j, tmp);
            }

            return tmp;
        }, _ => { });
    }

    private static void RunBlocks<T>(int width, int height, int degree, T[]? shared, Func<T[]> scratch, Action<int, int, T[]> body)
    {
        int blocks = (width + ColumnBlock - 1) / ColumnBlock;
        if (degree <= 1 || (long)width * height < 1 << 16)
        {
            T[] tmp = shared ?? scratch();
            for (int b = 0; b < blocks; b++)
            {
                body(b * ColumnBlock, Math.Min(ColumnBlock, width - (b * ColumnBlock)), tmp);
            }

            return;
        }

        Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = degree }, () => scratch(), (b, _, tmp) =>
        {
            body(b * ColumnBlock, Math.Min(ColumnBlock, width - (b * ColumnBlock)), tmp);
            return tmp;
        }, _ => { });
    }

    // ---- reversible 5-3 ----

    // The row holds the low-pass samples first, then the high-pass ones. Both lifting steps work on those two runs directly
    // (every neighbour of a low-pass sample is a high-pass sample and the other way round), so the steps are plain loops over
    // contiguous memory; only the final interleave is a strided copy.
    private static void HorizontalInt(Span<int> row, int[] tmp, int wLL, int pe)
    {
        int n = row.Length;
        if (n == 1)
        {
            if (pe != 0)
            {
                row[0] /= 2; // F.3.6: a single odd-positioned sample is a high-pass sample scaled by two
            }

            return;
        }

        Span<int> low = row[..wLL];
        Span<int> high = row[wLL..];
        Step53(low, high, pe, true);
        Step53(high, low, 1 - pe, false);

        // Interleave: low-pass samples go to the positions of even absolute index.
        row.CopyTo(tmp);
        int po = 1 - pe;
        for (int m = 0; m < low.Length; m++)
        {
            row[pe + (2 * m)] = tmp[m];
        }

        for (int m = 0; m < high.Length; m++)
        {
            row[po + (2 * m)] = tmp[wLL + m];
        }
    }

    // F.3.8.1: target[j] -= (left + right + 2) >> 2 (first step) or += (left + right) >> 1 (second), where left = src[j + off - 1] and
    // right = src[j + off], reflected at either end of src.
    private static void Step53(Span<int> target, ReadOnlySpan<int> src, int off, bool first)
    {
        int len = src.Length;
        int jlo = Math.Max(0, 1 - off);
        int jhi = Math.Min(target.Length - 1, len - 1 - off);
        for (int j = 0; j < Math.Min(jlo, target.Length); j++)
        {
            Apply53(target, src, j, off, first);
        }

        int c = jlo;
        if (Vector.IsHardwareAccelerated)
        {
            for (; c + Vector<int>.Count - 1 <= jhi; c += Vector<int>.Count)
            {
                var sum = new Vector<int>(src[(c + off - 1)..]) + new Vector<int>(src[(c + off)..]);
                var x = new Vector<int>(target[c..]);
                x = first ? x - Vector.ShiftRightArithmetic(sum + new Vector<int>(2), 2) : x + Vector.ShiftRightArithmetic(sum, 1);
                x.CopyTo(target[c..]);
            }
        }

        for (; c < target.Length; c++)
        {
            Apply53(target, src, c, off, first);
        }
    }

    private static void Apply53(Span<int> target, ReadOnlySpan<int> src, int j, int off, bool first)
    {
        int left = j + off - 1;
        int right = j + off;
        if (left < 0)
        {
            left = right;
        }

        if (right >= src.Length)
        {
            right = left;
        }

        int sum = src[left] + src[right];
        if (first)
        {
            target[j] -= (sum + 2) >> 2;
        }
        else
        {
            target[j] += sum >> 1;
        }
    }

    private static void VerticalInt(int[] a, int stride, int c0, int bw, int h, int hLL, int pv, int[] tmp)
    {
        if (h == 1)
        {
            if (pv != 0)
            {
                for (int c = 0; c < bw; c++)
                {
                    a[c0 + c] /= 2;
                }
            }

            return;
        }

        // Gather: logical row i comes from physical row m of the low-pass group (even absolute rows) or hLL + m of the high-pass group.
        for (int i = 0; i < h; i++)
        {
            int p = ((i - pv) & 1) == 0 ? (i - pv) / 2 : hLL + ((i - (1 - pv)) / 2);
            a.AsSpan((p * stride) + c0, bw).CopyTo(tmp.AsSpan(i * bw, bw));
        }

        for (int i = pv; i < h; i += 2)
        {
            int li = i > 0 ? i - 1 : i + 1;
            int ri = i + 1 < h ? i + 1 : i - 1;
            Update53(tmp.AsSpan(i * bw, bw), tmp.AsSpan(li * bw, bw), tmp.AsSpan(ri * bw, bw), true);
        }

        for (int i = 1 - pv; i < h; i += 2)
        {
            int li = i > 0 ? i - 1 : i + 1;
            int ri = i + 1 < h ? i + 1 : i - 1;
            Update53(tmp.AsSpan(i * bw, bw), tmp.AsSpan(li * bw, bw), tmp.AsSpan(ri * bw, bw), false);
        }

        for (int i = 0; i < h; i++)
        {
            tmp.AsSpan(i * bw, bw).CopyTo(a.AsSpan((i * stride) + c0, bw));
        }
    }

    // ---- irreversible 9-7 ----

    private static void HorizontalFloat(Span<float> row, float[] tmp, int wLL, int pe)
    {
        int n = row.Length;
        if (n == 1)
        {
            if (pe != 0)
            {
                row[0] *= 0.5f;
            }

            return;
        }

        Span<float> low = row[..wLL];
        Span<float> high = row[wLL..];

        // F.3.8.2: scaling, then four lifting steps, each reading the neighbours of the other parity.
        ScaleRow(low, K);
        ScaleRow(high, InvK);
        Step97(low, high, pe, Delta);
        Step97(high, low, 1 - pe, Gamma);
        Step97(low, high, pe, Beta);
        Step97(high, low, 1 - pe, Alpha);

        row.CopyTo(tmp);
        int po = 1 - pe;
        for (int m = 0; m < low.Length; m++)
        {
            row[pe + (2 * m)] = tmp[m];
        }

        for (int m = 0; m < high.Length; m++)
        {
            row[po + (2 * m)] = tmp[wLL + m];
        }
    }

    // target[j] -= coef * (left + right), where left = src[j + off - 1] and right = src[j + off], reflected at either end of src.
    private static void Step97(Span<float> target, ReadOnlySpan<float> src, int off, float coef)
    {
        int len = src.Length;
        int jlo = Math.Max(0, 1 - off);
        int jhi = Math.Min(target.Length - 1, len - 1 - off);
        for (int j = 0; j < Math.Min(jlo, target.Length); j++)
        {
            Apply97(target, src, j, off, coef);
        }

        int c = jlo;
        if (Vector.IsHardwareAccelerated)
        {
            var vc = new Vector<float>(coef);
            for (; c + Vector<float>.Count - 1 <= jhi; c += Vector<float>.Count)
            {
                var sum = new Vector<float>(src[(c + off - 1)..]) + new Vector<float>(src[(c + off)..]);
                (new Vector<float>(target[c..]) - (vc * sum)).CopyTo(target[c..]);
            }
        }

        for (; c < target.Length; c++)
        {
            Apply97(target, src, c, off, coef);
        }
    }

    private static void Apply97(Span<float> target, ReadOnlySpan<float> src, int j, int off, float coef)
    {
        int left = j + off - 1;
        int right = j + off;
        if (left < 0)
        {
            left = right;
        }

        if (right >= src.Length)
        {
            right = left;
        }

        target[j] -= coef * (src[left] + src[right]);
    }

    private static void VerticalFloat(float[] a, int stride, int c0, int bw, int h, int hLL, int pv, float[] tmp)
    {
        if (h == 1)
        {
            if (pv != 0)
            {
                for (int c = 0; c < bw; c++)
                {
                    a[c0 + c] *= 0.5f;
                }
            }

            return;
        }

        for (int i = 0; i < h; i++)
        {
            int p = ((i - pv) & 1) == 0 ? (i - pv) / 2 : hLL + ((i - (1 - pv)) / 2);
            a.AsSpan((p * stride) + c0, bw).CopyTo(tmp.AsSpan(i * bw, bw));
        }

        int po = 1 - pv;
        for (int i = pv; i < h; i += 2)
        {
            ScaleRow(tmp.AsSpan(i * bw, bw), K);
        }

        for (int i = po; i < h; i += 2)
        {
            ScaleRow(tmp.AsSpan(i * bw, bw), InvK);
        }

        VerticalStep(tmp, bw, h, pv, Delta);
        VerticalStep(tmp, bw, h, po, Gamma);
        VerticalStep(tmp, bw, h, pv, Beta);
        VerticalStep(tmp, bw, h, po, Alpha);

        for (int i = 0; i < h; i++)
        {
            tmp.AsSpan(i * bw, bw).CopyTo(a.AsSpan((i * stride) + c0, bw));
        }
    }

    // The vertical passes work on whole rows of a column block, so they are plain element-wise loops. The vector paths do exactly the
    // scalar operations (one multiply or add at a time, no fused multiply-add), so the output does not depend on the vector width.
    private static void ScaleRow(Span<float> row, float k)
    {
        int c = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vk = new Vector<float>(k);
            for (; c <= row.Length - Vector<float>.Count; c += Vector<float>.Count)
            {
                (new Vector<float>(row[c..]) * vk).CopyTo(row[c..]);
            }
        }

        for (; c < row.Length; c++)
        {
            row[c] *= k;
        }
    }

    // First 5-3 lifting step (predict: x -= (lo + hi + 2) >> 2) or the second (update: x += (lo + hi) >> 1).
    private static void Update53(Span<int> row, ReadOnlySpan<int> lo, ReadOnlySpan<int> hi, bool first)
    {
        int c = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; c <= row.Length - Vector<int>.Count; c += Vector<int>.Count)
            {
                var sum = new Vector<int>(lo[c..]) + new Vector<int>(hi[c..]);
                var x = new Vector<int>(row[c..]);
                x = first ? x - Vector.ShiftRightArithmetic(sum + new Vector<int>(2), 2) : x + Vector.ShiftRightArithmetic(sum, 1);
                x.CopyTo(row[c..]);
            }
        }

        if (first)
        {
            for (; c < row.Length; c++)
            {
                row[c] -= (lo[c] + hi[c] + 2) >> 2;
            }
        }
        else
        {
            for (; c < row.Length; c++)
            {
                row[c] += (lo[c] + hi[c]) >> 1;
            }
        }
    }

    private static void Update97(Span<float> row, ReadOnlySpan<float> lo, ReadOnlySpan<float> hi, float coef)
    {
        int c = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vc = new Vector<float>(coef);
            for (; c <= row.Length - Vector<float>.Count; c += Vector<float>.Count)
            {
                var sum = new Vector<float>(lo[c..]) + new Vector<float>(hi[c..]);
                (new Vector<float>(row[c..]) - (vc * sum)).CopyTo(row[c..]);
            }
        }

        for (; c < row.Length; c++)
        {
            row[c] -= coef * (lo[c] + hi[c]);
        }
    }

    private static void VerticalStep(float[] tmp, int bw, int h, int start, float coef)
    {
        for (int i = start; i < h; i += 2)
        {
            int li = i > 0 ? i - 1 : i + 1;
            int ri = i + 1 < h ? i + 1 : i - 1;
            Update97(tmp.AsSpan(i * bw, bw), tmp.AsSpan(li * bw, bw), tmp.AsSpan(ri * bw, bw), coef);
        }
    }
}
