using System.Buffers.Binary;

namespace OneImage.Ops.Quantize;

/// <summary>
/// A palette of up to 256 RGBA8 entries plus the lookup that maps arbitrary colors to their nearest entry.
/// The lookup is a table over 5-bit-per-channel cells (3 alpha bits in <see cref="QuantizeAlpha.Full"/> mode) that is
/// filled on demand, so mapping a pixel costs one table read after the first hit on a cell. Not thread-safe.
/// </summary>
internal sealed class QuantizedPalette
{
    private readonly int[] _r, _g, _b, _a;
    private readonly ushort[] _lut;
    private readonly bool _full;
    private readonly int _threshold;

    internal QuantizedPalette(byte[] rgba, int count, int transparentIndex, QuantizeAlpha alpha, int threshold, ColorSet? exact)
    {
        Rgba = rgba;
        Count = count;
        TransparentIndex = transparentIndex;
        Alpha = alpha;
        Exact = exact;
        _threshold = threshold;
        _full = alpha == QuantizeAlpha.Full;
        _r = new int[count];
        _g = new int[count];
        _b = new int[count];
        _a = new int[count];
        for (int i = 0; i < count; i++)
        {
            _r[i] = rgba[i * 4];
            _g[i] = rgba[(i * 4) + 1];
            _b[i] = rgba[(i * 4) + 2];
            _a[i] = rgba[(i * 4) + 3];
        }

        _lut = new ushort[(_full ? 8 : 1) << 15];
        Array.Fill(_lut, ushort.MaxValue);
    }

    /// <summary>Gets the entries, 4 bytes each (R, G, B, A).</summary>
    public byte[] Rgba { get; }

    /// <summary>Gets the number of entries, 1 to 256.</summary>
    public int Count { get; }

    /// <summary>Gets the index of the fully transparent entry, or -1 when there is none.</summary>
    public int TransparentIndex { get; }

    /// <summary>Gets the alpha mode the palette was built with.</summary>
    public QuantizeAlpha Alpha { get; }

    /// <summary>Gets whether the palette is exactly the distinct colors of the input (nothing was approximated).</summary>
    public bool IsExact => Exact != null;

    internal ColorSet? Exact { get; }

    /// <summary>Returns the index of the entry nearest to a color (squared distance over all four channels; ties go to the lower index).</summary>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <param name="a">Alpha; ignored in binary mode, where the transparent entry is excluded from the search.</param>
    /// <returns>An entry index.</returns>
    public int Lookup(int r, int g, int b, int a)
    {
        int cell = (r >> 3 << 10) | (g >> 3 << 5) | (b >> 3);
        if (_full)
        {
            cell |= (a >> 5) << 15;
        }

        ushort v = _lut[cell];
        if (v == ushort.MaxValue)
        {
            // Probe with the cell's centre so every color in a cell maps alike (deterministic and order-independent).
            int cr = (r & ~7) | 4;
            int cg = (g & ~7) | 4;
            int cb = (b & ~7) | 4;
            int ca = _full ? ((a & ~31) | 16) : 255;
            v = (ushort)Nearest(_r, _g, _b, _a, Count, TransparentIndex, cr, cg, cb, ca);
            _lut[cell] = v;
        }

        return v;
    }

    /// <summary>Records the nearest entry for a cell from a representative color (the mean of the pixels that fell in it).</summary>
    internal void SeedCell(int lutIndex, int r, int g, int b, int a) =>
        _lut[lutIndex] = (ushort)Nearest(_r, _g, _b, _a, Count, TransparentIndex, r, g, b, _full ? a : 255);

    internal static int Nearest(int[] pr, int[] pg, int[] pb, int[] pa, int count, int skip, int r, int g, int b, int a)
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

    internal int Threshold => _threshold;

    internal uint KeyOf(uint px) => ColorQuantizer.Key(px, Alpha, _threshold);
}

/// <summary>
/// Turns RGBA8 rows into palette indices, optionally dithering. Rows must be fed in order from the top for error diffusion;
/// the result depends only on the pixels and the palette, never on how rows were batched or which thread supplied them.
/// </summary>
internal sealed class PaletteMapper
{
    private static readonly byte[] s_bayer = BuildBayer();

    private readonly QuantizedPalette _palette;
    private readonly Dither _dither;
    private readonly int _width;
    private readonly int _spread;
    private int[] _errCur;
    private int[] _errNext;
    private int _nextRow;

    /// <summary>Creates a mapper.</summary>
    /// <param name="palette">The palette to map to.</param>
    /// <param name="width">Row width in pixels.</param>
    /// <param name="dither">The dithering mode.</param>
    public PaletteMapper(QuantizedPalette palette, int width, Dither dither)
    {
        _palette = palette;
        _width = width;
        _dither = palette.IsExact ? Dither.None : dither;
        _spread = OrderedSpread(palette);
        _errCur = _dither == Dither.FloydSteinberg ? new int[(width + 2) * 3] : [];
        _errNext = _dither == Dither.FloydSteinberg ? new int[(width + 2) * 3] : [];
    }

    /// <summary>
    /// The ordered-dither amplitude: the mean distance from an entry to its nearest neighbour, per channel. Noise of about one palette
    /// step is enough to hide banding; more only adds grain.
    /// </summary>
    private static int OrderedSpread(QuantizedPalette p)
    {
        byte[] c = p.Rgba;
        double sum = 0;
        int n = 0;
        for (int i = 0; i < p.Count; i++)
        {
            if (i == p.TransparentIndex)
            {
                continue;
            }

            long best = long.MaxValue;
            for (int j = 0; j < p.Count; j++)
            {
                if (j == i || j == p.TransparentIndex)
                {
                    continue;
                }

                long dr = c[i * 4] - c[j * 4];
                long dg = c[(i * 4) + 1] - c[(j * 4) + 1];
                long db = c[(i * 4) + 2] - c[(j * 4) + 2];
                best = Math.Min(best, (dr * dr) + (dg * dg) + (db * db));
            }

            if (best != long.MaxValue)
            {
                sum += Math.Sqrt(best);
                n++;
            }
        }

        return n == 0 ? 0 : Math.Clamp((int)(sum / n / 1.7320508075688772), 2, 128);
    }

    private static byte[] BuildBayer()
    {
        var m = new byte[64];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int v = 0;
                for (int i = 2; i >= 0; i--)
                {
                    v = (v << 2) | ((((x >> i) ^ (y >> i)) & 1) << 1) | ((y >> i) & 1);
                }

                m[(y * 8) + x] = (byte)v;
            }
        }

        return m;
    }

    /// <summary>Maps one row.</summary>
    /// <param name="y">The row number (used by ordered dithering; error diffusion requires consecutive rows starting at 0 and ignores it).</param>
    /// <param name="rgba">The row's pixels, 4 bytes each.</param>
    /// <param name="indices">Receives one palette index per pixel.</param>
    public void MapRow(int y, ReadOnlySpan<byte> rgba, Span<byte> indices)
    {
        QuantizedPalette p = _palette;
        int w = _width;
        rgba = rgba.Slice(0, w * 4);
        indices = indices.Slice(0, w);
        int tIndex = p.TransparentIndex;
        int limit = p.Alpha == QuantizeAlpha.Binary ? p.Threshold : 1;
        if (p.Exact is { } set)
        {
            uint lastKey = 0;
            byte lastIdx = 0;
            bool have = false;
            for (int x = 0; x < w; x++)
            {
                uint key = p.KeyOf(BinaryPrimitives.ReadUInt32LittleEndian(rgba.Slice(x * 4, 4)));
                if (!have || key != lastKey)
                {
                    int idx = set.Find(key);
                    if (idx < 0)
                    {
                        idx = p.Lookup((int)(key & 0xFF), (int)((key >> 8) & 0xFF), (int)((key >> 16) & 0xFF), (int)(key >> 24));
                    }

                    lastIdx = (byte)idx;
                    lastKey = key;
                    have = true;
                }

                indices[x] = lastIdx;
            }

            return;
        }

        switch (_dither)
        {
            case Dither.None:
                for (int x = 0; x < w; x++)
                {
                    ReadOnlySpan<byte> px = rgba.Slice(x * 4, 4);
                    indices[x] = tIndex >= 0 && px[3] < limit ? (byte)tIndex : (byte)p.Lookup(px[0], px[1], px[2], px[3]);
                }

                break;
            case Dither.Ordered:
                for (int x = 0; x < w; x++)
                {
                    ReadOnlySpan<byte> px = rgba.Slice(x * 4, 4);
                    if (tIndex >= 0 && px[3] < limit)
                    {
                        indices[x] = (byte)tIndex;
                        continue;
                    }

                    // Threshold in (-spread/2, spread/2), centred so flat colors keep their average.
                    int t = ((((s_bayer[((y & 7) * 8) + (x & 7)] * 2) + 1) - 64) * _spread) / 128;
                    indices[x] = (byte)p.Lookup(Math.Clamp(px[0] + t, 0, 255), Math.Clamp(px[1] + t, 0, 255), Math.Clamp(px[2] + t, 0, 255), px[3]);
                }

                break;
            default:
                FloydSteinberg(rgba, indices, limit, tIndex);
                break;
        }

        _nextRow++;
    }

    private void FloydSteinberg(ReadOnlySpan<byte> rgba, Span<byte> indices, int limit, int tIndex)
    {
        QuantizedPalette p = _palette;
        byte[] pal = p.Rgba;
        int[] cur = _errCur;
        int[] next = _errNext;
        Array.Clear(next);
        for (int x = 0; x < _width; x++)
        {
            ReadOnlySpan<byte> px = rgba.Slice(x * 4, 4);
            if (tIndex >= 0 && px[3] < limit)
            {
                indices[x] = (byte)tIndex;
                continue;
            }

            int e = (x + 1) * 3;
            int r = Math.Clamp(px[0] + ((cur[e] + 8) >> 4), 0, 255);
            int g = Math.Clamp(px[1] + ((cur[e + 1] + 8) >> 4), 0, 255);
            int b = Math.Clamp(px[2] + ((cur[e + 2] + 8) >> 4), 0, 255);
            int idx = p.Lookup(r, g, b, px[3]);
            indices[x] = (byte)idx;
            int er = r - pal[idx * 4];
            int eg = g - pal[(idx * 4) + 1];
            int eb = b - pal[(idx * 4) + 2];
            cur[e + 3] += er * 7;
            cur[e + 4] += eg * 7;
            cur[e + 5] += eb * 7;
            next[e - 3] += er * 3;
            next[e - 2] += eg * 3;
            next[e - 1] += eb * 3;
            next[e] += er * 5;
            next[e + 1] += eg * 5;
            next[e + 2] += eb * 5;
            next[e + 3] += er;
            next[e + 4] += eg;
            next[e + 5] += eb;
        }

        (_errCur, _errNext) = (next, cur);
    }
}
