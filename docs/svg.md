# SVG

Design of `RecursiveAI.OneImage.Svg` (assembly and namespace `OneImage.Svg`): SVG as an image format that renders directly at the size you ask for. This page describes what the package is, how it is built, and where it stands. The package currently detects SVG and reads its natural size; rendering arrives in the order given under [Roadmap](#roadmap).

```csharp
using OneImage;
using OneImage.Svg;

var config = new ImageConfiguration().Add(SvgFormat.Instance);   // not part of ImageConfiguration.Default, so it trims away when unused
var load = new LoadOptions { Configuration = config };

ImageInfo info = Image.Identify("logo.svg", load);                // natural size, nothing rendered
Image.Load("logo.svg", load).Resize(width: 400).Save("logo-400.png");   // planned: rasterized at 400 px, not scaled up from the natural size
```

## Scope

Static SVG 1.1 and the SVG Tiny-style content found in practice, plus the parts of SVG 2 and CSS that documents rely on:

| Area | Supported |
|---|---|
| Structure | `svg` (nested too), `g`, `defs`, `use`, `symbol`, `switch` (`systemLanguage`, `requiredFeatures`), `a`, `title`/`desc` ignored |
| Shapes | `rect` (with `rx`/`ry`), `circle`, `ellipse`, `line`, `polyline`, `polygon`, `path` |
| Painting | fill and stroke (width, caps, joins, miter limit, dashes), `fill-rule`, opacities, `paint-order`, `vector-effect: non-scaling-stroke`, `shape-rendering`, `visibility`, `display`, `color`/`currentColor`, `context-fill`/`context-stroke`, `mix-blend-mode`, `isolation`, `opacity` |
| Paint servers | `linearGradient`, `radialGradient` (with focal point and `fr`), `pattern`, href inheritance, `gradientTransform`/`patternTransform`, `spreadMethod`, both unit systems |
| Clipping and masking | `clipPath` (nested, per-child `clip-rule`), `mask` (luminance and alpha, nested), `overflow` clipping of nested viewports and markers |
| Markers | `marker-start`/`mid`/`end` and the `marker` shorthand, `orient`, `markerUnits`, `viewBox`, `refX`/`refY` |
| Images | `image` with a `data:` URI (PNG, JPEG, GIF, WebP, BMP, TIFF, ICO and nested SVG, decoded by OneImage's own codecs), `preserveAspectRatio`, `image-rendering` |
| Text | `text`, `tspan`, `textPath`, per-character `x`/`y`/`dx`/`dy`/`rotate`, `text-anchor`, baselines, decorations, spacing, `textLength`, writing modes; rendered through `OneImage.Text` |
| Filters | the `filter` element with the full SVG 1.1 primitive set, `feDropShadow`, and the CSS filter functions (`blur()`, `drop-shadow()`, `grayscale()`, ...) |
| CSS | presentation attributes, the `style` attribute, `<style>` with simple selectors, inheritance, `inherit`, `currentColor` |

Not supported, and never to be: SMIL animation, scripting and event attributes, `foreignObject`, `BackgroundImage`/`enable-background`, and any external fetch. Animation elements are ignored and the document renders as its first frame; `script` and `foreignObject` content is skipped.

**External references fail closed.** The package never opens a file, a URL or any other resource named by a document: `href`/`xlink:href` other than `#id` and `data:`, `@import`, `@font-face` sources, `url()` in CSS, the `xml-stylesheet` instruction. Such a reference is treated as dangling, which SVG already defines: an `image` or `use` renders nothing, a paint server reference falls back to its fallback color or `none`, a missing `filter` reference removes the element, a missing `clip-path`/`mask` is ignored. No error is raised and no I/O happens. A resolver hook can be added later as an explicit opt-in; there is none now.

## Architecture

```
bytes ─▶ source ─▶ XML parser ─▶ document ─▶ styles ─▶ render tree ─▶ renderer ─▶ pixels
         (gunzip,   (strict,      (typed     (CSS       (resolved,     (region at a
          caps)      bounded)      elements)  cascade)   size-free)     given scale)
```

1. **Source.** Reads the stream under a byte cap. Gzip data (`.svgz`) is detected by its signature and inflated with the library's own inflate, under the document cap and the expansion-ratio limit, then the stored CRC-32 and size are checked.
2. **XML parser** (`Xml/XmlParser.cs`). Our own, strict, XML 1.0 plus Namespaces, over UTF-8, UTF-16 (with BOM) and Latin-1/ASCII. It produces a plain tree of elements, attributes (namespace-resolved) and text.
3. **Document.** The tree is turned into typed SVG elements and attributes: lengths, colors, transforms, `viewBox`, `preserveAspectRatio`, paint, path data (parsed by the drawing layer's strict path parser). Unknown elements and attributes are kept as inert.
4. **Styles.** CSS is parsed, rules are matched and ordered, and every element gets computed property values with inheritance applied.
5. **Render tree.** The styled document is resolved into an immutable tree in user units, independent of the output size: groups (transform, opacity, clip, mask, filter, blend), paths (geometry, fill, stroke), images and text-as-outlines. `use`, `symbol`, nested `svg`, `switch`, markers and pattern contents are instantiated here, under budgets. References are resolved here too: a cycle or a dangling reference is turned into the defined fallback, never into a loop. The tree is built once per document and shared by every render of it.
6. **Renderer.** Draws a rectangle of the output at a given scale from the render tree, using the existing engine (see [Mapping to existing operations](#mapping-to-existing-operations)).

Everything up to and including the render tree is safe, bounds-checked code and runs under the [limits](#limits). There is no `unsafe` outside the core's kernel layer.

### XML parser: our own

The parser is written for this package rather than built on `System.Xml`. The deciding reasons, in order:

- **Policy is the point.** `XmlReader` can be made safe (`DtdProcessing.Prohibit`, no resolver), but then it is a safe configuration of a general parser. Ours cannot be configured unsafely: any `<!DOCTYPE` fails, and no entity reference except the five predefined ones and numeric character references is honored, so a document can never grow while it is read. There is nothing to forget to switch off.
- **Limits at the source.** Element count, depth, attributes per element, name length and namespace declarations in scope are enforced as the parser goes, before the memory they guard is allocated. `XmlReaderSettings` offers only character-count caps, so the rest would have to be counted in a wrapper anyway.
- **Iterative and allocation-aware.** The tree is built with an explicit stack (a 300,000-deep document is parsed without growing the call stack when the depth limit is raised) with interned names.
- **Trimming and AOT.** No dependency on `System.Xml`'s reader stack, which is large for trimmed or WASM apps.
- **Cost.** Measured on a 15 MiB document of 300,000 elements (local smoke, shared host at load ≈4.5, .NET 10.0.400): our parser builds the whole tree in 145–180 ms and 137 MiB allocated, including the 30 MiB UTF-16 copy of the text; `XmlReader` with DTDs prohibited only *reads* the same document, without building a tree, in 105–180 ms and 28 MiB. The two are the same speed class.

Deliberate deviations from a full XML processor: no DTDs of any kind (so documents with an internal subset, a handful of which exist in the wild, are rejected rather than partly honored), and encodings other than UTF-8, UTF-16 with BOM, ASCII and ISO-8859-1 are refused with a clear message instead of being guessed.

### Detection

`SvgFormat.Matches` sniffs the first 4 KiB: an optional BOM and white space, then any mix of XML declaration, comments, processing instructions and a DOCTYPE, then a start tag whose local name is `svg`. Gzip data is recognized by its signature and the first 2 KiB of its content is sniffed the same way, so a gzip file that is not an SVG is not claimed. The extensions are `.svg` and `.svgz`; the MIME type is `image/svg+xml`.

### Natural size

`Identify` reads only the root element's start tag (within the first 1 MiB), so it stays cheap on huge files. The natural size in CSS pixels (96 per inch) is:

- `width` and `height` when both are absolute lengths (`px`, `pt`, `pc`, `mm`, `cm`, `in`; `em` and `ex` at 16 and 8 px);
- percentages count as absent; a missing side is derived from the other through the `viewBox` aspect ratio; with neither, the `viewBox` size is used;
- the pixel size is the size rounded **up**, at least 1; zero, negative or malformed values fail with `ImageFormatException`.

A document with no usable width, height or `viewBox` takes the size of its content's bounding box; that needs geometry, so until the document model exists it fails with a format error.

## Rendering at the requested size

Rasterizing an SVG and then scaling the bitmap throws away exactly what makes SVG worth having. The core already treats this kind of thing like JPEG shrink-on-load: a decoder advertises what it can do and the graph pushes the request down. SVG gets the same treatment.

**Core changes** (small, additive; made when rendering lands):

- `DecoderCapabilities.RenderAtSize` (a new flag): the decoder can produce its frame at any pixel size, not only 1/2, 1/4, 1/8.
- `DecodeRequest` gains optional `TargetWidth` and `TargetHeight` (0 = the natural size). The `Region` of the request is then expressed in target pixels. The SVG decoder also sets `RandomAccess`: a 128×128 region is cheap to render because only the render-tree nodes that touch it are drawn.
- A graph rewrite beside the existing shrink-on-load one: when `Resize`/`Thumbnail` sits directly above an undisturbed decode of a `RenderAtSize` decoder (through the orient node, as for JPEG), the decode is re-created at the final size. `Fit.Stretch`/`Contain` map to the target directly; `Cover` renders at the covering size and crops (the crop is then pushed down as a region); `Pad` renders at the contained size and pads. Upscaling is honored (`upscale: true` renders sharper, not blurrier). Rotation, `Affine` and other geometry ops keep working on the natural-size raster and are not rewritten.

Without the rewrite, loading still works: the image has its natural size and downstream operations act on that raster. Output is always sRGB 8-bit straight RGBA, with a transparent background unless the caller flattens it.

**Region rendering.** One entry point draws "this rectangle of the output at this scale". The scale maps user space to pixels through the root `viewBox` and `preserveAspectRatio` into the target size (non-uniform if the caller asks for a stretched size). Per request the renderer:

1. looks up the render-tree nodes whose device bounds meet the region (a bounding-box grid built once per tree), in paint order;
2. paints into a `Canvas` the size of the region, with the region offset folded into the transform;
3. writes rows to the sink.

Whole-frame requests are cut into bands/tiles of at most 512×512 so offscreen layers stay small; the frame itself is never held in layers. Filter regions larger than a tile get their own bounded region (see [Limits](#limits)). Results do not depend on how the frame is cut: a tile's pixels equal the same pixels of a whole render (tested by comparing both).

## Mapping to existing operations

| SVG feature | Built from |
|---|---|
| Fill of a shape | `Fill(brush, path, rule, transform, opacity)`; `shape-rendering: crispEdges`/`optimizeSpeed` → `antialias: false` |
| Stroke | `Draw(pen, path, ...)`: width, `StrokeCap`, `StrokeJoin` (miter, round, bevel; SVG 2 `miter-clip`/`arcs` fall back to miter/bevel as SVG 2 allows), miter limit, dashes. Non-scaling stroke: stroke the transformed path |
| Solid, linear, radial paint | `Brush.Solid`, `Brush.LinearGradient`, `Brush.RadialGradient` with `GradientRepetition` for `spreadMethod`. **New:** an affine `transform` on gradient brushes (for `gradientTransform` and bounding-box units), and a focal/two-point-conical radial gradient |
| Pattern | The tile is rendered to an image at device resolution and painted with `Brush.FromImage` through the pattern transform. **New:** smooth (bilinear) sampling for non-axis-aligned pattern transforms |
| `opacity`, `mix-blend-mode`, `isolation` on a group | The group is drawn to an offscreen `Canvas` layer, then `Composite(layer, opacity, blend: BlendMode, alpha: AlphaComposition)`. Groups with none of these, no clip, mask or filter are drawn straight into the parent (no layer). A single shape with only `opacity` uses the fill/stroke opacity parameter |
| `clip-path` | Entirely geometric: each child shape (with its own `clip-rule`, transform and nested `clip-path`) is resolved with `PathBoolean`, the children are united (`UnionAll`), a nested clip is intersected, and the result drives `Image.Clip`/`Canvas.Clip`, whose coverage is multiplied into the fill coverage |
| `mask` | The mask content is rendered to a layer; its luminance (in `sRGB` or `linearRGB` per `color-interpolation`) or its alpha (`mask-type: alpha`) times the mask region becomes a coverage layer. **New:** an op that multiplies a layer's alpha by another image's luminance or alpha |
| Viewport clipping (`overflow`, nested `svg`, `symbol`, markers) | A rectangle path through `Clip` |
| `image` | `Image.Load` on the decoded `data:` bytes (limits apply, nested SVG included), `Resize`/`Pad`/`Crop` for `preserveAspectRatio`, drawn with `Composite` (or an image brush for rotated placement); `image-rendering` selects the resampling filter |
| Text | `OneImage.Text`: `TextBuilder.GeneratePath` gives outlines that are filled, stroked, clipped like any path; `TextOptions.Path` carries `textPath`; rich runs carry per-run font, decoration and fill. Per-character `x`/`y`/`dx`/`dy`/`rotate` needs the shaped glyphs placed individually: **new in `OneImage.Text`:** a public way to get each cluster's outline and advance after shaping |
| Markers | Instantiated into the render tree as groups with a transform per vertex |
| Filters | See below |

### Filters

A `filter` becomes a small graph of primitives evaluated on the element's rendered layer. Evaluation happens in linear-light premultiplied float:

| Primitive | Maps to |
|---|---|
| `feGaussianBlur` | `GaussianBlur(sigma, sigmaY, edge: Constant)`; the input outside the filter region is transparent |
| `feOffset` | whole-pixel offsets: `Pad`/`Crop`; fractional offsets: `Affine` translation |
| `feFlood` | a fill of the primitive subregion |
| `feBlend` | `Composite(..., blend: BlendMode)` (normal, multiply, screen, darken, lighten and the SVG 2 modes) |
| `feComposite` | `AlphaComposition` for over/in/out/atop/xor. **New:** `arithmetic` (k1..k4 on premultiplied channels, clamped) |
| `feColorMatrix` | `ColorMatrix` (matrix, saturate, hueRotate, luminanceToAlpha as a matrix) on straight color, as the SVG specification defines |
| `feMerge` | repeated source-over `Composite` |
| `feDropShadow` | blur + offset + flood + merge |
| `feImage` | a `data:` image, or an element reference rendered like `use` |
| `feComponentTransfer` | **New:** per-channel identity/table/discrete/linear/gamma on straight color, built on the tone-chain lookup tables |
| `feConvolveMatrix` | `Convolve` where it matches; **new:** target offset, bias, `preserveAlpha`, `edgeMode` duplicate/wrap/none |
| `feMorphology` | **New:** erode/dilate (separable min/max over a rectangular window) |
| `feTile` | **New:** tile a subregion |
| `feTurbulence` | **New:** the Perlin turbulence defined in the specification (fractal noise and turbulence, seeds, octaves, stitch tiles) |
| `feDisplacementMap` | **New** |
| `feDiffuseLighting`, `feSpecularLighting` and the three light sources | **New** |
| CSS filter functions | direct: `blur()` → `GaussianBlur`; `brightness`, `contrast`, `grayscale`, `hue-rotate`, `invert`, `opacity`, `saturate`, `sepia` → `ColorMatrix`/tone ops; `drop-shadow()` as `feDropShadow` |

The filter region (`x`, `y`, `width`, `height` in `objectBoundingBox` or `userSpaceOnUse`, default −10%, −10%, 120%, 120%) and primitive subregions are resolved in device pixels. The region needed for a tile is the tile grown by the filter's margin (blur radius, offset, morphology radius, convolution order), then clipped to the filter region. Filters on rotated or skewed content run on the pixel grid scaled by the transform's axis scales and the result is mapped back by the remaining rotation or skew.

## Color

- The document and output are **sRGB**. Colors parse to sRGB (`icc-color()` fallbacks are ignored). Painting, compositing, blending, opacity and gradient interpolation happen on encoded sRGB values in premultiplied float — SVG's default `color-interpolation: sRGB` — and are rounded to 8 bits when a layer is stored.
- `color-interpolation: linearRGB` on gradients and masks is honored through the `linear` option the brushes and ops already have.
- **Filters** follow `color-interpolation-filters`, whose default is `linearRGB`. A filter's layer is converted to linear sRGB once through the color engine's built-in linear profile, held as premultiplied float32 for the whole primitive graph, and converted back once at the end, so a chain of primitives does not round to 8 bits between steps. A primitive that asks for `sRGB` converts its inputs and outputs at that boundary. Primitives that must see straight color (`feColorMatrix`, `feComponentTransfer`) un-premultiply for the operation, as specified.
- Raster images embedded as `data:` URIs keep their own color profile and are converted to sRGB when drawn. Output frames carry no profile.

## Limits

Every bound is checked before the allocation or work it guards; breaking one raises `ImageLimitException`. `SvgLimits` (set per registration: `new SvgFormat(new SvgLimits { ... })`) holds these, alongside the ordinary `ImageLimits` of the load call (pixels per frame, dimension, expansion ratio), which also bound the output size.

| Limit | Default | Where it applies |
|---|---:|---|
| Document bytes (file, and gzip output) | 32 MiB | source — implemented |
| Elements | 500,000 | XML parser — implemented |
| Element nesting depth | 256 | XML parser — implemented |
| Attributes per element | 512 | XML parser — implemented |
| Name length | 256 characters | XML parser — implemented |
| Namespace declarations in scope | 256 | XML parser — implemented |
| Root start-tag scan for `Identify` | 1 MiB | source — implemented |
| Gzip expansion ratio | `ImageLimits.MaxExpansionRatio` (1100) | source — implemented |
| CSS text (all `<style>` and `style` attributes) | 2 MiB | styles |
| CSS rules | 20,000 | styles |
| Selector matching work | 100 million steps | styles; rules are indexed by id, class and type to keep honest documents far below this |
| Path data per attribute / segments per document | 8 MiB / 4 million | document |
| Instantiated render nodes (`use`, `symbol`, markers, pattern and mask contents, text glyphs) | 1,000,000 | render tree: each `use` is charged the memoized size of what it instantiates, so nested `use` cannot multiply |
| Reference depth (`use`→`use`, `clip-path`→`clip-path`, `mask`→`mask`, `pattern`/`marker`/`filter` chains) | 32 | render tree; cycles are detected separately and resolved to the dangling-reference fallback |
| Nested image documents | 3 levels | `image` of an SVG data URI; they share the budgets above |
| `data:` payload per image / per document | 16 MiB / 64 MiB | render tree; decoded under `ImageLimits` |
| Text characters | 1 million | render tree |
| Pattern tile size | 16 megapixels | renderer, after scaling to device pixels |
| Offscreen layer | 512×512 per tile | renderer |
| Filter region | 64 megapixels | renderer, in device pixels after clipping to what is needed |
| Filter primitives per filter / filter nesting | 256 / 4 | render tree |
| Convolution order, morphology radius, blur σ, turbulence octaves | 64×64, 1024 px, 1024 px, 16 | renderer; larger values are clamped, not errors |

Cancellation is observed at least once per tile and per filter primitive. Rendering errors that are not limits (a malformed path, an unusable gradient) follow the SVG error-handling rules — the element is not rendered — and never raise.

Lazy behavior follows the rest of the library: `Identify` and `Load` read the root start tag; the rest of the document is read when pixels are first pulled, so a malformed body fails then with `ImageFormatException`. `Load(..., validate: true)` is meant to parse the whole document up front; until rendering lands it only reads the root start tag.

## Testing against resvg's suite

Correctness is measured against the public test collection of the resvg project: its SVG files and reference PNGs, only. The committed subset lives in `tests/testdata/resvg` (MIT OR Apache-2.0, notice in `THIRD-PARTY-NOTICES.md`); `tools/fetch-resvg-tests.sh` downloads the full collection with its fonts at a pinned commit, and `ONEIMAGE_RESVG_TESTS` points the tests at it.

`ResvgConformance` loads every SVG, renders it at its reference PNG's size (300 px wide for the 200×200 test files) through the same `Resize` call a thumbnail request makes, and compares it to the reference with a stated tolerance: both images are composited over white, a pixel differs when any channel is off by more than 24 of 255, and a test passes when at most 0.25% of its pixels differ. The tolerance absorbs anti-aliasing differences between rasterizers, not wrong shapes or colors. It prints a pass-rate table per category (set `ONEIMAGE_SVG_REPORT` to also write it to a file), with the exclusions listed:

- DTD or entity declarations (refused by policy);
- external file references (refused by policy);
- scripts, animation, `foreignObject`, `@import` (not supported).

Behavior is specified from the SVG and CSS specifications and checked against the reference images; no renderer's source is consulted.

## Roadmap

Planned order. Each step is a coherent piece with its own observable claim, measured by the conformance table above and the stated tests.

1. **Document model and shapes.** The core render-at-size hook and rewrite; typed document; presentation attributes and the `style` attribute; the render tree for `g`, shapes and `path`; solid fill and stroke with all stroke properties; transforms, `viewBox`, `preserveAspectRatio`, nested `svg`, basic `use`/`symbol`; region rendering; bounding-box natural size. Claim: `shapes` and the stroke/fill groups of `painting` pass at a stated rate, and `Thumbnail` of an SVG renders at the target size (compared against rasterizing then resizing).
2. **CSS and structure.** `<style>`, selectors, cascade, inheritance, `currentColor`, `switch`, `systemLanguage`, `use` budgets and cycle handling, `data:` images, `transform-origin`. Claim: `structure` passes at a stated rate; the CSS cost limits are tested with hostile stylesheets.
3. **Paint servers.** Gradients (including focal radial and gradient transforms), patterns, `paint-order`, `context-fill`/`context-stroke`, plus the brush additions in the core. Claim: `paint-servers` pass rate.
4. **Painting extras.** Markers, `mix-blend-mode`, `isolation`, group opacity layers, `shape-rendering`, `image-rendering`, `visibility`, `display`. Claim: `painting` pass rate.
5. **Clipping and masking.** `clipPath`, `mask`, viewport clipping; the layer-multiply op. Claim: `masking` pass rate.
6. **Filters, part one.** The filter graph, regions and subregions, linear-light float evaluation, and the primitives that map onto existing operations, `feDropShadow` and the CSS filter functions. Claim: the corresponding `filters` directories.
7. **Filters, part two.** The new primitives: component transfer, arithmetic, morphology, tile, convolution completions, turbulence, displacement, lighting. Claim: the rest of `filters`.
8. **Text.** `text`/`tspan`/`textPath`, per-character positioning (with the `OneImage.Text` addition), fonts from `SvgOptions` and the system, fallback, decorations, writing modes; bidirectional text once `OneImage.Text` has it. Claim: `text` pass rate with the suite's fonts.
9. **Hardening and speed.** Fuzz targets for the document, CSS and renderer; hostile-input tests for every limit; benchmarks (24-megapixel and thumbnail renders, large maps, filter-heavy art) against the .NET SVG renderers and libvips/librsvg; the package readme, release notes and publishing setup. Claim: no fuzz findings after the stated run, and the benchmark table.

## Status

Done: package skeleton, format detection (plain and gzip), natural size, the strict bounded XML parser (fuzzed), the SVG limits that exist so far, and the conformance harness with the committed test files. Rendering is not available yet: pulling pixels fails with `ImageOperationException`.
