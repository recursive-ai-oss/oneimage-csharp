# OneImage.Web

**ASP.NET Core middleware that resizes, crops, converts and recompresses images on request**, driven by URL commands, with a disk cache, conditional requests (ETag, 304), HMAC-signed URLs and request limits. Built on [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp), a pure-managed image library: a thumbnail of a 24 MP JPEG is decoded at reduced size, and nothing native is involved.

## Install

```sh
dotnet add package RecursiveAI.OneImage.Web
```

Target framework `net10.0`.

## Use

```csharp
using OneImage.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOneImage();

var app = builder.Build();
app.UseOneImage();      // before UseStaticFiles
app.UseStaticFiles();
app.Run();
```

```text
/photos/cat.jpg?width=400
/photos/cat.jpg?w=300&h=300&fit=cover
/photos/cat.jpg?width=800&format=webp&q=80
```

The ImageSharp.Web query names (`rmode`, `rsampler`, `xy`, `bgcolor` and so on) work too, which makes moving from ImageSharp.Web a matter of swapping the registration calls.

- Safe by default: bounded output and source sizes, bounded concurrency, EXIF/GPS removed, unknown parameters ignored, nothing outside the provider root served.
- Signed URLs (HMAC-SHA-256/384/512), a presets-only mode, a Razor tag helper.
- Providers for the web root, any folder and any `IFileProvider`, or your own; cache, cache key and hash are replaceable.
- No reflection; trimmable and NativeAOT-compatible.

[Full guide: commands, ImageSharp.Web compatibility, security defaults, caching](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/docs/web.md)

License: MIT OR Apache-2.0.
