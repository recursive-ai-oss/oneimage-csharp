# Drawing

Vector fills and strokes for `Image` and `Canvas`: paths, an anti-aliased rasterizer, solid brushes, pens (width, joins, caps, dashes), blend modes. Gradient and image brushes, clipping and text build on the same layers (see [Extending](#extending)).

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

## Strokes

```csharp
using var canvas = new Canvas(800, 600, PixelFormat.Rgba8);
canvas.Draw(new Rgba32(30, 90, 200, 255), 6, Path.ParseSvg("M50 300 C 200 50 400 550 750 300"));            // color + width
canvas.Draw(Pen.Dash(new Rgba32(200, 30, 30, 255), 3).WithCap(StrokeCap.Round), Path.Circle(400, 300, 120));   // preset dash
canvas.DrawPolygon(new Pen(Brush.Solid(new Rgba32(0, 0, 0, 255)), 12, StrokeJoin.Round), points);             // helpers
Path outline = path.GenerateOutline(width: 8, join: StrokeJoin.Bevel);                                       // the stroke as a Path
```

- **API.** `Image.Draw` (lazy) and `Canvas.Draw` (in place) take a `Pen` or a color and width, with the options of `Fill` (`antialias`, `opacity`, `blend`, `composition`, `linear`, `transform`; no `rule`: strokes are always filled non-zero). Helpers: `DrawLine`, `DrawPolyline`, `DrawPolygon`, `DrawBeziers`, `DrawRectangle`, `DrawEllipse`. `Path.GenerateOutline(pen)` or `GenerateOutline(width, join, cap, miterLimit, dashPattern, dashOffset)` returns the outline: closed contours that, filled non-zero, cover exactly the stroke. A `Pen` is immutable: a `Brush` (any), width, `StrokeJoin` (`Miter`, `Round`, `Bevel`, `Square`), `StrokeCap` (`Butt`, `Round`, `Square`), miter limit and a dash pattern with offset; `Pen.Solid`, `Dash`, `Dot`, `DashDot`, `DashDotDot` are presets and `With...` methods change one property.
- **Defaults.** Width 1, miter joins with limit 4, butt caps, solid. A miter longer than `limit x width / 2` is cut to a bevel (SVG and PostScript). `Square` is Clipper's square join (ImageSharp's `JointStyle.Square`): the corner is cut flat at half the width from the vertex.
- **Geometry.** The stroke is centered on the path (half the width each side). Curves are flattened (0.1 pixel after the transform), then each figure becomes one outline contour (open) or two (closed: outer and inner edge), with the stroke on the same side of every contour so overlaps never cancel and the non-zero fill paints each pixel once: a translucent pen is even where it crosses itself. Round joins and caps are exact cubic arcs. Inside a turn the edges are cut at their intersection when both segments are long enough to reach it and otherwise pass through the vertex, so no hole opens however wide the pen. A `transform` is applied to the outline, so it scales the width too (as in SVG). Where two parts of the stroke overlap inside one pixel along an edge, the coverage is a little higher than the exact union.
- **Dashes.** A pattern is alternating on and off lengths in path units, starting with on; an odd-length pattern is repeated once (SVG). The offset moves the start into the pattern and may be negative. Every figure restarts the pattern at its start; a closed figure that is on at both its start and its end gets one dash across the start. Caps apply to every dash, so round or square caps lengthen each dash by half the width at both ends (SVG). Zero-length dashes draw dots with round and square caps. The presets (`Dash` 3:1, `Dot` 1:1, `DashDot` 3:1:1:1, `DashDotDot` 3:1:1:1:1:1) scale with the width as ImageSharp's do; a pattern given to `new Pen`/`WithDash` is absolute.
- **Degenerate input.** Repeated points are ignored. A figure of one point draws nothing, except `M p L p Z`, which draws a dot for round and square caps. Width 0 draws nothing. NaN, infinite or negative widths, miter limits below 1, bad dash patterns and offsets throw `ArgumentOutOfRangeException`; widths up to 2^24 are accepted.
- **Limits.** The outline is built when `Draw` is called (not lazily), in time proportional to the path, even where the path lies outside the image. An outline over 2^23 commands or 2^25 coordinates, a flattening over `Path.MaxFlattenedPoints`, or more than 2^22 dashes throws `ImageLimitException`. Output is identical for any thread count, and `Image` and `Canvas` give the same bytes.

### Compared with ImageSharp.Drawing 2.1.7

Alpha plane of a white stroke on a transparent 140 x 140 image (test: `StrokeImageSharpTests`; the numbers below are the mean and the maximum difference in levels):

| Case | mean | max |
|---|---|---|
| zigzag polyline, miter / round / square joins, width 1, 2.5, 12 | 0.10-0.19 | 33-206 |
| sharp-angle polyline (44 deg), same joins and widths | 0.16-0.18 | 20-51 |
| butt / round / square caps, width 2.5 and 12 | 0.11-0.16 | 39-102 |
| closed pentagon, miter w1 / w2.5, round and square w12 | 0.12-0.17 | 13-65 |
| cubic curve, round joins and caps, width 1 / 2.5 / 12 | 0.14-0.19 | 40-75 |
| circle, width 1 / 2.5 / 12 | 0.37-0.38 | 32-34 |
| dashes (dash, dot, dash-dot, dash-dot-dot), width 1 and 3 | 0.29-0.45 | 118-135 |

Systematic differences:

- **Position.** ImageSharp strokes land half a pixel right and down of where SVG puts them (a width-3 line at y = 30 covers rows 29 to 31 there and 28.5 to 31.5 here); its fills use pixel corners, its pens pixel centers. We follow SVG, so the table above moves our path by (0.5, 0.5) first. When porting, translate by half a pixel to reproduce its exact output.
- **Bevel.** ImageSharp has no bevel join (`Miter`, `Round`, `Square` only); `Bevel` is checked by geometry in tests.
- **Miter limit.** ImageSharp's limit is somewhat above 3.5; our default of 4 agrees on every join in the zigzag. Its tip pixels differ by up to 206 levels at sharp miters.
- **Circles.** The 0.37 mean is a thin difference all along the edge (maximum 34). Our curves are flattened to within 0.1 pixel of the true circle; the cause on their side was not isolated.
- **Dashes.** The maxima are single pixels at dash ends, not investigated further.

Speed (local smoke result, 2000 x 2000 RGBA, solid color, load 0.4 at start, .NET 10.0.11, X64 AVX-512 host; BenchmarkDotNet, 12 iterations): `OneImage Canvas` in place against ImageSharp.Drawing.

| Task | OneImage | ImageSharp.Drawing | Allocated (ours / theirs) |
|---|---|---|---|
| 1000-point polyline, width 3, round joins | 1.15 ms | 8.87 ms | 0.67 / 2.32 MB |
| 300 circles, width 3 | 9.5 ms (6.6 ms as lazy `Image` + copy) | 36.3 ms | 7.4 / 34.8 MB |
| dashed 400-point wave, width 3, 3:1 | 0.80 ms | 6.0 ms | 0.34 / 2.74 MB |

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
| `ctx.Draw(pen, path)`, `ctx.Draw(color, width, path)` | `img.Draw(pen, path)`, `img.Draw(color, width, path)` / `canvas.Draw(...)` |
| `Pens.Solid`, `Pens.Dash`, `Dot`, `DashDot`, `DashDotDot` | `Pen.Solid`, `Pen.Dash`, `Pen.Dot`, `Pen.DashDot`, `Pen.DashDotDot` |
| `new SolidPen(new PenOptions(color, width) { JointStyle, EndCapStyle })` | `new Pen(brush, width, join, cap, miterLimit)` |
| `PatternPen`, `PenOptions` stroke pattern (multiples of width) | `new Pen(..., dashPattern:)` (absolute lengths) or `pen.WithDash(...)` |
| `JointStyle.Miter/Round/Square`, `EndCapStyle.Butt/Round/Square` | `StrokeJoin.Miter/Round/Square` (plus `Bevel`), `StrokeCap.Butt/Round/Square` |
| `ctx.DrawLine`, `DrawPolygon`, `DrawBeziers`, `Draw(pen, new RectangularPolygon(...))` | `DrawLine`, `DrawPolygon`, `DrawPolyline`, `DrawBeziers`, `DrawRectangle`, `DrawEllipse` |
| `path.GenerateOutline(width, pattern, isClosed, joint, endcap)` | `path.GenerateOutline(pen)` / `path.GenerateOutline(width, join, cap, miterLimit, dashPattern, dashOffset)` |

Not yet: gradient/pattern/image/recolor brushes, polygon boolean operations, `Clip`, text.

## Extending

- **Stroker.** Done: see [Strokes](#strokes). Any new geometry (text outlines, boolean results) can be stroked by passing it to `Draw`.
- **Brushes.** Subclass `Brush` (its members are internal for now): `FillRow(x, y, rgba)` writes straight RGBA floats for a run of pixels; the paint step already handles non-solid brushes through that row call, mixing by coverage and blend mode. Linear-light fills with non-solid brushes are refused until a brush can say how to convert itself.
- **Clipping.** A clip is a second coverage source multiplied into the coverage before the paint step (`TileRasterizer` already yields one float row per pixel row).
- **Text.** Glyph outlines are `Path`s; text is a fill of their combination.
- **Layers.** A layer's mask is the same coverage plane; `BlendKernel.Row` is the composite step.
