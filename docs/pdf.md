# PDF

`RecursiveAI.OneImage.Pdf` (namespace `OneImage.Pdf`) draws PDF pages as an ordinary image format. It parses and interprets with [OnePDF](https://www.nuget.org/packages/RecursiveAI.OnePdf) (`RecursiveAI.OnePdf` and `RecursiveAI.OnePdf.Fonts`, 1.2.0 or later) and draws on OneImage's drawing engine, so a page is an `Image` like any other: it can be resized, cropped, converted and saved, and only the part of the page that is asked for is drawn.

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
- **JPEG 2000** is OneImage's own decoder (see [jpx.md](jpx.md)), registered at `PdfImageCodecs.Jpx`. Colour channels come out in the colour space the file states (sRGB, gray, CMYK; sYCC is converted to RGB; ICC-described RGB, gray and CMYK are converted to sRGB or gray, because a profile cannot be handed to OnePDF) and the opacity channel, if any, comes last, so OnePDF applies `/SMaskInData`, the declared `/ColorSpace` and masks. ISO 32000-2 7.4.9 has `/Decode` ignored for JPX images that are not masks, and OnePDF follows it. An image of more than 2^26 pixels is decoded at a reduced resolution. A file that is damaged inside its tile data, or beyond a limit, gives no image: it is left out and the page draws. The decoder is not told how large the page shows the picture, so smaller placements still decode at full size (only the pixel budget reduces).

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

## Known approximations and limitations

- Transparency groups: a non-isolated group that needs compositing (opacity below 1, a blend mode or a soft mask) is painted on a copy of its backdrop with its own coverage tracked apart, so blend modes inside it see the page and the backdrop is not counted twice (11.4.5, 11.4.8); an opaque backdrop is assumed, a translucent one is approximated. A knockout group (11.4.6) replaces what is under each object instead of accumulating; blend modes of objects inside a knockout group are not applied. A non-isolated group with none of these needs is drawn straight onto its parent, which is exact. Overprint is ignored.
- **Knockout of text and of fill-and-stroke.** `B`, `B*`, `b` and `b*` (11.7.4.4) arrive as one device call; with a translucent stroke the fill is painted without the stroke's area, so the stroke replaces it instead of compositing over it. Text knockout (9.3.8, `/TK`, true by default) is applied to the glyphs of Type 3 fonts, which are drawn as content: while translucent glyph procedures follow each other they share one knockout layer. Outline glyphs of one string are already one shape; overlapping translucent glyphs of *different* strings of a text object (separate `Tj`/`TJ` elements) still accumulate, because the device is not told where a text object begins and ends.
- Image edges are anti-aliased rather than snapped to the pixel grid. A picture shown larger than its samples is drawn with square samples unless it sets `/Interpolate true` (8.9.5.3: interpolation is off by default); the references often smooth it anyway. A reduced picture is area-averaged, then sampled bilinearly.
- Text is unhinted and drawn with exact-area coverage, which looks lighter than PDFium's and heavier than MuPDF's.

### Known limitations

- **Soft-mask `/TR` is treated as identity.** OnePDF does not expose function evaluation to devices. Needed from OnePDF: either a public `PdfFunction.Create(PdfObject)`/`Evaluate(ReadOnlySpan<double>, Span<double>)`, or a 256-entry transfer table on `PdfSoftMaskInfo` (null for identity).
- **Text in a non-embedded CJK font draws blank.** The package carries only the standard Latin fonts; there is no CJK fallback font yet.
- **Check boxes, radio buttons and push buttons under `/NeedAppearances true` keep their stored appearance.** OnePDF regenerates text and choice fields only.

## Render gate

SSIM against other renderers is a signal, not the bar: three renderers disagree with each other on many pages, and none of them is the standard. The standard is ISO 32000-2, so the gate finds the pages worth a look and a person judges each against the specification.

**Method.** Page 1 of each of the 307 files in OnePDF's test set (generated, real-world, US government, PDF 2.0 examples and veraPDF files) is drawn at 150 dpi by this package and by three reference renderers used as black-box programs: PDFium (pypdfium2, with a form environment), MuPDF (`mutool draw`) and pdf.js (headless Chrome). SSIM is computed on luminance with a 7 x 7 window. `tools/pdf-gate/gate.py` flags a page when any of these holds:

1. our SSIM is below 0.98 to at least two references **and** our SSIM to the closest reference is below the best SSIM between any two references on that page (we are further from the references than they are from each other);
2. our SSIM to the closest reference is below 0.90 (floor, a gross-error alarm);
3. we did not draw the page, or no reference drew it;
4. only one reference drew it and we are below 0.98 to it.

Every flagged page gets a row in `tools/pdf-gate/verdicts.csv`: the page, a verdict, the ISO 32000-2 section, the reason and the renderers that deviate. A flagged page without a row is reported as `unreviewed`. The verdicts are:

- **ours-correct**: we follow the specification and the named renderer deviates.
- **ours-wrong**: we depart from the specification. It is fixed or listed under the known limitations.
- **ambiguous**: the specification allows both results. Glyph-edge anti-aliasing is implementation-defined (10.7, scan conversion), so a page that differs only there is `ambiguous`; it counts as `ours-wrong` only if glyph shape, position, weight or coverage departs from what the specification defines. An edge-only page is recognised by: embedded outlines, the same ink within 5%, no offset (best sub-pixel shift 0), and a blurred SSIM (Gaussian sigma 2) of at least 0.99 to the closest reference.

The unflagged pages stay unflagged only while the rule holds, so the gate is re-run after any renderer change and new flags get verdicts. A separate suite of generated PDFs with analytically known pixels (`tests/OneImage.Pdf.Tests/SpecTruth`) checks the specification directly, without any reference renderer.

### Result: 26 of 307 pages flagged

| verdict | pages |
|---|---|
| ambiguous | 24 |
| ours-wrong | 0 |
| ours-correct | 2 |

All 26 have a verdict. By cause:

- **ambiguous, 10.7 scan conversion (edge anti-aliasing only), 10 pages:** cjk-03, cjk-05, math-02, math-04, math-08, multi-column-01, multi-column-02, old-08, report-08, table-02. No renderer is singled out: each differs from the others at glyph edges, PDFium furthest.
- **ambiguous, 10.7 plus 9.6.2.2 and 9.8 (edge anti-aliasing plus non-embedded fonts whose substitutes differ), 7 pages:** cjk-01, cjk-06, rotated-08, rtl-04, rtl-05, table-09, table-05 (PDFium draws lighter, about 83% of our ink).
- **ambiguous, 10.7 and 8.4.3.2 (zero-width rules are drawn as the thinnest line the device can), 1 page:** rotated-07; MuPDF draws the hairlines lighter.
- **ambiguous, 9.6.2.2 and 9.8 (non-embedded Times-like fonts, each renderer substitutes differently), 2 pages:** multi-column-08 and report-10. report-10 is the only page below the 0.90 floor (0.891).
- **ambiguous, 8.9.5.3 (reduction filter of a bilevel scan), 1 page:** 378647; PDFium and MuPDF agree exactly, ours matches pdf.js.
- **ambiguous, 12.5.5 (markup annotations without appearance streams), 2 pages:** raw-appearance-markup and raw-appearance-markup-needap; PDFium omits the shapes.
- **ambiguous, 7.5.6 (damaged file), 1 page:** raw-broken-truncated-stream; all four renderers refuse it.
- **ours-correct, 8.9.5.3 (no interpolation by default), 1 page:** slides-07-zen14027747. A 1536 x 864 noise texture with `/Interpolate false` is shown about 1.3 times enlarged; we draw square samples as the specification asks, and PDFium, MuPDF and pdf.js all smooth it.
- **ours-correct, 7.4.9 (`/Decode` is ignored for JPEG 2000 images), 1 page:** 966677. The banner is one JPEG 2000 image with `/Decode [1 0]` in a Separation color space; we use the samples as stored, as the specification says, and PDFium, MuPDF and pdf.js all apply the array and draw the banner with inverted tones.

By source (flagged / pages; the last column is the strict count, information below):

| source | pages | flagged | 0.98 to two references |
|---|---|---|---|
| generated-chrome | 18 | 0 | 18 |
| generated-enc | 12 | 0 | 12 |
| generated-lo | 6 | 0 | 6 |
| generated-raw | 31 | 3 | 25 |
| generated-tex | 1 | 0 | 1 |
| govdocs | 30 | 2 | 14 |
| pdf20examples | 7 | 0 | 7 |
| realworld | 92 | 21 | 33 |
| verapdf | 110 | 0 | 107 |
| all | 307 | 26 | 223 |

### SSIM tables (information only)

The strict rule (SSIM at least 0.98 to at least two of the other three renderers) applied to each renderer in turn, pages out of the total. It is not the bar; it shows that no renderer, PDFium least of all, meets it everywhere:

| source | pages | ours | PDFium | MuPDF | pdf.js |
|---|---|---|---|---|---|
| generated-chrome | 18 | 18 | 18 | 18 | 18 |
| generated-enc | 12 | 12 | 12 | 3 | 12 |
| generated-lo | 6 | 6 | 5 | 4 | 5 |
| generated-raw | 31 | 25 | 23 | 25 | 25 |
| generated-tex | 1 | 1 | 1 | 1 | 1 |
| govdocs | 30 | 14 | 6 | 12 | 13 |
| pdf20examples | 7 | 7 | 6 | 7 | 6 |
| realworld | 92 | 33 | 12 | 38 | 31 |
| verapdf | 110 | 107 | 107 | 107 | 107 |
| all | 307 | 223 | 190 | 215 | 218 |

By content class (`classify.py`), mean / min SSIM per pair of renderers:

| class | pages | flagged | ours-PDFium | ours-MuPDF | ours-pdf.js | PDFium-MuPDF | PDFium-pdf.js | MuPDF-pdf.js |
|---|---|---|---|---|---|---|---|---|
| dense text | 103 | 21 | 0.945 / 0.783 | 0.968 / 0.856 | 0.977 / 0.891 | 0.948 / 0.788 | 0.938 / 0.786 | 0.970 / 0.843 |
| forms | 21 | 0 | 0.958 / 0.868 | 0.981 / 0.935 | 0.981 / 0.885 | 0.958 / 0.857 | 0.951 / 0.860 | 0.976 / 0.885 |
| images | 13 | 2 | 0.926 / 0.787 | 0.965 / 0.877 | 0.975 / 0.895 | 0.955 / 0.893 | 0.934 / 0.802 | 0.971 / 0.883 |
| sparse | 153 | 1 | 0.998 / 0.939 | 0.998 / 0.982 | 0.998 / 0.952 | 0.998 / 0.927 | 0.997 / 0.939 | 0.998 / 0.952 |
| transparency | 16 | 2 | 0.976 / 0.946 | 0.967 / 0.865 | 0.909 / 0.349 | 0.954 / 0.812 | 0.900 / 0.350 | 0.901 / 0.313 |
| vector | 1 | 0 | 0.996 / 0.996 | 0.999 / 0.999 | 0.998 / 0.998 | 0.997 / 0.997 | 0.996 / 0.996 | 0.998 / 0.998 |
| all | 307 | 26 | 0.973 / 0.783 | 0.984 / 0.856 | 0.984 / 0.349 | 0.974 / 0.788 | 0.966 / 0.350 | 0.981 / 0.313 |

On 67 pages no two references agree at 0.98, so a copy of any one reference would fail the strict rule there. The vector row has a single page and says little.

Tools are in `tools/pdf-gate`: `make_manifest.py`, `render_oracles.py`, `classify.py` and `gate.py`.

## Speed

A whole page to `Rgba32` at 150 dpi from the file's bytes (parse, interpret, draw; BenchmarkDotNet `PdfPageBenchmarks`), against PDFium through pypdfium2 (`tools/pdf-gate/bench_pdfium.py`: open, render, copy to an array). One local run on a Ryzen 9 7950X3D (32 threads) with other work on the host (load 5–8), .NET 10.0.11, information only:

| page | ours | allocated | PDFium |
|---|---|---|---|
| text report, 1275 × 1651 | 8.4 ms | 22.5 MB | 3.8 ms |
| slides with a 2048 × 1152 JPEG and alpha mask, 1500 × 844 | 69 ms | 44 MB | 25 ms |
| form, 1042 × 1626 | 2.7 ms | 3.1 MB | 0.8 ms |
