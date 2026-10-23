using OneImage.Layers;

namespace OneImage.Psd;

/// <summary>Options for <see cref="PsdFile.Load(string, PsdReadOptions?, CancellationToken)"/>.</summary>
public sealed class PsdReadOptions
{
    /// <summary>Gets the image limits (pixels per frame, dimensions, metadata size, expansion ratio); <see langword="null"/> for <see cref="ImageConfiguration.Default"/>'s.</summary>
    public ImageLimits? Limits { get; init; }

    /// <summary>Gets the PSD-specific limits; <see langword="null"/> for <see cref="PsdLimits.Default"/>.</summary>
    public PsdLimits? PsdLimits { get; init; }

    /// <summary>Gets the disk-spill options of the loaded document (see <see cref="DocumentOptions.Spill"/>); <see langword="null"/> keeps everything in memory.</summary>
    public SpillOptions? Spill { get; init; }

    /// <summary>
    /// Gets whether gradient and pattern fill layers are drawn from their settings (the default) or from the raster Photoshop stored with them.
    /// Drawn fills are <see cref="FillLayer"/>s with a brush, so they follow the document size, masks and edits; a fill whose settings cannot be
    /// reproduced (noise gradients, perceptual interpolation, patterns the file does not carry) always uses the stored raster. The stored raster
    /// has Photoshop's own edge antialiasing and dither, so choose <see langword="false"/> when matching Photoshop pixel for pixel matters more than editing.
    /// </summary>
    public bool DrawFills { get; init; } = true;
}

/// <summary>How channel data is compressed when a file is written.</summary>
public enum PsdCompression
{
    /// <summary>Run-length coding (PackBits), as Photoshop writes by default.</summary>
    Rle = 0,

    /// <summary>No compression: fastest to write, largest files.</summary>
    Raw = 1,
}

/// <summary>Options for <see cref="PsdFile.Save(Stream, PsdWriteOptions?, CancellationToken)"/>.</summary>
public sealed class PsdWriteOptions
{
    /// <summary>
    /// Gets whether to write a PSB (large document) file. The default, <see langword="null"/>, writes a PSB only when the document is
    /// wider or taller than the 30,000 pixels a PSD allows. Asking for a PSD (<see langword="false"/>) fails with an
    /// <see cref="ImageOperationException"/> when the size needs a PSB, and a PSD section that would pass 4 GiB fails with an
    /// <see cref="ImageLimitException"/>; ask for a PSB for such documents.
    /// </summary>
    public bool? Large { get; init; }

    /// <summary>Gets how channels are compressed. The default is <see cref="PsdCompression.Rle"/>.</summary>
    public PsdCompression Compression { get; init; } = PsdCompression.Rle;
}
