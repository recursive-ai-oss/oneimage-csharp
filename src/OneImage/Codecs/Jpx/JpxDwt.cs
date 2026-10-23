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
            RunRows(h, w, degree, () => new int[w], (j, tmp) => HorizontalInt(a.AsSpan(j * stride, w), tmp, wLL, pe));
            int pv = cur.Y0 & 1;
            RunBlocks(w, h, degree, () => new int[h * ColumnBlock], (c0, bw, tmp) => VerticalInt(a, stride, c0, bw, h, hLL, pv, tmp));
        }
    }

    /// <summary>Reconstructs resolutions 1 .. <c>kept - 1</c> of a floating-point (irreversible) tile-component.</summary>
    public static void Inverse97(float[] a, int stride, JpxResolution[] res, int kept, int degree, CancellationToken ct)
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
            RunRows(h, w, degree, () => new float[w], (j, tmp) => HorizontalFloat(a.AsSpan(j * stride, w), tmp, wLL, pe));
            int pv = cur.Y0 & 1;
            RunBlocks(w, h, degree, () => new float[h * ColumnBlock], (c0, bw, tmp) => VerticalFloat(a, stride, c0, bw, h, hLL, pv, tmp));
        }
    }

    private static void RunRows<T>(int rows, int width, int degree, Func<T[]> scratch, Action<int, T[]> body)
    {
        if (degree <= 1 || (long)rows * width < 1 << 16)
        {
            T[] tmp = scratch();
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

    private static void RunBlocks<T>(int width, int height, int degree, Func<T[]> scratch, Action<int, int, T[]> body)
    {
        int blocks = (width + ColumnBlock - 1) / ColumnBlock;
        if (degree <= 1 || (long)width * height < 1 << 16)
        {
            T[] tmp = scratch();
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

        // Interleave: low-pass samples go to the even positions of the absolute grid.
        for (int m = 0; m < wLL; m++)
        {
            tmp[pe + (2 * m)] = row[m];
        }

        int po = 1 - pe;
        for (int m = 0, nh = n - wLL; m < nh; m++)
        {
            tmp[po + (2 * m)] = row[wLL + m];
        }

        Lift53(tmp, n, pe);
        tmp.AsSpan(0, n).CopyTo(row);
    }

    // F.3.8.1 on an interleaved signal whose first even-position sample is at index pe.
    private static void Lift53(int[] x, int n, int pe)
    {
        for (int i = pe; i < n; i += 2)
        {
            int l = i > 0 ? x[i - 1] : x[i + 1];
            int r = i + 1 < n ? x[i + 1] : x[i - 1];
            x[i] -= (l + r + 2) >> 2;
        }

        for (int i = 1 - pe; i < n; i += 2)
        {
            int l = i > 0 ? x[i - 1] : x[i + 1];
            int r = i + 1 < n ? x[i + 1] : x[i - 1];
            x[i] += (l + r) >> 1;
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
            Span<int> row = tmp.AsSpan(i * bw, bw);
            ReadOnlySpan<int> lo = tmp.AsSpan(li * bw, bw);
            ReadOnlySpan<int> hi = tmp.AsSpan(ri * bw, bw);
            for (int c = 0; c < row.Length; c++)
            {
                row[c] -= (lo[c] + hi[c] + 2) >> 2;
            }
        }

        for (int i = 1 - pv; i < h; i += 2)
        {
            int li = i > 0 ? i - 1 : i + 1;
            int ri = i + 1 < h ? i + 1 : i - 1;
            Span<int> row = tmp.AsSpan(i * bw, bw);
            ReadOnlySpan<int> lo = tmp.AsSpan(li * bw, bw);
            ReadOnlySpan<int> hi = tmp.AsSpan(ri * bw, bw);
            for (int c = 0; c < row.Length; c++)
            {
                row[c] += (lo[c] + hi[c]) >> 1;
            }
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

        for (int m = 0; m < wLL; m++)
        {
            tmp[pe + (2 * m)] = row[m];
        }

        int po = 1 - pe;
        for (int m = 0, nh = n - wLL; m < nh; m++)
        {
            tmp[po + (2 * m)] = row[wLL + m];
        }

        Lift97(tmp, n, pe);
        tmp.AsSpan(0, n).CopyTo(row);
    }

    // F.3.8.2: scaling then four lifting steps, each step reading the neighbours of the other parity.
    private static void Lift97(float[] x, int n, int pe)
    {
        int po = 1 - pe;
        for (int i = pe; i < n; i += 2)
        {
            x[i] *= K;
        }

        for (int i = po; i < n; i += 2)
        {
            x[i] *= InvK;
        }

        LiftStep(x, n, pe, Delta);
        LiftStep(x, n, po, Gamma);
        LiftStep(x, n, pe, Beta);
        LiftStep(x, n, po, Alpha);
    }

    private static void LiftStep(float[] x, int n, int start, float coef)
    {
        for (int i = start; i < n; i += 2)
        {
            float l = i > 0 ? x[i - 1] : x[i + 1];
            float r = i + 1 < n ? x[i + 1] : x[i - 1];
            x[i] -= coef * (l + r);
        }
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

    private static void ScaleRow(Span<float> row, float k)
    {
        for (int c = 0; c < row.Length; c++)
        {
            row[c] *= k;
        }
    }

    private static void VerticalStep(float[] tmp, int bw, int h, int start, float coef)
    {
        for (int i = start; i < h; i += 2)
        {
            int li = i > 0 ? i - 1 : i + 1;
            int ri = i + 1 < h ? i + 1 : i - 1;
            Span<float> row = tmp.AsSpan(i * bw, bw);
            ReadOnlySpan<float> lo = tmp.AsSpan(li * bw, bw);
            ReadOnlySpan<float> hi = tmp.AsSpan(ri * bw, bw);
            for (int c = 0; c < row.Length; c++)
            {
                row[c] -= coef * (lo[c] + hi[c]);
            }
        }
    }
}
