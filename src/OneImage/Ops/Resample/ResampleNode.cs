using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Separable resampling of the whole input to a new size. Each output tile pulls only the input rectangle its filter taps
/// reach (<see cref="RequiredInput"/>), converts it to premultiplied float rows (linear light unless disabled), optionally
/// box-reduces by an integer factor per axis, filters horizontally into an intermediate block, then filters vertically
/// and packs the result into the tile. Tiles are independent, so results do not depend on scheduling.
/// </summary>
internal sealed class ResampleNode : Node
{
    /// <summary>Axis ratios from this value up are first box-reduced by an integer factor (see <see cref="ReduceFactor"/>).</summary>
    public const double ReduceThreshold = 4;

    private readonly ResampleLayout _layout;
    private readonly WeightTable _wx;
    private readonly WeightTable _wy;
    private readonly int _fx;
    private readonly int _fy;

    // State of the streaming path (see StreamTile); all of it is guarded by _streamGate.
    private readonly object _streamGate = new();
    private readonly Dictionary<int, Tile?[]> _stash = [];
    private float[] _carry = [];
    private int _carryStart;
    private int _carryRows;
    private int _carryWidth;
    private int _lastRow = -1;

    private ResampleNode(Node input, int width, int height, ResamplingFilter filterX, ResamplingFilter filterY, bool linear, int fx, int fy)
        : base(
            width,
            height,
            input.Format,
            new OpDescriptor(
                "resample",
                new OpParameter("width", (long)width),
                new OpParameter("height", (long)height),
                new OpParameter("filter_x", Name(filterX)),
                new OpParameter("filter_y", Name(filterY)),
                new OpParameter("linear", linear),
                new OpParameter("reduce_x", (long)fx),
                new OpParameter("reduce_y", (long)fy)),
            input)
    {
        _layout = new ResampleLayout(input.Format, linear);
        _fx = fx;
        _fy = fy;
        _wx = WeightTable.Build((input.Width + fx - 1) / fx, width, (double)input.Width / width / fx, filterX);
        _wy = WeightTable.Build((input.Height + fy - 1) / fy, height, (double)input.Height / height / fy, filterY);
    }

    /// <summary>Builds the node, resolving <see cref="ResamplingFilter.Auto"/> per axis and choosing the box-reduction factors.</summary>
    public static ResampleNode Create(Node input, int width, int height, ResamplingFilter filter, bool linear)
    {
        ResamplingFilter fx = Resolve(filter, input.Width, width);
        ResamplingFilter fy = Resolve(filter, input.Height, height);
        return new ResampleNode(input, width, height, fx, fy, linear, fx == ResamplingFilter.Nearest ? 1 : ReduceFactor(input.Width, width), fy == ResamplingFilter.Nearest ? 1 : ReduceFactor(input.Height, height));
    }

    /// <summary>Lanczos3 for shrinking axes, Catmull-Rom for growing or unchanged ones.</summary>
    public static ResamplingFilter Resolve(ResamplingFilter filter, int sourceSize, int outputSize) =>
        filter != ResamplingFilter.Auto ? filter : outputSize < sourceSize ? ResamplingFilter.Lanczos3 : ResamplingFilter.CatmullRom;

    /// <summary>
    /// Integer box-reduction applied before the final filter: 1 below a ratio of 4, otherwise <c>floor(ratio / 2)</c>,
    /// which leaves a residual ratio in [2, 3) for the filter. The cost of the final filter then stays bounded.
    /// </summary>
    public static int ReduceFactor(int sourceSize, int outputSize)
    {
        double ratio = (double)sourceSize / outputSize;
        return ratio >= ReduceThreshold ? (int)(ratio / 2) : 1;
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output)
    {
        (int cs, int ce) = _wx.SourceRange(output.X, output.Right);
        (int rs, int re) = _wy.SourceRange(output.Y, output.Bottom);
        Node input = Inputs[0];
        int x0 = cs * _fx;
        int y0 = rs * _fy;
        return new ImageRect(x0, y0, Math.Min(ce * _fx, input.Width) - x0, Math.Min(re * _fy, input.Height) - y0);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        if (!Inputs[0].Streaming)
        {
            return ComputeBlock(tx, tx + 1, ty, stream: false, context)[0];
        }

        return StreamTile(tx, ty, context);
    }

    /// <summary>
    /// A streaming input can only be read forward, a few tile rows deep. So one thread at a time computes a whole output tile row
    /// (all columns) with a single forward pass over the input, keeps the finished tiles for the other columns' requests, and
    /// carries the horizontally filtered rows the next output row still needs, so input rows are never read twice.
    /// A row requested right after the next expected one first computes the expected one, because the scheduler may ask for two
    /// rows at once in either order.
    /// </summary>
    private Tile StreamTile(int tx, int ty, RenderContext context)
    {
        lock (_streamGate)
        {
            if (TakeStashed(tx, ty) is { } ready)
            {
                return ready;
            }

            if (ty == _lastRow + 2 || (_lastRow < 0 && ty == 1))
            {
                ComputeRowIntoStash(_lastRow + 1, context);
            }

            ComputeRowIntoStash(ty, context);
            return TakeStashed(tx, ty) ?? throw new ImageOperationException("Resample row was not produced.");
        }
    }

    private Tile? TakeStashed(int tx, int ty)
    {
        if (_stash.TryGetValue(ty, out Tile?[]? row) && row[tx] is { } tile)
        {
            row[tx] = null;
            return tile;
        }

        return null;
    }

    private void ComputeRowIntoStash(int ty, RenderContext context)
    {
        if (_stash.Remove(ty, out Tile?[]? stale))
        {
            ReleaseAll(stale);
        }

        Tile[] row = ComputeBlock(0, TilesX, ty, stream: true, context);
        _stash[ty] = row;
        _lastRow = ty;
        foreach (int old in _stash.Keys.Where(k => k < ty - 1).ToArray())
        {
            _stash.Remove(old, out Tile?[]? gone);
            ReleaseAll(gone!);
        }
    }

    private static void ReleaseAll(Tile?[] tiles)
    {
        foreach (Tile? t in tiles)
        {
            t?.Release();
        }
    }

    /// <summary>Computes output tiles [tx0, tx1) of row <paramref name="ty"/> together.</summary>
    private Tile[] ComputeBlock(int tx0, int tx1, int ty, bool stream, RenderContext context)
    {
        int x0 = tx0 << Tile.Shift;
        int y0 = ty << Tile.Shift;
        var o = new ImageRect(x0, y0, Math.Min(Width, tx1 << Tile.Shift) - x0, Math.Min(Height, (ty + 1) << Tile.Shift) - y0);
        (int cs, int ce) = _wx.SourceRange(o.X, o.Right);
        (int rs, int re) = _wy.SourceRange(o.Y, o.Bottom);
        Node input = Inputs[0];
        int p = _layout.Stride;
        int srcX0 = cs * _fx;
        int srcX1 = Math.Min(ce * _fx, input.Width);
        int srcY1 = Math.Min(re * _fy, input.Height);
        int srcPix = srcX1 - srcX0;
        int redCols = ce - cs;
        int redRows = re - rs;
        int ibpp = input.Format.BytesPerPixel;
        int hstride = o.Width * p;

        // Reduced rows the previous streamed row left behind are reused instead of reading the input again.
        int have = 0;
        if (stream && _carryRows > 0 && _carryStart <= rs && rs < _carryStart + _carryRows && _carryWidth == o.Width)
        {
            have = Math.Min(_carryStart + _carryRows, re) - rs;
        }

        int srcY0 = (rs + have) * _fy;
        int bandRows = Math.Max(1, Math.Min(Tile.Size, srcY1 - srcY0));

        byte[]? bytes = null;
        float[]? hbuf = null;
        float[]? rowF = null;
        float[]? red = null;
        float[]? acc = null;
        float[]? outF = null;
        var tiles = new Tile?[tx1 - tx0];
        try
        {
            hbuf = FloatScratch.Rent(redRows * hstride);
            if (have > 0)
            {
                _carry.AsSpan((rs - _carryStart) * hstride, have * hstride).CopyTo(hbuf);
            }

            int j = have;
            if (srcY0 < srcY1)
            {
                bytes = PooledTileStore.Instance.Rent(bandRows * srcPix * ibpp, clear: false);
                bool fuse = ResampleKernel.CanFuseReduce(_layout);
                rowF = fuse && _fx > 1 ? null : FloatScratch.Rent(srcPix * p);
                red = _fx > 1 ? FloatScratch.Rent(redCols * p) : null;
                acc = _fy > 1 ? FloatScratch.Rent(redCols * p) : null;
                int count = 0;
                for (int y = srcY0, n; y < srcY1; y += n)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    // Bands stop at tile-row boundaries so every input tile is fetched once per output tile.
                    n = Math.Min(Tile.Size - (y & (Tile.Size - 1)), srcY1 - y);
                    Render.ReadRegion(input, new ImageRect(srcX0, y, srcPix, n), bytes, srcPix * ibpp, input.Format, context);
                    for (int r = 0; r < n; r++)
                    {
                        int yy = y + r;
                        ReadOnlySpan<byte> srcRow = bytes.AsSpan(r * srcPix * ibpp, srcPix * ibpp);
                        Span<float> cur;
                        if (_fx > 1 && fuse)
                        {
                            ResampleKernel.UnpackReduceRow(srcRow, red!, srcPix, _fx, redCols, _layout);
                            cur = red.AsSpan(0, redCols * p);
                        }
                        else
                        {
                            ResampleKernel.UnpackRow(srcRow, rowF!, srcPix, _layout);
                            cur = rowF!.AsSpan(0, srcPix * p);
                            if (_fx > 1)
                            {
                                ResampleKernel.ReduceRow(cur, red!, p, _fx, srcPix, redCols);
                                cur = red.AsSpan(0, redCols * p);
                            }
                        }

                        if (_fy == 1)
                        {
                            Horizontal(cur, cs, o, hbuf.AsSpan(j * hstride, hstride));
                            j++;
                            continue;
                        }

                        if (count == 0)
                        {
                            cur.CopyTo(acc!);
                        }
                        else
                        {
                            ResampleKernel.AddRow(cur, acc.AsSpan(0, redCols * p));
                        }

                        count++;
                        if (((yy + 1) % _fy) == 0 || yy == input.Height - 1)
                        {
                            Span<float> block = acc.AsSpan(0, redCols * p);
                            ResampleKernel.ScaleRow(block, 1f / count);
                            Horizontal(block, cs, o, hbuf.AsSpan(j * hstride, hstride));
                            j++;
                            count = 0;
                        }
                    }
                }
            }

            outF = FloatScratch.Rent(hstride);
            int obpp = Format.BytesPerPixel;
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i] = Tile.Rent(Tile.ByteSize(Format), clear: false);
            }

            ReadOnlySpan<float> rows = hbuf.AsSpan(0, redRows * hstride);
            for (int row = 0; row < o.Height; row++)
            {
                int oy = o.Y + row;
                ReadOnlySpan<float> w = _wy.Weights.AsSpan(_wy.Offset[oy], _wy.Count[oy]);
                ResampleKernel.VerticalFilter(rows, hstride, _wy.Start[oy] - rs, w, outF.AsSpan(0, hstride));
                for (int i = 0; i < tiles.Length; i++)
                {
                    int tw = Math.Min(Tile.Size, o.Width - (i << Tile.Shift));
                    ResampleKernel.PackRow(outF.AsSpan(i * Tile.Size * p, tw * p), tiles[i]!.Span.Slice(row * Tile.Size * obpp, tw * obpp), tw, _layout);
                }
            }

            if (stream)
            {
                SaveCarry(hbuf, hstride, ty, rs, re, o.Width);
            }

            var result = new Tile[tiles.Length];
            for (int i = 0; i < tiles.Length; i++)
            {
                result[i] = tiles[i]!;
                tiles[i] = null;
            }

            return result;
        }
        finally
        {
            ReleaseAll(tiles);
            Return(bytes);
            Return(hbuf);
            Return(rowF);
            Return(red);
            Return(acc);
            Return(outF);
        }
    }

    // Keeps the reduced rows the next output tile row will start with.
    private void SaveCarry(float[] hbuf, int hstride, int ty, int rs, int re, int width)
    {
        int nextY = (ty + 1) << Tile.Shift;
        _carryRows = 0;
        if (nextY >= Height)
        {
            return;
        }

        (int nextFirst, _) = _wy.SourceRange(nextY, Math.Min(Height, nextY + Tile.Size));
        if (nextFirst >= re || nextFirst < rs)
        {
            return;
        }

        int rows = re - nextFirst;
        if (_carry.Length < rows * hstride)
        {
            _carry = new float[rows * hstride];
        }

        hbuf.AsSpan((nextFirst - rs) * hstride, rows * hstride).CopyTo(_carry);
        _carryStart = nextFirst;
        _carryRows = rows;
        _carryWidth = width;
    }

    private static void Return(float[]? array) => FloatScratch.Return(array);

    private static void Return(byte[]? array)
    {
        if (array != null)
        {
            PooledTileStore.Instance.Return(array);
        }
    }

    private static string Name(ResamplingFilter f) => FilterFunctions.Name(f);

    private void Horizontal(ReadOnlySpan<float> row, int firstCol, ImageRect o, Span<float> dst) =>
        ResampleKernel.HorizontalFilter(
            row,
            firstCol,
            dst,
            _layout.Stride,
            _wx.Start.AsSpan(o.X, o.Width),
            _wx.Count.AsSpan(o.X, o.Width),
            _wx.Offset.AsSpan(o.X, o.Width),
            _wx.Weights);
}
