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
}
