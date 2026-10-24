using System.Buffers;

namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Mirrors the image (lazy, exact: pixels move, nothing is resampled). Each output tile reads one input tile region, so only the
    /// tiles that are needed are computed. On a streaming (sequential-decode) source a horizontal flip streams; a vertical flip needs
    /// the last rows first, so the decoder is restarted and the frame ends up fully decoded once.
    /// </summary>
    /// <param name="axis">The axis to mirror.</param>
    /// <returns>The flipped image; every frame of an animation is flipped.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis"/> is not a defined value.</exception>
    public Image Flip(FlipAxis axis)
    {
        if (!Enum.IsDefined(axis))
        {
            throw new ArgumentOutOfRangeException(nameof(axis));
        }

        return Map(static (n, o) => OrientNode.Create(n, o), axis == FlipAxis.Horizontal ? 2 : 4);
    }

    /// <summary>
    /// Rotates by an angle in degrees, clockwise as seen on screen (lazy).
    /// <para>
    /// <b>Multiples of 90</b> (after reducing modulo 360) are exact pixel moves: no resampling, no background, and the filter,
    /// edge and background arguments are ignored. They are tile-local, but the turns read the source out of order (a 90° turn
    /// builds the first output rows from a whole column of the input; 180° reads bottom-up), so on a streaming source the decoder
    /// restarts and the frame ends up fully decoded once.
    /// </para>
    /// <para>
    /// <b>Other angles</b> resample with <see cref="Affine"/> about the image center. The output grows to the bounding box of the
    /// rotated image (<paramref name="expand"/>, the default): width and height are the extents rounded up (a 1e-6 tolerance keeps
    /// exact results exact), the rotated image touches the left and top edges, and uncovered corners are the background. With
    /// <paramref name="expand"/> false the output keeps the input size and the corners are cropped. Pixels whose taps reach beyond
    /// the source blend with the background (constant edge), which gives smooth antialiased borders. A rotation by a small angle
    /// reads rows spread over about width times tan(angle) pixels, so it only streams for small angles or narrow images.
    /// </para>
    /// </summary>
    /// <param name="degrees">Angle in degrees; positive turns clockwise. Any finite value.</param>
    /// <param name="expand">Grow the output to hold the whole rotated image. Defaults to <see langword="true"/>.</param>
    /// <param name="background">Fill for uncovered areas, as straight sRGB; defaults to transparent black. Formats without alpha
    /// drop the alpha (black by default); CMYK and Lab use zero samples.</param>
    /// <param name="filter">Reconstruction filter; <see cref="ResamplingFilter.Auto"/> is Catmull-Rom. See <see cref="Affine"/>.</param>
    /// <param name="edge">What lies beyond the image edge; defaults to <see cref="EdgeMode.Constant"/> (the background).</param>
    /// <param name="linear">Interpolate in linear light with premultiplied alpha, as <see cref="Resize"/> does. Defaults to <see langword="true"/>.</param>
    /// <returns>The rotated image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degrees"/> is not finite or an enum value is undefined.</exception>
    public Image Rotate(double degrees, bool expand = true, Rgba32 background = default, ResamplingFilter filter = ResamplingFilter.Auto, EdgeMode edge = EdgeMode.Constant, bool linear = true)
    {
        if (!double.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), "Angle must be finite.");
        }

        CheckSampling(filter, edge);
        double turns = degrees % 360.0;
        if (turns < 0)
        {
            turns += 360.0;
        }

        if (turns == 0)
        {
            return this;
        }

        if (turns is 90.0 or 180.0 or 270.0)
        {
            return Map(static (n, o) => OrientNode.Create(n, o), turns == 90.0 ? 6 : turns == 180.0 ? 3 : 8);
        }

        double rad = turns * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        return Map(
            static (n, a) =>
            {
                double cx = n.Width / 2.0;
                double cy = n.Height / 2.0;
                double tx = cx - ((a.Cos * cx) - (a.Sin * cy));
                double ty = cy - ((a.Sin * cx) + (a.Cos * cy));
                return BuildAffine(n, a.Cos, -a.Sin, a.Sin, a.Cos, tx, ty, a.Expand, a.Filter, a.Edge, a.Background, a.Linear);
            },
            (Cos: cos, Sin: sin, Expand: expand, Filter: filter, Edge: edge, Background: background, Linear: linear));
    }

    /// <summary>
    /// Applies an affine transform (lazy): a source point (x, y), in pixel-edge coordinates where pixel (i, j) covers [i, i+1) x [j, j+1),
    /// moves to <c>(a·x + b·y + tx, c·x + d·y + ty)</c>.
    /// <para>
    /// <b>Output bounds.</b> With <paramref name="expand"/> (the default) the output is the bounding box of the transformed source
    /// rectangle: its top-left corner is the box's minimum corner (so a pure translation changes nothing about the output and the
    /// image is not shifted), and its size is the extent rounded up, with a 1e-6 tolerance. With <paramref name="expand"/> false the
    /// output has the input size and the transform is applied as given, so translation shifts the image and parts leave the frame.
    /// </para>
    /// <para>
    /// <b>Sampling.</b> Each output pixel center is mapped back into the source and reconstructed with a separable filter (2 taps for
    /// bilinear, 4 for the cubics and Lanczos2, 6 for Lanczos3 and Welch, 10 for Lanczos5, 16 for Lanczos8), weights normalized to 1, positions computed in double
    /// precision, summation in float32 in a fixed order. <see cref="ResamplingFilter.Nearest"/> and <see cref="ResamplingFilter.Box"/>
    /// pick the single nearest source pixel; <see cref="ResamplingFilter.Auto"/> is Catmull-Rom. The kernels are not widened, so there
    /// is no prefiltering: for reductions beyond about 2× resize first. Taps beyond the source follow <paramref name="edge"/>; with
    /// the constant edge they read the background, so borders blend smoothly into it. Results are rounded half up and clamped, and
    /// are identical for every thread count and tile order. With <paramref name="linear"/> (default) values are linearized as
    /// sRGB and premultiplied by alpha before interpolation, as in <see cref="Resize"/>.
    /// </para>
    /// <para>
    /// Each output tile reads only the source rectangle its taps reach. Streaming sources stream when that rectangle stays within a
    /// few tile rows of the tile's own row (small skews and shears); otherwise the decoder restarts and the frame is decoded once.
    /// </para>
    /// </summary>
    /// <param name="a">Matrix entry: x contribution to the new x.</param>
    /// <param name="b">Matrix entry: y contribution to the new x.</param>
    /// <param name="c">Matrix entry: x contribution to the new y.</param>
    /// <param name="d">Matrix entry: y contribution to the new y.</param>
    /// <param name="tx">Translation added to x.</param>
    /// <param name="ty">Translation added to y.</param>
    /// <param name="expand">Grow the output to the transformed bounds. Defaults to <see langword="true"/>.</param>
    /// <param name="background">Fill for uncovered areas, straight sRGB; defaults to transparent black.</param>
    /// <param name="filter">Reconstruction filter. Defaults to <see cref="ResamplingFilter.Auto"/> (Catmull-Rom).</param>
    /// <param name="edge">What lies beyond the image edge. Defaults to <see cref="EdgeMode.Constant"/>.</param>
    /// <param name="linear">Interpolate in linear light. Defaults to <see langword="true"/>.</param>
    /// <returns>The transformed image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite or an enum value is undefined.</exception>
    /// <exception cref="ImageOperationException">The matrix is singular or the pixel format cannot be resampled.</exception>
    /// <exception cref="ImageLimitException">The output would exceed the engine's size limits.</exception>
    public Image Affine(double a, double b, double c, double d, double tx = 0, double ty = 0, bool expand = true, Rgba32 background = default, ResamplingFilter filter = ResamplingFilter.Auto, EdgeMode edge = EdgeMode.Constant, bool linear = true)
    {
        foreach (double v in (ReadOnlySpan<double>)[a, b, c, d, tx, ty])
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentOutOfRangeException(nameof(a), "Matrix values must be finite.");
            }
        }

        CheckSampling(filter, edge);
        return Map(
            static (n, p) => BuildAffine(n, p.A, p.B, p.C, p.D, p.Tx, p.Ty, p.Expand, p.Filter, p.Edge, p.Background, p.Linear),
            (A: a, B: b, C: c, D: d, Tx: tx, Ty: ty, Expand: expand, Filter: filter, Edge: edge, Background: background, Linear: linear));
    }

    /// <summary>
    /// Adds a border (lazy). The image keeps its place at (<paramref name="left"/>, <paramref name="top"/>) in the larger canvas.
    /// With <see cref="EdgeMode.Constant"/> the border is <paramref name="background"/>; the other modes continue the image
    /// (clamp repeats the edge pixels, mirror reflects with the edge pixel repeated, wrap tiles the image), exactly, with any
    /// border size. Pixels are copied, never resampled. Only tiles that touch the image read it. Constant and clamp borders keep
    /// streaming sources streaming; mirror and wrap read the image from other places (mirror/wrap at the top or bottom reads rows
    /// out of order and makes the decoder restart).
    /// </summary>
    /// <param name="left">Columns added on the left, zero or more.</param>
    /// <param name="top">Rows added on top, zero or more.</param>
    /// <param name="right">Columns added on the right, zero or more.</param>
    /// <param name="bottom">Rows added at the bottom, zero or more.</param>
    /// <param name="mode">How the border is filled. Defaults to <see cref="EdgeMode.Constant"/>.</param>
    /// <param name="background">Border color for the constant mode, straight sRGB; defaults to transparent black. Formats without
    /// alpha drop the alpha (black by default); CMYK and Lab use zero samples.</param>
    /// <returns>The padded image; every frame of an animation is padded.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is negative or <paramref name="mode"/> is undefined.</exception>
    /// <exception cref="ImageLimitException">The result would exceed the engine's size limits.</exception>
    public Image Pad(int left = 0, int top = 0, int right = 0, int bottom = 0, EdgeMode mode = EdgeMode.Constant, Rgba32 background = default)
    {
        if (left < 0 || top < 0 || right < 0 || bottom < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(left), "Border sizes cannot be negative.");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return Map(
            static (n, p) =>
            {
                long w = (long)n.Width + p.Left + p.Right;
                long h = (long)n.Height + p.Top + p.Bottom;
                if (w > int.MaxValue || h > int.MaxValue)
                {
                    throw new ImageLimitException("Padded size is too large.");
                }

                return w == n.Width && h == n.Height ? n : new ExtendNode(n, p.Left, p.Top, (int)w, (int)h, p.Mode, p.Background);
            },
            (Left: left, Top: top, Right: right, Bottom: bottom, Mode: mode, Background: background));
    }

    /// <summary>
    /// Rotates and flips the pixels so that an image stored with the given EXIF orientation (1 to 8) is shown upright (lazy, exact).
    /// 1 does nothing, 2 mirrors horizontally, 3 rotates 180°, 4 mirrors vertically, 5 transposes, 6 rotates 90° clockwise, 7 is the
    /// anti-diagonal transpose and 8 rotates 90° counterclockwise. The orientation is an argument here so the caller decides where
    /// it comes from. Does not touch the metadata.
    /// </summary>
    /// <param name="orientation">EXIF orientation value, 1 to 8.</param>
    /// <returns>The reoriented image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="orientation"/> is outside 1 to 8.</exception>
    public Image Orient(int orientation)
    {
        if (orientation is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(orientation), "Orientation must be 1 to 8.");
        }

        return Map(static (n, o) => OrientNode.Create(n, o), orientation);
    }

    /// <summary>
    /// Applies <see cref="ImageMetadata.Orientation"/> (see <see cref="Orient"/>) and resets it to 1, so applying it twice is harmless.
    /// Lazy. <see cref="Load(string,LoadOptions?,CancellationToken)"/> does this by default (<see cref="LoadOptions.AutoOrient"/>); call
    /// this on images loaded with <c>AutoOrient = false</c>, or built by hand with a recorded orientation. The EXIF blob keeps its old
    /// tag until the image is saved, when it is rewritten to the current orientation and size.
    /// </summary>
    /// <returns>The upright image, or this image when the orientation is already 1.</returns>
    public Image AutoOrient()
    {
        int orientation = Metadata.Orientation;
        if (orientation is < 2 or > 8)
        {
            return this;
        }

        var nodes = new Node[_frames.Length];
        var delays = new TimeSpan[_frames.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = OrientNode.Create(_frames[i].Node, orientation);
            delays[i] = _frames[i].Delay;
        }

        ImageMetadata reset = Metadata.WithOrientation(1);
        return new Image(nodes, delays, LoopCount, reset, SourceFormat);
    }

    /// <summary>
    /// Removes a uniform border (eager: the pixels are scanned now, in bands of rows, and the result is a lazy crop of the image).
    /// A pixel belongs to the border when every channel, including alpha, is within <paramref name="threshold"/> of the background
    /// on a 0 to 255 scale (the image is compared as 8-bit RGBA; pixels that are fully transparent on both sides always match).
    /// The result is the smallest rectangle holding every other pixel. For animations the union over all frames is used so frames
    /// stay aligned. If every pixel matches the background, the result is the single top-left pixel. The scan reads the image once
    /// from the top, so it works on streaming sources in bounded memory; the crop afterwards decodes again as needed.
    /// </summary>
    /// <param name="threshold">Largest per-channel difference still counted as background, 0 to 255. Defaults to 10.</param>
    /// <param name="background">The border color (straight sRGB), or <see langword="null"/> to use the top-left pixel of each frame.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The trimmed image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0 to 255.</exception>
    /// <exception cref="ImageOperationException">The format is CMYK or Lab.</exception>
    public Image Trim(int threshold = 10, Rgba32? background = null, CancellationToken cancellationToken = default)
    {
        if (threshold is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be 0 to 255.");
        }

        int x0 = int.MaxValue;
        int y0 = int.MaxValue;
        int x1 = -1;
        int y1 = -1;
        int minW = int.MaxValue;
        int minH = int.MaxValue;
        RenderContext context = RenderContext.Create(0, cancellationToken);
        foreach (ImageFrame f in _frames)
        {
            ImageRect box = TrimScan.Find(f.Node, background, threshold, context);
            if (!box.IsEmpty)
            {
                x0 = Math.Min(x0, box.X);
                y0 = Math.Min(y0, box.Y);
                x1 = Math.Max(x1, box.Right);
                y1 = Math.Max(y1, box.Bottom);
            }

            minW = Math.Min(minW, f.Width);
            minH = Math.Min(minH, f.Height);
        }

        if (x1 < 0)
        {
            return Crop(0, 0, 1, 1);
        }

        return Crop(x0, y0, Math.Min(x1, minW) - x0, Math.Min(y1, minH) - y0);
    }

    private static void CheckSampling(ResamplingFilter filter, EdgeMode edge)
    {
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }

        if (!Enum.IsDefined(edge))
        {
            throw new ArgumentOutOfRangeException(nameof(edge));
        }
    }

    private static AffineNode BuildAffine(Node n, double a, double b, double c, double d, double tx, double ty, bool expand, ResamplingFilter filter, EdgeMode edge, Rgba32 background, bool linear)
    {
        ResamplingFilter f = filter == ResamplingFilter.Auto ? ResamplingFilter.CatmullRom : filter;
        double ox = 0;
        double oy = 0;
        int w = n.Width;
        int h = n.Height;
        if (expand)
        {
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            foreach ((double x, double y) in new[] { (0.0, 0.0), (n.Width, 0.0), (0.0, n.Height), ((double)n.Width, n.Height) })
            {
                double X = (a * x) + (b * y) + tx;
                double Y = (c * x) + (d * y) + ty;
                minX = Math.Min(minX, X);
                maxX = Math.Max(maxX, X);
                minY = Math.Min(minY, Y);
                maxY = Math.Max(maxY, Y);
            }

            ox = minX;
            oy = minY;
            double fw = Math.Max(1.0, Math.Ceiling(maxX - minX - 1e-6));
            double fh = Math.Max(1.0, Math.Ceiling(maxY - minY - 1e-6));
            if (fw > int.MaxValue || fh > int.MaxValue)
            {
                throw new ImageLimitException("Transformed size is too large.");
            }

            w = (int)fw;
            h = (int)fh;
        }

        return new AffineNode(n, a, b, c, d, tx, ty, ox, oy, w, h, f, edge, background, linear);
    }
}

/// <summary>Finds the box of pixels that differ from the border color.</summary>
internal static class TrimScan
{
    public static ImageRect Find(Node node, Rgba32? background, int threshold, RenderContext context)
    {
        if (node.Format.Model is not (ColorModel.Rgb or ColorModel.Gray))
        {
            throw new ImageOperationException("Trim works on RGB and gray images.");
        }

        int w = node.Width;
        const int Band = Tile.Size;
        byte[] buf = Pools<byte>.Pool.Rent(w * Band * 4);
        try
        {
            Rgba32 bg = background ?? default;
            if (background is null)
            {
                Render.ReadRegion(node, new ImageRect(0, 0, 1, 1), buf, 4, PixelFormat.Rgba8, context);
                bg = new Rgba32(buf[0], buf[1], buf[2], buf[3]);
            }

            int minX = int.MaxValue;
            int maxX = -1;
            int minY = int.MaxValue;
            int maxY = -1;
            for (int y = 0; y < node.Height; y += Band)
            {
                int rows = Math.Min(Band, node.Height - y);
                Render.ReadRegion(node, new ImageRect(0, y, w, rows), buf, w * 4, PixelFormat.Rgba8, context);
                for (int r = 0; r < rows; r++)
                {
                    ReadOnlySpan<byte> row = buf.AsSpan(r * w * 4, w * 4);
                    int first = -1;
                    for (int x = 0; x < w; x++)
                    {
                        if (!IsBackground(row.Slice(x * 4, 4), bg, threshold))
                        {
                            first = x;
                            break;
                        }
                    }

                    if (first < 0)
                    {
                        continue;
                    }

                    int last = first;
                    for (int x = w - 1; x > first; x--)
                    {
                        if (!IsBackground(row.Slice(x * 4, 4), bg, threshold))
                        {
                            last = x;
                            break;
                        }
                    }

                    minX = Math.Min(minX, first);
                    maxX = Math.Max(maxX, last);
                    minY = Math.Min(minY, y + r);
                    maxY = Math.Max(maxY, y + r);
                }
            }

            return maxX < 0 ? new ImageRect(0, 0, 0, 0) : new ImageRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
        finally
        {
            Pools<byte>.Pool.Return(buf);
        }
    }

    private static bool IsBackground(ReadOnlySpan<byte> p, Rgba32 bg, int threshold)
    {
        if (p[3] == 0 && bg.A == 0)
        {
            return true;
        }

        return Math.Abs(p[0] - bg.R) <= threshold && Math.Abs(p[1] - bg.G) <= threshold && Math.Abs(p[2] - bg.B) <= threshold && Math.Abs(p[3] - bg.A) <= threshold;
    }
}
