namespace OneImage.Codecs.WebP;

/// <summary>Placeholder until the encoder lands.</summary>
internal sealed class WebPEncoderImpl : IImageEncoder
{
    public IReadOnlyList<PixelFormat> SupportedFormats { get; } = [PixelFormat.Rgba8, PixelFormat.Rgb8];

    public bool SupportsMultipleFrames => true;

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
