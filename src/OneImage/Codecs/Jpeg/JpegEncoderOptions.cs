namespace OneImage;

/// <summary>Chroma subsampling layouts for JPEG encoding. The name gives the sampling of the two chroma channels relative to luma.</summary>
public enum JpegSubsampling
{
    /// <summary>4:2:0 - chroma halved horizontally and vertically (the default; smallest files).</summary>
    Yuv420 = 0,

    /// <summary>4:4:4 - no subsampling.</summary>
    Yuv444 = 1,

    /// <summary>4:2:2 - chroma halved horizontally.</summary>
    Yuv422 = 2,

    /// <summary>4:4:0 - chroma halved vertically.</summary>
    Yuv440 = 3,
}

/// <summary>
/// Settings for writing JPEG files. Pass an instance through the save options; <see langword="null"/> means the defaults
/// documented here. Gray images are written as one component and CMYK as four (Adobe-style, ink stored inverted) regardless of
/// <see cref="Subsampling"/>.
/// </summary>
public sealed class JpegEncoderOptions : ImageEncoderOptions
{
    /// <summary>Gets or sets the quality, 1 (smallest) to 100 (best). The scale is the one libjpeg uses, so equal numbers give comparable quality. Default 75.</summary>
    public int Quality { get; set; } = 75;

    /// <summary>Gets or sets the chroma subsampling of color images. Default <see cref="JpegSubsampling.Yuv420"/>.</summary>
    public JpegSubsampling Subsampling { get; set; } = JpegSubsampling.Yuv420;

    /// <summary>
    /// Gets or sets whether to write a progressive file (several scans of increasing detail; usually smaller than the
    /// sequential file, always uses optimized Huffman tables, and needs the whole coefficient set in memory). Default <see langword="false"/>.
    /// </summary>
    public bool Progressive { get; set; }

    /// <summary>
    /// Gets or sets whether to compute Huffman tables from the actual data (smaller files, one extra pass and the whole
    /// coefficient set in memory) instead of the standard tables (streaming). Ignored when <see cref="Progressive"/> is set. Default <see langword="false"/>.
    /// </summary>
    public bool OptimizeHuffman { get; set; }

    /// <summary>Gets or sets the restart interval in MCUs (0 for none). Restart markers cost a few bytes each and limit damage from corruption. Default 0.</summary>
    public int RestartInterval { get; set; }
}
