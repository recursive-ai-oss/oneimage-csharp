namespace OneImage;

/// <summary>
/// The GIF format (GIF87a and GIF89a). Registered in the default configuration; custom configurations can add
/// <see cref="Instance"/> explicitly.
/// </summary>
/// <remarks>
/// <para><b>Reading.</b> Every frame is delivered as a full-screen, straight-alpha RGBA8 image with earlier frames composited under it
/// according to each frame's disposal method. The canvas starts transparent and "restore to background" clears to transparent
/// (the background color index is ignored, as browsers do). Frame rectangles are clipped to the logical screen; a logical screen of
/// zero width or height is replaced by the bounding box of the frames. Frame delays are the stored value in 10 ms units.
/// <see cref="Image.LoopCount"/> is the total number of plays: 0 for forever, 1 when the file has no NETSCAPE2.0 extension, otherwise the stored repeat count plus one. An XMP packet in an
/// application extension is exposed as <see cref="ImageMetadata.Xmp"/>. Interlaced frames are de-interlaced. A frame whose LZW data
/// ends early (no end code, or a cut-off file) keeps the pixels that were decoded and leaves the rest showing what was beneath;
/// invalid LZW codes throw <see cref="ImageFormatException"/>. Frame count and the total pixel count over all frames are checked
/// against <see cref="ImageLimits"/> before any pixel memory is allocated.</para>
/// <para><b>Writing.</b> Alpha is binary: pixels with alpha below 128 are transparent, the others opaque. A frame whose colors fit the
/// palette is written exactly; otherwise it is quantized (see <see cref="Image.Quantize"/>) and dithered per
/// <see cref="GifEncoderOptions"/>. Animations use one global palette when all frames together fit it, else a palette per frame, and
/// store only the changed rectangle of each frame (see <see cref="GifEncoderOptions.FrameDifferencing"/>). Delays are rounded to
/// 10 ms. A loop count other than 1 is written as a NETSCAPE2.0 extension (0 forever, otherwise count minus one). XMP metadata is written; other metadata is dropped.
/// Output is byte-identical for identical input.</para>
/// </remarks>
public sealed class GifFormat : IImageFormat
{
    private GifFormat()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static GifFormat Instance { get; } = new();

    /// <inheritdoc/>
    public string Name => "gif";

    /// <inheritdoc/>
    public IReadOnlyList<string> Extensions { get; } = [".gif"];

    /// <inheritdoc/>
    public string MimeType => "image/gif";

    /// <inheritdoc/>
    public int HeaderLength => 6;

    /// <inheritdoc/>
    public bool Matches(ReadOnlySpan<byte> header) =>
        header.Length >= 6 && (header[..6].SequenceEqual("GIF89a"u8) || header[..6].SequenceEqual("GIF87a"u8));

    /// <inheritdoc/>
    public IImageDecoder? Decoder => Codecs.Gif.GifDecoder.Instance;

    /// <inheritdoc/>
    public IImageEncoder? Encoder => Codecs.Gif.GifEncoder.Instance;
}
