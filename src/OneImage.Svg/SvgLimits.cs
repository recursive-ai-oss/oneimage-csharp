namespace OneImage.Svg;

/// <summary>
/// Bounds on what one SVG document may cost. Every bound is checked while reading, before the memory it guards is
/// allocated, and a document that exceeds one fails with <see cref="ImageLimitException"/>.
/// These apply together with the <see cref="ImageLimits"/> of the load call (pixels per frame, dimension, expansion ratio).
/// </summary>
public sealed class SvgLimits
{
    /// <summary>Gets the default limits.</summary>
    public static SvgLimits Default { get; } = new();

    /// <summary>
    /// Gets the largest document in bytes: the file itself, and the decompressed size of a gzip-compressed (<c>.svgz</c>) file.
    /// Default 32 MiB.
    /// </summary>
    public int MaxDocumentBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Gets the largest number of elements. Default 500,000.</summary>
    public int MaxElements { get; init; } = 500_000;

    /// <summary>Gets the deepest element nesting. Default 256.</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>Gets the largest number of attributes (namespace declarations included) on one element. Default 512.</summary>
    public int MaxAttributesPerElement { get; init; } = 512;

    /// <summary>Gets the longest element, attribute or prefix name in characters. Default 256.</summary>
    public int MaxNameLength { get; init; } = 256;

    /// <summary>Gets how many namespace declarations may be in scope at once. Default 256.</summary>
    public int MaxNamespaceBindings { get; init; } = 256;

    /// <summary>Gets how many bytes of a file are examined to find the root element's start tag when only the size is wanted. Default 1 MiB.</summary>
    public int MaxRootTagScanBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Gets how many elements one render tree may instantiate in total, counting every copy made by <c>use</c>. A document whose
    /// references multiply past this (nested <c>use</c> fan-out) fails with <see cref="ImageLimitException"/> before the copies are drawn. Default 500,000.
    /// </summary>
    public int MaxInstances { get; init; } = 500_000;

    /// <summary>Gets how deep <c>use</c> references may chain (a <c>use</c> of a group containing a <c>use</c> ...). Default 32.</summary>
    public int MaxReferenceDepth { get; init; } = 32;

    /// <summary>Gets how deep isolated layers (groups with <c>opacity</c>) may nest. Default 32.</summary>
    public int MaxLayerDepth { get; init; } = 32;

    /// <summary>
    /// Gets the most pixels one pattern tile is rendered with, after scaling to the output. A pattern seen larger than this is
    /// rendered at a coarser resolution (nested patterns get half of their parent's share each), not refused. Default 16 megapixels.
    /// </summary>
    public int MaxPatternTilePixels { get; init; } = 16 * 1024 * 1024;

    /// <summary>Gets the largest total size in characters of all <c>&lt;style&gt;</c> elements and <c>style</c> attributes together. Default 2 MiB.</summary>
    public int MaxCssBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Gets how many style rules (one per selector of a selector list) the style sheets may define. Default 20,000.</summary>
    public int MaxCssRules { get; init; } = 20_000;

    /// <summary>
    /// Gets how many selector matching steps (one per simple-selector test or ancestor/sibling move) the cascade may spend on one document.
    /// Honest style sheets use a small fraction of this because rules are indexed by id, class and type; a hostile one is stopped with
    /// <see cref="ImageLimitException"/>. Default 100 million.
    /// </summary>
    public long MaxCssMatchSteps { get; init; } = 100_000_000;

    /// <summary>Gets how many levels of <c>image</c> elements may nest (an SVG image that itself contains an SVG image ...). Default 3.</summary>
    public int MaxImageDepth { get; init; } = 3;

    /// <summary>Gets the largest <c>data:</c> payload of one <c>image</c> in bytes. Default 16 MiB.</summary>
    public int MaxImageBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Gets the largest total of all <c>data:</c> image payloads in one document, nested images included, in bytes. Default 64 MiB.</summary>
    public long MaxTotalImageBytes { get; init; } = 64L * 1024 * 1024;
}
