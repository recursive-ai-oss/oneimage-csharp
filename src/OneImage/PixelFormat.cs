namespace OneImage;

/// <summary>The color model of a pixel's color channels (alpha is separate).</summary>
public enum ColorModel : byte
{
    /// <summary>One luminance channel.</summary>
    Gray = 0,

    /// <summary>Red, green, blue.</summary>
    Rgb = 1,

    /// <summary>Cyan, magenta, yellow, black.</summary>
    Cmyk = 2,

    /// <summary>CIE L*a*b* (three channels; value ranges are defined by whoever produces the data).</summary>
    Lab = 3,
}

/// <summary>The storage type of one channel sample.</summary>
public enum SampleType : byte
{
    /// <summary>Unsigned 8-bit integer, 0..255 maps to 0..1.</summary>
    U8 = 0,

    /// <summary>Unsigned 16-bit integer, 0..65535 maps to 0..1.</summary>
    U16 = 1,

    /// <summary>IEEE half-precision float.</summary>
    F16 = 2,

    /// <summary>IEEE single-precision float.</summary>
    F32 = 3,
}

/// <summary>How an alpha channel relates to the color channels.</summary>
public enum AlphaMode : byte
{
    /// <summary>No alpha channel.</summary>
    None = 0,

    /// <summary>Alpha is the last channel; color is independent of alpha (the form codecs usually store).</summary>
    Straight = 1,

    /// <summary>Alpha is the last channel; color is already multiplied by alpha.</summary>
    Premultiplied = 2,
}

/// <summary>
/// Describes how pixels are stored: color model, sample type and alpha mode. Channels are interleaved,
/// color channels first and the optional alpha channel last. The default value is 8-bit gray.
/// </summary>
public readonly struct PixelFormat : IEquatable<PixelFormat>
{
    /// <summary>Creates a pixel format.</summary>
    /// <param name="model">The color model.</param>
    /// <param name="sample">The sample type of every channel.</param>
    /// <param name="alpha">The alpha mode.</param>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is not defined.</exception>
    public PixelFormat(ColorModel model, SampleType sample, AlphaMode alpha = AlphaMode.None)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)model, (int)ColorModel.Lab);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)sample, (int)SampleType.F32);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)alpha, (int)AlphaMode.Premultiplied);
        Model = model;
        Sample = sample;
        Alpha = alpha;
    }

    /// <summary>Gets 8-bit gray.</summary>
    public static PixelFormat Gray8 { get; } = new(ColorModel.Gray, SampleType.U8);

    /// <summary>Gets 8-bit gray with straight alpha.</summary>
    public static PixelFormat GrayAlpha8 { get; } = new(ColorModel.Gray, SampleType.U8, AlphaMode.Straight);

    /// <summary>Gets 16-bit gray.</summary>
    public static PixelFormat Gray16 { get; } = new(ColorModel.Gray, SampleType.U16);

    /// <summary>Gets 32-bit float gray.</summary>
    public static PixelFormat GrayF32 { get; } = new(ColorModel.Gray, SampleType.F32);

    /// <summary>Gets 8-bit RGB.</summary>
    public static PixelFormat Rgb8 { get; } = new(ColorModel.Rgb, SampleType.U8);

    /// <summary>Gets 8-bit RGB with straight alpha.</summary>
    public static PixelFormat Rgba8 { get; } = new(ColorModel.Rgb, SampleType.U8, AlphaMode.Straight);

    /// <summary>Gets 8-bit RGB with premultiplied alpha.</summary>
    public static PixelFormat Rgba8Premultiplied { get; } = new(ColorModel.Rgb, SampleType.U8, AlphaMode.Premultiplied);

    /// <summary>Gets 16-bit RGB.</summary>
    public static PixelFormat Rgb16 { get; } = new(ColorModel.Rgb, SampleType.U16);

    /// <summary>Gets 16-bit RGB with straight alpha.</summary>
    public static PixelFormat Rgba16 { get; } = new(ColorModel.Rgb, SampleType.U16, AlphaMode.Straight);

    /// <summary>Gets half-float RGB with straight alpha.</summary>
    public static PixelFormat RgbaF16 { get; } = new(ColorModel.Rgb, SampleType.F16, AlphaMode.Straight);

    /// <summary>Gets 32-bit float RGB.</summary>
    public static PixelFormat RgbF32 { get; } = new(ColorModel.Rgb, SampleType.F32);

    /// <summary>Gets 32-bit float RGB with straight alpha.</summary>
    public static PixelFormat RgbaF32 { get; } = new(ColorModel.Rgb, SampleType.F32, AlphaMode.Straight);

    /// <summary>Gets 32-bit float RGB with premultiplied alpha (the form ops compute in).</summary>
    public static PixelFormat RgbaF32Premultiplied { get; } = new(ColorModel.Rgb, SampleType.F32, AlphaMode.Premultiplied);

    /// <summary>Gets 8-bit CMYK.</summary>
    public static PixelFormat Cmyk8 { get; } = new(ColorModel.Cmyk, SampleType.U8);

    /// <summary>Gets 16-bit CMYK.</summary>
    public static PixelFormat Cmyk16 { get; } = new(ColorModel.Cmyk, SampleType.U16);

    /// <summary>Gets 32-bit float L*a*b*.</summary>
    public static PixelFormat LabF32 { get; } = new(ColorModel.Lab, SampleType.F32);

    /// <summary>Gets the color model.</summary>
    public ColorModel Model { get; }

    /// <summary>Gets the sample type of every channel.</summary>
    public SampleType Sample { get; }

    /// <summary>Gets the alpha mode.</summary>
    public AlphaMode Alpha { get; }

    /// <summary>Gets a value indicating whether the format has an alpha channel.</summary>
    public bool HasAlpha => Alpha != AlphaMode.None;

    /// <summary>Gets the number of color channels (excluding alpha).</summary>
    public int ColorChannels => Model switch
    {
        ColorModel.Gray => 1,
        ColorModel.Rgb => 3,
        ColorModel.Lab => 3,
        _ => 4,
    };

    /// <summary>Gets the number of channels including alpha.</summary>
    public int ChannelCount => ColorChannels + (HasAlpha ? 1 : 0);

    /// <summary>Gets the size of one sample in bytes.</summary>
    public int BytesPerSample => Sample switch
    {
        SampleType.U8 => 1,
        SampleType.U16 => 2,
        SampleType.F16 => 2,
        _ => 4,
    };

    /// <summary>Gets the size of one pixel in bytes.</summary>
    public int BytesPerPixel => ChannelCount * BytesPerSample;

    /// <summary>Compares two formats.</summary>
    /// <param name="left">The first format.</param>
    /// <param name="right">The second format.</param>
    /// <returns><see langword="true"/> when all components match.</returns>
    public static bool operator ==(PixelFormat left, PixelFormat right) => left.Equals(right);

    /// <summary>Compares two formats.</summary>
    /// <param name="left">The first format.</param>
    /// <param name="right">The second format.</param>
    /// <returns><see langword="true"/> when any component differs.</returns>
    public static bool operator !=(PixelFormat left, PixelFormat right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(PixelFormat other) => Model == other.Model && Sample == other.Sample && Alpha == other.Alpha;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PixelFormat other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ((int)Model << 16) | ((int)Sample << 8) | (int)Alpha;

    /// <inheritdoc/>
    public override string ToString() => $"{Model}/{Sample}/{Alpha}";
}

/// <summary>A pixel struct whose memory layout equals a <see cref="PixelFormat"/>, usable for typed pixel access.</summary>
/// <typeparam name="TSelf">The implementing struct.</typeparam>
public interface IPixel<TSelf>
    where TSelf : unmanaged, IPixel<TSelf>
{
    /// <summary>Gets the storage format this struct is laid out as.</summary>
    static abstract PixelFormat Format { get; }
}
