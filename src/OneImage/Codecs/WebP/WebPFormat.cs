using OneImage.Codecs.WebP;

namespace OneImage;

/// <summary>
/// The WebP format: lossy (VP8), lossless (VP8L), alpha and animation. Register it with
/// <see cref="ImageConfiguration.Add"/> or use <see cref="ImageConfiguration.Default"/>, which includes it.
/// </summary>
/// <remarks>
/// <para>Decoding delivers 8-bit RGB (no alpha) or straight-alpha RGBA. Lossy images use libwebp's default "fancy" chroma
/// upsampling and its integer YUV-to-RGB conversion, so results agree with libwebp to within one level per channel.
/// Lossless images decode bit-exactly.</para>
/// <para>Animations are exposed as full-canvas frames: disposal and blending are applied while decoding, with the canvas
/// starting transparent (the background color chunk is a hint and is not used). Blending is source-over on non-premultiplied
/// 8-bit sRGB values.</para>
/// <para>Metadata chunks (ICC profile, EXIF, XMP) are carried as raw blobs.</para>
/// </remarks>
public sealed class WebPFormat : IImageFormat
{
    private readonly WebPDecoderImpl _decoder;
    private readonly WebPEncoderImpl _encoder;

    /// <summary>Initializes the format.</summary>
    public WebPFormat()
    {
        _decoder = new WebPDecoderImpl(this);
        _encoder = new WebPEncoderImpl();
    }

    /// <inheritdoc/>
    public string Name => "webp";

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions { get; } = [".webp"];

    /// <inheritdoc/>
    public string MimeType => "image/webp";

    /// <inheritdoc/>
    public int HeaderLength => 12;

    /// <inheritdoc/>
    public IImageDecoder? Decoder => _decoder;

    /// <inheritdoc/>
    public IImageEncoder? Encoder => _encoder;

    /// <inheritdoc/>
    public bool Matches(ReadOnlySpan<byte> header) => WebPContainer.Matches(header);
}
