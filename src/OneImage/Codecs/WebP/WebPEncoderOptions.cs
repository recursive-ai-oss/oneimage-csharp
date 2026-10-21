namespace OneImage;

/// <summary>Settings for writing WebP files. Pass an instance through <see cref="SaveOptions.EncoderOptions"/>.</summary>
public sealed class WebPEncoderOptions : ImageEncoderOptions
{
    /// <summary>
    /// Gets or sets whether to write lossless (VP8L) data. The default is <see langword="false"/>: lossy (VP8) at
    /// <see cref="Quality"/> 75, which is also libwebp's default.
    /// </summary>
    public bool Lossless { get; init; }

    /// <summary>
    /// Gets or sets the lossy quality from 0 (smallest) to 100 (best); default 75. Lossy files with transparency store the
    /// alpha channel losslessly. Ignored for lossless files.
    /// </summary>
    public int Quality { get; init; } = 75;

    /// <summary>
    /// Gets or sets the search effort from 0 (fastest) to 6 (smallest output); default 4. For lossless files it widens the
    /// transform and match searches; for lossy files it enables rate-distortion mode decisions.
    /// </summary>
    public int Effort { get; init; } = 4;

    /// <summary>
    /// Gets or sets whether the color values of fully transparent pixels are preserved (default <see langword="true"/>
    /// for lossless files so that data round-trips bit-exactly). When <see langword="false"/> they may be replaced to
    /// improve compression.
    /// </summary>
    public bool Exact { get; init; } = true;
}
