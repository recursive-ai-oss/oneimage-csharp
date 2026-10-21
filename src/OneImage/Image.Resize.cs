using OneImage.Kernels;

namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Resizes (lazy). Give a width, a height or both. With only one, the other follows the aspect ratio (rounded half up, at
    /// least 1) and <paramref name="fit"/> is ignored. With both, <paramref name="fit"/> decides: <see cref="Fit.Contain"/> fits inside
    /// the box, <see cref="Fit.Cover"/> fills it and crops by <paramref name="anchor"/>, <see cref="Fit.Stretch"/> ignores the aspect
    /// ratio, <see cref="Fit.Pad"/> is Contain plus padding to the full box. For Contain, Cover and Pad the limiting axis lands exactly
    /// on the box size and the other is rounded half up (at least 1).
    /// <para>
    /// <b>Filters.</b> <see cref="ResamplingFilter.Auto"/> uses Lanczos3 on an axis that shrinks and Catmull-Rom on one that grows or
    /// stays. When shrinking, kernels are widened by the scale factor. Source positions use pixel centers. Taps beyond the image edge
    /// are folded onto the edge pixel (edge replication). An axis whose size does not change is copied exactly. Filter weights are
    /// normalized to sum to 1; cubic and Lanczos kernels can overshoot, and integer results are clamped.
    /// </para>
    /// <para>
    /// <b>Color.</b> With <paramref name="linear"/> (the default) RGB and gray data is treated as sRGB, converted to linear light
    /// and premultiplied by alpha before filtering, then converted back. This avoids dark halos and color fringes at edges.
    /// <c>linear: false</c> filters the stored values as they are (still premultiplied by alpha). Floating-point pixels are treated
    /// like the others, as sRGB-encoded values. CMYK and Lab are always filtered as stored. Integer results are rounded half up.
    /// A general ICC-based linearization will replace the sRGB assumption for tagged images.
    /// </para>
    /// <para>
    /// <b>Large reductions.</b> If the source is a decoder that can shrink on load (for example JPEG DCT scaling), the largest
    /// denominator of 8, 4 or 2 that keeps both dimensions at or above the filter's input size is requested first (not for
    /// <see cref="ResamplingFilter.Nearest"/>). Then, on any axis where the remaining ratio is 4 or more, the image is box-averaged by
    /// the integer factor floor(ratio / 2) before the filter runs, so the filter always sees a ratio below 4. Both are exact
    /// area averages of the linear-light values. The final filter's output is therefore close to, but not bit-identical with, a
    /// single-step filter at very large ratios.
    /// </para>
    /// <para>
    /// Nothing is computed until pixels are pulled; each output tile reads just the source region its taps reach.
    /// Results are identical for any thread count or tile order. Animated images resize every frame.
    /// </para>
    /// </summary>
    /// <param name="width">Target width, or <see langword="null"/> to derive it from <paramref name="height"/>.</param>
    /// <param name="height">Target height, or <see langword="null"/> to derive it from <paramref name="width"/>.</param>
    /// <param name="fit">How the image fits a width-and-height box. Defaults to <see cref="Fit.Contain"/>.</param>
    /// <param name="anchor">Where a cropped or padded image sits. Defaults to <see cref="Anchor.Center"/>.</param>
    /// <param name="upscale">When <see langword="false"/> the image is never enlarged: Contain, Cover and Pad cap the scale at 1
    /// (Cover then crops the unscaled image to the box where it is larger, so the result can be smaller than the box; Pad still returns
    /// the full box), and Stretch caps each axis at the source size. Defaults to <see langword="true"/>.</param>
    /// <param name="filter">The reconstruction filter. Defaults to <see cref="ResamplingFilter.Auto"/>.</param>
    /// <param name="linear">Resample in linear light. Defaults to <see langword="true"/>.</param>
    /// <param name="background">Padding color for <see cref="Fit.Pad"/>, given as straight sRGB. Defaults to transparent black (black where
    /// the format has no alpha; alpha is dropped, not flattened). Ignored for CMYK and Lab, which pad with zero samples.</param>
    /// <returns>The resized image.</returns>
    /// <exception cref="ArgumentException">Neither width nor height is given.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive or an enum value is undefined.</exception>
    /// <exception cref="ImageLimitException">The result would exceed the engine's dimension limits.</exception>
    /// <exception cref="ImageOperationException">The pixel format cannot be resampled.</exception>
    public Image Resize(
        int? width = null,
        int? height = null,
        Fit fit = Fit.Contain,
        Anchor anchor = Anchor.Center,
        bool upscale = true,
        ResamplingFilter filter = ResamplingFilter.Auto,
        bool linear = true,
        Rgba32 background = default)
    {
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }

        if (!Enum.IsDefined(anchor))
        {
            throw new ArgumentOutOfRangeException(nameof(anchor));
        }

        // Validate against frame 0 now so argument errors are eager.
        _ = ResizePlan.Compute(Width, Height, width, height, fit, upscale);
        return Map(
            static (n, a) => BuildResize(n, a),
            (width, height, fit, anchor, upscale, filter, linear, background));
    }

    private static Node BuildResize(Node node, (int? Width, int? Height, Fit Fit, Anchor Anchor, bool Upscale, ResamplingFilter Filter, bool Linear, Rgba32 Background) a)
    {
        ResizePlan plan = ResizePlan.Compute(node.Width, node.Height, a.Width, a.Height, a.Fit, a.Upscale);
        Node cur = node;
        if (plan.ResampleWidth != cur.Width || plan.ResampleHeight != cur.Height)
        {
            if (cur is DecodeSourceNode && a.Filter != ResamplingFilter.Nearest)
            {
                foreach (int denominator in (ReadOnlySpan<int>)[8, 4, 2])
                {
                    if (((cur.Width + denominator - 1) / denominator) >= plan.ResampleWidth
                        && ((cur.Height + denominator - 1) / denominator) >= plan.ResampleHeight
                        && ((DecodeSourceNode)cur).TryPushScale(denominator) is { } shrunk)
                    {
                        cur = shrunk;
                        break;
                    }
                }
            }

            cur = ResampleNode.Create(cur, plan.ResampleWidth, plan.ResampleHeight, a.Filter, a.Linear);
        }

        if (plan.NeedsCrop)
        {
            if (a.Anchor == Anchor.Smart)
            {
                cur = new SmartCropNode(cur, plan.CropWidth, plan.CropHeight);
            }
            else
            {
                (int x, int y) = ResizePlan.Place(a.Anchor, cur.Width, cur.Height, plan.CropWidth, plan.CropHeight);
                cur = new CropNode(cur, new ImageRect(x, y, plan.CropWidth, plan.CropHeight));
            }
        }

        if (plan.NeedsPad)
        {
            (int x, int y) = ResizePlan.Place(a.Anchor, plan.PadWidth, plan.PadHeight, cur.Width, cur.Height);
            cur = new ExtendNode(cur, x, y, plan.PadWidth, plan.PadHeight, EdgeMode.Constant, a.Background);
        }

        return cur;
    }
}
