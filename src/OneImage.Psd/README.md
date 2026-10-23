# OneImage.Psd

Photoshop PSD and PSB support for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp): read the merged composite as an image, or load the whole layer tree into a OneImage layered document, edit it and save it again. Pure C# with no dependency beyond OneImage and .NET.

```sh
dotnet add package RecursiveAI.OneImage.Psd
```

```csharp
using OneImage;
using OneImage.Psd;

// The merged composite, like any other format (register the format first; it is not in the default configuration).
var load = new LoadOptions { Configuration = new ImageConfiguration().Add(PsdFormat.Instance) };
Image.Load("poster.psd", load).Resize(width: 800).Save("poster.webp");

// The layers.
using PsdFile file = PsdFile.Load("poster.psd");
foreach (var layer in file.Document.Root.Children)
    Console.WriteLine($"{layer.Name} {layer.BlendMode} {file.GetInfo(layer).Kind}");
file.Document.Render().Save("flattened.png");
file.Save("edited.psd");
```

## What is covered

- **Refused:** multichannel documents (a typed `ImageFormatException`). Bitmap, gray, duotone, indexed, RGB, CMYK and Lab at 8, 16 and 32 bits load; CMYK and Lab are converted to RGB through the color engine.
- **Modelled and round-tripped:** pixel layers with offsets, groups, blend modes, opacity, fill opacity, visibility, clipping, lock flags, raster and vector masks (a vector mask becomes a raster mask), solid color fills, and the levels, curves, hue/saturation, brightness/contrast, exposure, invert, threshold and posterize adjustments.
- **Kept only as stored pixels plus untouched blocks:** text, smart objects, layer effects and styles, gradient and pattern fills and vector strokes. They render from the pixels Photoshop stored, are not redrawn, and their blocks are written back unchanged.
- **Known gaps against Photoshop:** posterize, curves and legacy brightness/contrast follow Photoshop (buckets, a natural cubic spline, the legacy formula), while brightness/contrast in Photoshop's current mode is a fitted approximation; hard mix and vivid light differ slightly; vector-mask path operations use the non-zero rule only; levels, curves, hue/saturation, brightness/contrast and exposure layers in CMYK and Lab documents are kept but not applied; dissolve and darker/lighter color render as Normal, Darken and Lighten.

The [PSD page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.4.0/docs/psd.md) has the full mapping, the fidelity figures, the limits and the speed. Layered documents are described in the [layers page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.4.0/docs/layers.md).

## License

Licensed under either of [MIT](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.4.0/LICENSE-MIT) or [Apache-2.0](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.4.0/LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
