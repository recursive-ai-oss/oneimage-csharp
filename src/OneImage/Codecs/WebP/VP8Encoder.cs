using System.Buffers.Binary;
using OneImage.Kernels;

namespace OneImage.Codecs.WebP;

/// <summary>
/// VP8 key frame encoder. Pipeline: RGB to YUV 4:2:0, a texture analysis pass that assigns macroblocks to four
/// quantizer segments, rate-distortion mode decisions (16x16, 4x4 and chroma intra prediction), token probability
/// optimization and bitstream writing. Output is deterministic for a given input and settings.
/// </summary>
internal sealed class VP8Encoder
{
    private const int Bps = VP8Decoder.Bps;
    private const int YOff = VP8Decoder.YOff;
    private const int UOff = VP8Decoder.UOff;
    private const int VOff = VP8Decoder.VOff;
    private const int LevelsPerMb = 25 * 16;
    private const int NumSegments = 4;

    private readonly int _w;
    private readonly int _h;
    private readonly int _mbw;
    private readonly int _mbh;
    private readonly int _ys;
    private readonly int _uvs;
    private readonly int _quality;
    private readonly int _effort;
    private readonly CancellationToken _token;
    private readonly int _degree;

    private readonly byte[] _srcY;
    private readonly byte[] _srcU;
    private readonly byte[] _srcV;
    private readonly byte[] _recY;
    private readonly byte[] _recU;
    private readonly byte[] _recV;

    private readonly Segment[] _seg = new Segment[NumSegments];
    private readonly byte[] _mbSegment;
    private int _dqUvDc;
    private int _dqUvAc;

    // Per macroblock results.
    private readonly short[] _levels;
    private readonly byte[] _isI4;
    private readonly byte[] _yMode;
    private readonly byte[] _uvMode;
    private readonly byte[] _bModes;
    private readonly byte[] _skip;

    // Contexts while scanning macroblocks.
    private readonly byte[] _intraTop;
    private readonly byte[] _intraLeft = new byte[4];
    private readonly byte[] _nzTop;
    private readonly byte[] _nzLeft = new byte[9];


    private sealed class Segment
    {
        public int Quant;
        public VP8QuantMatrix Y1 = null!;
        public VP8QuantMatrix Y2 = null!;
        public VP8QuantMatrix Uv = null!;
        public double LambdaI16;
        public double LambdaI4;
        public double LambdaUv;
        public double LambdaMode;
        public int Alpha;
        public int Beta;
        public int FilterStrength;
        public int MaxEdge;
    }

    private VP8Encoder(byte[] rgba, int w, int h, int quality, int effort, int degree, CancellationToken token)
    {
        _degree = degree;
        _w = w;
        _h = h;
        _quality = quality;
        _effort = effort;
        _token = token;
        _mbw = (w + 15) >> 4;
        _mbh = (h + 15) >> 4;
        _ys = _mbw * 16;
        _uvs = _mbw * 8;
        int ySize = _ys * _mbh * 16;
        _srcY = new byte[ySize];
        _srcU = new byte[ySize / 4];
        _srcV = new byte[ySize / 4];
        _recY = new byte[ySize];
        _recU = new byte[ySize / 4];
        _recV = new byte[ySize / 4];
        int n = _mbw * _mbh;
        _mbSegment = new byte[n];
        _levels = new short[checked(n * LevelsPerMb)];
        _isI4 = new byte[n];
        _yMode = new byte[n];
        _uvMode = new byte[n];
        _bModes = new byte[n * 16];
        _skip = new byte[n];
        _intraTop = new byte[_mbw * 4];
        _nzTop = new byte[_mbw * 9];
        ConvertToYuv(rgba);
    }

    /// <summary>Encodes straight RGBA (8 bits per channel) into a VP8 chunk payload.</summary>
    /// <param name="rgba">The pixels.</param>
    /// <param name="width">Image width, 1 to 16383.</param>
    /// <param name="height">Image height, 1 to 16383.</param>
    /// <param name="quality">0 to 100.</param>
    /// <param name="effort">0 to 6.</param>
    /// <param name="token">Cancellation.</param>
    /// <param name="degree">Most threads to use; the output does not depend on it.</param>
    public static byte[] Encode(byte[] rgba, int width, int height, int quality, int effort, CancellationToken token, int degree = 1)
    {
        var e = new VP8Encoder(rgba, width, height, quality, effort, degree, token);
        return e.Run();
    }

    // ---- color conversion (same integer math as libwebp's default conversion) ----

    private const int YuvFix = 16;
    private const int YuvHalf = 1 << (YuvFix - 1);

    private static int RgbToY(int r, int g, int b) => ((16839 * r) + (33059 * g) + (6420 * b) + YuvHalf + (16 << YuvFix)) >> YuvFix;

    private static int ClipUv(int uv, int rounding)
    {
        uv = (uv + rounding + (128 << (YuvFix + 2))) >> (YuvFix + 2);
        return (uv & ~0xff) == 0 ? uv : uv < 0 ? 0 : 255;
    }

    private static int RgbToU(int r, int g, int b, int rounding) => ClipUv((-9719 * r) - (19081 * g) + (28800 * b), rounding);

    private static int RgbToV(int r, int g, int b, int rounding) => ClipUv((28800 * r) - (24116 * g) - (4684 * b), rounding);

    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void ConvertToYuv(byte[] rgba)
    {
        int w = _w;
        int h = _h;
        for (int y = 0; y < _mbh * 16; y++)
        {
            int sy = Math.Min(y, h - 1);
            for (int x = 0; x < _ys; x++)
            {
                int sx = Math.Min(x, w - 1);
                int o = ((sy * w) + sx) * 4;
                _srcY[(y * _ys) + x] = (byte)RgbToY(rgba[o], rgba[o + 1], rgba[o + 2]);
            }
        }

        for (int cy = 0; cy < _mbh * 8; cy++)
        {
            int y0 = Math.Min(2 * cy, h - 1);
            int y1 = Math.Min((2 * cy) + 1, h - 1);
            for (int cx = 0; cx < _uvs; cx++)
            {
                int x0 = Math.Min(2 * cx, w - 1);
                int x1 = Math.Min((2 * cx) + 1, w - 1);
                int o00 = ((y0 * w) + x0) * 4;
                int o01 = ((y0 * w) + x1) * 4;
                int o10 = ((y1 * w) + x0) * 4;
                int o11 = ((y1 * w) + x1) * 4;
                int r = rgba[o00] + rgba[o01] + rgba[o10] + rgba[o11];
                int g = rgba[o00 + 1] + rgba[o01 + 1] + rgba[o10 + 1] + rgba[o11 + 1];
                int b = rgba[o00 + 2] + rgba[o01 + 2] + rgba[o10 + 2] + rgba[o11 + 2];
                _srcU[(cy * _uvs) + cx] = (byte)RgbToU(r, g, b, YuvHalf << 2);
                _srcV[(cy * _uvs) + cx] = (byte)RgbToV(r, g, b, YuvHalf << 2);
            }
        }
    }

    // ---- quality mapping and segments ----

    private static double QualityToCompression(double c)
    {
        double linear = c < 0.75 ? c * (2.0 / 3.0) : (2.0 * c) - 1.0;
        return Math.Pow(linear, 1.0 / 3.0);
    }

    private static int Clip(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private byte[] Run()
    {
        Analyze();
        SetupSegments();
        EncodeMacroblocks();
        return WriteStream();
    }

    // Texture analysis: a complexity score per macroblock, clustered into segments (spectral noise shaping).
    private void Analyze()
    {
        int n = _mbw * _mbh;
        int[] mbAlpha = new int[n];
        var scratch = new Scratch?[Workers(_mbh)];
        RunRows(scratch, (s, mby) => AnalyzeRow(s, mby, mbAlpha));
        int[] alphas = new int[256];
        long uvAlphaSum = 0;
        foreach (Scratch? s in scratch)
        {
            if (s == null)
            {
                continue;
            }

            for (int i = 0; i < 256; i++)
            {
                alphas[i] += s.Alphas[i];
            }

            uvAlphaSum += s.UvAlphaSum;
        }

        _uvAlpha = (int)((uvAlphaSum + (n / 2)) / n);
        AssignSegments(alphas, mbAlpha);
    }

    private void AnalyzeRow(Scratch s, int mby, int[] mbAlpha)
    {
        Span<short> coef = stackalloc short[16];
        int[] distribution = s.Distribution;
        byte[] w = s.Work;
        for (int mbx = 0; mbx < _mbw; mbx++)
        {
            // Neighbors for the analysis come from the source image itself.
            VP8Borders.Prepare(w, _srcY, _srcU, _srcV, _ys, _uvs, mbx, mby, _mbw);
            LoadSource(s, mbx, mby);
            int bestY = -1;
            for (int mode = 0; mode < 2; mode++)
            {
                Predict.Block(w, YOff, 16, mode == 0 ? VP8Decoder.DcPred : VP8Decoder.TmPred, mbx > 0, mby > 0);
                Array.Clear(distribution);
                for (int b = 0; b < 16; b++)
                {
                    VP8Fwd.Fdct(s.SrcMb, ((b >> 2) * 4 * 16) + ((b & 3) * 4), 16, w, YOff + ((b >> 2) * 4 * Bps) + ((b & 3) * 4), coef);
                    AddToDistribution(distribution, coef);
                }

                bestY = Math.Max(bestY, GetAlpha(distribution));
            }

            int bestUv = -1;
            for (int mode = 0; mode < 2; mode++)
            {
                int m = mode == 0 ? VP8Decoder.DcPred : VP8Decoder.TmPred;
                Predict.Block(w, UOff, 8, m, mbx > 0, mby > 0);
                Predict.Block(w, VOff, 8, m, mbx > 0, mby > 0);
                Array.Clear(distribution);
                for (int b = 0; b < 4; b++)
                {
                    int so = 256 + ((b >> 1) * 4 * 8) + ((b & 1) * 4);
                    VP8Fwd.Fdct(s.SrcMb, so, 8, w, UOff + ((b >> 1) * 4 * Bps) + ((b & 1) * 4), coef);
                    AddToDistribution(distribution, coef);
                    VP8Fwd.Fdct(s.SrcMb, so + 64, 8, w, VOff + ((b >> 1) * 4 * Bps) + ((b & 1) * 4), coef);
                    AddToDistribution(distribution, coef);
                }

                bestUv = Math.Max(bestUv, GetAlpha(distribution));
            }

            int alpha = ((3 * bestY) + bestUv + 2) >> 2;
            alpha = Clip(255 - alpha, 0, 255);
            s.Alphas[alpha]++;
            mbAlpha[(mby * _mbw) + mbx] = alpha;
            s.UvAlphaSum += bestUv;
        }
    }

    // Per-thread scratch state: everything a macroblock coder mutates besides the shared planes and neighbor contexts.
    private sealed class Scratch
    {
        public readonly byte[] Work = new byte[Bps * 27];
        public readonly byte[] Scr = new byte[Bps * 27];
        public readonly byte[] SrcMb = new byte[(16 * 16) + (2 * 64)];
        public readonly short[] Deq = new short[256];
        public readonly byte[] Preds = new byte[10 * 16];
        public readonly byte[] Rec4 = new byte[16];
        public readonly byte[] IntraLeft = new byte[4];
        public readonly byte[] NzLeft = new byte[9];
        public readonly int[] Distribution = new int[32];
        public readonly int[] Alphas = new int[256];
        public readonly int[] MaxEdge = new int[NumSegments];
        public readonly int[] StatsOnes = new int[1056];
        public readonly int[] StatsTotal = new int[1056];
        public readonly int[] StatsOnesCoded = new int[1056];
        public readonly int[] StatsTotalCoded = new int[1056];
        public long UvAlphaSum;
        public long I16Distortion;
        public readonly Cand I16 = new();
        public readonly Cand I4 = new();
        public readonly Cand Uv = new();
        public readonly Cand Tmp = new();
    }

    private int Workers(int rows) => Math.Max(1, Math.Min(_degree, rows));

    // Runs fn once per macroblock row. With a wavefront, row y starts a macroblock only once row y-1 has finished the one
    // up and to the right of it (the top-right pixels feed prediction), so any thread count produces the same bytes.
    private void RunRows(Scratch?[] scratch, Action<Scratch, int> fn)
    {
        int workers = scratch.Length;
        if (workers == 1)
        {
            var s = scratch[0] = new Scratch();
            for (int y = 0; y < _mbh; y++)
            {
                _token.ThrowIfCancellationRequested();
                fn(s, y);
            }

            return;
        }

        int next = -1;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = _token }, wi =>
        {
            var s = scratch[wi] = new Scratch();
            while (true)
            {
                _token.ThrowIfCancellationRequested();
                int y = Interlocked.Increment(ref next);
                if (y >= _mbh)
                {
                    return;
                }

                fn(s, y);
            }
        });
    }

    // Waits until row y-1 has completed macroblock column x+1 (or its last column), which is all the prediction of (x, y) reads.
    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void WaitForRowAbove(int y, int x)
    {
        if (y == 0)
        {
            return;
        }

        int need = Math.Min(x + 2, _mbw);
        if (Volatile.Read(ref _rowProgress[y - 1]) >= need)
        {
            return;
        }

        var spin = new SpinWait();
        while (Volatile.Read(ref _rowProgress[y - 1]) < need)
        {
            _token.ThrowIfCancellationRequested();
            // Never sleep: the row above is at most a few macroblocks away, and a one millisecond nap costs more than the whole wait.
            spin.SpinOnce(-1);
        }
    }

    private int[] _rowProgress = [];
    private Scratch?[] _scratches = [];

    private int _uvAlpha;

    private static void AddToDistribution(int[] distribution, ReadOnlySpan<short> coef)
    {
        for (int k = 0; k < 16; k++)
        {
            int v = Math.Abs(coef[k]) >> 3;
            distribution[v > 31 ? 31 : v]++;
        }
    }

    private static int GetAlpha(int[] distribution)
    {
        int maxValue = 0;
        int lastNonZero = 1;
        for (int k = 0; k < 32; k++)
        {
            int v = distribution[k];
            if (v > 0)
            {
                if (v > maxValue)
                {
                    maxValue = v;
                }

                lastNonZero = k;
            }
        }

        return maxValue > 1 ? 510 * lastNonZero / maxValue : 0;
    }

    private void AssignSegments(int[] alphas, int[] mbAlpha)
    {
        int nb = NumSegments;
        int[] centers = new int[nb];
        int minA = 0;
        while (minA <= 255 && alphas[minA] == 0)
        {
            minA++;
        }

        int maxA = 255;
        while (maxA > minA && alphas[maxA] == 0)
        {
            maxA--;
        }

        int range = maxA - minA;
        for (int k = 0, n = 1; k < nb; k++, n += 2)
        {
            centers[k] = minA + (n * range / (2 * nb));
        }

        int[] map = new int[256];
        int weightedAverage = 0;
        int[] accum = new int[nb];
        int[] distAccum = new int[nb];
        for (int iter = 0; iter < 6; iter++)
        {
            Array.Clear(accum);
            Array.Clear(distAccum);
            int n = 0;
            for (int a = minA; a <= maxA; a++)
            {
                if (alphas[a] != 0)
                {
                    while (n + 1 < nb && Math.Abs(a - centers[n + 1]) < Math.Abs(a - centers[n]))
                    {
                        n++;
                    }

                    map[a] = n;
                    distAccum[n] += a * alphas[a];
                    accum[n] += alphas[a];
                }
            }

            int displaced = 0;
            weightedAverage = 0;
            int totalWeight = 0;
            for (int k = 0; k < nb; k++)
            {
                if (accum[k] != 0)
                {
                    int newCenter = (distAccum[k] + (accum[k] / 2)) / accum[k];
                    displaced += Math.Abs(centers[k] - newCenter);
                    centers[k] = newCenter;
                    weightedAverage += newCenter * accum[k];
                    totalWeight += accum[k];
                }
            }

            weightedAverage = (weightedAverage + (totalWeight / 2)) / Math.Max(1, totalWeight);
            if (displaced < 5)
            {
                break;
            }
        }

        for (int i = 0; i < mbAlpha.Length; i++)
        {
            _mbSegment[i] = (byte)map[mbAlpha[i]];
        }

        int min = centers[0];
        int max = centers[0];
        for (int k = 1; k < nb; k++)
        {
            min = Math.Min(min, centers[k]);
            max = Math.Max(max, centers[k]);
        }

        if (max == min)
        {
            max = min + 1;
        }

        for (int k = 0; k < nb; k++)
        {
            _seg[k] = new Segment
            {
                Alpha = Clip(255 * (centers[k] - weightedAverage) / (max - min), -127, 127),
                Beta = Clip(255 * (centers[k] - min) / (max - min), 0, 255),
            };
        }
    }

    private void SetupSegments()
    {
        const int SnsStrength = 50;
        const int FilterStrengthSetting = 60;
        double amp = 0.9 * SnsStrength / 100.0 / 128.0;
        double cBase = QualityToCompression(_quality / 100.0);
        for (int i = 0; i < NumSegments; i++)
        {
            double expn = 1.0 - (amp * _seg[i].Alpha);
            double c = Math.Pow(cBase, expn);
            _seg[i].Quant = Clip((int)(127.0 * (1.0 - c)), 0, 127);
        }

        // Chroma quantizer offsets from the average chroma complexity.
        _dqUvAc = ((_uvAlpha - 64) * (6 - -4)) / 255;
        _dqUvAc = Clip(_dqUvAc * SnsStrength / 100, -4, 6);
        _dqUvDc = Clip(-4 * SnsStrength / 100, -15, 15);

        for (int i = 0; i < NumSegments; i++)
        {
            Segment s = _seg[i];
            int q = s.Quant;
            int y1Dc = VP8Tables.DcQLookup[Clip(q, 0, 127)];
            int y1Ac = VP8Tables.AcQLookup[Clip(q, 0, 127)];
            int y2Dc = VP8Tables.DcQLookup[Clip(q, 0, 127)] * 2;
            int y2Ac = Math.Max(8, (VP8Tables.AcQLookup[Clip(q, 0, 127)] * 101581) >> 16);
            int uvDc = VP8Tables.DcQLookup[Clip(q + _dqUvDc, 0, 117)];
            int uvAc = VP8Tables.AcQLookup[Clip(q + _dqUvAc, 0, 127)];
            s.Y1 = new VP8QuantMatrix(y1Dc, y1Ac, 96, 110, sharpen: true);
            s.Y2 = new VP8QuantMatrix(y2Dc, y2Ac, 96, 108, sharpen: false);
            s.Uv = new VP8QuantMatrix(uvDc, uvAc, 110, 115, sharpen: false);
            s.LambdaI16 = 3 * y2Ac * y2Ac;
            s.LambdaI4 = (3 * y1Ac * y1Ac) >> 7;
            s.LambdaUv = (3 * uvAc * uvAc) >> 6;
            s.LambdaMode = (y1Ac * y1Ac) >> 7;

            // Loop filter strength follows the quantizer step; flatter segments are filtered less.
            int qstep = VP8Tables.AcQLookup[Clip(q, 0, 127)] >> 2;
            int f = qstep * (5 * FilterStrengthSetting) / (256 + s.Beta);
            s.FilterStrength = f < 2 ? 0 : f > 63 ? 63 : f;
        }
    }

    // Filter strengths that would smooth the strongest block edge seen in each segment (libwebp's VP8AdjustFilterStrength).
    private static readonly byte[] LevelsFromDelta =
    [
        0, 1, 2, 3, 5, 6, 7, 8, 9, 11, 12, 13, 14, 15, 17, 18, 20, 21, 23, 24, 26, 27, 29, 30, 32, 33, 35, 36, 38, 39, 41, 42,
        44, 45, 47, 48, 50, 51, 53, 54, 56, 57, 59, 60, 62, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    ];

    private void AdjustFilterStrength()
    {
        for (int i = 0; i < NumSegments; i++)
        {
            Segment s = _seg[i];
            int delta = (s.MaxEdge * s.Y2.Q[1]) >> 3;
            int level = LevelsFromDelta[Math.Min(delta, 63)];
            if (level > s.FilterStrength)
            {
                s.FilterStrength = level;
            }
        }
    }


    private void LoadSource(Scratch s, int mbx, int mby)
    {
        for (int j = 0; j < 16; j++)
        {
            _srcY.AsSpan((((mby * 16) + j) * _ys) + (mbx * 16), 16).CopyTo(s.SrcMb.AsSpan(j * 16, 16));
        }

        for (int j = 0; j < 8; j++)
        {
            int o = (((mby * 8) + j) * _uvs) + (mbx * 8);
            _srcU.AsSpan(o, 8).CopyTo(s.SrcMb.AsSpan(256 + (j * 8), 8));
            _srcV.AsSpan(o, 8).CopyTo(s.SrcMb.AsSpan(256 + 64 + (j * 8), 8));
        }
    }

    // ---- macroblock coding ----

    private static readonly int[] I16ModeBits = [BitsOf(156, 0) + BitsOf(163, 0), BitsOf(156, 1) + BitsOf(128, 1), BitsOf(156, 0) + BitsOf(163, 1), BitsOf(156, 1) + BitsOf(128, 0)];

    private static int BitsOf(int prob, int bit) => VP8Fwd.BitCost(bit, prob);

    private void EncodeMacroblocks()
    {
        Array.Clear(_intraTop);
        Array.Clear(_nzTop);
        _rowProgress = new int[_mbh];
        var scratch = new Scratch?[Workers(_mbh)];
        RunRows(scratch, (s, mby) =>
        {
            Array.Clear(s.IntraLeft);
            Array.Clear(s.NzLeft);
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                WaitForRowAbove(mby, mbx);
                EncodeMacroblock(s, mbx, mby);
                Volatile.Write(ref _rowProgress[mby], mbx + 1);
            }
        });
        _scratches = scratch;
        foreach (Scratch? s in scratch)
        {
            for (int i = 0; s != null && i < NumSegments; i++)
            {
                _seg[i].MaxEdge = Math.Max(_seg[i].MaxEdge, s.MaxEdge[i]);
            }
        }
    }

    private static byte[] DefaultProbs() => VP8Tables.DefaultCoeffProbs.ToArray();

    private sealed class Cand
    {
        public readonly short[] Levels = new short[LevelsPerMb];
        public readonly byte[] Recon = new byte[Bps * 27];
        public readonly byte[] BModes = new byte[16];
        public long Score;
        public long Dist;
        public int Rate;
    }


    private void EncodeMacroblock(Scratch s, int mbx, int mby)
    {
        int mb = (mby * _mbw) + mbx;
        Segment sp = _seg[_mbSegment[mb]];
        VP8Borders.Prepare(s.Work, _recY, _recU, _recV, _ys, _uvs, mbx, mby, _mbw);
        LoadSource(s, mbx, mby);

        // Chroma and 16x16 luma are evaluated first; 4x4 only if it can beat the 16x16 result.
        int uvMode = ChooseUv(s, mbx, mby, sp, s.Uv);
        int i16Mode = ChooseI16(s, mbx, mby, sp, s.I16);
        bool useI4 = false;
        if (_effort >= 1)
        {
            // The 4x4 search overwrites the work buffer's luma area, which is fine: the 16x16 reconstruction is saved.
            useI4 = TryI4(s, mbx, mby, sp, s.I4, ModeScore(s.I16.Dist, s.I16.Rate, sp));
        }

        Cand y = useI4 ? s.I4 : s.I16;
        // A blocky 16x16 candidate (only luma DC levels, with visible distortion) records how strong the loop filter must be to
        // smooth the block edges out; the segment's filter strength is raised to that afterwards. It counts even when the
        // 4x4 coding wins, as the filter has to cope with such blocks wherever they appear.
        ReadOnlySpan<short> lv16 = s.I16.Levels;
        bool onlyDc = true;
        for (int i = 16; i < 16 + 256 && onlyDc; i++)
        {
            onlyDc = lv16[i] == 0 || (i - 16) % 16 == 0;
        }

        bool anyDc = false;
        for (int i = 0; i < 16 && !anyDc; i++)
        {
            anyDc = lv16[i] != 0;
        }

        if (onlyDc && anyDc && s.I16Distortion > 20 * sp.Y1.Q[0])
        {
            int maxV = Math.Max(Math.Abs((int)lv16[1]), Math.Max(Math.Abs((int)lv16[2]), Math.Abs((int)lv16[4])));
            s.MaxEdge[_mbSegment[mb]] = Math.Max(s.MaxEdge[_mbSegment[mb]], maxV);
        }
        _isI4[mb] = (byte)(useI4 ? 1 : 0);
        _yMode[mb] = (byte)i16Mode;
        _uvMode[mb] = (byte)uvMode;
        if (useI4)
        {
            Array.Copy(y.BModes, 0, _bModes, mb * 16, 16);
        }

        // Merge levels: luma from the chosen candidate, chroma from the chroma candidate.
        Span<short> dst = _levels.AsSpan(mb * LevelsPerMb, LevelsPerMb);
        y.Levels.AsSpan(0, 16 + 256).CopyTo(dst);
        s.Uv.Levels.AsSpan(16 + 256, 128).CopyTo(dst[(16 + 256)..]);
        if (useI4)
        {
            dst[..16].Clear();
        }

        bool allZero = true;
        foreach (short l in dst)
        {
            if (l != 0)
            {
                allZero = false;
                break;
            }
        }

        _skip[mb] = (byte)(allZero ? 1 : 0);

        // Update neighbor contexts exactly like the decoder will.
        UpdateContexts(s, mbx, mb, useI4);

        // Store the reconstruction.
        for (int j = 0; j < 16; j++)
        {
            y.Recon.AsSpan(YOff + (j * Bps), 16).CopyTo(_recY.AsSpan((((mby * 16) + j) * _ys) + (mbx * 16), 16));
        }

        for (int j = 0; j < 8; j++)
        {
            int o = (((mby * 8) + j) * _uvs) + (mbx * 8);
            s.Uv.Recon.AsSpan(UOff + (j * Bps), 8).CopyTo(_recU.AsSpan(o, 8));
            s.Uv.Recon.AsSpan(VOff + (j * Bps), 8).CopyTo(_recV.AsSpan(o, 8));
        }
    }

    // nz layout per macroblock column: Y 0..3, U 4..5, V 6..7, DC 8.
    // Counts the macroblock's tokens for the probability statistics and moves the non-zero contexts on past it.
    private void UpdateContexts(Scratch s, int mbx, int mb, bool i4)
    {
        ReadOnlySpan<short> lv = _levels.AsSpan(mb * LevelsPerMb, LevelsPerMb);
        var stats = new DualStatsSink(s.StatsOnes, s.StatsTotal, s.StatsOnesCoded, s.StatsTotalCoded, _skip[mb] == 0);
        ScanMacroblock(ref stats, lv, i4, _nzTop.AsSpan(mbx * 9, 9), s.NzLeft);
        for (int x = 0; x < 4; x++)
        {
            _intraTop[(mbx * 4) + x] = i4 ? _bModes[(mb * 16) + 12 + x] : MapMode(_yMode[mb]);
        }

        for (int y = 0; y < 4; y++)
        {
            s.IntraLeft[y] = i4 ? _bModes[(mb * 16) + (y * 4) + 3] : MapMode(_yMode[mb]);
        }
    }

    private static byte MapMode(int ymode) => ymode switch
    {
        VP8Decoder.DcPred => VP8Decoder.BDc,
        VP8Decoder.TmPred => VP8Decoder.BTm,
        VP8Decoder.VPred => VP8Decoder.BVe,
        _ => VP8Decoder.BHe,
    };

    private static int LastNonZero(ReadOnlySpan<short> levels, int first)
    {
        for (int i = 15; i >= first; i--)
        {
            if (levels[i] != 0)
            {
                return i;
            }
        }

        return -1;
    }

    // Adds a dequantized residual to the prediction already stored in buf.
    private static void AddResidual(byte[] buf, int off, int stride, ReadOnlySpan<short> coeffs, bool anyAc, bool anyCoeff)
    {
        if (!anyCoeff)
        {
            return;
        }

        if (!anyAc)
        {
            int dc = (coeffs[0] + 4) >> 3;
            for (int j = 0; j < 4; j++)
            {
                Span<byte> row = buf.AsSpan(off + (j * stride), 4);
                for (int i = 0; i < 4; i++)
                {
                    int v = row[i] + dc;
                    row[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                }
            }

            return;
        }

        WebPIdctKernel.Add(coeffs, buf, off, stride);
    }

    // Score for choosing between 16x16 and 4x4 coding: the rate weighs little, so this is mostly a distortion comparison.
    private static long ModeScore(long distortion, int rate256, Segment sp) => Score(distortion, rate256, sp.LambdaMode);

    private static long Score(long distortion, int rate256, double lambda) => (distortion * 256) + (long)(rate256 * lambda);

    private int ChooseI16(Scratch s, int mbx, int mby, Segment sp, Cand best)
    {
        byte[] w = s.Work;
        int bestMode = 0;
        best.Score = long.MaxValue;
        Span<short> coef = stackalloc short[16];
        Span<short> dc = stackalloc short[16];
        Span<short> y2 = stackalloc short[16];
        short[] deq = s.Deq;
        Span<short> lv = s.Tmp.Levels;
        Span<byte> tnz = stackalloc byte[4];
        Span<byte> lnz = stackalloc byte[4];
        for (int mode = 0; mode < 4; mode++)
        {
            Predict.Block(w, YOff, 16, mode, mbx > 0, mby > 0);
            for (int b = 0; b < 16; b++)
            {
                WebPFdctKernel.Forward(s.SrcMb, ((b >> 2) * 4 * 16) + ((b & 3) * 4), 16, w, YOff + ((b >> 2) * 4 * Bps) + ((b & 3) * 4), Bps, coef);
                coef.CopyTo(deq.AsSpan(b * 16, 16));
                dc[b] = coef[0];
            }

            VP8Fwd.Wht(dc, y2);
            lv.Clear();
            int lastDc = sp.Y2.Quantize(y2, lv[..16], 0);
            VP8Decoder.TransformWht(y2, deq);

            // Reconstruct into the scratch copy of the prediction.
            CopyLuma(w, s.Scr);
            int rate = VP8Cost.BlockCost(lv[..16], 1, 0, _nzTop[(mbx * 9) + 8] + s.NzLeft[8], lastDc);
            _nzTop.AsSpan(mbx * 9, 4).CopyTo(tnz);
            s.NzLeft.AsSpan(0, 4).CopyTo(lnz);
            for (int b = 0; b < 16; b++)
            {
                Span<short> blockCoef = deq.AsSpan(b * 16, 16);
                short savedDc = blockCoef[0];
                blockCoef[0] = 0;
                int last = sp.Y1.Quantize(blockCoef, lv.Slice(16 + (b * 16), 16), 1);
                lv[16 + (b * 16)] = 0;
                blockCoef[0] = savedDc;
                int bx = b & 3;
                int by = b >> 2;
                rate += VP8Cost.BlockCost(lv.Slice(16 + (b * 16), 16), 0, 1, lnz[by] + tnz[bx], last);
                byte nz = last >= 1 ? (byte)1 : (byte)0;
                lnz[by] = nz;
                tnz[bx] = nz;
                bool anyAc = last >= 1;
                AddResidual(s.Scr, YOff + (by * 4 * Bps) + (bx * 4), Bps, blockCoef, anyAc, anyAc || blockCoef[0] != 0);
            }

            rate += I16ModeBits[mode] + BitsOf(145, 1);
            long d = WebPSseKernel.Sse(s.SrcMb, 0, 16, s.Scr, YOff, Bps, 16);
            long score = Score(d, rate, sp.LambdaI16);
            if (score < best.Score)
            {
                best.Score = score;
                best.Dist = d;
                best.Rate = rate;
                bestMode = mode;
                s.I16Distortion = d;
                lv.CopyTo(best.Levels);
                CopyLuma(s.Scr, best.Recon);
            }
        }

        return bestMode;
    }

    // The luma part of a work buffer: 16 rows of 16 bytes at the macroblock's luma offset.
    private static void CopyLuma(byte[] from, byte[] to) =>
        from.AsSpan(YOff, (15 * Bps) + 16).CopyTo(to.AsSpan(YOff));

    // Bits of the 4x4 sub-block mode tree path, per (above mode, left mode, mode).
    private static readonly ushort[] SubModeBits = BuildSubModeBits();

    private static ushort[] BuildSubModeBits()
    {
        ReadOnlySpan<byte> kf = VP8Tables.KeyFrameBModeProbs;
        ushort[] t = new ushort[10 * 10 * 10];
        for (int ctx = 0; ctx < 100; ctx++)
        {
            for (int m = 0; m < 10; m++)
            {
                t[(ctx * 10) + m] = (ushort)ModeBits(kf.Slice(ctx * 9, 9), m);
            }
        }

        return t;
    }

    private bool TryI4(Scratch s, int mbx, int mby, Segment sp, Cand cand, long i16Score)
    {
        byte[] w = s.Work;
        VP8Borders.Prepare(w, _recY, _recU, _recV, _ys, _uvs, mbx, mby, _mbw);

        // The top-right pixels of the macroblock serve the right-most sub-block of every row.
        for (int r = 3; r < 16; r += 4)
        {
            for (int k = 0; k < 4; k++)
            {
                w[YOff + (r * Bps) + 16 + k] = w[YOff - Bps + 16 + k];
            }
        }

        Span<byte> modes = cand.BModes;
        Span<short> coef = stackalloc short[16];
        Span<short> lv = stackalloc short[16];
        Span<byte> tnz = stackalloc byte[4];
        Span<byte> lnz = stackalloc byte[4];
        _nzTop.AsSpan(mbx * 9, 4).CopyTo(tnz);
        s.NzLeft.AsSpan(0, 4).CopyTo(lnz);
        Span<int> modeScore = stackalloc int[10];
        Span<int> order = stackalloc int[10];
        Span<short> bestLv = stackalloc short[16];
        Span<byte> bestRec = stackalloc byte[16];
        byte[] preds = s.Preds;
        byte[] rec = s.Rec4;
        cand.Levels.AsSpan().Clear();
        CopyLuma(w, cand.Recon);
        long totalD = 0;
        int totalR = BitsOf(145, 0);
        int keep = _effort >= 6 ? 10 : _effort >= 5 ? 5 : 3;
        for (int b = 0; b < 16; b++)
        {
            int bx = b & 3;
            int by = b >> 2;
            int off = YOff + (by * 4 * Bps) + (bx * 4);
            int srcOff = (by * 4 * 16) + (bx * 4);
            int topMode = by == 0 ? _intraTop[(mbx * 4) + bx] : modes[((by - 1) * 4) + bx];
            int leftMode = bx == 0 ? s.IntraLeft[by] : modes[(by * 4) + bx - 1];
            int pb = (topMode * 10) + leftMode;

            // Rank the modes by the squared error of the plain prediction, then evaluate the best few in full.
            Predict.AllSubBlocks(w, off, preds);
            for (int m = 0; m < 10; m++)
            {
                modeScore[m] = (int)WebPSseKernel.Sse(s.SrcMb, srcOff, 16, preds, m * 16, 4, 4);
                order[m] = m;
            }

            for (int i = 1; i < 10; i++)
            {
                int key = order[i];
                int j = i - 1;
                while (j >= 0 && modeScore[order[j]] > modeScore[key])
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = key;
            }

            long bestScore = long.MaxValue;
            long bestD = 0;
            int bestR = 0;
            int bestMode = 0;
            for (int oi = 0; oi < keep; oi++)
            {
                int m = order[oi];
                WebPFdctKernel.Forward(s.SrcMb, srcOff, 16, preds, m * 16, 4, coef);
                int last = sp.Y1.Quantize(coef, lv, 0);
                Buffer.BlockCopy(preds, m * 16, rec, 0, 16);
                AddResidual(rec, 0, 4, coef, HasAc(lv), last >= 0);
                long d = WebPSseKernel.Sse(s.SrcMb, srcOff, 16, rec, 0, 4, 4);
                int rate = VP8Cost.BlockCost(lv, 3, 0, lnz[by] + tnz[bx], last);
                int mbits = SubModeBits[(pb * 10) + m];
                long score = Score(d, rate + mbits, sp.LambdaI4);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestD = d;
                    bestR = rate + mbits;
                    bestMode = m;
                    lv.CopyTo(bestLv);
                    rec.AsSpan(0, 16).CopyTo(bestRec);
                }
            }

            totalD += bestD;
            totalR += bestR;
            if (ModeScore(totalD, totalR, sp) >= i16Score)
            {
                return false;
            }

            modes[b] = (byte)bestMode;
            for (int j = 0; j < 4; j++)
            {
                bestRec.Slice(j * 4, 4).CopyTo(w.AsSpan(off + (j * Bps), 4));
                bestRec.Slice(j * 4, 4).CopyTo(cand.Recon.AsSpan(off + (j * Bps), 4));
            }

            bestLv.CopyTo(cand.Levels.AsSpan(16 + (b * 16), 16));
            byte nz = LastNonZero(bestLv, 0) >= 0 ? (byte)1 : (byte)0;
            lnz[by] = nz;
            tnz[bx] = nz;
        }

        cand.Score = ModeScore(totalD, totalR, sp);
        return cand.Score < i16Score;
    }

    private static bool HasAc(ReadOnlySpan<short> lv)
    {
        for (int i = 1; i < 16; i++)
        {
            if (lv[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int ModeBits(ReadOnlySpan<byte> p, int mode)
    {
        // Walk the mode tree to the leaf and add up the bit costs of the path.
        sbyte[] tree = VP8Decoder.BModeTreeShared;
        return PathBits(tree, p, 0, mode);
    }

    private static int PathBits(sbyte[] tree, ReadOnlySpan<byte> p, int node, int mode)
    {
        for (int bit = 0; bit < 2; bit++)
        {
            int child = tree[(2 * node) + bit];
            if (child <= 0)
            {
                if (-child == mode)
                {
                    return BitsOf(p[node], bit);
                }
            }
            else
            {
                int sub = PathBits(tree, p, child, mode);
                if (sub >= 0)
                {
                    return BitsOf(p[node], bit) + sub;
                }
            }
        }

        return -1;
    }

    private int ChooseUv(Scratch s, int mbx, int mby, Segment sp, Cand best)
    {
        byte[] w = s.Work;
        best.Score = long.MaxValue;
        int bestMode = 0;
        Span<short> coef = stackalloc short[16];
        Span<short> lv = stackalloc short[16];
        Span<byte> tnz = stackalloc byte[4];
        Span<byte> lnz = stackalloc byte[4];
        short[] tmpLv = s.Tmp.Levels;
        for (int mode = 0; mode < 4; mode++)
        {
            Predict.Block(w, UOff, 8, mode, mbx > 0, mby > 0);
            Predict.Block(w, VOff, 8, mode, mbx > 0, mby > 0);
            CopyChroma(w, s.Scr);
            int bits = 0;
            tmpLv.AsSpan(16 + 256, 128).Clear();
            long d = 0;
            for (int ch = 0; ch < 2; ch++)
            {
                int baseOff = ch == 0 ? UOff : VOff;
                int srcBase = 256 + (ch * 64);
                _nzTop.AsSpan((mbx * 9) + 4 + (ch * 2), 2).CopyTo(tnz);
                s.NzLeft.AsSpan(4 + (ch * 2), 2).CopyTo(lnz);
                for (int b = 0; b < 4; b++)
                {
                    int bx = b & 1;
                    int by = b >> 1;
                    int off = baseOff + (by * 4 * Bps) + (bx * 4);
                    WebPFdctKernel.Forward(s.SrcMb, srcBase + (by * 4 * 8) + (bx * 4), 8, w, off, Bps, coef);
                    int last = sp.Uv.Quantize(coef, lv, 0);
                    AddResidual(s.Scr, off, Bps, coef, HasAc(lv), last >= 0);
                    bits += VP8Cost.BlockCost(lv, 2, 0, lnz[by] + tnz[bx], last);
                    byte nz = last >= 0 ? (byte)1 : (byte)0;
                    lnz[by] = nz;
                    tnz[bx] = nz;
                    lv.CopyTo(tmpLv.AsSpan(16 + 256 + (ch * 64) + (b * 16), 16));
                }

                d += WebPSseKernel.Sse(s.SrcMb, srcBase, 8, s.Scr, baseOff, Bps, 8);
            }

            int uvBits = mode switch
            {
                VP8Decoder.DcPred => BitsOf(142, 0),
                VP8Decoder.VPred => BitsOf(142, 1) + BitsOf(114, 0),
                VP8Decoder.TmPred => BitsOf(142, 1) + BitsOf(114, 1) + BitsOf(183, 1),
                _ => BitsOf(142, 1) + BitsOf(114, 1) + BitsOf(183, 0),
            };
            long score = Score(d, bits + uvBits, sp.LambdaUv);
            if (score < best.Score)
            {
                best.Score = score;
                bestMode = mode;
                tmpLv.AsSpan(16 + 256, 128).CopyTo(best.Levels.AsSpan(16 + 256));
                CopyChroma(s.Scr, best.Recon);
            }
        }

        return bestMode;
    }

    // The chroma part of a work buffer: 8 rows of 8 bytes for each of U and V.
    private static void CopyChroma(byte[] from, byte[] to) =>
        from.AsSpan(UOff, VOff - UOff + (7 * Bps) + 8).CopyTo(to.AsSpan(UOff));

    // ---- bitstream ----

    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private byte[] WriteStream()
    {
        int n = _mbw * _mbh;
        // Token probabilities from the statistics of the coded levels.
        // The statistics were gathered while coding the macroblocks: every token, and the tokens of the macroblocks that are not skipped.
        int[] onesAll = new int[1056];
        int[] totalAll = new int[1056];
        int[] onesCoded = new int[1056];
        int[] totalCoded = new int[1056];
        foreach (Scratch? sc in _scratches)
        {
            for (int i = 0; sc != null && i < 1056; i++)
            {
                onesAll[i] += sc.StatsOnes[i];
                totalAll[i] += sc.StatsTotal[i];
                onesCoded[i] += sc.StatsOnesCoded[i];
                totalCoded[i] += sc.StatsTotalCoded[i];
            }
        }

        var stats = new StatsSink { Ones = onesAll, Total = totalAll };
        int skipCount = 0;
        for (int i = 0; i < n; i++)
        {
            skipCount += _skip[i];
        }

        int skipProba = n > 0 ? Clip(255 * (n - skipCount) / n, 1, 255) : 255;
        bool useSkip = skipCount > 0 && skipProba < 250;
        if (useSkip)
        {
            stats = new StatsSink { Ones = onesCoded, Total = totalCoded };
        }

        byte[] probs = DefaultProbs();
        byte[] def = DefaultProbs();
        bool[] update = new bool[1056];
        ReadOnlySpan<byte> updateProbs = VP8Tables.CoeffUpdateProbs;
        for (int i = 0; i < 1056; i++)
        {
            int tot = stats.Total[i];
            if (tot == 0)
            {
                continue;
            }

            int ones = stats.Ones[i];
            int zeros = tot - ones;
            int pNew = Clip(((zeros * 256) + (tot / 2)) / tot, 1, 255);
            int pOld = def[i];
            long costOld = ((long)zeros * VP8Fwd.BitCost0[pOld]) + ((long)ones * VP8Fwd.BitCost1[pOld]) + VP8Fwd.BitCost0[updateProbs[i]];
            long costNew = ((long)zeros * VP8Fwd.BitCost0[pNew]) + ((long)ones * VP8Fwd.BitCost1[pNew]) + VP8Fwd.BitCost1[updateProbs[i]] + (8 * 256);
            if (costNew < costOld)
            {
                update[i] = true;
                probs[i] = (byte)pNew;
            }
        }

        // Segment map probabilities.
        int[] segCount = new int[NumSegments];
        for (int i = 0; i < n; i++)
        {
            segCount[_mbSegment[i]]++;
        }

        byte[] segProbs = [255, 255, 255];
        segProbs[0] = ProbOfZero(segCount[0] + segCount[1], segCount[2] + segCount[3]);
        segProbs[1] = ProbOfZero(segCount[0], segCount[1]);
        segProbs[2] = ProbOfZero(segCount[2], segCount[3]);
        int usedSegments = 0;
        int onlySegment = 0;
        for (int s = 0; s < NumSegments; s++)
        {
            if (segCount[s] > 0)
            {
                usedSegments++;
                onlySegment = s;
            }
        }

        bool useSegments = usedSegments > 1;
        Segment head = useSegments ? _seg[0] : _seg[onlySegment];
        AdjustFilterStrength();
        int frameLevel = 0;
        for (int s = 0; s < NumSegments; s++)
        {
            frameLevel = Math.Max(frameLevel, _seg[s].FilterStrength);
        }

        if (!useSegments)
        {
            frameLevel = head.FilterStrength;
        }

        var bw = new VP8BoolWriter(n * 4);
        bw.PutBit(0, 128);
        bw.PutBit(0, 128);
        if (useSegments)
        {
            bw.PutBit(1, 128);
            bw.PutBit(1, 128); // update map
            bw.PutBit(1, 128); // update data
            bw.PutBit(1, 128); // absolute values
            for (int s = 0; s < NumSegments; s++)
            {
                bw.PutBit(1, 128);
                bw.PutSignedLiteral(_seg[s].Quant, 7);
            }

            for (int s = 0; s < NumSegments; s++)
            {
                bw.PutBit(1, 128);
                bw.PutSignedLiteral(_seg[s].FilterStrength, 6);
            }

            for (int s = 0; s < 3; s++)
            {
                if (segProbs[s] != 255)
                {
                    bw.PutBit(1, 128);
                    bw.PutLiteral(segProbs[s], 8);
                }
                else
                {
                    bw.PutBit(0, 128);
                }
            }
        }
        else
        {
            bw.PutBit(0, 128);
        }

        int baseQuant = head.Quant;
        bw.PutBit(0, 128); // normal (not simple) loop filter
        bw.PutLiteral(frameLevel, 6);
        bw.PutLiteral(0, 3); // sharpness
        bw.PutBit(0, 128); // no filter deltas
        bw.PutLiteral(0, 2); // one token partition
        bw.PutLiteral(baseQuant, 7);
        bw.PutBit(0, 128); // y1 dc delta
        bw.PutBit(0, 128); // y2 dc delta
        bw.PutBit(0, 128); // y2 ac delta
        if (_dqUvDc != 0)
        {
            bw.PutBit(1, 128);
            bw.PutSignedLiteral(_dqUvDc, 4);
        }
        else
        {
            bw.PutBit(0, 128);
        }

        if (_dqUvAc != 0)
        {
            bw.PutBit(1, 128);
            bw.PutSignedLiteral(_dqUvAc, 4);
        }
        else
        {
            bw.PutBit(0, 128);
        }

        bw.PutBit(0, 128); // refresh entropy probs
        for (int i = 0; i < 1056; i++)
        {
            bw.PutBit(update[i] ? 1 : 0, updateProbs[i]);
            if (update[i])
            {
                bw.PutLiteral(probs[i], 8);
            }
        }

        bw.PutBit(useSkip ? 1 : 0, 128);
        if (useSkip)
        {
            bw.PutLiteral(skipProba, 8);
        }

        // Macroblock headers.
        ReadOnlySpan<byte> kf = VP8Tables.KeyFrameBModeProbs;
        Array.Clear(_intraTop);
        for (int mby = 0; mby < _mbh; mby++)
        {
            Array.Clear(_intraLeft);
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                int mb = (mby * _mbw) + mbx;
                if (useSegments)
                {
                    int s = _mbSegment[mb];
                    bw.PutBit(s >> 1, segProbs[0]);
                    bw.PutBit(s & 1, segProbs[1 + (s >> 1)]);
                }

                if (useSkip)
                {
                    bw.PutBit(_skip[mb], skipProba);
                }

                if (_isI4[mb] != 0)
                {
                    bw.PutBit(0, 145);
                    for (int y = 0; y < 4; y++)
                    {
                        int left = _intraLeft[y];
                        for (int x = 0; x < 4; x++)
                        {
                            int mode = _bModes[(mb * 16) + (y * 4) + x];
                            int pb = ((_intraTop[(mbx * 4) + x] * 10) + left) * 9;
                            WriteMode(bw, kf.Slice(pb, 9), mode);
                            _intraTop[(mbx * 4) + x] = (byte)mode;
                            left = mode;
                        }

                        _intraLeft[y] = (byte)left;
                    }
                }
                else
                {
                    bw.PutBit(1, 145);
                    int ym = _yMode[mb];
                    switch (ym)
                    {
                        case VP8Decoder.DcPred:
                            bw.PutBit(0, 156);
                            bw.PutBit(0, 163);
                            break;
                        case VP8Decoder.VPred:
                            bw.PutBit(0, 156);
                            bw.PutBit(1, 163);
                            break;
                        case VP8Decoder.HPred:
                            bw.PutBit(1, 156);
                            bw.PutBit(0, 128);
                            break;
                        default:
                            bw.PutBit(1, 156);
                            bw.PutBit(1, 128);
                            break;
                    }

                    byte b = MapMode(ym);
                    _intraTop.AsSpan(mbx * 4, 4).Fill(b);
                    _intraLeft.AsSpan().Fill(b);
                }

                switch (_uvMode[mb])
                {
                    case VP8Decoder.DcPred:
                        bw.PutBit(0, 142);
                        break;
                    case VP8Decoder.VPred:
                        bw.PutBit(1, 142);
                        bw.PutBit(0, 114);
                        break;
                    case VP8Decoder.TmPred:
                        bw.PutBit(1, 142);
                        bw.PutBit(1, 114);
                        bw.PutBit(1, 183);
                        break;
                    default:
                        bw.PutBit(1, 142);
                        bw.PutBit(1, 114);
                        bw.PutBit(0, 183);
                        break;
                }
            }
        }

        byte[] part0 = bw.Finish();
        if (part0.Length >= 1 << 19)
        {
            throw new ImageOperationException("The image is too large for a single VP8 mode partition.");
        }

        var tw = new VP8BoolWriter(n * 16);
        var sink = new WriterSink(probs, tw);
        ScanTokens(ref sink, useSkip);
        byte[] part1 = tw.Finish();

        byte[] frame = new byte[10 + part0.Length + part1.Length];
        uint tag = (uint)((0 /* key frame */ | (0 << 1) | (1 << 4)) | (part0.Length << 5));
        frame[0] = (byte)tag;
        frame[1] = (byte)(tag >> 8);
        frame[2] = (byte)(tag >> 16);
        frame[3] = 0x9d;
        frame[4] = 0x01;
        frame[5] = 0x2a;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)_w);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)_h);
        part0.CopyTo(frame, 10);
        part1.CopyTo(frame, 10 + part0.Length);
        return frame;
    }

    private static byte ProbOfZero(int zeros, int ones)
    {
        int tot = zeros + ones;
        if (tot == 0 || ones == 0)
        {
            return 255;
        }

        return (byte)Clip(((zeros * 256) + (tot / 2)) / tot, 1, 255);
    }

    private static void WriteMode(VP8BoolWriter bw, ReadOnlySpan<byte> p, int mode)
    {
        sbyte[] tree = VP8Decoder.BModeTreeShared;
        Span<int> nodes = stackalloc int[8];
        Span<int> bits = stackalloc int[8];
        int count = FindPath(tree, 0, mode, nodes, bits, 0);
        for (int i = 0; i < count; i++)
        {
            bw.PutBit(bits[i], p[nodes[i]]);
        }
    }

    private static int FindPath(sbyte[] tree, int node, int mode, Span<int> nodes, Span<int> bits, int depth)
    {
        for (int bit = 0; bit < 2; bit++)
        {
            int child = tree[(2 * node) + bit];
            nodes[depth] = node;
            bits[depth] = bit;
            if (child <= 0)
            {
                if (-child == mode)
                {
                    return depth + 1;
                }
            }
            else
            {
                int r = FindPath(tree, child, mode, nodes, bits, depth + 1);
                if (r > 0)
                {
                    return r;
                }
            }
        }

        return 0;
    }

    // Walks the coded macroblocks in stream order, feeding every block's tokens to the sink.
    private void ScanTokens<TSink>(ref TSink sink, bool useSkip)
        where TSink : struct, ITokenSink
    {
        Array.Clear(_nzTop);
        for (int mby = 0; mby < _mbh; mby++)
        {
            Array.Clear(_nzLeft);
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                int mb = (mby * _mbw) + mbx;
                Span<byte> top = _nzTop.AsSpan(mbx * 9, 9);
                Span<byte> left = _nzLeft;
                bool i4 = _isI4[mb] != 0;
                if (useSkip && _skip[mb] != 0)
                {
                    top[..8].Clear();
                    left[..8].Clear();
                    if (!i4)
                    {
                        top[8] = 0;
                        left[8] = 0;
                    }

                    continue;
                }

                ScanMacroblock(ref sink, _levels.AsSpan(mb * LevelsPerMb, LevelsPerMb), i4, top, left);
            }
        }
    }

    // Feeds one macroblock's tokens to the sink in stream order and updates the top and left non-zero contexts.
    private static void ScanMacroblock<TSink>(ref TSink sink, ReadOnlySpan<short> lv, bool i4, Span<byte> top, Span<byte> left)
        where TSink : struct, ITokenSink
    {
        int first = 0;
        int type = 3;
        if (!i4)
        {
            int last = VP8Tokens.EmitBlock(ref sink, lv[..16], 1, 0, top[8] + left[8]);
            top[8] = left[8] = last >= 0 ? (byte)1 : (byte)0;
            first = 1;
            type = 0;
        }

        for (int b = 0; b < 16; b++)
        {
            int bx = b & 3;
            int by = b >> 2;
            int last = VP8Tokens.EmitBlock(ref sink, lv.Slice(16 + (b * 16), 16), type, first, left[by] + top[bx]);
            byte nz = last >= first ? (byte)1 : (byte)0;
            left[by] = nz;
            top[bx] = nz;
        }

        for (int ch = 0; ch < 2; ch++)
        {
            for (int b = 0; b < 4; b++)
            {
                int bx = b & 1;
                int by = b >> 1;
                int ti = 4 + (ch * 2) + bx;
                int li = 4 + (ch * 2) + by;
                int last = VP8Tokens.EmitBlock(ref sink, lv.Slice(16 + 256 + (ch * 64) + (b * 16), 16), 2, 0, left[li] + top[ti]);
                byte nz = last >= 0 ? (byte)1 : (byte)0;
                left[li] = nz;
                top[ti] = nz;
            }
        }
    }
}
