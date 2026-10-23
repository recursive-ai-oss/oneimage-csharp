using System.Numerics;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage;

public sealed partial class Canvas
{
    /// <summary>
    /// Fills the inside of <paramref name="path"/> with <paramref name="brush"/>, in place. The geometry, coverage, color and
    /// option rules are those of <see cref="Image.Fill(Brush, Path, bool, IntersectionRule, float, BlendMode, AlphaComposition, bool, Matrix3x2?, Path, IntersectionRule)"/>,
    /// and the pixels written are byte for byte what <c>Snapshot().Fill(...)</c> would show. Only the tiles the path can touch are
    /// written (shared tiles are copied first), so a small shape on a large canvas costs a few tiles. The fill runs on the calling thread.
    /// </summary>
    /// <param name="brush">What to paint with; see <see cref="Brush"/> for solid, gradient, pattern, image and recolor brushes.</param>
    /// <param name="path">The outline, in canvas pixel coordinates.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="rule">Inside rule for overlapping parts. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="opacity">Multiplier for the brush alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the path before filling. The default is none.</param>
    /// <param name="clip">Limits the drawing to the inside of this path (in image pixel coordinates, not moved by <paramref name="transform"/>): its coverage multiplies the shape's, so only pixels inside both change, and edges blend smoothly. The default is no clip. With <paramref name="antialias"/> off the clip is sampled at pixel centers.</param>
    /// <param name="clipRule">Inside rule for the clip path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <exception cref="ObjectDisposedException">The canvas was disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ImageOperationException">The canvas is CMYK or Lab, the composition would make a canvas without alpha transparent, or the transform leaves the finite range.</exception>
    /// <exception cref="ImageLimitException">The path flattens to too many segments (<see cref="Path.MaxFlattenedPoints"/>).</exception>
    public void Fill(Brush brush, Path path, bool antialias = true, IntersectionRule rule = IntersectionRule.NonZero, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null, Path? clip = null, IntersectionRule clipRule = IntersectionRule.NonZero)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(path);
        FillCore(brush, path, FillSettings.Check(antialias, rule, opacity, blend, composition, linear, transform, Format, clip, clipRule));
    }

    /// <summary>Fills <paramref name="path"/> with one color, in place; see <see cref="Fill(Brush, Path, bool, IntersectionRule, float, BlendMode, AlphaComposition, bool, Matrix3x2?, Path, IntersectionRule)"/>.</summary>
    /// <param name="color">The color, straight alpha, in the canvas's color space.</param>
    /// <param name="path">The outline, in canvas pixel coordinates.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="rule">Inside rule for overlapping parts. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <param name="opacity">Multiplier for the color's alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <param name="transform">Applied to the path before filling. The default is none.</param>
    /// <param name="clip">Limits the drawing to the inside of this path (in image pixel coordinates, not moved by <paramref name="transform"/>): its coverage multiplies the shape's, so only pixels inside both change, and edges blend smoothly. The default is no clip. With <paramref name="antialias"/> off the clip is sampled at pixel centers.</param>
    /// <param name="clipRule">Inside rule for the clip path. The default is <see cref="IntersectionRule.NonZero"/>.</param>
    /// <exception cref="ObjectDisposedException">The canvas was disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ImageOperationException">The canvas is CMYK or Lab, the composition would make a canvas without alpha transparent, or the transform leaves the finite range.</exception>
    /// <exception cref="ImageLimitException">The path flattens to too many segments.</exception>
    public void Fill(Rgba32 color, Path path, bool antialias = true, IntersectionRule rule = IntersectionRule.NonZero, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false, Matrix3x2? transform = null, Path? clip = null, IntersectionRule clipRule = IntersectionRule.NonZero) =>
        Fill(Brush.Solid(color), path, antialias, rule, opacity, blend, composition, linear, transform, clip, clipRule);

    /// <summary>Lays one color over every pixel, in place; see <see cref="Image.Fill(Rgba32, float, BlendMode, AlphaComposition, bool)"/>.</summary>
    /// <param name="color">The color, straight alpha, in the canvas's color space.</param>
    /// <param name="opacity">Multiplier for the color's alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <exception cref="ObjectDisposedException">The canvas was disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ImageOperationException">The canvas is CMYK or Lab, or the composition would make a canvas without alpha transparent.</exception>
    public void Fill(Rgba32 color, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false) =>
        Fill(Brush.Solid(color), Rectangle(Width, Height), false, IntersectionRule.NonZero, opacity, blend, composition, linear);

    /// <summary>Replaces every pixel with <paramref name="color"/>, in place; see <see cref="Image.Clear"/>.</summary>
    /// <param name="color">The color, straight alpha, in the canvas's color space.</param>
    /// <exception cref="ObjectDisposedException">The canvas was disposed.</exception>
    /// <exception cref="ImageOperationException">The canvas is CMYK or Lab.</exception>
    public void Clear(Rgba32 color) =>
        Fill(Brush.Solid(color), Rectangle(Width, Height), false, IntersectionRule.NonZero, 1f, BlendMode.Normal, Format.HasAlpha ? AlphaComposition.Src : AlphaComposition.SrcOver);

    private static Path Rectangle(int width, int height) => Path.Rectangle(0, 0, width, height);

    private void FillCore(Brush brush, Path path, FillSettings s)
    {
        Raster raster = Raster.BuildScoped(path, s.Transform, Width, Height, s.Tolerance);
        Raster? clip = s.Clip == null ? null : Raster.BuildScoped(s.Clip, Matrix3x2.Identity, Width, Height, s.Tolerance);
        FillRasterized(brush, path, s, raster, clip);

        // Both rasters were only read by the fill above, which has finished, so their buffers can serve the next fill on this thread.
        raster.Recycle();
        clip?.Recycle();
    }

    private void FillRasterized(Brush brush, Path path, FillSettings s, Raster raster, Raster? clip)
    {
        bool linear8 = s.Linear && FillPlan.CanBlendLinear8(Format);
        if (s.Linear && !linear8)
        {
            FillLinear(brush, path, s, raster, clip);
            return;
        }

        FillPlan plan;
        if (linear8)
        {
            float[]? solid = brush.TryGetSolid(out Rgba32 c) ? FillPlan.LinearSolid8(c, Format) : null;
            plan = FillPlan.CreateLinear8(Format, solid != null ? brush : brush.Linearized(ColorMap.Default(Format.Model)), s, solid);
        }
        else
        {
            plan = FillPlan.Create(Format, brush, s);
        }

        // Writable tiles are taken one by one (the tile table is single-writer); painting them is independent per tile and
        // runs in parallel. Each tile's result depends only on its own rasterization, so the output never depends on scheduling.
        List<(int Tx, int Ty, Tile Tile)> touched = s_touched ??= [];
        s_touched = null; // taken: a fill started from a paint callback on this thread builds its own list
        touched.Clear();
        for (int ty = raster.MinTileY; ty <= raster.MaxTileY; ty++)
        {
            for (int tx = raster.MinTileX; tx <= raster.MaxTileX; tx++)
            {
                if (raster.MayTouch(tx, ty) && (clip == null || clip.MayTouch(tx, ty)))
                {
                    touched.Add((tx, ty, WritableTile(tx, ty, willOverwriteAll: false)));
                }
            }
        }

        int degree = ImageEngine.ResolveDegree(0);
        if (degree <= 1 || touched.Count < 4)
        {
            foreach ((int tx, int ty, Tile tile) in touched)
            {
                PaintTile(raster, clip, plan, s, tx, ty, tile);
            }
        }
        else
        {
            PaintTilesInParallel(touched, degree, raster, clip, plan, s);
        }

        touched.Clear();
        s_touched = touched;
    }

    [ThreadStatic]
    private static List<(int Tx, int Ty, Tile Tile)>? s_touched;

    // Kept apart so a fill that stays on one thread does not allocate the closure the parallel loop needs.
    private void PaintTilesInParallel(List<(int Tx, int Ty, Tile Tile)> touched, int degree, Raster raster, Raster? clip, FillPlan plan, FillSettings s) =>
        Parallel.For(0, touched.Count, new ParallelOptions { MaxDegreeOfParallelism = degree }, i => PaintTile(raster, clip, plan, s, touched[i].Tx, touched[i].Ty, touched[i].Tile));

    /// <summary>
    /// Linear light converts through the image pipeline; the tiles it produces replace the touched ones. Without an outside snapshot
    /// the pipeline reads a private copy of the tile table (as <c>Clip</c> does), so the canvas keeps owning its tiles and the replaced
    /// ones go back to the pool. Either way the tiles the one-shot graph cached are given back when it is done.
    /// </summary>
    private void FillLinear(Brush brush, Path path, FillSettings s, Raster raster, Raster? clip)
    {
        bool exclusive = !_grid.Shared;
        TileGrid readGrid = exclusive ? _grid.Clone() : _grid;
        if (!exclusive)
        {
            _grid.Shared = true;
            _token = new object();
        }

        Node filled = Image.FromNode(new GridSourceNode(readGrid)).FillCore(brush, path, s).Root;
        RenderContext context = RenderContext.Create(1, CancellationToken.None);
        var replaced = new List<(int Index, Tile? Old)>();
        bool done = false;
        try
        {
            for (int ty = raster.MinTileY; ty <= raster.MaxTileY; ty++)
            {
                for (int tx = raster.MinTileX; tx <= raster.MaxTileX; tx++)
                {
                    if (!raster.MayTouch(tx, ty) || (clip != null && !clip.MayTouch(tx, ty)))
                    {
                        continue;
                    }

                    Tile source = filled.GetTile(tx, ty, context);
                    try
                    {
                        if (exclusive)
                        {
                            int idx = (ty * _grid.TilesX) + tx;
                            Tile fresh = Tile.Rent(Tile.ByteSize(Format), clear: false);
                            fresh.Owner = _token;
                            replaced.Add((idx, _grid.Tiles[idx]));
                            _grid.Tiles[idx] = fresh;
                            source.Span.CopyTo(fresh.Span);
                        }
                        else
                        {
                            source.Span.CopyTo(WritableTile(tx, ty, willOverwriteAll: true).Span);
                        }
                    }
                    finally
                    {
                        source.Release();
                    }
                }
            }

            done = true;
        }
        finally
        {
            Node.ReleaseAfterSave([filled]);
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

    private void PaintTile(Raster raster, Raster? clip, FillPlan plan, FillSettings s, int tx, int ty, Tile tile)
    {
        int x = tx << Tile.Shift, y = ty << Tile.Shift;
        int tw = Math.Min(Tile.Size, Width - x), th = Math.Min(Tile.Size, Height - y);
        RasterScratch scratch = RasterScratch.Rent();
        try
        {
            if (TileRasterizer.Rasterize(raster, tx, ty, tw, th, scratch, s.Antialias, s.Rule)
                && (clip == null || TileRasterizer.ApplyClip(clip, tx, ty, tw, th, scratch, s.Antialias, s.ClipRule)))
            {
                plan.Paint(tile.Span, tw, th, x, y, scratch);
            }
        }
        finally
        {
            RasterScratch.Return(scratch);
        }
    }
}
