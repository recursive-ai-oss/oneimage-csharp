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

![Bar chart: how many times faster OneImage is than ImageSharp on each benchmark task, log scale, higher is better. Almost every bar is a win; one task is a tie and one, where ImageSharp skips the ICC color conversion, is slower.](https://raw.githubusercontent.com/recursive-ai-oss/oneimage-csharp/v1.8.1/docs/img/oneimage-hero-light.svg)

[Full results, with memory, the losses and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/docs/performance.md)

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

## What is new in 1.8.1

- **Color to gray is about 5x faster, with identical output.** Reading an 8-bit RGB or RGBA image as `Gray8`, `Gray16` or gray with alpha uses an integer vector path that gives exactly the bytes of the previous float path (checked over every RGB value and every alpha). An A4 JPEG read as `Gray8` takes 11.5 ms instead of 58 ms (10 ms as `Rgb24`).

## What was new in 1.8.0

- **Repeated region reads no longer decode again.** From the second `CopyPixelsTo` of a sub-rectangle of a large JPEG or PNG, the rows read so far are kept in a bounded per-image plane in the requested format, so further band and region reads are copies. Load plus 20 random region reads of an A4 page: JPEG 47 to 26 ms, PNG 94 to 42 ms, with peak memory 31 to 10 MB above baseline. `ImageEngine.RegionCacheBytes` (64 MiB per frame, 0 turns it off) bounds it and `Image.ReleaseDecodedPixels()` gives it back.
- **The first `Load` costs less memory.** The first PNG load in a process adds 2.3 MB instead of 11 MB, and the first JPEG load 2.5 MB instead of 6.1 MB.
- Output is byte for byte the same as 1.7.0.

## Learn more

- [Guide](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/docs/guide.md): the lazy model, errors, operations,
  `Canvas`, metadata, color, animation, limits, AOT and trimming.
- [Layers](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/docs/layers.md): layered documents that render on demand, with
  incremental re-rendering and an optional disk-spill store. Photoshop files
  load into them through the separate `RecursiveAI.OneImage.Psd` package
  ([PSD page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/docs/psd.md)).
- [Migrating from ImageSharp and System.Drawing](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/docs/migrating.md)
- [How OneImage compares](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/docs/comparison.md), including where the
  others are the better fit
- [Benchmarks and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/docs/performance.md)
- [Changelog](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/CHANGELOG.md) and
  [security policy](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/SECURITY.md)

## License

Licensed under either of [MIT](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/LICENSE-MIT) or
[Apache-2.0](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.1/LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
