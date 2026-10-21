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
        ImageRect o = TileRect(tx, ty);
        (int cs, int ce) = _wx.SourceRange(o.X, o.Right);
        (int rs, int re) = _wy.SourceRange(o.Y, o.Bottom);
        Node input = Inputs[0];
        int p = _layout.Stride;
        int srcX0 = cs * _fx;
        int srcX1 = Math.Min(ce * _fx, input.Width);
        int srcY0 = rs * _fy;
        int srcY1 = Math.Min(re * _fy, input.Height);
        int srcPix = srcX1 - srcX0;
        int redCols = ce - cs;
        int redRows = re - rs;
        int ibpp = input.Format.BytesPerPixel;
        int bandRows = Math.Min(Tile.Size, srcY1 - srcY0);
        int hstride = o.Width * p;

        byte[]? bytes = null;
        float[]? hbuf = null;
        float[]? rowF = null;
        float[]? red = null;
        float[]? acc = null;
        float[]? outF = null;
        Tile? tile = null;
        try
        {
            bytes = ArrayPool<byte>.Shared.Rent(bandRows * srcPix * ibpp);
            hbuf = ArrayPool<float>.Shared.Rent(redRows * hstride);
            rowF = ArrayPool<float>.Shared.Rent(srcPix * p);
            red = _fx > 1 ? ArrayPool<float>.Shared.Rent(redCols * p) : null;
            acc = _fy > 1 ? ArrayPool<float>.Shared.Rent(redCols * p) : null;
            outF = ArrayPool<float>.Shared.Rent(hstride);

            int j = 0;
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
                    ResampleKernel.UnpackRow(bytes.AsSpan(r * srcPix * ibpp, srcPix * ibpp), rowF, srcPix, _layout);
                    Span<float> cur = rowF.AsSpan(0, srcPix * p);
                    if (_fx > 1)
                    {
                        ResampleKernel.ReduceRow(cur, red, p, _fx, srcPix, redCols);
                        cur = red.AsSpan(0, redCols * p);
                    }

                    if (_fy == 1)
                    {
                        Horizontal(cur, cs, o, hbuf.AsSpan(j * hstride, hstride));
                        j++;
                        continue;
                    }

                    if (count == 0)
                    {
                        cur.CopyTo(acc);
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

            tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
            int obpp = Format.BytesPerPixel;
            ReadOnlySpan<float> rows = hbuf.AsSpan(0, redRows * hstride);
            for (int row = 0; row < o.Height; row++)
            {
                int oy = o.Y + row;
                ReadOnlySpan<float> w = _wy.Weights.AsSpan(_wy.Offset[oy], _wy.Count[oy]);
                ResampleKernel.VerticalFilter(rows, hstride, _wy.Start[oy] - rs, w, outF.AsSpan(0, hstride));
                ResampleKernel.PackRow(outF, tile.Span.Slice(row * Tile.Size * obpp, o.Width * obpp), o.Width, _layout);
            }

            Tile result = tile;
            tile = null;
            return result;
        }
        finally
        {
            tile?.Release();
            Return(bytes);
            Return(hbuf);
            Return(rowF);
            Return(red);
            Return(acc);
            Return(outF);
        }
    }

    private static void Return<T>(T[]? array)
    {
        if (array != null)
        {
            ArrayPool<T>.Shared.Return(array);
        }
    }

    private static string Name(ResamplingFilter f) => f switch
    {
        ResamplingFilter.Nearest => "nearest",
        ResamplingFilter.Box => "box",
        ResamplingFilter.Bilinear => "bilinear",
        ResamplingFilter.CatmullRom => "catmull-rom",
        ResamplingFilter.Mitchell => "mitchell",
        ResamplingFilter.Lanczos2 => "lanczos2",
        ResamplingFilter.Lanczos3 => "lanczos3",
        _ => "auto",
    };

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
