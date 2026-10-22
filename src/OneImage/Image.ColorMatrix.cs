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
    /// Lomograph color shift (ImageSharp's matrix): strong contrast and saturation with a dark, cool cast. This is the color part of the
    /// look only: ImageSharp follows its matrix with a corner-darkening vignette, which is not applied here. Alpha is unchanged.
    /// </summary>
    /// <returns>The filtered image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Lomograph() => ApplyColorMatrix(ColorMatrix.Lomograph);

    /// <summary>
    /// Polaroid color shift (ImageSharp's matrix): warm, contrasty and slightly faded. This is the color part of the look only: ImageSharp
    /// follows its matrix with a vignette and tint, which are not applied here. Alpha is unchanged.
    /// </summary>
    /// <returns>The filtered image.</returns>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    public Image Polaroid() => ApplyColorMatrix(ColorMatrix.Polaroid);

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
