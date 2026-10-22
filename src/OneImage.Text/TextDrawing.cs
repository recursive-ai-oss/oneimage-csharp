using System.Numerics;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage.Text;

/// <summary>
/// Draws text on an <see cref="Image"/> or <see cref="Canvas"/> through the vector drawing layer: the text becomes one path that
/// is filled with a brush and/or stroked with a pen exactly like any other path (same anti-aliasing, blend modes and linear-light option).
/// </summary>
public static class TextDrawing
{
    /// <summary>
    /// Draws text on an image: filled with <paramref name="brush"/> when given, then outlined with <paramref name="pen"/> when given.
    /// The layout is computed now; the pixels are produced lazily like any other drawing step.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="text">The text; see <see cref="TextMeasurer.Measure"/> for how line breaks are handled.</param>
    /// <param name="options">Font, origin, wrapping and alignment.</param>
    /// <param name="brush">The fill, or null for none.</param>
    /// <param name="pen">The outline, or null for none. With both, the outline is drawn over the fill.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the paint alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <returns>A new image; the input is unchanged.</returns>
    /// <exception cref="ArgumentNullException">An argument other than the brush and pen is null.</exception>
    /// <exception cref="ArgumentException">Both <paramref name="brush"/> and <paramref name="pen"/> are null.</exception>
    /// <exception cref="ImageOperationException">The image is CMYK or Lab.</exception>
    /// <exception cref="ImageLimitException">The text or the resulting path is too large.</exception>
    /// <exception cref="FontFormatException">The font's glyph data is malformed.</exception>
    public static Image DrawText(this Image image, string text, TextOptions options, Brush? brush, Pen? pen = null, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        Path path = Prepare(text, options, brush, pen);
        Image result = image;
        if (brush is not null)
        {
            result = result.Fill(brush, path, antialias, IntersectionRule.NonZero, opacity, blend, composition, linear);
        }

        if (pen is not null)
        {
            result = result.Draw(pen, path, antialias, opacity, blend, composition, linear);
        }

        return result;
    }

    /// <summary>Draws text on an image filled with one color; see <see cref="DrawText(Image, string, TextOptions, Brush?, Pen?, bool, float, BlendMode, AlphaComposition, bool)"/>.</summary>
    /// <param name="image">The image.</param>
    /// <param name="text">The text.</param>
    /// <param name="options">Font, origin, wrapping and alignment.</param>
    /// <param name="color">The fill color, straight alpha, in the image's color space.</param>
    /// <returns>A new image.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static Image DrawText(this Image image, string text, TextOptions options, Rgba32 color) =>
        image.DrawText(text, options, Brush.Solid(color));

    /// <summary>
    /// Draws text on a canvas, in place: filled with <paramref name="brush"/> when given, then outlined with <paramref name="pen"/> when given.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="text">The text.</param>
    /// <param name="options">Font, origin, wrapping and alignment.</param>
    /// <param name="brush">The fill, or null for none.</param>
    /// <param name="pen">The outline, or null for none.</param>
    /// <param name="antialias">Smooth edges. The default is <see langword="true"/>.</param>
    /// <param name="opacity">Multiplier for the paint alpha, 0 to 1. The default is 1.</param>
    /// <param name="blend">Blend mode. The default is <see cref="BlendMode.Normal"/>.</param>
    /// <param name="composition">Porter-Duff operator. The default is <see cref="AlphaComposition.SrcOver"/>.</param>
    /// <param name="linear">Blend in linear light. The default is <see langword="false"/>.</param>
    /// <exception cref="ObjectDisposedException">The canvas was disposed.</exception>
    /// <exception cref="ArgumentNullException">An argument other than the brush and pen is null.</exception>
    /// <exception cref="ArgumentException">Both <paramref name="brush"/> and <paramref name="pen"/> are null.</exception>
    /// <exception cref="ImageOperationException">The canvas is CMYK or Lab.</exception>
    /// <exception cref="ImageLimitException">The text or the resulting path is too large.</exception>
    /// <exception cref="FontFormatException">The font's glyph data is malformed.</exception>
    public static void DrawText(this Canvas canvas, string text, TextOptions options, Brush? brush, Pen? pen = null, bool antialias = true, float opacity = 1f, BlendMode blend = BlendMode.Normal, AlphaComposition composition = AlphaComposition.SrcOver, bool linear = false)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Path path = Prepare(text, options, brush, pen);
        if (brush is not null)
        {
            canvas.Fill(brush, path, antialias, IntersectionRule.NonZero, opacity, blend, composition, linear);
        }

        if (pen is not null)
        {
            canvas.Draw(pen, path, antialias, opacity, blend, composition, linear);
        }
    }

    /// <summary>Draws text on a canvas filled with one color, in place.</summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="text">The text.</param>
    /// <param name="options">Font, origin, wrapping and alignment.</param>
    /// <param name="color">The fill color, straight alpha, in the canvas's color space.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void DrawText(this Canvas canvas, string text, TextOptions options, Rgba32 color) =>
        canvas.DrawText(text, options, Brush.Solid(color));

    private static Path Prepare(string text, TextOptions options, Brush? brush, Pen? pen)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        if (brush is null && pen is null)
        {
            throw new ArgumentException("Give a brush, a pen, or both.", nameof(brush));
        }

        return TextBuilder.GeneratePath(text, options);
    }
}
