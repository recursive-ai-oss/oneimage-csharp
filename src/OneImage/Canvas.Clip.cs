using System.Numerics;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage;

public sealed partial class Canvas
{
    /// <summary>
    /// Runs <paramref name="operations"/> on the canvas's pixels but keeps the result only inside <paramref name="path"/>, in place.
    /// The operations receive a snapshot of the canvas (see <see cref="Snapshot"/>) and return the processed image; the pixels
    /// written are byte for byte what <c>Snapshot().Clip(...)</c> shows, with the semantics described at
    /// <see cref="Image.Clip(Path, Func{Image, Image}, bool, IntersectionRule, Matrix3x2?)"/>. Only tiles the path can touch are
    /// written (shared tiles are copied first); they are computed in parallel with identical output at any thread count.
    /// </summary>
    /// <param name="path">The region, in canvas pixel coordinates.</param>
    /// <param name="operations">Builds the processed image from the snapshot. Called once, synchronously.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="rule">Inside rule for overlapping parts of the path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="transform">Applied to the path first. The default is none.</param>
    /// <exception cref="ObjectDisposedException">The canvas was disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="operations"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rule"/> is undefined or the transform is not finite.</exception>
    /// <exception cref="ImageOperationException">The canvas is CMYK or Lab, or the operations returned null or changed the size.</exception>
    /// <exception cref="ImageLimitException">The path flattens to too many segments.</exception>
    public void Clip(Path path, Func<Image, Image> operations, bool antialias = true, IntersectionRule rule = IntersectionRule.NonZero, Matrix3x2? transform = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(operations);
        // Without an outside snapshot nothing else can see the tiles, so the work reads a private copy of the tile table instead of
        // calling Snapshot(): the canvas keeps owning its tiles, writes swap in fresh pooled tiles, and the replaced ones go back
        // to the pool afterwards. With an outside snapshot the tiles belong to it and the ordinary copy-on-write path applies.
        bool exclusive = !_grid.Shared;
        TileGrid readGrid = exclusive ? _grid.Clone() : _grid;
        if (!exclusive)
        {
            _grid.Shared = true;
            _token = new object();
        }

        Image clipped = Image.FromNode(new GridSourceNode(readGrid)).Clip(path, operations, antialias, rule, transform);
        var node = (ClipNode)clipped.Root;
        Raster raster = node.Raster;
        var touched = new List<(int Tx, int Ty, Tile Tile)>();
        var replaced = new List<(int Index, Tile? Old)>();
        bool done = false;
        try
        {
            for (int ty = raster.MinTileY; ty <= raster.MaxTileY; ty++)
            {
                for (int tx = raster.MinTileX; tx <= raster.MaxTileX; tx++)
                {
                    if (!raster.MayTouch(tx, ty))
                    {
                        continue;
                    }

                    if (exclusive)
                    {
                        int idx = (ty * _grid.TilesX) + tx;
                        Tile fresh = Tile.Rent(Tile.ByteSize(Format), clear: false, _store);
                        fresh.Owner = _token;
                        replaced.Add((idx, _grid.Tiles[idx]));
                        _grid.Tiles[idx] = fresh;
                        touched.Add((tx, ty, fresh));
                    }
                    else
                    {
                        touched.Add((tx, ty, WritableTile(tx, ty, willOverwriteAll: true)));
                    }
                }
            }

            // Each tile depends only on its own coverage and the source tiles, so the order of work never shows in the output.
            RenderContext context = RenderContext.Create(1, CancellationToken.None);
            int degree = ImageEngine.ResolveDegree(0);
            if (degree <= 1 || touched.Count < 2)
            {
                foreach ((int tx, int ty, Tile tile) in touched)
                {
                    CopyClipped(node, context, tx, ty, tile);
                }
            }
            else
            {
                Parallel.For(0, touched.Count, new ParallelOptions { MaxDegreeOfParallelism = degree }, i => CopyClipped(node, context, touched[i].Tx, touched[i].Ty, touched[i].Tile));
            }

            done = true;
        }
        finally
        {
            // The clipped graph was a one-shot pass: give back the tiles its nodes cached rather than leave them to the cache budget.
            Node.ReleaseAfterSave([node]);

            // Success: the replaced tiles are no longer referenced. Failure: put them back so the canvas is unchanged.
            foreach ((int index, Tile? old) in replaced)
            {
                if (done)
                {
                    old?.Release();
                }
                else
                {
                    _grid.Tiles[index]?.Release();
                    _grid.Tiles[index] = old;
                }
            }

            if (exclusive)
            {
                // The private table's references; the canvas's own were handled above.
                readGrid.ReleaseAll();
            }
        }
    }

    private static void CopyClipped(Node node, RenderContext context, int tx, int ty, Tile target)
    {
        Tile source = node.GetTile(tx, ty, context);
        try
        {
            source.Span.CopyTo(target.Span);
        }
        finally
        {
            source.Release();
        }
    }
}
