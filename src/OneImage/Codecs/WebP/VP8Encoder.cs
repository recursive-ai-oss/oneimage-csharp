using System.Buffers.Binary;

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

    private readonly byte[] _work = new byte[Bps * 27];
    private readonly byte[] _scr = new byte[Bps * 27];
    private readonly byte[] _bestY16 = new byte[Bps * 27];
    private readonly byte[] _srcMb = new byte[16 * 16 + 2 * 64];

    private sealed class Segment
    {
        public int Quant;
        public VP8QuantMatrix Y1 = null!;
        public VP8QuantMatrix Y2 = null!;
        public VP8QuantMatrix Uv = null!;
        public double LambdaI16;
        public double LambdaI4;
        public double LambdaUv;
        public int Alpha;
        public int Beta;
        public int FilterStrength;
        public int MaxEdge;
    }

    private VP8Encoder(byte[] rgba, int w, int h, int quality, int effort, CancellationToken token)
    {
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
    public static byte[] Encode(byte[] rgba, int width, int height, int quality, int effort, CancellationToken token)
    {
        var e = new VP8Encoder(rgba, width, height, quality, effort, token);
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
        int[] alphas = new int[256];
        int[] mbAlpha = new int[n];
        long uvAlphaSum = 0;
        Span<short> coef = stackalloc short[16];
        int[] distribution = new int[32];
        byte[] w = _work;
        for (int mby = 0; mby < _mbh; mby++)
        {
            _token.ThrowIfCancellationRequested();
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                // Neighbors for the analysis come from the source image itself.
                VP8Borders.Prepare(w, _srcY, _srcU, _srcV, _ys, _uvs, mbx, mby, _mbw);
                LoadSource(mbx, mby);
                int bestY = -1;
                for (int mode = 0; mode < 2; mode++)
                {
                    Predict.Block(w, YOff, 16, mode == 0 ? VP8Decoder.DcPred : VP8Decoder.TmPred, mbx > 0, mby > 0);
                    Array.Clear(distribution);
                    for (int b = 0; b < 16; b++)
                    {
                        VP8Fwd.Fdct(_srcMb, ((b >> 2) * 4 * 16) + ((b & 3) * 4), 16, w, YOff + ((b >> 2) * 4 * Bps) + ((b & 3) * 4), coef);
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
                        VP8Fwd.Fdct(_srcMb, so, 8, w, UOff + ((b >> 1) * 4 * Bps) + ((b & 1) * 4), coef);
                        AddToDistribution(distribution, coef);
                        VP8Fwd.Fdct(_srcMb, so + 64, 8, w, VOff + ((b >> 1) * 4 * Bps) + ((b & 1) * 4), coef);
                        AddToDistribution(distribution, coef);
                    }

                    bestUv = Math.Max(bestUv, GetAlpha(distribution));
                }

                int alpha = ((3 * bestY) + bestUv + 2) >> 2;
                alpha = Clip(255 - alpha, 0, 255);
                alphas[alpha]++;
                mbAlpha[(mby * _mbw) + mbx] = alpha;
                uvAlphaSum += bestUv;
            }
        }

        _uvAlpha = (int)((uvAlphaSum + (n / 2)) / n);
        AssignSegments(alphas, mbAlpha);
    }

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
            s.LambdaI16 = LambdaScale * y2Ac * y2Ac;
            s.LambdaI4 = LambdaScale * y1Ac * y1Ac;
            s.LambdaUv = LambdaScale * uvAc * uvAc;

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

    // Weight of one bit in squared-error units, relative to the squared quantizer step.
    internal static double LambdaScale = 0.12;

    private void LoadSource(int mbx, int mby)
    {
        for (int j = 0; j < 16; j++)
        {
            _srcY.AsSpan((((mby * 16) + j) * _ys) + (mbx * 16), 16).CopyTo(_srcMb.AsSpan(j * 16, 16));
        }

        for (int j = 0; j < 8; j++)
        {
            int o = (((mby * 8) + j) * _uvs) + (mbx * 8);
            _srcU.AsSpan(o, 8).CopyTo(_srcMb.AsSpan(256 + (j * 8), 8));
            _srcV.AsSpan(o, 8).CopyTo(_srcMb.AsSpan(256 + 64 + (j * 8), 8));
        }
    }

    // ---- macroblock coding ----

    private static readonly int[] I16ModeBits = [BitsOf(156, 0) + BitsOf(163, 0), BitsOf(156, 1) + BitsOf(128, 1), BitsOf(156, 0) + BitsOf(163, 1), BitsOf(156, 1) + BitsOf(128, 0)];

    private static int BitsOf(int prob, int bit) => VP8Fwd.BitCost(bit, prob);

    private void EncodeMacroblocks()
    {
        byte[] probs = DefaultProbs();
        Array.Clear(_intraTop);
        Array.Clear(_nzTop);
        for (int mby = 0; mby < _mbh; mby++)
        {
            _token.ThrowIfCancellationRequested();
            Array.Clear(_intraLeft);
            Array.Clear(_nzLeft);
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                EncodeMacroblock(mbx, mby, probs);
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
    }

    private long _i16Distortion;
    private readonly Cand _i16 = new();
    private readonly Cand _i4 = new();
    private readonly Cand _uv = new();
    private readonly Cand _tmp = new();

    private void EncodeMacroblock(int mbx, int mby, byte[] probs)
    {
        int mb = (mby * _mbw) + mbx;
        Segment sp = _seg[_mbSegment[mb]];
        VP8Borders.Prepare(_work, _recY, _recU, _recV, _ys, _uvs, mbx, mby, _mbw);
        LoadSource(mbx, mby);

        // Chroma and 16x16 luma are evaluated first; 4x4 only if it can beat the 16x16 result.
        int uvMode = ChooseUv(mbx, mby, sp, probs, _uv);
        int i16Mode = ChooseI16(mbx, mby, sp, probs, _i16);
        bool useI4 = false;
        if (_effort >= 1)
        {
            // The 4x4 search overwrites the work buffer's luma area, which is fine: the 16x16 reconstruction is saved.
            useI4 = TryI4(mbx, mby, sp, probs, _i4, _i16.Score);
        }

        Cand y = useI4 ? _i4 : _i16;
        if (!useI4)
        {
            // A blocky macroblock (only luma DC levels, with visible distortion) records how strong the loop filter must be to
            // smooth the block edges out; the segment's filter strength is raised to that afterwards.
            ReadOnlySpan<short> lv16 = _i16.Levels;
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

            if (onlyDc && anyDc && _i16Distortion > 20 * sp.Y1.Q[0])
            {
                int maxV = Math.Max(Math.Abs((int)lv16[1]), Math.Max(Math.Abs((int)lv16[2]), Math.Abs((int)lv16[4])));
                sp.MaxEdge = Math.Max(sp.MaxEdge, maxV);
            }
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
        _uv.Levels.AsSpan(16 + 256, 128).CopyTo(dst[(16 + 256)..]);
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
        UpdateContexts(mbx, mb, useI4);

        // Store the reconstruction.
        for (int j = 0; j < 16; j++)
        {
            y.Recon.AsSpan(YOff + (j * Bps), 16).CopyTo(_recY.AsSpan((((mby * 16) + j) * _ys) + (mbx * 16), 16));
        }

        for (int j = 0; j < 8; j++)
        {
            int o = (((mby * 8) + j) * _uvs) + (mbx * 8);
            _uv.Recon.AsSpan(UOff + (j * Bps), 8).CopyTo(_recU.AsSpan(o, 8));
            _uv.Recon.AsSpan(VOff + (j * Bps), 8).CopyTo(_recV.AsSpan(o, 8));
        }
    }

    // nz layout per macroblock column: Y 0..3, U 4..5, V 6..7, DC 8.
    private void UpdateContexts(int mbx, int mb, bool i4)
    {
        Span<short> lv = _levels.AsSpan(mb * LevelsPerMb, LevelsPerMb);
        Span<byte> top = _nzTop.AsSpan(mbx * 9, 9);
        Span<byte> left = _nzLeft;
        if (!i4)
        {
            int nz = LastNonZero(lv[..16], 0) >= 0 ? 1 : 0;
            top[8] = left[8] = (byte)nz;
        }

        int first = i4 ? 0 : 1;
        for (int b = 0; b < 16; b++)
        {
            int nz = LastNonZero(lv.Slice(16 + (b * 16), 16), first) >= first ? 1 : 0;
            top[b & 3] = (byte)nz;
            left[b >> 2] = (byte)nz;
        }

        for (int ch = 0; ch < 2; ch++)
        {
            for (int b = 0; b < 4; b++)
            {
                int nz = LastNonZero(lv.Slice(16 + 256 + (ch * 64) + (b * 16), 16), 0) >= 0 ? 1 : 0;
                top[4 + (ch * 2) + (b & 1)] = (byte)nz;
                left[4 + (ch * 2) + (b >> 1)] = (byte)nz;
            }
        }

        for (int x = 0; x < 4; x++)
        {
            _intraTop[(mbx * 4) + x] = i4 ? _bModes[(mb * 16) + 12 + x] : MapMode(_yMode[mb]);
        }

        for (int y = 0; y < 4; y++)
        {
            _intraLeft[y] = i4 ? _bModes[(mb * 16) + (y * 4) + 3] : MapMode(_yMode[mb]);
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

    private static long Sse(ReadOnlySpan<byte> a, int ao, int astride, ReadOnlySpan<byte> b, int bo, int bstride, int size)
    {
        long sum = 0;
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int d = a[ao + (j * astride) + i] - b[bo + (j * bstride) + i];
                sum += d * d;
            }
        }

        return sum;
    }

    // Adds a dequantized residual to the prediction already stored in buf.
    private static void AddResidual(byte[] buf, int off, ReadOnlySpan<short> coeffs, bool anyAc, bool anyCoeff)
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
                for (int i = 0; i < 4; i++)
                {
                    int v = buf[off + (j * Bps) + i] + dc;
                    buf[off + (j * Bps) + i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                }
            }

            return;
        }

        Idct.Add(coeffs, buf, off);
    }

    private static long Score(long distortion, int rate256, double lambda) => (distortion * 256) + (long)(rate256 * lambda);

    private int ChooseI16(int mbx, int mby, Segment sp, byte[] probs, Cand best)
    {
        byte[] w = _work;
        int bestMode = 0;
        best.Score = long.MaxValue;
        Span<short> coef = stackalloc short[16];
        Span<short> dc = stackalloc short[16];
        Span<short> y2 = stackalloc short[16];
        short[] deq = new short[256];
        Span<short> lv = _tmp.Levels;
        Span<byte> tnz = stackalloc byte[4];
        Span<byte> lnz = stackalloc byte[4];
        for (int mode = 0; mode < 4; mode++)
        {
            Predict.Block(w, YOff, 16, mode, mbx > 0, mby > 0);
            for (int b = 0; b < 16; b++)
            {
                VP8Fwd.Fdct(_srcMb, ((b >> 2) * 4 * 16) + ((b & 3) * 4), 16, w, YOff + ((b >> 2) * 4 * Bps) + ((b & 3) * 4), coef);
                coef.CopyTo(deq.AsSpan(b * 16, 16));
                dc[b] = coef[0];
            }

            VP8Fwd.Wht(dc, y2);
            lv.Clear();
            sp.Y2.Quantize(y2, lv[..16], 0);
            VP8Decoder.TransformWht(y2, deq);
            // Reconstruct.
            Array.Copy(w, _scr, w.Length);
            int rate = 0;
            var cost = new CostSink(probs);
            VP8Tokens.EmitBlock(ref cost, lv[..16], 1, 0, _nzTop[(mbx * 9) + 8] + _nzLeft[8]);
            _nzTop.AsSpan((mbx * 9), 4).CopyTo(tnz);
            _nzLeft.AsSpan(0, 4).CopyTo(lnz);
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
                VP8Tokens.EmitBlock(ref cost, lv.Slice(16 + (b * 16), 16), 0, 1, lnz[by] + tnz[bx]);
                byte nz = last >= 1 ? (byte)1 : (byte)0;
                lnz[by] = nz;
                tnz[bx] = nz;
                bool anyAc = last >= 1;
                AddResidual(_scr, YOff + (by * 4 * Bps) + (bx * 4), blockCoef, anyAc, anyAc || blockCoef[0] != 0);
            }

            rate = cost.Cost + I16ModeBits[mode] + BitsOf(145, 1);
            long d = Sse(_srcMb, 0, 16, _scr, YOff, Bps, 16);
            long score = Score(d, rate, sp.LambdaI16);
            if (score < best.Score)
            {
                best.Score = score;
                bestMode = mode;
                _i16Distortion = d;
                lv.CopyTo(best.Levels);
                Array.Copy(_scr, best.Recon, _scr.Length);
            }
        }

        return bestMode;
    }

    private bool TryI4(int mbx, int mby, Segment sp, byte[] probs, Cand cand, long i16Score)
    {
        byte[] w = _work;
        VP8Borders.Prepare(w, _recY, _recU, _recV, _ys, _uvs, mbx, mby, _mbw);
        // The top-right pixels of the macroblock serve the right-most sub-block of every row.
        for (int r = 3; r < 16; r += 4)
        {
            for (int k = 0; k < 4; k++)
            {
                w[YOff + (r * Bps) + 16 + k] = w[YOff - Bps + 16 + k];
            }
        }

        ReadOnlySpan<byte> kf = VP8Tables.KeyFrameBModeProbs;
        Span<byte> modes = cand.BModes;
        Span<short> coef = stackalloc short[16];
        Span<short> lv = stackalloc short[16];
        Span<byte> tnz = stackalloc byte[4];
        Span<byte> lnz = stackalloc byte[4];
        _nzTop.AsSpan(mbx * 9, 4).CopyTo(tnz);
        _nzLeft.AsSpan(0, 4).CopyTo(lnz);
        Span<int> modeScore = stackalloc int[10];
        Span<int> order = stackalloc int[10];
        Span<byte> bestRecon = stackalloc byte[4 * Bps];
        Span<short> bestLv = stackalloc short[16];
        cand.Levels.AsSpan().Clear();
        Array.Copy(w, cand.Recon, w.Length);
        long total = 0;
        int headerBits = BitsOf(145, 0);
        int keep = _effort >= 6 ? 10 : _effort >= 5 ? 5 : 3;
        for (int b = 0; b < 16; b++)
        {
            int bx = b & 3;
            int by = b >> 2;
            int off = YOff + (by * 4 * Bps) + (bx * 4);
            int srcOff = (by * 4 * 16) + (bx * 4);
            int topMode = by == 0 ? _intraTop[(mbx * 4) + bx] : modes[((by - 1) * 4) + bx];
            int leftMode = bx == 0 ? _intraLeft[by] : modes[(by * 4) + bx - 1];
            int pb = ((topMode * 10) + leftMode) * 9;

            // Rank the modes by the squared error of the plain prediction, then evaluate the best few in full.
            for (int m = 0; m < 10; m++)
            {
                Predict.SubBlock(w, off, m);
                modeScore[m] = (int)Sse(_srcMb, srcOff, 16, w, off, Bps, 4);
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
            int bestMode = 0;
            for (int oi = 0; oi < keep; oi++)
            {
                int m = order[oi];
                Predict.SubBlock(w, off, m);
                VP8Fwd.Fdct(_srcMb, srcOff, 16, w, off, coef);
                int last = sp.Y1.Quantize(coef, lv, 0);
                // Reconstruct into scratch.
                for (int j = 0; j < 4; j++)
                {
                    w.AsSpan(off + (j * Bps), 4).CopyTo(_scr.AsSpan(off + (j * Bps), 4));
                }

                AddResidual(_scr, off, coef, HasAc(lv), last >= 0);
                long d = Sse(_srcMb, srcOff, 16, _scr, off, Bps, 4);
                var cost = new CostSink(probs);
                VP8Tokens.EmitBlock(ref cost, lv, 3, 0, lnz[by] + tnz[bx]);
                int rate = cost.Cost;
                int mbits = ModeBits(kf.Slice(pb, 9), m);
                long score = Score(d, rate + mbits, sp.LambdaI4);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestMode = m;
                    lv.CopyTo(bestLv);
                    for (int j = 0; j < 4; j++)
                    {
                        _scr.AsSpan(off + (j * Bps), 4).CopyTo(bestRecon.Slice(j * Bps, 4));
                    }
                }

            }

            total += bestScore;
            if (total + (headerBits * sp.LambdaI4) > i16Score)
            {
                return false;
            }

            modes[b] = (byte)bestMode;
            for (int j = 0; j < 4; j++)
            {
                bestRecon.Slice(j * Bps, 4).CopyTo(w.AsSpan(off + (j * Bps), 4));
                bestRecon.Slice(j * Bps, 4).CopyTo(cand.Recon.AsSpan(off + (j * Bps), 4));
            }

            bestLv.CopyTo(cand.Levels.AsSpan(16 + (b * 16), 16));
            byte nz = LastNonZero(bestLv, 0) >= 0 ? (byte)1 : (byte)0;
            lnz[by] = nz;
            tnz[bx] = nz;
        }

        cand.Score = total + (long)(headerBits * sp.LambdaI4);
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

    private int ChooseUv(int mbx, int mby, Segment sp, byte[] probs, Cand best)
    {
        byte[] w = _work;
        best.Score = long.MaxValue;
        int bestMode = 0;
        Span<short> coef = stackalloc short[16];
        Span<short> lv = stackalloc short[16];
        Span<byte> tnz = stackalloc byte[4];
        Span<byte> lnz = stackalloc byte[4];
        short[] tmpLv = _tmp.Levels;
        for (int mode = 0; mode < 4; mode++)
        {
            Predict.Block(w, UOff, 8, mode, mbx > 0, mby > 0);
            Predict.Block(w, VOff, 8, mode, mbx > 0, mby > 0);
            Array.Copy(w, _scr, w.Length);
            var cost = new CostSink(probs);
            Array.Clear(tmpLv);
            long d = 0;
            for (int ch = 0; ch < 2; ch++)
            {
                int baseOff = ch == 0 ? UOff : VOff;
                int srcBase = 256 + (ch * 64);
                _nzTop.AsSpan((mbx * 9) + 4 + (ch * 2), 2).CopyTo(tnz);
                _nzLeft.AsSpan(4 + (ch * 2), 2).CopyTo(lnz);
                for (int b = 0; b < 4; b++)
                {
                    int bx = b & 1;
                    int by = b >> 1;
                    int off = baseOff + (by * 4 * Bps) + (bx * 4);
                    VP8Fwd.Fdct(_srcMb, srcBase + (by * 4 * 8) + (bx * 4), 8, w, off, coef);
                    int last = sp.Uv.Quantize(coef, lv, 0);
                    AddResidual(_scr, off, coef, HasAc(lv), last >= 0);
                    VP8Tokens.EmitBlock(ref cost, lv, 2, 0, lnz[by] + tnz[bx]);
                    byte nz = last >= 0 ? (byte)1 : (byte)0;
                    lnz[by] = nz;
                    tnz[bx] = nz;
                    lv.CopyTo(tmpLv.AsSpan(16 + 256 + (ch * 64) + (b * 16), 16));
                }

                d += Sse(_srcMb, srcBase, 8, _scr, baseOff, Bps, 8);
            }

            int uvBits = mode switch
            {
                VP8Decoder.DcPred => BitsOf(142, 0),
                VP8Decoder.VPred => BitsOf(142, 1) + BitsOf(114, 0),
                VP8Decoder.TmPred => BitsOf(142, 1) + BitsOf(114, 1) + BitsOf(183, 1),
                _ => BitsOf(142, 1) + BitsOf(114, 1) + BitsOf(183, 0),
            };
            long score = Score(d, cost.Cost + uvBits, sp.LambdaUv);
            if (score < best.Score)
            {
                best.Score = score;
                bestMode = mode;
                tmpLv.CopyTo(best.Levels, 0);
                Array.Copy(_scr, best.Recon, _scr.Length);
            }
        }

        return bestMode;
    }

    // ---- bitstream ----

    private byte[] WriteStream()
    {
        int n = _mbw * _mbh;
        // Token probabilities from the statistics of the coded levels.
        var stats = new StatsSink { Ones = new int[1056], Total = new int[1056] };
        ScanTokens(ref stats, useSkip: false);
        int skipCount = 0;
        for (int i = 0; i < n; i++)
        {
            skipCount += _skip[i];
        }

        int skipProba = n > 0 ? Clip(255 * (n - skipCount) / n, 1, 255) : 255;
        bool useSkip = skipCount > 0 && skipProba < 250;
        if (useSkip)
        {
            stats = new StatsSink { Ones = new int[1056], Total = new int[1056] };
            ScanTokens(ref stats, useSkip: true);
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

                ReadOnlySpan<short> lv = _levels.AsSpan(mb * LevelsPerMb, LevelsPerMb);
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
    }
}
