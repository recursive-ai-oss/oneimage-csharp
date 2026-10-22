# OneImage

A pure-managed .NET image library: one-line thumbnail, crop and convert, and lazy, tiled, streaming load/edit/render on the same engine. It depends on nothing beyond the .NET base class library, reads untrusted files through bounds-checked managed code, and is NativeAOT- and trim-compatible.

**Status:** early development (version `0.0.1-dev`). The package is not published anywhere yet and the name is provisional. Until it is, use it from source: clone the repository and add a project reference to `src/OneImage/OneImage.csproj` (target framework `net10.0`).

## Quick start

```csharp
using OneImage;

// A 400 px wide thumbnail, upright, in sRGB, without EXIF/GPS, written as WebP.
Image.Thumbnail("in.jpg", 400).Save("thumb.webp");

// Load, edit, save. Nothing is decoded until Save.
Image.Load("in.jpg")
    .Resize(width: 1200)
    .Rotate(90)
    .Save("out.png");

// Format and quality.
Image.Load("in.png").Save("out.jpg", new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 85 } });
```

The format of `Save` follows the file extension. For streams and buffer writers, name it: `image.Save(stream, PngFormat.Instance)`.

## Two types

- **`Image`** is lazy and immutable. `Load`, `Resize`, `Crop`, `Composite` and the other operations only record what to do and return a new `Image`. Pixels are computed, tile by tile, when you save or read them. Because nothing is copied until then, a 24 MP photo can become a 400 px thumbnail without ever being held at full size.
- **`Canvas`** is a mutable, tiled surface for editing. `image.ToCanvas()` and `canvas.Snapshot()` move between the two; a snapshot costs no copy.

Read [docs/guide.md](docs/guide.md) next: it explains the lazy model and what follows from it (when errors appear, how sources are held, metadata, color, limits, threading). Coming from another library? See [docs/migrating.md](docs/migrating.md).

## Formats

| Format | Read | Write |
|---|---|---|
| PNG | all color types and depths, interlaced, animated (APNG) | 8/16-bit gray, truecolor, ± alpha, indexed, animated; compression level and filter |
| JPEG | baseline, progressive, arithmetic, 12-bit, gray, CMYK/YCCK; shrink-on-load | baseline and progressive; quality, subsampling, optimized tables |
| WebP | lossy, lossless, alpha, animation | lossy, lossless, alpha, animation |
| GIF | animation, transparency, disposal | animation, palette quantizing with dithering, frame differencing |
| TIFF | strips and tiles, BigTIFF, none/LZW/Deflate/PackBits/JPEG/CCITT, multi-page | none/LZW/Deflate/PackBits, multi-page, BigTIFF |
| BMP | 1–32 bpp, RLE, V4/V5 headers | 24/32 bpp |
| ICO | yes | yes |

## Building and testing

```
dotnet build -c Release
dotnet test -c Release
```

Build output goes outside the checkout: set `ONEIMAGE_ARTIFACTS` to choose the directory, otherwise it defaults to `../OneImage-artifacts` next to the repository. Every code sample in the documentation is compiled and run by `tests/OneImage.Tests/Docs/`.

## License

Licensed under either of [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
