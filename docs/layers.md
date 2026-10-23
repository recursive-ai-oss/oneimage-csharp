# Layers

A layered document that renders on demand: pixel layers with offsets, groups, fill and adjustment layers, masks, clipping, all 24 blend modes, and a cache that recomputes only what an edit touched. It lives in the core package, namespace `OneImage.Layers`, and is built on the same tiles, blend kernels and drawing as the rest of the library.

```csharp
using OneImage;
using OneImage.Layers;
using Path = OneImage.Drawing.Path;

using var doc = new Document(1920, 1080, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });

PixelLayer photo = doc.Add(doc.CreatePixelLayer("photo", Image.Load("photo.jpg"), x: 100, y: 50));
LayerMask mask = photo.AddMask(revealAll: false, area: doc.Bounds);   // the mask canvas is in document coordinates here
mask.Pixels.Fill(new Rgba32(255, 255, 255, 255), Path.Circle(700, 450, 300));
mask.Feather = 12;

AdjustmentLayer warm = doc.Add(doc.CreateAdjustmentLayer("levels", Adjustment.Levels(inBlack: 0.05, gamma: 1.1)));
warm.Opacity = 0.8f;

PixelLayer paint = doc.Add(doc.CreatePixelLayer("paint"));
paint.BlendMode = BlendMode.Multiply;
paint.Pixels.Draw(new Rgba32(200, 40, 40, 255), 8f, Path.Polyline([new(300, 300), new(500, 380), new(700, 320)]));

doc.Render().Save("out.webp");        // lazy: only the tiles a save needs are computed
paint.Pixels.Fill(new Rgba32(0, 0, 0, 255), Path.Circle(900, 500, 20));
doc.Render().Save("out2.webp");       // recomputes just the tiles the circle touched
```

## The model

| Type | What it is |
|---|---|
| `Document` | Size, pixel format, color profile, background color, a root group, the render cache and (optionally) a spill store. Single-writer, `IDisposable`. |
| `DocumentSnapshot` | An immutable frozen state, taken in O(layers). Render it from any thread while you keep editing. |
| `Layer` | Base class: `Name`, `Visible`, `Opacity`, `FillOpacity`, `BlendMode`, `ClipToBelow`, `Locks`, `Mask`, `Bounds`. |
| `PixelLayer` | A `Canvas` (the document's pixel format) placed at `X`, `Y`. Paint on `Pixels` with any `Canvas` operation. |
| `GroupLayer` | Children, bottom first; `PassThrough` or isolated. The document's `Root` is a group. |
| `LayerStyle` | Optional `Layer.Style`: drop shadow, inner shadow, outer and inner glow, stroke, color overlay, gradient overlay, pattern overlay, bevel and emboss and satin, drawn from the layer's shape at composite time. See [Layer styles](#layer-styles). |
| `FillLayer` | A `Brush` (solid or gradient) covering the whole document. |
| `AdjustmentLayer` | An `Adjustment` (a point operation) applied to everything below it in its scope. |
| `LayerMask` | An 8-bit gray canvas placed at `X`, `Y` in document coordinates with `Density`, `Feather`, `OutsideValue` and `Enabled`. |
| `LayerLocks` | Lock flags carried as plain data; the model never refuses an edit because of them. |

Layers are created by the document (`CreatePixelLayer`, `CreateGroup`, `CreateFillLayer`, `CreateAdjustmentLayer`), put into a group with `Add`/`Insert`, and edited in place. `GroupLayer.Remove` detaches a layer without disposing it; `Move` reorders. Groups nest to `GroupLayer.MaxDepth` (64). A layer belongs to one document.

**Pixel format and color.** A document is RGB or gray, straight alpha, 8-bit (the default `Rgba8`), 16-bit or float. `DocumentOptions.ColorProfile` names the color space the pixel values are in (sRGB or the sRGB-curve gray profile by default); `CreatePixelLayer(name, image)` converts an image into it. CMYK and Lab documents are not supported yet; the profile model is part of the options so they can be added. Brush and background colors are in the document's color space.

## How a render is composited

For every output tile, the renderer folds the layers over the background from the bottom up. Each step is the same code as `Image.Composite(overlay, x, y, opacity, linear: false, blend, alpha)`:

- **Stack equals chain.** A stack of pixel layers renders to exactly the bytes of the equivalent chain of `Composite` calls (checked for every blend mode, with and without alpha in the background). Layers use source-over coverage with their `BlendMode` colors (the W3C rule `Cs' = (1 − αb)·Cs + αb·B(Cb, Cs)`), computed on premultiplied float, rounded half up on output.
- **Opacity.** The effective opacity is `Opacity × FillOpacity` (a float product). The model has no layer styles, so the two are interchangeable; Photoshop treats a few blend modes differently under fill opacity, and this model does not.
- **Hidden and empty.** Invisible layers and layers with zero effective opacity are skipped; a layer whose pixels (or whose sparse canvas tiles) cannot touch a tile costs nothing for that tile.
- **Linear light.** `DocumentOptions.BlendInLinearLight` makes every compositing step behave like `Composite(..., linear: true)`: both sides go to linear light, blend there, and come back. The default blends the stored values, as image editors do.

### Masks

The coverage of a pixel is `mask / 255`; **density** `d` mixes it toward fully visible, `255·(1 − d) + d·mask`; **feather** blurs the mask first with a Gaussian of that standard deviation in pixels (not in linear light, edges clamp). Outside the mask canvas the value is `OutsideValue` (255 for `AddMask(revealAll: true)`, 0 otherwise) before density and feather. The coverage multiplies the layer's alpha after the layer is positioned: `alpha' = round(alpha × coverage / 255)` on 8-bit data. A disabled mask is ignored. Masks apply to groups, fill and adjustment layers the same way.

### Groups

```csharp
GroupLayer g = doc.Add(doc.CreateGroup("highlights", passThrough: true));
```

- **Pass-through** (default). The children are composited straight onto what is below, so a child's `Multiply` sees the real backdrop and an adjustment inside the group adjusts everything below the group. At full opacity and no mask the result is bit-identical to the same layers without the group. Below full opacity or with a mask, the result is mixed with the untouched backdrop: `backdrop + k·(result − backdrop)` on premultiplied floats, `k = opacity × mask`. The group's own blend mode is ignored.
- **Isolated** (`PassThrough = false`). The children are composited onto a transparent surface, and the surface is laid over the backdrop as one layer with the group's blend mode, opacity and mask. A `Multiply` child of an isolated group over an empty surface shows its own color (Multiply with a transparent backdrop is the source); give the *group* the Multiply mode to multiply the whole group into the backdrop. Example: on a backdrop (200, 100, 50), a child (100, 200, 255) in Multiply gives (78, 78, 50) pass-through, and (100, 200, 255) isolated; with the group set to Multiply the isolated version gives (78, 78, 50) again.

### Clipping

A layer with `ClipToBelow = true` is drawn only where the nearest layer below it that is not itself clipped (its *base*) has coverage. The base and the layers clipped to it are drawn into a transparent surface (the base normally, each clipped layer with its own blend mode, opacity and mask, source-atop so the base's shape never grows), and that surface is laid over the backdrop with the **base's** blend mode, opacity and mask. This is Photoshop's "blend clipped layers as group" (Adobe's help: clipped layers take the opacity and blending mode of the base layer).

The edge cases, and where they follow Photoshop and where they do not:

- **Hidden base.** A hidden base, or one with zero opacity, hides everything clipped to it, whatever the clipped layers' own settings. Photoshop does the same: the base's visibility is the clipping group's visibility. A hidden *clipped* layer is skipped and the run goes on.
- **Fill opacity of the base.** The model has no layer styles, so `FillOpacity` multiplies into the opacity of the whole clipping group. Photoshop applies a base's fill opacity to the base's own pixels only (the usual trick of fill 0% on the base to show just the clipped layers, which needs the "blend clipped layers as group" option off). That is a deliberate difference; an "off" mode is not modelled.
- **No base.** A clipped layer at the bottom of its group, or after an adjustment layer (which cannot be a base), is drawn as an ordinary layer.
- **Group as the base.** Layers clipped to a group clip to the group's composited shape; the group is drawn on its own surface (isolated), whatever its pass-through setting, and its blend mode, opacity and mask are the clipping group's. Photoshop allows a group as a base.
- **Group clipped to a base.** Photoshop cannot clip a group (only layers); the flag exists in PSD files written by other tools, so it is handled here as an extension. An *isolated* group is composited on its own and drawn onto the surface source-atop with the group's blend mode, opacity and mask. A *pass-through* group has no surface of its own: its layers are drawn onto the clipping surface one after another, each as if it were clipped to the base (a child's blend mode sees what is already on the surface, an adjustment inside adjusts the surface), so at full opacity and no mask it is bit-identical to the same layers clipped one by one. Below full opacity, or with a mask, the result is mixed with the surface as it was before the group. A child's own `ClipToBelow` is ignored inside such a group, because every child is clipped to the base.
- **Adjustment layers.** A clipped adjustment adjusts only the base's surface.

### Adjustment layers

`Adjustment` has factories for `Levels`, `Curves`, `HueSaturation`, `BrightnessContrast`, `Exposure`, `Gamma`, `Invert`, `Threshold`, `Posterize`, `Grayscale` and `ColorMatrix` (the defaults are those of the `Image` method of the same name; `Curves`, `BrightnessContrast` and `Posterize` also have overloads that take a `CurveInterpolation`, `BrightnessContrastMode` or `PosterizeMode` to select Photoshop's formulas, which is what the PSD reader uses), and `Custom(name, Func<Image, Image>)`. An adjustment must be a **point operation**: each output pixel depends only on the same input pixel. Tiles are adjusted one at a time, so an operation that looks at neighbors (blur, resize) would show tile seams; the built-in factories are all point operations. Alpha is untouched. The adjusted backdrop is blended back as `backdrop + k·(adjusted − backdrop)`, `k = opacity × mask`; with a blend mode other than Normal the adjusted backdrop is first blended over the backdrop with that mode (Luminosity or Color make an adjustment touch tone only).

## Layer styles

`Layer.Style` (null by default) holds a `LayerStyle`: an immutable list of up to 64 effects, each an immutable `LayerEffect` with a blend mode, opacity (0..1) and color. Kinds: `DropShadowEffect`, `InnerShadowEffect`, `OuterGlowEffect`, `InnerGlowEffect`, `StrokeEffect` (outside, inside, center), `ColorOverlayEffect`, `GradientOverlayEffect`, `PatternOverlayEffect`, `BevelEmbossEffect` and `SatinEffect`; shadows, glows, satin and the bevel's gloss take an `EffectContour` (a piecewise-linear falloff remap of up to 256 points). Sizes are 0..250 px, distances 0..30000 px, spread and choke fractions 0..1, angles degrees counter-clockwise from the right; a value outside throws `ArgumentOutOfRangeException` when the effect is built. `Describe()` lists the style as `style.*` parameters with language-neutral names (`drop-shadow`, `stroke`, ...). A style survives a PSD save and load (see the [PSD page](psd.md#layer-effects)).

**Defaults** (as in Photoshop): shadows Multiply, 75 %, black, light 120 degrees, distance 5, size 5; glows Screen, 75 %, light yellow, size 5; stroke Normal, 100 %, black, 3 px outside; overlay Normal, 100 %, black; satin Multiply, 50 %, black, 19 degrees, distance 11, size 14, inverted.

**Gradients, patterns and bevels.** `EffectGradient` holds color stops and opacity stops (locations and midpoints 0..1, up to 256 each, smoothness 0..1, optional linear-light blending); `EffectGradientFill` lays it out as linear, radial, angle, reflected or diamond (and, for strokes, shape burst) with an angle, scale (0.1..10), reverse, align-with-layer and offsets. `GradientOverlayEffect.Fill` is one; `StrokeEffect.Gradient` paints a stroke with one, and `OuterGlowEffect.Gradient` / `InnerGlowEffect.Gradient` color a glow by its falloff (first stop at the shape). `EffectPattern` is an RGBA tile (up to 2^24 pixels) and `EffectPatternFill` tiles it with a scale (0.01..10), link-with-layer and a phase; `PatternOverlayEffect.Fill` and `StrokeEffect.PatternFill` use it. "Aligned with the layer" means `LayerStyle.AlignBox` when set, else the layer's bounds (the PSD reader sets the box to the shape's bounds); otherwise the document. `BevelEmbossEffect` (style, technique, depth 0..10, size 0..250, soften 0..16 px, up or down, angle and altitude, highlight and shadow mode, color and opacity, gloss contour) lights the shape as a height field; the base `Opacity` scales both its highlight and shadow, and its base `Blend` and `Color` are unused. Gradients, patterns and bevels are computed per tile over a window around it like the other effects, so a region render equals the same pixels of a whole render.

**Drawing.** The shape is the layer's alpha times its mask (fill layers and `LayerStyle.ShapeFromMask` use the mask coverage alone). Bottom to top: drop shadows, outer glows, the outside part of bevels and embosses, the layer's pixels, color overlays, gradient overlays, pattern overlays, satin, inner glows, inner shadows, the inside part of bevels and embosses, strokes; the same kind draws in list order. The layer's opacity scales every effect. Fill opacity scales the pixels and, since an overlay recolors the pixels without changing their alpha, the overlays; shadows, glows, satin and strokes ignore it, and a drop shadow is knocked out where the shape is (`KnockedOut`, default true). Shadows and glows blur with sigma 0.41 x size (exact Gaussian up to sigma 2.5, three box blurs beyond); strokes and edge falloffs come from an exact signed distance of the antialiased alpha. Effects are computed per tile over a window around it, so a region render equals the same pixels of a whole render at any degree.

**Which layers.** Pixel and fill layers draw their style. A style on a group or an adjustment layer is kept in the model but not drawn. A layer clipped to the layer below, and the base of a clipping run, draw effects in a simpler pass (cut to the base; interior effects laid over rather than mixed in). Disabled effects are skipped.

**Dirty tiles.** An effect reaches beyond the layer by its distance plus size, and an inner effect (inner shadow, inner glow, bevel, inside stroke) sees the shape that far away: a layer edit or a style change marks the layer's bounds, or the edited area, widened by the larger of the two (`Layer.Reach`), so a stroke painted next to a shadow recomputes the tiles the shadow reaches and no others. Replacing `Style` marks both the old and the new reach.

## Render on demand and dirty regions

`document.Render()` returns an `Image` and computes nothing. Pulling a region (`CopyPixelsTo`, `Save`, `Crop` then save) computes only the document tiles it covers, and only the layers that touch each tile. A region render equals the same pixels of a whole render, at any degree of parallelism.

Composited tiles are cached per document (`DocumentOptions.RenderCacheBytes`, 256 MiB by default; the oldest are dropped and recomputed). `Snapshot()` finds out what changed:

1. For every pixel layer and mask it takes an O(1) snapshot of the canvas's tile table and compares it with the table from the previous snapshot. Canvas writes are copy-on-write, so a tile that was written is a different object, and the comparison is a pointer check per tile. Nothing needs to be told about strokes, fills or `Lock` writes.
2. Property and structure changes (opacity, blend mode, visibility, mask settings, adding, removing, moving and reordering layers, `Background`) mark the area they can change: a layer's bounds, or for a move the layers it passed. Adjustment and fill layers cover the whole document. A feathered mask widens its dirty area by four sigma.
3. The marked tiles get a new revision. A later render recomputes a cached tile only if it changed after the cache entry was made.

`Document.TilesComputed` and `TilesReused` count tile requests, for tests and tuning. Snapshots share the cache: rendering an old snapshot after an edit is correct (tiles changed since then are recomputed for it).

## Storage and the disk-spill store

Without options everything stays in memory. With `DocumentOptions.Spill` the document's canvases (and its cached composited tiles) are rented from a store that keeps at most `BudgetBytes` of **sealed** tiles in memory and parks the rest in a temporary file, reading them back when their pixels are touched:

```csharp
var doc = new Document(6000, 4000, new DocumentOptions
{
    Spill = new SpillOptions { BudgetBytes = 256L << 20, Directory = "/var/tmp" },
});
// ... add layers; call layer.Flush() after painting a big layer so its tiles can move to disk
Console.WriteLine(doc.SpillStatistics);   // tiles written/read, resident and file bytes
```

- A tile is sealed when a canvas snapshot makes it immutable: `Document.Snapshot()` and `PixelLayer.Flush()` do that for every tile of a layer. Tiles that can still be written in place are never moved, so while you paint a layer its tiles count against the budget but cannot leave memory. Flush a layer when you finish with it.
- Eviction is least-recently-used with a second chance. A tile is written once; later evictions of the same unchanged tile only drop the memory.
- The file is created on first use, named `oneimage-spill-*.tmp`, unlinked immediately on Unix and opened delete-on-close on Windows, so it cannot outlive the process. `SpillOptions.MaxFileBytes` (64 GiB) bounds it; going over throws `ImageLimitException`. Dispose the document to close it.
- Images rendered from a spilling document read from the file: do not use them after the document is disposed.
- An evicted tile's memory is reused once no reader can still see it, with no forced garbage collection. Code that reads spill tiles for a known stretch wraps it in a read scope (the layer renderer does this per rectangle it composites, and the copy-out per tile it copies); a buffer evicted meanwhile waits in an epoch until every scope that was open at eviction has closed, then goes to a small free list that rentals and page-ins draw from. A tile read by code outside a scope (a user reading a layer canvas, a codec saving a canvas) is marked, and its buffer is left to the garbage collector when evicted, since a span may still point at it. The wait is bounded (32 MiB of waiting buffers, 64 MiB of idle ones); past that, buffers go to the collector. Peak resident memory is the budget plus the layer being painted, the tile objects and the usual slack of the managed heap (about 540 MB for 100 layers of 24 MP under 256 MiB).

## Descriptions

`Document.Describe()` and `Layer.Describe()` return a `LayerDescription`: a kind (`document`, `pixel`, `group`, `fill`, `adjustment`), parameters whose values are only `long`, `double`, `bool` or `string`, and the children of a group. Names are lower-case and hyphenated: `name`, `visible`, `opacity`, `fill-opacity`, `blend` (`multiply`, `color-dodge`, … or `pass-through`), `clip-to-below`, `lock-transparent-pixels`, `lock-pixels`, `lock-position`, `x`, `y`, `width`, `height`, `mask-x`, `mask-y`, `mask-width`, `mask-height`, `mask-outside`, `mask-density`, `mask-feather`, `mask-enabled`, `pass-through`, `brush.*`, `adjustment` and `adjustment.*`. Pixels are not part of a description.

## What a PSD reader or writer would map onto

| PSD concept | Here |
|---|---|
| Layer record: name, visible, opacity, fill opacity, blend key, clipping, lock flags | `Layer` properties of the same names |
| Blend keys (norm, mul, scrn, over, …) | `BlendMode` (the 24 modes); dissolve, darker/lighter color and pass-through-as-blend are not modes here (pass-through is `GroupLayer.PassThrough`) |
| Pixel channels and layer rectangle | `PixelLayer.Pixels` and `X`, `Y` |
| Layer mask and vector mask, density, feather, default color | `LayerMask` (raster masks; vector masks would be rasterized into one) |
| Layer group / section divider | `GroupLayer`, isolated or pass-through |
| Solid color and gradient fill layers | `FillLayer` |
| Adjustment layers (levels, curves, hue/saturation, …) | `AdjustmentLayer` with the matching `Adjustment` |
| Document size, depth, color mode, profile | `Document` size, `DocumentOptions.Format` and `ColorProfile` |
| Layer effects, smart objects, text layers | not modeled |

## Limits and edge behavior

- Layer canvases are checked against `ImageLimits` (or `DocumentOptions.Limits`) when created; the engine's hard caps apply.
- A layer may lie partly or wholly outside the document; the outside is kept but not drawn.
- `Opacity`, `FillOpacity` and `Density` throw `ArgumentOutOfRangeException` outside 0..1. A failing adjustment (one that changes the size or format) throws `ImageOperationException` when a tile is pulled, not when the layer is created.
- A document is single-writer. Snapshots and the images rendered from them are safe to use from any thread.
- Rendering an `Image` after `Document.Dispose()` throws `ObjectDisposedException` when a spill store was used.

## Measured

Ryzen 9 7950X3D (16 cores, 32 threads), .NET 10.0.13, on a shared machine with other jobs running (load 4-12 during the runs); documents of full-size 24 MP Rgba8 layers at 50% opacity, Normal blend. "Default" is the library per-call thread limit (4); "32" raises it for the call. The ImageSharp 3.1.12 rows clone a 24 MP image and chain DrawImage calls, using every core. "AVX2 only" rows run with `DOTNET_EnableAVX512=0`.

| Task | OneImage, default | OneImage, 32 threads | ImageSharp |
|---|---|---|---|
| Flatten 10 layers, every tile recomputed | 77 ms, 0.25 MB allocated | 34 ms | 72 ms |
| Flatten 50 layers, every tile recomputed | 361 ms, 0.25 MB allocated | 129 ms | 297 ms |
| Flatten 10 layers, AVX2 only | 115 ms | 40 ms | 91 ms |
| Flatten 50 layers, AVX2 only | 544 ms | 157 ms | 340 ms |
| Stroke on one of 20 layers, re-render, 512 x 512 viewport | 0.48 ms | | |
| Same, whole image into a caller buffer (24 MP copy-out dominates) | 8.0 ms | | |
| Same document, all 1504 tiles recomputed | 358 ms | | |

After a stroke, 2 of the 1504 tiles are recomputed and the other 1502 come from the cache. A 100-layer 24 MP document (9.6 GB of pixels) renders in 1.4 s with a 256 MiB spill budget at about 540 MB peak resident; 151,862 tiles are written and 150,368 read back, and the process makes no forced collection.

At the default degree, flattening 10 layers (77 ms) is slower than ImageSharp's 72 ms on all cores; it is published as an exception in [performance.md](performance.md#layer-flatten-exception).
