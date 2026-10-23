# PSD and PSB

`RecursiveAI.OneImage.Psd` (namespace `OneImage.Psd`) reads and writes Photoshop documents. It has two entry points on one parser:

- **The merged image** is an ordinary `Image` format. `PsdFormat` is registered in an `ImageConfiguration` (it is not in the default configuration, because the default cannot reference a separate package), and `Image.Load`, `Identify`, `Thumbnail` and the rest then work on `.psd` and `.psb` like any other file. Saving an `Image` to PSD writes a flat document.
- **The layers**: `PsdFile.Load` returns a `PsdFile` whose `Document` is an `OneImage.Layers.Document` (pixel, group, fill and adjustment layers with masks, blend modes and clipping). `PsdFile.Save` writes it back. Everything the document model cannot express stays in the file model as opaque blocks and is written back unchanged.

```csharp
var config = new ImageConfiguration().Add(PsdFormat.Instance);
Image.Load("poster.psd", new LoadOptions { Configuration = config }).Resize(width: 800).Save("poster.webp");

using PsdFile file = PsdFile.Load("poster.psd");
file.Document.Render().Save("flattened.png");
file.Save("edited.psd");                       // PSB when the document is wider or taller than 30,000 px
```

## What is covered

**Multichannel documents are refused** with a typed error, both as a merged image and through `PsdFile.Load`. Every other color mode (bitmap, gray, duotone, indexed, RGB, CMYK, Lab) loads.

**Modelled, and round-tripped through `PsdFile.Save`:** pixel layers with their offsets, groups (pass-through or isolated), the blend modes the layer model has, opacity, fill opacity, visibility, clipping to the layer below, lock flags, raster masks (area, default value, density, feather, disabled flag), vector masks (rasterized to a raster mask), solid color fill layers, gradient and pattern fill layers (drawn from their settings, see [Gradient and pattern fills](#gradient-and-pattern-fills)), and the adjustment layers levels, curves, hue/saturation, brightness/contrast, exposure, invert, threshold and posterize. These render with the library's own engine.

**Kept only as their stored pixels plus untouched data blocks:** text layers, smart objects, layer effects and styles (`lfx2`, `lrFX`), vector strokes, and gradient or pattern fills that cannot be drawn (the fallbacks listed under [Gradient and pattern fills](#gradient-and-pattern-fills)). They render from the raster Photoshop stored with the layer, are not redrawn and cannot be edited here, and their blocks are written back unchanged. Dissolve and the darker/lighter color blend modes are also kept as keys but render as Normal, Darken and Lighten.

**Known fidelity gaps** against Photoshop: brightness/contrast in Photoshop's current (non-legacy) mode is a fitted approximation (see below); hard mix and vivid light differ slightly; vector-mask path operations (combine, subtract, intersect) use the non-zero rule only; levels, curves, hue/saturation, brightness/contrast and exposure layers in CMYK and Lab documents are kept but not applied; the base layer's fill opacity in a clipping group applies to the whole group. Figures are in [Fidelity of the layer render](#fidelity-of-the-layer-render).

## Reading the merged image

The merged image is read in the file's own depth: gray, RGB and indexed at 8, 16 and 32 bits (indexed and bitmap deliver 8-bit RGB and gray), bitmap, duotone as gray, CMYK and Lab converted to RGB through the color engine (8-bit, Lab 16-bit stays 16-bit). Raw, run-length, ZIP and ZIP-with-prediction channels are read, 8, 16 and 32 bits. Multichannel documents are refused with a typed error.

- A composite with transparency is stored by Photoshop blended with white. By default `PsdFormat` removes the white matte so the result is straight alpha; `new PsdFormat(unmatte: false)` returns the stored samples.
- A file without a composite (or with a stub of raw data too short for the image) is rendered from its layers instead.
- The composite is bit-exact on the hand-built files of the test suite for every depth, mode and compression. Against ImageMagick's reader on the corpus below, 215 of 313 files are identical; the others are mostly files with transparency (ImageMagick keeps the matte), CMYK and Lab (different color conversion), and 16/32-bit (a Q8 build quantizes).

## Layers: what maps to what

| In the file | In the document | Notes |
|---|---|---|
| Pixel layer | `PixelLayer` at its offset | Layer bounds are kept; an empty or inverted rectangle is an empty layer. |
| Group (open or closed folder) | `GroupLayer` | Pass-through (`pass`) or isolated with its own blend mode; open/closed state is kept in `PsdLayerInfo.GroupOpen`. Nesting is capped by `PsdLimits.MaxNesting`. |
| Blend modes | `BlendMode` | All the Photoshop modes the model has. Dissolve and darker/lighter color have no equivalent: the key is kept and written back, and rendering uses Normal, Darken and Lighten. |
| Opacity, fill opacity (`iOpa`), visibility, clipping | `Opacity`, `FillOpacity`, `Visible`, `ClipToBelow` | Opacity is stored in 8 bits, so values are quantized to 1/255 on write. The hidden flag follows real files (set means hidden). |
| Locks (`lspf`, flags) | `Locks` | |
| Layer id, unicode name | `PsdLayerInfo.LayerId`, `Layer.Name` | The Pascal name is written in Latin-1 with `?` for other characters; `luni` carries the real name. |
| Raster mask | `LayerMask` | Area, default (outside) value, density, feather, disabled flag. Mask parameters are read in either position Photoshop uses. |
| Vector mask (`vmsk`/`vsms`) | A raster mask | The path is rasterized with the non-zero rule at the layer's size. With both kinds, the two are multiplied. The written mask carries the flag that says it already includes the path. |
| Solid color fill (`SoCo`) | `FillLayer` | Written as a document-size layer plus `SoCo`. |
| Levels, curves, hue/saturation, brightness/contrast, exposure, invert, threshold, posterize | `AdjustmentLayer` | Per-channel levels and curves apply before the master set. Posterize, curves and brightness/contrast use Photoshop's formulas, described under [Adjustment formulas](#adjustment-formulas). Levels, curves, hue/saturation, brightness/contrast and exposure layers of CMYK and Lab documents are kept as placeholders (the pixels were converted to RGB, and these act on ink or Lab channels), and written as an identity levels layer, since their parameters mean nothing in an RGB file. Invert, threshold and posterize apply as usual. |
| Gradient and pattern fills (`GdFl`, `PtFl`), shape layers with such fills | `FillLayer` with a gradient or pattern brush | Drawn from the settings; the original block is kept and written back. Fills outside the supported subset, or read with `PsdReadOptions.DrawFills = false`, are pixel layers of the stored pixels. |
| Text, smart objects, layer effects (`lfx2`, `lrFX`), strokes (`vstk`) | Pixel layer + kept blocks | Drawn from the stored pixels only; the blocks are written back untouched. |
| Image resources, ICC profile, document-level blocks | `PsdFile.Resources`, `SourceProfile`, `DocumentBlocks` | Kept and written back. Document-level odd-length blocks keep their unpadded length so they write back byte for byte. |
| Layer info of 16 and 32-bit files | `Lr16` / `Lr32` blocks | Written like Photoshop does. |

Not stored in the file: the document `Background` (it is a render-time color, so a merged image written with a white background reads back as layers over transparency), and anything the layer model does not have (layer styles, smart-object contents, vector strokes as editable data).

Defaults of the model: 8-bit gray files load as `GrayAlpha8` documents, other files as RGB (`Rgba8`, `Rgba16`, `RgbaF32` by depth), CMYK always as 8-bit RGB. Colors of CMYK/Lab layers go through the color engine, so layers of CMYK files are not bit-for-bit the file's samples.

## Writing

`PsdFile.Save` writes layers with run-length coding (default) or raw channels, 8, 16 and 32 bits, a merged image rendered from the layers (with an alpha channel when the background is not opaque), the sRGB profile, and a PSB when the document is wider or taller than 30,000 px or `PsdWriteOptions.Large` says so (a PSB holds up to 300,000 px). A layer with no PSD equivalent (an image brush fill layer, a custom adjustment, a gradient or pattern fill whose brush was replaced) throws `ImageOperationException` instead of being dropped. A non-seekable stream is written through a temporary file.

Writing the same loaded file twice gives the same bytes, and a document read back from a file renders to the same pixels as the document that was written (opacities are quantized to 8 bits first).

## Adjustment formulas

These three were derived from the composites Photoshop stored in the sample files (each file applies one setting to a strip of a photograph and a color ramp, so the stored composite against our render of the layers below gives the transfer function exactly), not from any other implementation. The public `Image` methods keep their defaults; the formulas are options (`PosterizeMode.Buckets`, `CurveInterpolation.Natural`, `BrightnessContrastMode.PhotoshopLegacy` / `PhotoshopSmooth`) that the PSD reader selects.

- **Posterize** cuts the range into `levels` equal bins and rounds each bin down. On 8-bit samples a value `v` goes to bin `k = floor(v * levels / 256)` and the output is `floor(k * 255 / (levels - 1))`, so 3 levels give 0, 127, 255 with the edges at 86 and 171, and 13 levels give 0, 21, 42, 63, 85 and so on (rounding down, not to nearest). 16-bit and float samples use `floor(v * levels)` and `k / (levels - 1)`. Exact against the stored 8-bit files (0 error); 16-bit files differ by about 0.1-0.3 levels in the mean, at bin edges.
- **Curves** interpolate the control points with a natural cubic spline (zero curvature at both ends) in 0..255 coordinates, flat before the first and after the last point, with the result clamped; the per-channel curves act first and the composite curve after them. Points that are not in output order give a non-monotone curve, as in Photoshop. Mean error against the stored composites is under 0.01 levels (maximum 1). The public `Curves` keeps its monotone interpolation by default.
- **Brightness/contrast, legacy mode** (a `brit` block without a `CgEd` descriptor, or a descriptor with `useLegacy` set): with brightness `b` in levels (-150..150) and contrast `c` (-100..100), `c8 = trunc(c * 2.55)`; for `c >= 0` the slope is `k = 255 / (255 - c8)` and `out = k * (v + b - 127) + 127`; for `c < 0` it is `k = (255 + c8) / 255` and `out = k * (v - 127) + 127 + b`; results are rounded and clamped. Exact on the sample files except one level at a few values.
- **Brightness/contrast, current mode** (Photoshop CS3 and later keep the real settings in a `CgEd` descriptor next to a zeroed `brit` block; older readers saw no adjustment at all). Photoshop bends the tone curve instead of shifting it, and the exact curve is not documented. `PhotoshopSmooth` is an approximation fitted to four stored settings: a quadratic bow `x + 1.7 * (b / 150) * x * (1 - x)` followed by a soft contrast slope about 0.5 (the legacy slope to the power 0.2). It brings the sample files from about 37 levels of mean error to about 8; individual layers range from 5 to about 25 levels. Treat it as an approximation.

A model-built adjustment written to a PSD file always uses the file's formulas: a posterize layer is written as `post` (buckets), a curve as `curv` (read back as a natural spline) and brightness/contrast as the legacy `brit` block, so build documents meant for PSD with the matching mode. A loaded layer is written back from its own stored block, byte for byte.

## Gradient and pattern fills

Fill layers (`GdFl`, `PtFl`) and shape layers filled with a gradient or pattern are drawn by the library as `FillLayer`s with a gradient or pattern brush, so they scale, move and composite like any layer. `PsdReadOptions.DrawFills` (default true) turns this off: the layers then load as pixel layers of the stored raster, as before. An unchanged fill is written back as its original block plus the brush rendered over the document; replacing the brush with anything but a solid color throws on save.

The rules were derived from the rasters and composites Photoshop stored in the sample files, not from any other implementation.

- **Gradients:** linear, radial, angle, reflected and diamond styles with angle, scale, offset (percent of the box), reverse, color and opacity stops, midpoints and smoothness. Position is evaluated at integer pixel coordinates relative to the box center. The gradient line is the chord through the box center at the angle, of length `scale * min(w / |cos|, h / |sin|)`; radial, reflected and diamond use half of it as the radius, and the angle style starts at the angle and runs clockwise. Between stops the position is bent by the midpoint, then eased by `smoothness` toward a smoothstep. Colors interpolate in encoded values, or in linear light when the fill says so. Stop colors in RGB, gray, CMYK and Lab are converted through the color engine.
- **Patterns:** tiles come from the document's pattern resources (`Patt`, `Pat2`, `Pat3`; RGB or gray, 8 or 16 bits, raw or run-length coded). The tile origin is the document origin for fill layers and one pixel right of the box corner for shape layers.
- **Box:** shape layers use the vector path bounds; fill layers use the layer rectangle, else the raster mask rectangle, else the document; "align with layer" off means the document.
- **Stored colors win:** where Photoshop wrote a raster for the layer, the drawn fill takes its colors from it and keeps its own alpha, so the common case matches Photoshop exactly and the drawing only supplies what was not stored (an enlarged or edited layer).
- **Falls back to the stored pixels** for noise gradients, perceptual interpolation, stop colors that are foreground/background or HSB, boxes with a side under 16 px, patterns with a scale other than 100%, an angle or a phase, missing or unsupported patterns (not RGB or gray, or with alpha), and descriptors that are truncated or out of range. Photoshop snaps gradient direction and length to whole pixels; that is not modelled.

Against the stored rasters without the stored-color rule, patterns are exact, gradients typically differ by 0.1 to 1.2 levels in the mean (worst case 6.8, a 60 degree reflected gradient on 32 px), and gray and CMYK fills by 3 to 6 levels. Midpoint ownership with three or more stops is checked on one file only.

## Fidelity of the layer render

Rendering a loaded layer tree and comparing with the file's own stored composite (corpus of 316 PSD/PSB test files from the psd-tools project, MIT licensed, fetched by `tools/fetch-psd-tests.sh`, never committed): 315 load; the one that does not is a multichannel file. Of the 152 files with layers and no feature we do not draw (the set before fills were drawn), the median mean error is 0.134 levels and is unchanged with fills drawn; 160 files are fully modelled now (the 8 added are fill files: gradient-fill went from 8.3 to 0.0 levels, vector-mask2 from 71.9 to 1.0), with a median of 0.108 (106 files at or under 0.5, 115 at or under 2, 128 at or under 10; it was 0.275 before the Photoshop adjustment formulas). Over all 315 loadable files the median went from 0.353 to 0.276. The 19 files with posterize, curves or brightness/contrast layers (or the adjustment compositions that use them) have a median of 0.005 (was 10.5): posterize, curves and legacy brightness/contrast layers are now exact or within a level. The rest differ for known reasons:

- stored composites that are stale or are Photoshop's "not saved with a composite" placeholder (`layers-minimal/*`, `masks/2.psd`, `note.psd`);
- text, smart objects, layer effects and strokes, which are not drawn (gradient and pattern fills outside the supported subset also render from stored pixels);
- brightness/contrast in Photoshop's current mode is approximated (mean error 8 levels on the sample files); hard-mix and vivid-light differ slightly;
- path operations on vector masks (combine, subtract, intersect) use the non-zero rule only;
- adjustments in CMYK and Lab documents.

## Limits and safety

Everything is parsed in safe code. `PsdLimits` bounds layers (20,000), group nesting (64), one block (64 MiB), all blocks (256 MiB), decoded channel bytes in total (16 GiB) and per channel (1 GiB), and descriptor depth (32) and items (65,536); `ImageLimits` bounds pixels, dimensions and the expansion ratio of ZIP channels. Every size is checked against the header before anything is allocated, nesting is followed with explicit stacks, and a 300,000 x 300,000 layer rectangle in a few hundred bytes of file fails on its header. `tests/OneImage.Psd.Tests/HostileFileTests.cs` has a crafted file for each limit; the `psd` fuzz target (see `fuzz/README.md`) ran 10 minutes (61,770 executions) clean.

## Speed

A 4000 x 3000 PSD with three full-size layers (run-length coded, 36 MB), BenchmarkDotNet short job, .NET 10.0.13, AMD Ryzen 9 7950X3D (32 threads) shared with other jobs (load average 4.9 before the run, 9.9 after), Magick.NET Q8 as the competitor (ImageSharp has no PSD reader). Our default per-call degree of 4 applies.

| Task | Ours | Magick.NET | Allocated (ours / Magick) |
|---|---|---|---|
| Merged image to `Rgba32` | 42 ms | 79 ms | 2.6 MB / 3 KB (managed only) |
| All layers loaded and flattened | 233 ms | 651 ms (decodes the layers, does not composite them) | 132 MB / 8 KB (managed only) |

The loaded host makes the absolute numbers noisy (the error column of the merged row is 26 ms); the ratios held across the 5 iterations. Magick's allocation is native memory and is not counted. Run with `dotnet run -c Release --project bench/OneImage.Benchmarks -- --filter '*PsdReadBenchmarks*'`.
