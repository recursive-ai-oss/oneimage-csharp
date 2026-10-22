namespace OneImage;

/// <summary>Settings for the GIF encoder. Pass an instance in <see cref="SaveOptions.EncoderOptions"/>.</summary>
public sealed class GifEncoderOptions : ImageEncoderOptions
{
    private readonly int _maxColors = 256;

    /// <summary>
    /// Gets the most palette entries per color table, 2 to 256 (default 256). A transparent entry counts. Images with fewer distinct
    /// colors than this are written exactly, with no approximation.
    /// </summary>
    public int MaxColors
    {
        get => _maxColors;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 256);
            _maxColors = value;
        }
    }

    /// <summary>
    /// Gets how frames with more colors than the palette holds are dithered (default <see cref="Dither.FloydSteinberg"/>).
    /// It has no effect on frames whose colors fit.
    /// </summary>
    public Dither Dither { get; init; } = Dither.FloydSteinberg;

    /// <summary>
    /// Gets where palettes come from (default <see cref="Quantizer.Wu"/>). <see cref="Quantizer.WebSafe"/> and <see cref="Quantizer.Werner"/> use their
    /// fixed colors (and a transparent entry when a frame needs one) and ignore <see cref="MaxColors"/>. Frames whose colors already fit
    /// <see cref="MaxColors"/> are written exactly, whatever the quantizer. Only the palette changes; <see cref="Dither"/> still applies.
    /// </summary>
    public Quantizer Quantizer { get; init; } = Quantizer.Wu;

    /// <summary>
    /// Gets whether animation frames after the first store only the rectangle that changed, with unchanged pixels inside it written as
    /// transparent (default <see langword="true"/>). Turning it off writes every frame completely; the decoded animation is the same.
    /// </summary>
    public bool FrameDifferencing { get; init; } = true;
}
