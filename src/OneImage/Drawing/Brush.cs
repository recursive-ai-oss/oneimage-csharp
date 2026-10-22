namespace OneImage.Drawing;

/// <summary>
/// Says what color a fill puts down at each pixel. A fill asks its brush for one row of colors at a time, after the
/// rasterizer has decided how much of each pixel the shape covers; the brush never sees geometry. Create brushes with
/// <see cref="Solid"/>; further kinds (gradients, patterns, images) plug in here.
/// </summary>
public abstract class Brush
{
    internal Brush()
    {
    }

    /// <summary>A brush that paints one color everywhere. The color's numbers are used as they are, in the image's own color space and encoding.</summary>
    /// <param name="color">The color, with straight (non-premultiplied) alpha.</param>
    /// <returns>The brush.</returns>
    public static Brush Solid(Rgba32 color) => new SolidBrush(color);

    /// <summary>Gets the color if this brush paints one color everywhere, which lets fills skip the per-row work.</summary>
    internal abstract bool TryGetSolid(out Rgba32 color);

    /// <summary>
    /// Writes the brush color for pixels (<paramref name="x"/>.., <paramref name="y"/>) as straight RGBA floats in 0..1, four per
    /// pixel, in the image's own color space. <c>rgba.Length / 4</c> pixels are wanted.
    /// </summary>
    internal abstract void FillRow(int x, int y, Span<float> rgba);

    /// <summary>The brush's parameters for the op descriptor.</summary>
    internal abstract OpParameter[] Describe();
}

/// <summary>A brush with one color.</summary>
internal sealed class SolidBrush(Rgba32 color) : Brush
{
    internal override bool TryGetSolid(out Rgba32 c)
    {
        c = color;
        return true;
    }

    internal override void FillRow(int x, int y, Span<float> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i] = color.R / 255f;
            rgba[i + 1] = color.G / 255f;
            rgba[i + 2] = color.B / 255f;
            rgba[i + 3] = color.A / 255f;
        }
    }

    internal override OpParameter[] Describe() =>
        [new("brush", "solid"), new("r", (long)color.R), new("g", (long)color.G), new("b", (long)color.B), new("a", (long)color.A)];
}
