using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OneImage.Codecs.WebP;

namespace OneImage.Kernels;

/// <summary>
/// Residual histograms for the lossless encoder's predictor search. Scalar: the loop is a gather and four histogram updates per
/// pixel, which does not vectorize, so the work goes into removing bounds checks from the hot loop. Callers hand in whole,
/// validated pixel arrays; the sizes are checked once per call.
/// </summary>
internal static class WebPPredictKernels
{
    /// <summary>
    /// Adds the four channel histograms (alpha, red, green, blue at offsets 0, 256, 512, 768) of the residuals of pixels
    /// x0..x1-1, y0..y1-1 of an image <paramref name="w"/> by <paramref name="h"/> predicted with <paramref name="mode"/> (0..13). Every <paramref name="rowStep"/>-th row is scored.
    /// First-row and first-column pixels use fixed predictors that do not depend on the mode, so they are left out.
    /// </summary>
    public static void ScoreTile(uint[] a, int w, int h, int x0, int y0, int x1, int y1, int mode, int[] hist, int rowStep = 1)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(hist);
        if (w <= 0 || h <= 0 || a.Length < (long)w * h || hist.Length < 1024 || x0 < 0 || y0 < 0 || x1 > w || y1 > h || (uint)mode > 13 || rowStep < 1)
        {
            throw new ArgumentException("Invalid tile geometry.");
        }

        int xs = Math.Max(x0, 1);
        int n = x1 - xs;
        if (n <= 0)
        {
            return;
        }

        for (int y = Math.Max(y0, 1); y < y1; y += rowStep)
        {
            int row = (y * w) + xs;
            // Slicing validates every window once; the loops below then run without bounds checks.
            ReadOnlySpan<uint> cur = a.AsSpan(row, n);
            ReadOnlySpan<uint> l = a.AsSpan(row - 1, n);
            ReadOnlySpan<uint> t = a.AsSpan(row - w, n);
            ReadOnlySpan<uint> tl = a.AsSpan(row - w - 1, n);
            ReadOnlySpan<uint> tr = a.AsSpan(row - w + 1, n);
            switch (mode)
            {
                case 0: ScoreRow<PBlack>(cur, l, t, tl, tr, hist); break;
                case 1: ScoreRow<PLeft>(cur, l, t, tl, tr, hist); break;
                case 2: ScoreRow<PTop>(cur, l, t, tl, tr, hist); break;
                case 3: ScoreRow<PTopRight>(cur, l, t, tl, tr, hist); break;
                case 4: ScoreRow<PTopLeft>(cur, l, t, tl, tr, hist); break;
                case 5: ScoreRow<PAvg3>(cur, l, t, tl, tr, hist); break;
                case 6: ScoreRow<PAvgLTl>(cur, l, t, tl, tr, hist); break;
                case 7: ScoreRow<PAvgLT>(cur, l, t, tl, tr, hist); break;
                case 8: ScoreRow<PAvgTlT>(cur, l, t, tl, tr, hist); break;
                case 9: ScoreRow<PAvgTTr>(cur, l, t, tl, tr, hist); break;
                case 10: ScoreRow<PAvg4>(cur, l, t, tl, tr, hist); break;
                case 11: ScoreRow<PSelect>(cur, l, t, tl, tr, hist); break;
                case 12: ScoreRow<PGradient>(cur, l, t, tl, tr, hist); break;
                default: ScoreRow<PHalfGradient>(cur, l, t, tl, tr, hist); break;
            }
        }
    }

    private static void ScoreRow<TP>(ReadOnlySpan<uint> cur, ReadOnlySpan<uint> l, ReadOnlySpan<uint> t, ReadOnlySpan<uint> tl, ReadOnlySpan<uint> tr, Span<int> hist)
        where TP : struct, IPredictor
    {
        int n = cur.Length;
        if (l.Length != n || t.Length != n || tl.Length != n || tr.Length != n || hist.Length < 1024)
        {
            throw new ArgumentException("Mismatched rows.");
        }

        ref uint pc = ref MemoryMarshal.GetReference(cur);
        ref uint pl = ref MemoryMarshal.GetReference(l);
        ref uint pt = ref MemoryMarshal.GetReference(t);
        ref uint ptl = ref MemoryMarshal.GetReference(tl);
        ref uint ptr = ref MemoryMarshal.GetReference(tr);
        ref int h = ref MemoryMarshal.GetReference(hist);
        for (int k = 0; k < n; k++)
        {
            uint p = TP.Predict(Unsafe.Add(ref pl, k), Unsafe.Add(ref pt, k), Unsafe.Add(ref ptl, k), Unsafe.Add(ref ptr, k));
            uint r = SubPixels(Unsafe.Add(ref pc, k), p);
            Unsafe.Add(ref h, (int)(r >> 24))++;
            Unsafe.Add(ref h, 256 + (int)((r >> 16) & 0xff))++;
            Unsafe.Add(ref h, 512 + (int)((r >> 8) & 0xff))++;
            Unsafe.Add(ref h, 768 + (int)(r & 0xff))++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SubPixels(uint a, uint b)
    {
        uint ag = ((a | 0x00ff00ff) - (b & 0xff00ff00)) & 0xff00ff00;
        uint rb = ((a | 0xff00ff00) - (b & 0x00ff00ff)) & 0x00ff00ff;
        return ag | rb;
    }

    private interface IPredictor
    {
        static abstract uint Predict(uint l, uint t, uint tl, uint tr);
    }

    private struct PBlack : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => 0xff000000; }

    private struct PLeft : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => l; }

    private struct PTop : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => t; }

    private struct PTopRight : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => tr; }

    private struct PTopLeft : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => tl; }

    private struct PAvg3 : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => VP8LDecoder.Average2(VP8LDecoder.Average2(l, tr), t); }

    private struct PAvgLTl : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => VP8LDecoder.Average2(l, tl); }

    private struct PAvgLT : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => VP8LDecoder.Average2(l, t); }

    private struct PAvgTlT : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => VP8LDecoder.Average2(tl, t); }

    private struct PAvgTTr : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => VP8LDecoder.Average2(t, tr); }

    private struct PAvg4 : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => VP8LDecoder.Average2(VP8LDecoder.Average2(l, tl), VP8LDecoder.Average2(t, tr)); }

    private struct PSelect : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => VP8LDecoder.Select(l, t, tl); }

    private struct PGradient : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => VP8LDecoder.ClampAddSubtractFull(l, t, tl); }

    private struct PHalfGradient : IPredictor { [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Predict(uint l, uint t, uint tl, uint tr) => VP8LDecoder.ClampAddSubtractHalf(VP8LDecoder.Average2(l, t), tl); }
}
