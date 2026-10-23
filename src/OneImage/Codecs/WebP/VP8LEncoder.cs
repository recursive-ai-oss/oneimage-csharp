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

    /// <summary>Encodes pixels the caller hands over. The encoder transforms them in place to avoid whole-frame copies, so the array's contents are undefined afterwards.</summary>
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

    // A copy of Len pixels from Dist pixels back, starting at pixel Pos.
    private struct Match
    {
        public int Pos;
        public int Len;
        public int Dist;
    }

    private sealed class MatchList
    {
        public Match[] Items = [];
        public int Count;

        public void Add(Match m)
        {
            if (Count == Items.Length)
            {
                Array.Resize(ref Items, Math.Max(256, Items.Length * 2));
            }

            Items[Count++] = m;
        }

        public void AddRange(MatchList other)
        {
            if (Count + other.Count > Items.Length)
            {
                Array.Resize(ref Items, Count + other.Count);
            }

            Array.Copy(other.Items, 0, Items, Count, other.Count);
            Count += other.Count;
        }
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

            // The plans transform the pixels in place; every plan but the last one works on a copy.
            if (palette != null)
            {
                bool second = _o.Effort >= 5 && palette.Length > 16 && _o.Predictor != false;
                Consider(EncodePlan(pixels, hasAlpha, header, palette, false, false, false, consume: !second));
                if (second)
                {
                    Consider(EncodePlan(pixels, hasAlpha, header, palette, false, true, false, consume: true));
                }
            }
            else
            {
                bool pred = _o.Predictor ?? _o.Effort > 0;
                bool cc = _o.CrossColor ?? _o.Effort > 0;
                if (_o.SubtractGreen != null)
                {
                    Consider(EncodePlan(pixels, hasAlpha, header, null, _o.SubtractGreen.Value, pred, cc, consume: true));
                }
                else if (_o.Effort >= 5)
                {
                    Consider(EncodePlan(pixels, hasAlpha, header, null, true, pred, cc, consume: false));
                    Consider(EncodePlan(pixels, hasAlpha, header, null, false, pred, cc, consume: true));
                }
                else
                {
                    // Subtracting green helps most photos but hurts some synthetic or already decorrelated images:
                    // decide on a centered sample.
                    bool green = _o.Effort < 3 || PreferSubtractGreen(pixels, pred, cc);
                    Consider(EncodePlan(pixels, hasAlpha, header, null, green, pred, cc, consume: true));
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
                    () => withGreen = new Session(sw, sh, _o, _token).EncodePlan(sample, false, false, null, true, pred, cc, consume: false).Length,
                    () => without = new Session(sw, sh, _o, _token).EncodePlan(sample, false, false, null, false, pred, cc, consume: false).Length);
            }
            else
            {
                withGreen = new Session(sw, sh, _o, _token).EncodePlan(sample, false, false, null, true, pred, cc, consume: false).Length;
                without = new Session(sw, sh, _o, _token).EncodePlan(sample, false, false, null, false, pred, cc, consume: true).Length;
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

        // With consume the source array may be overwritten; without it the pixels are copied before any in-place step.
        private byte[] EncodePlan(uint[] source, bool hasAlpha, bool header, uint[]? palette, bool subtractGreen, bool predictor, bool crossColor, bool consume)
        {
            _bw = new VP8LBitWriter();
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
            else
            {
                if (!consume && (subtractGreen || predictor || crossColor))
                {
                    cur = (uint[])source.Clone();
                }

                if (subtractGreen)
                {
                    for (int i = 0; i < cur.Length; i++)
                    {
                        uint p = cur[i];
                        uint g = (p >> 8) & 0xff;
                        cur[i] = (p & 0xff00ff00) | (((p & 0xff0000) - (g << 16)) & 0xff0000) | (((p & 0xff) - g) & 0xff);
                    }

                    _bw.Put(1, 1);
                    _bw.Put(2, 2);
                }
            }

            // The palette's index image is a fresh array; the others work on the (owned) pixels in place.
            if (predictor)
            {
                ApplyPredictor(cur, cw);
            }
            if (crossColor && palette == null)
            {
                ApplyCrossColor(cur, cw);
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

        // Replaces the pixels by their prediction residuals, in place. A tile row reads its neighbors' original values from a
        // private copy of its rows plus the row above, taken before anything of it is overwritten.
        private void ApplyPredictor(uint[] px, int w)
        {
            int bits = TransformBitsFor(w, _h);
            int tw = (w + (1 << bits) - 1) >> bits;
            int th = (_h + (1 << bits) - 1) >> bits;
            uint[] modesImg = new uint[tw * th];
            uint[] above = new uint[(th - 1) * w];
            for (int ty = 1; ty < th; ty++)
            {
                Array.Copy(px, ((ty << bits) - 1) * w, above, (ty - 1) * w, w);
            }

            int scratchRows = Math.Min(_h, (1 << bits) + 1);
            void Row(int ty, PredictorScratch sc)
            {
                _token.ThrowIfCancellationRequested();
                int y0 = ty << bits;
                int y1 = Math.Min(_h, y0 + (1 << bits));
                int off = ty > 0 ? 1 : 0;
                uint[] a = sc.Rows ??= new uint[scratchRows * w];
                int[] hist = sc.Hist;
                if (off != 0)
                {
                    Array.Copy(above, (ty - 1) * w, a, 0, w);
                }

                Array.Copy(px, y0 * w, a, off * w, (y1 - y0) * w);
                int rows = y1 - y0 + off;
                for (int tx = 0; tx < tw; tx++)
                {
                    int x0 = tx << bits;
                    int x1 = Math.Min(w, x0 + (1 << bits));
                    int bestMode = 1;
                    if (_o.Effort > 0)
                    {
                        float bestScore = float.MinValue;
                        for (int mode = 0; mode < 14; mode++)
                        {
                            // The histogram is all zero here: it starts that way and the scan below zeroes what it reads.
                            Kernels.WebPPredictKernels.ScoreTile(a, w, rows, x0, off, x1, rows, mode, hist, _o.Effort >= 5 ? 1 : 2);
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
                            px[(y * w) + x] = Residual(a, w, x, y - y0 + off, y == 0, bestMode);
                        }
                    }
                }
            }

            // Tile rows read only their private copies and write only their own pixels, so rows can run in any order.
            ForRows(th, () => new PredictorScratch(), Row);

            _bw.Put(1, 1);
            _bw.Put(0, 2);
            _bw.Put((uint)(bits - 2), 3);
            WriteImage(modesImg, tw, th, isMain: false);
        }

        private sealed class PredictorScratch
        {
            public int[] Hist { get; } = new int[4 * 256];

            public uint[]? Rows { get; set; }
        }

        // a holds the rows around the pixel; y is its row in a and firstRow whether that row is the top row of the image.
        private static uint Residual(uint[] a, int w, int x, int y, bool firstRow, int mode)
        {
            int i = (y * w) + x;
            uint pred;
            if (firstRow)
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

        // In place: a tile reads only its own pixels, all of them before it overwrites any.
        private void ApplyCrossColor(uint[] src, int w)
        {
            int bits = TransformBitsFor(w, _h);
            int tw = (w + (1 << bits) - 1) >> bits;
            int th = (_h + (1 << bits) - 1) >> bits;
            uint[] elems = new uint[tw * th];
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
                            src[(y * w) + x] = (p & 0xff00ff00) | ((uint)nr << 16) | (uint)nb;
                        }
                    }
                }
            }

            ForRows(th, () => new int[256], Row);

            _bw.Put(1, 1);
            _bw.Put(1, 2);
            _bw.Put((uint)(bits - 2), 3);
            WriteImage(elems, tw, th, isMain: false);
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
            MatchList matches = Lz77(px, w);
            WriteTokenImage(px, w, isMain, matches);
        }

        // The token stream is never stored: it is the pixels with the matches cut out of them (see Walk). That keeps the
        // coder's state proportional to the number of matches rather than to the number of pixels.
        private void WriteTokenImage(uint[] px, int w, bool isMain, MatchList matches)
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
                    float bestCost = EstimateCacheCost(matches, px, 0, w);
                    ReadOnlySpan<int> candidates = [4, 7, 10];
                    float[] costs = new float[candidates.Length];
                    if (_o.Degree > 1)
                    {
                        Parallel.For(0, costs.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(_o.Degree, costs.Length), CancellationToken = _token }, i => costs[i] = EstimateCacheCost(matches, px, i == 0 ? 4 : i == 1 ? 7 : 10, w));
                    }
                    else
                    {
                        for (int i = 0; i < costs.Length; i++)
                        {
                            costs[i] = EstimateCacheCost(matches, px, candidates[i], w);
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

            if (isMain)
            {
                _bw.Put(0, 1); // a single prefix code group: no entropy image
            }

            var hist = new Histogram(256 + 24 + (cacheBits > 0 ? 1 << cacheBits : 0));
            var count = new CountSink(hist, w);
            Walk(px, matches, cacheBits, ref count);

            var codes = new HuffmanCode[5];
            codes[0] = HuffmanCode.Build(hist.Green, 15);
            codes[1] = HuffmanCode.Build(hist.Red, 15);
            codes[2] = HuffmanCode.Build(hist.Blue, 15);
            codes[3] = HuffmanCode.Build(hist.Alpha, 15);
            codes[4] = HuffmanCode.Build(hist.Dist, 15);
            for (int k = 0; k < 5; k++)
            {
                codes[k].Store(_bw);
            }

            if (isMain)
            {
                // The size of the token stream follows from the counts and the code lengths.
                long bits = count.ExtraBits;
                bits += codes[0].Cost(hist.Green) + codes[1].Cost(hist.Red) + codes[2].Cost(hist.Blue) + codes[3].Cost(hist.Alpha) + codes[4].Cost(hist.Dist);
                _bw.Reserve(bits);
            }

            var write = new WriteSink(_bw, codes, w);
            Walk(px, matches, cacheBits, ref write);
        }

        private interface ITokenSink
        {
            void Literal(uint argb);

            void CacheHit(uint key);

            void Copy(int length, int distance);
        }

        // Visits the tokens of an image: literals for the pixels outside the matches, color cache hits among them when
        // cacheBits is set (a pixel is a hit when its slot already holds it; copied pixels enter the cache too, exactly as
        // the decoder will see them), and the matches.
        private static void Walk<TSink>(uint[] px, MatchList matches, int cacheBits, ref TSink sink)
            where TSink : struct, ITokenSink
        {
            Match[] items = matches.Items;
            int count = matches.Count;
            uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
            int shift = 32 - cacheBits;
            int pos = 0;
            for (int k = 0; k <= count; k++)
            {
                int end = k < count ? items[k].Pos : px.Length;
                if (cache == null)
                {
                    for (; pos < end; pos++)
                    {
                        sink.Literal(px[pos]);
                    }
                }
                else
                {
                    for (; pos < end; pos++)
                    {
                        uint v = px[pos];
                        uint key = (v * 0x1e35a7bdu) >> shift;
                        if (cache[key] == v)
                        {
                            sink.CacheHit(key);
                        }
                        else
                        {
                            cache[key] = v;
                            sink.Literal(v);
                        }
                    }
                }

                if (k == count)
                {
                    break;
                }

                Match m = items[k];
                sink.Copy(m.Len, m.Dist);
                if (cache != null)
                {
                    for (int j = 0; j < m.Len; j++)
                    {
                        uint v = px[pos + j];
                        cache[(v * 0x1e35a7bdu) >> shift] = v;
                    }
                }

                pos += m.Len;
            }
        }

        private struct CountSink(Histogram h, int w) : ITokenSink
        {
            // Extra bits of the copies' length and distance codes.
            public long ExtraBits;

            public void Literal(uint argb)
            {
                h.Green[(argb >> 8) & 0xff]++;
                h.Red[(argb >> 16) & 0xff]++;
                h.Blue[argb & 0xff]++;
                h.Alpha[argb >> 24]++;
            }

            public void CacheHit(uint key) => h.Green[280 + key]++;

            public void Copy(int length, int distance)
            {
                PrefixEncode(length, out int lc, out int lb, out _);
                h.Green[256 + lc]++;
                PrefixEncode(DistanceToCode(w, distance), out int dc, out int db, out _);
                h.Dist[dc]++;
                ExtraBits += lb + db;
            }
        }

        private struct WriteSink(VP8LBitWriter bw, HuffmanCode[] codes, int w) : ITokenSink
        {
            public void Literal(uint argb)
            {
                codes[0].WriteSymbol(bw, (int)((argb >> 8) & 0xff));
                codes[1].WriteSymbol(bw, (int)((argb >> 16) & 0xff));
                codes[2].WriteSymbol(bw, (int)(argb & 0xff));
                codes[3].WriteSymbol(bw, (int)(argb >> 24));
            }

            public void CacheHit(uint key) => codes[0].WriteSymbol(bw, 280 + (int)key);

            public void Copy(int length, int distance)
            {
                PrefixEncode(length, out int lc, out int lb, out int lv);
                codes[0].WriteSymbol(bw, 256 + lc);
                if (lb > 0)
                {
                    bw.Put((uint)lv, lb);
                }

                PrefixEncode(DistanceToCode(w, distance), out int dc, out int db, out int dv);
                codes[4].WriteSymbol(bw, dc);
                if (db > 0)
                {
                    bw.Put((uint)dv, db);
                }
            }
        }

        // Entropy estimate of the token stream as it would look with a color cache of the given size (0: none).
        private static float EstimateCacheCost(MatchList matches, uint[] px, int bits, int w)
        {
            var h = new Histogram(256 + 24 + (bits > 0 ? 1 << bits : 0));
            var sink = new CountSink(h, w);
            Walk(px, matches, bits, ref sink);
            return h.EntropyBits();
        }

        // ---- LZ77 ----

        // Images are parsed in independent bands of this many pixels: a match never crosses a band end, so the matches do not
        // depend on how many threads parse the bands. The cost is at most one shortened match per band.
        private const int BandPixels = 1 << 18;

        // Positions without a match after which the match search gets shallow.
        private const int DrySpan = 4096;

        // How far back a match may start.
        private const int Window = (1 << 20) - 120;

        // Images up to this many pixels hash into a map sized to them instead of the 2^18-entry table.
        private const int SmallHashPixels = 1 << 14;

        // The hash chains (for each pixel, the previous pixel with the same hash) are built a few bands at a time and only
        // the last Window entries are kept, so their memory does not grow with the image. Entries are the same as if one
        // chain covered the whole image, so the matches do not change.
        private MatchList Lz77(uint[] px, int w)
        {
            int n = px.Length;
            if (!_o.Lz77 || n < 4)
            {
                return new MatchList();
            }

            const int HashBits = 18;
            // The hash table maps each 18-bit hash to the latest pixel with it. A full table is 1 MiB, which the small images that
            // make up most of a file's work (predictor and color-transform maps, palettes) must not pay on every call, so they use
            // an exact stand-in with the same answers: an open-addressing map sized to the pixel count.
            bool small = n <= SmallHashPixels;
            int[] head = [];
            int[] keys = [];
            int[] vals = [];
            int mask = 0;
            if (small)
            {
                int cap = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, 2 * n));
                keys = new int[cap];
                vals = new int[cap];
                mask = cap - 1;
                Array.Fill(keys, -1);
            }
            else
            {
                head = new int[1 << HashBits];
                Array.Fill(head, -1);
            }

            int bands = (n + BandPixels - 1) / BandPixels;
            int degree = Math.Min(_o.Degree, bands);
            int waveBands = Math.Max(8, 2 * degree);
            int waveLen = (int)Math.Min(n, (long)waveBands * BandPixels);
            int[] chain = System.Buffers.ArrayPool<int>.Shared.Rent((int)Math.Min(n, (long)Window + waveLen));
            try
            {
                var perBand = new MatchList[bands];
                int chainBase = 0; // chain[i - chainBase] belongs to pixel i
                for (int ws = 0; ws < n; ws += waveLen)
                {
                    int we = Math.Min(n, ws + waveLen);
                    int newBase = Math.Max(0, ws - Window);
                    if (newBase != chainBase)
                    {
                        Array.Copy(chain, newBase - chainBase, chain, 0, ws - newBase);
                        chainBase = newBase;
                    }

                    for (int i = ws; i < Math.Min(we, n - 1); i++)
                    {
                        uint hv = ((px[i] * 0x1e35a7bdu) ^ (px[i + 1] * 0x9e3779b1u)) >> (32 - HashBits);
                        if (small)
                        {
                            int slot = (int)hv & mask;
                            while (keys[slot] != -1 && keys[slot] != (int)hv)
                            {
                                slot = (slot + 1) & mask;
                            }

                            chain[i - chainBase] = keys[slot] == -1 ? -1 : vals[slot];
                            keys[slot] = (int)hv;
                            vals[slot] = i;
                        }
                        else
                        {
                            chain[i - chainBase] = head[hv];
                            head[hv] = i;
                        }
                    }

                    if (we == n)
                    {
                        chain[n - 1 - chainBase] = -1;
                    }

                    int b0 = ws / BandPixels;
                    int b1 = (we + BandPixels - 1) / BandPixels;
                    void Band(int band)
                    {
                        int start = band * BandPixels;
                        var list = new MatchList();
                        ParseBand(px, chain, chainBase, w, start, Math.Min(n, start + BandPixels), list);
                        perBand[band] = list;
                    }

                    if (degree <= 1)
                    {
                        for (int band = b0; band < b1; band++)
                        {
                            Band(band);
                        }
                    }
                    else
                    {
                        Parallel.For(b0, b1, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = _token }, Band);
                    }
                }

                if (bands == 1)
                {
                    return perBand[0];
                }

                var all = new MatchList();
                foreach (MatchList l in perBand)
                {
                    all.AddRange(l);
                }

                return all;
            }
            finally
            {
                System.Buffers.ArrayPool<int>.Shared.Return(chain);
            }
        }

        // Greedy (lazy at higher effort) parse of the pixels start..end-1; the pixels between matches stay literals.
        private void ParseBand(uint[] px, int[] chain, int chainBase, int w, int start, int end, MatchList matches)
        {
            const int MaxLen = 4096;
            const int window = Window;
            int maxChain = _o.Effort switch { 0 => 4, 1 => 8, 2 => 16, 3 => 24, 4 => 16, 5 => 96, _ => 256 };
            bool lazy = _o.Effort >= 2;

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

                int j = chain[i - chainBase];
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

                    j = chain[j - chainBase];
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
                            pos++;
                            m = m2;
                            have = true;
                            continue;
                        }
                    }

                    matches.Add(new Match { Pos = pos, Len = m.len, Dist = m.dist });
                    pos += m.len;
                }
                else
                {
                    pos++;
                }
            }
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
