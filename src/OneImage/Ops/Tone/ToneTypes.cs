namespace OneImage;

/// <summary>Selects which color channel an operation touches.</summary>
public enum ColorChannel
{
    /// <summary>Every color channel (the only valid choice for gray images). Alpha is never included.</summary>
    All = 0,

    /// <summary>The red channel of an RGB image.</summary>
    Red = 1,

    /// <summary>The green channel of an RGB image.</summary>
    Green = 2,

    /// <summary>The blue channel of an RGB image.</summary>
    Blue = 3,
}

/// <summary>A control point of a tone curve. Both coordinates are normalized to 0..1 (input on X, output on Y).</summary>
/// <param name="X">Input level, 0..1.</param>
/// <param name="Y">Output level, 0..1.</param>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// The working row of a fused tone program: up to four float planes (colors 0..2, alpha in plane 3) holding straight
/// (non-premultiplied) values, one image row wide.
/// </summary>
internal ref struct ToneRow
{
    public Span<float> Buf;
    public int Width;
    public int Colors;
    public bool HasAlpha;

    /// <summary>Sample type of the format this row currently represents; steps that depend on the grid of stored values read it.</summary>
    public SampleType Sample;

    public readonly Span<float> Plane(int plane) => Buf.Slice(plane * Tile.Size, Width);
}
