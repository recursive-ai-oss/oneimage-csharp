namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Applies a projective (perspective) transform (lazy): a source point (x, y), in pixel-edge coordinates where pixel (i, j) covers
    /// [i, i+1) x [j, j+1), moves to the point <see cref="ProjectiveMatrix.Transform"/> gives. With the bottom row (0, 0, 1) this is
    /// <see cref="Affine"/>.
    /// <para>
    /// <b>Output bounds.</b> With <paramref name="expand"/> (the default) the output is the bounding box of the transformed source
    /// rectangle: its top-left corner is the box's minimum corner (so a pure translation changes nothing about the output and the
    /// image is not shifted; ImageSharp keeps the offset instead), and its size is the extent rounded up, with a 1e-6 tolerance.
    /// With <paramref name="expand"/> false the output has the input size and the transform is applied as given.
    /// </para>
    /// <para>
    /// <b>Valid matrices.</b> The matrix must be finite and invertible, and the four corners of the source rectangle must all lie on
    /// the same side of the horizon (the line where <c>w = M31·x + M32·y + M33</c> is zero); a matrix that sends part of the image through
    /// the horizon ("behind the camera") has no meaningful picture and throws <see cref="ImageOperationException"/>, when pixels are
    /// pulled for a lazily built image. A matrix whose <c>w</c> is negative everywhere is flipped to its positive multiple. Output pixels
    /// whose source point would lie behind the horizon (possible when <paramref name="expand"/> is false) get the background.
    /// An output larger than the engine's size limits throws <see cref="ImageLimitException"/>.
    /// </para>
    /// <para>
    /// <b>Sampling.</b> As in <see cref="Affine"/>: each output pixel center is mapped back into the source in double precision and
    /// reconstructed with a separable filter normalized to 1 (2 taps for bilinear, 4 for the cubics and Lanczos2, 6 for Lanczos3, 10
    /// for Lanczos5, 16 for Lanczos8), summed in float32 in a fixed order without fused multiply-add. The kernels are not widened, so there is no
    /// prefiltering: for a reduction beyond about 2× resize first. <see cref="ResamplingFilter.Auto"/> is Catmull-Rom;
    /// <see cref="ResamplingFilter.Nearest"/> and <see cref="ResamplingFilter.Box"/> pick the nearest source pixel. Taps beyond the
    /// source follow <paramref name="edge"/>. Results are rounded half up and clamped, and are identical for every thread count
    /// and tile order. With <paramref name="linear"/> (default) values are linearized as sRGB and premultiplied by alpha first.
    /// </para>
    /// <para>
    /// Each output tile reads exactly the source rectangle its taps reach (computed from the tile's corners, cut at the horizon).
    /// Streaming sources stream when that rectangle stays within a few tile rows of the tile's own row; otherwise the decoder
    /// restarts and the frame is decoded once.
    /// </para>
    /// </summary>
    /// <param name="matrix">The transform from source to destination coordinates.</param>
    /// <param name="expand">Grow the output to the transformed bounds. Defaults to <see langword="true"/>.</param>
    /// <param name="background">Fill for uncovered areas, straight sRGB; defaults to transparent black.</param>
    /// <param name="filter">Reconstruction filter. Defaults to <see cref="ResamplingFilter.Auto"/> (Catmull-Rom).</param>
    /// <param name="edge">What lies beyond the image edge. Defaults to <see cref="EdgeMode.Constant"/>.</param>
    /// <param name="linear">Interpolate in linear light. Defaults to <see langword="true"/>.</param>
    /// <returns>The transformed image; every frame of an animation is transformed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A matrix value is not finite or an enum value is undefined.</exception>
    public Image Perspective(ProjectiveMatrix matrix, bool expand = true, Rgba32 background = default, ResamplingFilter filter = ResamplingFilter.Auto, EdgeMode edge = EdgeMode.Constant, bool linear = true)
    {
        CheckMatrix(matrix);
        CheckSampling(filter, edge);
        return Map(
            static (n, p) => BuildPerspective(n, p.Matrix, p.Expand, p.Filter, p.Edge, p.Background, p.Linear),
            (Matrix: matrix, Expand: expand, Filter: filter, Edge: edge, Background: background, Linear: linear));
    }

    /// <summary>
    /// Applies the transform a <see cref="ProjectiveTransformBuilder"/> describes (lazy), evaluated for the size of each frame.
    /// The steps are copied now; changing the builder later does not affect this image. Everything else is as in
    /// <see cref="Perspective(ProjectiveMatrix, bool, Rgba32, ResamplingFilter, EdgeMode, bool)"/>.
    /// </summary>
    /// <param name="builder">The steps.</param>
    /// <param name="expand">Grow the output to the transformed bounds. Defaults to <see langword="true"/>.</param>
    /// <param name="background">Fill for uncovered areas, straight sRGB; defaults to transparent black.</param>
    /// <param name="filter">Reconstruction filter. Defaults to <see cref="ResamplingFilter.Auto"/> (Catmull-Rom).</param>
    /// <param name="edge">What lies beyond the image edge. Defaults to <see cref="EdgeMode.Constant"/>.</param>
    /// <param name="linear">Interpolate in linear light. Defaults to <see langword="true"/>.</param>
    /// <returns>The transformed image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is undefined.</exception>
    public Image Perspective(ProjectiveTransformBuilder builder, bool expand = true, Rgba32 background = default, ResamplingFilter filter = ResamplingFilter.Auto, EdgeMode edge = EdgeMode.Constant, bool linear = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        CheckSampling(filter, edge);
        return Map(
            static (n, p) => BuildPerspective(n, p.Steps(n.Width, n.Height), p.Expand, p.Filter, p.Edge, p.Background, p.Linear),
            (Steps: builder.Freeze(), Expand: expand, Filter: filter, Edge: edge, Background: background, Linear: linear));
    }

    /// <summary>
    /// Pins the four corners of the image to four points (lazy): top-left, top-right, bottom-right, bottom-left, in destination
    /// coordinates. Shorthand for <see cref="ProjectiveMatrix.FromRectangle"/> followed by
    /// <see cref="Perspective(ProjectiveMatrix, bool, Rgba32, ResamplingFilter, EdgeMode, bool)"/>, evaluated for the size of each
    /// frame. With <paramref name="expand"/> (the default) the output is the bounding box of the quadrilateral, shifted so its
    /// minimum corner is the origin.
    /// </summary>
    /// <param name="corners">The four destination points, a convex quadrilateral in clockwise screen order.</param>
    /// <param name="expand">Grow the output to the transformed bounds. Defaults to <see langword="true"/>.</param>
    /// <param name="background">Fill for uncovered areas, straight sRGB; defaults to transparent black.</param>
    /// <param name="filter">Reconstruction filter. Defaults to <see cref="ResamplingFilter.Auto"/> (Catmull-Rom).</param>
    /// <param name="edge">What lies beyond the image edge. Defaults to <see cref="EdgeMode.Constant"/>.</param>
    /// <param name="linear">Interpolate in linear light. Defaults to <see langword="true"/>.</param>
    /// <returns>The transformed image.</returns>
    /// <exception cref="ArgumentException">The points are not four finite points of a convex quadrilateral.</exception>
    public Image Perspective(ReadOnlySpan<(double X, double Y)> corners, bool expand = true, Rgba32 background = default, ResamplingFilter filter = ResamplingFilter.Auto, EdgeMode edge = EdgeMode.Constant, bool linear = true)
    {
        (double X, double Y)[] copy = corners.ToArray();
        _ = ProjectiveMatrix.FromRectangle(1, 1, copy);
        CheckSampling(filter, edge);
        return Map(
            static (n, p) => BuildPerspective(n, ProjectiveMatrix.FromRectangle(n.Width, n.Height, p.Corners), p.Expand, p.Filter, p.Edge, p.Background, p.Linear),
            (Corners: copy, Expand: expand, Filter: filter, Edge: edge, Background: background, Linear: linear));
    }

    private static void CheckMatrix(ProjectiveMatrix m)
    {
        foreach (double v in (ReadOnlySpan<double>)[m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33])
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentOutOfRangeException(nameof(m), "Matrix values must be finite.");
            }
        }
    }

    private static ProjectiveNode BuildPerspective(Node n, ProjectiveMatrix m, bool expand, ResamplingFilter filter, EdgeMode edge, Rgba32 background, bool linear)
    {
        ResamplingFilter f = filter == ResamplingFilter.Auto ? ResamplingFilter.CatmullRom : filter;
        double sw = n.Width;
        double sh = n.Height;

        // w is linear in (x, y), so it keeps one sign over the whole rectangle exactly when it does at the four corners.
        ReadOnlySpan<(double X, double Y)> corners = [(0, 0), (sw, 0), (sw, sh), (0, sh)];
        int positive = 0;
        int negative = 0;
        foreach ((double x, double y) in corners)
        {
            double w = (m.M31 * x) + (m.M32 * y) + m.M33;
            if (w > 0)
            {
                positive++;
            }
            else if (w < 0)
            {
                negative++;
            }
        }

        if (positive != 4 && negative != 4)
        {
            throw new ImageOperationException("The perspective transform sends part of the image through the horizon (w is zero or changes sign over the image).");
        }

        if (negative == 4)
        {
            m = new ProjectiveMatrix(-m.M11, -m.M12, -m.M13, -m.M21, -m.M22, -m.M23, -m.M31, -m.M32, -m.M33);
        }

        double ox = 0;
        double oy = 0;
        int ow = n.Width;
        int oh = n.Height;
        if (expand)
        {
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            foreach ((double x, double y) in corners)
            {
                (double X, double Y) = m.Transform(x, y);
                minX = Math.Min(minX, X);
                maxX = Math.Max(maxX, X);
                minY = Math.Min(minY, Y);
                maxY = Math.Max(maxY, Y);
            }

            double fw = Math.Max(1.0, Math.Ceiling(maxX - minX - 1e-6));
            double fh = Math.Max(1.0, Math.Ceiling(maxY - minY - 1e-6));
            if (!(fw <= int.MaxValue) || !(fh <= int.MaxValue))
            {
                throw new ImageLimitException("Transformed size is too large.");
            }

            ox = minX;
            oy = minY;
            ow = (int)fw;
            oh = (int)fh;
        }

        return new ProjectiveNode(n, m, ox, oy, ow, oh, f, edge, background, linear);
    }
}
