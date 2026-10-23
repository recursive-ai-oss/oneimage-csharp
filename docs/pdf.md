# PDF

`RecursiveAI.OneImage.Pdf` (namespace `OneImage.Pdf`) draws PDF pages as an ordinary image format. It parses and interprets with [OnePDF](https://www.nuget.org/packages/RecursiveAI.OnePdf) (`RecursiveAI.OnePdf` and `RecursiveAI.OnePdf.Fonts`, 1.1.0 or later) and draws on OneImage's drawing engine, so a page is an `Image` like any other: it can be resized, cropped, converted and saved, and only the part of the page that is asked for is drawn.

```csharp
var config = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = 150 }));
Image.Load("report.pdf", new LoadOptions { Configuration = config }).Save("page1.png");   // every page is a frame
Image.Load("report.pdf", new LoadOptions { Configuration = config }).Resize(width: 400).Save("thumb.webp");
```

`PdfFormat` is not in the default configuration (a separate package cannot be). Reading is sniffed from `%PDF-` in the first kilobyte.

## Options

`PdfRenderOptions`: `Dpi` (150), `FirstPage`/`PageCount` (the selected pages are frames; the frame size is the page box, with rotation and user unit applied, times dpi/72, rounded to the nearest pixel), `Box` (crop box, limited to the media box), `Annotations` (view, print or none), `GenerateAppearances` (true), `Background` (opaque white; pass a transparent color for the page content alone), `Password`, `Limits` (`PdfRenderLimits`) and `DocumentLimits` (OnePDF's parsing and interpretation limits). Everything is documented on the types.

## How a page is drawn

- The page is interpreted once, at scale 1 in points, into OnePDF's display list. That list is kept with the loaded image together with the shared picture, shading and pattern caches (under a pixel budget).
- A page is drawn in fixed **128 × 128 pixel cells**. A cell replays only the part of the list under it. A region of any size is the cells it touches, cut out, so **a region is byte for byte the same pixels as that part of a whole render, at any degree of parallelism** (tests at degrees 1, 4 and 16, and with concurrent readers).
- The decoder asks for render-at-size: `Resize`/`Thumbnail` draw the page at the final size rather than drawing it large and reducing it.
- Text is drawn as glyph outlines (no hinting), one path per run, in the fill, stroke or clip mode of the text state. Standard-14 and substitute fonts come from `RecursiveAI.OnePdf.Fonts`.
- Images are reduced by area averaging when the page shows them smaller than they are, then sampled bilinearly through the placement. Stencil masks, soft masks, color-key masks and `/Decode` are applied by OnePDF.
- Color: gray and RGB directly; CMYK through the generic CMYK profile; ICC-based spaces through the color engine; CalGray, CalRGB and Lab through XYZ with Bradford adaptation.
- Shadings (types 1–7), tiling patterns (colored and uncolored), soft masks (alpha and luminosity), transparency groups, blend modes and constant alpha are supported.
- **Forms and markup.** A widget or markup annotation with an appearance stream draws it. One without, and every field of a form that sets `/NeedAppearances true`, draws the appearance OnePDF generates (text and choice values, check marks, radio buttons, highlights, lines and so on) unless `GenerateAppearances` is false. Under `NeedAppearances` OnePDF 1.1 regenerates text and choice fields; stale appearance streams of check boxes, radio buttons and push buttons are drawn as they are stored.

## Image codecs

- **DCT** is OneImage's own JPEG decoder, returning the components as stored (no Adobe inversion; OnePDF applies `/Decode`).
- **JBIG2** is OneImage's own decoder (see [jbig2.md](jbig2.md)). It returns the page in the standard's sense, 1 for black, and OnePDF applies the PDF inversion, `/Decode` and `/ImageMask`. `/JBIG2Globals` are passed separately. A **damaged stream throws in the decoder; the bridge treats that as an image that cannot be decoded, leaves it out and draws the rest of the page** (OnePDF records a diagnostic). A stream that exceeds a `Jbig2Limits` or `ImageLimits` bound is left out the same way.
- **JPEG 2000** has a hook (`PdfImageCodecs.Jpx`) and no decoder yet: such images are not drawn.

## Limits

`PdfRenderLimits` bounds what one file may cost, each checked against the value the file states, before the memory is allocated. They apply together with the `ImageLimits` of the load call and OnePDF's own limits.

| Limit | Default | Behavior beyond it |
|---|---|---|
| frame size (`ImageLimits`) | pixels per frame, dimension | the load fails with `ImageLimitException` before anything is drawn (a 14,400 pt page at 150 dpi, a huge dpi) |
| `MaxFileBytes` | 1 GiB | `ImageLimitException` before reading |
| `MaxImagePixels` | 2²⁶ | the image is skipped without decoding (declared size is read from the dictionary) |
| `MaxNesting` | 24 | groups, soft masks and pattern cells drawn at once; deeper content throws `ImageLimitException` |
| `MaxClipDepth` | 4,096 | `ImageLimitException` |
| `MaxPatternTileSide` | 2,048 | the cell is drawn at a lower resolution |
| `MaxPatternCopies` | 64 | further copies are not drawn into the tile |
| `MaxCachedPixels` | 2²⁶ | pictures beyond the budget are rebuilt when needed instead of kept |

A pattern that paints with itself is bounded by the nesting limit. The tests build one hostile file per row (`tests/OneImage.Pdf.Tests/LimitTests.cs`), including the allocation made before a refusal.

## Fuzzing

`fuzz/run.sh pdf <seconds>` loads a file, draws a resized whole page and two regions under small limits, seeded from `testdata/pdf` and any folders in `ONEIMAGE_PDF_SEEDS`. Seeded from the 307 files of OnePDF's test set and run for 10 minutes: 85,718 executions, no crashes, hangs or out-of-memory inputs.

## Known approximations

- A transparency group that is non-isolated and needs compositing (opacity below 1, a blend mode or a soft mask) is drawn as isolated; a non-isolated group with none of those is drawn straight onto its parent, which is exact. Knockout groups are drawn as non-knockout. Overprint is ignored.
- A soft mask's `/TR` transfer function is not applied (identity). OnePDF does not expose function evaluation to devices; see the API need below.
- Image edges are anti-aliased rather than snapped to the pixel grid.
- Text is unhinted and drawn with exact-area coverage, which looks lighter than PDFium's and heavier than MuPDF's.
- Glyphs with no font to draw them (for example CJK text in a font that is not embedded) are left out: the package carries only the standard Latin fonts.

### What the bridge would like from OnePDF

- **Function evaluation or a transfer table** for `PdfSoftMaskInfo.Transfer`: either a public `PdfFunction.Create(PdfObject)`/`Evaluate(ReadOnlySpan<double>, Span<double>)`, or a `ReadOnlyMemory<byte>` 256-entry table on `PdfSoftMaskInfo` (null for identity). With it, `/TR` is one table lookup per mask pixel.
- **Regeneration of check boxes, radio buttons and push buttons under `/NeedAppearances true`**, as PDFium does; they currently draw their stale stored appearance.

## Render gate

Method: page 1 of each of the 307 files in OnePDF's test set (generated, real-world, US government, PDF 2.0 examples and veraPDF files) is drawn at 150 dpi by this package and by three reference renderers used as black-box programs: PDFium (pypdfium2, with a form environment), MuPDF (`mutool draw`) and pdf.js (headless Chrome). A page **passes when its SSIM (luminance, 7 × 7 window) is at least 0.98 against at least two of the three**. Tools are in `tools/pdf-gate`: `make_manifest.py`, `render_oracles.py`, `compare.py`, `classify.py`, `triage.py`.

Result: **217 of 307 pages pass**.

| source | pages | pass |
|---|---|---|
| generated (Chrome, LibreOffice, TeX, encrypted, hand-written) | 68 | 63 |
| PDF 2.0 examples | 7 | 6 |
| veraPDF | 110 | 100 |
| US government (govdocs) | 30 | 14 |
| real-world (forms, papers, slides, scans, CJK, RTL, rotated) | 92 | 34 |

### The references disagree with each other

The same rule applied to each reference in turn (a page passes when it scores 0.98 against at least two of the *other three* renderers): ours 216, MuPDF 216, pdf.js 219, **PDFium 191** of 307. The bar is not reached by every reference itself, on text-heavy pages most of all. By content (`classify.py`; mean / min SSIM per pair):

| class | pages | pass | ours-PDFium | ours-MuPDF | ours-pdf.js | PDFium-MuPDF | PDFium-pdf.js | MuPDF-pdf.js |
|---|---|---|---|---|---|---|---|---|
| dense text (≥ 400 glyphs) | 103 | 43 | 0.945 / 0.783 | 0.968 / 0.856 | 0.977 / 0.891 | 0.948 / 0.788 | 0.938 / 0.786 | 0.970 / 0.843 |
| forms (widgets) | 21 | 13 | 0.958 / 0.868 | 0.981 / 0.935 | 0.981 / 0.885 | 0.958 / 0.857 | 0.951 / 0.860 | 0.976 / 0.885 |
| images (≥ 20% of the page) | 13 | 5 | 0.934 / 0.820 | 0.975 / 0.896 | 0.982 / 0.961 | 0.955 / 0.893 | 0.934 / 0.802 | 0.971 / 0.883 |
| transparency (groups, masks, alpha, blend) | 16 | 11 | 0.977 / 0.946 | 0.967 / 0.865 | 0.909 / 0.350 | 0.954 / 0.812 | 0.900 / 0.350 | 0.901 / 0.313 |
| vector (≥ 40 fills and strokes) | 1 | 1 | 0.996 / 0.996 | 0.999 / 0.999 | 0.998 / 0.998 | 0.997 / 0.997 | 0.996 / 0.996 | 0.998 / 0.998 |
| sparse (everything else) | 153 | 143 | 0.993 / 0.901 | 0.994 / 0.901 | 0.994 / 0.902 | 0.998 / 0.927 | 0.997 / 0.939 | 0.998 / 0.952 |
| all | 307 | 216 | 0.971 / 0.783 | 0.982 / 0.856 | 0.982 / 0.350 | 0.974 / 0.788 | 0.966 / 0.350 | 0.981 / 0.313 |

(The corpus has one page that is mostly line art; the vector row says little. The pass column counts a page that fewer than two references can draw as failing; `compare.py` counts it as passing, hence 216 against 217.)

### Why the failing pages fail

By the triage count 91 pages fail (the gate script counts 90: it lets a page pass when fewer than two references could draw it).

| cause | pages | note |
|---|---|---|
| the references do not agree: no two of them reach 0.98 with each other | 64 | includes the damaged file that has no page to draw (a typed error). A copy of any one reference would fail these too. Mostly text-weight and positioning differences; also generated markup that PDFium does not draw, and PDF/A transparency files on which pdf.js disagrees with both others |
| text drawing: the references agree, ours is 0.95–0.98 from the best of them | 19 | exact-area, unhinted glyph coverage against each renderer's own; spot checks of the difference images show edge pixels of glyphs, not missing or moved content |
| JPEG 2000 images (no decoder yet) | 8 | references agree; ours is blank where the picture is |

Bugs found by the triage and fixed: images reduced by area averaging came out white (one divisor; it affected every image shown smaller than stored, including the slides with banner images); JBIG2 scans drew blank (now drawn; scan pages went from 0.63–0.90 to 0.82–0.98).

## Speed

A whole page to `Rgba32` at 150 dpi from the file's bytes (parse, interpret, draw; BenchmarkDotNet `PdfPageBenchmarks`), against PDFium through pypdfium2 (`tools/pdf-gate/bench_pdfium.py`: open, render, copy to an array). One local run on a Ryzen 9 7950X3D (32 threads) with other work on the host (load 5–8), .NET 10.0.11, information only:

| page | ours | allocated | PDFium |
|---|---|---|---|
| text report, 1275 × 1651 | 8.4 ms | 22.5 MB | 3.8 ms |
| slides with a 2048 × 1152 JPEG and alpha mask, 1500 × 844 | 69 ms | 44 MB | 25 ms |
| form, 1042 × 1626 | 2.7 ms | 3.1 MB | 0.8 ms |
