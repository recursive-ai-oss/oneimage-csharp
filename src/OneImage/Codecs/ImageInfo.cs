namespace OneImage;

/// <summary>Metadata carried alongside pixels. Blobs are kept verbatim; typed reads arrive with the metadata model.</summary>
public sealed class ImageMetadata
{
    /// <summary>Gets an instance with no metadata.</summary>
    public static ImageMetadata Empty { get; } = new();

    /// <summary>Gets the embedded ICC profile, if any.</summary>
    public ReadOnlyMemory<byte> IccProfile { get; init; }

    /// <summary>Gets the raw EXIF blob, if any.</summary>
    public ReadOnlyMemory<byte> Exif { get; init; }

    /// <summary>Gets the raw XMP packet, if any.</summary>
    public ReadOnlyMemory<byte> Xmp { get; init; }

    /// <summary>Gets the raw IPTC blob, if any.</summary>
    public ReadOnlyMemory<byte> Iptc { get; init; }

    /// <summary>Gets the EXIF orientation (1..8). 1 means upright. The core does not apply it.</summary>
    public int Orientation { get; init; } = 1;

    /// <summary>Gets the total size of all blobs in bytes.</summary>
    public long TotalBytes => (long)IccProfile.Length + Exif.Length + Xmp.Length + Iptc.Length;
}

/// <summary>Header-level description of one frame.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Format">The pixel format the decoder delivers rows in.</param>
/// <param name="Delay">Display time for animations (zero for still images).</param>
public sealed record FrameInfo(int Width, int Height, PixelFormat Format, TimeSpan Delay = default);

/// <summary>What a decoder learned from the headers, without decoding pixels.</summary>
public sealed class ImageInfo
{
    /// <summary>Creates the description.</summary>
    /// <param name="format">The detected format.</param>
    /// <param name="frames">At least one frame description.</param>
    /// <param name="loopCount">Animation loop count; 0 means forever.</param>
    /// <param name="metadata">Metadata blobs, or <see langword="null"/> for none.</param>
    /// <param name="decoderState">Opaque value the decoder may stash to avoid re-parsing headers; passed back on each decode call.</param>
    public ImageInfo(IImageFormat format, IReadOnlyList<FrameInfo> frames, int loopCount = 0, ImageMetadata? metadata = null, object? decoderState = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(frames);
        Format = format;
        Frames = frames;
        LoopCount = loopCount;
        Metadata = metadata ?? ImageMetadata.Empty;
        DecoderState = decoderState;
    }

    /// <summary>Gets the format.</summary>
    public IImageFormat Format { get; }

    /// <summary>Gets the frame descriptions.</summary>
    public IReadOnlyList<FrameInfo> Frames { get; }

    /// <summary>Gets the animation loop count; 0 means forever.</summary>
    public int LoopCount { get; }

    /// <summary>Gets the metadata.</summary>
    public ImageMetadata Metadata { get; }

    /// <summary>Gets the width of the first frame.</summary>
    public int Width => Frames.Count > 0 ? Frames[0].Width : 0;

    /// <summary>Gets the height of the first frame.</summary>
    public int Height => Frames.Count > 0 ? Frames[0].Height : 0;

    /// <summary>Gets the decoder's private state.</summary>
    public object? DecoderState { get; }
}
