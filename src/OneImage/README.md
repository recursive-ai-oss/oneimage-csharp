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

![Bar chart: how many times faster OneImage is than ImageSharp on each benchmark task, log scale, higher is better. Almost every bar is a win; one task is a tie and one, where ImageSharp skips the ICC color conversion, is slower.](https://raw.githubusercontent.com/recursive-ai-oss/oneimage-csharp/v1.6.0/docs/img/oneimage-hero-light.svg)

[Full results, with memory, the losses and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/docs/performance.md)

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

## What is new in 1.6.0

- **PDF pages as an image format.** The new `RecursiveAI.OneImage.Pdf` package draws PDF pages on OneImage's engine (parsing by [OnePDF](https://www.nuget.org/packages/RecursiveAI.OnePdf)): a page is a frame, drawn in 128 px cells on demand and at the requested size when resized, with JPEG, JPEG 2000 and JBIG2 images decoded by OneImage itself.
- **Progressive JPEG decodes much faster, with identical output.** A 24 MP noisy progressive file takes 68.5 ms instead of 389 (smooth 62.3 instead of 199; 1/8 scale 12.8 instead of 38.5; ImageSharp 432 / 210, NetVips 404 / 257).
- **PSD files keep your style edits.** `PsdFile.Save` writes layer styles and gradient and pattern fills created or edited in the model; gradient and pattern overlays, bevel and emboss and gradient strokes and glows are drawn from the settings.
- **Hinting is hardened.** A hostile font can no longer throw out of the TrueType interpreter.
- Lossless WebP encoding of small images uses less memory, with byte-identical output.

## Learn more

- [Guide](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/docs/guide.md): the lazy model, errors, operations,
  `Canvas`, metadata, color, animation, limits, AOT and trimming.
- [Layers](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/docs/layers.md): layered documents that render on demand, with
  incremental re-rendering and an optional disk-spill store. Photoshop files
  load into them through the separate `RecursiveAI.OneImage.Psd` package
  ([PSD page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/docs/psd.md)).
- [Migrating from ImageSharp and System.Drawing](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/docs/migrating.md)
- [How OneImage compares](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/docs/comparison.md), including where the
  others are the better fit
- [Benchmarks and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/docs/performance.md)
- [Changelog](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/CHANGELOG.md) and
  [security policy](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/SECURITY.md)

## License

Licensed under either of [MIT](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/LICENSE-MIT) or
[Apache-2.0](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.6.0/LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
