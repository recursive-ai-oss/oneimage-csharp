using System.Runtime.ExceptionServices;

namespace OneImage;

/// <summary>
/// A node of the lazy graph: knows its size and format, its inputs, and how to compute one tile on demand.
/// Computed tiles go through the shared cache when <see cref="Cacheable"/>.
/// </summary>
internal abstract class Node
{
    private static long s_nextId;

    protected Node(int width, int height, PixelFormat format, OpDescriptor descriptor, params Node[] inputs)
    {
        ImageLimits.CheckEngine(width, height);
        Id = Interlocked.Increment(ref s_nextId);
        Width = width;
        Height = height;
        Format = format;
        Descriptor = descriptor;
        Inputs = inputs;
    }

    public long Id { get; }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public OpDescriptor Descriptor { get; }

    public Node[] Inputs { get; }

    public int TilesX => (Width + Tile.Size - 1) >> Tile.Shift;

    public int TilesY => (Height + Tile.Size - 1) >> Tile.Shift;

    /// <summary>Gets a value indicating whether computed tiles are kept in the shared cache. Nodes that own their storage say no.</summary>
    public virtual bool Cacheable => true;

    /// <summary>
    /// Gets a value indicating whether this node's pixels come from a streaming decode, directly or through its inputs. Streaming
    /// results use the small streaming cache and are rendered a couple of tile rows at a time so memory stays independent of image height.
    /// </summary>
    public virtual bool Streaming
    {
        get
        {
            foreach (Node input in Inputs)
            {
                if (input.Streaming)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The input region needed to compute <paramref name="output"/> (in this node's coordinates) from input <paramref name="inputIndex"/>. Defaults to the same region; ops with margins or offsets override it.</summary>
    public virtual ImageRect RequiredInput(int inputIndex, ImageRect output) => output;

    /// <summary>Gets the valid (inside the image) rectangle of a tile.</summary>
    public ImageRect TileRect(int tx, int ty)
    {
        int x = tx << Tile.Shift;
        int y = ty << Tile.Shift;
        return new ImageRect(x, y, Math.Min(Tile.Size, Width - x), Math.Min(Tile.Size, Height - y));
    }

    /// <summary>Returns a tile with one reference for the caller, who must release it.</summary>
    public Tile GetTile(int tx, int ty, RenderContext context)
    {
        if ((uint)tx >= (uint)TilesX || (uint)ty >= (uint)TilesY)
        {
            throw new ArgumentOutOfRangeException(nameof(tx), "Tile outside the image.");
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        if (!Cacheable)
        {
            return ComputeTile(tx, ty, context);
        }

        return context.CacheFor(this).GetOrCompute(Id, tx, ty, (Node: this, tx, ty, context), static s => s.Node.ComputeTile(s.tx, s.ty, s.context), context.CancellationToken);
    }

    /// <summary>Computes a tile. The result carries one reference owned by the caller. Must be deterministic.</summary>
    protected abstract Tile ComputeTile(int tx, int ty, RenderContext context);
}

internal static class Render
{
    /// <summary>
    /// Copies a rectangle of a node into <paramref name="destination"/>, converting rows to <paramref name="destinationFormat"/>.
    /// Tiles are computed in parallel (degree from the context) in batches of whole tile rows, then copied in order, so the
    /// result never depends on scheduling. Nested calls from inside a tile computation run serially.
    /// </summary>
    public static void ReadRegion(Node node, ImageRect region, Span<byte> destination, int destinationStride, PixelFormat destinationFormat, RenderContext context)
    {
        if (!region.IsInside(node.Width, node.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Region lies outside the image.");
        }

        if (region.IsEmpty)
        {
            return;
        }

        int dbpp = destinationFormat.BytesPerPixel;
        long rowBytes = (long)region.Width * dbpp;
        if (destinationStride == 0)
        {
            destinationStride = checked((int)rowBytes);
        }

        if (destinationStride < rowBytes)
        {
            throw new ArgumentException("Stride is smaller than a row.", nameof(destinationStride));
        }

        if (((long)(region.Height - 1) * destinationStride) + rowBytes > destination.Length)
        {
            throw new ArgumentException("Destination is too small for the region.", nameof(destination));
        }

        int sbpp = node.Format.BytesPerPixel;
        bool convert = node.Format != destinationFormat;
        ConvertRowKernel? converter = convert ? KernelRegistry.Get<ConvertRowKernel>("convert") : null;
        int tx0 = region.X >> Tile.Shift;
        int tx1 = (region.Right - 1) >> Tile.Shift;
        int ty0 = region.Y >> Tile.Shift;
        int ty1 = (region.Bottom - 1) >> Tile.Shift;
        int perRow = tx1 - tx0 + 1;
        int degree = context.Degree;
        int chunkRows = degree > 1 ? Math.Max(1, (2 * degree) / perRow) : 1;
        if (node.Streaming)
        {
            // A streaming source keeps a few tile rows; never ask for more rows at once than that window covers.
            chunkRows = Math.Min(chunkRows, 2);
        }

        var leases = new Tile?[perRow * chunkRows];
        try
        {
            for (int cy = ty0; cy <= ty1; cy += chunkRows)
            {
                int rows = Math.Min(chunkRows, ty1 - cy + 1);
                int count = rows * perRow;
                Fetch(node, leases, count, tx0, perRow, cy, context);
                for (int i = 0; i < count; i++)
                {
                    int tx = tx0 + (i % perRow);
                    int ty = cy + (i / perRow);
                    Tile tile = leases[i]!;
                    ImageRect part = region.Intersect(new ImageRect(tx << Tile.Shift, ty << Tile.Shift, Tile.Size, Tile.Size));
                    ReadOnlySpan<byte> src = tile.Span;
                    int tileX = part.X - (tx << Tile.Shift);
                    int tileY = part.Y - (ty << Tile.Shift);
                    for (int r = 0; r < part.Height; r++)
                    {
                        ReadOnlySpan<byte> srow = src.Slice((((tileY + r) << Tile.Shift) + tileX) * sbpp, part.Width * sbpp);
                        int doff = ((part.Y - region.Y + r) * destinationStride) + ((part.X - region.X) * dbpp);
                        Span<byte> drow = destination.Slice(doff, part.Width * dbpp);
                        if (converter == null)
                        {
                            srow.CopyTo(drow);
                        }
                        else
                        {
                            converter(srow, node.Format, drow, destinationFormat, part.Width);
                        }
                    }

                    tile.Release();
                    leases[i] = null;
                }
            }
        }
        finally
        {
            foreach (Tile? t in leases)
            {
                t?.Release();
            }
        }
    }

    private static void Fetch(Node node, Tile?[] leases, int count, int tx0, int perRow, int ty0, RenderContext context)
    {
        if (context.Degree <= 1 || count == 1)
        {
            for (int i = 0; i < count; i++)
            {
                leases[i] = node.GetTile(tx0 + (i % perRow), ty0 + (i / perRow), context);
            }

            return;
        }

        RenderContext nested = context.Nested;
        var options = new ParallelOptions { MaxDegreeOfParallelism = context.Degree, CancellationToken = context.CancellationToken };
        try
        {
            Parallel.For(0, count, options, i => leases[i] = node.GetTile(tx0 + (i % perRow), ty0 + (i / perRow), nested));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Surface the real error (format, limit, cancellation) rather than the wrapper.
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }
}
