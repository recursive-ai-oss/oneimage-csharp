namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Mosaic (lazy): the image is cut into <paramref name="size"/> × <paramref name="size"/> blocks starting at the top-left
    /// corner, and every block takes the color of one pixel, the one at <c>block start + size / 2</c> (the last pixel when that lies
    /// beyond the edge, so partial blocks at the right and bottom use their nearest pixel). Nothing is averaged: the output holds
    /// only pixel values that were in the input, in every pixel format. Animated images are pixelated frame by frame.
    /// </summary>
    /// <param name="size">Block size in pixels, at least 1 (1 returns the image unchanged). Defaults to 4.</param>
    /// <returns>The pixelated image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is below 1.</exception>
    public Image Pixelate(int size = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        return size == 1 ? this : Map(static (n, s) => new PixelateNode(n, s), size);
    }

    /// <summary>
    /// Oil-paint effect (lazy) for gray and RGB images. For every pixel the window of <paramref name="brushSize"/> × <paramref name="brushSize"/>
    /// pixels around it (centered for odd sizes; an even size reaches one pixel further up and left) is sorted into
    /// <paramref name="levels"/> intensity bins by the mean of red, green and blue, and the pixel becomes the mean color of the
    /// fullest bin (the brightest bin wins a tie). Pixels beyond the edge repeat the edge pixel. Alpha is kept. The result depends only on stored values, with no color-space
    /// conversion; non-8-bit images are processed at 16-bit precision.
    /// <para>
    /// ImageSharp's version looks at a window that reaches only from the pixel up and to the left (<c>brushSize / 2 + 1</c> pixels
    /// wide), so its result is shifted down and right by about a quarter of the brush size and its brush is about half as wide. This one is centered,
    /// so for the same <paramref name="brushSize"/> its strokes are larger and the picture does not move. Use a brush size about half as large to get similar strokes.
    /// </para>
    /// Each tile reads its rectangle plus half the brush; sizes up to 255 are accepted.
    /// </summary>
    /// <param name="levels">Number of intensity bins, 1 to 1024. Defaults to 10.</param>
    /// <param name="brushSize">Window size in pixels, 1 to 255. Defaults to 15.</param>
    /// <returns>The painted image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image OilPaint(int levels = 10, int brushSize = 15)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levels, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(brushSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(brushSize, 255);
        return Map(static (n, a) => new OilPaintNode(n, a.Levels, a.Brush, a.Brush / 2, (a.Brush - 1) / 2), (Levels: levels, Brush: brushSize));
    }

    // The window ImageSharp uses (the pixel and up to brushSize / 2 before it), for comparing against that library.
    internal Image OilPaintAsImageSharp(int levels, int brushSize) =>
        Map(static (n, a) => new OilPaintNode(n, a.Levels, a.Brush, a.Brush >> 1, 0), (Levels: levels, Brush: brushSize));

    /// <summary>
    /// Darkens (or tints) the image towards its edges (lazy), for gray and RGB images. The amount at a pixel is
    /// <c>clamp(0.9 × d / sqrt(rx² + ry²), 0, 1)</c> where d is the distance in pixels from the center of the area and
    /// <c>rx</c>, <c>ry</c> are the radii, so the center is untouched and the amount grows linearly with distance, reaching 1 slightly
    /// beyond the radius. The color is blended over the pixel with ordinary source-over, with the color's alpha multiplied by the
    /// amount, on the stored (encoded) values. Gray images use the luma of the color. This is ImageSharp's vignette.
    /// </summary>
    /// <param name="color">Vignette color. Defaults to opaque black.</param>
    /// <param name="radiusX">Horizontal radius in pixels, or 0 (the default) for half the area's width.</param>
    /// <param name="radiusY">Vertical radius in pixels, or 0 (the default) for half the area's height.</param>
    /// <param name="area">The rectangle to apply the effect in, or <see langword="null"/> (the default) for the whole image. Pixels outside it are unchanged; its center is
    /// <c>(x + width / 2, y + height / 2)</c> with integer division.</param>
    /// <returns>The image with the vignette.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A radius is negative or not finite, or <paramref name="area"/> is not inside the image.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Vignette(Rgba32? color = null, double radiusX = 0, double radiusY = 0, ImageRect? area = null)
    {
        CheckRadius(radiusX, nameof(radiusX));
        CheckRadius(radiusY, nameof(radiusY));
        CheckArea(area);
        return Map(static (n, a) => new RadialOverlayNode(n, RadialKind.Vignette, a.Color, a.Rx, a.Ry, a.Area), (Color: color ?? new Rgba32(0, 0, 0, 255), Rx: radiusX, Ry: radiusY, Area: area));
    }

    /// <summary>
    /// A radial tint (lazy) for gray and RGB images: full color at the center of the area, fading linearly with distance. The amount
    /// at a pixel is <c>clamp(1 − 0.95 × d / radius, 0, 1)</c>, so pixels further than about 1.05 radii from the center are unchanged.
    /// The color is blended as for <see cref="Vignette"/>. This is ImageSharp's glow, whose default color is black (so the default
    /// darkens the center); pass a light color for a glow.
    /// </summary>
    /// <param name="color">Glow color. Defaults to opaque black.</param>
    /// <param name="radius">Radius in pixels, limited to half the area's width, or 0 (the default) for half the area's width.</param>
    /// <param name="area">The rectangle to apply the effect in, or <see langword="null"/> (the default) for the whole image. Pixels outside it are unchanged.</param>
    /// <returns>The image with the glow.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or not finite, or <paramref name="area"/> is not inside the image.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Glow(Rgba32? color = null, double radius = 0, ImageRect? area = null)
    {
        CheckRadius(radius, nameof(radius));
        CheckArea(area);
        return Map(static (n, a) => new RadialOverlayNode(n, RadialKind.Glow, a.Color, a.Radius, 0, a.Area), (Color: color ?? new Rgba32(0, 0, 0, 255), Radius: radius, Area: area));
    }

    /// <summary>
    /// Bokeh blur (lazy): a lens blur whose highlights bloom into soft discs. The premultiplied stored values are raised to
    /// <paramref name="gamma"/>, convolved with a disc-shaped aperture of the given radius, and raised to <c>1 / gamma</c>; a gamma above 1 makes bright points
    /// outweigh dark surroundings, as in a photograph of lights. With a gamma of 1 it is a plain disc blur.
    /// <para>
    /// <b>Method.</b> The aperture is not summed over its area. It is a sum of <paramref name="components"/> separable complex
    /// kernels (Gaussian times a complex chirp) fitted to a disc, so the cost grows with the radius, not its square. More components
    /// give a sharper disc edge and more cost; one component looks like a soft blob. The fit is the one published by Garcia,
    /// "Circular Separable Convolution Depth of Field", the same family ImageSharp uses; it can ring slightly beside hard edges,
    /// and results agree with ImageSharp's to within a few 8-bit levels. Pixels beyond the edge come from
    /// <paramref name="edge"/>. Each tile reads its rectangle grown by the radius.
    /// </para>
    /// </summary>
    /// <param name="radius">Aperture radius in pixels, 1 to 255. Defaults to 32.</param>
    /// <param name="components">Number of kernel components, 1 to 6. Defaults to 2.</param>
    /// <param name="gamma">Highlight exponent, greater than 0 and at most 8. Defaults to 3.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The blurred image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range or the edge mode is unsupported.</exception>
    public Image BokehBlur(int radius = 32, int components = 2, double gamma = 3, EdgeMode edge = EdgeMode.Clamp)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, 255);
        ArgumentOutOfRangeException.ThrowIfLessThan(components, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(components, 6);
        if (!(gamma > 0 && gamma <= 8))
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), "Gamma must be greater than 0 and at most 8.");
        }

        FilterEdge.Validate(edge);
        return Map(static (n, a) => new BokehNode(n, a.Radius, a.Components, a.Gamma, a.Edge), (Radius: radius, Components: components, Gamma: gamma, Edge: edge));
    }

    /// <summary>
    /// Gaussian sharpening (lazy): convolves with the kernel <c>2·δ − g</c> in each direction, where g is the normalized Gaussian of
    /// <paramref name="sigma"/> (radius <c>ceil(3σ)</c>, as for <see cref="GaussianBlur"/>) and δ is a unit impulse. The horizontal pass is limited to the
    /// range 0 to 1 before the vertical pass runs, as in ImageSharp, which keeps the overshoot at hard edges smaller than the plain 2-D kernel would give. The two 1-D passes give
    /// a 2-D kernel that sums to 1, boosts the center and surrounds it with a negative ring, so flat areas keep their value. Unlike
    /// <see cref="Sharpen"/> there is no amount or threshold.
    /// <para>
    /// ImageSharp does this on the stored values, and so does this method by default (<paramref name="linear"/> false); with
    /// <c>true</c> it works in linear light on premultiplied alpha like the blurs, which gives stronger halos on the dark side of edges.
    /// Alpha is filtered like a color channel in premultiplied form; integer results are rounded and clamped. Edges, locality
    /// and animation behave as for <see cref="GaussianBlur"/>; sigma up to 42 is accepted and computed exactly.
    /// </para>
    /// </summary>
    /// <param name="sigma">Gaussian standard deviation in pixels, 0 to 42; 0 returns the image unchanged. Defaults to 3.</param>
    /// <param name="linear">Filter in linear light. Defaults to <see langword="false"/>.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The sharpened image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Sigma is negative, not finite or above 42, or the edge mode is unsupported.</exception>
    /// <exception cref="ImageOperationException">The pixel format cannot be filtered (more than four channels).</exception>
    public Image GaussianSharpen(double sigma = 3, bool linear = false, EdgeMode edge = EdgeMode.Clamp)
    {
        CheckSigma(sigma, nameof(sigma));
        if (GaussianRadius(sigma) > MaxRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), $"Sigma is limited to 42 (kernel radius {MaxRadius}).");
        }

        FilterEdge.Validate(edge);
        return sigma == 0 ? this : Map(static (n, a) => BuildGaussianSharpen(n, a.Sigma, a.Linear, a.Edge), (Sigma: sigma, Linear: linear, Edge: edge));
    }

    /// <summary>
    /// Edge detection with a small fixed kernel set (lazy), for gray and RGB images. See <see cref="EdgeDetector"/> for the
    /// kernels. With <paramref name="grayscale"/> (the default) the Rec. 709 luma of the stored values is filtered (8-bit images
    /// are first converted to 8-bit gray, as a separate grayscale step would) and the result is a gray image without alpha;
    /// otherwise each color channel is filtered on its own, the format and alpha are kept (alpha is not filtered), and straight alpha is
    /// used. Everything works on stored values (not linear light), as ImageSharp does. Results below 0 become 0 and integer results are clamped to the sample range;
    /// float results are not clamped above. Pixels beyond the edge come from <paramref name="edge"/> (clamp by default, so a flat border has no response).
    /// Each tile reads its rectangle grown by 1 pixel (2 for the 5×5 kernels).
    /// </summary>
    /// <param name="detector">The kernel set. Defaults to <see cref="EdgeDetector.Sobel"/>.</param>
    /// <param name="grayscale">Filter luma and return gray. Defaults to <see langword="true"/>.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The edge image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The detector or edge mode is not supported.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image DetectEdges(EdgeDetector detector = EdgeDetector.Sobel, bool grayscale = true, EdgeMode edge = EdgeMode.Clamp)
    {
        if (!Enum.IsDefined(detector))
        {
            throw new ArgumentOutOfRangeException(nameof(detector));
        }

        FilterEdge.Validate(edge);
        return Map(static (n, a) => new EdgeDetectNode(n, a.Detector, a.Gray, a.Edge), (Detector: detector, Gray: grayscale, Edge: edge));
    }

    /// <summary>
    /// Crops to the area that has detail (eager, like <see cref="Trim"/>): the 8-bit image from <c>DetectEdges(EdgeDetector.Sobel, grayscale: false)</c> is
    /// reduced to luma (Rec. 709) and every pixel at or above <paramref name="threshold"/> (a fraction of full scale) counts as detail;
    /// the result is the smallest rectangle holding all of them. An image with no such pixel is returned unchanged. For animations the union over all frames is used. The scan reads the image
    /// once, top to bottom, in bounded memory.
    /// </summary>
    /// <param name="threshold">Edge strength that counts as detail, 0 to 1. Defaults to 0.5.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The cropped image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0 to 1.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image EntropyCrop(float threshold = 0.5f, CancellationToken cancellationToken = default)
    {
        if (!(threshold >= 0 && threshold <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1.");
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
            ImageRect box = EntropyScan.Find(f.Node, threshold, context);
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
            return this;
        }

        x1 = Math.Min(x1, minW);
        y1 = Math.Min(y1, minH);
        return x1 <= x0 || y1 <= y0 || (x0 == 0 && y0 == 0 && x1 == minW && y1 == minH) ? this : Crop(x0, y0, x1 - x0, y1 - y0);
    }

    private static void CheckRadius(double radius, string name)
    {
        if (!(radius >= 0) || !double.IsFinite(radius))
        {
            throw new ArgumentOutOfRangeException(name, "Radius must be finite and not negative.");
        }
    }

    private void CheckArea(ImageRect? area)
    {
        if (area is { } a && (a.IsEmpty || !a.IsInside(Width, Height)))
        {
            throw new ArgumentOutOfRangeException(nameof(area), "The area must be a non-empty rectangle inside the image.");
        }
    }

    private static ConvolveNode BuildGaussianSharpen(Node n, double sigma, bool linear, EdgeMode edge)
    {
        int r = GaussianRadius(sigma);
        float[] g = GaussianWeights(sigma);
        float[] k = new float[g.Length];
        for (int i = 0; i < k.Length; i++)
        {
            k[i] = -g[i];
        }

        k[r] += 2f;
        var spec = new ConvolveSpec { RadiusX = r, RadiusY = r, Horizontal = k, Vertical = k, ClampBetweenPasses = true };
        return new ConvolveNode(
            n,
            spec,
            linear,
            edge,
            new OpDescriptor(
                "gaussian_sharpen",
                new OpParameter("sigma", sigma),
                new OpParameter("linear", linear),
                new OpParameter("edge", edge.ToString().ToLowerInvariant())));
    }
}
