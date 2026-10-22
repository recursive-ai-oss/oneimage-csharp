using System.Numerics;
using OneImage.Drawing;
using OneImage.Svg.Dom;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

/// <summary>Draws <see cref="RImage"/> nodes: rasters through an image brush at their drawn size, nested documents as vector content on a layer.</summary>
internal static class ImageRenderer
{
    public static void Draw(Canvas canvas, RImage img, Affine dev, int depth, SvgLimits limits)
    {
        Affine full = dev.Multiply(img.Ctm);
        if (!full.IsFinite || !dev.IsFinite)
        {
            return;
        }

        Matrix3x2 m = full.ToMatrix3x2();
        Matrix3x2 d = dev.ToMatrix3x2();
        if (!IsFinite(m) || !IsFinite(d))
        {
            return;
        }

        Path? clip = img.Clip?.Transform(d);
        Path rect = Path.Rectangle(0, 0, Coord.F(img.Width), Coord.F(img.Height));
        if (img.Nested is { } nested)
        {
            DrawNested(canvas, img, nested, full, m, rect, clip, depth, limits);
            return;
        }

        // A picture drawn smaller than it is is shrunk once (per axis) to the size it is drawn at, so every drawn pixel averages the
        // source pixels it covers; enlarging, and the placement of what is left, is bilinear in the brush.
        double sx = Math.Sqrt((full.A * full.A) + (full.B * full.B));
        double sy = Math.Sqrt((full.C * full.C) + (full.D * full.D));
        RasterSource source = img.Raster!;
        int rw = source.Image.Width;
        int rh = source.Image.Height;
        int tw = rw;
        int th = rh;
        if (!img.Pixelated && sx > 0 && sy > 0)
        {
            tw = (int)Math.Clamp(Math.Round(rw * sx), 1, rw);
            th = (int)Math.Clamp(Math.Round(rh * sy), 1, rh);
        }

        SampledPixels? prepared = source.Prepared(tw, th);
        if (prepared is null)
        {
            return;
        }

        // Prepared pixel -> canvas pixel.
        Matrix3x2 t = Matrix3x2.CreateScale((float)((double)rw / tw), (float)((double)rh / th)) * m;
        canvas.Fill(new SampledBrush(prepared, t, img.Pixelated), rect, antialias: true, opacity: img.Opacity, transform: m, clip: clip);
    }

    private static void DrawNested(Canvas canvas, RImage img, SvgScene nested, Affine full, Matrix3x2 m, Path rect, Path? clip, int depth, SvgLimits limits)
    {
        if (depth >= limits.MaxLayerDepth)
        {
            throw new ImageLimitException("Groups with opacity and images are nested too deep.");
        }

        double det = full.Determinant;
        if (!(Math.Abs(det) > 1e-12))
        {
            return;
        }

        // The canvas in the nested document's own space, to skip what cannot be seen.
        var inv = new Affine(full.D / det, -full.B / det, -full.C / det, full.A / det, ((full.C * full.F) - (full.D * full.E)) / det, ((full.B * full.E) - (full.A * full.F)) / det);
        (double qx0, double qy0, double qx1, double qy1) = inv.TransformRect(-1, -1, canvas.Width + 1, canvas.Height + 1);
        using var layer = new Canvas(canvas.Width, canvas.Height, PixelFormat.Rgba8);
        if (nested.Grid is { } grid)
        {
            var hits = new List<int>();
            grid.Query(qx0, qy0, qx1, qy1, hits);
            foreach (int i in hits)
            {
                SceneRenderer.DrawNode(layer, nested.Nodes[i], full, qx0, qy0, qx1, qy1, depth + 1, limits);
            }
        }
        else
        {
            foreach (RNode n in nested.Nodes)
            {
                SceneRenderer.DrawNode(layer, n, full, qx0, qy0, qx1, qy1, depth + 1, limits);
            }
        }

        Image snapshot = layer.Snapshot();
        canvas.Fill(Brush.FromImage(snapshot, tile: false), rect, antialias: true, opacity: img.Opacity, transform: m, clip: clip);
    }

    private static bool IsFinite(Matrix3x2 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32);
}
