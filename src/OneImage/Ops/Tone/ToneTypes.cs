namespace OneImage;

/// <summary>Selects which color channel an operation touches.</summary>
public enum ColorChannel
{
    /// <summary>Every color channel (the only valid choice for gray images). Alpha is never included.</summary>
    All = 0,

    /// <summary>The red channel of an RGB image.</summary>
    Red = 1,

    /// <summary>The green channel of an RGB image.</summary>
    Green = 2,

    /// <summary>The blue channel of an RGB image.</summary>
    Blue = 3,
}

/// <summary>How <see cref="Image.Posterize(int, bool, PosterizeMode)"/> assigns input values to output levels.</summary>
public enum PosterizeMode
{
    /// <summary>Each value goes to the nearest of the evenly spaced output levels (the behavior of <see cref="Image.Posterize(int, bool)"/>).</summary>
    Nearest = 0,

    /// <summary>
    /// Photoshop's buckets: the input range is cut into <c>levels</c> equal bins and every value in bin k becomes level k, rounded down.
    /// On 8-bit samples the bins are <c>floor(v * levels / 256)</c> and the output is <c>floor(k * 255 / (levels - 1))</c>, so 0 and 255
    /// are both reached and every output level covers about the same number of inputs. Other sample types use <c>floor(v * levels)</c>
    /// and output <c>k / (levels - 1)</c>.
    /// </summary>
    Buckets = 1,
}

/// <summary>How <see cref="Image.Curves(ReadOnlySpan{CurvePoint}, ColorChannel, bool, CurveInterpolation)"/> interpolates between control points.</summary>
public enum CurveInterpolation
{
    /// <summary>Monotone cubic (Fritsch-Carlson): never overshoots between points. The default of <see cref="Image.Curves(ReadOnlySpan{CurvePoint}, ColorChannel, bool)"/>.</summary>
    Monotone = 0,

    /// <summary>
    /// Natural cubic spline (zero curvature at the end points), which is what Photoshop's curves follow: the curve may overshoot and
    /// wiggle between points, and points whose outputs are not in order give a non-monotone curve. Results are clamped to 0..1.
    /// </summary>
    Natural = 1,
}

/// <summary>Which formula <see cref="Image.BrightnessContrast(double, double, bool, BrightnessContrastMode)"/> uses.</summary>
public enum BrightnessContrastMode
{
    /// <summary>Contrast as a slope around mid-gray and brightness added afterwards (the behavior of <see cref="Image.BrightnessContrast(double, double, bool)"/>).</summary>
    Linear = 0,

    /// <summary>
    /// Photoshop's "Use Legacy" brightness/contrast. Brightness is <c>brightness * 150</c> levels and contrast is <c>contrast * 100</c> percent,
    /// working on the 0..255 scale around 127. With <c>c8 = trunc(contrast * 255)</c>: a positive contrast has slope <c>255 / (255 - c8)</c> and
    /// the brightness is added before it, <c>out = k * (v + b - 127) + 127</c>; a negative contrast has slope <c>(255 + c8) / 255</c> and the
    /// brightness is added after it, <c>out = k * (v - 127) + 127 + b</c>. Results clamp to the range. Exact on 8-bit samples (derived from stored
    /// Photoshop renders); other sample types use the same formula on values scaled to 0..255.
    /// </summary>
    PhotoshopLegacy = 1,

    /// <summary>
    /// An approximation of Photoshop's current (non-legacy) brightness/contrast, which bends the tone curve instead of shifting it: a quadratic bow
    /// <c>y = x + 1.7 * brightness * x * (1 - x)</c> followed by a soft contrast slope about 0.5 (the legacy slope raised to the power 0.2).
    /// Fitted to four stored Photoshop renders (mean error 5 to 8 levels on three of them, about 25 on the fourth); the exact curve is not
    /// documented. Brightness and contrast scale as in <see cref="PhotoshopLegacy"/>.
    /// </summary>
    PhotoshopSmooth = 2,
}

/// <summary>A control point of a tone curve. Both coordinates are normalized to 0..1 (input on X, output on Y).</summary>
/// <param name="X">Input level, 0..1.</param>
/// <param name="Y">Output level, 0..1.</param>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// The working row of a fused tone program: up to four float planes (colors 0..2, alpha in plane 3) holding straight
/// (non-premultiplied) values, one image row wide.
/// </summary>
internal ref struct ToneRow
{
    public Span<float> Buf;
    public int Width;
    public int Colors;
    public bool HasAlpha;

    /// <summary>Sample type of the format this row currently represents; steps that depend on the grid of stored values read it.</summary>
    public SampleType Sample;

    public readonly Span<float> Plane(int plane) => Buf.Slice(plane * Tile.Size, Width);
}
