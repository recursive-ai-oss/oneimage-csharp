namespace OneImage.Svg;

/// <summary>
/// The SVG format registration: recognizes SVG and gzip-compressed SVG (<c>.svgz</c>) and decodes it. Register it with
/// <see cref="ImageConfiguration.Add"/>; <see cref="ImageConfiguration.Default"/> does not include it, so the package trims away when unused.
/// </summary>
/// <remarks>
/// Reading the size (<see cref="Image.Identify(string, LoadOptions?)"/>) parses only the root element's start tag. The rest of the
/// document is read, and malformed content reported, when pixels are first pulled. Rendering is not available yet:
/// pulling pixels fails with <see cref="ImageOperationException"/>.
/// </remarks>
public sealed class SvgFormat : IImageFormat
{
    /// <summary>Initializes the format with custom limits.</summary>
    /// <param name="limits">The document limits, or <see langword="null"/> for <see cref="SvgLimits.Default"/>.</param>
    public SvgFormat(SvgLimits? limits = null)
    {
        Limits = limits ?? SvgLimits.Default;
        Decoder = new SvgDecoder(this);
    }

    /// <summary>Gets a shared instance with the default limits.</summary>
    public static SvgFormat Instance { get; } = new();

    /// <summary>Gets the document limits of this registration.</summary>
    public SvgLimits Limits { get; }

    /// <inheritdoc />
    public string Name => "svg";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".svg", ".svgz"];

    /// <inheritdoc />
    public string MimeType => "image/svg+xml";

    /// <inheritdoc />
    public int HeaderLength => 4096;

    /// <inheritdoc />
    public IImageDecoder? Decoder { get; }

    /// <inheritdoc />
    public IImageEncoder? Encoder => null;

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> header) => SvgSniffer.Matches(header);
}
