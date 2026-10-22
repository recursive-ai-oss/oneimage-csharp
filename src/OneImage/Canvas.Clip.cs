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
        Image clipped = Snapshot().Clip(path, operations, antialias, rule, transform);
        var node = (ClipNode)clipped.Root;
        Raster raster = node.Raster;
        var touched = new List<(int Tx, int Ty, Tile Tile)>();
        for (int ty = raster.MinTileY; ty <= raster.MaxTileY; ty++)
        {
            for (int tx = raster.MinTileX; tx <= raster.MaxTileX; tx++)
            {
                if (raster.MayTouch(tx, ty))
                {
                    touched.Add((tx, ty, WritableTile(tx, ty, willOverwriteAll: true)));
                }
            }
        }

        // Each tile depends only on its own coverage and the snapshot, so the order of work never shows in the output.
        RenderContext context = RenderContext.Create(1, CancellationToken.None);
        int degree = ImageEngine.ResolveDegree(0);
        if (degree <= 1 || touched.Count < 2)
        {
            foreach ((int tx, int ty, Tile tile) in touched)
            {
                CopyClipped(node, context, tx, ty, tile);
            }

            return;
        }

        Parallel.For(0, touched.Count, new ParallelOptions { MaxDegreeOfParallelism = degree }, i => CopyClipped(node, context, touched[i].Tx, touched[i].Ty, touched[i].Tile));
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
