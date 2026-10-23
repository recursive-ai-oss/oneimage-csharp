# OneImage

**A pure-managed .NET image library: one-line thumbnails, crops and
conversions, and lazy, tiled, streaming load, edit and render on the
same engine.** PNG, JPEG, WebP, GIF, TIFF, BMP and ICO, with ICC color
management. Pure C# with no dependency beyond .NET itself; untrusted
files are read through bounds-checked managed code. It also includes
vector drawing and a layered document model (`OneImage.Layers`: pixel,
group, fill and adjustment layers, masks, clipping and blend modes,
rendered on demand).

How many times faster OneImage is than ImageSharp 3.1.12 on each
benchmark task (log scale, higher is better; slower tasks and ties are
marked):

![Bar chart: how many times faster OneImage is than ImageSharp on each benchmark task, log scale, higher is better. Almost every bar is a win; one task is a tie and one, where ImageSharp skips the ICC color conversion, is slower.](https://raw.githubusercontent.com/recursive-ai-oss/oneimage-csharp/v1.5.0/docs/img/oneimage-hero-light.svg)

[Full results, with memory, the losses and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/docs/performance.md)

## Install

```sh
dotnet add package RecursiveAI.OneImage
```

The package is `RecursiveAI.OneImage`; the namespace is `OneImage`
(`using OneImage;`).

Target framework `net10.0`.

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

The format of `Save` follows the file extension. For streams and buffer
writers, name it: `image.Save(stream, PngFormat.Instance)`.

## What is new in 1.5.0

- **JPEG is faster, with identical output.** A 24 MP 4:2:0 photograph decodes into a reused buffer in 32 ms (ImageSharp 82-97 ms on the same worker); files with restart markers decode their intervals in parallel (25.5 ms full size, 9.0 ms at 1/8); a q90 encode takes 51.7 ms (was 82.8; ImageSharp 101.7). The inverse and forward DCTs also have 128-bit vector paths, so hosts without AVX2 decode 24 MP in 47 ms instead of 79.
- **TIFF is faster.** Whole-frame reads decode blocks in parallel straight into your buffer (a 10-page LZW read allocates 92 KB instead of 965 and takes 46.9 ms at the default degree), and LZW decoding and the horizontal predictor are faster.
- **WebP encoding needs far less memory.** A 24 MP lossy encode peaks at about 101 MB instead of 304, and a 24 MP lossless encode at 377 MB instead of 1056 (SkiaSharp 663, Magick.NET 736), with byte-identical output.
- **Photoshop-style tone modes** (opt-in, existing overloads unchanged): `Posterize`, `Curves` and `BrightnessContrast` take a mode argument.
- **Layer styles:** `Layer.Style` (`LayerStyle`) draws drop and inner shadows, glows, strokes, color overlays and satin when a layered document is composited.
- The `RecursiveAI.OneImage.Psd` package reads Photoshop's formulas, gradient and pattern fills and layer effects.

## Learn more

- [Guide](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/docs/guide.md): the lazy model, errors, operations,
  `Canvas`, metadata, color, animation, limits, AOT and trimming.
- [Layers](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/docs/layers.md): layered documents that render on demand, with
  incremental re-rendering and an optional disk-spill store. Photoshop files
  load into them through the separate `RecursiveAI.OneImage.Psd` package
  ([PSD page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/docs/psd.md)).
- [Migrating from ImageSharp and System.Drawing](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/docs/migrating.md)
- [How OneImage compares](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/docs/comparison.md), including where the
  others are the better fit
- [Benchmarks and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/docs/performance.md)
- [Changelog](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/CHANGELOG.md) and
  [security policy](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/SECURITY.md)

## License

Licensed under either of [MIT](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/LICENSE-MIT) or
[Apache-2.0](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
