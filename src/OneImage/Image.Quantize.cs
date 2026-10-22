using OneImage.Ops.Quantize;

namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Limits every frame to a palette of at most <paramref name="colors"/> colors (lazy). The result keeps an ordinary RGB(A) 8-bit
    /// format, so it can be processed, saved to any format, or turned into an indexed file by an encoder that wants palettes.
    /// </summary>
    /// <remarks>
    /// <para>The palette is chosen per frame with Wu's variance-minimizing box splitting, refined with two k-means passes.
    /// A frame that already has no more distinct colors than requested is returned unchanged. Input of any bit depth is
    /// reduced to 8 bits per channel first; gray input becomes RGB.</para>
    /// <para>Output is <see cref="PixelFormat.Rgba8"/> when the input has an alpha channel and <see cref="PixelFormat.Rgb8"/> otherwise.</para>
    /// <para>Because the palette needs statistics of the whole frame and error diffusion runs in row order, the first tile request
    /// computes the frame in two forward passes over its input and keeps the result in memory. Results are identical regardless of
    /// threading or which tile is asked for first.</para>
    /// <para>Graph descriptor: <c>quantize(colors, dither, alpha[, quantizer])</c>.</para>
    /// </remarks>
    /// <param name="colors">The most colors in the result, 2 to 256 (default 256). With <see cref="QuantizeAlpha.Binary"/> a transparent entry counts.</param>
    /// <param name="dither">Dithering mode (default <see cref="Dither.None"/>).</param>
    /// <param name="alpha">Alpha handling (default <see cref="QuantizeAlpha.Full"/>: alpha is quantized with the color). Only <see cref="Quantizer.Wu"/> quantizes alpha; the other choices treat it as a switch (alpha below 128 is transparent) whatever this says.</param>
    /// <param name="quantizer">Where the palette comes from (default <see cref="Quantizer.Wu"/>). <see cref="Quantizer.WebSafe"/> and <see cref="Quantizer.Werner"/> use their fixed palettes (plus a transparent entry when needed) and ignore <paramref name="colors"/>.</param>
    /// <returns>The quantized image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="colors"/> is outside 2 to 256, or an enum argument is undefined.</exception>
    /// <exception cref="ImageOperationException">The pixel format is CMYK or Lab.</exception>
    public Image Quantize(int colors = 256, Dither dither = OneImage.Dither.None, QuantizeAlpha alpha = QuantizeAlpha.Full, Quantizer quantizer = Quantizer.Wu)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(colors, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(colors, 256);
        if (!Enum.IsDefined(dither))
        {
            throw new ArgumentOutOfRangeException(nameof(dither));
        }

        if (!Enum.IsDefined(alpha))
        {
            throw new ArgumentOutOfRangeException(nameof(alpha));
        }

        if (!Enum.IsDefined(quantizer))
        {
            throw new ArgumentOutOfRangeException(nameof(quantizer));
        }

        foreach (ImageFrame f in _frames)
        {
            if (f.Format.Model is not (ColorModel.Gray or ColorModel.Rgb))
            {
                throw new ImageOperationException($"Quantize is not defined for {f.Format}.");
            }
        }

        return Map(static (n, p) => new QuantizeNode(n, p.colors, p.dither, p.alpha, p.quantizer), (colors, dither, alpha, quantizer));
    }
}
