namespace OneImage;

/// <summary>How <see cref="Image.Resize"/> fits the image into the requested width and height.</summary>
public enum Fit
{
    /// <summary>Scale so the whole image fits inside the box, keeping the aspect ratio. The result may be smaller than the box on one axis. This is the default.</summary>
    Contain = 0,

    /// <summary>Scale so the image covers the whole box, keeping the aspect ratio, then crop the overflow (see <see cref="Anchor"/>). The result is exactly the box size.</summary>
    Cover = 1,

    /// <summary>Scale each axis independently to the box size, ignoring the aspect ratio.</summary>
    Stretch = 2,

    /// <summary>Like <see cref="Contain"/>, then pad with the background color to exactly the box size, placing the image by <see cref="Anchor"/>.</summary>
    Pad = 3,

    /// <summary>
    /// Like <see cref="Pad"/> but never enlarges: an image that fits inside the box keeps its size and sits in the box at the
    /// <see cref="Anchor"/>; a larger one is shrunk to fit first. The result is exactly the box size. The <c>upscale</c> argument is
    /// ignored. When only one side is given the other follows the aspect ratio and the same rules apply. This is ImageSharp's
    /// <c>ResizeMode.BoxPad</c>.
    /// </summary>
    BoxPad = 4,

    /// <summary>
    /// Scale so the image covers the box, keeping the aspect ratio, without cropping, so the result is at least the box size on
    /// both axes (the shortest side matches the box). Never enlarges: when the box is wider or taller than the image, the image is
    /// returned at its own size. The <c>upscale</c> argument is ignored. When only one side is given the other follows the aspect
    /// ratio. This is ImageSharp's <c>ResizeMode.Min</c>.
    /// </summary>
    Min = 5,

    /// <summary>
    /// Alias of <see cref="Contain"/>: scale so the whole image fits inside the box, keeping the aspect ratio, and enlarge when the
    /// image is smaller (with the default <c>upscale: true</c>). This is ImageSharp's <c>ResizeMode.Max</c>.
    /// </summary>
    Max = Contain,

    /// <summary>Alias of <see cref="Cover"/> under ImageSharp's name for the default mode (<c>ResizeMode.Crop</c>).</summary>
    Crop = Cover,
}

/// <summary>Where the image sits when a <see cref="Fit"/> crops (<see cref="Fit.Cover"/>) or pads (<see cref="Fit.Pad"/>) it.</summary>
public enum Anchor
{
    /// <summary>Centered on both axes; an odd leftover pixel goes to the bottom/right. This is the default.</summary>
    Center = 0,

    /// <summary>Top-left corner.</summary>
    TopLeft = 1,

    /// <summary>Top edge, centered horizontally.</summary>
    Top = 2,

    /// <summary>Top-right corner.</summary>
    TopRight = 3,

    /// <summary>Left edge, centered vertically.</summary>
    Left = 4,

    /// <summary>Right edge, centered vertically.</summary>
    Right = 5,

    /// <summary>Bottom-left corner.</summary>
    BottomLeft = 6,

    /// <summary>Bottom edge, centered horizontally.</summary>
    Bottom = 7,

    /// <summary>Bottom-right corner.</summary>
    BottomRight = 8,

    /// <summary>
    /// For <see cref="Fit.Cover"/>: crop to the region with the most detail. The covering image is reduced to at most 128 pixels,
    /// the sum of Sobel gradient magnitudes of its luma is computed for every candidate window along the cropped axis, and the
    /// window with the largest sum wins (ties go to the one closest to the center). The analysis runs lazily, the first time
    /// pixels are pulled. For other fits it behaves like <see cref="Center"/>.
    /// </summary>
    Smart = 9,
}

/// <summary>The reconstruction filter used by resampling.</summary>
public enum ResamplingFilter
{
    /// <summary>Lanczos3 when an axis shrinks and Catmull-Rom when it grows (decided per axis). This is the default.</summary>
    Auto = 0,

    /// <summary>Nearest neighbor: each output pixel copies the source pixel containing its center.</summary>
    Nearest = 1,

    /// <summary>Area average: each output pixel is the exact coverage-weighted mean of the source pixels it overlaps.</summary>
    Box = 2,

    /// <summary>Triangle filter (linear interpolation, widened when shrinking).</summary>
    Bilinear = 3,

    /// <summary>Catmull-Rom cubic (B = 0, C = 1/2).</summary>
    CatmullRom = 4,

    /// <summary>Mitchell–Netravali cubic (B = C = 1/3).</summary>
    Mitchell = 5,

    /// <summary>Lanczos windowed sinc with two lobes.</summary>
    Lanczos2 = 6,

    /// <summary>Lanczos windowed sinc with three lobes.</summary>
    Lanczos3 = 7,

    /// <summary>Hermite cubic (B = 0, C = 0): <c>2|x|³ − 3|x|² + 1</c> on |x| &lt; 1. Support radius 1. Smooth but soft; no negative lobes.</summary>
    Hermite = 8,

    /// <summary>Lanczos windowed sinc with five lobes (support radius 5).</summary>
    Lanczos5 = 9,

    /// <summary>Lanczos windowed sinc with eight lobes (support radius 8).</summary>
    Lanczos8 = 10,

    /// <summary>Robidoux cubic (Mitchell–Netravali family with B ≈ 0.3782, C ≈ 0.3109), the "Keys-like" filter tuned for minimal blur and ringing on axis-aligned scaling. Support radius 2.</summary>
    Robidoux = 11,

    /// <summary>Robidoux Sharp cubic (B ≈ 0.2620, C ≈ 0.3690): a sharper variant of <see cref="Robidoux"/>. Support radius 2.</summary>
    RobidouxSharp = 12,

    /// <summary>Cubic B-spline (B = 1, C = 0): very smooth and blurry, never rings. Support radius 2.</summary>
    Spline = 13,

    /// <summary>Sinc windowed by a Welch parabola: <c>sinc(x)·(1 − (x/3)²)</c> on |x| &lt; 3, the kernel ImageSharp calls Welch. Support radius 3.</summary>
    Welch = 14,

    /// <summary>Alias of <see cref="CatmullRom"/> under the name ImageSharp and System.Drawing users know (<c>Bicubic</c>).</summary>
    Bicubic = CatmullRom,

    /// <summary>Alias of <see cref="Bilinear"/> under ImageSharp's name (<c>Triangle</c>).</summary>
    Triangle = Bilinear,
}
