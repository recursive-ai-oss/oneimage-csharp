namespace OneImage.Pdf;

/// <summary>Which page box of a PDF page is drawn.</summary>
public enum PdfPageBox
{
    /// <summary>The crop box, or the media box when the page has none. The default; this is what viewers show.</summary>
    CropBox,

    /// <summary>The media box.</summary>
    MediaBox,

    /// <summary>The bleed box, falling back to the crop box.</summary>
    BleedBox,

    /// <summary>The trim box, falling back to the crop box.</summary>
    TrimBox,

    /// <summary>The art box, falling back to the crop box.</summary>
    ArtBox,
}

/// <summary>Which annotation appearances are drawn on top of the page content.</summary>
public enum PdfAnnotationRendering
{
    /// <summary>Annotations a viewer shows on screen: not hidden and not marked NoView. The default.</summary>
    View,

    /// <summary>Annotations that print: the Print flag set and not hidden.</summary>
    Print,

    /// <summary>No annotations.</summary>
    None,
}

/// <summary>
/// How <see cref="PdfFormat"/> draws PDF pages. Every member has a default, so <c>new PdfRenderOptions()</c> is a sensible setup:
/// all pages, the crop box, 150 dpi, annotations as on screen, on an opaque white background.
/// </summary>
public sealed class PdfRenderOptions
{
    /// <summary>
    /// Gets the resolution that defines the natural size of a page: <c>width in points x Dpi / 72</c> pixels, rounded to the nearest
    /// pixel (at least 1). A resize that sits directly on the loaded image draws the page at the final size instead, whatever this is.
    /// Must be finite and positive. Default 150.
    /// </summary>
    public double Dpi { get; init; } = 150;

    /// <summary>Gets the zero-based index of the first page that becomes a frame. Default 0.</summary>
    public int FirstPage { get; init; }

    /// <summary>
    /// Gets how many pages become frames, counted from <see cref="FirstPage"/>; fewer if the document ends sooner. A selection larger than
    /// <see cref="ImageLimits.MaxFrames"/> fails with <see cref="ImageLimitException"/>: choose a range, or raise the limit. Default all pages.
    /// </summary>
    public int PageCount { get; init; } = int.MaxValue;

    /// <summary>Gets which page box is drawn. Default <see cref="PdfPageBox.CropBox"/>.</summary>
    public PdfPageBox Box { get; init; } = PdfPageBox.CropBox;

    /// <summary>Gets which annotation appearances are drawn. Default <see cref="PdfAnnotationRendering.View"/>.</summary>
    public PdfAnnotationRendering Annotations { get; init; } = PdfAnnotationRendering.View;

    /// <summary>
    /// Gets whether form fields and markup annotations without a usable appearance stream are drawn from an appearance OnePDF generates
    /// (text and choice values, check marks, highlights, lines and so on), and whether a form that sets <c>/NeedAppearances true</c> has its
    /// field appearances rebuilt, as viewers do. Default <see langword="true"/>. Has no effect when <see cref="Annotations"/> is
    /// <see cref="PdfAnnotationRendering.None"/>.
    /// </summary>
    public bool GenerateAppearances { get; init; } = true;

    /// <summary>
    /// Gets the color the page is drawn on. PDF pages have no background of their own; viewers show white. Use a transparent color to
    /// get the page content alone. Default opaque white.
    /// </summary>
    public Rgba32 Background { get; init; } = new(255, 255, 255, 255);

    /// <summary>Gets the password of an encrypted document, or <see langword="null"/> for the empty user password. Default <see langword="null"/>.</summary>
    public string? Password { get; init; }

    /// <summary>Gets the bounds on what drawing may cost. Default <see cref="PdfRenderLimits.Default"/>.</summary>
    public PdfRenderLimits Limits { get; init; } = PdfRenderLimits.Default;

    /// <summary>Gets the limits for reading the document and interpreting its content. <see langword="null"/> uses OnePDF's defaults.</summary>
    public OnePdf.PdfLimits? DocumentLimits { get; init; }
}
