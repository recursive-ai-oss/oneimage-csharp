using Microsoft.AspNetCore.Http;

namespace OneImage.Web;

/// <summary>
/// Settings of the image middleware. The defaults are safe for a public site: bounded output size, bounded source size,
/// unknown parameters ignored, EXIF/XMP/IPTC removed, and nothing is written outside the cache folder.
/// </summary>
public sealed class OneImageOptions
{
    /// <summary>Gets or sets the library configuration (codecs). <see langword="null"/> (the default) uses <see cref="ImageConfiguration.Default"/>.</summary>
    public ImageConfiguration? Configuration { get; set; }

    /// <summary>
    /// Gets or sets how long browsers and shared caches may reuse a response without asking again (<c>Cache-Control: public, max-age</c>).
    /// Defaults to 7 days. <see cref="TimeSpan.Zero"/> sends <c>no-cache</c>, so clients always revalidate (cheap: a 304).
    /// </summary>
    public TimeSpan BrowserMaxAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Gets or sets how long a processed image stays valid in the cache before it is processed again. Defaults to 365 days. A changed source invalidates sooner.</summary>
    public TimeSpan CacheMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Gets or sets the length in hex characters of the cache file name (the leading part of the SHA-256 of the cache key).
    /// Defaults to 32 (128 bits); the valid range is 12 to 64. The full hash is also stored in the entry and checked, so a short name can only cost a reprocess, never a wrong image.
    /// </summary>
    public int CacheHashLength { get; set; } = 32;

    /// <summary>Gets or sets a string mixed into every cache key and ETag. Change it to invalidate everything, for instance after changing what an <see cref="OnBeforeSaveAsync"/> hook does.</summary>
    public string CacheSalt { get; set; } = string.Empty;

    /// <summary>Gets or sets the HMAC secret. When set, every request that carries commands must carry a valid <c>hmac</c> token or it fails with 403. Defaults to <see langword="null"/> (no signing). Use at least 32 random bytes.</summary>
    public byte[]? HmacSecretKey { get; set; }

    /// <summary>Gets or sets the HMAC hash function. Defaults to <see cref="HmacAlgorithm.Sha256"/>.</summary>
    public HmacAlgorithm HmacAlgorithm { get; set; }

    /// <summary>Gets or sets how unrecognized query parameters are treated. Defaults to <see cref="UnknownCommandBehavior.Ignore"/>.</summary>
    public UnknownCommandBehavior UnknownCommands { get; set; }

    /// <summary>Gets the parameter names that <see cref="UnknownCommandBehavior.Reject"/> lets through (case-insensitive), for parameters that belong to other middleware.</summary>
    public ISet<string> IgnoredParameters { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets whether only named presets are accepted (<c>?preset=thumb</c>). Every other command is ignored. Defaults to <see langword="false"/>. This is the strongest protection for an unsigned public endpoint: the set of possible cache entries is the product of the presets and the files.</summary>
    public bool PresetsOnly { get; set; }

    /// <summary>Gets the presets: a name and the command string it stands for, for example <c>["thumb"] = "width=200&amp;height=200&amp;fit=cover&amp;format=webp"</c>. Names are case-insensitive.</summary>
    public IDictionary<string, string> Presets { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the fit used when a width and a height are both given and no <c>fit</c>/<c>rmode</c> is. Defaults to <see cref="Fit.Contain"/>. <see cref="UseImageSharpDefaults"/> sets it to <see cref="Fit.Cover"/>, the ImageSharp.Web default.</summary>
    public Fit DefaultFit { get; set; } = Fit.Contain;

    /// <summary>Gets or sets whether images may be enlarged when a request does not say. Defaults to <see langword="false"/>: a thumbnail is never bigger than the original. A request can say <c>upscale=1</c>.</summary>
    public bool DefaultUpscale { get; set; }

    /// <summary>Gets or sets the largest requested or resulting width. Defaults to 4096. A request above it fails with 400.</summary>
    public int MaxWidth { get; set; } = 4096;

    /// <summary>Gets or sets the largest requested or resulting height. Defaults to 4096. A request above it fails with 400.</summary>
    public int MaxHeight { get; set; } = 4096;

    /// <summary>Gets or sets the largest resulting pixel count (width times height). Defaults to 16,777,216 (4096 x 4096). Applies to the output, including requests that give no size, so re-encoding a 24 MP photo needs a higher value.</summary>
    public long MaxOutputPixels { get; set; } = 1L << 24;

    /// <summary>Gets or sets the largest source file that is read, in bytes. Defaults to 64 MiB. Larger files fail with 413.</summary>
    public long MaxSourceBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Gets or sets the longest query string accepted, in characters. Defaults to 1024. Longer ones fail with 400.</summary>
    public int MaxQueryLength { get; set; } = 1024;

    /// <summary>Gets or sets the decoding limits for source images. Defaults to the library defaults with at most 2^27 pixels per frame, 2^28 pixels in total and 500 frames.</summary>
    public ImageLimits SourceLimits { get; set; } = new ImageLimits { MaxPixelsPerFrame = 1L << 27, MaxTotalPixels = 1L << 28, MaxFrames = 500 };

    /// <summary>Gets or sets how many images are processed at the same time; further requests wait. Defaults to the processor count (at least 2). Each image is also processed with the library's own bounded parallelism.</summary>
    public int MaxConcurrentProcessing { get; set; } = Math.Max(2, Environment.ProcessorCount);

    /// <summary>Gets the names of the formats a request may produce (<see cref="IImageFormat.Name"/>). Defaults to jpeg, png, webp, gif and bmp.</summary>
    public ISet<string> AllowedFormats { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "jpeg", "png", "webp", "gif", "bmp" };

    /// <summary>Gets or sets whether EXIF, XMP and IPTC are kept in the output. Defaults to <see langword="false"/>: they are removed because they can reveal location and device. The ICC profile always stays, and orientation is applied to the pixels.</summary>
    public bool KeepMetadata { get; set; }

    /// <summary>Gets or sets whether the stored EXIF orientation is applied. Defaults to <see langword="true"/>. A request can say <c>orient=0</c>.</summary>
    public bool AutoOrient { get; set; } = true;

    /// <summary>Gets or sets the colour that transparent pixels are flattened onto when the output format has no alpha (JPEG, BMP). Defaults to white.</summary>
    public Rgba32 FlattenColor { get; set; } = new(255, 255, 255, 255);

    /// <summary>Gets or sets a hook run after the commands were read from the query string and the signature was checked. It may add, change or remove commands in <see cref="ImageCommandContext.Commands"/> (keyed by the canonical names), for instance to choose WebP when the browser accepts it. If a hook varies its result by anything other than the URL, the response also needs a <c>Vary</c> header, set in <see cref="OnPrepareResponseAsync"/>.</summary>
    public Func<ImageCommandContext, Task>? OnParseCommandsAsync { get; set; }

    /// <summary>Gets or sets a hook run on the lazy image just before it is encoded. It may replace <see cref="ImageSaveContext.Image"/> to apply extra operations such as a watermark. The result is cached, so change <see cref="CacheSalt"/> whenever the hook's behavior changes.</summary>
    public Func<ImageSaveContext, Task>? OnBeforeSaveAsync { get; set; }

    /// <summary>Gets or sets a hook run after an image was actually processed (not on cache hits).</summary>
    public Func<ImageProcessedContext, Task>? OnProcessedAsync { get; set; }

    /// <summary>Gets or sets a hook run just before a response from this middleware is sent, including 304 responses. It may add or replace headers.</summary>
    public Func<HttpContext, Task>? OnPrepareResponseAsync { get; set; }

    /// <summary>
    /// Sets <see cref="DefaultFit"/> to <see cref="Fit.Cover"/> and <see cref="DefaultUpscale"/> to <see langword="true"/>, which is how ImageSharp.Web
    /// treats a request that gives a width and a height without a resize mode (crop, enlarging allowed).
    /// </summary>
    /// <returns>This instance.</returns>
    public OneImageOptions UseImageSharpDefaults()
    {
        DefaultFit = Fit.Cover;
        DefaultUpscale = true;
        return this;
    }

    internal void Validate()
    {
        if (CacheHashLength is < 12 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(CacheHashLength), "CacheHashLength must be between 12 and 64.");
        }

        if (MaxWidth < 1 || MaxHeight < 1 || MaxOutputPixels < 1 || MaxSourceBytes < 1 || MaxQueryLength < 1 || MaxConcurrentProcessing < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxWidth), "Size and concurrency limits must be positive.");
        }

        if (HmacSecretKey is { Length: 0 })
        {
            throw new ArgumentException("HmacSecretKey must not be empty.", nameof(HmacSecretKey));
        }

        if (BrowserMaxAge < TimeSpan.Zero || CacheMaxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BrowserMaxAge), "Durations must not be negative.");
        }
    }
}
