using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Resamples the input through an affine transform by inverse mapping. The forward transform maps source continuous coordinates
/// (pixel (i, j) covers [i, i+1) x [j, j+1)) to destination coordinates; the output covers the destination rectangle
/// (<c>originX</c>, <c>originY</c>, width, height). Each output tile reads the bounding box of the source taps of its pixels.
/// </summary>
internal sealed class AffineNode : Node
{
    private readonly ResampleLayout _layout;
    private readonly EdgeMode _edge;
    private readonly ResamplingFilter _filter;
    private readonly float[] _background;
    private readonly double _baseX;
    private readonly double _baseY;
    private readonly double _ia;
    private readonly double _ib;
    private readonly double _ic;
    private readonly double _id;

    public AffineNode(Node input, double a, double b, double c, double d, double tx, double ty, double originX, double originY, int width, int height, ResamplingFilter filter, EdgeMode edge, Rgba32 background, bool linear)
        : base(
            width,
            height,
            input.Format,
            new OpDescriptor(
                "affine",
                new OpParameter("a", a),
                new OpParameter("b", b),
                new OpParameter("c", c),
                new OpParameter("d", d),
                new OpParameter("tx", tx),
                new OpParameter("ty", ty),
                new OpParameter("origin_x", originX),
                new OpParameter("origin_y", originY),
                new OpParameter("width", (long)width),
                new OpParameter("height", (long)height),
                new OpParameter("filter", FilterFunctions.Name(filter)),
                new OpParameter("edge", edge.ToString().ToLowerInvariant()),
                new OpParameter("linear", linear),
                new OpParameter("red", (long)background.R),
                new OpParameter("green", (long)background.G),
                new OpParameter("blue", (long)background.B),
                new OpParameter("alpha", (long)background.A)),
            input)
    {
        double det = (a * d) - (b * c);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-12)
        {
            throw new ImageOperationException("The transform is singular or not finite.");
        }

        _layout = new ResampleLayout(input.Format, linear);
        _edge = edge;
        _filter = filter;
        _ia = d / det;
        _ib = -b / det;
        _ic = -c / det;
        _id = a / det;

        // Source index-space position of output pixel (x, y): inverse * (origin + (x, y) + 0.5 - t) - 0.5.
        double bx = originX + 0.5 - tx;
        double by = originY + 0.5 - ty;
        _baseX = (_ia * bx) + (_ib * by) - 0.5;
        _baseY = (_ic * bx) + (_id * by) - 0.5;
        foreach ((int x, int y) in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) })
        {
            (double px, double py) = Position(x, y);
            if (!double.IsFinite(px) || !double.IsFinite(py) || Math.Abs(px) > 1e9 || Math.Abs(py) > 1e9)
            {
                throw new ImageOperationException("The transform reaches positions too far from the image.");
            }
        }

        byte[] pixel = ExtendNode.BackgroundPixel(input.Format, background);
        float[] tmp = new float[_layout.Stride];
        ResampleKernel.UnpackRow(pixel, tmp, 1, _layout);
        _background = tmp;
    }

    // Written exactly like the sampler's per-pixel expression so corner bounds and pixel positions agree bit for bit.
    private (double X, double Y) Position(int x, int y) => (_baseX + (y * _ib) + (x * _ia), _baseY + (y * _id) + (x * _ic));

    internal static (int Lo, int Hi) Tap(double minPos, double maxPos, int support) =>
        support == 0
            ? ((int)Math.Floor(minPos + 0.5), (int)Math.Floor(maxPos + 0.5))
            : ((int)Math.Floor(minPos) - support + 1, (int)Math.Floor(maxPos) + support);

    public override ImageRect RequiredInput(int inputIndex, ImageRect output)
    {
        Node input = Inputs[0];
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        foreach ((int x, int y) in new[] { (output.X, output.Y), (output.Right - 1, output.Y), (output.X, output.Bottom - 1), (output.Right - 1, output.Bottom - 1) })
        {
            (double px, double py) = Position(x, y);
            minX = Math.Min(minX, px);
            maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py);
            maxY = Math.Max(maxY, py);
        }

        int support = AffineFilterTable.Support(_filter);
        (int x0, int x1) = Hull(_edge, Tap(minX, maxX, support), input.Width);
        (int y0, int y1) = Hull(_edge, Tap(minY, maxY, support), input.Height);
        return x0 > x1 || y0 > y1 ? new ImageRect(0, 0, 0, 0) : new ImageRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    // Input indices that taps in [range.Lo, range.Hi] resolve to, as an inclusive range (empty when Lo > Hi).
    internal static (int Lo, int Hi) Hull(EdgeMode edge, (int Lo, int Hi) range, int n)
    {
        (int lo, int hi) = range;
        switch (edge)
        {
            case EdgeMode.Constant:
                return (Math.Max(lo, 0), Math.Min(hi, n - 1));
            case EdgeMode.Clamp:
                return (AffineKernel.MapIndex(lo, n, edge), AffineKernel.MapIndex(hi, n, edge));
            default:
                if (lo >= 0 && hi < n)
                {
                    return (lo, hi);
                }

                if ((long)hi - lo + 1 >= n)
                {
                    return (0, n - 1);
                }

                int a = int.MaxValue;
                int b = -1;
                for (int i = lo; i <= hi; i++)
                {
                    int m = AffineKernel.MapIndex(i, n, edge);
                    a = Math.Min(a, m);
                    b = Math.Max(b, m);
                }

                return (a, b);
        }
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        ImageRect need = RequiredInput(0, o);
        Node input = Inputs[0];
        int p = _layout.Stride;
        int ibpp = input.Format.BytesPerPixel;
        int obpp = Format.BytesPerPixel;
        int bandRows = Math.Min(Tile.Size, Math.Max(need.Height, 1));
        float[] region = Pools<float>.Pool.Rent(Math.Max(1, need.Width * need.Height * p));
        byte[] band = Pools<byte>.Pool.Rent(Math.Max(1, bandRows * need.Width * ibpp));
        float[] row = Pools<float>.Pool.Rent(o.Width * p);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            // Pull the source rectangle in bands of rows and convert it to premultiplied (linear) floats.
            for (int y = 0, n; y < need.Height; y += n)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                // Bands stop at tile-row boundaries so no source tile is fetched twice.
                n = Math.Min(Tile.Size - ((need.Y + y) & (Tile.Size - 1)), need.Height - y);
                Render.ReadRegion(input, new ImageRect(need.X, need.Y + y, need.Width, n), band, need.Width * ibpp, input.Format, context);
                for (int r = 0; r < n; r++)
                {
                    ResampleKernel.UnpackRow(
                        band.AsSpan(r * need.Width * ibpp, need.Width * ibpp),
                        region.AsSpan((y + r) * need.Width * p, need.Width * p),
                        need.Width,
                        _layout);
                }
            }

            ReadOnlySpan<float> rgn = region.AsSpan(0, need.Width * need.Height * p);
            for (int r = 0; r < o.Height; r++)
            {
                int y = o.Y + r;
                AffineKernel.SampleRow(
                    rgn,
                    need.Width,
                    need.Height,
                    need.X,
                    need.Y,
                    input.Width,
                    input.Height,
                    p,
                    _edge,
                    _background,
                    _filter,
                    _baseX + (y * _ib),
                    _baseY + (y * _id),
                    _ia,
                    _ic,
                    o.X,
                    o.Width,
                    row);
                ResampleKernel.PackRow(row, tile.Span.Slice(r * Tile.Size * obpp, o.Width * obpp), o.Width, _layout);
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
            Pools<float>.Pool.Return(region);
            Pools<byte>.Pool.Return(band);
            Pools<float>.Pool.Return(row);
        }
    }
}
