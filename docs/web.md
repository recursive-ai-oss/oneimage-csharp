# OneImage.Web: image processing for ASP.NET Core

`RecursiveAI.OneImage.Web` resizes, crops, converts and recompresses images on request, driven by URL commands, and caches the results on disk. It is built on the OneImage library, so a thumbnail of a 24 MP JPEG is decoded at reduced size rather than in full. It is a separate package: the core `RecursiveAI.OneImage` package stays free of ASP.NET Core. Target framework `net10.0`; no reflection, trimmable and NativeAOT-compatible (Razor tag helpers aside, which Razor itself does not support under AOT).

Contents: [quick start](#quick-start) · [commands](#commands) · [ImageSharp.Web compatibility](#imagesharpweb-compatibility) · [security defaults](#security-defaults) · [signed URLs](#signed-urls) · [caching](#caching-and-conditional-requests) · [providers](#providers) · [hooks](#hooks) · [how it works](#how-it-works) · [errors](#status-codes)

## Quick start

```sh
dotnet add package RecursiveAI.OneImage.Web
```

```csharp
using OneImage.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOneImage();

var app = builder.Build();
app.UseOneImage();      // before UseStaticFiles
app.UseStaticFiles();
app.Run();
```

Now every image under `wwwroot` can be asked for in another size or format:

```text
/photos/cat.jpg?width=400                    400 px wide, same format, aspect ratio kept
/photos/cat.jpg?w=300&h=300&fit=cover        300 x 300, cropped around the center
/photos/cat.jpg?width=800&format=webp&q=80   WebP at quality 80
```

A request without commands is not touched and falls through to the static file middleware. `AddOneImage` takes the settings and returns a builder:

```csharp
builder.Services.AddOneImage(o =>
{
    o.MaxWidth = 2000;
    o.Presets["thumb"] = "width=200&height=200&fit=cover&format=webp";
    o.HmacSecretKey = Convert.FromBase64String(builder.Configuration["Images:Key"]!);
})
.ConfigureCache(c => c.CacheRootPath = "/var/cache/images");
```

In Razor, `@addTagHelper *, OneImage.Web` enables `<img src="~/photos/cat.jpg" oi-width="400" oi-format="webp" />`, which writes the URL with the commands and, when a key is configured, the signature. In code, `ImageUrlSigner.Sign("/photos/cat.jpg?width=400")` does the same.

## Commands

Names are case-insensitive. Each command may appear once; two spellings of the same command with different values fail with 400.

| Command (aliases) | Values | Default | Meaning |
|---|---|---|---|
| `width` (`w`), `height` (`h`) | 1 to `MaxWidth` / `MaxHeight` | none | Target size. With one of them the other follows the aspect ratio. With none the size is kept. |
| `fit` | `contain`, `cover` (`crop`), `stretch`, `pad` | `OneImageOptions.DefaultFit` (`contain`) | How the image fills a width-and-height box. |
| `anchor` (`ranchor`) | `center`, `top`, `bottom`, `left`, `right`, `topleft`, `topright`, `bottomleft`, `bottomright`, `smart` | `center` | Which part a crop keeps or where padding goes. `smart` picks the busiest part. |
| `focus` (`xy`) | `x,y`, each 0 to 1 | none | A focal point for `cover` with both sizes; the crop is centered on it as far as the edges allow. Replaces `anchor`. |
| `upscale` | `1`/`0`, `true`/`false` | `DefaultUpscale` (`false`) | Allow enlarging past the source size. |
| `filter` (`rsampler`) | `auto`, `nearest`, `box`, `bilinear` (`triangle`), `catmullrom` (`bicubic`), `mitchell`, `lanczos2`, `lanczos3` | `auto` | Resampling filter. Lanczos3 when shrinking, Catmull-Rom when enlarging. Resampling always happens in linear light. |
| `bg` | color | none | Padding color and the color transparency is flattened onto. |
| `format` (`fm`) | `jpeg`/`jpg`, `png`, `webp`, `gif`, `bmp` (those in `AllowedFormats`) | the source format if allowed, else PNG | Output format. |
| `quality` (`q`) | 1 to 100 | encoder default (75) | JPEG and WebP quality. Ignored for other formats. |
| `orient` (`autoorient`) | `1`/`0` | `1` | Apply the EXIF orientation. |
| `preset` | a name from `Presets` | none | A stored command string. Explicit commands override its values. |

Colors are `rgb`, `rgba`, `rrggbb` or `rrggbbaa` hex (with or without `#`, which must be sent as `%23`), `r,g,b[,a]` numbers, or `white`, `black`, `transparent`, `red`, `green`, `blue`, `yellow`, `gray`.

**Unknown parameters** are ignored by default and take no part in the cache key, so `?width=100&utm_source=x` and `?width=100` are one cache entry. `UnknownCommands = Reject` makes them 400 errors (list the ones that belong to other middleware in `IgnoredParameters`). A request with no recognized command is not processed.

**Invalid values** (`width=abc`, `quality=0`, an unknown format or preset) are 400 errors; nothing is silently clamped.

**Presets only.** `PresetsOnly = true` honors `preset` and nothing else; all other parameters are ignored. The set of possible results is then the presets times your files, which is the right setting for a public site that does not sign its URLs.

## ImageSharp.Web compatibility

The ImageSharp.Web 3.2.0 spellings are accepted alongside ours, so existing URLs keep working:

| ImageSharp.Web | Here |
|---|---|
| `width`, `height` | same |
| `rmode=crop` / `pad` / `boxpad` / `max` / `min` / `stretch` | `crop` is `fit=cover` with enlarging, `pad` is `fit=pad`, `boxpad` is `fit=pad` without enlarging, `max` is `fit=contain` without enlarging, `min` scales to cover the box without cropping, `stretch` is `fit=stretch`. `manual` is not supported and fails with 400. |
| `ranchor` / `anchor`, `xy` | same (`anchor` also accepts `smart`) |
| `rsampler` | `nearest`, `box`, `triangle`, `bicubic`, `mitchell`, `lanczos2`, `lanczos3`. `welch`, `hermite`, `spline`, `robidoux`, `lanczos5` and `lanczos8` are not available and fail with 400. |
| `color` | padding color |
| `bgcolor` | the color transparency is flattened onto |
| `orient` / `autoorient`, `format`, `quality` | same |
| `compand` | accepted and ignored: resampling is always in linear light |
| `preset` (preset-only parser) | `PresetsOnly` |

Defaults differ in one place: ImageSharp.Web crops when a width and a height are given without a mode, and enlarges. Call `options.UseImageSharpDefaults()` (it sets `DefaultFit = Cover` and `DefaultUpscale = true`) for the same behavior. HMAC tokens are not interchangeable with ImageSharp.Web's (see below); regenerate signed URLs.

Setting mapping: `BrowserMaxAge`, `CacheMaxAge`, `CacheHashLength`, `OnParseCommandsAsync`, `OnBeforeSaveAsync`, `OnProcessedAsync` and `OnPrepareResponseAsync` keep their names; `HMACSecretKey` is `HmacSecretKey`. ImageSharp.Web's custom `IImageWebProcessor` classes have no equivalent: do the extra work in `OnBeforeSaveAsync`, which receives the lazy `Image`.

## Security defaults

The defaults assume the endpoint is public and unauthenticated.

- **Output size is bounded.** `MaxWidth` and `MaxHeight` default to 4096 and `MaxOutputPixels` to 16,777,216. They apply to requested values (400 at parse) and to the resulting image, including when no size is requested, so re-encoding a 24 MP photo needs a higher `MaxOutputPixels`.
- **Source size is bounded.** `MaxSourceBytes` (64 MiB, 413) and `SourceLimits`, the library's decoding limits with 2^27 pixels per frame, 2^28 in total and 500 frames (413). A decompression-bomb file fails before its pixels are allocated.
- **Query length** is capped at 1024 characters.
- **Work is bounded.** At most `MaxConcurrentProcessing` images (the processor count, at least 2) are processed at once; others wait.
- **Cache growth.** Without a signing key, every distinct valid combination of commands creates a cache entry, and nothing deletes entries. On a public site use `HmacSecretKey` or `PresetsOnly`, or restrict values in an `OnParseCommandsAsync` hook (for example snapping widths to a list). Stale and unwanted entries can be removed by deleting files in the cache folder at any time.
- **No path escapes.** Providers never serve a path outside their root; the physical provider also hides dot-files. Only paths with a known image extension are looked at.
- **Metadata.** EXIF, XMP and IPTC are removed (they can contain GPS positions); the ICC profile stays and the pixels are converted to sRGB as `Image.Thumbnail` does. `KeepMetadata = true` keeps them.
- **Cache folder** defaults to `oneimage-cache` in the content root, not under `wwwroot`, so cached files are only reachable through the middleware.
- Error responses are fixed short texts; they never echo input or file paths.

## Signed URLs

Set `HmacSecretKey` (at least 32 random bytes) and every request that carries commands must carry a valid `hmac` parameter, or it fails with **403**. Choose `HmacAlgorithm.Sha256` (default), `Sha384` or `Sha512`.

The token is the lower-case hex HMAC of the request path (decoded, with the path base) followed by `?` and the raw query string without the `hmac` parameter, exactly as sent. Any change to the path or the query, including the order of parameters, invalidates it. The token is compared in constant time. There is no expiry in this version; rotate the key to revoke.

```csharp
var signer = app.Services.GetRequiredService<ImageUrlSigner>();
string url = signer.Sign("/photos/cat.jpg?width=400&format=webp");
// /photos/cat.jpg?width=400&format=webp&hmac=3f8a...
```

`HmacUtilities.ComputeToken(value, key, algorithm)` and `HmacUtilities.SignedText(path, query)` are available for signing from other code. Requests without recognized commands need no token (they are not processed), and presets need one too when a key is set.

## Caching and conditional requests

**Cache key.** `PathCacheKey` builds `request path + newline + canonical commands`. The canonical text is the *normalized* command set with every default applied (`ProcessingCommands.ToCanonicalString()`, for example `v1;w=400;fit=Contain;anchor=Center;filter=Auto;up=0;fmt=webp;q=80;orient=1;meta=0`), plus `CacheSalt` if set. Aliases, parameter order, case and unknown parameters therefore do not create separate entries. `HostPathCacheKey` also includes the host. Replace the key with `SetCacheKey<T>()`.

**Cache hash and layout.** `Sha256CacheHash` hashes the key; the first `CacheHashLength` hex characters (default 32, 12 to 64) name the files, spread over `CacheFolderDepth` folders (default 2) of `CacheFolderNameLength` characters (default 2): `cache/ab/cd/abcd....img` and a `.meta` text record. The record holds the full hash, so a collision on the shortened name causes a reprocess, never a wrong image.

**Invalidation on source change.** The record stores the source's modification time and length. On each request the provider's current values are compared; any difference reprocesses the image and replaces the entry. Entries older than `CacheMaxAge` (365 days) are replaced too. `CacheSalt` invalidates everything at once.

**Writing.** Files are written under a temporary name and renamed, the record last, so readers never see partial entries and several processes can share a folder. If a cache file was deleted, the next request rebuilds it. A failing cache write is logged and the response is still served.

**Response headers.** `ETag` (derived from the key hash and the source version, so it is known without reading the cache), `Last-Modified` (the source's), `Cache-Control: public, max-age=<BrowserMaxAge>` (7 days; zero sends `no-cache`), `Content-Type`, `Content-Length` and `X-Content-Type-Options: nosniff`. `If-None-Match` (weak comparison, `*`) and `If-Modified-Since` produce **304** without reading or processing anything. `HEAD` is answered with the headers only.

**Concurrent identical requests** are processed once: the first request starts the work, the others wait for it (and may give up if their client disconnects, without cancelling the work). The source version is part of that wait key, so a request for a newer source never receives the older result.

## Providers

A provider finds the source file for a request. Providers are asked in registration order until one returns a source; a provider can restrict itself to a path prefix.

| Provider | Source |
|---|---|
| `WebRootImageProvider` | `wwwroot` (registered by default) |
| `PhysicalFileImageProvider(root, behavior, prefix)` | any folder, under a URL prefix |
| `FileProviderImageProvider(IFileProvider, behavior, prefix)` | any `IFileProvider` |
| your own `IImageProvider` | databases, blob storage, anything with a modified time and a length |

`ProcessingBehavior.CommandOnly` (default) processes only requests with commands; `All` also re-encodes plain requests.

```csharp
builder.Services.AddOneImage()
    .ClearProviders()
    .AddProvider(_ => new PhysicalFileImageProvider("/srv/photos", requestPathPrefix: "/photos"))
    .AddProvider<MyBlobProvider>();
```

`SetCache<T>()`, `SetCacheKey<T>()` and `SetCacheHash<T>()` replace the other parts. An `IImageCache` needs two methods (get and set by hash).

## Hooks

| Hook | When | Typical use |
|---|---|---|
| `OnParseCommandsAsync` | after the query was read and the signature checked | add `format=webp` when the browser accepts it (add a `Vary: Accept` header in `OnPrepareResponseAsync`), snap sizes |
| `OnBeforeSaveAsync` | on the lazy result image, before encoding | watermark, extra filters (change `CacheSalt` when it changes) |
| `OnProcessedAsync` | after an image was actually processed, not on cache hits | metrics |
| `OnPrepareResponseAsync` | before any response of the middleware, 304 included | headers |

## How it works

For an image request the middleware, in order: ignores non-GET/HEAD requests and paths without an image extension; reads the commands (expanding a preset); verifies the signature; runs `OnParseCommandsAsync`; asks the providers for the source; validates and normalizes the commands against the limits; computes the cache key, hash and ETag; answers 304 if the client's validators match; looks in the cache and checks the source version; otherwise processes once (see above) and stores the result.

Processing is the library's thumbnail pipeline: the source bytes are read once (bounded by `MaxSourceBytes`), decoded lazily with the configured limits, resized (JPEG sources are decoded at 1/2, 1/4 or 1/8 size when the target allows), converted to sRGB, stripped of EXIF/XMP/IPTC, flattened for formats without alpha, and encoded.

## Status codes

| Code | When |
|---|---|
| 200, 304 | served; not modified |
| 400 | invalid, unknown (when rejecting), conflicting or over-limit command; long query; result over the output limits |
| 403 | missing or wrong `hmac` while a key is configured |
| 413 | source file over `MaxSourceBytes`, or over the decoding limits |
| 415 | the source is not a readable image |
| 422 | the image could not be processed |
| (next middleware) | no recognized command, no such file, other method or extension |

## Not included yet

Cloud storage providers and caches, per-URL expiry for signed links, a fluent URL builder, an in-memory result cache in front of the disk cache, and automatic cache cleanup.
