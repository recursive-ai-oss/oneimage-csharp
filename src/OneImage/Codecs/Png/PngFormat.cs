namespace OneImage;

/// <summary>The PNG format registration: signature detection plus the decoder and encoder.</summary>
public sealed class PngFormat : IImageFormat
{
    private PngFormat()
    {
    }

    /// <summary>Gets the shared instance. Register it with <see cref="ImageConfiguration.Add"/>.</summary>
    public static PngFormat Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "png";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".png", ".apng"];

    /// <inheritdoc />
    public string MimeType => "image/png";

    /// <inheritdoc />
    public int HeaderLength => 8;

    /// <inheritdoc />
    public IImageDecoder? Decoder => PngDecoder.Instance;

    /// <inheritdoc />
    public IImageEncoder? Encoder => PngEncoder.Instance;

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> header) => header.Length >= 8 && header[..8].SequenceEqual(PngChunk.Signature);
}

/// <summary>How a PNG row is filtered before compression.</summary>
public enum PngFilterMode
{
    /// <summary>Tries all five filters on every row and keeps the one with the smallest sum of absolute byte values
    /// (the minimum-sum heuristic libpng uses). Images with less than 8 bits per pixel use no filter.</summary>
    Adaptive = 0,

    /// <summary>No filtering.</summary>
    None = 1,

    /// <summary>Every row uses the Sub filter.</summary>
    Sub = 2,

    /// <summary>Every row uses the Up filter.</summary>
    Up = 3,

    /// <summary>Every row uses the Average filter.</summary>
    Average = 4,

    /// <summary>Every row uses the Paeth filter.</summary>
    Paeth = 5,
}

/// <summary>Whether the PNG encoder may write a palette.</summary>
public enum PngColorMode
{
    /// <summary>RGB and RGBA images with at most 256 distinct colors (alpha included) are written with an exact palette
    /// at the smallest bit depth; everything else is written as supplied. A fully opaque RGBA image written this way reads
    /// back as RGB.</summary>
    Auto = 0,

    /// <summary>Always write gray or truecolor, never a palette.</summary>
    Truecolor = 1,

    /// <summary>Require a palette: more than 256 colors needs a quantizer, which is not wired in yet, and fails with
    /// <see cref="ImageOperationException"/>.</summary>
    Indexed = 2,
}

/// <summary>PNG encoder settings.</summary>
public sealed class PngEncoderOptions : ImageEncoderOptions
{
    private readonly int _level = 6;

    /// <summary>Gets the deflate level, 0 (stored) to 9 (smallest). The default is 6.</summary>
    public int CompressionLevel
    {
        get => _level;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 9);
            _level = value;
        }
    }

    /// <summary>Gets the row filter strategy. The default is <see cref="PngFilterMode.Adaptive"/>.</summary>
    public PngFilterMode Filter { get; init; } = PngFilterMode.Adaptive;

    /// <summary>Gets the palette policy. The default is <see cref="PngColorMode.Auto"/>.</summary>
    public PngColorMode ColorMode { get; init; } = PngColorMode.Auto;
}
