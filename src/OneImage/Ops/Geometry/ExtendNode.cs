using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Grows the canvas around the input: the input sits at (Left, Top) and every other pixel is the background (constant mode) or
/// is read from the input through the edge mode. An output tile reads one input rectangle: the bounding box of the pixels it
/// maps to (for mirror and wrap this is the whole axis when the tile straddles a seam).
/// </summary>
internal sealed class ExtendNode : Node
{
    private readonly int _left;
    private readonly int _top;
    private readonly EdgeMode _mode;
    private readonly byte[] _background;

    public ExtendNode(Node input, int left, int top, int width, int height, EdgeMode mode, Rgba32 background)
        : base(
            width,
            height,
            input.Format,
            new OpDescriptor(
                "extend",
                new OpParameter("left", (long)left),
                new OpParameter("top", (long)top),
                new OpParameter("width", (long)width),
                new OpParameter("height", (long)height),
                new OpParameter("mode", mode.ToString().ToLowerInvariant()),
                new OpParameter("red", (long)background.R),
                new OpParameter("green", (long)background.G),
                new OpParameter("blue", (long)background.B),
                new OpParameter("alpha", (long)background.A)),
            input)
    {
        _left = left;
        _top = top;
        _mode = mode;
        _background = BackgroundPixel(input.Format, background);
    }

    /// <summary>The background as one pixel of <paramref name="format"/>: color converted without flattening; zero for CMYK and Lab.</summary>
    public static byte[] BackgroundPixel(PixelFormat format, Rgba32 background)
    {
        byte[] pixel = new byte[format.BytesPerPixel];
        if (format.Model is ColorModel.Rgb or ColorModel.Gray)
        {
            ConvertKernel.ConvertRow([background.R, background.G, background.B, background.A], PixelFormat.Rgba8, pixel, format, 1);
        }

        return pixel;
    }

    // Mapped input index for output index i along an axis, or -1 for the background.
    private int MapX(int x) => AffineKernel.MapIndex(x - _left, Inputs[0].Width, _mode);

    private int MapY(int y) => AffineKernel.MapIndex(y - _top, Inputs[0].Height, _mode);

    public override ImageRect RequiredInput(int inputIndex, ImageRect output)
    {
        (int x0, int x1) = Hull(output.X, output.Right, _left, Inputs[0].Width);
        (int y0, int y1) = Hull(output.Y, output.Bottom, _top, Inputs[0].Height);
        return x0 > x1 || y0 > y1 ? new ImageRect(0, 0, 0, 0) : new ImageRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    // Smallest [lo, hi] of input indices that output indices [from, to) map to; lo > hi when all of them are background.
    private (int Lo, int Hi) Hull(int from, int to, int offset, int n)
    {
        int lo = int.MaxValue;
        int hi = -1;
        if (_mode is EdgeMode.Constant or EdgeMode.Clamp)
        {
            // Both are monotone, so the ends decide.
            foreach (int i in (ReadOnlySpan<int>)[from, to - 1])
            {
                int m = AffineKernel.MapIndex(i - offset, n, _mode);
                if (m >= 0)
                {
                    lo = Math.Min(lo, m);
                    hi = Math.Max(hi, m);
                }
            }

            if (_mode == EdgeMode.Constant)
            {
                // The interior of a range that spans the whole image is inside even when both ends are outside.
                int a = Math.Max(from - offset, 0);
                int b = Math.Min(to - 1 - offset, n - 1);
                if (a <= b)
                {
                    lo = Math.Min(lo, a);
                    hi = Math.Max(hi, b);
                }
            }

            return (lo, hi);
        }

        if (to - from >= n)
        {
            return (0, n - 1);
        }

        for (int i = from; i < to; i++)
        {
            int m = AffineKernel.MapIndex(i - offset, n, _mode);
            lo = Math.Min(lo, m);
            hi = Math.Max(hi, m);
        }

        return (lo, hi);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        ImageRect need = RequiredInput(0, o);
        int bpp = Format.BytesPerPixel;
        int stride = need.Width * bpp;
        int[] cols = Pools<int>.Pool.Rent(o.Width);
        byte[]? scratch = need.IsEmpty ? null : Pools<byte>.Pool.Rent(stride * need.Height);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            if (scratch != null)
            {
                Render.ReadRegion(Inputs[0], need, scratch, stride, Format, context);
            }

            for (int x = 0; x < o.Width; x++)
            {
                cols[x] = MapX(o.X + x);
            }

            for (int row = 0; row < o.Height; row++)
            {
                Span<byte> dst = tile.Span.Slice(row * Tile.Size * bpp, o.Width * bpp);
                int sy = MapY(o.Y + row);
                if (sy < 0)
                {
                    GeometryKernel.FillRow(dst, o.Width, _background);
                    continue;
                }

                ReadOnlySpan<byte> srcRow = scratch.AsSpan((sy - need.Y) * stride, stride);
                int x = 0;
                while (x < o.Width)
                {
                    int c = cols[x];
                    int run = 1;
                    if (c < 0)
                    {
                        while (x + run < o.Width && cols[x + run] < 0)
                        {
                            run++;
                        }

                        GeometryKernel.FillRow(dst.Slice(x * bpp, run * bpp), run, _background);
                    }
                    else
                    {
                        // Consecutive input pixels move as one block; reversed stretches (mirror) go pixel by pixel.
                        while (x + run < o.Width && cols[x + run] == c + run)
                        {
                            run++;
                        }

                        srcRow.Slice((c - need.X) * bpp, run * bpp).CopyTo(dst.Slice(x * bpp, run * bpp));
                    }

                    x += run;
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
            Pools<int>.Pool.Return(cols);
            if (scratch != null)
            {
                Pools<byte>.Pool.Return(scratch);
            }
        }
    }
}
