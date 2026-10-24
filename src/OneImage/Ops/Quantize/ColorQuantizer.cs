using System.Buffers;
using System.Buffers.Binary;

namespace OneImage.Ops.Quantize;

/// <summary>
/// Builds a palette of at most 256 colors from RGBA8 (straight alpha) rows with Wu's variance-minimizing box splitting
/// (Graphics Gems II, "Efficient Statistical Computations for Optimal Color Quantization").
/// </summary>
/// <remarks>
/// Wu is used because it works from a fixed-size histogram, so cost depends on the pixel count only once (one pass, no sorting
/// or per-pixel tree), and it is deterministic by construction. Differences from the textbook version: palette entries are
/// the exact mean of the pixels in each box rather than the 5-bit cell centre, the cut search also covers alpha in
/// <see cref="QuantizeAlpha.Full"/> mode, and two weighted k-means passes over the populated histogram cells polish the result.
/// If the input has no more distinct colors than requested the palette is exactly those colors in order of first appearance,
/// so images that already fit are never altered.
/// Not thread-safe.
/// </remarks>
internal sealed class ColorQuantizer
{
    private const int D = 33; // 32 bins plus the zero plane of the cumulative moments

    private readonly int _maxColors;
    private readonly QuantizeAlpha _alpha;
    private readonly int _alphaThreshold;
    private int _aDim; // 2 (one alpha plane) or 9; full mode starts with one plane and grows when a pixel is not opaque
    private long[] _w, _r, _g, _b, _a, _sq; // the common-size set comes from, and goes back to, s_spare
    private readonly bool _reserveTransparent;
    private readonly ColorSet _set = new();
    private bool _exact = true;
    private long _transparentCount;
    private uint _lastKey;
    private bool _haveLast;

    /// <summary>Creates a quantizer.</summary>
    /// <param name="maxColors">Palette size limit, 2 to 256 (a transparent entry counts).</param>
    /// <param name="alpha">Alpha handling.</param>
    /// <param name="alphaThreshold">With <see cref="QuantizeAlpha.Binary"/>, pixels with alpha below this are transparent.</param>
    /// <param name="reserveTransparent">With <see cref="QuantizeAlpha.Binary"/>, the palette always ends with a fully transparent entry (counted in <paramref name="maxColors"/>) even when no input pixel is transparent, so it can be reused for later frames that have transparent pixels.</param>
    public ColorQuantizer(int maxColors, QuantizeAlpha alpha = QuantizeAlpha.Binary, int alphaThreshold = 128, bool reserveTransparent = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColors, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxColors, 256);
        _maxColors = maxColors;
        _alpha = alpha;
        _alphaThreshold = Math.Clamp(alphaThreshold, 1, 255);
        _reserveTransparent = reserveTransparent && alpha == QuantizeAlpha.Binary;
        _aDim = 2;
        int n = _aDim * D * D * D;
        long[][] set = Interlocked.Exchange(ref s_spare, null) ?? [new long[n], new long[n], new long[n], new long[n], new long[n], new long[n]];
        (_w, _r, _g, _b, _a, _sq) = (set[0], set[1], set[2], set[3], set[4], set[5]);
    }

    // The six histogram planes are 0.5 MB each, so each is a large-object-heap allocation. One zeroed set is kept for reuse: quantizing
    // the frames of an animation one after another then allocates nothing. A set that grew for translucent input is not kept.
    private static long[][]? s_spare;

    private void ReleaseHistogram()
    {
        long[] w = _w;
        if (w == null)
        {
            return;
        }

        long[][] set = [_w, _r, _g, _b, _a, _sq];
        _w = _r = _g = _b = _a = _sq = null!;
        if (_aDim != 2)
        {
            return;
        }

        foreach (long[] m in set)
        {
            Array.Clear(m);
        }

        Interlocked.CompareExchange(ref s_spare, set, null);
    }

    /// <summary>Packs a pixel into the key used for exact color matching: transparent pixels collapse to 0.</summary>
    internal static uint Key(uint px, QuantizeAlpha alpha, int threshold)
    {
        uint a = px >> 24;
        if (alpha == QuantizeAlpha.Binary)
        {
            return a < (uint)threshold ? 0u : px | 0xFF000000u;
        }

        return a == 0 ? 0u : px;
    }

    /// <summary>Adds pixels to the statistics.</summary>
    /// <param name="rgba">Whole pixels, 4 bytes each (R, G, B, A).</param>
    public void AddRows(ReadOnlySpan<byte> rgba)
    {
        int count = rgba.Length >> 2;
        bool full = _alpha == QuantizeAlpha.Full;
        for (int i = 0; i < count; i++)
        {
            uint key = Key(BinaryPrimitives.ReadUInt32LittleEndian(rgba.Slice(i << 2, 4)), _alpha, _alphaThreshold);
            if (_exact && (!_haveLast || key != _lastKey))
            {
                if (_set.Find(key) < 0)
                {
                    if (_set.Count >= _maxColors - (_reserveTransparent ? 1 : 0))
                    {
                        _exact = false;
                    }
                    else
                    {
                        _set.Add(key);
                    }
                }

                _lastKey = key;
                _haveLast = true;
            }

            if (key == 0)
            {
                _transparentCount++;
                continue;
            }

            int r = (int)(key & 0xFF);
            int g = (int)((key >> 8) & 0xFF);
            int b = (int)((key >> 16) & 0xFF);
            int a = (int)(key >> 24);
            if (full && _aDim == 2 && a != 255)
            {
                GrowAlphaPlanes();
            }

            int ab = _aDim == 2 ? 1 : (a >> 5) + 1;
            int idx = (((((ab * D) + (r >> 3) + 1) * D) + (g >> 3) + 1) * D) + (b >> 3) + 1;
            _w[idx]++;
            _r[idx] += r;
            _g[idx] += g;
            _b[idx] += b;
            _a[idx] += a;
            _sq[idx] += (r * r) + (g * g) + (b * b) + (a * a);
        }
    }

    /// <summary>Opaque pixels sit in alpha plane 1; once translucent pixels show up the histogram gets eight alpha bins and plane 1 moves to bin 8.</summary>
    private void GrowAlphaPlanes()
    {
        int cube = D * D * D;
        _w = Grow(_w, cube);
        _r = Grow(_r, cube);
        _g = Grow(_g, cube);
        _b = Grow(_b, cube);
        _a = Grow(_a, cube);
        _sq = Grow(_sq, cube);
        _aDim = 9;
    }

    private static long[] Grow(long[] old, int cube)
    {
        var grown = new long[9 * cube];
        Array.Copy(old, cube, grown, 8 * cube, cube);
        return grown;
    }

    /// <summary>Computes the palette from everything added so far.</summary>
    /// <returns>The palette, usable with <see cref="PaletteMapper"/>.</returns>
    public QuantizedPalette BuildPalette()
    {
        try
        {
            if (_exact)
            {
                int n = _set.Count;
                bool addT = _reserveTransparent && _transparentCount == 0;
                var rgbaExact = new byte[(n + (addT ? 1 : 0)) * 4];
                int tIndex = -1;
                for (int i = 0; i < n; i++)
                {
                    uint k = _set[i];
                    BinaryPrimitives.WriteUInt32LittleEndian(rgbaExact.AsSpan(i * 4, 4), k);
                    if (k == 0)
                    {
                        tIndex = i;
                    }
                }

                if (addT)
                {
                    tIndex = n;
                    n++;
                }

                return new QuantizedPalette(rgbaExact, n, tIndex, _alpha, _alphaThreshold, _set);
            }

            bool binaryTransparent = _transparentCount > 0 || _reserveTransparent; // fully transparent pixels always get their own exact entry
            int target = _maxColors - (binaryTransparent ? 1 : 0);
            return BuildWu(target, binaryTransparent);
        }
        finally
        {
            ReleaseHistogram();
        }
    }

    private QuantizedPalette BuildWu(int target, bool addTransparent)
    {
        bool full = _alpha == QuantizeAlpha.Full;

        // Gather populated cells (mean color, weight) before the histogram is turned into cumulative moments.
        // The cells go in a pooled array that doubles by renting: a growing List would leave every smaller copy behind as garbage
        // for each palette built, and an animation builds many.
        Cell[] cells = Pools<Cell>.Pool.Rent(4096);
        int cellCount = 0;
        try
        {
            for (int ab = 1; ab < _aDim; ab++)
            {
                for (int r = 1; r < D; r++)
                {
                    for (int g = 1; g < D; g++)
                    {
                        int rowBase = (((((ab * D) + r) * D) + g) * D);
                        for (int b = 1; b < D; b++)
                        {
                            long w = _w[rowBase + b];
                            if (w == 0)
                            {
                                continue;
                            }

                            int lutIndex = (((((full ? (_aDim == 2 ? 7 : ab - 1) : 0) << 5) | (r - 1)) << 5 | (g - 1)) << 5) | (b - 1);
                            if (cellCount == cells.Length)
                            {
                                Cell[] bigger = Pools<Cell>.Pool.Rent(cells.Length * 2);
                                Array.Copy(cells, bigger, cellCount);
                                Pools<Cell>.Pool.Return(cells);
                                cells = bigger;
                            }

                            cells[cellCount++] = new Cell(lutIndex, w, (int)((_r[rowBase + b] + (w >> 1)) / w), (int)((_g[rowBase + b] + (w >> 1)) / w), (int)((_b[rowBase + b] + (w >> 1)) / w), (int)((_a[rowBase + b] + (w >> 1)) / w));
                        }
                    }
                }
            }

            var entries = new List<(int R, int G, int B, int A)>();
            if (cellCount > 0)
            {
                CumulateMoments();
                SplitBoxes(target, entries);
                Refine(cells.AsSpan(0, cellCount), entries);
            }

            int count = entries.Count;
            int tIndex = -1;
            var rgba = new byte[(count + (addTransparent ? 1 : 0)) * 4];
            for (int i = 0; i < count; i++)
            {
                rgba[i * 4] = (byte)entries[i].R;
                rgba[(i * 4) + 1] = (byte)entries[i].G;
                rgba[(i * 4) + 2] = (byte)entries[i].B;
                rgba[(i * 4) + 3] = (byte)entries[i].A;
            }

            if (addTransparent)
            {
                tIndex = count;
                count++;
            }

            var palette = new QuantizedPalette(rgba, count, tIndex, _alpha, _alphaThreshold, null);
            for (int ci = 0; ci < cellCount; ci++)
            {
                Cell c = cells[ci];
                palette.SeedCell(c.LutIndex, c.R, c.G, c.B, c.A);
            }

            return palette;
        }
        finally
        {
            Pools<Cell>.Pool.Return(cells);
        }
    }

    private readonly record struct Cell(int LutIndex, long W, int R, int G, int B, int A);

    private void CumulateMoments()
    {
        long[][] all = [_w, _r, _g, _b, _a, _sq];
        foreach (long[] m in all)
        {
            // Prefix sums along each of the four axes, one after another, give the 4-D cumulative moment.
            for (int ab = 0; ab < _aDim; ab++)
            {
                for (int r = 0; r < D; r++)
                {
                    for (int g = 0; g < D; g++)
                    {
                        int rowBase = (((((ab * D) + r) * D) + g) * D);
                        for (int b = 1; b < D; b++)
                        {
                            m[rowBase + b] += m[rowBase + b - 1];
                        }
                    }
                }
            }

            for (int ab = 0; ab < _aDim; ab++)
            {
                for (int r = 0; r < D; r++)
                {
                    for (int g = 1; g < D; g++)
                    {
                        int rowBase = (((((ab * D) + r) * D) + g) * D);
                        for (int b = 0; b < D; b++)
                        {
                            m[rowBase + b] += m[rowBase - D + b];
                        }
                    }
                }
            }

            for (int ab = 0; ab < _aDim; ab++)
            {
                for (int r = 1; r < D; r++)
                {
                    int planeBase = ((ab * D) + r) * D * D;
                    for (int i = 0; i < D * D; i++)
                    {
                        m[planeBase + i] += m[planeBase - (D * D) + i];
                    }
                }
            }

            for (int ab = 1; ab < _aDim; ab++)
            {
                int cube = D * D * D;
                int slab = ab * cube;
                for (int i = 0; i < cube; i++)
                {
                    m[slab + i] += m[slab - cube + i];
                }
            }
        }
    }

    private struct Stats
    {
        public long W, R, G, B, A, Sq;
    }

    private sealed class Box
    {
        public readonly int[] Lo = new int[4];
        public readonly int[] Hi = new int[4];
        public double Variance;
    }

    private Stats Sum(int[] lo, int[] hi)
    {
        var s = default(Stats);
        for (int mask = 0; mask < 16; mask++)
        {
            int a = (mask & 1) != 0 ? lo[0] : hi[0];
            int r = (mask & 2) != 0 ? lo[1] : hi[1];
            int g = (mask & 4) != 0 ? lo[2] : hi[2];
            int b = (mask & 8) != 0 ? lo[3] : hi[3];
            int idx = (((((a * D) + r) * D) + g) * D) + b;
            int bits = (mask & 1) + ((mask >> 1) & 1) + ((mask >> 2) & 1) + ((mask >> 3) & 1);
            if ((bits & 1) == 0)
            {
                s.W += _w[idx];
                s.R += _r[idx];
                s.G += _g[idx];
                s.B += _b[idx];
                s.A += _a[idx];
                s.Sq += _sq[idx];
            }
            else
            {
                s.W -= _w[idx];
                s.R -= _r[idx];
                s.G -= _g[idx];
                s.B -= _b[idx];
                s.A -= _a[idx];
                s.Sq -= _sq[idx];
            }
        }

        return s;
    }

    private static double Norm(in Stats s) =>
        ((double)s.R * s.R + (double)s.G * s.G + (double)s.B * s.B + (double)s.A * s.A) / s.W;

    private double VarianceOf(Box box)
    {
        Stats s = Sum(box.Lo, box.Hi);
        return s.W == 0 ? 0 : s.Sq - Norm(s);
    }

    private void SplitBoxes(int target, List<(int R, int G, int B, int A)> entries)
    {
        var boxes = new List<Box>();
        var first = new Box();
        first.Hi[0] = _aDim - 1;
        first.Hi[1] = first.Hi[2] = first.Hi[3] = D - 1;
        first.Variance = VarianceOf(first);
        boxes.Add(first);
        var lowerHi = new int[4];
        var upperLo = new int[4];
        while (boxes.Count < target)
        {
            int best = -1;
            double bestVar = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Variance > bestVar)
                {
                    bestVar = boxes[i].Variance;
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            Box box = boxes[best];
            Stats whole = Sum(box.Lo, box.Hi);
            int bestDim = -1;
            int bestPos = 0;
            double bestScore = -1;
            for (int d = 0; d < 4; d++)
            {
                for (int p = box.Lo[d] + 1; p < box.Hi[d]; p++)
                {
                    Array.Copy(box.Hi, lowerHi, 4);
                    lowerHi[d] = p;
                    Stats lo = Sum(box.Lo, lowerHi);
                    if (lo.W == 0 || lo.W == whole.W)
                    {
                        continue;
                    }

                    var up = new Stats { W = whole.W - lo.W, R = whole.R - lo.R, G = whole.G - lo.G, B = whole.B - lo.B, A = whole.A - lo.A };
                    double score = Norm(lo) + Norm(up);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestDim = d;
                        bestPos = p;
                    }
                }
            }

            if (bestDim < 0)
            {
                box.Variance = 0; // a single populated cell cannot be split any further
                continue;
            }

            var upper = new Box();
            Array.Copy(box.Lo, upper.Lo, 4);
            Array.Copy(box.Hi, upper.Hi, 4);
            upper.Lo[bestDim] = bestPos;
            box.Hi[bestDim] = bestPos;
            box.Variance = VarianceOf(box);
            upper.Variance = VarianceOf(upper);
            boxes.Add(upper);
        }

        foreach (Box box in boxes)
        {
            Stats s = Sum(box.Lo, box.Hi);
            if (s.W == 0)
            {
                continue;
            }

            long h = s.W >> 1;
            entries.Add(((int)((s.R + h) / s.W), (int)((s.G + h) / s.W), (int)((s.B + h) / s.W), (int)((s.A + h) / s.W)));
        }
    }

    private static void Refine(ReadOnlySpan<Cell> cells, List<(int R, int G, int B, int A)> entries)
    {
        int n = entries.Count;
        int padded = Kernels.QuantizeKernels.PaddedLength(n);
        int[] pr = QuantizedPalette.PaddedTable(padded);
        int[] pg = QuantizedPalette.PaddedTable(padded);
        int[] pb = QuantizedPalette.PaddedTable(padded);
        int[] pa = QuantizedPalette.PaddedTable(padded);
        for (int i = 0; i < n; i++)
        {
            (pr[i], pg[i], pb[i], pa[i]) = entries[i];
        }

        var sr = new long[n];
        var sg = new long[n];
        var sb = new long[n];
        var sa = new long[n];
        var sw = new long[n];
        for (int iter = 0; iter < 2; iter++)
        {
            Array.Clear(sr);
            Array.Clear(sg);
            Array.Clear(sb);
            Array.Clear(sa);
            Array.Clear(sw);
            foreach (Cell c in cells)
            {
                int j = QuantizedPalette.Nearest(pr, pg, pb, pa, n, -1, c.R, c.G, c.B, c.A);
                sr[j] += c.W * c.R;
                sg[j] += c.W * c.G;
                sb[j] += c.W * c.B;
                sa[j] += c.W * c.A;
                sw[j] += c.W;
            }

            for (int j = 0; j < n; j++)
            {
                if (sw[j] > 0)
                {
                    long h = sw[j] >> 1;
                    pr[j] = (int)((sr[j] + h) / sw[j]);
                    pg[j] = (int)((sg[j] + h) / sw[j]);
                    pb[j] = (int)((sb[j] + h) / sw[j]);
                    pa[j] = (int)((sa[j] + h) / sw[j]);
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            entries[i] = (pr[i], pg[i], pb[i], pa[i]);
        }
    }
}
