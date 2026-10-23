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

## What is new in 1.5.0

- Posterize, curves and brightness/contrast layers use Photoshop's own formulas (brightness/contrast in the current mode is a fitted approximation). Against the stored composites of 19 sample files the median mean error went from 10.5 to 0.005 levels.
- Gradient and pattern fill layers (and shape layers filled with them) are drawn from their settings as fill layers; `PsdReadOptions.DrawFills = false` keeps the stored pixels. 160 sample files are now fully modelled (median error 0.108 levels).
- Layer effects (drop and inner shadows, outer and inner glows, solid strokes, color overlays, satin) are read into `Layer.Style` and drawn. The stroke sample files went from 15-40 levels of error to 0.00-0.16.
- A hue/saturation adjustment with an out-of-range value is kept instead of failing to load.

## Known limitations

- **Edited layer styles are written from the model;** effect items the model does not carry (noise and perceptual gradients, the bevel contour and texture, unknown keys) are kept from the file, not edited. Written files are checked with independent readers, not with Photoshop itself.
- **Fills with no PSD equivalent are written as pixels** (anything but linear or radial gradients and bitmap patterns or hatches, and fills in gray or 32-bit documents).
- Bevel and emboss and the placement of aligned patterns are approximations of Photoshop (a few levels off on the sample files).
- Text, smart objects, vector strokes and the effects that are not drawn render from their stored pixels.

## What is covered

- **Refused:** multichannel documents (a typed `ImageFormatException`). Bitmap, gray, duotone, indexed, RGB, CMYK and Lab at 8, 16 and 32 bits load; CMYK and Lab are converted to RGB through the color engine.
- **Modelled and round-tripped:** pixel layers with offsets, groups, blend modes, opacity, fill opacity, visibility, clipping, lock flags, raster and vector masks (a vector mask becomes a raster mask), solid color fills, gradient and pattern fills (drawn from their settings; `PsdReadOptions.DrawFills = false` keeps the stored pixels; a new linear or radial gradient, bitmap pattern or hatch brush is written as fill settings, and any other brush as pixels), and the levels, curves, hue/saturation, brightness/contrast, exposure, invert, threshold and posterize adjustments.
- **Layer effects:** drop and inner shadows, outer and inner glows, strokes (solid, gradient or pattern), color, gradient and pattern overlays, bevel and emboss, gradient glows and satin are read into `Layer.Style` and drawn; what is not reproduced (noise gradients, dissolve mode) is counted in `PsdLayerInfo.EffectsNotDrawn`. An untouched style is written back byte for byte; a style you create or edit is written as a new effects block that keeps the items the model has no field for, and reading it back gives the same effects.
- **Kept only as stored pixels plus untouched blocks:** text, smart objects, vector strokes, the layer effects that are not drawn, and gradient or pattern fills outside the drawn subset. They render from the pixels Photoshop stored, are not redrawn, and their blocks are written back unchanged.
- **Known gaps against Photoshop:** posterize, curves and legacy brightness/contrast follow Photoshop (buckets, a natural cubic spline, the legacy formula), while brightness/contrast in Photoshop's current mode is a fitted approximation; hard mix and vivid light differ slightly; vector-mask path operations use the non-zero rule only; levels, curves, hue/saturation, brightness/contrast and exposure layers in CMYK and Lab documents are kept but not applied; dissolve and darker/lighter color render as Normal, Darken and Lighten.

The [PSD page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/docs/psd.md) has the full mapping, the fidelity figures, the limits and the speed. Layered documents are described in the [layers page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/docs/layers.md).

## License

Licensed under either of [MIT](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/LICENSE-MIT) or [Apache-2.0](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.5.0/LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
