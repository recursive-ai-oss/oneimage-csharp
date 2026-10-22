using System.Runtime.ExceptionServices;

namespace OneImage;

/// <summary>Computes a value from one tile; <paramref name="rect"/> is the valid part of the tile (edge tiles are smaller than 128).</summary>
internal delegate TPartial TileMap<out TPartial>(ImageRect rect, ReadOnlySpan<byte> tile);

/// <summary>
/// Pulls every tile of a node and reduces them to one value. Tiles are computed in parallel a few tile rows at a time
/// (never more than two rows for a streaming source, so memory stays bounded), each tile yields a partial result, and the
/// partials of a batch are merged sequentially in tile order. The merge order therefore never depends on thread count or scheduling.
/// </summary>
internal static class TileReducer
{
    public static void Run<TPartial>(Node node, RenderContext context, TileMap<TPartial> map, Action<TPartial> reduce)
    {
        int tilesX = node.TilesX;
        int tilesY = node.TilesY;
        int degree = context.Degree;
        int chunkRows = degree > 1 ? Math.Max(1, (2 * degree) / tilesX) : 1;
        if (node.Streaming)
        {
            chunkRows = Math.Min(chunkRows, 2);
        }

        var partials = new TPartial?[tilesX * chunkRows];
        for (int ty0 = 0; ty0 < tilesY; ty0 += chunkRows)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            int rows = Math.Min(chunkRows, tilesY - ty0);
            int count = rows * tilesX;
            if (degree <= 1 || count == 1)
            {
                for (int i = 0; i < count; i++)
                {
                    partials[i] = One(node, i % tilesX, ty0 + (i / tilesX), map, context);
                }
            }
            else
            {
                RenderContext nested = context.Nested;
                var options = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = context.CancellationToken };
                try
                {
                    Parallel.For(0, count, options, i => partials[i] = One(node, i % tilesX, ty0 + (i / tilesX), map, nested));
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
                    throw;
                }
            }

            for (int i = 0; i < count; i++)
            {
                reduce(partials[i]!);
                partials[i] = default;
            }
        }
    }

    private static TPartial One<TPartial>(Node node, int tx, int ty, TileMap<TPartial> map, RenderContext context)
    {
        Tile tile = node.GetTile(tx, ty, context);
        try
        {
            return map(node.TileRect(tx, ty), tile.Span);
        }
        finally
        {
            tile.Release();
        }
    }
}
