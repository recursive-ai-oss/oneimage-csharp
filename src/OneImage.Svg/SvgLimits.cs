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
}
