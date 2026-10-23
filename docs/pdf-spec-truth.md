# PDF spec-truth tests

`tests/OneImage.Pdf.Tests/SpecTruth/` checks the page renderer (`RecursiveAI.OneImage.Pdf`) against pages whose correct pixels follow from the PDF specification alone. No other renderer is involved: each test builds a small PDF in code, renders it at 72 dpi (one pixel per point), and compares the result with a raster computed in the test from the spec's own formulas.

Reference renderers disagree with each other, and none is always right, so they cannot be ground truth. These tests are the part of the render check that depends on none of them.

## What is covered

| Family | Examples |
|---|---|
| Color fills | DeviceGray/RGB/CMYK, ICCBased (sRGB, gray), Indexed, Separation with a type 2 tint transform |
| Shapes and fill rules | aligned and fractional rectangles, triangle and circle areas, non-zero vs even-odd on overlapping, opposite-direction, concentric and pentagram paths |
| Matrix | translate, scale, mirror, rotate 90/180/270 and 45°, concatenation order, `q`/`Q` |
| Page geometry | `/Rotate` (including 360, -90, 450), MediaBox and CropBox offsets, reversed corners, `/UserUnit`, dpi |
| Clipping | rectangular, nested, even-odd, restore by `Q`, clip after the painting operator, strokes and images under a clip |
| Transparency | `ca`/`CA`, all 12 separable and 4 non-separable blend modes, blend with alpha, `/BM` arrays, luminosity and alpha soft masks, `/SMask /None`, the mask's CTM |
| Groups | isolated vs non-isolated, group opacity, knockout, plain forms vs groups, form `/BBox` and `/Matrix` |
| Images | 1/2/4/8/16-bit samples, `/Decode`, Indexed, 7 placements, stencil masks, explicit masks of another resolution, `/SMask` (with `/Decode` and `/Matte`), color key masks, inline images, Flate with and without a predictor |
| Strokes | width, hairline, caps, joins, miter limit, degenerate subpaths, dash patterns (the spec's own examples), dashes under a matrix, caps on dashes, fill-then-stroke operators |
| Text | Type 3 fonts made of rectangles: `Td`, `Tm`, `TJ`, `Tc`, `Tw`, `Tz`, `Ts`, `TL`, `T*`, `'`, `"`, `TD`, text state across `BT`/`ET`, render modes, `d0`/`d1` colors, text knockout; one clip-mode case with a standard font |
| Shadings | axial and radial, `/Extend`, function types 0, 2 and 3, function arrays, `/Domain`, `sh` with clip, `/BBox` and alpha, shading patterns (`/Matrix`, `/Background`) |
| Tiling patterns | colored and uncolored, pattern matrix, steps larger than the BBox, BBox clipping, stroking |
| Annotations | appearance placed by `/Rect` and `/Matrix`, `/AS` states, hidden and print flags, drawn after page content |
| Structure | content stream arrays, inherited MediaBox and Resources |

Every test carries `[Trait("Section", "...")]` with the ISO 32000 section it tests, and a comment quoting the rule where it is not obvious. Section numbers are those of ISO 32000-1:2008, which ISO 32000-2 keeps for the parts used here.

## How the expected raster is derived

`Expected` is a plain RGB raster. Tests paint into it in PDF user space (origin bottom-left):

- rectangles by exact area coverage, composited source-over in device space;
- images by inverse-mapping each pixel center through the placement matrix;
- shadings by evaluating the spec's parametric formulas at each pixel center;
- blend modes with the formulas of 11.3.5, written out in `BlendMath.cs`;
- anything else with a short per-pixel function.

CMYK has no conversion in the specification, so its expectation is the library's documented one (the generic CMYK profile), obtained through the public `ConvertColor` API.

## Tolerances

- **Exact**: integer-valued expectations (pixel-aligned geometry, whole-level colors). The rendered pixel must equal the expected pixel.
- **Within 1 level**: where rounding is left open (half levels from alpha, gradients sampled at pixel centers, 16-bit and Decode scaling).
- **Edge (3 levels)**: pixels partly covered by a shape. Anti-aliasing is implementation-defined, so only the covered area is checked, as a per-pixel area fraction or as the sum over a shape (triangle, circle, round caps and joins). Curves allow for flattening, which the specification leaves to the flatness tolerance.
- **Not compared**: pixels where the specification gives no single answer, listed in the test next to the reason. Examples: partial pixels on a shading's boundary, and the region between sample centers of an enlarged image, which a reader may interpolate (see below).
- Stroke geometry where renderers legitimately differ (the join of two arms of a self-overlapping path) is skipped in that test only.

## Known failures

Tests for behavior the renderer gets wrong are marked with an xunit `Skip` that names the defect and the section, so the suite stays green and the list is greppable (`grep -rn "Skip =" tests/OneImage.Pdf.Tests/SpecTruth`). Remove the `Skip` when the behavior is fixed.

## Running

```
dotnet test tests/OneImage.Pdf.Tests -- --filter-namespace '*SpecTruth*'
```

The whole suite runs in about a second, needs no network and no external tools, and is deterministic. Set `ONEIMAGE_SPEC_TRUTH_DUMP=<directory>` to keep every generated PDF (named by content hash, with an index of test names) for looking at in another viewer.

## Image interpolation

Section 8.9.5.3 makes interpolation opt-in (`/Interpolate true`) and lets a reader skip it. Enlarged images are therefore compared only where any interpolation returns the sample itself (a pixel exactly at a sample center, or the outer half of an edge sample). One dedicated test requires a block-exact enlargement by default.
