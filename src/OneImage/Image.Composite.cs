namespace OneImage;

public sealed partial class Image
{
    private const int MaxCompositeOffset = 1 << 28;

    /// <summary>
    /// Lays <paramref name="overlay"/> over this image with its top-left corner at (<paramref name="x"/>, <paramref name="y"/>)
    /// (lazy). This is Normal (source-over) compositing: the overlay's alpha, scaled by <paramref name="opacity"/>, decides how
    /// much of the base shows through, computed on premultiplied values.
    /// <para>
    /// <b>Geometry.</b> The result has the base's size, format and metadata. The overlay may be any size and may lie partly or
    /// fully outside the base; the parts outside are ignored. Only the tiles the overlay touches are computed; every other tile
    /// is the base's own tile, and only the overlay pixels that land on a tile are read.
    /// </para>
    /// <para>
    /// <b>Color.</b> The overlay is converted to the base's color profile (relative colorimetric with black point compensation)
    /// when the two differ, so a Display P3 overlay on an sRGB base keeps its colors. The base must be RGB or gray, with or without
    /// alpha; a base without alpha stays opaque (the overlay is flattened onto it), and a base with alpha keeps its alpha mode
    /// and gets the union coverage <c>a + b(1 - a)</c>. Gray overlays go onto RGB bases as neutral colors.
    /// </para>
    /// <para>
    /// <b>Blend space.</b> By default the values are blended as stored (encoded), which is what image editors do. With
    /// <paramref name="linear"/> the base and overlay go to linear light first, are blended there with 32-bit float intermediates,
    /// and come back, which matches physical light mixing but looks different at soft edges.
    /// </para>
    /// <para>
    /// <b>Precision and rounding.</b> 8-bit RGB over 8-bit RGBA uses integer arithmetic: the overlay alpha times opacity is
    /// rounded to 8 bits, the mix is rounded half up; the result is within 1 of the exact value. Every other combination is computed in
    /// 32-bit float and rounded half up on output. Results are identical on every hardware path and for any thread count.
    /// </para>
    /// <para>
    /// <b>Animation.</b> Every frame of this image gets the overlay's first frame.
    /// </para>
    /// </summary>
    /// <param name="overlay">The image to lay on top.</param>
    /// <param name="x">Horizontal position of the overlay's left edge in the base; may be negative.</param>
    /// <param name="y">Vertical position of the overlay's top edge in the base; may be negative.</param>
    /// <param name="opacity">Extra opacity multiplier, 0 to 1. 0 returns the base unchanged. The default is 1.</param>
    /// <param name="linear">Blend in linear light instead of the encoded space. The default is <see langword="false"/>.</param>
    /// <returns>The composited image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="overlay"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is outside 0..1 (or NaN), or an offset is beyond ±2²⁸.</exception>
    /// <exception cref="ImageOperationException">The base or overlay is CMYK or Lab, or a profile conversion is not possible.</exception>
    public Image Composite(Image overlay, int x = 0, int y = 0, float opacity = 1f, bool linear = false)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (!(opacity >= 0f && opacity <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), "Opacity must be between 0 and 1.");
        }

        if (Math.Abs((long)x) > MaxCompositeOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if (Math.Abs((long)y) > MaxCompositeOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if (Format.Model is not (ColorModel.Rgb or ColorModel.Gray) || overlay.Format.Model is ColorModel.Lab)
        {
            throw new ImageOperationException("Composite works on RGB and gray images; convert CMYK and Lab first.");
        }

        ColorProfile baseProfile = ColorProfile;
        Image ov = SameProfile(overlay.ColorProfile, baseProfile) ? overlay : overlay.ConvertColor(baseProfile);
        if (ov.Format.Model != Format.Model)
        {
            throw new ImageOperationException("The overlay could not be brought into the base's color model.");
        }

        Image b = this;
        if (linear)
        {
            b = ToLinearLight(out _);
            ov = ov.ToLinearLight(out _);
        }

        Node overlayNode = ov._frames[0].Node;
        var nodes = new Node[b._frames.Length];
        var delays = new TimeSpan[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new CompositeNode(b._frames[i].Node, overlayNode, x, y, opacity);
            delays[i] = b._frames[i].Delay;
        }

        // The linear intermediate carries the linear profile, so the way back knows what the numbers mean.
        var result = new Image(nodes, delays, LoopCount, b.Metadata, SourceFormat);
        return linear ? result.FromLinearLight(baseProfile, Format).WithMetadata(Metadata) : result;
    }

    private static bool SameProfile(ColorProfile a, ColorProfile b) =>
        ReferenceEquals(a, b) || (a.Model == b.Model && a.IccBytes.Span.SequenceEqual(b.IccBytes.Span));
}
