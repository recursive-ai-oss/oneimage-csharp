using OneImage.Pdf.Render;

namespace OneImage.Pdf;

/// <summary>
/// The PDF format registration: recognizes PDF files and draws their pages. Register it with <see cref="ImageConfiguration.Add"/>;
/// <see cref="ImageConfiguration.Default"/> does not include it, so the package trims away when unused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Frames.</b> Every selected page is one frame (see <see cref="PdfRenderOptions.FirstPage"/> and <see cref="PdfRenderOptions.PageCount"/>),
/// with its own size: the page box times <see cref="PdfRenderOptions.Dpi"/> / 72, with the page's rotation and user unit applied. Reading the size
/// (<see cref="Image.Identify(string, LoadOptions?)"/>) opens the document and reads page dictionaries only; no content is run.
/// </para>
/// <para>
/// <b>Drawing.</b> A page's content is interpreted once, on the first pull of pixels, into a display list. The picture is then drawn in
/// fixed 128 x 128 pixel cells by replaying only the part of the list that touches the cell, so a region is exactly the same pixels as
/// the same region of a whole render, at any thread count, and large pages stream. Output is 8-bit sRGB straight RGBA, drawn on
/// <see cref="PdfRenderOptions.Background"/> (opaque white by default). When a resize sits directly on the loaded image the page is drawn at
/// the final size (see <see cref="DecoderCapabilities.RenderAtSize"/>), not drawn large and scaled down.
/// </para>
/// <para>
/// <b>Failure.</b> The document structure is read when the image is loaded; damaged documents are repaired where possible (OnePDF records
/// what it repaired). A page whose content is damaged draws what could be read. Hostile content that exceeds a limit fails with
/// <see cref="ImageLimitException"/> when pixels are pulled, once for the page, not once per tile.
/// </para>
/// <para>
/// <b>Fonts.</b> Creating a format registers OnePDF's substitute fonts for the standard 14 fonts with <c>OnePdf.Fonts</c>, and OneImage's
/// JPEG decoder for DCT images. Fonts that are not embedded and are not standard are looked up among the installed fonts unless
/// <c>OnePdf.PdfFontProviders.UseSystemFonts</c> is turned off.
/// </para>
/// </remarks>
public sealed class PdfFormat : IImageFormat
{
    /// <summary>Initializes the format.</summary>
    /// <param name="options">How pages are drawn, or <see langword="null"/> for <see cref="PdfRenderOptions"/> defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PdfRenderOptions.Dpi"/> is not finite and positive, or a page selection value is negative.</exception>
    public PdfFormat(PdfRenderOptions? options = null)
    {
        options ??= new PdfRenderOptions();
        if (!double.IsFinite(options.Dpi) || options.Dpi <= 0 || options.Dpi > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Dpi must be finite and positive.");
        }

        if (options.FirstPage < 0 || options.PageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The page selection must not be negative.");
        }

        Options = options;
        Decoder = new PdfDecoder(this);
        OnePdf.Fonts.FontsPackage.Register();
        PdfImageCodecs.RegisterDefaults();
    }

    /// <summary>Gets a shared instance with default options.</summary>
    public static PdfFormat Instance { get; } = new();

    /// <summary>Gets the options of this registration.</summary>
    public PdfRenderOptions Options { get; }

    /// <inheritdoc />
    public string Name => "pdf";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".pdf"];

    /// <inheritdoc />
    public string MimeType => "application/pdf";

    /// <inheritdoc />
    public int HeaderLength => 1024;

    /// <inheritdoc />
    public IImageDecoder? Decoder { get; }

    /// <inheritdoc />
    public IImageEncoder? Encoder => null;

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> header)
    {
        // ISO 32000-2 7.5.2: the header is %PDF-; readers accept it anywhere in the first 1024 bytes.
        return header.IndexOf("%PDF-"u8) >= 0;
    }
}
