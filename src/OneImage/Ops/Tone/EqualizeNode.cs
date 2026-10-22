using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;

namespace OneImage;

/// <summary>
/// Histogram equalization of luminance (see <see cref="Image.HistogramEqualization"/> for the behavior). The node works on
/// gray or RGB samples of 8 or 16 bits (other sample types are processed as 16-bit), with straight alpha carried through.
/// The global and tile methods count luminance over the whole input once (in parallel bands whose results are merged in a fixed
/// order, so they never depend on the thread count) and keep the resulting mappings; the sliding window method needs only the
/// neighborhood of each output tile.
/// </summary>
internal sealed partial class EqualizeNode : Node
{
    internal const long MaxTableEntries = 1L << 24;

    // Rec. 709 luma weights. The luminance is formed in 32-bit float the way ImageSharp forms it, because pixels whose exact
    // luma is half way between two levels (about 1 in 10000) land on one side or the other depending on float rounding, and a
    // different rounding moves them a whole histogram bin: each sample is scaled to 0..1, multiplied by its weight, the three
    // products are added in R, G, B order, the sum is scaled back by the full scale and rounded to even. Probing showed two
    // variants of the scaling: ImageSharp's global method (and the histogram counts of its sliding window) multiply the sample by
    // 1/full-scale; its tile method and the sliding window's per-pixel lookup divide by the full scale. Both are kept so
    // that near-tie pixels fall in the same bin as there.
    private const float WeightR = 0.2126f;
    private const float WeightG = 0.7152f;
    private const float WeightB = 0.0722f;

    private static readonly float[] s_r8 = Weights(WeightR, divide: false);
    private static readonly float[] s_g8 = Weights(WeightG, divide: false);
    private static readonly float[] s_b8 = Weights(WeightB, divide: false);
    private static readonly float[] s_r8d = Weights(WeightR, divide: true);
    private static readonly float[] s_g8d = Weights(WeightG, divide: true);
    private static readonly float[] s_b8d = Weights(WeightB, divide: true);

    private readonly HistogramEqualizationMethod _method;
    private readonly int _levels;
    private readonly bool _clip;
    private readonly int _limit;
    private readonly int _tiles;
    private readonly PixelFormat _work;
    private readonly int _max;
    private readonly int _bps;
    private readonly int _colors;
    private readonly bool _alpha;
    private readonly int _wbpp;
    private readonly int _tileW;
    private readonly int _tileH;
    private readonly int _window;
    private readonly object _gate = new();
    private float[]? _luts;

    public EqualizeNode(Node input, HistogramEqualizationMethod method, int levels, bool clip, int limit, int tiles)
        : base(
            input.Width,
            input.Height,
            input.Format,
            new OpDescriptor(
                "histogram_equalization",
                new OpParameter("method", MethodName(method)),
                new OpParameter("luminance_levels", (long)levels),
                new OpParameter("clip_histogram", clip),
                new OpParameter("clip_limit", (long)limit),
                new OpParameter("tiles", (long)tiles)),
            input)
    {
        if (input.Format.Model is not (ColorModel.Gray or ColorModel.Rgb))
        {
            throw new ImageOperationException($"HistogramEqualization is defined for gray and RGB images, not {input.Format.Model}.");
        }

        _method = method;
        _levels = levels;
        _clip = clip;
        _limit = limit;
        _tiles = tiles;
        SampleType st = input.Format.Sample == SampleType.U8 ? SampleType.U8 : SampleType.U16;
        _work = new PixelFormat(input.Format.Model, st, input.Format.HasAlpha ? AlphaMode.Straight : AlphaMode.None);
        _max = st == SampleType.U8 ? 255 : 65535;
        _bps = st == SampleType.U8 ? 1 : 2;
        _colors = input.Format.ColorChannels;
        _alpha = input.Format.HasAlpha;
        _wbpp = (_colors + (_alpha ? 1 : 0)) * _bps;
        _tileW = (Width + tiles - 1) / tiles;
        _tileH = (Height + tiles - 1) / tiles;
        _window = Math.Max(2, Width / tiles);
    }

    private static string MethodName(HistogramEqualizationMethod m) => m switch
    {
        HistogramEqualizationMethod.Global => "global",
        HistogramEqualizationMethod.AdaptiveTileInterpolation => "adaptive-tiles",
        _ => "adaptive-window",
    };

    private static float[] Weights(float w, bool divide)
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            t[i] = w * (divide ? i / 255f : i * (1f / 255f));
        }

        return t;
    }

    /// <summary>Luminance bins of one row of work-format pixels.</summary>
    private void FillBins(ReadOnlySpan<byte> row, int width, Span<ushort> bins, bool divide = false)
    {
        int step = _wbpp;
        int c = _colors;
        bool rescale = _levels != _max + 1;
        if (!rescale && _bps == 1 && c == 3 && _alpha)
        {
            Kernels.EqualizeKernel.LumaRgba8(row, width, bins, divide);
            return;
        }

        for (int x = 0; x < width; x++)
        {
            int p = x * step;
            int lum;
            if (_bps == 1)
            {
                if (c != 3)
                {
                    lum = row[p];
                }
                else if (divide)
                {
                    lum = (int)MathF.Round((s_r8d[row[p]] + s_g8d[row[p + 1]] + s_b8d[row[p + 2]]) * 255f);
                }
                else
                {
                    lum = (int)MathF.Round((s_r8[row[p]] + s_g8[row[p + 1]] + s_b8[row[p + 2]]) * 255f);
                }
            }
            else if (c == 3)
            {
                int r = BinaryPrimitives.ReadUInt16LittleEndian(row[p..]);
                int g = BinaryPrimitives.ReadUInt16LittleEndian(row[(p + 2)..]);
                int b = BinaryPrimitives.ReadUInt16LittleEndian(row[(p + 4)..]);
                const float inv = 1f / 65535f;
                lum = divide
                    ? (int)MathF.Round(((WeightR * (r / 65535f)) + (WeightG * (g / 65535f)) + (WeightB * (b / 65535f))) * 65535f)
                    : (int)MathF.Round(((WeightR * (r * inv)) + (WeightG * (g * inv)) + (WeightB * (b * inv))) * 65535f);
            }
            else
            {
                lum = BinaryPrimitives.ReadUInt16LittleEndian(row[p..]);
            }

            // A level count other than the sample range quantizes: round(lum / full scale * (levels - 1)).
            bins[x] = rescale ? (ushort)((((long)lum * (_levels - 1) * 2) + _max) / (2L * _max)) : (ushort)lum;
        }
    }

    /// <summary>
    /// Clips a histogram at <paramref name="limit"/> and spreads the removed counts back over all bins in whole counts, so the
    /// total is unchanged: every bin gets <c>excess / bins</c> (integer division) and the remainder goes one count each to bins
    /// spaced <c>bins / remainder</c> apart, starting at bin 0. A bin can end up above the limit. This is the integer scheme of
    /// the published contrast-limited equalization procedure (Zuiderveld, Graphics Gems IV) without its refill of bins
    /// that would pass the limit; probing showed ImageSharp's results follow exactly this variant, and neither a smooth
    /// fractional spread nor the capped variant.
    /// </summary>
    internal static void ClipAndRedistribute(Span<int> h, int limit)
    {
        int bins = h.Length;
        long excess = 0;
        for (int i = 0; i < bins; i++)
        {
            if (h[i] > limit)
            {
                excess += h[i] - limit;
            }
        }

        if (excess == 0)
        {
            return;
        }

        long share = excess / bins;
        for (int i = 0; i < bins; i++)
        {
            h[i] = Math.Min(h[i], limit) + (int)share;
        }

        long left = excess - (share * bins);
        if (left > 0)
        {
            // left < bins, so one sweep with this stride places every remaining count.
            long step = Math.Max(1, bins / left);
            for (long b = 0; b < bins && left > 0; b += step)
            {
                h[(int)b]++;
                left--;
            }
        }
    }

    /// <summary>Turns a histogram into the output value (in sample units) of every bin. <paramref name="n"/> counts the pixels the histogram stands for, including any that no bin holds.</summary>
    private void BuildLut(ReadOnlySpan<uint> hist, long n, Span<float> lut, Span<int> scratch)
    {
        for (int i = 0; i < hist.Length; i++)
        {
            scratch[i] = (int)hist[i];
        }

        if (_clip)
        {
            ClipAndRedistribute(scratch[..hist.Length], _limit);
        }

        long c = 0;
        long min = 0;
        for (int i = 0; i < lut.Length; i++)
        {
            c += scratch[i];
            if (min == 0 && c > 0)
            {
                min = c;
            }

            lut[i] = min == 0 || n <= min ? 0f : (float)((double)(c - min) / (n - min) * _max);
        }
    }

    private float[] EnsureLuts(RenderContext context)
    {
        float[]? l = Volatile.Read(ref _luts);
        if (l != null)
        {
            return l;
        }

        lock (_gate)
        {
            return _luts ??= BuildLuts(context);
        }
    }

    private float[] BuildLuts(RenderContext context)
    {
        int levels = _levels;
        if (_method == HistogramEqualizationMethod.Global)
        {
            uint[][] parts = CountBands(context, tiled: false, out _);
            var total = new uint[levels];
            foreach (uint[] part in parts)
            {
                for (int i = 0; i < levels; i++)
                {
                    total[i] += part[i];
                }
            }

            var lut = new float[levels];
            BuildLut(total, (long)Width * Height, lut, new int[levels]);
            return lut;
        }

        uint[][] bands = CountBands(context, tiled: true, out _);
        int n = _tiles;
        var luts = new float[checked(n * n * levels)];
        long full = (long)_tileW * _tileH;
        int[] scratch = new int[levels];
        for (int ty = 0; ty < n; ty++)
        {
            for (int tx = 0; tx < n; tx++)
            {
                int index = (ty * n) + tx;
                ReadOnlySpan<uint> hist = ty < bands.Length ? bands[ty].AsSpan(tx * levels, levels) : new uint[levels];
                BuildLut(hist, full, luts.AsSpan(index * levels, levels), scratch);
            }
        }

        return luts;
    }

    /// <summary>
    /// Counts luminance bins over the whole input in bands of rows. Global: one histogram per band of 256 rows. Tiled: one band
    /// per row of tiles, holding the histograms of its tiles side by side. Bands are independent, so any thread count gives the same counts.
    /// </summary>
    private uint[][] CountBands(RenderContext context, bool tiled, out int bandRows)
    {
        int w = Width;
        int h = Height;
        int levels = _levels;
        int rowsPerBand = tiled ? _tileH : 256;
        bandRows = rowsPerBand;
        int bands = (h + rowsPerBand - 1) / rowsPerBand;
        var result = new uint[bands][];
        int[] colOffset = new int[w];
        if (tiled)
        {
            for (int x = 0; x < w; x++)
            {
                colOffset[x] = (x / _tileW) * levels;
            }
        }

        int degree = Inputs[0].Streaming ? 1 : Math.Max(1, Math.Min(context.OuterDegree, bands));
        if (degree <= 1)
        {
            for (int b = 0; b < bands; b++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                result[b] = CountBand(b, rowsPerBand, tiled, colOffset, context);
            }

            return result;
        }

        RenderContext nested = context.Nested;
        var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = context.CancellationToken };
        try
        {
            Parallel.For(0, bands, options, b => result[b] = CountBand(b, rowsPerBand, tiled, colOffset, nested));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }

        return result;
    }

    private uint[] CountBand(int band, int rowsPerBand, bool tiled, int[] colOffset, RenderContext context)
    {
        Node input = Inputs[0];
        int levels = _levels;
        int y0 = band * rowsPerBand;
        int y1 = Math.Min(Height, y0 + rowsPerBand);
        var counts = new uint[tiled ? _tiles * levels : levels];
        // Global counting spreads over four histograms so that runs of equal pixels do not wait on each other's stores.
        uint[]? lanes = tiled ? null : new uint[4 * levels];
        bool same = input.Format == _work;
        int ibpp = input.Format.BytesPerPixel;
        byte[]? converted = same ? null : ArrayPool<byte>.Shared.Rent(Tile.Size * _wbpp);
        ushort[] bins = ArrayPool<ushort>.Shared.Rent(Tile.Size);
        try
        {
            for (int ty = y0 >> Tile.Shift; ty <= (y1 - 1) >> Tile.Shift; ty++)
            {
                for (int tx = 0; tx < input.TilesX; tx++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    // Tiles are read in place and not kept in the cache: this pass touches each of them once.
                    Tile tile = input.GetTile(tx, ty, context, useCache: false);
                    try
                    {
                        ImageRect rect = input.TileRect(tx, ty);
                        int w = rect.Width;
                        for (int y = Math.Max(y0, rect.Y); y < Math.Min(y1, rect.Bottom); y++)
                        {
                            ReadOnlySpan<byte> row = tile.Span.Slice((y - rect.Y) * Tile.Size * ibpp, w * ibpp);
                            if (!same)
                            {
                                Kernels.ConvertKernel.ConvertRow(row, input.Format, converted, _work, w);
                                row = converted.AsSpan(0, w * _wbpp);
                            }

                            FillBins(row, w, bins, divide: tiled);
                            if (tiled)
                            {
                                for (int i = 0; i < w; i++)
                                {
                                    counts[colOffset[rect.X + i] + bins[i]]++;
                                }
                            }
                            else
                            {
                                int i = 0;
                                for (; i + 4 <= w; i += 4)
                                {
                                    lanes![bins[i]]++;
                                    lanes[levels + bins[i + 1]]++;
                                    lanes[(2 * levels) + bins[i + 2]]++;
                                    lanes[(3 * levels) + bins[i + 3]]++;
                                }

                                for (; i < w; i++)
                                {
                                    lanes![bins[i]]++;
                                }
                            }
                        }
                    }
                    finally
                    {
                        tile.Release();
                    }
                }
            }

            if (lanes != null)
            {
                for (int i = 0; i < levels; i++)
                {
                    counts[i] = lanes[i] + lanes[levels + i] + lanes[(2 * levels) + i] + lanes[(3 * levels) + i];
                }
            }

            return counts;
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(bins);
            if (converted != null)
            {
                ArrayPool<byte>.Shared.Return(converted);
            }
        }
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        if (_method == HistogramEqualizationMethod.AdaptiveSlidingWindow)
        {
            return ComputeWindowTile(o, context);
        }

        float[] luts = EnsureLuts(context);
        Node input = Inputs[0];
        int w = o.Width;
        int wbpp = _wbpp;
        int ibpp = input.Format.BytesPerPixel;
        int obpp = Format.BytesPerPixel;
        bool convert = _work != input.Format;
        bool global = _method == HistogramEqualizationMethod.Global;
        int n = _tiles;
        int levels = _levels;
        Tile source = input.GetTile(tx, ty, context);
        ushort[] bins = ArrayPool<ushort>.Shared.Rent(w);
        int[] vals = ArrayPool<int>.Shared.Rent(w);
        byte[]? workRow = convert ? ArrayPool<byte>.Shared.Rent(w * wbpp) : null;
        byte[]? outRow = convert ? ArrayPool<byte>.Shared.Rent(w * wbpp) : null;
        int[]? table = null;
        int[]? xa = null;
        int[]? xb = null;
        float[]? xf = null;
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            if (global)
            {
                table = ArrayPool<int>.Shared.Rent(levels);
                for (int i = 0; i < levels; i++)
                {
                    table[i] = Quantize(luts[i]);
                }
            }
            else
            {
                xa = ArrayPool<int>.Shared.Rent(w);
                xb = ArrayPool<int>.Shared.Rent(w);
                xf = ArrayPool<float>.Shared.Rent(w);
                for (int x = 0; x < w; x++)
                {
                    Axis(o.X + x, _tileW, n, out xa[x], out xb[x], out xf[x]);
                }
            }

            for (int r = 0; r < o.Height; r++)
            {
                ReadOnlySpan<byte> src = source.Span.Slice(r * Tile.Size * ibpp, w * ibpp);
                if (convert)
                {
                    Kernels.ConvertKernel.ConvertRow(src, input.Format, workRow, _work, w);
                    src = workRow.AsSpan(0, w * wbpp);
                }

                FillBins(src, w, bins, divide: !global);
                if (global)
                {
                    for (int x = 0; x < w; x++)
                    {
                        vals[x] = table![bins[x]];
                    }
                }
                else
                {
                    int y = o.Y + r;
                    Axis(y, _tileH, n, out int ya, out int yb, out float yf);
                    bool ys = ya == yb;
                    int rowA = ya * n * levels;
                    int rowB = yb * n * levels;
                    for (int x = 0; x < w; x++)
                    {
                        bool xs = xa![x] == xb![x];
                        // Where the last block of an axis ends at the image edge, ImageSharp leaves the final pixel of the
                        // edge strip along the other axis untouched.
                        if (xs != ys && ((!xs && o.X + x == Width - 1) || (!ys && y == Height - 1)))
                        {
                            vals[x] = -1;
                            continue;
                        }

                        int bin = bins[x];
                        float fx = xf![x];
                        float top = luts[rowA + (xa[x] * levels) + bin];
                        if (fx != 0f)
                        {
                            top = (top * (1f - fx)) + (luts[rowA + (xb[x] * levels) + bin] * fx);
                        }

                        if (yf != 0f)
                        {
                            float bottom = luts[rowB + (xa[x] * levels) + bin];
                            if (fx != 0f)
                            {
                                bottom = (bottom * (1f - fx)) + (luts[rowB + (xb[x] * levels) + bin] * fx);
                            }

                            top = (top * (1f - yf)) + (bottom * yf);
                        }

                        vals[x] = Quantize(top);
                    }
                }

                Span<byte> dst = convert ? outRow.AsSpan(0, w * wbpp) : tile.Span.Slice(r * Tile.Size * obpp, w * obpp);
                StoreRow(dst, src, vals, w);
                if (convert)
                {
                    Kernels.ConvertKernel.ConvertRow(dst, _work, tile.Span.Slice(r * Tile.Size * obpp, w * obpp), Format, w);
                }
            }

            return tile;
        }
        catch
        {
            tile.Release();
            throw;
        }
        finally
        {
            source.Release();
            ArrayPool<ushort>.Shared.Return(bins);
            ArrayPool<int>.Shared.Return(vals);
            if (workRow != null)
            {
                ArrayPool<byte>.Shared.Return(workRow);
                ArrayPool<byte>.Shared.Return(outRow!);
            }

            if (table != null)
            {
                ArrayPool<int>.Shared.Return(table);
            }

            if (xa != null)
            {
                ArrayPool<int>.Shared.Return(xa);
                ArrayPool<int>.Shared.Return(xb!);
                ArrayPool<float>.Shared.Return(xf!);
            }
        }
    }

    /// <summary>Rounds a mapped value (in sample units) to the nearest sample, half up.</summary>
    private int Quantize(float v)
    {
        int q = (int)(v + 0.5f);
        return q > _max ? _max : (q < 0 ? 0 : q);
    }

    /// <summary>
    /// Position of pixel <paramref name="p"/> between tile centers along one axis. Tile <c>t</c>'s center is at <c>t * size + size / 2</c>;
    /// from one center to the next the second tile's weight is <c>k / (size - 1)</c>. Before the first and after the last center a
    /// single tile applies.
    /// </summary>
    private static void Axis(int p, int size, int tiles, out int a, out int b, out float f)
    {
        int half = size / 2;
        a = 0;
        b = 0;
        f = 0f;
        if (p < half)
        {
            return;
        }

        int j = (p - half) / size;
        if (j + 1 >= tiles)
        {
            a = tiles - 1;
            b = tiles - 1;
            return;
        }

        a = j;
        b = j + 1;
        f = size == 1 ? 0f : ((p - half) % size) / (float)(size - 1);
    }

    /// <summary>Writes one row: every color channel of a pixel gets the mapped value, alpha is copied; a negative value keeps the source pixel.</summary>
    private void StoreRow(Span<byte> dst, ReadOnlySpan<byte> src, ReadOnlySpan<int> vals, int width)
    {
        int c = _colors;
        int wbpp = _wbpp;
        if (_bps == 1 && c == 3 && _alpha)
        {
            for (int x = 0; x < width; x++)
            {
                int v = vals[x];
                int p = x * 4;
                if (v < 0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(dst[p..], BinaryPrimitives.ReadUInt32LittleEndian(src[p..]));
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(dst[p..], ((uint)v * 0x010101u) | ((uint)src[p + 3] << 24));
                }
            }

            return;
        }

        if (_bps == 1 && c == 1 && !_alpha)
        {
            for (int x = 0; x < width; x++)
            {
                int v = vals[x];
                dst[x] = v < 0 ? src[x] : (byte)v;
            }

            return;
        }

        for (int x = 0; x < width; x++)
        {
            int p = x * wbpp;
            int v = vals[x];
            if (v < 0)
            {
                src.Slice(p, wbpp).CopyTo(dst.Slice(p, wbpp));
                continue;
            }

            if (_bps == 1)
            {
                byte b = (byte)v;
                dst[p] = b;
                if (c == 3)
                {
                    dst[p + 1] = b;
                    dst[p + 2] = b;
                }

                if (_alpha)
                {
                    dst[p + c] = src[p + c];
                }
            }
            else
            {
                for (int k = 0; k < c; k++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(dst[(p + (k * 2))..], (ushort)v);
                }

                if (_alpha)
                {
                    src.Slice(p + (c * 2), 2).CopyTo(dst.Slice(p + (c * 2), 2));
                }
            }
        }
    }
}
