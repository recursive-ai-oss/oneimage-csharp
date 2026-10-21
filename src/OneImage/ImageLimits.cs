namespace OneImage;

/// <summary>
/// Bounds on what the library will accept from untrusted data. Checked against header values before the
/// allocation they guard. Defaults are conservative; <see cref="Unrestricted"/> opts out of the configurable
/// bounds (a hard engine cap on dimensions still applies).
/// </summary>
public sealed class ImageLimits
{
    /// <summary>Hard engine cap on one dimension; applies even to <see cref="Unrestricted"/>.</summary>
    public const int EngineMaxDimension = 1 << 24;

    /// <summary>Hard engine cap on the number of 128x128 tiles in one frame; applies even to <see cref="Unrestricted"/>.</summary>
    public const long EngineMaxTiles = 1 << 22;

    /// <summary>Gets the default limits: 2^28 pixels per frame, 65,535 per dimension, 1,000 frames, 2^30 total pixels, 16 MB metadata, 1,100:1 expansion.</summary>
    public static ImageLimits Default { get; } = new();

    /// <summary>Gets limits that accept everything except what the engine cannot represent.</summary>
    public static ImageLimits Unrestricted { get; } = new()
    {
        MaxPixelsPerFrame = long.MaxValue,
        MaxDimension = int.MaxValue,
        MaxFrames = int.MaxValue,
        MaxTotalPixels = long.MaxValue,
        MaxMetadataBytes = long.MaxValue,
        MaxExpansionRatio = double.PositiveInfinity,
    };

    /// <summary>Gets the maximum width times height of one frame.</summary>
    public long MaxPixelsPerFrame { get; init; } = 1L << 28;

    /// <summary>Gets the maximum width or height of a frame.</summary>
    public int MaxDimension { get; init; } = 65535;

    /// <summary>Gets the maximum number of frames in one image.</summary>
    public int MaxFrames { get; init; } = 1000;

    /// <summary>Gets the maximum sum of pixels over all frames.</summary>
    public long MaxTotalPixels { get; init; } = 1L << 30;

    /// <summary>Gets the maximum bytes of metadata blobs (ICC, EXIF, XMP, IPTC) in one image.</summary>
    public long MaxMetadataBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>
    /// Gets the maximum ratio of decompressed to compressed bytes that deflate and LZW decoders may produce.
    /// Decoders enforce it as they inflate; the default is just above deflate's theoretical maximum of 1032:1.
    /// </summary>
    public double MaxExpansionRatio { get; init; } = 1100;

    /// <summary>Checks the size of one frame against the limits. Call it as soon as a header is parsed, before allocating.</summary>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <exception cref="ImageFormatException">A dimension is not positive.</exception>
    /// <exception cref="ImageLimitException">A bound is exceeded.</exception>
    public void CheckFrame(long width, long height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ImageFormatException($"Invalid image size {width}x{height}.");
        }

        if (width > MaxDimension || height > MaxDimension)
        {
            throw new ImageLimitException($"Image size {width}x{height} exceeds the dimension limit of {MaxDimension}.");
        }

        if (width * height > MaxPixelsPerFrame)
        {
            throw new ImageLimitException($"Image size {width}x{height} exceeds the limit of {MaxPixelsPerFrame} pixels per frame.");
        }

        CheckEngine(width, height);
    }

    /// <summary>Checks a decoded header (frame count, every frame, total pixels, metadata size).</summary>
    /// <param name="info">The parsed header information.</param>
    /// <exception cref="ImageFormatException">The header is inconsistent.</exception>
    /// <exception cref="ImageLimitException">A bound is exceeded.</exception>
    public void Check(ImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Frames.Count == 0)
        {
            throw new ImageFormatException("Image has no frames.");
        }

        if (info.Frames.Count > MaxFrames)
        {
            throw new ImageLimitException($"Frame count {info.Frames.Count} exceeds the limit of {MaxFrames}.");
        }

        long total = 0;
        foreach (FrameInfo f in info.Frames)
        {
            CheckFrame(f.Width, f.Height);
            total += (long)f.Width * f.Height;
            if (total > MaxTotalPixels)
            {
                throw new ImageLimitException($"Total pixel count exceeds the limit of {MaxTotalPixels}.");
            }
        }

        if (info.Metadata.TotalBytes > MaxMetadataBytes)
        {
            throw new ImageLimitException($"Metadata of {info.Metadata.TotalBytes} bytes exceeds the limit of {MaxMetadataBytes}.");
        }
    }

    internal static void CheckEngine(long width, long height)
    {
        if (width > EngineMaxDimension || height > EngineMaxDimension
            || ((width + 127) >> 7) * ((height + 127) >> 7) > EngineMaxTiles)
        {
            throw new ImageLimitException($"Image size {width}x{height} exceeds what the engine can represent.");
        }
    }
}
