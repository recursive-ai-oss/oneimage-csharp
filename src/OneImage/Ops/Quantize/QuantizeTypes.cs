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
