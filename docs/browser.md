# OneImage in the browser (WebAssembly)

The core package and the Svg, Pdf, Psd and Text packages run on the .NET WebAssembly runtime (`browser-wasm`, the Mono runtime that Blazor WebAssembly and `Microsoft.NET.Sdk.WebAssembly` apps use). They need no native components and no workload: the libraries are plain managed code, trim-clean, and the output is the same pixels as on other hosts.

## What is different there

The browser runtime has **one thread**. A task queued to the thread pool does not run while the thread that queued it is waiting for it, and blocking on another thread throws `PlatformNotSupportedException` ("Cannot wait on monitors on this runtime"). So in the browser OneImage runs sequentially, and results are the same bytes as with threads (they never depend on the degree):

| Where | On other hosts | In the browser |
|---|---|---|
| Degree of parallelism | `min(4, cores)` per call, capped by `ImageEngine.MaxDegree` | Always 1, whatever `MaxDegree`, `DefaultCallDegree`, `maxDegree:` or the options say. Tile rendering, resize bands, encoders (GIF frame prefetch, WebP alpha and lossless parallelism, JPEG) and canvas drawing run in the calling thread. |
| PNG decode of 1 MiB or more | Inflates the next batch of rows on a pool task while unfiltering the current one | Decodes the batches in order |
| Frames of 16 MiB or more in their own format (a 600 dpi scan, a 20 MP photo) | Stream through a sliding window of tile rows with a decoder thread beside the reader, so memory depends on width, not height | Decoded once, whole, into tiles, like smaller frames |
| JPEG and WebP decode | Overlap entropy decoding, transform and delivery on helper threads for frames of about 1 MP and up | Single pass (these helpers are only started when the runtime reports more than one processor) |

Calls that return a `Task` (`LoadAsync`, `SaveAsync`) work, but only complete when you `await` them from the browser's event loop; never block on them with `.Wait()` or `.Result`.

## Memory and the pixel limits

A browser tab has far less memory than a server, and because large frames are decoded whole there, one hostile file could otherwise ask for about 1 GiB. The default `ImageLimits` are therefore lower in the browser (non-browser defaults are unchanged):

| Limit | Default elsewhere | Default in the browser |
|---|---|---|
| `MaxPixelsPerFrame` | 2²⁸ (268 M) | 2²⁶ (67 M: 8192 × 8192, 256 MiB as `Rgba8`) |
| `MaxTotalPixels` | 2³⁰ | 2²⁷ |

An A4 page at 600 dpi (about 36 MP) and a 50 MP photograph fit; a 100 MP image is refused with `ImageLimitException` before anything is allocated. All other limits are the same. To change them, pass your own limits per call or set the global default once at startup:

```csharp
ImageConfiguration.Default.Limits = new ImageLimits { MaxPixelsPerFrame = 1L << 27 };
Image.Load(bytes, new LoadOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 1L << 24 } }); // a stricter app
```

The 2²⁶ figure is an default chosen conservatively, not a measured browser limit: the 32-bit WebAssembly heap is 2 GiB to 4 GiB depending on the browser, and a decoded frame plus the buffer you read it into must both fit. Raise it only if you know your target browsers; lower the 256 MiB tile cache (`ImageEngine.CacheBudgetBytes`) and call `image.ReleaseDecodedPixels()` when you are done with a large image.

In the browser the decode of a large frame is not bounded by the streaming window, so `crop`, region reads and thumbnails of a big PNG or JPEG hold the whole decoded frame until you release it.

## Building an app

```xml
<Project Sdk="Microsoft.NET.Sdk.WebAssembly">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="RecursiveAI.OneImage" Version="..." />
  </ItemGroup>
</Project>
```

No `wasm-tools` workload is required, and none of the settings (trimming, AOT, `InvariantGlobalization`, threads) changes the results. A build with `WasmEnableThreads` still runs OneImage with degree 1.

## What CI checks

The `wasm` job publishes `tests/OneImage.WasmSmoke` and runs it in headless Chrome, twice: Release (trimmed, trim warnings are errors) and Debug (untrimmed, on the Mono interpreter). The app fails on any exception or Mono assertion. It covers:

- rendering image-only (scanned-style) PDF pages to RGBA through the Pdf package, including a JPEG scan, a bilevel scan and a calibrated-color fill;
- encode and decode round trips of 1.2 MP images in PNG, JPEG, WebP and TIFF;
- 20 MB PNG and JPEG frames: whole read, region reads, crop + resize + save, color conversion on the decode, and quantizing;
- raised `MaxDegree` / `DefaultCallDegree` (8 and 4) with an animated GIF to GIF and WebP, WebP lossy and lossless encodes with alpha, and a resize;
- sRGB to Display P3, tone operations and an SVG with filters;
- the browser pixel limits.

## Known gaps

- Everything is single-threaded, so large operations are slower than on a desktop host, and a long call blocks the page unless you run the app in a Web Worker.
- Large frames are held whole (see above); there is no bounded-memory streaming in the browser.
- The 2²⁶ default is untested on real devices with little memory (phones).
- Not covered by the smoke app: PSD, Text and fonts, Web (ASP.NET Core only), and the JPX and JBIG2 decoders reached through PDF.
- NativeAOT-compiled WebAssembly (`RunAOTCompilation`) has been tried once with the smoke app and passes, but is not run in CI.
