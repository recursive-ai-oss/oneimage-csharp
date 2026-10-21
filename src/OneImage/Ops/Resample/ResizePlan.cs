namespace OneImage;

/// <summary>The sizes a <see cref="Image.Resize"/> call works out from the source size, the box and the fit.</summary>
internal readonly record struct ResizePlan(int ResampleWidth, int ResampleHeight, int CropWidth, int CropHeight, int PadWidth, int PadHeight)
{
    public bool NeedsCrop => CropWidth != ResampleWidth || CropHeight != ResampleHeight;

    public bool NeedsPad => PadWidth != CropWidth || PadHeight != CropHeight;

    public static ResizePlan Compute(int sw, int sh, int? width, int? height, Fit fit, bool upscale)
    {
        if (width is null && height is null)
        {
            throw new ArgumentException("Give a width, a height or both.");
        }

        if (width is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        if (!Enum.IsDefined(fit))
        {
            throw new ArgumentOutOfRangeException(nameof(fit));
        }

        long tw;
        long th;
        if (width is null || height is null)
        {
            // One side given: the other follows the aspect ratio, and fit does not matter.
            fit = Fit.Stretch;
            tw = width ?? Scale(sw, height!.Value, sh);
            th = height ?? Scale(sh, width!.Value, sw);
        }
        else
        {
            tw = width.Value;
            th = height.Value;
        }

        long rw;
        long rh;
        switch (fit)
        {
            case Fit.Stretch:
                rw = upscale ? tw : Math.Min(tw, sw);
                rh = upscale ? th : Math.Min(th, sh);
                return Check(rw, rh, rw, rh, rw, rh);
            case Fit.Cover:
                bool widthLimited = tw * sh >= th * sw;
                (rw, rh) = widthLimited ? (tw, Scale(sh, tw, sw)) : (Scale(sw, th, sh), th);
                if (!upscale && (widthLimited ? tw > sw : th > sh))
                {
                    (rw, rh) = (sw, sh);
                }

                return Check(rw, rh, Math.Min(tw, rw), Math.Min(th, rh), Math.Min(tw, rw), Math.Min(th, rh));
            default:
                bool wl = tw * sh <= th * sw;
                (rw, rh) = wl ? (tw, Math.Min(th, Scale(sh, tw, sw))) : (Math.Min(tw, Scale(sw, th, sh)), th);
                if (!upscale && (wl ? tw > sw : th > sh))
                {
                    (rw, rh) = (sw, sh);
                }

                return fit == Fit.Pad ? Check(rw, rh, rw, rh, tw, th) : Check(rw, rh, rw, rh, rw, rh);
        }
    }

    /// <summary>The offset of an inner rectangle inside an outer one for an anchor (floor of the half on centered axes).</summary>
    public static (int X, int Y) Place(Anchor anchor, int outerW, int outerH, int innerW, int innerH)
    {
        (int ax, int ay) = anchor switch
        {
            Anchor.TopLeft => (0, 0),
            Anchor.Top => (1, 0),
            Anchor.TopRight => (2, 0),
            Anchor.Left => (0, 1),
            Anchor.Right => (2, 1),
            Anchor.BottomLeft => (0, 2),
            Anchor.Bottom => (1, 2),
            Anchor.BottomRight => (2, 2),
            _ => (1, 1),
        };
        return (((outerW - innerW) * ax) / 2, ((outerH - innerH) * ay) / 2);
    }

    // round-half-up of size * numerator / denominator, at least 1.
    private static long Scale(long size, long numerator, long denominator) =>
        Math.Max(1, ((size * numerator * 2) + denominator) / (2 * denominator));

    private static ResizePlan Check(long rw, long rh, long cw, long ch, long pw, long ph)
    {
        if (Math.Max(Math.Max(rw, rh), Math.Max(pw, ph)) > int.MaxValue)
        {
            throw new ImageLimitException("Resize target is too large.");
        }

        return new ResizePlan((int)rw, (int)rh, (int)cw, (int)ch, (int)pw, (int)ph);
    }
}
