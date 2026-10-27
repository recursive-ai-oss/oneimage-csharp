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

![Bar chart: how many times faster OneImage is than ImageSharp on each benchmark task, log scale, higher is better. Almost every bar is a win; one task is a tie and one, where ImageSharp skips the ICC color conversion, is slower.](https://raw.githubusercontent.com/recursive-ai-oss/oneimage-csharp/v1.10.2/docs/img/oneimage-hero-light.svg)

[Full results, with memory, the losses and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/docs/performance.md)

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

## What is new in 1.10.2

- **Fixed: a blur much wider than the image could crash the process.** `GaussianBlur`, `BoxBlur` and `Sharpen` on an image only a few pixels across compared with sigma or radius recursed until the stack overflowed. They now blur such images directly with the documented kernel, and any finite sigma is accepted. Every blur that finished before gives the same bytes.

## What was new in 1.10.1

- **Fixed: valid PNG files could fail to decode** (every release since 1.0.0). The deflate decoder could go out of step on compressed data that uses long codes, which some optimizing encoders produce, and the read then failed with a format error. TIFF Deflate, PSD ZIP, SVGZ and WOFF use the same decoder. In tests, whole-image reads of affected files always failed rather than returning wrong pixels. A crop, region or band read could, rarely, return wrong pixels in its last rows. See the changelog for the measurements.
- Complete reads of deflate data now verify the checksum when it is present: a wrong checksum is an error, while a missing or cut-short one after complete data is still accepted. Reads of part of a PNG cannot verify it and are covered by the chunk CRCs only.
- Fixed: an image loaded from a `Stream` could have its compressed bytes recycled while a decode was still reading them.

## What was new in 1.10.0

- **Text builds on [OneFont](https://github.com/recursive-ai-oss/onefont-csharp).** `RecursiveAI.OneImage.Text` now uses `RecursiveAI.OneFont` 1.2 or later for font parsing, OpenType shaping, bidi and line breaking instead of its own copy. Rendering is byte for byte the same, measuring a paragraph allocates about 40% less, and the public API is unchanged.
- Fixed: `TextShaping.ShapeRun` clusters no longer leave gaps when the shaper drops characters, and a character mapped past the font's glyph count is drawn as the missing glyph.

## What was new in 1.9.0

- **Give back decoded pixels per frame.** `ImageFrame.ReleaseDecodedPixels()` is public, and releasing now also clears the cached tiles of operations above the decode and of TIFF decodes, and reaches the region plane through `frame.ToImage()`.
- **Region reads without the plane, per call.** `CopyPixelsTo(destination, rect, useRegionCache: false)` reads a band or region of a large JPEG or PNG holding only the decoder's few tile rows, without changing the process-wide `ImageEngine.RegionCacheBytes`.
- Output is byte for byte the same as 1.8.1.

## What was new in 1.8.1

- **Color to gray is about 5x faster, with identical output.** Reading an 8-bit RGB or RGBA image as `Gray8`, `Gray16` or gray with alpha uses an integer vector path that gives exactly the bytes of the previous float path (checked over every RGB value and every alpha). An A4 JPEG read as `Gray8` takes 11.5 ms instead of 58 ms (10 ms as `Rgb24`).

## What was new in 1.8.0

- **Repeated region reads no longer decode again.** From the second `CopyPixelsTo` of a sub-rectangle of a large JPEG or PNG, the rows read so far are kept in a bounded per-image plane in the requested format, so further band and region reads are copies. Load plus 20 random region reads of an A4 page: JPEG 47 to 26 ms, PNG 94 to 42 ms, with peak memory 31 to 10 MB above baseline. `ImageEngine.RegionCacheBytes` (64 MiB per frame, 0 turns it off) bounds it and `Image.ReleaseDecodedPixels()` gives it back.
- **The first `Load` costs less memory.** The first PNG load in a process adds 2.3 MB instead of 11 MB, and the first JPEG load 2.5 MB instead of 6.1 MB.
- Output is byte for byte the same as 1.7.0.

## Learn more

- [Guide](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/docs/guide.md): the lazy model, errors, operations,
  `Canvas`, metadata, color, animation, limits, AOT and trimming.
- [Layers](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/docs/layers.md): layered documents that render on demand, with
  incremental re-rendering and an optional disk-spill store. Photoshop files
  load into them through the separate `RecursiveAI.OneImage.Psd` package
  ([PSD page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/docs/psd.md)).
- [Migrating from ImageSharp and System.Drawing](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/docs/migrating.md)
- [How OneImage compares](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/docs/comparison.md), including where the
  others are the better fit
- [Benchmarks and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/docs/performance.md)
- [Changelog](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/CHANGELOG.md) and
  [security policy](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/SECURITY.md)

## License

Licensed under either of [MIT](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/LICENSE-MIT) or
[Apache-2.0](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.10.2/LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
