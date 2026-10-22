using OneImage.Kernels;
using System.Runtime.ExceptionServices;

namespace OneImage;

/// <summary>
/// A node of the lazy graph: knows its size and format, its inputs, and how to compute one tile on demand.
/// Computed tiles go through the shared cache when <see cref="Cacheable"/>.
/// </summary>
internal abstract class Node
{
    private static long s_nextId;
    private CacheReaper? _reaper;

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

    /// <summary>Gets a value indicating whether a decoder with native blocks larger than a tile feeds this node, so renders should <see cref="Prefetch"/> first.</summary>
    public virtual bool HasBlockedSource
    {
        get
        {
            foreach (Node input in Inputs)
            {
                if (input.HasBlockedSource)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Hint before a large render: lets sources decode their native blocks for <paramref name="region"/> (this node's
    /// coordinates) in parallel, ahead of the per-tile requests. Never changes results.
    /// </summary>
    public virtual void Prefetch(ImageRect region, RenderContext context)
    {
        for (int i = 0; i < Inputs.Length; i++)
        {
            ImageRect need = RequiredInput(i, region).Intersect(new ImageRect(0, 0, Inputs[i].Width, Inputs[i].Height));
            if (!need.IsEmpty)
            {
                Inputs[i].Prefetch(need, context);
            }
        }
    }

    /// <summary>Gets a value indicating whether getting a tile is just a lookup of stored pixels (no computation), so small copies are not worth parallelizing.</summary>
    public virtual bool DirectStorage => false;

    /// <summary>
    /// Gets a value indicating whether this node's cached or materialized tiles belong to the pipeline it is part of, so a finished
    /// one-shot save may give them back. False for nodes another pipeline can share, such as a full-size decode.
    /// </summary>
    protected virtual bool PipelinePrivate => true;

    /// <summary>Gives back memory only this pipeline could have wanted again (materialized tiles). Everything is recomputed if the image is used again.</summary>
    protected virtual void ReleaseMaterialized()
    {
    }

    /// <summary>
    /// Called when a save has consumed an image's graph: drops the cached tiles of the pipeline's own nodes and releases memory they
    /// hold, instead of leaving it for the garbage collector to find. A caller that saves the same image again just recomputes.
    /// </summary>
    public static void ReleaseAfterSave(IEnumerable<Node> roots)
    {
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var ids = new HashSet<long>();
        var stack = new Stack<Node>(roots);
        while (stack.TryPop(out Node? n))
        {
            if (!seen.Add(n))
            {
                continue;
            }

            if (n.PipelinePrivate)
            {
                ids.Add(n.Id);
                n.ReleaseMaterialized();
            }

            foreach (Node input in n.Inputs)
            {
                stack.Push(input);
            }
        }

        if (ids.Count > 0)
        {
            TileCache.Global.RemoveNodes(-1, ids);
            TileCache.Streaming.RemoveNodes(-1, ids);
        }
    }

    /// <summary>Returns a tile with one reference for the caller, who must release it.</summary>
    public Tile GetTile(int tx, int ty, RenderContext context) => GetTile(tx, ty, context, useCache: true);

    /// <summary>
    /// Returns a tile with one reference for the caller. With <paramref name="useCache"/> false a computed tile is not stored in
    /// the shared cache (used for tiles that are consumed exactly once, such as every tile of a whole-image render).
    /// </summary>
    public Tile GetTile(int tx, int ty, RenderContext context, bool useCache)
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

        if (!useCache)
        {
            // Reuse a tile that is already cached, but do not add new ones.
            Tile? hit = context.CacheFor(this).TryGet(Id, tx, ty);
            return hit ?? ComputeTile(tx, ty, context);
        }

        TileCache cache = context.CacheFor(this);
        if (_reaper == null)
        {
            // Created on first use only: cached tiles are keyed by node id and hold no reference to the node, so without this
            // they would outlive a dropped image until budget pressure evicts them.
            Interlocked.CompareExchange(ref _reaper, new CacheReaper(Id, cache), null);
        }

        return cache.GetOrCompute(Id, tx, ty, (Node: this, tx, ty, context), static s => s.Node.ComputeTile(s.tx, s.ty, s.context), context.CancellationToken);
    }

    /// <summary>Computes a tile. The result carries one reference owned by the caller. Must be deterministic.</summary>
    protected abstract Tile ComputeTile(int tx, int ty, RenderContext context);
}

/// <summary>Held only by its node; when the node is collected this is too, and it releases the node's cached tiles.</summary>
internal sealed class CacheReaper(long nodeId, TileCache cache)
{
    ~CacheReaper()
    {
        TileCache.Global.RemoveNode(nodeId);
        TileCache.Streaming.RemoveNode(nodeId);
        if (cache != TileCache.Global && cache != TileCache.Streaming)
        {
            cache.RemoveNode(nodeId);
        }
    }
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

        // A render of the whole node reads every tile once, so caching them only costs: they would be evicted unread.
        // Streaming nodes keep their own window and do not go through this cache anyway.
        bool wholeNode = region.X == 0 && region.Y == 0 && region.Width == node.Width && region.Height == node.Height && !node.Streaming;
        var job = new CopyJob(node, region, destinationStride, destinationFormat, node.Format != destinationFormat ? KernelRegistry.Get<ConvertRowKernel>("convert") : null, useCache: !wholeNode);
        int tx0 = region.X >> Tile.Shift;
        int tx1 = (region.Right - 1) >> Tile.Shift;
        int ty0 = region.Y >> Tile.Shift;
        int ty1 = (region.Bottom - 1) >> Tile.Shift;
        int perRow = tx1 - tx0 + 1;
        int totalTiles = perRow * (ty1 - ty0 + 1);
        // Stored pixels copy at memory speed on one thread; the fork/join (and its allocations) only pays off for big copies or computed tiles.
        bool cheapCopy = node.DirectStorage && (long)region.Width * region.Height * dbpp < (4L << 20);
        if (context.Degree <= 1 || totalTiles == 1 || cheapCopy)
        {
            for (int ty = ty0; ty <= ty1; ty++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    job.CopyTile(tx, ty, destination, node.GetTile(tx, ty, context, job.UseCache));
                }
            }

            return;
        }

        // Each worker fetches a tile and copies it straight into its own disjoint rectangle of the destination, so the copy-out
        // runs in parallel and the bytes written never depend on scheduling. A streaming source keeps only a few tile rows, so
        // those are walked two tile rows at a time.
        int chunkRows = node.Streaming ? 2 : ty1 - ty0 + 1;
        bool blocked = node.HasBlockedSource;
        if (blocked)
        {
            // Decode native blocks ahead of the tile requests, a bounded band of tile rows at a time so the decoder's block cache
            // is not asked to hold more than one band.
            long bandBytes = Math.Max(1, (long)region.Width * Tile.Size * node.Format.BytesPerPixel);
            chunkRows = (int)Math.Clamp((16L << 20) / bandBytes, 1, ty1 - ty0 + 1);
        }
        RenderContext nested = context.Nested;
        var options = new ParallelOptions { MaxDegreeOfParallelism = context.Degree, CancellationToken = context.CancellationToken };
        try
        {
            PinnedDestination.Run(destination, 0, (pinned, _) =>
            {
                for (int cy = ty0; cy <= ty1; cy += chunkRows)
                {
                    int first = cy;
                    int count = Math.Min(chunkRows, ty1 - cy + 1) * perRow;
                    if (blocked)
                    {
                        int top = Math.Max(region.Y, cy << Tile.Shift);
                        int bottom = Math.Min(region.Bottom, (cy + Math.Min(chunkRows, ty1 - cy + 1)) << Tile.Shift);
                        node.Prefetch(new ImageRect(region.X, top, region.Width, bottom - top), context);
                    }

                    Parallel.For(0, count, options, i =>
                    {
                        int tx = tx0 + (i % perRow);
                        int ty = first + (i / perRow);
                        job.CopyTile(tx, ty, pinned, node.GetTile(tx, ty, nested, job.UseCache));
                    });
                }
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Surface the real error (format, limit, cancellation) rather than the wrapper.
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
    }

    /// <summary>What copying one tile into the destination needs; shared by all workers (read-only).</summary>
    private sealed class CopyJob(Node node, ImageRect region, int stride, PixelFormat destinationFormat, ConvertRowKernel? converter, bool useCache)
    {
        private readonly int _sbpp = node.Format.BytesPerPixel;
        private readonly int _dbpp = destinationFormat.BytesPerPixel;

        public bool UseCache { get; } = useCache;

        /// <summary>Copies (and converts) the part of <paramref name="tile"/> inside the region, then releases the tile.</summary>
        public void CopyTile(int tx, int ty, Span<byte> destination, Tile tile)
        {
            try
            {
                Prepare(tx, ty, out ImageRect part, out int first, out int length);
                Copy(tile, tx, ty, part, destination.Slice(first, length));
            }
            finally
            {
                tile.Release();
            }
        }

        public void CopyTile(int tx, int ty, PinnedDestination destination, Tile tile)
        {
            try
            {
                Prepare(tx, ty, out ImageRect part, out int first, out int length);
                Copy(tile, tx, ty, part, destination.Slice(first, length));
            }
            finally
            {
                tile.Release();
            }
        }

        private void Prepare(int tx, int ty, out ImageRect part, out int first, out int length)
        {
            part = region.Intersect(new ImageRect(tx << Tile.Shift, ty << Tile.Shift, Tile.Size, Tile.Size));
            first = ((part.Y - region.Y) * stride) + ((part.X - region.X) * _dbpp);
            length = ((part.Height - 1) * stride) + (part.Width * _dbpp);
        }

        private void Copy(Tile tile, int tx, int ty, ImageRect part, Span<byte> block)
        {
            ReadOnlySpan<byte> src = tile.Span;
            int tileX = part.X - (tx << Tile.Shift);
            int tileY = part.Y - (ty << Tile.Shift);
            int rowBytes = part.Width * _dbpp;
            for (int r = 0; r < part.Height; r++)
            {
                ReadOnlySpan<byte> srow = src.Slice((((tileY + r) << Tile.Shift) + tileX) * _sbpp, part.Width * _sbpp);
                Span<byte> drow = block.Slice(r * stride, rowBytes);
                if (converter == null)
                {
                    srow.CopyTo(drow);
                }
                else
                {
                    converter(srow, node.Format, drow, destinationFormat, part.Width);
                }
            }
        }
    }
}
