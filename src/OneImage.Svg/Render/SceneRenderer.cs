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
                    DrawNode(canvas, scene.Nodes[i], dev, qx0, qy0, qx1, qy1, 0, limits);
                }
            }
            else
            {
                foreach (RNode n in scene.Nodes)
                {
                    DrawNode(canvas, n, dev, qx0, qy0, qx1, qy1, 0, limits);
                }
            }

            WriteBand(canvas, region.Width, rows, y, sink);
        }
    }

    private static void WriteBand(Canvas canvas, int width, int rows, int firstRow, IRowSink sink)
    {
        int count = width * rows;
        Rgba32[] pixels = ArrayPool<Rgba32>.Shared.Rent(count);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(count * 4);
        try
        {
            canvas.CopyTo(pixels.AsSpan(0, count), new ImageRect(0, 0, width, rows));
            for (int i = 0; i < count; i++)
            {
                Rgba32 p = pixels[i];
                int o = i * 4;
                bytes[o] = p.R;
                bytes[o + 1] = p.G;
                bytes[o + 2] = p.B;
                bytes[o + 3] = p.A;
            }

            sink.WriteRows(firstRow, bytes.AsSpan(0, count * 4), rows);
        }
        finally
        {
            ArrayPool<Rgba32>.Shared.Return(pixels);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static void DrawNode(Canvas canvas, RNode node, Affine dev, double qx0, double qy0, double qx1, double qy1, int depth, SvgLimits limits)
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
            case RLayer layer:
                if (depth >= limits.MaxLayerDepth)
                {
                    throw new ImageLimitException("Groups with opacity are nested too deep.");
                }

                using (var offscreen = new Canvas(canvas.Width, canvas.Height, PixelFormat.Rgba8))
                {
                    foreach (RNode child in layer.Children)
                    {
                        DrawNode(offscreen, child, dev, qx0, qy0, qx1, qy1, depth + 1, limits);
                    }

                    Image snapshot = offscreen.Snapshot();
                    canvas.Fill(Brush.FromImage(snapshot, tile: false), Path.Rectangle(0, 0, canvas.Width, canvas.Height), opacity: layer.Opacity);
                }

                break;
        }
    }

    private static void DrawPath(Canvas canvas, RPath p, Affine dev)
    {
        Affine full = dev.Multiply(p.Ctm);
        if (!full.IsFinite)
        {
            return;
        }

        Matrix3x2 m = full.ToMatrix3x2();
        Path? clip = p.Clip?.Transform(dev.ToMatrix3x2());
        IntersectionRule rule = p.EvenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero;
        if (p.HasFill)
        {
            Interlocked.Increment(ref _pathsDrawn);
            canvas.Fill(p.FillColor, p.Geometry, p.Antialias, rule, p.FillOpacity, transform: m, clip: clip);
        }

        if (p.HasStroke)
        {
            Interlocked.Increment(ref _pathsDrawn);

            // Flatten curves finely enough for the pixel grid: 0.1 px, expressed in the geometry's own units.
            float tolerance = (float)Math.Clamp(Path.DefaultTolerance / Math.Max(full.MaxScale, 1e-6), 1e-6, 1e6);
            canvas.Fill(p.StrokeColor, p.StrokeOutline(tolerance), p.Antialias, IntersectionRule.NonZero, p.StrokeOpacity, transform: m, clip: clip);
        }
    }
}
