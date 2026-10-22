using System.Numerics;

namespace OneImage.Codecs.WebP;

/// <summary>Settings of the lossless encoder. The defaults give a good size/speed balance; tests use the switches to cover every transform.</summary>
internal sealed class VP8LEncodeOptions
{
    /// <summary>Gets or sets the effort from 0 (fastest) to 6 (smallest).</summary>
    public int Effort { get; set; } = 4;

    /// <summary>Gets or sets whether to use the palette transform when there are at most 256 colors (null: automatic).</summary>
    public bool? Palette { get; set; }

    public bool? SubtractGreen { get; set; }

    public bool? Predictor { get; set; }

    public bool? CrossColor { get; set; }

    /// <summary>Gets or sets the transform tile size as a power of two exponent (2..9); 0 picks one.</summary>
    public int TransformBits { get; set; }

    /// <summary>Gets or sets the color cache size exponent (1..11), 0 for none, -1 for automatic.</summary>
    public int CacheBits { get; set; } = -1;

    /// <summary>Gets or sets whether backward references are searched.</summary>
    public bool Lz77 { get; set; } = true;

    /// <summary>Gets or sets the entropy image tile exponent (2..9) forcing several prefix code groups; 0 lets the encoder decide.</summary>
    public int HistogramBits { get; set; }

    /// <summary>Gets or sets whether histogram clustering into several prefix code groups is allowed.</summary>
    public bool Cluster { get; set; } = true;

    /// <summary>Gets or sets the most threads to use. The output does not depend on it.</summary>
    public int Degree { get; set; } = 1;
}

/// <summary>Encoder for the WebP lossless bitstream (RFC 9649 section 3).</summary>
internal static class VP8LEncoder
{
    private static readonly float[] Log2Table = BuildLogTable();

    private static float[] BuildLogTable()
    {
        float[] t = new float[4097];
        for (int i = 2; i < t.Length; i++)
        {
            t[i] = (float)(i * Math.Log2(i));
        }

        return t;
    }

    // c * log2(c)
    internal static float CLog2(uint c) => c < Log2Table.Length ? Log2Table[c] : (float)(c * Math.Log2(c));

    /// <summary>Encodes a full VP8L chunk payload (signature, size, transforms and pixels).</summary>
    public static byte[] Encode(ReadOnlySpan<uint> argb, int width, int height, bool hasAlpha, VP8LEncodeOptions options, CancellationToken token) =>
        EncodeArray(argb.ToArray(), width, height, hasAlpha, options, token);

    /// <summary>Encodes pixels the caller hands over: the encoder only reads the array.</summary>
    public static byte[] EncodeArray(uint[] pixels, int width, int height, bool hasAlpha, VP8LEncodeOptions options, CancellationToken token)
    {
        var enc = new Session(width, height, options, token);
        return enc.EncodeBest(pixels, hasAlpha, header: true);
    }

    /// <summary>Encodes an 8-bit plane (lossy alpha) as a headerless stream: the plane is the green channel.</summary>
    public static byte[] EncodePlane(ReadOnlySpan<byte> plane, int width, int height, VP8LEncodeOptions options, CancellationToken token)
    {
        uint[] pixels = new uint[plane.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = 0xff000000u | ((uint)plane[i] << 8);
        }

        var enc = new Session(width, height, options, token);
        return enc.EncodeBest(pixels, hasAlpha: false, header: false);
    }

    private struct Tok
    {
        public uint Value;
        public int Dist;
        public byte Kind; // 0 literal (Value = argb), 1 color cache index, 2 copy (Value = length, Dist = pixel distance)
    }

    private sealed class Session
    {
        private readonly int _w;
        private readonly int _h;
        private readonly VP8LEncodeOptions _o;
        private readonly CancellationToken _token;
        private VP8LBitWriter _bw = new();

        public Session(int w, int h, VP8LEncodeOptions o, CancellationToken token)
        {
            _w = w;
            _h = h;
            _o = o;
            _token = token;
        }

        public byte[] EncodeBest(uint[] pixels, bool hasAlpha, bool header)
        {
            uint[]? palette = _o.Palette == false ? null : TryPalette(pixels);
            byte[]? best = null;
            void Consider(byte[] b)
            {
                if (best == null || b.Length < best.Length)
                {
                    best = b;
                }
            }

            if (palette != null)
            {
                Consider(EncodePlan(pixels, hasAlpha, header, palette, false, false, false));
                if (_o.Effort >= 5 && palette.Length > 16 && _o.Predictor != false)
                {
                    Consider(EncodePlan(pixels, hasAlpha, header, palette, false, true, false));
                }
            }
            else
            {
                bool pred = _o.Predictor ?? _o.Effort > 0;
                bool cc = _o.CrossColor ?? _o.Effort > 0;
                if (_o.SubtractGreen != null)
                {
                    Consider(EncodePlan(pixels, hasAlpha, header, null, _o.SubtractGreen.Value, pred, cc));
                }
                else if (_o.Effort >= 5)
                {
                    Consider(EncodePlan(pixels, hasAlpha, header, null, true, pred, cc));
                    Consider(EncodePlan(pixels, hasAlpha, header, null, false, pred, cc));
                }
                else
                {
                    // Subtracting green helps most photos but hurts some synthetic or already decorrelated images:
                    // decide on a centered sample.
                    Consider(EncodePlan(pixels, hasAlpha, header, null, _o.Effort < 3 || PreferSubtractGreen(pixels, pred, cc), pred, cc));
                }
            }

            return best!;
        }

        private bool PreferSubtractGreen(uint[] pixels, bool pred, bool cc)
        {
            int sw = Math.Min(_w, 256);
            int sh = Math.Min(_h, 256);
            if (sw == _w && sh == _h && (long)_w * _h < 4096)
            {
                return true;
            }

            int x0 = (_w - sw) / 2;
            int y0 = (_h - sh) / 2;
            uint[] sample = new uint[sw * sh];
            for (int y = 0; y < sh; y++)
            {
                Array.Copy(pixels, ((y0 + y) * _w) + x0, sample, y * sw, sw);
            }

            int withGreen = 0;
            int without = 0;
            if (_o.Degree > 1)
            {
                Parallel.Invoke(
                    new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = _token },
                    () => withGreen = new Session(sw, sh, _o, _token).EncodePlan(sample, false, false, null, true, pred, cc).Length,
                    () => without = new Session(sw, sh, _o, _token).EncodePlan(sample, false, false, null, false, pred, cc).Length);
            }
            else
            {
                withGreen = new Session(sw, sh, _o, _token).EncodePlan(sample, false, false, null, true, pred, cc).Length;
                without = new Session(sw, sh, _o, _token).EncodePlan(sample, false, false, null, false, pred, cc).Length;
            }

            return withGreen <= without;
        }

        // Returns the sorted palette when the image has at most 256 colors.
        private static uint[]? TryPalette(uint[] pixels)
        {
            var set = new HashSet<uint>();
            uint last = 0;
            bool any = false;
            foreach (uint p in pixels)
            {
                if (any && p == last)
                {
                    continue;
                }

                last = p;
                any = true;
                if (set.Add(p) && set.Count > 256)
                {
                    return null;
                }
            }

            uint[] pal = [.. set];
            Array.Sort(pal);
            return pal;
        }

        private byte[] EncodePlan(uint[] source, bool hasAlpha, bool header, uint[]? palette, bool subtractGreen, bool predictor, bool crossColor)
        {
            _bw = new VP8LBitWriter((_w * _h / 4) + 1024);
            if (header)
            {
                _bw.Put(0x2f, 8);
                _bw.Put((uint)(_w - 1), 14);
                _bw.Put((uint)(_h - 1), 14);
                _bw.Put(hasAlpha ? 1u : 0u, 1);
                _bw.Put(0, 3);
            }

            uint[] cur = source;
            int cw = _w;
            if (palette != null)
            {
                cur = ApplyPalette(source, palette, out cw);
            }
            else if (subtractGreen)
            {
                cur = new uint[source.Length];
                for (int i = 0; i < cur.Length; i++)
                {
                    uint p = source[i];
                    uint g = (p >> 8) & 0xff;
                    cur[i] = (p & 0xff00ff00) | (((p & 0xff0000) - (g << 16)) & 0xff0000) | (((p & 0xff) - g) & 0xff);
                }

                _bw.Put(1, 1);
                _bw.Put(2, 2);
            }
            if (predictor)
            {
                cur = ApplyPredictor(cur, cw);
            }
            if (crossColor && palette == null)
            {
                cur = ApplyCrossColor(cur, cw);
            }
            _bw.Put(0, 1); // no more transforms
            WriteImage(cur, cw, _h, isMain: true);
            return _bw.Finish();
        }

        // Runs body for every row index, on several threads when the options allow it. Each thread gets its own scratch from make.
        private void ForRows<TScratch>(int rows, Func<TScratch> make, Action<int, TScratch> body)
        {
            int degree = Math.Min(_o.Degree, rows);
            if (degree <= 1)
            {
                TScratch s = make();
                for (int y = 0; y < rows; y++)
                {
                    body(y, s);
                }

                return;
            }

            Parallel.For(0, rows, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = _token }, make, (y, _, s) =>
            {
                body(y, s);
                return s;
            }, _ => { });
        }

        private int TransformBitsFor(int w, int h)
        {
            if (_o.TransformBits is >= 2 and <= 9)
            {
                return _o.TransformBits;
            }

            long px = (long)w * h;
            return px < 1 << 16 ? 3 : px < 1 << 20 ? 4 : 5;
        }

        private uint[] ApplyPalette(uint[] source, uint[] palette, out int width)
        {
            var map = new Dictionary<uint, int>(palette.Length);
            for (int i = 0; i < palette.Length; i++)
            {
                map[palette[i]] = i;
            }

            int n = palette.Length;
            int bits = n > 16 ? 0 : n > 4 ? 1 : n > 2 ? 2 : 3;
            width = (_w + (1 << bits) - 1) >> bits;
            uint[] result = new uint[width * _h];
            int bitsPerPixel = 8 >> bits;
            for (int y = 0; y < _h; y++)
            {
                for (int x = 0; x < _w; x++)
                {
                    uint idx = (uint)map[source[(y * _w) + x]];
                    ref uint dst = ref result[(y * width) + (x >> bits)];
                    dst |= idx << (8 + ((x & ((1 << bits) - 1)) * bitsPerPixel));
                }
            }

            for (int i = 0; i < result.Length; i++)
            {
                result[i] |= 0xff000000;
            }

            _bw.Put(1, 1);
            _bw.Put(3, 2);
            _bw.Put((uint)(n - 1), 8);
            uint[] delta = new uint[n];
            delta[0] = palette[0];
            for (int i = 1; i < n; i++)
            {
                delta[i] = SubPixels(palette[i], palette[i - 1]);
            }

            WriteImage(delta, n, 1, isMain: false);
            return result;
        }

        private static uint SubPixels(uint a, uint b)
        {
            uint ag = ((a | 0x00ff00ff) - (b & 0xff00ff00)) & 0xff00ff00;
            uint rb = ((a | 0xff00ff00) - (b & 0x00ff00ff)) & 0x00ff00ff;
            return ag | rb;
        }

        // ---- predictor transform ----

        private uint[] ApplyPredictor(uint[] src, int w)
        {
            int bits = TransformBitsFor(w, _h);
            int tw = (w + (1 << bits) - 1) >> bits;
            int th = (_h + (1 << bits) - 1) >> bits;
            uint[] modesImg = new uint[tw * th];
            uint[] res = new uint[src.Length];
            void Row(int ty, int[] hist)
            {
                _token.ThrowIfCancellationRequested();
                for (int tx = 0; tx < tw; tx++)
                {
                    int x0 = tx << bits;
                    int y0 = ty << bits;
                    int x1 = Math.Min(w, x0 + (1 << bits));
                    int y1 = Math.Min(_h, y0 + (1 << bits));
                    int bestMode = 1;
                    if (_o.Effort > 0)
                    {
                        float bestScore = float.MinValue;
                        for (int mode = 0; mode < 14; mode++)
                        {
                            // The histogram is all zero here: it starts that way and the scan below zeroes what it reads.
                            Kernels.WebPPredictKernels.ScoreTile(src, w, _h, x0, y0, x1, y1, mode, hist, _o.Effort >= 5 ? 1 : 2);
                            float score = 0;
                            for (int i = 0; i < hist.Length; i++)
                            {
                                int c = hist[i];
                                if (c != 0)
                                {
                                    hist[i] = 0;
                                    if (c > 1)
                                    {
                                        score += CLog2((uint)c);
                                    }
                                }
                            }

                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestMode = mode;
                            }
                        }
                    }

                    modesImg[(ty * tw) + tx] = 0xff000000u | ((uint)bestMode << 8);
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            res[(y * w) + x] = Residual(src, w, x, y, bestMode);
                        }
                    }
                }
            }

            // Tiles read only the source and write only their own residuals and mode, so rows can run in any order.
            ForRows(th, () => new int[4 * 256], Row);

            _bw.Put(1, 1);
            _bw.Put(0, 2);
            _bw.Put((uint)(bits - 2), 3);
            WriteImage(modesImg, tw, th, isMain: false);
            return res;
        }

        private static uint Residual(uint[] a, int w, int x, int y, int mode)
        {
            int i = (y * w) + x;
            uint pred;
            if (y == 0)
            {
                pred = x == 0 ? 0xff000000 : a[i - 1];
            }
            else if (x == 0)
            {
                pred = a[i - w];
            }
            else
            {
                uint l = a[i - 1];
                uint t = a[i - w];
                uint tl = a[i - w - 1];
                uint tr = a[i - w + 1];
                switch (mode)
                {
                    case 0:
                        pred = 0xff000000;
                        break;
                    case 1:
                        pred = l;
                        break;
                    case 2:
                        pred = t;
                        break;
                    case 3:
                        pred = tr;
                        break;
                    case 4:
                        pred = tl;
                        break;
                    case 5:
                        pred = VP8LDecoder.Average2(VP8LDecoder.Average2(l, tr), t);
                        break;
                    case 6:
                        pred = VP8LDecoder.Average2(l, tl);
                        break;
                    case 7:
                        pred = VP8LDecoder.Average2(l, t);
                        break;
                    case 8:
                        pred = VP8LDecoder.Average2(tl, t);
                        break;
                    case 9:
                        pred = VP8LDecoder.Average2(t, tr);
                        break;
                    case 10:
                        pred = VP8LDecoder.Average2(VP8LDecoder.Average2(l, tl), VP8LDecoder.Average2(t, tr));
                        break;
                    case 11:
                        pred = VP8LDecoder.Select(l, t, tl);
                        break;
                    case 12:
                        pred = VP8LDecoder.ClampAddSubtractFull(l, t, tl);
                        break;
                    default:
                        pred = VP8LDecoder.ClampAddSubtractHalf(VP8LDecoder.Average2(l, t), tl);
                        break;
                }
            }

            return SubPixels(a[i], pred);
        }

        // ---- cross-color transform ----

        private uint[] ApplyCrossColor(uint[] src, int w)
        {
            int bits = TransformBitsFor(w, _h);
            int tw = (w + (1 << bits) - 1) >> bits;
            int th = (_h + (1 << bits) - 1) >> bits;
            uint[] elems = new uint[tw * th];
            uint[] res = new uint[src.Length];
            void Row(int ty, int[] hist)
            {
                _token.ThrowIfCancellationRequested();
                for (int tx = 0; tx < tw; tx++)
                {
                    int x0 = tx << bits;
                    int y0 = ty << bits;
                    int x1 = Math.Min(w, x0 + (1 << bits));
                    int y1 = Math.Min(_h, y0 + (1 << bits));
                    int g2r = 0;
                    int g2b = 0;
                    int r2b = 0;
                    if (_o.Effort > 0)
                    {
                        g2r = Search(src, w, x0, y0, x1, y1, hist, 0, 0, 0);
                        g2b = Search(src, w, x0, y0, x1, y1, hist, 0, 0, 1);
                        r2b = Search(src, w, x0, y0, x1, y1, hist, g2b, 0, 2);
                    }

                    elems[(ty * tw) + tx] = 0xff000000u | ((uint)(r2b & 0xff) << 16) | ((uint)(g2b & 0xff) << 8) | (uint)(g2r & 0xff);
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            uint p = src[(y * w) + x];
                            int green = (sbyte)((p >> 8) & 0xff);
                            int red = (int)((p >> 16) & 0xff);
                            int blue = (int)(p & 0xff);
                            int nr = (red - ((g2r * green) >> 5)) & 0xff;
                            int nb = (blue - ((g2b * green) >> 5) - ((r2b * (sbyte)red) >> 5)) & 0xff;
                            res[(y * w) + x] = (p & 0xff00ff00) | ((uint)nr << 16) | (uint)nb;
                        }
                    }
                }
            }

            ForRows(th, () => new int[256], Row);

            _bw.Put(1, 1);
            _bw.Put(1, 2);
            _bw.Put((uint)(bits - 2), 3);
            WriteImage(elems, tw, th, isMain: false);
            return res;
        }

        // Coarse-to-fine search for one multiplier (units of 1/32): kind 0 = green-to-red, 1 = green-to-blue, 2 = red-to-blue.
        private static int Search(uint[] src, int w, int x0, int y0, int x1, int y1, int[] hist, int g2b, int unused, int kind)
        {
            float Score(int c) => kind switch
            {
                0 => ScoreChannel(src, w, x0, y0, x1, y1, hist, c, 0, true),
                1 => ScoreChannel(src, w, x0, y0, x1, y1, hist, c, 0, false),
                _ => ScoreChannel(src, w, x0, y0, x1, y1, hist, g2b, c, false),
            };

            int best = 0;
            float bestScore = Score(0);
            foreach (int c in (ReadOnlySpan<int>)[8, -8, 16, -16, 32, -32])
            {
                float s = Score(c);
                if (s > bestScore)
                {
                    bestScore = s;
                    best = c;
                }
            }

            int center = best;
            foreach (int d in (ReadOnlySpan<int>)[-4, -2, 2, 4])
            {
                int c = center + d;
                float s = Score(c);
                if (s > bestScore)
                {
                    bestScore = s;
                    best = c;
                }
            }

            return best;
        }

        // Score (higher is better) of the red residual (red=true) or blue residual for a candidate transform over a tile.
        private static float ScoreChannel(uint[] src, int w, int x0, int y0, int x1, int y1, int[] hist, int g2x, int r2b, bool red)
        {
            Array.Clear(hist);
            for (int y = y0; y < y1; y += 2)
            {
                for (int x = x0; x < x1; x++)
                {
                    uint p = src[(y * w) + x];
                    int green = (sbyte)((p >> 8) & 0xff);
                    if (red)
                    {
                        hist[(int)(((p >> 16) - (uint)((g2x * green) >> 5)) & 0xff)]++;
                    }
                    else
                    {
                        int rr = (sbyte)((p >> 16) & 0xff);
                        hist[(int)((p - (uint)((g2x * green) >> 5) - (uint)((r2b * rr) >> 5)) & 0xff)]++;
                    }
                }
            }

            float s = 0;
            foreach (int c in hist)
            {
                if (c > 1)
                {
                    s += CLog2((uint)c);
                }
            }

            return s;
        }

        // ---- entropy coded image ----

        private void WriteImage(uint[] px, int w, int h, bool isMain)
        {
            Tok[] stream = Lz77(px, w, h, out int count);
            try
            {
                WriteTokenImage(px, w, h, isMain, stream, count);
            }
            finally
            {
                System.Buffers.ArrayPool<Tok>.Shared.Return(stream);
            }
        }

        private void WriteTokenImage(uint[] px, int w, int h, bool isMain, Tok[] stream, int count)
        {
            int cacheBits = 0;
            if (isMain && _o.CacheBits != 0)
            {
                if (_o.CacheBits > 0)
                {
                    cacheBits = _o.CacheBits;
                }
                else if (_o.Effort > 0)
                {
                    float bestCost = EstimateCost(stream, count, w);
                    ReadOnlySpan<int> candidates = [4, 7, 10];
                    float[] costs = new float[candidates.Length];
                    if (_o.Degree > 1)
                    {
                        Parallel.For(0, costs.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(_o.Degree, costs.Length), CancellationToken = _token }, i => costs[i] = EstimateCacheCost(stream, count, px, i == 0 ? 4 : i == 1 ? 7 : 10, w));
                    }
                    else
                    {
                        for (int i = 0; i < costs.Length; i++)
                        {
                            costs[i] = EstimateCacheCost(stream, count, px, candidates[i], w);
                        }
                    }

                    for (int i = 0; i < costs.Length; i++)
                    {
                        if (costs[i] < bestCost)
                        {
                            bestCost = costs[i];
                            cacheBits = candidates[i];
                        }
                    }
                }

                if (cacheBits > 0)
                {
                    ApplyCache(stream, count, px, cacheBits);
                }
            }
            if (cacheBits > 0)
            {
                _bw.Put(1, 1);
                _bw.Put((uint)cacheBits, 4);
            }
            else
            {
                _bw.Put(0, 1);
            }

            int greenSize = 256 + 24 + (cacheBits > 0 ? 1 << cacheBits : 0);
            int groups = 1;
            int[]? tileGroup = null;
            int histoBits = 0;
            int tilesW = 0;
            int tilesH = 0;
            if (isMain)
            {
                if (_o.Cluster && _o.Effort > 0 && (long)w * h >= 1024)
                {
                    tileGroup = Cluster(stream, count, w, h, greenSize, out groups, out histoBits, out tilesW, out tilesH);
                }

                if (groups > 1)
                {
                    _bw.Put(1, 1);
                    _bw.Put((uint)(histoBits - 2), 3);
                    uint[] img = new uint[tilesW * tilesH];
                    for (int i = 0; i < img.Length; i++)
                    {
                        img[i] = 0xff000000u | ((uint)tileGroup![i] << 8);
                    }

                    WriteImage(img, tilesW, tilesH, isMain: false);
                }
                else
                {
                    _bw.Put(0, 1);
                }
            }

            // Histograms per group.
            var hists = new Histogram[groups];
            for (int g = 0; g < groups; g++)
            {
                hists[g] = new Histogram(greenSize);
            }

            if (groups == 1)
            {
                AddTokens(hists[0], stream, 0, count, w);
            }
            else
            {
                ForEachTokenTile(stream, count, w, histoBits, tilesW, (ti, tokIndex) => AddToken(hists[tileGroup![ti]], stream[tokIndex], w));
            }

            var codes = new HuffmanCode[groups * 5];
            for (int g = 0; g < groups; g++)
            {
                codes[(g * 5) + 0] = HuffmanCode.Build(hists[g].Green, 15);
                codes[(g * 5) + 1] = HuffmanCode.Build(hists[g].Red, 15);
                codes[(g * 5) + 2] = HuffmanCode.Build(hists[g].Blue, 15);
                codes[(g * 5) + 3] = HuffmanCode.Build(hists[g].Alpha, 15);
                codes[(g * 5) + 4] = HuffmanCode.Build(hists[g].Dist, 15);
                for (int k = 0; k < 5; k++)
                {
                    codes[(g * 5) + k].Store(_bw);
                }
            }
            if (groups == 1)
            {
                WriteTokens(stream, 0, count, codes, 0, w);
            }
            else
            {
                ForEachTokenTile(stream, count, w, histoBits, tilesW, (ti, tokIndex) => WriteTokens(stream, tokIndex, tokIndex + 1, codes, tileGroup![ti] * 5, w));
            }
        }

        // Visits every token with the index of the tile its first pixel lies in.
        private static void ForEachTokenTile(Tok[] stream, int count, int w, int histoBits, int tilesW, Action<int, int> visit)
        {
            int x = 0;
            int y = 0;
            for (int i = 0; i < count; i++)
            {
                visit(((y >> histoBits) * tilesW) + (x >> histoBits), i);
                int len = stream[i].Kind == 2 ? (int)stream[i].Value : 1;
                x += len;
                while (x >= w)
                {
                    x -= w;
                    y++;
                }
            }
        }

        private static void AddToken(Histogram h, Tok t, int w)
        {
            switch (t.Kind)
            {
                case 0:
                    h.Green[(t.Value >> 8) & 0xff]++;
                    h.Red[(t.Value >> 16) & 0xff]++;
                    h.Blue[t.Value & 0xff]++;
                    h.Alpha[t.Value >> 24]++;
                    break;
                case 1:
                    h.Green[280 + t.Value]++;
                    break;
                default:
                    PrefixEncode((int)t.Value, out int lc, out _, out _);
                    h.Green[256 + lc]++;
                    PrefixEncode(DistanceToCode(w, t.Dist), out int dc, out _, out _);
                    h.Dist[dc]++;
                    break;
            }
        }

        private static void AddTokens(Histogram h, Tok[] s, int from, int to, int w)
        {
            for (int i = from; i < to; i++)
            {
                AddToken(h, s[i], w);
            }
        }

        private void WriteTokens(Tok[] s, int from, int to, HuffmanCode[] codes, int cbase, int w)
        {
            HuffmanCode g = codes[cbase];
            HuffmanCode r = codes[cbase + 1];
            HuffmanCode b = codes[cbase + 2];
            HuffmanCode a = codes[cbase + 3];
            HuffmanCode d = codes[cbase + 4];
            for (int i = from; i < to; i++)
            {
                Tok t = s[i];
                switch (t.Kind)
                {
                    case 0:
                        g.WriteSymbol(_bw, (int)((t.Value >> 8) & 0xff));
                        r.WriteSymbol(_bw, (int)((t.Value >> 16) & 0xff));
                        b.WriteSymbol(_bw, (int)(t.Value & 0xff));
                        a.WriteSymbol(_bw, (int)(t.Value >> 24));
                        break;
                    case 1:
                        g.WriteSymbol(_bw, 280 + (int)t.Value);
                        break;
                    default:
                        {
                            PrefixEncode((int)t.Value, out int lc, out int lb, out int lv);
                            g.WriteSymbol(_bw, 256 + lc);
                            if (lb > 0)
                            {
                                _bw.Put((uint)lv, lb);
                            }

                            PrefixEncode(DistanceToCode(w, t.Dist), out int dc, out int db, out int dv);
                            d.WriteSymbol(_bw, dc);
                            if (db > 0)
                            {
                                _bw.Put((uint)dv, db);
                            }

                            break;
                        }
                }
            }
        }

        // ---- LZ77 ----

        // Images are parsed in independent bands of this many pixels: a match never crosses a band end, so the tokens do not
        // depend on how many threads parse the bands. The cost is at most one shortened match per band.
        private const int BandPixels = 1 << 18;

        // Positions without a match after which the match search gets shallow.
        private const int DrySpan = 4096;

        private Tok[] Lz77(uint[] px, int w, int h, out int count)
        {
            int n = px.Length;
            Tok[] toks = System.Buffers.ArrayPool<Tok>.Shared.Rent(n);
            if (!_o.Lz77 || n < 4)
            {
                int k = 0;
                foreach (uint p in px)
                {
                    toks[k++] = new Tok { Kind = 0, Value = p };
                }

                count = k;
                return toks;
            }

            const int HashBits = 18;
            int[] head = new int[1 << HashBits];
            Array.Fill(head, -1);
            int[] chain = System.Buffers.ArrayPool<int>.Shared.Rent(n);
            try
            {
                for (int i = 0; i < n - 1; i++)
                {
                    uint hv = ((px[i] * 0x1e35a7bdu) ^ (px[i + 1] * 0x9e3779b1u)) >> (32 - HashBits);
                    chain[i] = head[hv];
                    head[hv] = i;
                }

                chain[n - 1] = -1;
                return ParseBands(px, chain, w, toks, out count);
            }
            finally
            {
                System.Buffers.ArrayPool<int>.Shared.Return(chain);
            }
        }

        private Tok[] ParseBands(uint[] px, int[] chain, int w, Tok[] toks, out int count)
        {
            int n = px.Length;
            int bands = (n + BandPixels - 1) / BandPixels;
            int[] bandCount = new int[bands];
            void Band(int band)
            {
                int start = band * BandPixels;
                bandCount[band] = ParseBand(px, chain, w, start, Math.Min(n, start + BandPixels), toks);
            }

            int degree = Math.Min(_o.Degree, bands);
            if (degree <= 1)
            {
                for (int band = 0; band < bands; band++)
                {
                    Band(band);
                }
            }
            else
            {
                Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = _token }, Band);
            }

            // Each band wrote its tokens at the start of its own pixel range; close the gaps.
            int nt = bandCount[0];
            for (int band = 1; band < bands; band++)
            {
                Array.Copy(toks, band * BandPixels, toks, nt, bandCount[band]);
                nt += bandCount[band];
            }

            count = nt;
            return toks;
        }

        // Greedy (lazy at higher effort) parse of the pixels start..end-1, writing tokens from toks[start] on; returns their number.
        private int ParseBand(uint[] px, int[] chain, int w, int start, int end, Tok[] toks)
        {
            const int MaxLen = 4096;
            int window = (1 << 20) - 120;
            int maxChain = _o.Effort switch { 0 => 4, 1 => 8, 2 => 16, 3 => 24, 4 => 16, 5 => 96, _ => 256 };
            bool lazy = _o.Effort >= 2;
            int nt = start;

            (int len, int dist) Find(int i)
            {
                int maxLen = Math.Min(MaxLen, end - i);
                int bestLen = 0;
                int bestDist = 0;
                if (maxLen < 3)
                {
                    return (0, 0);
                }

                ReadOnlySpan<uint> cur = px.AsSpan(i, maxLen);

                // Short distances have the cheapest codes; try them first.
                foreach (int d in (ReadOnlySpan<int>)[1, w])
                {
                    if (d <= i)
                    {
                        int len = cur.CommonPrefixLength(px.AsSpan(i - d, maxLen));
                        if (len > bestLen)
                        {
                            bestLen = len;
                            bestDist = d;
                        }
                    }
                }

                if (bestLen >= maxLen)
                {
                    return (bestLen, bestDist);
                }

                int j = chain[i];
                int steps = 0;
                while (j >= 0 && i - j <= window && steps++ < maxChain)
                {
                    if (px[j + bestLen] == px[i + bestLen] && px[j] == px[i])
                    {
                        int len = cur.CommonPrefixLength(px.AsSpan(j, maxLen));
                        if (len > bestLen)
                        {
                            bestLen = len;
                            bestDist = i - j;
                            if (len >= maxLen)
                            {
                                break;
                            }
                        }
                    }

                    j = chain[j];
                }

                return (bestLen, bestDist);
            }

            int fullChain = maxChain;
            int dry = 0;
            int pos = start;
            (int len, int dist) m = (0, 0);
            bool have = false;
            while (pos < end)
            {
                if ((pos & 0xffff) == 0)
                {
                    _token.ThrowIfCancellationRequested();
                }

                if (!have)
                {
                    m = Find(pos);
                }

                have = false;
                int min = m.dist <= 4096 ? 3 : 4;

                // After a long run without any match (photographs, noise) the deep chain walks only cost time: look at a few
                // candidates until matches show up again.
                maxChain = m.len >= min ? fullChain : dry > DrySpan ? Math.Min(fullChain, 2) : fullChain;
                dry = m.len >= min ? 0 : dry + 1;
                if (m.len >= min)
                {
                    if (lazy && pos + 1 < end)
                    {
                        (int len, int dist) m2 = Find(pos + 1);
                        if (m2.len > m.len + 1)
                        {
                            toks[nt++] = new Tok { Kind = 0, Value = px[pos] };
                            pos++;
                            m = m2;
                            have = true;
                            continue;
                        }
                    }

                    toks[nt++] = new Tok { Kind = 2, Value = (uint)m.len, Dist = m.dist };
                    pos += m.len;
                }
                else
                {
                    toks[nt++] = new Tok { Kind = 0, Value = px[pos] };
                    pos++;
                }
            }

            return nt - start;
        }

        // Replaces literals found in a color cache by cache indices, in place.
        private static void ApplyCache(Tok[] toks, int count, uint[] px, int bits)
        {
            uint[] cache = new uint[1 << bits];
            int shift = 32 - bits;
            int pos = 0;
            for (int i = 0; i < count; i++)
            {
                ref Tok t = ref toks[i];
                if (t.Kind == 0)
                {
                    uint key = (t.Value * 0x1e35a7bdu) >> shift;
                    if (cache[key] == t.Value)
                    {
                        t = new Tok { Kind = 1, Value = key };
                    }
                    else
                    {
                        cache[key] = t.Value;
                    }

                    pos++;
                }
                else
                {
                    // Copied pixels enter the cache too, exactly as the decoder will see them.
                    int len = (int)t.Value;
                    for (int k = 0; k < len; k++)
                    {
                        uint v = px[pos + k];
                        cache[(v * 0x1e35a7bdu) >> shift] = v;
                    }

                    pos += len;
                }
            }
        }

        // Entropy estimate of the token stream as it would look with a color cache of the given size, without building it.
        private static float EstimateCacheCost(Tok[] toks, int count, uint[] px, int bits, int w)
        {
            var h = new Histogram(256 + 24 + (1 << bits));
            uint[] cache = new uint[1 << bits];
            int shift = 32 - bits;
            int pos = 0;
            for (int i = 0; i < count; i++)
            {
                Tok t = toks[i];
                if (t.Kind == 0)
                {
                    uint key = (t.Value * 0x1e35a7bdu) >> shift;
                    if (cache[key] == t.Value)
                    {
                        h.Green[280 + key]++;
                    }
                    else
                    {
                        cache[key] = t.Value;
                        AddToken(h, t, w);
                    }

                    pos++;
                }
                else
                {
                    int len = (int)t.Value;
                    for (int k = 0; k < len; k++)
                    {
                        uint v = px[pos + k];
                        cache[(v * 0x1e35a7bdu) >> shift] = v;
                    }

                    pos += len;
                    AddToken(h, t, w);
                }
            }

            return h.EntropyBits();
        }

        private static float EstimateCost(Tok[] stream, int count, int w)
        {
            var h = new Histogram(256 + 24);
            AddTokens(h, stream, 0, count, w);
            return h.EntropyBits();
        }

        // ---- histogram clustering ----

        private static int[]? Cluster(Tok[] stream, int count, int w, int h, int greenSize, out int groups, out int histoBits, out int tilesW, out int tilesH)
        {
            groups = 1;
            histoBits = 0;
            tilesW = tilesH = 0;
            return null;
        }

        private sealed class Histogram
        {
            public Histogram(int greenSize)
            {
                Green = new uint[greenSize];
                Red = new uint[256];
                Blue = new uint[256];
                Alpha = new uint[256];
                Dist = new uint[40];
            }

            public uint[] Green { get; }

            public uint[] Red { get; }

            public uint[] Blue { get; }

            public uint[] Alpha { get; }

            public uint[] Dist { get; }

            public float EntropyBits()
            {
                return Entropy(Green) + Entropy(Red) + Entropy(Blue) + Entropy(Alpha) + Entropy(Dist);
            }

            private static float Entropy(uint[] c)
            {
                double total = 0;
                double sum = 0;
                foreach (uint v in c)
                {
                    if (v > 0)
                    {
                        total += v;
                        sum += CLog2(v);
                    }
                }

                return total > 0 ? (float)((total * Math.Log2(total)) - sum) : 0;
            }
        }
    }

    // ---- prefix coding of lengths and distances ----

    /// <summary>Splits a value of at least 1 into its prefix code, extra bit count and extra bits (RFC 9649 section 3.6.2.2).</summary>
    internal static void PrefixEncode(int value, out int code, out int extraBits, out int extraValue)
    {
        if (value <= 4)
        {
            code = value - 1;
            extraBits = 0;
            extraValue = 0;
            return;
        }

        int d = value - 1;
        int highest = BitOperations.Log2((uint)d);
        int second = (d >> (highest - 1)) & 1;
        extraBits = highest - 1;
        extraValue = d & ((1 << extraBits) - 1);
        code = (2 * highest) + second;
    }

    private static readonly sbyte[] DistanceMap =
    [
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2, 2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3,
        3, 1, -3, 1, 2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0, 1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4,
        4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0, 1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2,
        4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0, 1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2,
        4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3, 0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1,
        4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2, 3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5,
        8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6, -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7,
        -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
    ];

    // code = 1 + index of (xi, yi) for a short distance; indexed [yi * 17 + (xi + 8)].
    private static readonly byte[] PlaneCodeLut = BuildPlaneLut();

    private static byte[] BuildPlaneLut()
    {
        byte[] lut = new byte[8 * 17];
        for (int i = 0; i < 120; i++)
        {
            int xi = DistanceMap[i * 2];
            int yi = DistanceMap[(i * 2) + 1];
            lut[(yi * 17) + xi + 8] = (byte)(i + 1);
        }

        return lut;
    }

    /// <summary>Maps a pixel distance to the code that is stored (short codes for the nearby neighborhood).</summary>
    internal static int DistanceToCode(int xsize, int dist)
    {
        int yoff = dist / xsize;
        int xoff = dist - (yoff * xsize);
        if (xoff <= 8 && yoff < 8)
        {
            int c = PlaneCodeLut[(yoff * 17) + xoff + 8];
            if (c != 0)
            {
                return c;
            }
        }

        if (xoff > xsize - 8 && yoff < 7)
        {
            int c = PlaneCodeLut[((yoff + 1) * 17) + (xoff - xsize) + 8];
            if (c != 0)
            {
                return c;
            }
        }

        return dist + 120;
    }
}
