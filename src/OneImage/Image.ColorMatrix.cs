namespace OneImage;

// Color-matrix operations and the preset looks built on them. They are tone-class ops: lazy, per pixel, mapped over every frame, and
// fused with adjacent color ops into one pass per tile (see ToneNode). The matrix works on the stored (encoded) values, as in ImageSharp.
public sealed partial class Image
{
    /// <summary>
    /// Applies a 5x4 <see cref="ColorMatrix"/> to every pixel: <c>out = in * matrix</c> on straight RGBA in 0..1 (see <see cref="ColorMatrix"/> for the
    /// layout, which is ImageSharp's <c>Filter(ColorMatrix)</c>). The work is done on the stored (encoded) values, not in linear light.
    /// </summary>
    /// <remarks>
    /// Integer results round half up and clamp to the sample range; float results are not clamped (alpha always is). Color is straight,
    /// so semi-transparent pixels keep their color unless the matrix changes it, and the matrix may read and write alpha. Gray images become
    /// RGB (the one gray channel feeds red, green and blue alike) unless the matrix is the identity. An image without alpha gains straight
    /// alpha when the matrix produces anything other than 1 (for example <see cref="Opacity"/>). The identity matrix returns this image.
    /// 8-bit RGB and RGBA run a direct vector kernel; the result is the same bytes as the generic float path at any hardware level.
    /// </remarks>
    /// <param name="matrix">The matrix. Every value must be finite with a magnitude of at most 1,000,000.</param>
    /// <returns>The transformed image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A matrix value is not finite or is too large.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image ApplyColorMatrix(ColorMatrix matrix) => ApplyTone(new ColorMatrixStep(matrix));

    /// <summary>
    /// Sepia tone: red, green and blue become the classic warm weighted mixes (0.393, 0.769, 0.189 and so on, as in ImageSharp), blended
    /// linearly with the original by <paramref name="amount"/>. Alpha is unchanged. Gray images become RGB.
    /// </summary>
    /// <param name="amount">0 (no change, returns this image) to 1 (full sepia). Default 1.</param>
    /// <returns>The toned image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Sepia(float amount = 1f) => ApplyColorMatrix(ColorMatrix.Sepia(amount));

    /// <summary>
    /// High-contrast black and white: every color channel becomes <c>1.5 * (R + G + B) - 1</c>, clamped, as in ImageSharp. Unlike
    /// <see cref="Grayscale"/> it is not a luminance conversion: it is bright, hard and clips. Alpha is unchanged.
    /// </summary>
    /// <returns>The black-and-white image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image BlackWhite() => ApplyColorMatrix(ColorMatrix.BlackWhite);

    /// <summary>Kodachrome film look (ImageSharp's matrix): warm, saturated reds, slightly lifted blue. Alpha is unchanged.</summary>
    /// <returns>The filtered image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Kodachrome() => ApplyColorMatrix(ColorMatrix.Kodachrome);

    /// <summary>
    /// The Lomograph look as ImageSharp's <c>Lomograph()</c> renders it: <see cref="ColorMatrix.Lomograph"/> (strong contrast and saturation with a
    /// dark, cool cast), then a very dark green (0, 10, 0) <see cref="Vignette"/> over the whole image with the default radii. The matrix alone is available as
    /// <c>ApplyColorMatrix(ColorMatrix.Lomograph)</c>. The two steps are separate, so the result is rounded to the sample type between them; it
    /// agrees with ImageSharp to within a level or two at 8 bits. Alpha is unchanged by the matrix; the vignette blends over the pixel as described there.
    /// </summary>
    /// <returns>The filtered image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Lomograph() => ApplyColorMatrix(ColorMatrix.Lomograph).Vignette(new Rgba32(0, 10, 0, 255));

    /// <summary>
    /// The Polaroid look as ImageSharp's <c>Polaroid()</c> renders it: <see cref="ColorMatrix.Polaroid"/> (warm, contrasty), then a dark-orange
    /// <see cref="Vignette"/> (color 102, 34, 0) with the default radii, then an orange <see cref="Glow"/> at half strength in the center (color 255, 153, 102, alpha 128,
    /// radius a quarter of the width). The matrix alone is available as <c>ApplyColorMatrix(ColorMatrix.Polaroid)</c>. The steps are separate, so the
    /// result is rounded to the sample type between them; it agrees with ImageSharp to within a level or two at 8 bits.
    /// </summary>
    /// <returns>The filtered image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Polaroid() => ApplyColorMatrix(ColorMatrix.Polaroid).Vignette(new Rgba32(102, 34, 0, 255)).Glow(new Rgba32(255, 153, 102, 128), Width / 4.0);

    /// <summary>
    /// Simulates a color vision deficiency with ImageSharp's matrices, on the stored values. Alpha is unchanged. Gray images become RGB.
    /// </summary>
    /// <param name="mode">The deficiency to simulate.</param>
    /// <returns>The simulated image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image ColorBlindness(ColorBlindnessMode mode) => ApplyColorMatrix(ColorMatrix.ColorBlindness(mode));

    /// <summary>
    /// Multiplies every pixel's alpha by <paramref name="amount"/>; color is untouched (straight alpha). An image without alpha gains it.
    /// </summary>
    /// <param name="amount">0 (fully transparent) to 1 (unchanged, returns this image).</param>
    /// <returns>The image with scaled opacity.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Opacity(float amount) => ApplyColorMatrix(ColorMatrix.Opacity(amount));
}
