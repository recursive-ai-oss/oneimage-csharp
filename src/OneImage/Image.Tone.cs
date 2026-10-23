namespace OneImage;

// Color and tone operations. All of them are lazy, run per pixel (so they stream), map over every frame and compute in float32
// on straight (non-premultiplied) color: alpha is never changed unless an op says so, and semi-transparent pixels keep their color.
// Adjacent ops fuse into one pass over each tile (see ToneNode). Integer results round half up and clamp to the sample range; the
// result of every op is rounded to the image's sample type before the next one starts, whether or not they are fused. For
// more precision than 8 or 16 bits between ops, convert to a float format first.
public sealed partial class Image
{
    /// <summary>
    /// Converts to gray using Rec. 709 luminance (0.2126 R + 0.7152 G + 0.0722 B). Alpha is unchanged and gray images are returned as they are.
    /// </summary>
    /// <param name="linear">Compute the luminance in linear light (decode the sRGB curve, weigh, encode). This is the default and the
    /// physically correct mean; <see langword="false"/> weighs the stored values, which is what many editors and the pixel-format
    /// <see cref="ConvertTo"/> do and is darker for saturated colors. 8-bit data uses a 16-bit linear intermediate (under 0.05 of an 8-bit step of error).</param>
    /// <param name="toGrayFormat">When <see langword="true"/> the result is a one-channel (plus alpha) gray image; by default the format is
    /// kept and R = G = B, so later ops and encoders see the same layout.</param>
    /// <returns>The gray image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Grayscale(bool linear = true, bool toGrayFormat = false) => ApplyTone(new GrayscaleStep(linear, toGrayFormat));

    /// <summary>
    /// Adjusts brightness and contrast on 0..1 values: <c>out = (v - 0.5) * k + 0.5 + brightness</c> with <c>k = tan((contrast + 1) * pi / 4)</c>,
    /// so contrast 0 is the identity, -1 flattens to mid-gray and values approaching 1 make a hard threshold (1 is capped at 0.9999).
    /// Integer results clamp to 0..max; float results are not clamped.
    /// </summary>
    /// <param name="brightness">Added to every value, -1..1 (1 = white). Default 0.</param>
    /// <param name="contrast">-1..1. Default 0.</param>
    /// <param name="linear">Work in linear light instead of on the stored (encoded) values. Default <see langword="false"/>, like Photoshop's legacy controls.</param>
    /// <returns>The adjusted image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside -1..1 or not finite.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image BrightnessContrast(double brightness = 0, double contrast = 0, bool linear = false)
    {
        CheckRange(brightness, -1, 1, nameof(brightness));
        CheckRange(contrast, -1, 1, nameof(contrast));
        return ApplyTone(new BrightnessContrastStep(brightness, contrast, linear));
    }

    /// <summary>
    /// Brightness and contrast with one of Photoshop's formulas (see <see cref="BrightnessContrastMode"/>). Brightness -1..1 stands for Photoshop's
    /// -150..150 and contrast -1..1 for -100..100, so a PSD setting of (20, 57) is <c>(20 / 150.0, 57 / 100.0)</c>. Integer results clamp.
    /// </summary>
    /// <param name="brightness">-1..1.</param>
    /// <param name="contrast">-1..1.</param>
    /// <param name="linear">Work in linear light instead of on the stored values. Photoshop works on stored values, so leave it <see langword="false"/> to match it.</param>
    /// <param name="mode">The formula. <see cref="BrightnessContrastMode.Linear"/> gives the same result as <see cref="BrightnessContrast(double, double, bool)"/>.</param>
    /// <returns>The adjusted image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside -1..1 or not finite, or <paramref name="mode"/> is undefined.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image BrightnessContrast(double brightness, double contrast, bool linear, BrightnessContrastMode mode)
    {
        CheckRange(brightness, -1, 1, nameof(brightness));
        CheckRange(contrast, -1, 1, nameof(contrast));
        return mode switch
        {
            BrightnessContrastMode.Linear => ApplyTone(new BrightnessContrastStep(brightness, contrast, linear)),
            BrightnessContrastMode.PhotoshopLegacy => ApplyTone(new PhotoshopBrightnessContrastStep(brightness, contrast, false, linear)),
            BrightnessContrastMode.PhotoshopSmooth => ApplyTone(new PhotoshopBrightnessContrastStep(brightness, contrast, true, linear)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>Multiplies by 2^<paramref name="stops"/>. Integer results clamp; float results are not clamped.</summary>
    /// <param name="stops">Exposure change in stops (+1 doubles the light). Between -16 and 16.</param>
    /// <param name="linear">Multiply in linear light. This is the default, because exposure is a physical scale; <see langword="false"/> scales the stored values.</param>
    /// <returns>The adjusted image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stops"/> is out of range.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Exposure(double stops, bool linear = true)
    {
        CheckRange(stops, -16, 16, nameof(stops));
        return ApplyTone(new ExposureStep(stops, linear));
    }

    /// <summary>Applies <c>out = in ^ (1 / gamma)</c> on 0..1 values (values at or below 0 give 0): gamma above 1 brightens midtones, below 1 darkens them.</summary>
    /// <param name="gamma">Greater than 0 and at most 100.</param>
    /// <param name="linear">Apply in linear light instead of on the stored values. Default <see langword="false"/>.</param>
    /// <returns>The adjusted image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gamma"/> is out of range.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Gamma(double gamma, bool linear = false)
    {
        if (!(gamma > 0 && gamma <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), "Gamma must be in (0, 100].");
        }

        return ApplyTone(new GammaStep(gamma, linear));
    }

    /// <summary>
    /// Levels: the input range [<paramref name="inBlack"/>, <paramref name="inWhite"/>] is stretched to 0..1 (clamped), raised to
    /// <c>1 / gamma</c>, then mapped to [<paramref name="outBlack"/>, <paramref name="outWhite"/>]. All levels are normalized (0..1) so
    /// the same call means the same at 8 and 16 bits; for 8-bit points divide by 255.
    /// </summary>
    /// <param name="inBlack">Input black point, 0 to below <paramref name="inWhite"/>. Default 0.</param>
    /// <param name="inWhite">Input white point, up to 1. Default 1.</param>
    /// <param name="gamma">Midtone gamma, above 1 brightens. Default 1.</param>
    /// <param name="outBlack">Output for black, 0..1. Default 0.</param>
    /// <param name="outWhite">Output for white, 0..1. Default 1 (an output range with white below black inverts).</param>
    /// <param name="channel">One channel or <see cref="ColorChannel.All"/> (the composite). Default All. Gray images only accept All.</param>
    /// <param name="linear">Work in linear light. Default <see langword="false"/>.</param>
    /// <returns>The adjusted image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A level is out of range or the input range is empty.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab, or the channel does not exist.</exception>
    public Image Levels(double inBlack = 0, double inWhite = 1, double gamma = 1, double outBlack = 0, double outWhite = 1, ColorChannel channel = ColorChannel.All, bool linear = false)
    {
        CheckRange(inBlack, 0, 1, nameof(inBlack));
        CheckRange(inWhite, 0, 1, nameof(inWhite));
        CheckRange(outBlack, 0, 1, nameof(outBlack));
        CheckRange(outWhite, 0, 1, nameof(outWhite));
        if (!(inBlack < inWhite))
        {
            throw new ArgumentOutOfRangeException(nameof(inWhite), "The input white point must be above the black point.");
        }

        if (!(gamma > 0 && gamma <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), "Gamma must be in (0, 100].");
        }

        CheckChannel(channel);
        return ApplyTone(new LevelsStep(inBlack, inWhite, gamma, outBlack, outWhite, channel, linear));
    }

    /// <summary>
    /// Maps values through a smooth curve defined by control points (monotone cubic interpolation, so the curve never overshoots
    /// between points). The curve is flat before the first and after the last point; include (0,0) and (1,1) for a full-range curve.
    /// A curve through y = x from 0 to 1 is the identity.
    /// </summary>
    /// <param name="points">At least two points with distinct X, both coordinates in 0..1.</param>
    /// <param name="channel">One channel or <see cref="ColorChannel.All"/>. Default All.</param>
    /// <param name="linear">Work in linear light. Default <see langword="false"/>.</param>
    /// <returns>The adjusted image.</returns>
    /// <exception cref="ArgumentException">Fewer than two points or duplicate X values.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside 0..1.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab, or the channel does not exist.</exception>
    public Image Curves(ReadOnlySpan<CurvePoint> points, ColorChannel channel = ColorChannel.All, bool linear = false)
    {
        CheckChannel(channel);
        return ApplyTone(new CurvesStep(points, channel, linear));
    }

    /// <summary>
    /// Like <see cref="Curves(ReadOnlySpan{CurvePoint}, ColorChannel, bool)"/> with a choice of interpolation. <see cref="CurveInterpolation.Natural"/>
    /// follows Photoshop's curves (a natural cubic spline in 0..1, flat beyond the first and last point, results clamped to 0..1).
    /// </summary>
    /// <param name="points">At least two points with distinct X, both coordinates in 0..1.</param>
    /// <param name="channel">One channel or <see cref="ColorChannel.All"/>.</param>
    /// <param name="linear">Work in linear light.</param>
    /// <param name="interpolation">The interpolation between points.</param>
    /// <returns>The adjusted image.</returns>
    /// <exception cref="ArgumentException">Fewer than two points or duplicate X values.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside 0..1, or <paramref name="interpolation"/> is undefined.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab, or the channel does not exist.</exception>
    public Image Curves(ReadOnlySpan<CurvePoint> points, ColorChannel channel, bool linear, CurveInterpolation interpolation)
    {
        if (interpolation is not (CurveInterpolation.Monotone or CurveInterpolation.Natural))
        {
            throw new ArgumentOutOfRangeException(nameof(interpolation));
        }

        CheckChannel(channel);
        return ApplyTone(new CurvesStep(points, channel, linear, interpolation));
    }

    /// <summary>
    /// Shifts hue, saturation and lightness in the HSL model (values clamped to 0..1 first; pixels with no saturation keep their gray).
    /// Saturation scales S by (1 + saturation); lightness moves L toward white (positive) or black (negative) by that fraction of the remaining distance.
    /// On gray images only lightness has an effect. This differs from the matrix-based saturate and hue-rotate of other libraries,
    /// which preserve luminance.
    /// </summary>
    /// <param name="hue">Rotation in degrees (any value, wraps). Default 0.</param>
    /// <param name="saturation">-1 (gray) to 1 (twice the saturation, clamped). Default 0.</param>
    /// <param name="lightness">-1 (black) to 1 (white). Default 0.</param>
    /// <param name="linear">Work on linear-light values. Default <see langword="false"/>.</param>
    /// <returns>The adjusted image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image HueSaturationLightness(double hue = 0, double saturation = 0, double lightness = 0, bool linear = false)
    {
        if (!double.IsFinite(hue))
        {
            throw new ArgumentOutOfRangeException(nameof(hue));
        }

        CheckRange(saturation, -1, 1, nameof(saturation));
        CheckRange(lightness, -1, 1, nameof(lightness));
        return ApplyTone(new HslStep(hue, saturation, lightness, linear));
    }

    /// <summary>
    /// Inverts colors (lazy): every color channel v becomes 1 - v (255 - v and 65535 - v for integer samples); alpha is unchanged. Float results are not clamped.
    /// </summary>
    /// <param name="linear">Invert in linear light. Default <see langword="false"/> (the familiar negative).</param>
    /// <returns>The inverted image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Invert(bool linear = false) => ApplyTone(new InvertStep(linear));

    /// <summary>Makes every color 0 or 1 (black or white) depending on a level.</summary>
    /// <param name="level">Values at or above this become 1, others 0. Between 0 and 1; default 0.5.</param>
    /// <param name="perChannel">Threshold each channel on its own (giving the eight primary colors) instead of the Rec. 709 luminance. Default <see langword="false"/>. Gray images always use their one channel.</param>
    /// <param name="linear">Compare linear-light values. Default <see langword="false"/>.</param>
    /// <returns>The thresholded image; alpha is unchanged.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 0..1.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Threshold(double level = 0.5, bool perChannel = false, bool linear = false)
    {
        CheckRange(level, 0, 1, nameof(level));
        return ApplyTone(new ThresholdStep(level, perChannel, linear));
    }

    /// <summary>Reduces each color channel to <paramref name="levels"/> evenly spaced values (rounding to the nearest; 0 and 1 are always kept).</summary>
    /// <param name="levels">Number of output values per channel, 2 to 65536.</param>
    /// <param name="linear">Quantize linear-light values. Default <see langword="false"/>.</param>
    /// <returns>The posterized image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="levels"/> is out of range.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Posterize(int levels, bool linear = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levels, 65536);
        return ApplyTone(new PosterizeStep(levels, linear));
    }

    /// <summary>
    /// Like <see cref="Posterize(int, bool)"/> with a choice of rounding. <see cref="PosterizeMode.Buckets"/> is Photoshop's: equal input bins, each mapped
    /// down to its level (see <see cref="PosterizeMode.Buckets"/> for the exact rule on 8-bit samples).
    /// </summary>
    /// <param name="levels">Number of output values per channel, 2 to 65536.</param>
    /// <param name="linear">Quantize linear-light values.</param>
    /// <param name="mode">How inputs map to levels.</param>
    /// <returns>The posterized image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="levels"/> is out of range or <paramref name="mode"/> is undefined.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Posterize(int levels, bool linear, PosterizeMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levels, 65536);
        if (mode is not (PosterizeMode.Nearest or PosterizeMode.Buckets))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        bool eightBit = _frames[0].Format.Sample == SampleType.U8;
        return ApplyTone(new PosterizeStep(levels, linear, mode, eightBit));
    }

    /// <summary>
    /// Mixes RGB channels: each output color is the weighted sum of the input R, G and B (plus an offset with twelve values), one matrix row per output channel. Integer results clamp, float results do not.
    /// Alpha is unchanged.
    /// </summary>
    /// <param name="matrix">Nine values (3x3) or twelve (3x4, the last column being offsets in 0..1 units), row-major.</param>
    /// <param name="linear">Mix linear-light values. Default <see langword="false"/> (Photoshop's channel mixer works on stored values).</param>
    /// <returns>The mixed image.</returns>
    /// <exception cref="ArgumentException">The matrix has the wrong length.</exception>
    /// <exception cref="ImageOperationException">The image is not RGB.</exception>
    public Image ChannelMixer(ReadOnlySpan<double> matrix, bool linear = false) => ApplyTone(new ChannelMixerStep(matrix, linear));

    /// <summary>
    /// Splits into one gray image per stored channel: red, green, blue (or just gray), then alpha if present. Channels are straight values
    /// (premultiplied pixels are un-multiplied first) in the sample type of the source.
    /// </summary>
    /// <returns>The channel images, each lazy.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image[] SplitChannels()
    {
        var result = new Image[Format.ChannelCount];
        for (int k = 0; k < result.Length; k++)
        {
            result[k] = ApplyTone(new ExtractChannelStep(k));
        }

        return result;
    }

    /// <summary>
    /// Combines gray images into one: one channel gives gray, two gray + alpha, three RGB, four RGBA (straight alpha). Metadata and
    /// frame timing come from the first image.
    /// </summary>
    /// <param name="channels">One to four single-channel (gray, no alpha) images of the same size, frame count and sample type.</param>
    /// <returns>The combined image.</returns>
    /// <exception cref="ArgumentException">The count is not 1 to 4, or sizes, frame counts or sample types differ.</exception>
    /// <exception cref="ImageOperationException">An image is not single-channel gray.</exception>
    public static Image CombineChannels(IReadOnlyList<Image> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count is < 1 or > 4)
        {
            throw new ArgumentException("Combine one to four channels.", nameof(channels));
        }

        Image first = channels[0];
        foreach (Image c in channels)
        {
            ArgumentNullException.ThrowIfNull(c);
            if (c.Format.Model != ColorModel.Gray || c.Format.HasAlpha)
            {
                throw new ImageOperationException("Channels must be gray images without alpha.");
            }

            if (c.FrameCount != first.FrameCount || c.Format.Sample != first.Format.Sample)
            {
                throw new ArgumentException("Channels must have the same frame count and sample type.", nameof(channels));
            }

            for (int f = 0; f < c.FrameCount; f++)
            {
                if (c._frames[f].Width != first._frames[f].Width || c._frames[f].Height != first._frames[f].Height)
                {
                    throw new ArgumentException("Channels must have the same size.", nameof(channels));
                }
            }
        }

        if (channels.Count == 1)
        {
            return first;
        }

        var nodes = new Node[first._frames.Length];
        var delays = new TimeSpan[nodes.Length];
        for (int f = 0; f < nodes.Length; f++)
        {
            var inputs = new Node[channels.Count];
            for (int i = 0; i < inputs.Length; i++)
            {
                inputs[i] = channels[i]._frames[f].Node;
            }

            nodes[f] = new CombineChannelsNode(inputs);
            delays[f] = first._frames[f].Delay;
        }

        return new Image(nodes, delays, first.LoopCount, first.Metadata, first.SourceFormat);
    }

    /// <summary>Adds an alpha channel of constant opacity. Images that already have alpha are returned unchanged.</summary>
    /// <param name="alpha">Opacity of every pixel, 0..1. Default 1 (opaque). The result has straight alpha.</param>
    /// <returns>The image with alpha.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="alpha"/> is outside 0..1.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image AddAlpha(double alpha = 1)
    {
        CheckRange(alpha, 0, 1, nameof(alpha));
        return ApplyTone(new AddAlphaStep(alpha));
    }

    /// <summary>Drops the alpha channel, keeping the color values as they are (straight color; no flattening). Use <see cref="Flatten"/> to composite first.</summary>
    /// <returns>The image without alpha, or this image if it has none.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image RemoveAlpha() => ApplyTone(new RemoveAlphaStep());

    /// <summary>
    /// Composites onto an opaque background color (source-over: <c>c * a + bg * (1 - a)</c>) and drops alpha. Fully opaque pixels keep their
    /// color exactly and fully transparent ones become the background exactly. Images without alpha are returned unchanged.
    /// </summary>
    /// <param name="background">The background as sRGB; its own alpha is ignored. For gray images its Rec. 709 luminance of the stored values is used.</param>
    /// <param name="linear">Blend in linear light. Default <see langword="false"/>: blending in the stored space is what editors do by default.</param>
    /// <returns>The flattened image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Flatten(Rgba32 background, bool linear = false) => ApplyTone(new FlattenStep(background, linear));

    private Image ApplyTone(ToneStep step)
    {
        foreach (ImageFrame f in _frames)
        {
            step.Validate(f.Format);
        }

        return Map(static (n, s) => ToneNode.Build(n, s), step);
    }

    private static void CheckRange(double v, double min, double max, string name)
    {
        if (!(v >= min && v <= max))
        {
            throw new ArgumentOutOfRangeException(name, $"Must be between {min} and {max}.");
        }
    }

    private void CheckChannel(ColorChannel channel)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        if (Format.Model == ColorModel.Gray && channel != ColorChannel.All)
        {
            throw new ImageOperationException("Gray images have only the All channel.");
        }
    }
}
