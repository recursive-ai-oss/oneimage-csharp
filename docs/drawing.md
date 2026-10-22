# Drawing

Vector fills for `Image` and `Canvas`: paths, an anti-aliased rasterizer, solid brushes, blend modes. Strokes, gradient and image brushes, clipping and text build on the same layers (see [Extending](#extending)).

```csharp
using OneImage;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;   // System.IO is imported by default in most projects

Image img = Image.Load("photo.jpg")
    .Fill(new Rgba32(255, 255, 255, 200), Path.Star(300, 200, 5, 60, 140))
    .Fill(new Rgba32(0, 0, 0, 255), Path.ParseSvg("M10 10 H 90 V 90 Z"), rule: IntersectionRule.EvenOdd);

using var canvas = new Canvas(800, 600, PixelFormat.Rgba8);
canvas.Clear(new Rgba32(255, 255, 255, 255));
canvas.Fill(new Rgba32(30, 90, 200, 255), Path.Circle(400, 300, 200));   // in place
```

## Coordinates

Pixel coordinates, x to the right, y down. Pixel (i, j) covers [i, i+1) × [j, j+1); its center is (i + 0.5, j + 0.5). A rectangle from (2, 2) to (4, 4) fills exactly four pixels.

## Layers

A fill is five steps, each knowing nothing of the next:

1. **Geometry.** `Path` is an immutable list of figures: move, line, quadratic, cubic, close. `PathBuilder`, the shape factories (`Rectangle`, `RoundedRectangle`, `Ellipse`, `Circle`, `Polygon`, `Polyline`, `RegularPolygon`, `Star`, `Combine`) and `Path.TryParseSvg` produce paths; `Transform`, `Translate`, `Scale`, `Rotate` and `Reverse` return new ones. Arcs (SVG elliptical arcs included) are stored as cubics of at most 90°.
2. **Flatten.** Curves become straight segments within 0.1 pixel (`Path.DefaultTolerance`). A curve whose control polygon lies wholly outside the image is replaced by its chord, which has the same winding effect, so curves that are huge or far away cost nothing.
3. **Coverage.** The segments are clipped to the image (above and below dropped; left of the image folded onto x = 0, where it still sets the winding; right of it dropped) and sorted into buckets by tile row. A tile is rasterized from its row's bucket only, so rendering one tile never touches the rest of the shape. Anti-aliasing is exact area accumulation (the signed-area method of font-rs and stb_truetype): each edge adds the area to its right in every pixel it crosses; a running sum along the row gives the covered fraction. Non-zero takes `min(|sum|, 1)`, even-odd folds the sum with period 2. Without anti-aliasing a pixel is in when its center is.
4. **Paint.** A `Brush` supplies the color for the covered pixels. The only brush so far is `Brush.Solid`; the rasterizer never sees it and the brush never sees geometry.
5. **Composite.** The paint is combined with the pixels through the same kernels as `Composite` (`BlendMode`, `AlphaComposition`, premultiplied float). The operator is applied at full strength and mixed back by the coverage, so for Normal/source-over this is the usual alpha blend, and other modes keep soft edges. 8-bit RGB and RGBA with Normal/source-over have a direct path; fully covered pixels of replacing operators are written without a blend.

## Semantics worth knowing

- **Color space.** Brush colors are numbers in the image's own space; nothing is converted. By default blending happens on stored (encoded) values, as in an image editor. `linear: true` fills in linear light (the base is converted for the tiles the path can touch, so those pixels may change by rounding).
- **Lazy and in place.** `Image.Fill` returns a lazy image; only tiles the path can touch are computed and the rest are the input's own tiles. A run of fills is computed from the tile under the whole run in one pass. `Canvas.Fill` draws in place, in parallel over tiles, with the same bytes as `Snapshot().Fill(...)`. Output is identical for any thread count.
- **Animation.** Every frame gets the fill, in its own pixel grid.
- **Failure.** Bad arguments throw at once. A path that flattens to more than `Path.MaxFlattenedPoints` points (2²⁴), or a transform that leaves the finite range, fails with a typed exception when pixels are computed. NaN and infinite coordinates are refused by the builder. Coordinates beyond about ±2²⁴ lose sub-pixel precision when clipped.
- **SVG path data.** `TryParseSvg` reads M, L, H, V, C, S, Q, T, A, Z in both cases. It is strict (first error fails the whole parse), bounded (64 Mi characters, 2²⁵ commands), and never throws. It is fuzzed (`fuzz/run.sh svg`).
- **Known limit.** Where outline parts of opposite direction cross inside one pixel, their areas cancel and that pixel is slightly under-covered.

## Compared with ImageSharp.Drawing 2.1.7

Alpha plane of a white fill on a transparent 140 × 140 image, our output against theirs (test: `ImageSharpCoverageTests`):

| Shape | mean diff (levels) | max diff | Where the maximum sits |
|---|---|---|---|
| rectangle at fractional offsets | 0.13 | 22 | edge pixels |
| thin rectangle (0.4 px wide) | 0.55 | 163 | their output has full alpha on pixels where the exact area is 40% |
| circle / ellipse | 0.18 / 0.12 | 28 | curved edges |
| star, blob (1000 points) | 0.10 / 0.15 | 54 / 22 | vertices |
| pentagram, non-zero / even-odd | 0.11 / 0.15 | 99 | pixels where two edges cross |
| cubic loops | 0.26 | 176 | near the sharp ends of the curve |
| SVG arcs, rounded rectangle, multi-figure holes | 0.05–0.07 | 16–26 | curved edges |

The maxima are single pixels, not investigated further. Without anti-aliasing a pixel is in when its center is. ImageSharp's non-anti-aliased fill truncates crossings to whole pixels and samples rows near their bottom, so integer-aligned shapes match exactly while shapes at fractional positions differ by up to one pixel row or column on their border (mean ≤ 0.4 levels on curves).

Speed (local smoke result, 2000 × 2000 RGBA, solid color, 4 threads, load 4–7): see `bench/OneImage.Benchmarks/Drawing`. A 1000-point polygon takes 1.6 ms (opaque) or 3.2 ms (alpha 160) in place against 8.5 and 7.6 ms; 300 circles take 8.9 and 13.6 ms against 21.4 and 21.6 ms, with about half the allocation.

## ImageSharp.Drawing names

| ImageSharp.Drawing | OneImage |
|---|---|
| `ctx.Fill(color, path)` | `img.Fill(color, path)` / `canvas.Fill(color, path)` |
| `ctx.Fill(brush, path)`, `Brushes.Solid(c)` | `img.Fill(Brush.Solid(c), path)` |
| `ctx.Fill(color)` (whole image) | `img.Fill(color)`; `Clear(color)` replaces pixels |
| `GraphicsOptions.Antialias` | `antialias:` |
| `ShapeOptions.IntersectionRule` | `rule:` (`IntersectionRule.NonZero` is the default; set `rule:` explicitly when porting) |
| `GraphicsOptions.BlendPercentage`, `ColorBlendingMode`, `AlphaCompositionMode` | `opacity:`, `blend:`, `composition:` |
| `DrawingOptions.Transform` | `transform:` or `path.Transform(matrix)` |
| `PathBuilder` (`AddLine`, `AddCubicBezier`, `AddArc`, `CloseFigure`) | `PathBuilder` (`LineTo`, `CubicBezierTo`, `ArcTo`, `Close`) |
| `RectangularPolygon`, `EllipsePolygon`, `RegularPolygon`, `Star`, `Polygon`, `ComplexPolygon` | `Path.Rectangle`, `Ellipse`/`Circle`, `RegularPolygon`, `Star`, `Polygon`, `Path.Combine` |
| `Path.TryParseSvgPath` | `Path.TryParseSvg` |
| `path.Transform`, `path.Bounds`, `path.Flatten()` | the same names on `Path` |

Not yet: strokes and pens, gradient/pattern/image/recolor brushes, polygon boolean operations, `Clip`, text.

## Extending

- **Stroker.** Offsets a flattened `Path` into outline polygons, then calls the same fill with the non-zero rule. Nothing below geometry changes.
- **Brushes.** Subclass `Brush` (its members are internal for now): `FillRow(x, y, rgba)` writes straight RGBA floats for a run of pixels; the paint step already handles non-solid brushes through that row call, mixing by coverage and blend mode. Linear-light fills with non-solid brushes are refused until a brush can say how to convert itself.
- **Clipping.** A clip is a second coverage source multiplied into the coverage before the paint step (`TileRasterizer` already yields one float row per pixel row).
- **Text.** Glyph outlines are `Path`s; text is a fill of their combination.
- **Layers.** A layer's mask is the same coverage plane; `BlendKernel.Row` is the composite step.
