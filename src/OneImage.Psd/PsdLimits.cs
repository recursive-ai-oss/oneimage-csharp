namespace OneImage.Psd;

/// <summary>
/// Limits for reading a PSD or PSB file, on top of <see cref="ImageLimits"/> (pixels per frame, dimensions, metadata size). Every value is
/// checked against what the file's headers declare before memory is allocated for it; a file that exceeds one fails with
/// <see cref="ImageLimitException"/>.
/// </summary>
public sealed class PsdLimits
{
    /// <summary>Gets the limits used when none are given.</summary>
    public static PsdLimits Default { get; } = new();

    /// <summary>Gets the most layer records (groups and their dividers included) a file may hold. The default is 20,000.</summary>
    public int MaxLayers { get; init; } = 20_000;

    /// <summary>Gets the deepest group nesting. The default is 64, the depth a layered document allows.</summary>
    public int MaxNesting { get; init; } = 64;

    /// <summary>Gets the most bytes one opaque block (text, smart object, layer style, resource) may hold. The default is 64 MiB.</summary>
    public long MaxBlobBytes { get; init; } = 64L << 20;

    /// <summary>Gets the most bytes of opaque blocks the whole file may keep. The default is 256 MiB.</summary>
    public long MaxTotalBlobBytes { get; init; } = 256L << 20;

    /// <summary>
    /// Gets the most bytes the channels of all layers may occupy once decoded (width × height × bytes per sample × channels, summed over
    /// the layer records). The default is 16 GiB; the layers are not decoded all at once, so this bounds the work and not the memory.
    /// </summary>
    public long MaxDecodedChannelBytes { get; init; } = 16L << 30;

    /// <summary>Gets the most bytes one channel may take decoded in memory at once (ZIP-compressed channels are inflated whole). The default is 1 GiB.</summary>
    public long MaxChannelBytes { get; init; } = 1L << 30;

    /// <summary>Gets the most levels of nested descriptors and lists inside a descriptor. The default is 32.</summary>
    public int MaxDescriptorDepth { get; init; } = 32;

    /// <summary>Gets the most items a descriptor, list or curve may hold. The default is 65,536.</summary>
    public int MaxDescriptorItems { get; init; } = 65_536;

    /// <summary>Gets the most enabled effects one layer may draw; the ones past it are not drawn. The default is 32.</summary>
    public int MaxEffectsPerLayer { get; init; } = 32;
}
