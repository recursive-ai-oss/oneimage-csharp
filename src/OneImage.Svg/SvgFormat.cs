namespace OneImage.Svg;

/// <summary>
/// The SVG format registration: recognizes SVG and gzip-compressed SVG (<c>.svgz</c>) and decodes it. Register it with
/// <see cref="ImageConfiguration.Add"/>; <see cref="ImageConfiguration.Default"/> does not include it, so the package trims away when unused.
/// </summary>
/// <remarks>
/// Reading the size (<see cref="Image.Identify(string, LoadOptions?)"/>) parses only the root element's start tag, unless the root gives no
/// usable size, in which case the whole document is read to measure its content. The rest of the document is read, and malformed content
/// reported, when pixels are first pulled. Output is 8-bit sRGB straight RGBA, transparent where nothing is drawn.
/// <para>
/// When a resize sits directly on the loaded image, the document is drawn at the final size (see <see cref="DecoderCapabilities.RenderAtSize"/>)
/// rather than drawn at its natural size and scaled; <see cref="Image.Thumbnail(Image, int?, int?, Fit, Anchor, bool, ResamplingFilter, bool)"/>
/// never enlarges unless <c>upscale</c> is set. Only the shapes under a requested region are drawn for it.
/// </para>
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
