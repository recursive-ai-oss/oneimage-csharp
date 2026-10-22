namespace OneImage;

public sealed partial class Image
{
    /// <summary>
    /// Stretches the tonal range with the equalization curve of the image's luminance histogram (lazy).
    /// </summary>
    /// <remarks>
    /// <para>Input is converted to 8-bit RGB(A). The luminance histogram (256 levels) of the whole
    /// frame is counted once, exactly, on the first tile request, and turned into the usual equalization curve. Not synchronized, the curve is
    /// applied to every R, G and B value as it stands, so a channel that is dark compared with the luminance is lifted with it. Synchronized,
    /// each pixel is multiplied by <c>curve(Y) / Y</c> instead (clamped at 255), which moves its luminance onto the curve and keeps the ratios
    /// between its channels. Alpha is untouched. A black pixel stays black.</para>
    /// <para>Graph descriptor: <c>autoLevel(syncChannels)</c>.</para>
    /// </remarks>
    /// <param name="syncChannels">Scale the three channels together by the luminance gain (default <see langword="true"/>); otherwise apply the curve to each channel value.</param>
    /// <returns>The adjusted image.</returns>
    /// <exception cref="ImageOperationException">The pixel format is CMYK or Lab.</exception>
    public Image AutoLevel(bool syncChannels = true)
    {
        CheckQuantizable("AutoLevel");
        return Eight().Map(static (n, s) => new AutoLevelNode(n, s), syncChannels);
    }

    /// <summary>
    /// Turns the image into two colors with Bradley adaptive thresholding (lazy): a pixel becomes <paramref name="lower"/> when its
    /// brightness is at or below <paramref name="thresholdLimit"/> times the mean brightness of the window around it, otherwise
    /// <paramref name="upper"/>.
    /// </summary>
    /// <remarks>
    /// <para>Brightness is the Rec. 709 luminance (0.2126 R + 0.7152 G + 0.0722 B) of the 8-bit samples, rounded to a whole number. The window is a square that reaches <c>s − 2</c> pixels before and <c>s + 1</c>
    /// pixels after the pixel in each direction, with <c>s = max(1, width / 16)</c>, clipped at the image edge (the mean is over the
    /// pixels that exist). The window sums come from an integral image built per tile over the tile plus its margin, so cost grows with
    /// the square of the window for very wide images. Pixels are 8-bit RGBA; alpha takes part only through the result colors.</para>
    /// <para>The result is <see cref="PixelFormat.Rgba8"/>. Graph descriptor: <c>adaptiveThreshold(upper, lower, limit)</c>.</para>
    /// </remarks>
    /// <param name="upper">The color for bright pixels (default opaque white).</param>
    /// <param name="lower">The color for dark pixels (default opaque black).</param>
    /// <param name="thresholdLimit">The fraction of the local mean a pixel must exceed to be bright, 0 to 1 (default 0.85).</param>
    /// <returns>The thresholded image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thresholdLimit"/> is outside 0 to 1.</exception>
    /// <exception cref="ImageOperationException">The pixel format is CMYK or Lab.</exception>
    public Image AdaptiveThreshold(Rgba32? upper = null, Rgba32? lower = null, float thresholdLimit = 0.85f)
    {
        if (!(thresholdLimit >= 0f && thresholdLimit <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdLimit));
        }

        CheckQuantizable("AdaptiveThreshold");
        uint u = Pack(upper ?? new Rgba32(255, 255, 255, 255));
        uint l = Pack(lower ?? new Rgba32(0, 0, 0, 255));
        return Map(static (n, a) => new AdaptiveThresholdNode(n, a.u, a.l, a.limit), (u, l, limit: thresholdLimit));
    }

    private static uint Pack(Rgba32 c) => c.R | ((uint)c.G << 8) | ((uint)c.B << 16) | ((uint)c.A << 24);

    // The histogram nodes work on 8-bit RGB(A): other depths and gray are converted first.
    private Image Eight()
    {
        bool alpha = _frames[0].Format.HasAlpha;
        return ConvertTo(alpha ? PixelFormat.Rgba8 : PixelFormat.Rgb8);
    }
}
