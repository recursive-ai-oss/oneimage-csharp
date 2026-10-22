# Drawing

Vector fills and strokes for `Image` and `Canvas`: paths, an anti-aliased rasterizer, solid, gradient, pattern, image and recolor brushes, pens (width, joins, caps, dashes), blend modes. Boolean operations on paths and clipping (see [Boolean operations and clipping](#boolean-operations-and-clipping)) and text build on the same layers (see [Extending](#extending)).

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
4. **Paint.** A `Brush` supplies the color for the covered pixels, one row at a time (see [Brushes](#brushes)); the rasterizer never sees it and the brush never sees geometry.
5. **Composite.** The paint is combined with the pixels through the same kernels as `Composite` (`BlendMode`, `AlphaComposition`, premultiplied float). The operator is applied at full strength and mixed back by the coverage, so for Normal/source-over this is the usual alpha blend, and other modes keep soft edges. 8-bit RGB and RGBA with Normal/source-over have a direct path; fully covered pixels of replacing operators are written without a blend.

## Semantics worth knowing

- **Color space.** Brush colors are numbers in the image's own space; nothing is converted. By default blending happens on stored (encoded) values, as in an image editor. `linear: true` fills in linear light (the base is converted for the tiles the path can touch, so those pixels may change by rounding).
- **Lazy and in place.** `Image.Fill` returns a lazy image; only tiles the path can touch are computed and the rest are the input's own tiles. A run of fills is computed from the tile under the whole run in one pass. `Canvas.Fill` draws in place, in parallel over tiles, with the same bytes as `Snapshot().Fill(...)`. Output is identical for any thread count.
- **Animation.** Every frame gets the fill, in its own pixel grid.
- **Failure.** Bad arguments throw at once. A path that flattens to more than `Path.MaxFlattenedPoints` points (2²⁴), or a transform that leaves the finite range, fails with a typed exception when pixels are computed. NaN and infinite coordinates are refused by the builder. Coordinates beyond about ±2²⁴ lose sub-pixel precision when clipped.
- **SVG path data.** `TryParseSvg` reads M, L, H, V, C, S, Q, T, A, Z in both cases. It is strict (first error fails the whole parse), bounded (64 Mi characters, 2²⁵ commands), and never throws. It is fuzzed (`fuzz/run.sh svg`).
- **Known limit.** Where outline parts of opposite direction cross inside one pixel, their areas cancel and that pixel is slightly under-covered.

## Brushes

```csharp
var sunset = Brush.LinearGradient(new Vector2(0, 0), new Vector2(0, 600),
    [new(0f, new Rgba32(255, 200, 80, 255)), new(0.6f, new Rgba32(220, 60, 90, 255)), new(1f, new Rgba32(30, 20, 90, 255))]);
canvas.Fill(sunset, Path.Rectangle(0, 0, 800, 600));
canvas.Fill(Brush.Hatch(HatchStyle.ForwardDiagonal, new Rgba32(255, 255, 255, 120)), Path.Circle(400, 300, 150));
img = img.Fill(Brush.FromImage(texture, offsetX: 10), Path.Star(300, 200, 5, 60, 140));
```

| Factory | Paints |
|---|---|
| `Brush.Solid(color)` | one color |
| `Brush.LinearGradient(start, end, stops, repetition, premultiplied)` | position = projection of the pixel center on the line start→end |
| `Brush.RadialGradient(center, radius, stops, ...)` | position = distance from the center / radius |
| `Brush.EllipticGradient(center, axisEnd, axisRatio, stops, ..., squaredDistance)` | position = normalized distance in an ellipse whose first axis runs from the center to `axisEnd` and whose other axis is `axisRatio` times as long |
| `Brush.PathGradient(points, colors, centerColor, center)` | barycentric blend of the vertex colors and a center color over the triangles (center, point *i*, point *i*+1); transparent outside them |
| `Brush.Pattern(foreground, background, bool[,] cells, transform)` | a tiled two-color bitmap, cells indexed `[row, column]` |
| `Brush.Hatch(HatchStyle, foreground, background, transform)` | `Horizontal`, `Vertical`, `ForwardDiagonal`, `BackwardDiagonal`, `Percent10`, `Percent20`, `Min` (4 × 4 cells) |
| `Brush.FromImage(source, region, offsetX, offsetY, transform, tile)` | the pixels of an image, repeated (or placed once with `tile: false`) |
| `Brush.Recolor(source, target, threshold)` | the target color where the pixels already there are close to the source color |

**Coordinates.** A brush works in the image's pixel grid, at pixel centers, and does not move with the shape: two fills with one brush line up. A fill's `transform` moves only the path. Patterns and image brushes take their own `transform` (pattern or source space to image space, row-vector `Matrix3x2`; each pixel takes the cell or source pixel under its center, without smoothing); gradients take their geometry in image pixels.

**Gradient colors.** Stops are sorted by position (stable, so two stops at one position make a hard edge); before the first stop the first color holds and after the last the last. Positions outside 0..1 are allowed. `GradientRepetition` says what happens for positions outside 0..1: `None` extends the end colors, `Repeat` starts over each unit (negative positions too), `Reflect` runs back and forth, `DontFill` leaves them transparent (both ends inclusive).

**Interpolation.** Colors blend linearly in the numbers they are given in, which is the image's encoded values (as CSS, SVG and editors do), and by default *premultiplied* by alpha, so red fading to transparent blue stays red as it fades. `premultiplied: false` blends straight colors, where red to transparent blue passes through purple; this is what ImageSharp.Drawing does, and the two agree when every stop is opaque. With `linear: true` the stop colors are converted to linear light first, interpolation runs in linear light, and the fill converts back with everything else: black to white then has its midpoint at sRGB 188, not 128. The conversion uses the image's own profile curve (a 4096-step table from the same profile conversion the pixels get), so brush colors and pixels agree for matrix/curve profiles; for gray images a color is first reduced to gray with Rec. 709 weights. `PathGradient`, pattern, hatch and recolor colors are converted the same way; an image brush's texels are converted as they are read.

**Recolor.** With *d* the Euclidean distance between a pixel and `source` over straight R, G, B, A in 0..1, the strength is `max(0, 1 − d² / (4 · threshold))` (1 for an exact match, 0 from *d* = 2·√threshold); the pixel then becomes the target painted with its alpha times that strength. Threshold 0 changes exact matches only. The formula was fitted to ImageSharp's `RecolorBrush` on opaque pixels (agreement to 1 level); on pixels with partial alpha ImageSharp's result differs a little, and it is not matched. In linear fills the comparison happens in linear light.

**Image brush.** The source is read once, on first use, as 8-bit straight RGBA from its first frame; a region over 2²⁶ pixels is refused (`ImageLimitException`; pass a smaller `region`). The fill node lists the source image as an input, and its descriptor names that node. Failures reading the source surface when the pixels are computed.

**Hostile input.** Stops with NaN or infinite positions, no stops, a zero-length gradient line or axis, a zero, negative or NaN radius or ratio, empty patterns, singular or non-finite transforms, a negative or NaN threshold and fewer than three path-gradient points are refused with `ArgumentException` or `ArgumentOutOfRangeException` when the brush is built. Rows asked for at any coordinate (including ±2³¹) never throw and never produce NaN.

**Writing a brush.** Derive from `Brush` and implement `FillRow(x, y, rgba)`: write straight RGBA floats (0..1, in the image's space) for the run of pixels starting at image column `x` on row `y`; it is called from several threads on different tiles and must give the same numbers for the same pixel every time. A brush that needs the pixels already there (like `Recolor`) overrides `ReadsBackdrop` and the overload that receives them. `TryGetSolid` lets a one-color brush take the fast path and `Describe` names the brush in the op descriptor (the default says `custom`). In a `linear: true` fill the numbers a custom brush writes are treated as encoded values and converted to linear light after the call.

**Speed.** Opaque brush pixels over fully covered pixels are written without reading the backdrop (8-bit RGBA through a vector kernel, `BrushKernel.RgbaF32ToRgba8`, with a scalar reference); everything else goes through the blend kernels. Measured on this workstation (2000 × 2000 RGBA, 4 threads, load 4–5, .NET 10; local smoke results, see `bench/OneImage.Benchmarks/Drawing/BrushBenchmarks.cs`):

| Fill | OneImage (in place) | ImageSharp.Drawing | allocated |
|---|---|---|---|
| linear gradient, whole image, 3 stops | 5.1 ms | 11.3 ms | 28 KB vs 125 MB |
| radial gradient in a circle | 6.2 ms | 98.8 ms | 24 KB vs 80 MB |
| hatch over a 1000-point polygon | 4.7 ms | 12.5 ms | 63 KB vs 180 KB |
| 64 × 64 image brush over the same polygon | 5.2 ms | 15.8 ms | 63 KB vs 532 KB |

### Compared with ImageSharp.Drawing 2.1.7

The same fill is rendered by both on a 96 × 64 backdrop and the RGBA bytes compared (test: `BrushImageSharpTests`); "mean" is per channel value, in levels.

| Brush and case | mean | max | notes |
|---|---|---|---|
| linear gradient, three stops, None / Repeat / Reflect / DontFill, opaque and translucent backdrop | 0.000 | 0 | start at the image corner, see below |
| hard stops; unsorted stops and stops outside 0..1 | 0.000 | 0 | |
| translucent stops, `premultiplied: false` | 0.10–0.11 | 1 | rounding |
| radial gradient, all four modes | 0.000 | 0 | |
| elliptic gradient, all four modes, `squaredDistance: true`, slanted axis | 0.000–0.0001 | 1 | axis end mirrored, see below |
| path gradient, hexagon (polygon moved half a pixel) | 0.011 | 136 | only on pixels on the outline; 0 of 5464 pixels away from it differ by more than 2 |
| path gradient, concave L shape, default center | 0.009 | 4 | |
| seven hatch presets, whole image and inside a rectangle | 0.000 | 0 | |
| custom pattern, translucent colors | 0.000 | 0 | |
| image brush: whole, region + offset, inside a rectangle, opacity 0.5 | 0.000–0.022 | 0–1 | the offset equals the rectangle's corner |
| recolor, thresholds 0.02 / 0.1 / 0.3 (opaque pixels) | 0.000 | 0–1 | |

Systematic differences, all by choice:

- **Pixels behind a gradient's start.** ImageSharp mirrors them for a slanted line (position −0.3 looks like 0.3) and clamps them for an axis-aligned line even in Repeat mode. Here `None` clamps, `Repeat` continues the pattern backward and `DontFill` leaves them empty, on any line. The comparisons put the start at the image corner so no pixel lies behind it.
- **Elliptic gradients.** ImageSharp turns a slanted ellipse the opposite way from its axis end (an axis pointing down-right draws an ellipse elongated up-right) and uses the *square* of the normalized distance as the position. Here the axis points at `axisEnd` and the position is the distance itself, like the radial gradient; `squaredDistance: true` gives ImageSharp's falloff. The comparison mirrors the axis end.
- **Translucent stops** blend premultiplied by default, as said above.
- **Anchoring.** ImageSharp anchors an image brush at the top-left of the shape's bounds (patterns at the image origin); here every brush is anchored in the image, so to match, use `offsetX`/`offsetY` equal to the shape's corner.
- **Path gradient.** ImageSharp evaluates at pixel corners (x, y) and includes the outline; this library evaluates at pixel centers, so the same picture needs the points moved by half a pixel. The outline is inclusive here too (within 10⁻⁵).
- **Recolor on partially transparent pixels** is not matched.

## Compared with ImageSharp.Drawing 2.1.7 (coverage)

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

## Boolean operations and clipping

```csharp
Path both = Path.Circle(100, 100, 60).Union(Path.Circle(150, 110, 50));          // one outline around both
Path moon = Path.Circle(100, 100, 60).Subtract(Path.Circle(125, 90, 55));        // the first minus the second
Path lens = a.Intersect(b);   Path odd = a.Xor(b);   Path merged = PathBoolean.UnionAll(circles);
Path clean = Path.Polygon(selfCrossingPoints).Resolve(IntersectionRule.EvenOdd);   // no overlaps, no crossings
Path grown = outline.Offset(12, StrokeJoin.Round);  Path shrunk = outline.Offset(-12);

img = img.Clip(Path.Circle(300, 200, 120), i => i.GaussianBlur(8));             // blur only inside the circle
canvas.Clip(heart, i => i.Invert().Grayscale());                                 // the same, in place
img = img.Fill(Brush.Solid(red), Path.Rectangle(0, 0, 800, 600), clip: heart);   // paint only inside the heart
```

### Path booleans

`BooleanOperation` is `Union`, `Intersection`, `Difference` (subject minus clip) or `Xor`. `path.Boolean(operation, other, subjectRule, clipRule, tolerance)` does any of them, and `Union`, `Intersect`, `Subtract` and `Xor` are the short forms. Each path is read as the region its closed figures enclose under its own `IntersectionRule` (non-zero by default), so self-intersecting paths, shapes with holes and paths that overlap themselves work as they do when filled. `PathBoolean.UnionAll(paths)` merges any number of paths in one pass; it resolves each path on its own first, so a path wound the other way, or with holes, does not cancel its neighbors. `Resolve(rule)` rewrites one path as non-overlapping polygons.

- **Result.** A `Path` of straight-edged, closed polygons: curves are flattened first (`tolerance`, default 0.1 px, like a fill), so a union of two circles is a polygon with arcs of short segments. Outer contours and holes run in opposite directions, nothing overlaps and no two edges cross, so the result fills the same under either rule. Collinear vertices are removed, edges two shapes share on opposite sides vanish, and shapes that only touch at a point stay separate. An empty region gives `Path.Empty`.
- **Exactness.** Every coordinate is rounded to a grid of 1/256 pixel, and intersection points are rounded to it too; all decisions (which side, which order, what crosses) are made in exact integer arithmetic. Collinear and coincident edges, edges meeting at a vertex, spikes, zero-area slivers and edges that cross at tiny angles are therefore handled without tolerances. Rounding an intersection moves the two edges by under 1/512 px, which can make a new crossing nearby; the engine finds and settles those too (a few rounds at most). The result does not depend on thread count (the work is single-threaded) or on the order of the input figures except for where each polygon starts.
- **Limits.** Coordinates beyond ±33,554,432 px or more than 2²² flattened segments fail with `ImageLimitException`, as does an arrangement of more than 2²³ pieces or a comparison count past 2³¹. Time is about n log n plus the number of crossings (a 1000-point polygon against a 24-point star takes about 0.16 ms); a pentagram-like star of 2001 vertices (two million crossings) takes about 12 s.
- **Offset.** `Offset(distance, join, miterLimit, rule)` falls out of the same machinery: the region unioned with (distance > 0) or minus (distance < 0) the band of points within |distance| of its boundary, built from a rectangle per edge and a wedge per corner (`Round`, `Miter` with a limit, `Bevel` or `Square`). Shrinking removes parts thinner than twice the distance. Corners of the result follow the join where the boundary turns away from the offset; elsewhere they stay sharp.

### Clip

`image.Clip(path, operations, antialias, rule, transform)` runs `operations` (a `Func<Image, Image>`, called once, now) and keeps the result only inside `path`: pixels outside are the input's own, pixels inside are the operations' result byte for byte, and pixels along the edge mix the two by the path's coverage (exact area, or the pixel center without `antialias`). The mix is made in premultiplied float values, as a layer mask would, and rounded to the format. Any chain works because it is lazy and ordinary: the operations never see the path. They do see the whole image, so a blur at the edge draws on pixels outside the path, and the operations must keep the image size and frame count (a different pixel format is converted back).

Only tiles the path can reach ask the operations for pixels; tiles wholly inside are the operations' tiles, tiles wholly outside are the input's. On a 2000 x 2000 image a blur clipped to a circle computes the circle's tiles only. `canvas.Clip(...)` is the same in place, in parallel over tiles, with the same bytes as `Snapshot().Clip(...)`; the operations read the pixels as they were before the call, so each rewritten tile is swapped for a fresh pooled one and the old tile goes back to the pool (unless you hold a `Snapshot()`, which keeps the old tiles). Frames of an animation are clipped each with the path in their own grid. Clips nest: the operations can contain a `Clip`. The descriptor is named `clip` with the rule, anti-aliasing, transform and path as parameters.

### Clipped fills and strokes

`Fill` and `Draw` (with a `Pen`, or a color and width) on `Image` and `Canvas` take `clip:` and `clipRule:`. The clip path's coverage is multiplied into the shape's coverage row before painting, so only pixels inside both change and the clip edge is anti-aliased like any other; the clip is in image coordinates and `transform:` does not move it. With `antialias: false` both are sampled at pixel centers. This costs one extra rasterization per touched tile and nothing when `clip` is null. It works with every brush, blend mode and with `linear: true`. The helpers (`DrawLine`, `DrawPolygon`, ...) do not take a clip; wrap them in `Clip(...)`.

### Compared with ImageSharp.Drawing 2.1.7

Alpha plane of a white fill of each result on a transparent 140 x 140 image (test: `BooleanImageSharpTests`; the results of `Boolean` against `IPath.Clip(ShapeOptions, ...)`, each filled by its own library; mean and maximum difference in levels, then the filled area in pixels, ours / theirs):

| Case | Union | Intersection | Difference | Xor |
|---|---|---|---|---|
| two overlapping circles | 0.22 / 32 | 0.11 / 34 | 0.17 / 32 | 0.33 / 32 |
| 5-point star and a rectangle | 0.10 / 112 | 0.02 / 14 | 0.12 / 112 | 0.15 / 112 |
| pentagram (self-intersecting) and a rectangle | 0.12 / 65 | 0.03 / 11 | 0.12 / 65 | 0.20 / 65 |
| pentagram (even-odd) and a circle | 0.12 / 65 | 0.14 / 95 | 0.21 / 148 | 0.29 / 183 |
| square with a square hole (even-odd) and a bar | 0.08 / 16 | 0.02 / 16 | 0.02 / 16 | 0.12 / 16 |
| two rectangles sharing an edge | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 |
| 1000-point blob and a 7-point star | 0.19 / 163 | 0.11 / 91 | 0.15 / 106 | 0.32 / 187 |
| self-crossing bow tie and a circle | 0.13 / 127 | 0.15 / 76 | 0.13 / 127 | 0.27 / 127 |

The maxima are single pixels at vertices, where the two libraries round slightly differently. Filled areas agree within about 1% (the largest gap is a small 680-pixel lens, 1.1%; most are under 0.3%); ours are exact on the 1/256 grid, which `BooleanTests` checks against analytic areas and a point-by-point brute-force definition of each operation, including random polygons and lattice polygons whose edges overlap and cross at lattice points constantly.

Differences by choice:

- ImageSharp's `IPath.Clip(...)` defaults to *difference*; here the operation is always named. Its `ShapeOptions.IntersectionRule` applies to both operands; here each has its own rule.
- ImageSharp's `Clip` on a context applies the processors to the path's bounding rectangle and then masks with the path; here the operations are lazy and only the tiles the path reaches are computed.
- Results are polygons of straight segments in both libraries; ours are snapped to 1/256 px and have collinear vertices removed.

Speed (local smoke result, 2000 x 2000 RGBA, .NET 10.0.11, X64 AVX-512 host, shared workstation with load 4.7 at start rising to about 10 during the run, so treat as rough; see `bench/OneImage.Benchmarks/Drawing/ClipBenchmarks.cs`): union of 200 circles 7.2 ms vs 11.1 ms (3.2 MB vs 23 MB allocated); a star minus a 1000-point polygon 0.158 ms vs 0.134 ms (33 KB vs 205 KB; the one row slower than ImageSharp.Drawing, split about evenly between finding the crossings and the sweep); a Gaussian blur clipped to a circle 12.3 ms lazy + copy-out and 12.9 ms in place on a Canvas, vs 56 ms (the Canvas row allocates 88 KB, the lazy row 52 KB, ImageSharp.Drawing 423 KB).

## ImageSharp.Drawing names

| ImageSharp.Drawing | OneImage |
|---|---|
| `ctx.Fill(color, path)` | `img.Fill(color, path)` / `canvas.Fill(color, path)` |
| `ctx.Fill(brush, path)`, `Brushes.Solid(c)` | `img.Fill(Brush.Solid(c), path)` |
| `LinearGradientBrush`, `RadialGradientBrush`, `EllipticGradientBrush`, `PathGradientBrush`, `ColorStop`, `GradientRepetitionMode` | `Brush.LinearGradient`, `RadialGradient`, `EllipticGradient`, `PathGradient`, `ColorStop`, `GradientRepetition` |
| `PatternBrush`, `Brushes.Horizontal` ... `Brushes.Min` | `Brush.Pattern`, `Brush.Hatch(HatchStyle, ...)` |
| `ImageBrush`, `RecolorBrush` | `Brush.FromImage`, `Brush.Recolor` |
| `ctx.Fill(color)` (whole image) | `img.Fill(color)`; `Clear(color)` replaces pixels |
| `GraphicsOptions.Antialias` | `antialias:` |
| `ShapeOptions.IntersectionRule` | `rule:` (`IntersectionRule.NonZero` is the default; set `rule:` explicitly when porting) |
| `GraphicsOptions.BlendPercentage`, `ColorBlendingMode`, `AlphaCompositionMode` | `opacity:`, `blend:`, `composition:` |
| `DrawingOptions.Transform` | `transform:` or `path.Transform(matrix)` |
| `PathBuilder` (`AddLine`, `AddCubicBezier`, `AddArc`, `CloseFigure`) | `PathBuilder` (`LineTo`, `CubicBezierTo`, `ArcTo`, `Close`) |
| `RectangularPolygon`, `EllipsePolygon`, `RegularPolygon`, `Star`, `Polygon`, `ComplexPolygon` | `Path.Rectangle`, `Ellipse`/`Circle`, `RegularPolygon`, `Star`, `Polygon`, `Path.Combine` |
| `Path.TryParseSvgPath` | `Path.TryParseSvg` |
| `path.Transform`, `path.Bounds`, `path.Flatten()` | the same names on `Path` |
| `subject.Clip(new ShapeOptions { ClippingOperation = ..., IntersectionRule = ... }, clips)` | `subject.Boolean(BooleanOperation, clip, subjectRule, clipRule)`; `Union`, `Intersect`, `Subtract`, `Xor`; `PathBoolean.UnionAll(paths)` |
| `ClippingOperation.Union/Intersection/Difference/Xor` | `BooleanOperation.Union/Intersection/Difference/Xor` |
| `ctx.Clip(path, c => c.GaussianBlur(8))` | `img.Clip(path, i => i.GaussianBlur(8))` / `canvas.Clip(path, ...)` |
| `ctx.Fill(options, brush, path)` with a clipped region | `img.Fill(brush, path, clip: clipPath)` |
| `ctx.Draw(pen, path)`, `ctx.Draw(color, width, path)` | `img.Draw(pen, path)`, `img.Draw(color, width, path)` / `canvas.Draw(...)` |
| `Pens.Solid`, `Pens.Dash`, `Dot`, `DashDot`, `DashDotDot` | `Pen.Solid`, `Pen.Dash`, `Pen.Dot`, `Pen.DashDot`, `Pen.DashDotDot` |
| `new SolidPen(new PenOptions(color, width) { JointStyle, EndCapStyle })` | `new Pen(brush, width, join, cap, miterLimit)` |
| `PatternPen`, `PenOptions` stroke pattern (multiples of width) | `new Pen(..., dashPattern:)` (absolute lengths) or `pen.WithDash(...)` |
| `JointStyle.Miter/Round/Square`, `EndCapStyle.Butt/Round/Square` | `StrokeJoin.Miter/Round/Square` (plus `Bevel`), `StrokeCap.Butt/Round/Square` |
| `ctx.DrawLine`, `DrawPolygon`, `DrawBeziers`, `Draw(pen, new RectangularPolygon(...))` | `DrawLine`, `DrawPolygon`, `DrawPolyline`, `DrawBeziers`, `DrawRectangle`, `DrawEllipse` |
| `path.GenerateOutline(width, pattern, isClosed, joint, endcap)` | `path.GenerateOutline(pen)` / `path.GenerateOutline(width, join, cap, miterLimit, dashPattern, dashOffset)` |

Not yet: text.

## Extending

- **Stroker.** Done: see [Strokes](#strokes). Any new geometry (text outlines, boolean results) can be stroked by passing it to `Draw`.
- **Brushes.** Subclass `Brush` (see *Writing a brush* above): `FillRow(x, y, rgba)` writes straight RGBA floats for a run of pixels; the paint step handles every brush through that row call, mixing by coverage and blend mode, in linear light too. Pens take any `Brush`.
- **Clipping.** Done: see [Boolean operations and clipping](#boolean-operations-and-clipping). A clip is a second coverage source multiplied into the coverage before the paint step (`TileRasterizer.ApplyClip`), and `ClipNode` is the same coverage used as a mask between an image and its processed copy.
- **Text.** Glyph outlines are `Path`s; text is a fill of their combination.
- **Layers.** A layer's mask is the same coverage plane; `BlendKernel.Row` is the composite step.
