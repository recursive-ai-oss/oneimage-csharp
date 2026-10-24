using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using OneImage.Kernels;

namespace OneImage.Ops.Quantize;

/// <summary>
/// A palette that answers "which entry is nearest to this color" exactly: smallest squared distance over R, G, B and A, ties to the
/// lower index. Shared by all workers. When every entry has the same alpha the alpha term cannot change the order, so the search runs
/// over RGB only and uses a lazily built grid: for each 8×8×8 cell of the color cube, the entries that could be nearest to some
/// point in the cell (a pure function of the cell, so any thread may build it). A lookup then scans a handful of entries.
/// </summary>
internal sealed class PaletteIndex
{
    private const int BruteLimit = 6;
    private const int Shift = 3; // grid cells are 8 levels wide in each channel
    private const int Bits = 8 - Shift;
    private const int CellMax = (1 << Shift) - 1;
    private readonly Cand[] _all;
    private readonly bool _uniformAlpha;
    private readonly Cand[]?[]? _cells;

    // One palette entry laid out for the distance loop. Extra is the alpha term of the distance to a fully opaque query: zero when
    // all entries share one alpha (it cannot change the order), else (a - 255)^2.
    private readonly struct Cand
    {
        public readonly int R, G, B, A, Extra, Index;

        public Cand(int index, int r, int g, int b, int a, int extra)
        {
            Index = index;
            R = r;
            G = g;
            B = b;
            A = a;
            Extra = extra;
        }
    }

    public PaletteIndex(ReadOnlySpan<byte> rgba)
    {
        Count = rgba.Length / 4;
        Rgba = rgba.ToArray();
        _uniformAlpha = true;
        for (int i = 1; i < Count; i++)
        {
            _uniformAlpha &= rgba[(i * 4) + 3] == rgba[3];
        }

        _all = new Cand[Count];
        for (int i = 0; i < Count; i++)
        {
            int a = rgba[(i * 4) + 3];
            _all[i] = new Cand(i, rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2], a, _uniformAlpha ? 0 : (a - 255) * (a - 255));
        }

        if (Count > BruteLimit)
        {
            _cells = new Cand[1 << (3 * Bits)][];
        }
    }

    public int Count { get; }

    /// <summary>Gets the entries, 4 bytes each.</summary>
    public byte[] Rgba { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Nearest(int r, int g, int b, int a)
    {
        // The grid answers any query when alpha is uniform, and fully opaque queries otherwise (the common case: a palette with
        // one transparent entry dithering an opaque photo).
        Cand[]?[]? cells = _cells;
        if (cells != null && (_uniformAlpha || a == 255))
        {
            int cell = ((r >> Shift) << (2 * Bits)) | ((g >> Shift) << Bits) | (b >> Shift);
            Cand[] list = cells[cell] ?? Build(cell);
            return list.Length == 1 ? list[0].Index : Scan(list, r, g, b, a, true);
        }

        return Scan(_all, r, g, b, a, false);
    }

    private static int Scan(Cand[] list, int r, int g, int b, int a, bool rgbOnly)
    {
        int best = 0;
        int bd = int.MaxValue;
        for (int i = 0; i < list.Length; i++)
        {
            ref readonly Cand c = ref list[i];
            int dr = c.R - r;
            int dg = c.G - g;
            int db = c.B - b;
            int d = (dr * dr) + (dg * dg) + (db * db);
            d += rgbOnly ? c.Extra : (c.A - a) * (c.A - a);
            if (d < bd)
            {
                bd = d;
                best = c.Index;
            }
        }

        return best;
    }

    private Cand[] Build(int cell)
    {
        int lr = (cell >> (2 * Bits)) << Shift;
        int lg = ((cell >> Bits) & ((1 << Bits) - 1)) << Shift;
        int lb = (cell & ((1 << Bits) - 1)) << Shift;
        Span<int> min = Count <= 512 ? stackalloc int[Count] : new int[Count];
        int bound = int.MaxValue;
        for (int i = 0; i < Count; i++)
        {
            Cand c = _all[i];
            min[i] = Gap(c.R, lr) + Gap(c.G, lg) + Gap(c.B, lb) + c.Extra;
            int far = Far(c.R, lr) + Far(c.G, lg) + Far(c.B, lb) + c.Extra;
            bound = Math.Min(bound, far);
        }

        int n = 0;
        for (int i = 0; i < Count; i++)
        {
            if (min[i] <= bound)
            {
                n++;
            }
        }

        var result = new Cand[n];
        n = 0;
        for (int i = 0; i < Count; i++)
        {
            if (min[i] <= bound)
            {
                result[n++] = _all[i];
            }
        }

        Volatile.Write(ref _cells![cell], result);
        return result;
    }

    // Squared distance from p to the interval [lo, lo + CellMax], and to its farthest end.
    private static int Gap(int p, int lo)
    {
        int d = p < lo ? lo - p : p > lo + CellMax ? p - (lo + CellMax) : 0;
        return d * d;
    }

    private static int Far(int p, int lo)
    {
        int d = Math.Max(Math.Abs(p - lo), Math.Abs(p - (lo + CellMax)));
        return d * d;
    }
}

/// <summary>
/// One pass's view of a <see cref="PaletteIndex"/>. In coarse mode it remembers the answer for the first color seen in each
/// 5-bit-per-channel cell and gives it to every later color in that cell (see <c>fastMatch</c> of <see cref="Image.Dither(IReadOnlyList{Rgba32}, Dither, float, bool, CancellationToken)"/>).
/// Not thread-safe; make one per pass.
/// </summary>
internal sealed class ExactPalette
{
    private readonly PaletteIndex _index;
    private readonly ushort[]? _cells;

    public ExactPalette(PaletteIndex index, bool coarse)
    {
        _index = index;
        if (coarse)
        {
            _cells = new ushort[1 << 20];
            Array.Fill(_cells, ushort.MaxValue);
        }
    }

    /// <summary>Returns the index of the nearest entry to a packed little-endian RGBA color.</summary>
    public int Nearest(uint rgba)
    {
        int r = (int)(rgba & 0xFF);
        int g = (int)((rgba >> 8) & 0xFF);
        int b = (int)((rgba >> 16) & 0xFF);
        int a = (int)(rgba >> 24);
        if (_cells == null)
        {
            return _index.Nearest(r, g, b, a);
        }

        int cell = ((r >> 3) << 15) | ((g >> 3) << 10) | ((b >> 3) << 5) | (a >> 3);
        int cached = _cells[cell];
        if (cached != ushort.MaxValue)
        {
            return cached;
        }

        int idx = _index.Nearest(r, g, b, a);
        _cells[cell] = (ushort)idx;
        return idx;
    }
}

/// <summary>
/// Graph node for <see cref="Image.Dither(IReadOnlyList{Rgba32}, Dither, float, bool, CancellationToken)"/>: maps every pixel to its nearest entry of a
/// caller palette, spreading the error with an error-diffusion kernel or perturbing the input with an ordered-dither matrix.
/// Ordered modes are pixel-local and compute per tile; error diffusion runs in row order, so the first tile request computes the
/// frame in one forward pass and keeps the result.
/// </summary>
internal sealed class DitherNode : Node
{
    private readonly byte[] _palette;
    private readonly PaletteIndex _index;
    private readonly Dither _dither;
    private readonly float _scale;
    private readonly bool _coarse;
    private readonly DiffusionKernel? _kernel;
    private readonly int[]? _matrix;
    private readonly int _matrixSize;
    private readonly float _spread;
    private readonly float[]? _offsets; // per matrix cell: the amount added to every channel
    private readonly byte[]? _perturb; // per matrix cell, per input byte: the byte after adding the amount
    private readonly uint[] _packed; // palette entries as little-endian RGBA
    private readonly object _gate = new();
    private TileGrid? _grid;

    public DitherNode(Node input, byte[] paletteRgba, Dither dither, float scale, bool coarse)
        : base(
            input.Width,
            input.Height,
            input.Format.HasAlpha || HasTranslucentEntry(paletteRgba) ? PixelFormat.Rgba8 : PixelFormat.Rgb8,
            new OpDescriptor(
                "dither",
                new OpParameter("dither", dither.ToString()),
                new OpParameter("scale", (double)scale),
                new OpParameter("match", coarse ? "fast" : "exact"),
                new OpParameter("palette", Convert.ToHexString(paletteRgba))),
            input)
    {
        _coarse = coarse;
        _palette = paletteRgba;
        _index = new PaletteIndex(paletteRgba);
        _packed = new uint[paletteRgba.Length / 4];
        for (int i = 0; i < _packed.Length; i++)
        {
            _packed[i] = BitConverter.ToUInt32(paletteRgba, i * 4);
        }

        _dither = dither;
        _scale = scale;
        _kernel = DitherTables.Kernel(dither);
        if (_kernel == null && dither != Dither.None)
        {
            _matrix = DitherTables.Matrix(dither, out _matrixSize);

            // The perturbation amplitude shrinks as the palette grows: one step between neighbouring levels of an evenly spread
            // palette, which has cbrt(count) levels per channel.
            int count = paletteRgba.Length / 4;
            float levels = MathF.Cbrt(count) - 1f;
            _spread = levels <= 1f ? 255f : MathF.Floor(255f / levels);
            _offsets = new float[_matrix!.Length];
            for (int i = 0; i < _offsets.Length; i++)
            {
                _offsets[i] = _spread * _scale * (((_matrix[i] + 1f) / (_matrixSize * _matrixSize)) - 0.5f);
            }

            _perturb = new byte[_offsets.Length * 256];
            for (int i = 0; i < _offsets.Length; i++)
            {
                for (int v = 0; v < 256; v++)
                {
                    _perturb[(i * 256) + v] = Perturb(v + _offsets[i]);
                }
            }
        }
    }

    public override bool Cacheable => !Sequential;

    public override bool Streaming => !Sequential && base.Streaming;

    // Error diffusion, and any mode whose answer depends on the order colors are seen in, runs in one forward pass.
    private bool Sequential => _kernel != null || _coarse;

    private static bool HasTranslucentEntry(byte[] p)
    {
        for (int i = 3; i < p.Length; i += 4)
        {
            if (p[i] != 255)
            {
                return true;
            }
        }

        return false;
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        if (Sequential)
        {
            TileGrid grid = Volatile.Read(ref _grid) ?? Materialize(context);
            return grid.Tiles[(ty * grid.TilesX) + tx]!.Retain();
        }

        ImageRect r = TileRect(tx, ty);
        int w = r.Width;
        int h = r.Height;
        byte[] src = Pools<byte>.Pool.Rent(w * h * 4);
        try
        {
            Render.ReadRegion(Inputs[0], r, src, w * 4, PixelFormat.Rgba8, context);
            Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
            try
            {
                var pal = new ExactPalette(_index, coarse: false);
                int bpp = Format.BytesPerPixel;
                Span<byte> dst = tile.Span;
                ReadOnlySpan<uint> srcPx = SpanCast.As<uint>(src.AsSpan(0, w * h * 4));
                uint[] packed = _packed;
                byte[]? perturb = _perturb;
                for (int y = 0; y < h; y++)
                {
                    int o = y * Tile.Size * bpp;
                    ReadOnlySpan<uint> row = srcPx.Slice(y * w, w);
                    int mrow = perturb is null ? 0 : ((r.Y + y) % _matrixSize) * _matrixSize;
                    int mcol = perturb is null ? 0 : r.X % _matrixSize;
                    for (int x = 0; x < row.Length; x++)
                    {
                        uint key = row[x];
                        if (perturb != null)
                        {
                            // The amount added depends only on the matrix cell, so each cell has a 256-entry table of results.
                            ReadOnlySpan<byte> t = perturb.AsSpan((mrow + mcol) * 256, 256);
                            key = t[(int)(key & 0xFF)] | ((uint)t[(int)((key >> 8) & 0xFF)] << 8) | ((uint)t[(int)((key >> 16) & 0xFF)] << 16) | (key & 0xFF000000u);
                            if (++mcol == _matrixSize)
                            {
                                mcol = 0;
                            }
                        }

                        uint chosen = packed[pal.Nearest(key)];
                        if (bpp == 4)
                        {
                            dst[o] = (byte)chosen;
                            dst[o + 1] = (byte)(chosen >> 8);
                            dst[o + 2] = (byte)(chosen >> 16);
                            dst[o + 3] = (byte)(chosen >> 24);
                        }
                        else
                        {
                            dst[o] = (byte)chosen;
                            dst[o + 1] = (byte)(chosen >> 8);
                            dst[o + 2] = (byte)(chosen >> 16);
                        }

                        o += bpp;
                    }
                }

                return tile;
            }
            catch
            {
                tile.Release();
                throw;
            }
        }
        finally
        {
            Pools<byte>.Pool.Return(src);
        }
    }

    // The matrix is anchored at the image origin, so tiles agree with a whole-frame pass.
    private uint Perturbed(byte[] px, int p, int y, int x)
    {
        float f = _offsets![((y % _matrixSize) * _matrixSize) + (x % _matrixSize)];
        return (uint)Perturb(px[p] + f) | ((uint)Perturb(px[p + 1] + f) << 8) | ((uint)Perturb(px[p + 2] + f) << 16) | ((uint)px[p + 3] << 24);
    }

    // The sum is truncated toward zero after clamping, like a float-to-byte conversion.
    private static byte Perturb(float v) => (byte)Math.Clamp(v, 0f, 255f);

    private TileGrid Materialize(RenderContext context)
    {
        lock (_gate)
        {
            if (_grid != null)
            {
                return _grid;
            }

            Node input = Inputs[0];
            DiffusionKernel? k = _kernel;
            int w = Width;
            int h = Height;
            int rowBytes = checked(w * 4);
            int ahead = k?.RowsBelow ?? 0;
            int bandRows = Tile.Size;
            int outBpp = Format.BytesPerPixel;
            byte[] buf = Pools<byte>.Pool.Rent(checked(rowBytes * (bandRows + ahead)));
            byte[] outBand = Pools<byte>.Pool.Rent(checked(w * outBpp * bandRows));
            var grid = new TileGrid(w, h, Format, null);
            try
            {
                var pal = new ExactPalette(_index, _coarse);
                var taps = k?.Taps ?? [];
                int nt = taps.Length;
                var tdx = new int[nt];
                var tdy = new int[nt];
                var coef = k?.Coefficients ?? [];
                var coefV = new Vector128<float>[nt];
                var toff = new int[nt];
                for (int i = 0; i < nt; i++)
                {
                    tdx[i] = taps[i].Dx;
                    tdy[i] = taps[i].Dy;
                    coefV[i] = Vector128.Create(coef[i]);
                    toff[i] = (taps[i].Dy * w) + taps[i].Dx;
                }

                float scale = _scale;
                byte[] palette = _palette;
                // Palette entries as packed pixels and as unit-range floats, so the error of a pixel is one vector subtraction.
                int entries = palette.Length / 4;
                var palPacked = new uint[entries];
                var palUnit = new Vector128<float>[entries];
                for (int i = 0; i < entries; i++)
                {
                    palPacked[i] = BitConverter.ToUInt32(palette, i * 4);
                    palUnit[i] = DitherKernel.Unit(palPacked[i]);
                }

                                Vector128<float> vscale = Vector128.Create(scale);
                using var sink = new StripToTileBuffer(grid);
                Span<uint> px = SpanCast.As<uint>(buf.AsSpan(0, checked(rowBytes * (bandRows + ahead))));
                int baseRow = 0;
                int have = 0; // rows of buf that hold image rows baseRow..baseRow+have-1
                while (baseRow < h)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    int want = Math.Min(h - baseRow, bandRows + ahead);
                    if (have < want)
                    {
                        Render.ReadRegion(input, new ImageRect(0, baseRow + have, w, want - have), buf.AsSpan(have * rowBytes), rowBytes, PixelFormat.Rgba8, context);
                        have = want;
                    }

                    int rows = Math.Min(bandRows, h - baseRow);
                    int rowsLeft = h - baseRow;
                    for (int r = 0; r < rows; r++)
                    {
                        int o = r * w * outBpp;
                        Span<uint> cur = px.Slice(r * w, w);
                        bool rowInterior = rowsLeft - r > (k?.RowsBelow ?? 0);
                        for (int x = 0; x < w; x++)
                        {
                            uint pixel = cur[x];
                            uint key = _matrix != null ? Perturbed(buf, (r * rowBytes) + (x * 4), baseRow + r, x) : pixel;
                            int idx = pal.Nearest(key);
                            uint chosen = palPacked[idx];
                            outBand[o] = (byte)chosen;
                            outBand[o + 1] = (byte)(chosen >> 8);
                            outBand[o + 2] = (byte)(chosen >> 16);
                            if (outBpp == 4)
                            {
                                outBand[o + 3] = (byte)(chosen >> 24);
                            }

                            o += outBpp;
                            if (k == null)
                            {
                                continue;
                            }

                            // Error in unit floats; a neighbour is re-quantized to 8 bits after each addition.
                            Vector128<float> e = (DitherKernel.Unit(pixel) - palUnit[idx]) * vscale;
                            if (Vector128.EqualsAll(e, Vector128<float>.Zero))
                            {
                                continue;
                            }

                            if (rowInterior && x >= k.Left && x < w - k.Right)
                            {
                                // Away from the edges every tap lands inside the band, so the taps are plain offsets.
                                int at = (r * w) + x;
                                for (int i = 0; i < nt; i++)
                                {
                                    px[at + toff[i]] = DitherKernel.AddShare(px[at + toff[i]], e * coefV[i]);
                                }

                                continue;
                            }

                            for (int i = 0; i < nt; i++)
                            {
                                int xx = x + tdx[i];
                                int yy = r + tdy[i];
                                if ((uint)xx >= (uint)w || yy >= rowsLeft)
                                {
                                    continue;
                                }

                                int q = (yy * w) + xx;
                                px[q] = DitherKernel.AddShare(px[q], e * coefV[i]);
                            }
                        }
                    }

                    sink.WriteRows(baseRow, outBand.AsSpan(0, rows * w * outBpp), rows);

                    // Rows below the band already carry diffused error; they start the next band.
                    int keep = have - rows;
                    if (keep > 0)
                    {
                        Buffer.BlockCopy(buf, rows * rowBytes, buf, 0, keep * rowBytes);
                    }

                    have = keep;
                    baseRow += rows;
                }

                Volatile.Write(ref _grid, grid);
                return grid;
            }
            catch
            {
                foreach (Tile? t in grid.Tiles)
                {
                    t?.Release();
                }

                throw;
            }
            finally
            {
                Pools<byte>.Pool.Return(buf);
                Pools<byte>.Pool.Return(outBand);
            }
        }
    }
}
