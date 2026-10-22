using System.Globalization;
using System.Text;

namespace OneImage.Web;

/// <summary>
/// The validated, normalized form of a request's commands: every default is applied, aliases and the ImageSharp.Web
/// spellings are resolved, and values are within the configured limits. Two requests that mean the same share one cache entry.
/// </summary>
public sealed class ProcessingCommands
{
    internal ProcessingCommands()
    {
    }

    /// <summary>Gets the requested width in pixels, or <see langword="null"/> to derive it from the height (or keep the source width when no size is given).</summary>
    public int? Width { get; internal set; }

    /// <summary>Gets the requested height in pixels, or <see langword="null"/>.</summary>
    public int? Height { get; internal set; }

    /// <summary>Gets how the image fits a width-and-height box.</summary>
    public Fit Fit { get; internal set; }

    /// <summary>Gets whether the result is at least as large as the box in both directions without cropping (ImageSharp.Web <c>rmode=min</c>). <see cref="Fit"/> is <see cref="Fit.Stretch"/> then, applied to the computed size.</summary>
    public bool CoverWithoutCrop { get; internal set; }

    /// <summary>Gets where a cropped or padded image sits.</summary>
    public Anchor Anchor { get; internal set; }

    /// <summary>Gets the focal point (x, y, each from 0 at the left/top to 1 at the right/bottom) a crop is centered on, or <see langword="null"/>. Only set when <see cref="Fit"/> is <see cref="Fit.Cover"/> and both dimensions are given; it replaces <see cref="Anchor"/>.</summary>
    public (double X, double Y)? Focus { get; internal set; }

    /// <summary>Gets the resampling filter.</summary>
    public ResamplingFilter Filter { get; internal set; }

    /// <summary>Gets whether the image may be enlarged.</summary>
    public bool Upscale { get; internal set; }

    /// <summary>Gets the colour that padding is filled with, or <see langword="null"/> for transparent.</summary>
    public Rgba32? PadColor { get; internal set; }

    /// <summary>Gets the colour transparent pixels are flattened onto, or <see langword="null"/> to flatten only when the output format needs it (onto <see cref="OneImageOptions.FlattenColor"/>).</summary>
    public Rgba32? FlattenColor { get; internal set; }

    /// <summary>Gets the requested output format, or <see langword="null"/> to keep the source format when it is allowed and PNG otherwise.</summary>
    public IImageFormat? Format { get; internal set; }

    /// <summary>Gets the requested JPEG/WebP quality from 1 to 100, or <see langword="null"/> for the encoder default.</summary>
    public int? Quality { get; internal set; }

    /// <summary>Gets whether the EXIF orientation is applied.</summary>
    public bool AutoOrient { get; internal set; }

    /// <summary>Gets whether EXIF, XMP and IPTC are kept.</summary>
    public bool KeepMetadata { get; internal set; }

    /// <summary>Gets the canonical text form that goes into the cache key. Stable across releases of the same major version.</summary>
    /// <returns>The canonical string.</returns>
    public string ToCanonicalString()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(96);
        sb.Append("v1");
        if (Width is { } w)
        {
            sb.Append(";w=").Append(w.ToString(c));
        }

        if (Height is { } h)
        {
            sb.Append(";h=").Append(h.ToString(c));
        }

        if (Width is not null || Height is not null)
        {
            sb.Append(";fit=").Append(Fit.ToString()).Append(CoverWithoutCrop ? "-min" : string.Empty)
              .Append(";anchor=").Append(Anchor.ToString())
              .Append(";filter=").Append(Filter.ToString())
              .Append(";up=").Append(Upscale ? '1' : '0');
            if (Focus is { } f)
            {
                sb.Append(";focus=").Append(f.X.ToString("R", c)).Append(',').Append(f.Y.ToString("R", c));
            }
        }

        if (PadColor is { } p)
        {
            sb.Append(";pad=").Append(Hex(p));
        }

        if (FlattenColor is { } fl)
        {
            sb.Append(";flat=").Append(Hex(fl));
        }

        if (Format is { } fmt)
        {
            sb.Append(";fmt=").Append(fmt.Name);
        }

        if (Quality is { } q)
        {
            sb.Append(";q=").Append(q.ToString(c));
        }

        sb.Append(";orient=").Append(AutoOrient ? '1' : '0').Append(";meta=").Append(KeepMetadata ? '1' : '0');
        return sb.ToString();
    }

    private static string Hex(Rgba32 p) => string.Create(CultureInfo.InvariantCulture, $"{p.R:x2}{p.G:x2}{p.B:x2}{p.A:x2}");
}

/// <summary>The context of <see cref="OneImageOptions.OnParseCommandsAsync"/>.</summary>
public sealed class ImageCommandContext
{
    internal ImageCommandContext(Microsoft.AspNetCore.Http.HttpContext httpContext, IDictionary<string, string> commands)
    {
        HttpContext = httpContext;
        Commands = commands;
    }

    /// <summary>Gets the request.</summary>
    public Microsoft.AspNetCore.Http.HttpContext HttpContext { get; }

    /// <summary>Gets the commands of the request, keyed by canonical name (<c>width</c>, <c>height</c>, <c>fit</c>, <c>mode</c>, <c>anchor</c>, <c>focus</c>, <c>filter</c>, <c>bg</c>, <c>color</c>, <c>flatten</c>, <c>format</c>, <c>quality</c>, <c>orient</c>, <c>upscale</c>, <c>compand</c>). Presets are already expanded. Edit freely; the result is validated afterwards.</summary>
    public IDictionary<string, string> Commands { get; }
}

/// <summary>The context of <see cref="OneImageOptions.OnBeforeSaveAsync"/>.</summary>
public sealed class ImageSaveContext
{
    internal ImageSaveContext(Microsoft.AspNetCore.Http.HttpContext httpContext, ProcessingCommands commands, Image image)
    {
        HttpContext = httpContext;
        Commands = commands;
        Image = image;
    }

    /// <summary>Gets the request.</summary>
    public Microsoft.AspNetCore.Http.HttpContext HttpContext { get; }

    /// <summary>Gets the normalized commands.</summary>
    public ProcessingCommands Commands { get; }

    /// <summary>Gets or sets the processed image that is about to be encoded. Assign a new image to apply further operations.</summary>
    public Image Image { get; set; }
}

/// <summary>The context of <see cref="OneImageOptions.OnProcessedAsync"/>.</summary>
public sealed class ImageProcessedContext
{
    internal ImageProcessedContext(Microsoft.AspNetCore.Http.HttpContext httpContext, ProcessingCommands commands, string contentType, long length)
    {
        HttpContext = httpContext;
        Commands = commands;
        ContentType = contentType;
        Length = length;
    }

    /// <summary>Gets the request that triggered the processing.</summary>
    public Microsoft.AspNetCore.Http.HttpContext HttpContext { get; }

    /// <summary>Gets the normalized commands.</summary>
    public ProcessingCommands Commands { get; }

    /// <summary>Gets the MIME type of the encoded result.</summary>
    public string ContentType { get; }

    /// <summary>Gets the size of the encoded result in bytes.</summary>
    public long Length { get; }
}
