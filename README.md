# OneImage

**A pure-managed .NET image library: one-line thumbnails, crops and
conversions, and lazy, tiled, streaming load, edit and render on the
same engine.** PNG, JPEG, WebP, GIF, TIFF, BMP and ICO, with ICC color
management. Pure C# with no dependency beyond .NET itself; untrusted
files are read through bounds-checked managed code.

[![NuGet](https://img.shields.io/nuget/v/RecursiveAI.OneImage)](https://www.nuget.org/packages/RecursiveAI.OneImage)
[![CI](https://github.com/recursive-ai-oss/oneimage-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/recursive-ai-oss/oneimage-csharp/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](#install)
[![License](https://img.shields.io/badge/license-MIT%20OR%20Apache--2.0-blue)](#license)

How many times faster OneImage is than ImageSharp 3.1.12 on each
benchmark task (wall-clock time, log scale, higher is better; a task
where OneImage is slower shows as a bar to the left, a tie in grey):

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/recursive-ai-oss/oneimage-csharp/main/docs/img/oneimage-hero-dark.svg">
  <img alt="Bar chart: how many times faster OneImage is than ImageSharp on each benchmark task, log scale, higher is better. Almost every bar is a win; one task is a tie and one, where ImageSharp skips the ICC color conversion, is slower." src="https://raw.githubusercontent.com/recursive-ai-oss/oneimage-csharp/main/docs/img/oneimage-hero-light.svg">
</picture>

[Full results, with memory, the losses and the method →](docs/performance.md)

## Why OneImage

- **Lazy, tiled and streaming.** `Resize`, `Crop` and the other
  operations only record what to do. Pixels are computed tile by tile
  when you save, so a 24 MP photo becomes a thumbnail without being held
  at full size, and JPEGs decode at 1/2, 1/4 or 1/8 scale when that is
  enough ([guide](docs/guide.md#the-lazy-model)).
- **One-liners and an editing surface.** `Image.Thumbnail(...)` for the
  common job; an immutable `Image` pipeline and a mutable tiled `Canvas`
  with O(1) snapshots when you need to edit pixels
  ([guide](docs/guide.md#pixel-access-and-canvas)).
- **Color and metadata done right.** ICC v2/v4 profiles, CMYK to sRGB,
  resampling in linear light, EXIF orientation applied for you, and
  thumbnails that strip GPS by default
  ([color](docs/guide.md#color), [metadata](docs/guide.md#metadata)).
- **Safe on files you did not write.** Parsers and decoders are
  bounds-checked C#; the only `unsafe` code is in SIMD kernels that run
  on already-validated buffers. Size limits are on by default and
  checked from headers before anything large is allocated; every decoder
  is fuzzed ([limits](docs/guide.md#limits),
  [fuzzing](fuzz/README.md)). NativeAOT and trimming are supported.
- **Fast, with the losses shown.** Faster than ImageSharp on every
  benchmark task except one tie and one CMYK task where ImageSharp skips
  the ICC conversion, and with less peak memory on most. Not on all: a
  JPEG thumbnail uses about 1 MiB more peak memory than ImageSharp, and
  NetVips is ahead on multi-page TIFF reads (about 3× at the default
  thread limit), on JPEG encoding and on one of the PNG encode inputs
  ([every loss](docs/performance.md#results-at-a-glance)).

[How OneImage compares](docs/comparison.md) with ImageSharp, SkiaSharp,
Magick.NET and NetVips, including when one of them is the better
choice.

## Install

```sh
dotnet add package RecursiveAI.OneImage
```

The package is `RecursiveAI.OneImage`; the namespace is `OneImage`
(`using OneImage;`).

Target framework `net10.0`. Moving from ImageSharp or System.Drawing?
See the [migration guide](docs/migrating.md).

## Quick start

<!-- sample: QuickStart -->
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
writers, name it: `image.Save(stream, PngFormat.Instance)`. `Image`
is lazy and immutable; read the [guide](docs/guide.md) next for when
errors appear, how sources are held, limits and threading.

## Where to go

- **Using it:** the [guide](docs/guide.md) (lazy model, errors,
  operations, `Canvas`, metadata, color, animation, limits, AOT and
  trimming, formats). The API reference is the XML documentation your
  IDE shows.
- **Migrating:** [from ImageSharp and System.Drawing](docs/migrating.md).
- **Serving images from ASP.NET Core:** [OneImage.Web](docs/web.md), URL-driven
  resizing with a disk cache, signed URLs and request limits.
- **Drawing and text:** [vector drawing](docs/drawing.md) (paths, fills,
  strokes, brushes, boolean operations, clipping) is in the core package;
  [OneImage.Text](docs/text.md) adds fonts, OpenType shaping, line
  breaking, rich text and color glyphs.
- **Layered documents:** [layers](docs/layers.md) (`OneImage.Layers`: pixel,
  group, fill and adjustment layers, masks, clipping, blend modes, render on
  demand with incremental re-rendering, and an optional disk-spill store) is in
  the core package.
- **Photoshop files:** [OneImage.Psd](docs/psd.md) (`RecursiveAI.OneImage.Psd`)
  reads PSD and PSB as an image format (the merged composite) and loads the
  layer tree into a [layers](docs/layers.md) document, edits and writes it
  back. Photoshop's posterize, curves and brightness/contrast formulas are
  used, gradient and pattern fills are drawn from their settings, and common
  layer effects (shadows, glows, strokes, overlays, satin) are drawn; text,
  smart objects and the other effects are kept as their stored pixels plus
  untouched blocks, and multichannel documents are refused. Edited layer
  styles and fills are written back; the page lists what is modelled and the
  known limitations.
- **Reading SVG:** [OneImage.Svg](docs/svg.md) (`RecursiveAI.OneImage.Svg`)
  registers SVG and `.svgz` as an image format that is drawn at the size you
  ask for; it depends on the core package and OneImage.Text.
- **Choosing a library:** [how OneImage compares](docs/comparison.md),
  including where the others win.
- **Evidence:** [benchmark tables](docs/performance.md#results-at-a-glance)
  (the numbers behind the chart, every loss and the method),
  [fuzzing](fuzz/README.md).
- **What changed:** [CHANGELOG.md](CHANGELOG.md).
- **Contributing and security:** [CONTRIBUTING.md](CONTRIBUTING.md),
  [SECURITY.md](SECURITY.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## License

Licensed under either of [MIT](LICENSE-MIT) or
[Apache-2.0](LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
