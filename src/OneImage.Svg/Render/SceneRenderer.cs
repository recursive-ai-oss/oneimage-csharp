using System.Buffers;
using System.Numerics;
using OneImage.Drawing;
using OneImage.Svg.Dom;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

/// <summary>
/// Draws a rectangle of the output from a render tree at a given output size, with the engine's fill and composite operations.
/// The output is cut into bands of at most <see cref="MaxBandRows"/> rows that are drawn one after another, so layers and canvases
/// stay small however large the output is; because every shape is rasterized by exact area per pixel, a pixel does not depend on
/// how the output was cut.
/// </summary>
internal static class SceneRenderer
{
    private const int MaxBandRows = 128;
    private const int MaxBandPixels = 4 * 1024 * 1024;

    private static long _pathsDrawn;

    /// <summary>Counts the shapes that were actually rasterized (fills and strokes); a diagnostic that tests use to see that culling works.</summary>
    internal static long PathsDrawn => Interlocked.Read(ref _pathsDrawn);

    /// <summary>Renders <paramref name="region"/> of a <paramref name="targetWidth"/> x <paramref name="targetHeight"/> render of the scene into the sink.</summary>
    public static void Render(SvgScene scene, int targetWidth, int targetHeight, ImageRect region, IRowSink sink, SvgLimits limits, CancellationToken cancel)
    {
        double sx = targetWidth / scene.Width;
        double sy = targetHeight / scene.Height;
        var scale = Affine.Scale(sx, sy);
        int bandRows = Math.Clamp(MaxBandPixels / Math.Max(region.Width, 1), 1, MaxBandRows);
        var hits = new List<int>();
        try
        {
            RenderBands(scene, region, sink, limits, sx, sy, scale, bandRows, hits, cancel);
        }
        finally
        {
            // The clip cache points into the scene; do not keep a finished document alive from a pool thread.
            t_clipSource = null;
            t_clipDevice = null;
        }
    }

    private static void RenderBands(SvgScene scene, ImageRect region, IRowSink sink, SvgLimits limits, double sx, double sy, Affine scale, int bandRows, List<int> hits, CancellationToken cancel)
    {
        for (int y = 0; y < region.Height; y += bandRows)
        {
            cancel.ThrowIfCancellationRequested();
            int rows = Math.Min(bandRows, region.Height - y);
            int px = region.X;
            int py = region.Y + y;
            using var canvas = new Canvas(region.Width, rows, PixelFormat.Rgba8);
            Affine dev = Affine.Translate(-px, -py).Multiply(scale);

            // The band in scene space, with a pixel of slack for anti-aliased edges.
            double qx0 = (px - 1) / sx;
            double qy0 = (py - 1) / sy;
            double qx1 = (px + region.Width + 1) / sx;
            double qy1 = (py + rows + 1) / sy;
            if (scene.Grid is { } grid)
            {
                grid.Query(qx0, qy0, qx1, qy1, hits);
                foreach (int i in hits)
                {
                    DrawNode(canvas, scene.Nodes[i], dev, qx0, qy0, qx1, qy1, 0, limits, cancel);
                }
            }
            else
            {
                foreach (RNode n in scene.Nodes)
                {
                    DrawNode(canvas, n, dev, qx0, qy0, qx1, qy1, 0, limits, cancel);
                }
            }

            WriteBand(canvas, region.Width, rows, y, sink);
        }
    }

    private static void WriteBand(Canvas canvas, int width, int rows, int firstRow, IRowSink sink)
    {
        int length = width * rows * 4;
        byte[] bytes = Pools<byte>.Pool.Rent(length);
        try
        {
            canvas.CopyToBytes(bytes.AsSpan(0, length), new ImageRect(0, 0, width, rows));
            sink.WriteRows(firstRow, bytes.AsSpan(0, length), rows);
        }
        finally
        {
            Pools<byte>.Pool.Return(bytes);
        }
    }

    internal static void DrawNode(Canvas canvas, RNode node, Affine dev, double qx0, double qy0, double qx1, double qy1, int depth, SvgLimits limits, CancellationToken cancel = default)
    {
        if (node.X1 < qx0 || node.X0 > qx1 || node.Y1 < qy0 || node.Y0 > qy1)
        {
            return;
        }

        switch (node)
        {
            case RPath p:
                DrawPath(canvas, p, dev);
                break;
            case RImage image:
                ImageRenderer.Draw(canvas, image, dev, depth, limits);
                break;
            case RLayer layer:
                if (depth >= limits.MaxLayerDepth)
                {
                    throw new ImageLimitException("Groups with opacity are nested too deep.");
                }

                // The layer is only as large as the part of its bounds inside this band, so a small clipped group does not cost a whole band.
                (int ox, int oy, int lw, int lh) = LayerRect(layer, dev, canvas.Width, canvas.Height);
                if (lw <= 0 || lh <= 0)
                {
                    break;
                }

                Affine ldev = Affine.Translate(-ox, -oy).Multiply(dev);

                // The layer's tiles are read in place by the composite below and handed back to the pool right after, so a layer costs
                // pooled memory instead of fresh arrays (a snapshot would keep its tiles until the garbage collector found them).
                List<Canvas>? maskCanvases = layer.Mask is null ? null : [];
                var offscreen = new Canvas(lw, lh, PixelFormat.Rgba8);
                ImageBrush? brush = null;
                try
                {
                    if (layer.Filters is not null)
                    {
                        FilterRenderer.Draw(offscreen, layer, ldev, depth, limits, cancel);
                    }
                    else
                    {
                        foreach (RNode child in layer.Children)
                        {
                            DrawNode(offscreen, child, ldev, qx0, qy0, qx1, qy1, depth + 1, limits, cancel);
                        }
                    }

                    Image content = offscreen.View();
                    if (layer.Mask is { } mask)
                    {
                        content = content.ApplyMask(RenderMask(mask, lw, lh, ldev, qx0, qy0, qx1, qy1, depth + 1, limits, maskCanvases!), mask.MaskAlpha ? MaskChannel.Alpha : MaskChannel.Luminance);
                    }

                    Path? clip = layer.ClipPath?.Transform(dev.ToMatrix3x2());
                    brush = new ImageBrush(content, null, ox, oy, null, tile: false, pooled: true);
                    canvas.Fill(brush, Path.Rectangle(ox, oy, lw, lh), opacity: layer.Opacity, blend: layer.Blend, composition: layer.Plus ? AlphaComposition.Plus : AlphaComposition.SrcOver, clip: clip);
                }
                finally
                {
                    brush?.Release();
                    offscreen.Dispose();
                    if (maskCanvases is not null)
                    {
                        foreach (Canvas c in maskCanvases)
                        {
                            c.Dispose();
                        }
                    }
                }

                break;
        }
    }

    // The layer's bounds in the band's pixels, widened to whole pixels and cut to the band.
    private static (int X, int Y, int Width, int Height) LayerRect(RLayer layer, Affine dev, int width, int height)
    {
        (double x0, double y0, double x1, double y1) = dev.TransformRect(layer.X0, layer.Y0, layer.X1, layer.Y1);
        if (!double.IsFinite(x0 + y0 + x1 + y1))
        {
            return (0, 0, width, height);
        }

        int ax = (int)Math.Clamp(Math.Floor(x0), 0, width);
        int ay = (int)Math.Clamp(Math.Floor(y0), 0, height);
        int bx = (int)Math.Clamp(Math.Ceiling(x1), 0, width);
        int by = (int)Math.Clamp(Math.Ceiling(y1), 0, height);
        return (ax, ay, bx - ax, by - ay);
    }

    // The mask's content as an image, narrowed by the mask it carries itself. Depth is bounded by the layer-depth limit.
    // The canvases the mask chain is drawn on are added to <owned>: the returned image reads them in place, so the caller disposes them after the composite.
    private static Image RenderMask(RLayer mask, int width, int height, Affine dev, double qx0, double qy0, double qx1, double qy1, int depth, SvgLimits limits, List<Canvas> owned)
    {
        if (depth >= limits.MaxLayerDepth)
        {
            throw new ImageLimitException("Masks are nested too deep.");
        }

        var maskCanvas = new Canvas(width, height, PixelFormat.Rgba8);
        owned.Add(maskCanvas);
        foreach (RNode child in mask.Children)
        {
            DrawNode(maskCanvas, child, dev, qx0, qy0, qx1, qy1, depth + 1, limits);
        }

        Image content = maskCanvas.View();
        return mask.Mask is { } next
            ? content.ApplyMask(RenderMask(next, width, height, dev, qx0, qy0, qx1, qy1, depth + 1, limits, owned), next.MaskAlpha ? MaskChannel.Alpha : MaskChannel.Luminance)
            : content;
    }

    private static bool IsFinite(Matrix3x2 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32);

    private static void DrawPath(Canvas canvas, RPath p, Affine dev)
    {
        Affine full = dev.Multiply(p.Ctm);
        if (!full.IsFinite)
        {
            return;
        }

        Matrix3x2 m = full.ToMatrix3x2();
        Matrix3x2 d = dev.ToMatrix3x2();
        if (!IsFinite(m) || !IsFinite(d))
        {
            return;
        }

        Path? clip = DeviceClip(p.Clip, d);
        IntersectionRule rule = p.EvenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero;
        if (p.StrokeFirst)
        {
            Stroke(canvas, p, dev, m, full, clip);
            Fill(canvas, p, dev, m, rule, clip);
        }
        else
        {
            Fill(canvas, p, dev, m, rule, clip);
            Stroke(canvas, p, dev, m, full, clip);
        }
    }

    // Shapes in one viewport share its clip: the device-space copy is made once for a run of them instead of once per shape.
    [ThreadStatic]
    private static Path? t_clipSource;
    [ThreadStatic]
    private static Matrix3x2 t_clipMatrix;
    [ThreadStatic]
    private static Path? t_clipDevice;

    private static Path? DeviceClip(Path? clip, Matrix3x2 d)
    {
        if (clip is null)
        {
            return null;
        }

        if (ReferenceEquals(clip, t_clipSource) && t_clipMatrix == d)
        {
            return t_clipDevice;
        }

        Path device = clip.Transform(d);
        t_clipSource = clip;
        t_clipMatrix = d;
        t_clipDevice = device;
        return device;
    }

    private static void Fill(Canvas canvas, RPath p, Affine dev, Matrix3x2 m, IntersectionRule rule, Path? clip)
    {
        if (!p.HasFill)
        {
            return;
        }

        Interlocked.Increment(ref _pathsDrawn);
        if (p.FillPaint is null)
        {
            canvas.Fill(p.FillBrush, p.Geometry, p.Antialias, rule, p.FillOpacity, transform: m, clip: clip);
        }
        else if (p.FillPaint.CreateBrush(dev) is { } brush)
        {
            canvas.Fill(brush, p.Geometry, p.Antialias, rule, p.FillOpacity, transform: m, clip: clip);
        }
    }

    private static void Stroke(Canvas canvas, RPath p, Affine dev, Matrix3x2 m, Affine full, Path? clip)
    {
        if (!p.HasStroke)
        {
            return;
        }

        Interlocked.Increment(ref _pathsDrawn);

        // Flatten curves finely enough for the pixel grid: 0.1 px, expressed in the geometry's own units.
        float tolerance = (float)Math.Clamp(Path.DefaultTolerance / Math.Max(full.MaxScale, 1e-6), 1e-6, 1e6);
        Path outline = p.StrokeOutline(tolerance);
        if (p.StrokePaint is null)
        {
            canvas.Fill(p.StrokeBrush, outline, p.Antialias, IntersectionRule.NonZero, p.StrokeOpacity, transform: m, clip: clip);
        }
        else if (p.StrokePaint.CreateBrush(dev) is { } brush)
        {
            canvas.Fill(brush, outline, p.Antialias, IntersectionRule.NonZero, p.StrokeOpacity, transform: m, clip: clip);
        }
    }
}
