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

**Modelled, and round-tripped through `PsdFile.Save`:** pixel layers with their offsets, groups (pass-through or isolated), the blend modes the layer model has, opacity, fill opacity, visibility, clipping to the layer below, lock flags, raster masks (area, default value, density, feather, disabled flag), vector masks (rasterized to a raster mask), solid color fill layers, and the adjustment layers levels, curves, hue/saturation, brightness/contrast, exposure, invert, threshold and posterize. These render with the library's own engine.

**Kept only as their stored pixels plus untouched data blocks:** text layers, smart objects, layer effects and styles (`lfx2`, `lrFX`), gradient and pattern fills (including shape layers filled with them) and vector strokes. They render from the raster Photoshop stored with the layer, are not redrawn and cannot be edited here, and their blocks are written back unchanged. Dissolve and the darker/lighter color blend modes are also kept as keys but render as Normal, Darken and Lighten.

**Known fidelity gaps** against Photoshop: posterize rounds to nearest instead of to buckets; curves use a different spline; brightness/contrast scaling is approximate; hard mix and vivid light differ slightly; vector-mask path operations (combine, subtract, intersect) use the non-zero rule only; levels, curves, hue/saturation, brightness/contrast and exposure layers in CMYK and Lab documents are kept but not applied; the base layer's fill opacity in a clipping group applies to the whole group. Figures are in [Fidelity of the layer render](#fidelity-of-the-layer-render).

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
| Levels, curves, hue/saturation, brightness/contrast, exposure, invert, threshold, posterize | `AdjustmentLayer` | Per-channel levels and curves apply before the master set. Levels, curves, hue/saturation, brightness/contrast and exposure layers of CMYK and Lab documents are kept as placeholders (the pixels were converted to RGB, and these act on ink or Lab channels), and written as an identity levels layer, since their parameters mean nothing in an RGB file. Invert, threshold and posterize apply as usual. |
| Gradient and pattern fills, shape layers with such fills | A pixel layer of the stored pixels | Not redrawn: the stored raster (often 1 x 1) is what renders. |
| Text, smart objects, layer effects (`lfx2`, `lrFX`), strokes (`vstk`) | Pixel layer + kept blocks | Drawn from the stored pixels only; the blocks are written back untouched. |
| Image resources, ICC profile, document-level blocks | `PsdFile.Resources`, `SourceProfile`, `DocumentBlocks` | Kept and written back. Document-level odd-length blocks keep their unpadded length so they write back byte for byte. |
| Layer info of 16 and 32-bit files | `Lr16` / `Lr32` blocks | Written like Photoshop does. |

Not stored in the file: the document `Background` (it is a render-time color, so a merged image written with a white background reads back as layers over transparency), and anything the layer model does not have (layer styles, smart-object contents, vector strokes as editable data).

Defaults of the model: 8-bit gray files load as `GrayAlpha8` documents, other files as RGB (`Rgba8`, `Rgba16`, `RgbaF32` by depth), CMYK always as 8-bit RGB. Colors of CMYK/Lab layers go through the color engine, so layers of CMYK files are not bit-for-bit the file's samples.

## Writing

`PsdFile.Save` writes layers with run-length coding (default) or raw channels, 8, 16 and 32 bits, a merged image rendered from the layers (with an alpha channel when the background is not opaque), the sRGB profile, and a PSB when the document is wider or taller than 30,000 px or `PsdWriteOptions.Large` says so (a PSB holds up to 300,000 px). A layer with no PSD equivalent (a gradient or image brush fill layer, a custom adjustment) throws `ImageOperationException` instead of being dropped. A non-seekable stream is written through a temporary file.

Writing the same loaded file twice gives the same bytes, and a document read back from a file renders to the same pixels as the document that was written (opacities are quantized to 8 bits first).

## Fidelity of the layer render

Rendering a loaded layer tree and comparing with the file's own stored composite (corpus of 316 PSD/PSB test files from the psd-tools project, MIT licensed, fetched by `tools/fetch-psd-tests.sh`, never committed): 315 load; the one that does not is a multichannel file. Of the 152 files with layers and no feature we do not draw, the median mean error is 0.275 levels (96 files at or under 0.5, 105 at or under 2, 123 at or under 10). The rest differ for known reasons:

- stored composites that are stale or are Photoshop's "not saved with a composite" placeholder (`layers-minimal/*`, `masks/2.psd`, `note.psd`);
- gradient and pattern fills, text, smart objects, layer effects and strokes, which are not drawn;
- posterize rounds to nearest here and to buckets in Photoshop; curves are monotone cubic here and a different spline there; brightness/contrast scaling is approximate; hard-mix and vivid-light differ slightly;
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
