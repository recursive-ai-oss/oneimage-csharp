namespace OneImage;

/// <summary>Which value of a mask image becomes the coverage in <see cref="Image.ApplyMask"/>.</summary>
public enum MaskChannel
{
    /// <summary>The mask's alpha.</summary>
    Alpha = 0,

    /// <summary>The mask's luminance times its alpha (the luminance of the premultiplied color), with weights 0.2125, 0.7154 and 0.0721.</summary>
    Luminance = 1,
}

public sealed partial class Image
{
    /// <summary>
    /// Multiplies this image's coverage by a per-pixel mask taken from <paramref name="mask"/> (lazy): where the mask is 0 the result is
    /// transparent, where it is 1 the image is unchanged, and in between the pixel is faded, as with a layer mask in an image editor.
    /// <para>
    /// <b>Mask value.</b> With <see cref="MaskChannel.Alpha"/> the coverage is the mask's alpha. With <see cref="MaskChannel.Luminance"/> it is
    /// the mask's luminance (weights 0.2125, 0.7154 and 0.0721, as CSS Masking defines for <c>mask-type: luminance</c>) times its alpha, so
    /// transparent pixels hide whatever color they carry. The luminance is computed on the stored (encoded) values, or on linear-light
    /// values with <paramref name="linear"/>, which uses the sRGB transfer curve. The mask is first converted to 8-bit RGBA, so its
    /// coverage has 256 levels; the luminance sum is exact in 16-bit fixed point and rounded to nearest.
    /// </para>
    /// <para>
    /// <b>Geometry.</b> The mask's top-left corner sits at (<paramref name="x"/>, <paramref name="y"/>) in this image. It may be any size and
    /// may lie partly or fully outside the image; <b>everything this image has outside the mask is made transparent</b>, as if the mask were
    /// 0 there. A pixel that ends with alpha 0 is transparent black (its color is dropped). Only the tiles the mask touches read it; tiles it misses are transparent without reading this image's pixels.
    /// </para>
    /// <para>
    /// <b>Result.</b> The size, format, alpha mode and metadata of this image. The image must have an alpha channel (add one first
    /// otherwise). 8-bit straight RGBA keeps its colors and gets the alpha <c>round(a·m)</c> computed in integers; every other format is
    /// scaled as premultiplied 32-bit float and rounded half up on output. Results are identical on every hardware path and for any thread count.
    /// </para>
    /// <para>
    /// <b>Animation.</b> Every frame of this image gets the mask's first frame.
    /// </para>
    /// </summary>
    /// <param name="mask">The image the coverage comes from.</param>
    /// <param name="channel">Which mask value to use. The default is <see cref="MaskChannel.Alpha"/>.</param>
    /// <param name="x">Horizontal position of the mask's left edge in this image; may be negative.</param>
    /// <param name="y">Vertical position of the mask's top edge in this image; may be negative.</param>
    /// <param name="linear">Compute the luminance in linear light. Ignored for <see cref="MaskChannel.Alpha"/>. The default is <see langword="false"/>.</param>
    /// <returns>The masked image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is undefined or an offset is beyond ±2²⁸.</exception>
    /// <exception cref="ImageOperationException">This image has no alpha channel or is CMYK or Lab, or the mask is CMYK or Lab.</exception>
    public Image ApplyMask(Image mask, MaskChannel channel = MaskChannel.Alpha, int x = 0, int y = 0, bool linear = false)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (channel is not (MaskChannel.Alpha or MaskChannel.Luminance))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        if (Math.Abs((long)x) > MaxCompositeOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (Math.Abs((long)y) > MaxCompositeOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if (Format.Model is not (ColorModel.Rgb or ColorModel.Gray) || !Format.HasAlpha)
        {
            throw new ImageOperationException("ApplyMask needs an RGB or gray image with an alpha channel.");
        }

        if (mask.Format.Model is not (ColorModel.Rgb or ColorModel.Gray))
        {
            throw new ImageOperationException("The mask must be an RGB or gray image; convert CMYK and Lab first.");
        }

        Node maskNode = mask.ConvertTo(PixelFormat.Rgba8)._frames[0].Node;
        var nodes = new Node[_frames.Length];
        var delays = new TimeSpan[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new MaskNode(_frames[i].Node, maskNode, x, y, channel, linear && channel == MaskChannel.Luminance);
            delays[i] = _frames[i].Delay;
        }

        return new Image(nodes, delays, LoopCount, Metadata, SourceFormat);
    }
}
