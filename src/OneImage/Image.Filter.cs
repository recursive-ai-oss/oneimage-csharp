using System.Globalization;
using System.Text;

namespace OneImage;

public sealed partial class Image
{
    // Largest kernel radius a filter computes exactly per tile. A radius is also the vertical margin a streaming source has to
    // supply; at 48 pixels the margin is well under one tile row, so streaming stays within its window. Wider Gaussian, box and
    // unsharp blurs switch to the reduced-resolution approximation described on those methods.
    internal const int ExactRadiusLimit = 48;

    // The largest radius any filter accepts (a margin of one tile row at most).
    internal const int MaxRadius = 127;

    /// <summary>
    /// Gaussian blur (lazy). The kernel is separable and sampled at pixel centers: weight(k) = exp(-k² / (2σ²)) for
    /// k = -r..r with <c>r = max(1, ceil(3σ))</c>, normalized to sum 1 (so the truncated tail is spread over the taps).
    /// A sigma of 0 on an axis leaves that axis unchanged; with both axes 0 the image is returned as is.
    /// <para>
    /// <b>Working space.</b> With <paramref name="linear"/> (the default) RGB and gray data is treated as sRGB, converted to linear light
    /// and premultiplied by alpha before filtering, then converted back, so blurring an object on transparency leaves no dark
    /// or colored halo and edges between bright and dark areas blend as light does. <c>linear: false</c> filters the stored values (still
    /// premultiplied by alpha). Floating-point pixels are treated as sRGB-encoded values; CMYK and Lab are always filtered as stored.
    /// Integer results are rounded half up and clamped. Alpha is blurred like a color channel.
    /// </para>
    /// <para>
    /// <b>Edges.</b> Pixels beyond the image come from <paramref name="edge"/>: <see cref="EdgeMode.Clamp"/> (default) repeats the
    /// edge pixel, <see cref="EdgeMode.Mirror"/> reflects (repeating the edge pixel), <see cref="EdgeMode.Constant"/> reads transparent
    /// black (so edges darken on opaque images). <see cref="EdgeMode.Wrap"/> is not supported.
    /// </para>
    /// <para>
    /// <b>Locality and streaming.</b> Each output tile reads its own region grown by r pixels on every side and nothing else. For
    /// σ up to 16 (r ≤ 48) the result is exact and, on a sequential decoder, streams in a bounded window of tile rows. Above
    /// that the kernel is wider than a streaming window can supply, so the blur is approximated: the image is box-averaged
    /// down by f = ceil(σ / 4) (in its own pixel format, in the working space), blurred exactly in float with σ / f, and
    /// enlarged back with Catmull-Rom. On a streaming source the reduced copy (1/f² of the pixels, so 4% of the frame at σ = 20 and
    /// 0.07% at σ = 150) is read once, top to bottom, and kept for the life of the image; everything else still streams. The result stays within a
    /// few 8-bit levels of the exact blur on photographic content (mean difference under 0.6 level). Results are identical for any thread count or tile order. Animated images blur every frame.
    /// </para>
    /// </summary>
    /// <param name="sigma">Standard deviation in pixels, finite and not negative.</param>
    /// <param name="sigmaY">Vertical standard deviation, or <see langword="null"/> to use <paramref name="sigma"/> on both axes.</param>
    /// <param name="linear">Filter in linear light. Defaults to <see langword="true"/>.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The blurred image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A sigma is negative or not finite, or the edge mode is unsupported.</exception>
    /// <exception cref="ImageOperationException">The pixel format cannot be filtered (more than four channels).</exception>
    public Image GaussianBlur(double sigma, double? sigmaY = null, bool linear = true, EdgeMode edge = EdgeMode.Clamp)
    {
        double sy = sigmaY ?? sigma;
        CheckSigma(sigma, nameof(sigma));
        CheckSigma(sy, nameof(sigmaY));
        FilterEdge.Validate(edge);
        return Map(static (n, a) => BuildGaussian(n, a.Sx, a.Sy, a.Linear, a.Edge), (Sx: sigma, Sy: sy, Linear: linear, Edge: edge));
    }

    /// <summary>
    /// Box blur (lazy): every output pixel is the mean of the (2r+1) × (2r+1) window around it, computed as two 1-D passes.
    /// Working space, edge handling, rounding, locality and animation behave as for <see cref="GaussianBlur"/>.
    /// For radius above 48 the blur is approximated by box-averaging down, blurring with radius ≈ r / f and enlarging with Catmull-Rom
    /// (f = ceil(r / 16)); the window width then differs from 2r+1 by at most about 2%.
    /// </summary>
    /// <param name="radius">Window radius in pixels, at least 1 and at most 4096.</param>
    /// <param name="linear">Filter in linear light. Defaults to <see langword="true"/>.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The blurred image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The radius is out of range or the edge mode is unsupported.</exception>
    /// <exception cref="ImageOperationException">The pixel format cannot be filtered (more than four channels).</exception>
    public Image BoxBlur(int radius, bool linear = true, EdgeMode edge = EdgeMode.Clamp)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, 4096);
        FilterEdge.Validate(edge);
        return Map(static (n, a) => BuildBox(n, a.Radius, a.Linear, a.Edge), (Radius: radius, Linear: linear, Edge: edge));
    }

    /// <summary>
    /// Unsharp mask (lazy): <c>out = in + amount × (in − blur(in))</c> per color channel, where the blur is a Gaussian of standard
    /// deviation <paramref name="radius"/> (see <see cref="GaussianBlur"/> for its kernel, edges and the approximation used above
    /// σ = 16). Differences whose size is below <paramref name="threshold"/> are left unchanged, which keeps flat noisy areas from being amplified.
    /// <para>
    /// The combination runs in the working space of <paramref name="linear"/> (default linear light, premultiplied by alpha), and the
    /// threshold is in that space as a fraction of full scale. Alpha is not sharpened; with alpha the premultiplied color is limited to
    /// [0, alpha] so no invalid pixels appear. Integer results are rounded half up and clamped; float results are not clamped.
    /// Linear-light sharpening gives stronger halos on the dark side of an edge than sharpening stored values does; pass
    /// <c>linear: false</c> for the conventional (Photoshop-like) look.
    /// </para>
    /// </summary>
    /// <param name="amount">Strength; 1 adds back the full difference, 0 changes nothing, negative values blur. Defaults to 1.</param>
    /// <param name="radius">Gaussian sigma in pixels, not negative. Defaults to 1.</param>
    /// <param name="threshold">Minimum difference to sharpen, 0 to 1. Defaults to 0 (sharpen everything).</param>
    /// <param name="linear">Work in linear light. Defaults to <see langword="true"/>.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The sharpened image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range or the edge mode is unsupported.</exception>
    /// <exception cref="ImageOperationException">The pixel format cannot be filtered (more than four channels).</exception>
    public Image Sharpen(double amount = 1.0, double radius = 1.0, double threshold = 0.0, bool linear = true, EdgeMode edge = EdgeMode.Clamp)
    {
        if (!double.IsFinite(amount))
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        CheckSigma(radius, nameof(radius));
        if (!(threshold >= 0 && threshold <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1.");
        }

        FilterEdge.Validate(edge);
        return Map(static (n, a) => BuildSharpen(n, a.Amount, a.Radius, a.Threshold, a.Linear, a.Edge), (Amount: amount, Radius: radius, Threshold: threshold, Linear: linear, Edge: edge));
    }

    /// <summary>
    /// Convolves with a user kernel (lazy): each output channel is the sum over the kernel of weight × input pixel, divided by
    /// <paramref name="divisor"/>, plus <paramref name="offset"/>. The kernel is applied as written (correlation: weight
    /// <c>kernel[j, i]</c> multiplies the pixel at offset (i − cx, j − cy) from the output pixel, with the center at (cx, cy)), so
    /// it is not flipped; for symmetric kernels the two are the same. Width and height must be odd.
    /// <para>
    /// A kernel of rank one (the outer product of a row and a column, within 1e-6 of its largest weight) runs as two 1-D passes; others
    /// run as a full 2-D sum over their non-zero weights. Both give the same result within float rounding.
    /// The divisor defaults to the sum of the weights (1 when that sum is 0), so a smoothing kernel keeps brightness.
    /// The offset is added to the color channels after dividing, as a fraction of full scale in the working space; alpha gets no offset.
    /// </para>
    /// <para>
    /// Working space, alpha, edges, rounding, locality and animation behave as for <see cref="GaussianBlur"/>; a kernel with negative
    /// weights can overshoot, and integer results are clamped. Radii (half the kernel size) up to 127 are accepted; streaming sources
    /// keep their bounded-window behavior while the vertical radius stays under one tile row.
    /// </para>
    /// </summary>
    /// <param name="kernel">Row-major weights, <c>kernelHeight</c> rows of <c>kernelWidth</c>.</param>
    /// <param name="kernelWidth">Odd kernel width.</param>
    /// <param name="kernelHeight">Odd kernel height.</param>
    /// <param name="divisor">The divisor, or 0 for the sum of the weights.</param>
    /// <param name="offset">Value added after dividing. Defaults to 0.</param>
    /// <param name="linear">Filter in linear light. Defaults to <see langword="true"/>.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The filtered image.</returns>
    /// <exception cref="ArgumentException">The size does not match the weights, a size is even, or a weight is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is too large or the edge mode is unsupported.</exception>
    /// <exception cref="ImageOperationException">The pixel format cannot be filtered (more than four channels).</exception>
    public Image Convolve(ReadOnlySpan<float> kernel, int kernelWidth, int kernelHeight, double divisor = 0, double offset = 0, bool linear = true, EdgeMode edge = EdgeMode.Clamp) =>
        ConvolveCore(kernel, kernelWidth, kernelHeight, divisor, offset, linear, edge, allowSeparable: true);

    /// <summary>Convolves with a two-dimensional kernel indexed <c>[row, column]</c>; see <see cref="Convolve(ReadOnlySpan{float},int,int,double,double,bool,EdgeMode)"/>.</summary>
    /// <param name="kernel">The weights, odd in both dimensions.</param>
    /// <param name="divisor">The divisor, or 0 for the sum of the weights.</param>
    /// <param name="offset">Value added after dividing. Defaults to 0.</param>
    /// <param name="linear">Filter in linear light. Defaults to <see langword="true"/>.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The filtered image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kernel"/> is null.</exception>
    /// <exception cref="ArgumentException">A size is even or a weight is not finite.</exception>
    public Image Convolve(float[,] kernel, double divisor = 0, double offset = 0, bool linear = true, EdgeMode edge = EdgeMode.Clamp)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        int h = kernel.GetLength(0);
        int w = kernel.GetLength(1);
        float[] flat = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                flat[(y * w) + x] = kernel[y, x];
            }
        }

        return ConvolveCore(flat, w, h, divisor, offset, linear, edge, allowSeparable: true);
    }

    /// <summary>
    /// Median filter (lazy): every sample becomes the median of the (2r+1) × (2r+1) window around it, channel by channel, on the stored
    /// values, so there is no working-space choice (the median of a monotone transform is the transform of the median) and no
    /// rounding. Alpha is filtered like a color channel, and nothing is premultiplied or un-premultiplied. This removes
    /// speckle noise while keeping edges.
    /// <para>
    /// <b>Algorithm.</b> 8-bit images use a sliding 256-bin histogram per channel (each step removes one window column and adds one,
    /// and the median bin moves only as far as the counts require, so the cost per sample is proportional to r). 16-bit and
    /// floating-point images sort each window. <b>Edges</b> as for <see cref="GaussianBlur"/>. Each tile reads its region grown
    /// by r. Radii up to 127 are accepted, and the margin is exact, so streaming sources keep their bounded window for r up to 127.
    /// </para>
    /// </summary>
    /// <param name="radius">Window radius in pixels, 1 to 127.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The filtered image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The radius is out of range or the edge mode is unsupported.</exception>
    public Image Median(int radius, EdgeMode edge = EdgeMode.Clamp)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, MaxRadius);
        FilterEdge.Validate(edge);
        return Map(static (n, a) => new MedianNode(n, a.Radius, a.Edge), (Radius: radius, Edge: edge));
    }

    /// <summary>
    /// Sobel edge strength (lazy). The luminance of each pixel is taken as <see cref="ConvertTo"/> to gray would take it (Rec. 709
    /// weights on the stored values, alpha ignored, no linearization; gray images use their own values), the 3×3 Sobel sums gx
    /// and gy are formed, and the output is <c>min(1, scale × sqrt(gx² + gy²) / 4)</c> as a gray image of the input's sample type. With
    /// the division by 4 an ideal black-to-white step gives full scale at scale 1. Float output is not clamped.
    /// The result has no alpha channel. CMYK and Lab images are refused.
    /// <para>
    /// Edge handling as for <see cref="GaussianBlur"/> (default clamp: a flat border has no gradient). Each tile reads its region grown by one pixel.
    /// </para>
    /// </summary>
    /// <param name="scale">Output gain, positive. Defaults to 1.</param>
    /// <param name="edge">What lies beyond the edge. Defaults to <see cref="EdgeMode.Clamp"/>.</param>
    /// <returns>The gradient magnitude image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The scale is not positive and finite or the edge mode is unsupported.</exception>
    /// <exception cref="ImageOperationException">The color model is CMYK or Lab.</exception>
    public Image Sobel(double scale = 1.0, EdgeMode edge = EdgeMode.Clamp)
    {
        if (!(scale > 0) || !double.IsFinite(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        FilterEdge.Validate(edge);
        return Map(static (n, a) => new SobelNode(n, a.Scale, a.Edge), (Scale: scale, Edge: edge));
    }

    internal Image ConvolveCore(ReadOnlySpan<float> kernel, int kernelWidth, int kernelHeight, double divisor, double offset, bool linear, EdgeMode edge, bool allowSeparable)
    {
        if (kernelWidth < 1 || kernelHeight < 1 || kernelWidth % 2 == 0 || kernelHeight % 2 == 0)
        {
            throw new ArgumentException("Kernel width and height must be odd and positive.");
        }

        if (kernel.Length != (long)kernelWidth * kernelHeight)
        {
            throw new ArgumentException("The kernel length does not match its width and height.", nameof(kernel));
        }

        if (kernelWidth / 2 > MaxRadius || kernelHeight / 2 > MaxRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(kernel), $"Kernel radius is limited to {MaxRadius}.");
        }

        if (!double.IsFinite(divisor) || !double.IsFinite(offset))
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), "Divisor and offset must be finite.");
        }

        double sum = 0;
        double max = 0;
        foreach (float w in kernel)
        {
            if (!float.IsFinite(w))
            {
                throw new ArgumentException("Kernel weights must be finite.", nameof(kernel));
            }

            sum += w;
            max = Math.Max(max, Math.Abs(w));
        }

        FilterEdge.Validate(edge);
        double div = divisor != 0 ? divisor : (Math.Abs(sum) > 1e-12 ? sum : 1);
        int rx = kernelWidth / 2;
        int ry = kernelHeight / 2;
        ConvolveSpec spec = max == 0 || !allowSeparable || !TrySeparate(kernel, kernelWidth, kernelHeight, max, div, rx, ry, offset, out ConvolveSpec? sep)
            ? new ConvolveSpec { RadiusX = rx, RadiusY = ry, Full = Scale(kernel, div), Offset = (float)offset }
            : sep!;
        var sb = new StringBuilder();
        foreach (float w in kernel)
        {
            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(w.ToString("R", CultureInfo.InvariantCulture));
        }

        string kernelText = sb.ToString();
        return Map(
            static (n, a) => new ConvolveNode(
                n,
                a.Spec,
                a.Linear,
                a.Edge,
                new OpDescriptor(
                    "convolve",
                    new OpParameter("width", (long)a.Width),
                    new OpParameter("height", (long)a.Height),
                    new OpParameter("kernel", a.Text),
                    new OpParameter("divisor", a.Div),
                    new OpParameter("offset", a.Offset),
                    new OpParameter("linear", a.Linear),
                    new OpParameter("edge", a.Edge.ToString().ToLowerInvariant()))),
            (Spec: spec, Linear: linear, Edge: edge, Width: kernelWidth, Height: kernelHeight, Text: kernelText, Div: div, Offset: offset));
    }

    private static float[] Scale(ReadOnlySpan<float> kernel, double div)
    {
        float[] r = new float[kernel.Length];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = (float)(kernel[i] / div);
        }

        return r;
    }

    // Splits a rank-one kernel into a row and a column. The pivot is the largest weight; the row is the pivot row, the column is
    // the pivot column normalized by the pivot; every weight must match the product to within 1e-6 of the largest.
    private static bool TrySeparate(ReadOnlySpan<float> k, int w, int h, double max, double div, int rx, int ry, double offset, out ConvolveSpec? spec)
    {
        int pi = 0;
        for (int i = 1; i < k.Length; i++)
        {
            if (Math.Abs(k[i]) > Math.Abs(k[pi]))
            {
                pi = i;
            }
        }

        int py = pi / w;
        int px = pi % w;
        double pivot = k[pi];
        double[] col = new double[h];
        for (int y = 0; y < h; y++)
        {
            col[y] = k[(y * w) + px] / pivot;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (Math.Abs(k[(y * w) + x] - (col[y] * k[(py * w) + x])) > 1e-6 * max)
                {
                    spec = null;
                    return false;
                }
            }
        }

        float[] hw = new float[w];
        for (int x = 0; x < w; x++)
        {
            hw[x] = k[(py * w) + x];
        }

        float[] vw = new float[h];
        for (int y = 0; y < h; y++)
        {
            vw[y] = (float)(col[y] / div);
        }

        spec = new ConvolveSpec { RadiusX = rx, RadiusY = ry, Horizontal = hw, Vertical = vw, Offset = (float)offset };
        return true;
    }

    private static void CheckSigma(double sigma, string name)
    {
        if (!(sigma >= 0) || !double.IsFinite(sigma))
        {
            throw new ArgumentOutOfRangeException(name, "Sigma must be finite and not negative.");
        }
    }

    internal static int GaussianRadius(double sigma) => sigma <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(3 * sigma));

    internal static float[] GaussianWeights(double sigma)
    {
        int r = GaussianRadius(sigma);
        if (r == 0)
        {
            return [1f];
        }

        double[] half = new double[r + 1];
        double sum = 0;
        for (int k = 0; k <= r; k++)
        {
            half[k] = Math.Exp(-(double)k * k / (2 * sigma * sigma));
            sum += k == 0 ? half[k] : 2 * half[k];
        }

        float[] w = new float[(2 * r) + 1];
        for (int k = 0; k <= r; k++)
        {
            w[r + k] = (float)(half[k] / sum);
            w[r - k] = w[r + k];
        }

        return w;
    }

    private static Node BuildGaussian(Node n, double sx, double sy, bool linear, EdgeMode edge)
    {
        if (sx == 0 && sy == 0)
        {
            return n;
        }

        if (Math.Max(GaussianRadius(sx), GaussianRadius(sy)) > ExactRadiusLimit)
        {
            return BuildWide(n, Math.Max(sx, sy) / 4, linear, keepFloat: false, (small, fx, fy) => BuildGaussian(small, sx / fx, sy / fy, linear, edge));
        }

        var spec = new ConvolveSpec { RadiusX = GaussianRadius(sx), RadiusY = GaussianRadius(sy), Horizontal = GaussianWeights(sx), Vertical = GaussianWeights(sy) };
        return new ConvolveNode(
            n,
            spec,
            linear,
            edge,
            new OpDescriptor(
                "gaussian_blur",
                new OpParameter("sigma_x", sx),
                new OpParameter("sigma_y", sy),
                new OpParameter("radius_x", (long)spec.RadiusX),
                new OpParameter("radius_y", (long)spec.RadiusY),
                new OpParameter("linear", linear),
                new OpParameter("edge", edge.ToString().ToLowerInvariant())));
    }

    private static Node BuildBox(Node n, int radius, bool linear, EdgeMode edge)
    {
        if (radius > ExactRadiusLimit)
        {
            return BuildWide(n, radius / 16.0, linear, keepFloat: false, (small, fx, fy) => BuildBox(small, Math.Max(1, (int)Math.Round(radius / Math.Max(fx, fy))), linear, edge));
        }

        float[] w = new float[(2 * radius) + 1];
        Array.Fill(w, 1f / w.Length);
        var spec = new ConvolveSpec { RadiusX = radius, RadiusY = radius, Horizontal = w, Vertical = w };
        return new ConvolveNode(
            n,
            spec,
            linear,
            edge,
            new OpDescriptor("box_blur", new OpParameter("radius", (long)radius), new OpParameter("linear", linear), new OpParameter("edge", edge.ToString().ToLowerInvariant())));
    }

    private static Node BuildSharpen(Node n, double amount, double sigma, double threshold, bool linear, EdgeMode edge)
    {
        if (amount == 0 || sigma == 0)
        {
            return n;
        }

        var desc = new OpDescriptor(
            "unsharp_mask",
            new OpParameter("amount", amount),
            new OpParameter("radius", sigma),
            new OpParameter("threshold", threshold),
            new OpParameter("linear", linear),
            new OpParameter("edge", edge.ToString().ToLowerInvariant()));
        if (GaussianRadius(sigma) > ExactRadiusLimit)
        {
            Node blurred = BuildWide(n, sigma / 4, linear, keepFloat: true, (small, fx, fy) => BuildGaussian(small, sigma / fx, sigma / fy, linear, edge));
            return new UnsharpCombineNode(n, blurred, amount, threshold, linear, desc);
        }

        int r = GaussianRadius(sigma);
        float[] w = GaussianWeights(sigma);
        var spec = new ConvolveSpec { RadiusX = r, RadiusY = r, Horizontal = w, Vertical = w, Unsharp = true, Amount = (float)amount, Threshold = (float)threshold };
        return new ConvolveNode(n, spec, linear, edge, desc);
    }

    // Approximates a wide blur at reduced resolution: box-average down by about `reduceBy` in the image's own format, blur there
    // in float with the callback (given the actual per-axis reduction factors), enlarge with Catmull-Rom. A streaming source is
    // read once into the reduced copy, which is small, so the blur itself may read it in any order.
    private static Node BuildWide(Node n, double reduceBy, bool linear, bool keepFloat, Func<Node, double, double, Node> blur)
    {
        double f = Math.Max(2, Math.Ceiling(reduceBy));
        int w2 = Math.Max(1, (int)Math.Round(n.Width / f));
        int h2 = Math.Max(1, (int)Math.Round(n.Height / f));
        double fx = (double)n.Width / w2;
        double fy = (double)n.Height / h2;
        var floatFormat = new PixelFormat(n.Format.Model, SampleType.F32, n.Format.Alpha);
        Node small = ResampleNode.Create(n, w2, h2, ResamplingFilter.Box, linear);
        if (small.Streaming)
        {
            small = new MaterializeNode(small);
        }

        Node smallFloat = small.Format.Sample == SampleType.F32 ? small : new ConvertNode(small, floatFormat);
        Node up = ResampleNode.Create(blur(smallFloat, fx, fy), n.Width, n.Height, ResamplingFilter.CatmullRom, linear);
        return keepFloat || n.Format.Sample == SampleType.F32 ? up : new ConvertNode(up, n.Format);
    }
}
