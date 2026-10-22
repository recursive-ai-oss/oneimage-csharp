namespace OneImage;

/// <summary>How <see cref="Image.Quantize"/> and the GIF encoder spread the error of limiting an image to a palette.</summary>
public enum Dither
{
    /// <summary>Every pixel takes its nearest palette color. Smallest output, visible banding in gradients.</summary>
    None = 0,

    /// <summary>
    /// Floyd–Steinberg error diffusion, left to right, top to bottom, with integer arithmetic (weights 7, 3, 5, 1 over 16).
    /// The result does not depend on threading because the pass runs in row order.
    /// </summary>
    FloydSteinberg = 1,

    /// <summary>Ordered dithering with an 8×8 Bayer matrix. Pixels are independent, so it has no sequential state and compresses better than error diffusion.</summary>
    Ordered = 2,

    /// <summary>Atkinson error diffusion (divisor 8, six neighbours at weight 1: a quarter of the error is dropped, which keeps highlights and shadows clean).</summary>
    Atkinson = 3,

    /// <summary>Burkes error diffusion (divisor 32, two rows).</summary>
    Burkes = 4,

    /// <summary>Jarvis–Judice–Ninke error diffusion (divisor 48, three rows).</summary>
    JarvisJudiceNinke = 5,

    /// <summary>Sierra two-row error diffusion (divisor 16).</summary>
    Sierra2 = 6,

    /// <summary>Sierra three-row error diffusion (divisor 32).</summary>
    Sierra3 = 7,

    /// <summary>Sierra Lite error diffusion (divisor 4, two rows, three taps).</summary>
    SierraLite = 8,

    /// <summary>Stevenson–Arce error diffusion (divisor 200, four rows).</summary>
    StevensonArce = 9,

    /// <summary>Stucki error diffusion (divisor 42, three rows).</summary>
    Stucki = 10,

    /// <summary>Ordered dithering with a 2×2 Bayer matrix.</summary>
    Bayer2x2 = 11,

    /// <summary>Ordered dithering with a 3×3 matrix.</summary>
    Ordered3x3 = 12,

    /// <summary>Ordered dithering with a 4×4 Bayer matrix.</summary>
    Bayer4x4 = 13,

    /// <summary>Ordered dithering with an 8×8 Bayer matrix (<see cref="Image.Dither(IReadOnlyList{Rgba32}, Dither, float, bool, CancellationToken)"/> treats <see cref="Ordered"/> as this mode).</summary>
    Bayer8x8 = 14,

    /// <summary>Ordered dithering with a 16×16 Bayer matrix.</summary>
    Bayer16x16 = 15,
}

/// <summary>Where the palette of <see cref="Image.Quantize(int, Dither, QuantizeAlpha, Quantizer)"/> and the GIF encoder comes from.</summary>
public enum Quantizer
{
    /// <summary>Wu's variance-minimizing box splitting refined by two k-means passes. Best quality; the default.</summary>
    Wu = 0,

    /// <summary>
    /// An adaptive octree over 8-bit RGB: colors are merged bottom-up, least-used branches first, until the palette fits.
    /// Faster than <see cref="Wu"/> and a little coarser.
    /// </summary>
    Octree = 1,

    /// <summary>The CSS Color Module Level 4 named colors (<see cref="KnownPalettes.WebSafe"/>); the requested color count is ignored.</summary>
    WebSafe = 2,

    /// <summary>The 110 colors of Werner's Nomenclature of Colours (<see cref="KnownPalettes.Werner"/>); the requested color count is ignored.</summary>
    Werner = 3,
}

/// <summary>How the alpha channel takes part in color quantization.</summary>
public enum QuantizeAlpha
{
    /// <summary>
    /// Alpha is a switch: pixels with alpha below 128 become one fully transparent palette entry, all others become opaque.
    /// This is what GIF and palette-based PNG without per-entry alpha can express.
    /// </summary>
    Binary = 0,

    /// <summary>Alpha is quantized together with the color (palette entries carry their own alpha).</summary>
    Full = 1,
}
