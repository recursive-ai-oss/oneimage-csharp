using OneImage.Codecs.Jpeg;

namespace OneImage;

/// <summary>
/// The JPEG format (ITU-T T.81 / JFIF).
/// <para>
/// Decoding: baseline, extended sequential and progressive DCT, Huffman or arithmetic entropy coding, 8- and 12-bit
/// samples, restart intervals, any sampling layout whose factors divide the largest one, gray, YCbCr, RGB and
/// (Adobe) CMYK/YCCK. Lossless and hierarchical processes are rejected with <see cref="ImageFormatException"/>.
/// Output is <see cref="PixelFormat.Gray8"/>, <see cref="PixelFormat.Rgb8"/> or <see cref="PixelFormat.Cmyk8"/>; 12-bit data is
/// widened to the 16-bit formats. Chroma is upsampled with the triangle ("fancy") filter. Adobe CMYK, which stores
/// ink inverted, is delivered with 0 meaning no ink.
/// </para>
/// <para>
/// The decoder supports <see cref="DecoderCapabilities.Scale"/> (reduced inverse DCT at 1/2, 1/4 and 1/8 without decoding
/// full-resolution pixels) and <see cref="DecoderCapabilities.Region"/> (rows and blocks outside the region are not transformed,
/// and a sequential file stops being read after the last needed row).
/// </para>
/// </summary>
public sealed class JpegFormat : IImageFormat, IImageDecoder, IImageEncoder
{
    private static readonly PixelFormat[] s_encodable = [PixelFormat.Rgb8, PixelFormat.Gray8, PixelFormat.Cmyk8];

    private JpegFormat()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static JpegFormat Instance { get; } = new();

    /// <inheritdoc/>
    public string Name => "jpeg";

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions { get; } = [".jpg", ".jpeg", ".jpe", ".jfif"];

    /// <inheritdoc/>
    public string MimeType => "image/jpeg";

    /// <inheritdoc/>
    public int HeaderLength => 3;

    /// <inheritdoc/>
    public IImageDecoder? Decoder => this;

    /// <inheritdoc/>
    public IImageEncoder? Encoder => this;

    /// <inheritdoc/>
    public DecoderCapabilities Capabilities => DecoderCapabilities.Region | DecoderCapabilities.Scale;

    /// <inheritdoc/>
    public IReadOnlyList<PixelFormat> SupportedFormats => s_encodable;

    /// <inheritdoc/>
    public bool SupportsMultipleFrames => false;

    /// <inheritdoc/>
    public bool Matches(ReadOnlySpan<byte> header) => header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

    /// <inheritdoc/>
    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        source.Position = 0;
        using var reader = new JpegReader(source);
        JpegHeaders h = JpegParser.ReadHeaders(reader, limits, new JpegTables(), collectMetadata: true);
        JpegFrame f = h.Frame!;
        var plan = JpegColorPlan.Decide(f, h);
        var meta = new ImageMetadata { IccProfile = h.Icc, Exif = h.Exif, Xmp = h.Xmp, Iptc = h.Iptc };
        return new ImageInfo(this, [new FrameInfo(f.Width, f.Height, plan.Format(f.Precision))], 0, meta);
    }

    /// <inheritdoc/>
    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNotEqual(frameIndex, 0);
        source.Position = 0;
        JpegDecoderCore.Decode(source, request, sink, limits, cancellationToken);
    }

    /// <inheritdoc/>
    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        JpegEncoderCore.Encode(request, destination, cancellationToken);
    }
}
