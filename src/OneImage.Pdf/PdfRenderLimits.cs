namespace OneImage.Pdf;

/// <summary>
/// Bounds on what one PDF may cost to draw. Each bound is checked against the value the file states, before the memory it guards is
/// allocated, and a file that exceeds one fails with <see cref="ImageLimitException"/> (or, for a single image or pattern, is skipped
/// where the PDF specification asks readers to ignore what they cannot show: see each member).
/// These apply together with the <see cref="ImageLimits"/> of the load call (pixels per frame, dimension, frames) and with the
/// <see cref="OnePdf.PdfLimits"/> that bound parsing and content interpretation.
/// </summary>
public sealed class PdfRenderLimits
{
    /// <summary>Gets the default limits.</summary>
    public static PdfRenderLimits Default { get; } = new();

    /// <summary>Gets the largest file in bytes that is read into memory. Larger files fail with <see cref="ImageLimitException"/>. Default 1 GiB.</summary>
    public long MaxFileBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Gets the largest image in pixels (width times height as the file states them) that is drawn. A larger image is skipped, not
    /// decoded, and the rest of the page is still drawn. Default 2^26 (67 million), which holds a 600 dpi scan of an A3 sheet.
    /// </summary>
    public long MaxImagePixels { get; init; } = 1L << 26;

    /// <summary>
    /// Gets the deepest nesting of transparency groups, soft masks and pattern cells being drawn at once. Deeper content fails with
    /// <see cref="ImageLimitException"/>. Default 24.
    /// </summary>
    public int MaxNesting { get; init; } = 24;

    /// <summary>
    /// Gets the deepest stack of clips (<c>W n</c> inside <c>q</c>) that is followed. Deeper content fails with
    /// <see cref="ImageLimitException"/>. Default 4,096.
    /// </summary>
    public int MaxClipDepth { get; init; } = 4096;

    /// <summary>
    /// Gets the largest side in pixels of the picture a tiling pattern's cell is drawn into. A cell that would need more is drawn at a
    /// lower resolution, so the pattern is softer but still shown. Default 2,048.
    /// </summary>
    public int MaxPatternTileSide { get; init; } = 2048;

    /// <summary>
    /// Gets how many copies of a pattern cell are drawn into one tile picture when the cell's box is larger than its step. More
    /// copies are not drawn. Default 64.
    /// </summary>
    public int MaxPatternCopies { get; init; } = 64;

    /// <summary>
    /// Gets the largest number of pixels of intermediate pictures (decoded and reduced images, pattern tiles) kept alive for one page
    /// while it is drawn. Pictures beyond the budget are rebuilt when needed instead of kept. Default 2^26.
    /// </summary>
    public long MaxCachedPixels { get; init; } = 1L << 26;
}
