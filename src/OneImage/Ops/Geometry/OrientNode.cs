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

    private readonly int _orientation;

    private OrientNode(Node input, int orientation)
        : base(
            orientation >= 5 ? input.Height : input.Width,
            orientation >= 5 ? input.Width : input.Height,
            input.Format,
            new OpDescriptor("orient", new OpParameter("orientation", (long)orientation)),
            input)
    {
        _orientation = orientation;
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

    /// <summary>Gets the EXIF-style orientation number (2 to 8) this node applies.</summary>
    public int Orientation => _orientation;

    /// <summary>The same orientation applied to another input (used to move decoder-side work below this node).</summary>
    public Node WithInput(Node input) => Create(input, _orientation);

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
        const int Mask = Tile.Size - 1;

        // Gather straight from the input tiles (at most 2 x 2 for a 128-pixel output tile) instead of copying the region first.
        int tx0 = need.X >> Tile.Shift;
        int ty0 = need.Y >> Tile.Shift;
        int nx = ((need.Right - 1) >> Tile.Shift) - tx0 + 1;
        int ny = ((need.Bottom - 1) >> Tile.Shift) - ty0 + 1;
        var leases = new Tile?[nx * ny];
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            for (int i = 0; i < leases.Length; i++)
            {
                leases[i] = Inputs[0].GetTile(tx0 + (i % nx), ty0 + (i / nx), context);
            }

            (int dx, int dy) = o.Width > 1 ? StepOf(o) : (1, 0);
            long step = ((long)dx * bpp) + ((long)dy * Tile.Size * bpp);
            for (int row = 0; row < o.Height; row++)
            {
                (int sx, int sy) = Source(o.X, o.Y + row);
                Span<byte> dst = tile.Span.Slice(row * Tile.Size * bpp, o.Width * bpp);
                for (int i = 0; i < o.Width;)
                {
                    int cx = sx + (i * dx);
                    int cy = sy + (i * dy);
                    Tile src = leases[(((cy >> Tile.Shift) - ty0) * nx) + ((cx >> Tile.Shift) - tx0)]!;

                    // The run of output pixels that stay inside this input tile.
                    int run = o.Width - i;
                    if (dx != 0)
                    {
                        run = Math.Min(run, dx > 0 ? Tile.Size - (cx & Mask) : (cx & Mask) + 1);
                    }
                    else
                    {
                        run = Math.Min(run, dy > 0 ? Tile.Size - (cy & Mask) : (cy & Mask) + 1);
                    }

                    long first = ((((long)(cy & Mask) * Tile.Size) + (cx & Mask))) * bpp;
                    GeometryKernel.GatherRow(src.Span, first, step, dst.Slice(i * bpp, run * bpp), run, bpp);
                    i += run;
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
            foreach (Tile? t in leases)
            {
                t?.Release();
            }
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
