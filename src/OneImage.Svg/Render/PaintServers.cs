using System.Numerics;
using OneImage.Drawing;
using OneImage.Svg.Dom;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

internal enum ResolvedKind
{
    None,
    Solid,
    Server,
}

/// <summary>
/// What a <c>fill</c> or <c>stroke</c> value turned into once references, fallbacks and context paints were resolved: nothing, a flat
/// color, or a paint server that makes a brush at draw time.
/// </summary>
internal readonly record struct ResolvedPaint(ResolvedKind Kind, Rgba32 Color = default, RPaint? Server = null)
{
    public static ResolvedPaint None => default;

    public static ResolvedPaint Solid(Rgba32 color) => new(ResolvedKind.Solid, color);

    public static ResolvedPaint FromServer(RPaint server) => new(ResolvedKind.Server, default, server);
}

/// <summary>The object bounding box of the element a paint server paints, in that element's own coordinates; for a <c>use</c> it is known once the element's content is built.</summary>
internal sealed class BBoxHolder
{
    public bool Known { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>Gets or sets whether a paint depends on the box, so a <c>use</c> has to measure its content.</summary>
    public bool Needed { get; set; }

    public bool IsUsable => Known && Width > 0 && Height > 0 && double.IsFinite(Width + Height + X + Y);

    public static BBoxHolder Of(Path geometry)
    {
        System.Drawing.RectangleF b = geometry.Bounds;
        return new BBoxHolder { Known = true, X = b.X, Y = b.Y, Width = b.Width, Height = b.Height };
    }
}

/// <summary>A paint server resolved against the element it paints: it makes a brush for a given device matrix.</summary>
internal abstract class RPaint
{
    /// <summary>Makes the brush for a canvas whose pixels relate to scene space by <paramref name="dev"/>; <see langword="null"/> when nothing can be painted.</summary>
    public abstract Brush? CreateBrush(Affine dev);

    /// <summary>Narrows a matrix to the float form brushes take; <see langword="false"/> when that overflows or loses the inverse (the brush would reject it).</summary>
    protected static bool TryMatrix(Affine m, out Matrix3x2 matrix)
    {
        matrix = m.ToMatrix3x2();
        return m.IsFinite && Math.Abs(m.Determinant) > 1e-20
            && float.IsFinite(matrix.M11 + matrix.M12 + matrix.M21 + matrix.M22 + matrix.M31 + matrix.M32)
            && Matrix3x2.Invert(matrix, out _);
    }
}

/// <summary>A linear or radial gradient: stops and geometry in gradient space and the matrix that places them.</summary>
internal sealed class RGradientPaint : RPaint
{
    public required bool Radial { get; init; }

    public required ColorStop[] Stops { get; init; }

    public required GradientRepetition Spread { get; init; }

    /// <summary>The coordinate system the gradient lives in: the painted element's (or the context element's) user space in scene space.</summary>
    public required Affine BaseCtm { get; init; }

    public required Affine GradientTransform { get; init; }

    public required BBoxHolder? BBox { get; init; }

    // Linear: (X1,Y1)-(X2,Y2). Radial: center (Cx,Cy), radius R, focal (Fx,Fy), focal radius Fr. Fractions of the box in bounding box units.
    public double X1 { get; init; }

    public double Y1 { get; init; }

    public double X2 { get; init; }

    public double Y2 { get; init; }

    public double R { get; init; }

    public double Fx { get; init; }

    public double Fy { get; init; }

    public double Fr { get; init; }

    public override Brush? CreateBrush(Affine dev)
    {
        Affine m = dev.Multiply(BaseCtm);
        if (BBox is { } box)
        {
            if (!box.IsUsable)
            {
                return null;
            }

            m = m.Multiply(new Affine(box.Width, 0, 0, box.Height, box.X, box.Y));
        }

        m = m.Multiply(GradientTransform);
        if (!TryMatrix(m, out Matrix3x2 matrix))
        {
            return null;
        }

        // SVG interpolates straight (non-premultiplied) colors.
        return Radial
            ? Brush.RadialGradient(new Vector2(Coord.F(X1), Coord.F(Y1)), Coord.F(R), new Vector2(Coord.F(Fx), Coord.F(Fy)), Coord.F(Fr), Stops, matrix, Spread, premultiplied: false)
            : Brush.LinearGradient(new Vector2(Coord.F(X1), Coord.F(Y1)), new Vector2(Coord.F(X2), Coord.F(Y2)), Stops, matrix, Spread, premultiplied: false);
    }
}

/// <summary>
/// A pattern: its content is drawn once into a tile image at the resolution the pattern is seen at, and the tile repeats through a
/// bilinear image brush placed by the pattern's matrix. Tile images are kept for the scales they were made at.
/// </summary>
internal sealed class RPatternPaint : RPaint
{
    private const int MaxCachedTiles = 2;

    [ThreadStatic]
    private static int s_depth;

    private readonly List<(int Width, int Height, double ScaleX, double ScaleY, Canvas Canvas, Image Image)> _tiles = [];

    public required RNode[] Content { get; init; }

    public required Affine BaseCtm { get; init; }

    /// <summary>The pattern's own transform (<c>patternTransform</c>).</summary>
    public required Affine PatternTransform { get; init; }

    /// <summary>The tile rectangle in pattern space; fractions of the bounding box when <see cref="TileInBox"/>.</summary>
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public required bool TileInBox { get; init; }

    /// <summary>Content coordinates are fractions of the bounding box (no view box).</summary>
    public required bool ContentInBox { get; init; }

    public required bool HasViewBox { get; init; }

    public double ViewX { get; init; }

    public double ViewY { get; init; }

    public double ViewWidth { get; init; }

    public double ViewHeight { get; init; }

    public AspectRatio Aspect { get; init; }

    public required BBoxHolder? BBox { get; init; }

    public required SvgLimits Limits { get; init; }

    private const double MaxTileSide = 16384;

    public override Brush? CreateBrush(Affine dev)
    {
        double tx = X, ty = Y, tw = Width, th = Height;
        if (BBox is { } box)
        {
            if (!box.IsUsable)
            {
                return null;
            }

            if (TileInBox)
            {
                tx = box.X + (tx * box.Width);
                ty = box.Y + (ty * box.Height);
                tw *= box.Width;
                th *= box.Height;
            }
        }

        if (!(tw > 0) || !(th > 0) || !double.IsFinite(tx + ty + tw + th))
        {
            return null;
        }

        Affine contentMatrix = Affine.Identity;
        if (HasViewBox)
        {
            contentMatrix = Aspect.Fit(ViewX, ViewY, ViewWidth, ViewHeight, tw, th);
        }
        else if (ContentInBox && BBox is { } cb)
        {
            contentMatrix = Affine.Scale(cb.Width, cb.Height);
        }

        if (!contentMatrix.IsInvertible)
        {
            return null;
        }

        Affine mp = dev.Multiply(BaseCtm).Multiply(PatternTransform);
        if (!mp.IsFinite || !(Math.Abs(mp.Determinant) > 1e-20))
        {
            return null;
        }

        double sx = Math.Sqrt((mp.A * mp.A) + (mp.B * mp.B));
        double sy = Math.Sqrt((mp.C * mp.C) + (mp.D * mp.D));
        double fw = tw * sx, fh = th * sy;
        if (!(fw > 0) || !(fh > 0) || !double.IsFinite(fw + fh))
        {
            return null;
        }

        // Each level of nesting gets half the pixels of the one outside it, so nested tiles stay within about twice the limit.
        double budget = Limits.MaxPatternTilePixels / (double)(1L << Math.Min(s_depth, 20));
        double w = Math.Ceiling(fw - 1e-4), h = Math.Ceiling(fh - 1e-4);
        // A very long thin tile (a pattern skewed by 90 degrees scales one axis by 10^16) is cheap in pixels but costs a render pass per 128-row
        // band, so each side is also kept to a fixed length; the tile is then drawn coarser along that axis.
        w = Math.Clamp(w, 1, MaxTileSide);
        h = Math.Clamp(h, 1, MaxTileSide);
        double scaleX = sx, scaleY = sy;
        if (w * h > budget)
        {
            // Past the limit the tile is made at a coarser resolution rather than refused, so zooming into a pattern still draws it.
            double k = Math.Sqrt(budget / (w * h));
            w = Math.Max(Math.Floor(w * k), 1);
            h = Math.Max(Math.Floor(h * k), 1);
            while (w * h > budget && (w > 1 || h > 1))
            {
                w = Math.Max(w - 1, 1);
                h = Math.Max(h - 1, 1);
            }
        }

        int pw = (int)w, ph = (int)h;
        if (w < Math.Ceiling(fw - 1e-4) || h < Math.Ceiling(fh - 1e-4))
        {
            // A coarser tile fills its pixels exactly.
            scaleX = pw / tw;
            scaleY = ph / th;
        }

        // The content is drawn at the scale it is seen at, so its edges stay as sharp as a direct drawing; only the tile's size is rounded up
        // to whole pixels, and the brush stretches that back to the exact period.
        Image tile = GetTile(pw, ph, scaleX, scaleY, contentMatrix);

        // Tile pixel -> tile space -> pattern space -> device.
        Affine m = mp.Multiply(Affine.Translate(tx, ty)).Multiply(Affine.Scale(tw / pw, th / ph));
        if (!TryMatrix(m, out Matrix3x2 brushMatrix))
        {
            return null;
        }

        // The tile is drawn at the resolution it is seen at, so without rotation or skew each pixel takes its own tile pixel (smoothing would only
        // blur the tile's edges at fractional offsets); a turned pattern is smoothed.
        bool axisAligned = Math.Abs(m.B) < 1e-6 * Math.Abs(m.A) + 1e-12 && Math.Abs(m.C) < 1e-6 * Math.Abs(m.D) + 1e-12;
        return Brush.FromImage(tile, axisAligned ? ImageSampling.Nearest : ImageSampling.Bilinear, transform: brushMatrix, tile: true);
    }

    private Image GetTile(int pw, int ph, double scaleX, double scaleY, Affine contentMatrix)
    {
        lock (_tiles)
        {
            foreach ((int w, int h, double kx, double ky, Canvas _, Image image) in _tiles)
            {
                if (w == pw && h == ph && Math.Abs(kx - scaleX) < 1e-9 * scaleX && Math.Abs(ky - scaleY) < 1e-9 * scaleY)
                {
                    return image;
                }
            }

            // The canvas stays referenced with its snapshot; a Canvas is safe to leave to the garbage collector.
            var canvas = new Canvas(pw, ph, PixelFormat.Rgba8);
            Affine dev = Affine.Scale(scaleX, scaleY).Multiply(contentMatrix);
            double pad = 2 / Math.Max(Math.Min(Math.Sqrt((dev.A * dev.A) + (dev.B * dev.B)), Math.Sqrt((dev.C * dev.C) + (dev.D * dev.D))), 1e-9);
            (double qx0, double qy0, double qx1, double qy1) = Invert(contentMatrix).TransformRect(-pad, -pad, (pw / scaleX) + pad, (ph / scaleY) + pad);
            s_depth++;
            try
            {
                foreach (RNode node in Content)
                {
                    SceneRenderer.DrawNode(canvas, node, dev, qx0, qy0, qx1, qy1, 0, Limits);
                }
            }
            finally
            {
                s_depth--;
            }

            Image snapshot = canvas.Snapshot();
            if (_tiles.Count >= MaxCachedTiles)
            {
                _tiles.RemoveAt(0);
            }

            _tiles.Add((pw, ph, scaleX, scaleY, canvas, snapshot));
            return snapshot;
        }
    }

    internal static Affine Invert(Affine m)
    {
        double det = m.Determinant;
        double a = m.D / det, b = -m.B / det, c = -m.C / det, d = m.A / det;
        return new Affine(a, b, c, d, -((a * m.E) + (c * m.F)), -((b * m.E) + (d * m.F)));
    }
}
