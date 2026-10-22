# OneImage

**A pure-managed .NET image library: one-line thumbnails, crops and
conversions, and lazy, tiled, streaming load, edit and render on the
same engine.** PNG, JPEG, WebP, GIF, TIFF, BMP and ICO, with ICC color
management. Pure C# with no dependency beyond .NET itself; untrusted
files are read through bounds-checked managed code.

How many times faster OneImage is than ImageSharp 3.1.12 on each
benchmark task (log scale, higher is better; slower tasks and ties are
marked):

![Bar chart: how many times faster OneImage is than ImageSharp on each benchmark task, log scale, higher is better. Almost every bar is a win; one task is a tie and one, where ImageSharp skips the ICC color conversion, is slower.](https://raw.githubusercontent.com/recursive-ai-oss/oneimage-csharp/v1.0.0/docs/img/oneimage-hero-light.svg)

[Full results, with memory, the losses and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.0.0/docs/performance.md)

## Install

```sh
dotnet add package OneImage
```

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

## Learn more

- [Guide](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.0.0/docs/guide.md): the lazy model, errors, operations,
  `Canvas`, metadata, color, animation, limits, AOT and trimming.
- [Migrating from ImageSharp and System.Drawing](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.0.0/docs/migrating.md)
- [How OneImage compares](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.0.0/docs/comparison.md), including where the
  others are the better fit
- [Benchmarks and the method](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.0.0/docs/performance.md)
- [Changelog](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/CHANGELOG.md) and
  [security policy](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/SECURITY.md)

## License

Licensed under either of [MIT](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/LICENSE-MIT) or
[Apache-2.0](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
