using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// Exact rotations and flips, named by the EXIF orientation that undoes them (1 = identity, 2 = mirror horizontally, 3 = rotate 180,
/// 4 = mirror vertically, 5 = transpose, 6 = rotate 90 clockwise, 7 = transverse, 8 = rotate 270 clockwise). Pixels move, nothing is
/// resampled. Each output tile reads one rectangle of the input (the bounding box of the tile's source pixels) and gathers from it.
/// </summary>
internal sealed class OrientNode : Node
{
    private readonly bool _transpose;
    private readonly bool _flipU;
    private readonly bool _flipV;

    private OrientNode(Node input, int orientation)
        : base(
            orientation >= 5 ? input.Height : input.Width,
            orientation >= 5 ? input.Width : input.Height,
            input.Format,
            new OpDescriptor("orient", new OpParameter("orientation", (long)orientation)),
            input)
    {
        _transpose = orientation >= 5;
        (_flipU, _flipV) = orientation switch
        {
            2 or 6 => (true, false),
            3 or 7 => (true, true),
            4 or 8 => (false, true),
            _ => (false, false),
        };
    }

    /// <summary>Builds the node, or returns the input itself for orientation 1.</summary>
    public static Node Create(Node input, int orientation)
    {
        if (orientation is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(orientation), "Orientation must be 1 to 8.");
        }

        return orientation == 1 ? input : new OrientNode(input, orientation);
    }

    // The input pixel shown at output (x, y). Output coordinates are flipped first, then swapped if the orientation transposes.
    private (int X, int Y) Source(int x, int y)
    {
        int u = _flipU ? Width - 1 - x : x;
        int v = _flipV ? Height - 1 - y : y;
        return _transpose ? (v, u) : (u, v);
    }

    public override ImageRect RequiredInput(int inputIndex, ImageRect output)
    {
        (int ax, int ay) = Source(output.X, output.Y);
        (int bx, int by) = Source(output.Right - 1, output.Bottom - 1);
        int x0 = Math.Min(ax, bx);
        int y0 = Math.Min(ay, by);
        return new ImageRect(x0, y0, Math.Max(ax, bx) - x0 + 1, Math.Max(ay, by) - y0 + 1);
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        ImageRect o = TileRect(tx, ty);
        ImageRect need = RequiredInput(0, o);
        int bpp = Format.BytesPerPixel;
        int stride = need.Width * bpp;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(stride * need.Height);
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(Inputs[0], need, scratch, stride, Format, context);
            ReadOnlySpan<byte> src = scratch.AsSpan(0, stride * need.Height);
            (int dx, int dy) = o.Width > 1 ? StepOf(o) : (1, 0);
            long step = ((long)dx * bpp) + ((long)dy * stride);
            for (int row = 0; row < o.Height; row++)
            {
                (int sx, int sy) = Source(o.X, o.Y + row);
                long first = ((long)(sy - need.Y) * stride) + ((long)(sx - need.X) * bpp);
                GeometryKernel.GatherRow(src, first, step, tile.Span.Slice(row * Tile.Size * bpp, o.Width * bpp), o.Width, bpp);
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
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    // How the source position moves when the output column advances by one.
    private (int Dx, int Dy) StepOf(ImageRect o)
    {
        (int ax, int ay) = Source(o.X, o.Y);
        (int bx, int by) = Source(o.X + 1, o.Y);
        return (bx - ax, by - ay);
    }
}
