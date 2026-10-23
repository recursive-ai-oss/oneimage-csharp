namespace OneImage.Layers;

/// <summary>
/// The operation of an <see cref="AdjustmentLayer"/>: a named, serializable point operation. A <i>point</i> operation maps each pixel
/// to a new pixel by itself (levels, curves, hue/saturation, color matrix, invert, …); the renderer applies it to one tile at a
/// time, so an operation that looks at neighbors (blur, sharpen, resize) would show tile seams and is not accepted by the built-in
/// factories. Every factory documents the defaults of the underlying <see cref="Image"/> method it calls; the adjustment acts on
/// straight (non-premultiplied) color in the document's pixel format and leaves alpha alone.
/// </summary>
public sealed class Adjustment
{
    private readonly Func<Image, Image> _apply;

    private Adjustment(string name, IReadOnlyList<OpParameter> parameters, Func<Image, Image> apply)
    {
        Name = name;
        Parameters = parameters;
        _apply = apply;
    }

    /// <summary>Gets the language-neutral name, e.g. <c>levels</c>, <c>curves</c>, <c>hue-saturation</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the parameters (long, double, bool or string values) that describe the operation; empty for <see cref="Custom"/>.</summary>
    public IReadOnlyList<OpParameter> Parameters { get; }

    internal Image Apply(Image image) => _apply(image);

    /// <summary>Levels: see <see cref="Image.Levels"/>. All values are 0..1 on the encoded scale unless <paramref name="linear"/>.</summary>
    /// <param name="inBlack">Input black point.</param>
    /// <param name="inWhite">Input white point, above <paramref name="inBlack"/>.</param>
    /// <param name="gamma">Midtone gamma, above 0; 1 leaves the midtones.</param>
    /// <param name="outBlack">Output black point.</param>
    /// <param name="outWhite">Output white point.</param>
    /// <param name="channel">The channel to adjust; the default is all color channels.</param>
    /// <param name="linear">Work in linear light; the default is the encoded values.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment Levels(double inBlack = 0, double inWhite = 1, double gamma = 1, double outBlack = 0, double outWhite = 1, ColorChannel channel = ColorChannel.All, bool linear = false)
    {
        // Validate now rather than at the first render.
        _ = Probe().Levels(inBlack, inWhite, gamma, outBlack, outWhite, channel, linear);
        return new("levels", [new("in-black", inBlack), new("in-white", inWhite), new("gamma", gamma), new("out-black", outBlack), new("out-white", outWhite), new("channel", channel.ToString().ToLowerInvariant()), new("linear", linear)],
            i => i.Levels(inBlack, inWhite, gamma, outBlack, outWhite, channel, linear));
    }

    /// <summary>Curves through control points (monotone interpolation), see <see cref="Image.Curves"/>.</summary>
    /// <param name="points">At least two points with X and Y in 0..1.</param>
    /// <param name="channel">The channel to adjust; the default is all color channels.</param>
    /// <param name="linear">Work in linear light; the default is the encoded values.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment Curves(IReadOnlyList<CurvePoint> points, ColorChannel channel = ColorChannel.All, bool linear = false)
    {
        ArgumentNullException.ThrowIfNull(points);
        CurvePoint[] copy = [.. points];
        _ = Probe().Curves(copy, channel, linear);
        string text = string.Join(';', copy.Select(static p => Layer.Num(p.X) + "," + Layer.Num(p.Y)));
        return new("curves", [new("points", text), new("channel", channel.ToString().ToLowerInvariant()), new("linear", linear)], i => i.Curves(copy, channel, linear));
    }

    /// <summary>Hue, saturation and lightness shifts, see <see cref="Image.HueSaturationLightness"/>.</summary>
    /// <param name="hue">Hue shift in degrees.</param>
    /// <param name="saturation">Saturation change, -1..1.</param>
    /// <param name="lightness">Lightness change, -1..1.</param>
    /// <param name="linear">Work in linear light; the default is the encoded values.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment HueSaturation(double hue = 0, double saturation = 0, double lightness = 0, bool linear = false)
    {
        _ = Probe().HueSaturationLightness(hue, saturation, lightness, linear);
        return new("hue-saturation", [new("hue", hue), new("saturation", saturation), new("lightness", lightness), new("linear", linear)],
            i => i.HueSaturationLightness(hue, saturation, lightness, linear));
    }

    /// <summary>Brightness and contrast, see <see cref="Image.BrightnessContrast"/>.</summary>
    /// <param name="brightness">Brightness change, -1..1.</param>
    /// <param name="contrast">Contrast change, -1..1.</param>
    /// <param name="linear">Work in linear light; the default is the encoded values.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment BrightnessContrast(double brightness = 0, double contrast = 0, bool linear = false)
    {
        _ = Probe().BrightnessContrast(brightness, contrast, linear);
        return new("brightness-contrast", [new("brightness", brightness), new("contrast", contrast), new("linear", linear)], i => i.BrightnessContrast(brightness, contrast, linear));
    }

    /// <summary>Exposure in stops, see <see cref="Image.Exposure"/> (linear light by default).</summary>
    /// <param name="stops">Stops to add; negative darkens.</param>
    /// <param name="linear">Work in linear light, the default.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment Exposure(double stops, bool linear = true)
    {
        _ = Probe().Exposure(stops, linear);
        return new("exposure", [new("stops", stops), new("linear", linear)], i => i.Exposure(stops, linear));
    }

    /// <summary>Gamma, see <see cref="Image.Gamma"/>.</summary>
    /// <param name="gamma">Gamma above 0; 1 changes nothing.</param>
    /// <param name="linear">Work in linear light; the default is the encoded values.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment Gamma(double gamma, bool linear = false)
    {
        _ = Probe().Gamma(gamma, linear);
        return new("gamma", [new("gamma", gamma), new("linear", linear)], i => i.Gamma(gamma, linear));
    }

    /// <summary>Inverts the color channels, see <see cref="Image.Invert"/>.</summary>
    /// <param name="linear">Invert in linear light; the default is the encoded values.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment Invert(bool linear = false) => new("invert", [new("linear", linear)], i => i.Invert(linear));

    /// <summary>Thresholds to black and white, see <see cref="Image.Threshold"/>.</summary>
    /// <param name="level">The cut, 0..1.</param>
    /// <param name="perChannel">Threshold each channel on its own instead of the luminance.</param>
    /// <param name="linear">Work in linear light; the default is the encoded values.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment Threshold(double level = 0.5, bool perChannel = false, bool linear = false)
    {
        _ = Probe().Threshold(level, perChannel, linear);
        return new("threshold", [new("level", level), new("per-channel", perChannel), new("linear", linear)], i => i.Threshold(level, perChannel, linear));
    }

    /// <summary>Reduces each channel to a number of levels, see <see cref="Image.Posterize"/>.</summary>
    /// <param name="levels">Levels per channel, 2 or more.</param>
    /// <param name="linear">Work in linear light; the default is the encoded values.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment Posterize(int levels, bool linear = false)
    {
        _ = Probe().Posterize(levels, linear);
        return new("posterize", [new("levels", (long)levels), new("linear", linear)], i => i.Posterize(levels, linear));
    }

    /// <summary>Desaturates to gray values kept in the document's format, see <see cref="Image.Grayscale"/> (linear light by default).</summary>
    /// <param name="linear">Weigh the channels in linear light, the default.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment Grayscale(bool linear = true) => new("grayscale", [new("linear", linear)], i => i.Grayscale(linear));

    /// <summary>A 5×4 color matrix on straight, encoded values, see <see cref="ColorMatrix"/>.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment ColorMatrix(ColorMatrix matrix)
    {
        var p = new List<OpParameter>(20);
        for (int r = 0; r < 5; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                p.Add(new($"m{r + 1}{c + 1}", (double)matrix[r, c]));
            }
        }

        return new("color-matrix", p, i => i.ApplyColorMatrix(matrix));
    }

    /// <summary>
    /// An adjustment from your own point operation. It must be a pure per-pixel function of the input image (no resampling, no neighbors, no
    /// dependence on the image size) that returns an image of the same size and format, and it must be safe to call from several
    /// threads. It has no parameters, so a document using it cannot be rebuilt from its description.
    /// </summary>
    /// <param name="name">A name for the description.</param>
    /// <param name="pointOperation">The operation.</param>
    /// <returns>The adjustment.</returns>
    public static Adjustment Custom(string name, Func<Image, Image> pointOperation)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(pointOperation);
        return new(name, [], pointOperation);
    }

    private static Image Probe() => Image.FromPixels<Rgba32>([default], 1, 1);
}
