# Comparison

How OneImage compares with the four libraries most .NET projects reach
for: ImageSharp, SkiaSharp, Magick.NET and NetVips. It says where
OneImage is the better fit and where one of them is.

**Versions.** Checked on 2026-10-22 against ImageSharp 3.1.12 (the last
release that builds without a license key, and the one used in our
benchmarks), SkiaSharp 4.153.1, Magick.NET-Q8-AnyCPU 15.0.0, and NetVips
3.2.0 with libvips 8.18.7. Details of the other libraries come from their
own documentation and from using them in our tests; they change, so
check theirs before you decide. Performance numbers are on the
[performance page](performance.md).

## What each library is

- **OneImage** is pure managed C# with no dependency beyond the .NET base
  class library. `Image` is a lazy, immutable description of a pipeline,
  computed in 128×128 tiles; `Canvas` is a mutable tiled surface.
  Licensed MIT OR Apache-2.0.
- **ImageSharp** is pure managed C#. It decodes eagerly into an
  `Image<TPixel>` and edits it with `Mutate`. Its license is Six Labors'
  Split License: free for open source and small companies, a paid
  commercial license otherwise.
- **SkiaSharp** wraps Google's Skia graphics library: a 2D drawing
  engine with paths, text and GPU backends, whose image codecs come with
  it. MIT for the wrapper; Skia is BSD-licensed.
- **Magick.NET** wraps ImageMagick: very broad format support and a very
  large operation set. Apache-2.0 for the wrapper; ImageMagick has its
  own permissive license.
- **NetVips** wraps libvips, a demand-driven, streaming image library
  written in C. MIT for the wrapper; libvips is LGPL 2.1 or later,
  shipped as a native binary.

## Like with like

| | OneImage | ImageSharp | SkiaSharp | Magick.NET | NetVips |
|---|---|---|---|---|---|
| Native dependency | none | none | native Skia per platform | native ImageMagick per platform | native libvips per platform |
| Evaluation | lazy, tiled, streaming | eager | eager | eager | lazy, streaming |
| Pixel type in the API | one `Image`, runtime format | `Image<TPixel>` | `SKBitmap` / `SKImage` | `MagickImage` | `Image` |
| Formats we use it for | PNG, JPEG, WebP, GIF, TIFF, BMP, ICO | JPEG, PNG, GIF, BMP, TIFF, WebP and a few more | JPEG, PNG, WebP, GIF, BMP decode, more through Skia | well over a hundred | what your libvips build includes |
| ICC color management | ICC v2/v4 profiles, CMYK to sRGB, four intents | did not convert in our CMYK task | color spaces via Skia | LittleCMS | LittleCMS |
| NativeAOT and trimming | supported, checked in CI | see its documentation | see its documentation | see its documentation | see its documentation |
| Drawing, text, vectors | no | via the separate Drawing and Fonts packages | yes, a drawing engine | yes | limited |
| Thumbnail a large JPEG | decodes at reduced scale, bounded memory | decodes with a target size | scaled decode | `jpeg:size` hint | `thumbnail` shrink-on-load |

Notes on the table:

- **Streaming and memory.** OneImage and NetVips both compute only what
  the output needs. In our thumbnail rows OneImage's peak working memory
  is a few MiB, and NetVips's measured peak is tens of MiB. The numbers
  are on the [performance page](performance.md).
- **ICC.** In our CMYK JPEG task OneImage, NetVips and Magick.NET apply
  the embedded profile, and ImageSharp's `Load` does not convert it, so
  its output is far from the ICC-correct reference. The page shows the
  color error for each library.
- **Native dependencies** mean a package per platform and architecture,
  and a native library in your attack surface and your deployment. They
  also mean decades of optimization and format coverage.

## Where OneImage is the better fit

- You want one package with no native binaries: serverless, containers
  without a libc story, trimmed or NativeAOT apps, or places where
  shipping a native library is a review problem.
- You open files you did not write and want the parsers to be managed,
  bounds-checked code with limits on by default (see
  [limits](guide.md#limits)).
- You want thumbnails that are fast and small in memory, with EXIF
  orientation applied and GPS stripped by default.
- You want a permissive license without a commercial tier.
- You want both one-line batch work and an editable tiled surface
  (`Canvas`) on the same engine.

## Where the others are the better fit

- **NetVips is faster when you can use every core.** It is ahead of
  OneImage on multi-page TIFF reads (about 3× at OneImage's default
  per-call thread limit, about 1.8× with the limit raised to all
  cores), on JPEG encoding (1.58×) and on one PNG encode input. If raw
  throughput on a many-core machine matters more than avoiding a native
  dependency, use NetVips. Its dedicated resize is also ahead of ours
  when downscaling an already decoded image.
- **Magick.NET covers far more formats and operations.** If you need
  HEIC, AVIF, JPEG XL, JPEG 2000, PSD, camera RAW, PDF or SVG input,
  or ImageMagick's long tail of operations, OneImage does not have it.
  OneImage reads and writes PNG, JPEG, WebP, GIF, TIFF, BMP and ICO only.
- **SkiaSharp is a drawing engine.** Paths, text, gradients, GPU
  surfaces and UI integration are Skia's job. OneImage has no text and
  no drawing yet, and no layers beyond a normal-mode `Composite`. SkiaSharp also decodes
  our 50-frame GIF test in less than half the time.
- **ImageSharp has a larger ecosystem of processors and a longer
  record,** and its Drawing and Fonts packages cover shapes, gradients and text, including complex-script shaping and right-to-left layout. If
  your license situation is fine and you rely on those, staying is
  reasonable. The [migration guide](migrating.md) shows the differences
  if you decide to move.
- **You target a framework other than .NET 10.** OneImage runs on
  `net10.0` only. The others support older frameworks.
- **You need ARM64 performance numbers or a long field record.** OneImage
  is checked for correctness on ARM64 but has no published ARM64
  timings, and it is new.

## Not in OneImage yet

Compared with ImageSharp 3.1.12 and its Drawing 2.1.7, Fonts 2.1.3 and Web 3.2.0 packages (the last releases that build without a license key), OneImage does not have yet:

- **Drawing and text:** shapes and paths with boolean operations, fills and strokes (dashes, joins, caps), solid, gradient, pattern and image brushes, clipping to a path, and text (OpenType shaping, bidirectional layout, font fallback, COLR color fonts, text along a path). Coming soon.
- **Effects:** the public integral image (`CalculateIntegralImage`). Coming soon.
- **Formats:** TGA, PBM and QOI. Not planned.

Coming soon means planned, without a date. The [full list](migrating.md#not-in-oneimage-yet) has the exact ImageSharp APIs and what to use in the meantime.

## Choosing

- Thumbnails, crops and conversions for the web, in a service, with no
  native dependencies: OneImage.
- Maximum throughput on many cores and you can ship libvips: NetVips.
- Many formats, or a long tail of operations: Magick.NET.
- Drawing, text, GPU or a UI toolkit: SkiaSharp.
- An existing ImageSharp codebase that works for you: stay, and look at
  the [migration guide](migrating.md) when that changes.
