# OneImage.Pdf

PDF page rendering for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp): a PDF is an ordinary image format whose frames are pages. Parsing and interpretation come from [OnePDF](https://www.nuget.org/packages/RecursiveAI.OnePdf); drawing is OneImage's own engine, in safe C# with no native code.

```sh
dotnet add package RecursiveAI.OneImage.Pdf
```

This installs `RecursiveAI.OneImage` and OnePDF (`RecursiveAI.OnePdf` and `RecursiveAI.OnePdf.Fonts`, 1.2.0 or later). The package depends on the exact core version it was built with.

```csharp
using OneImage;
using OneImage.Pdf;

// Register the format first; it is not in the default configuration.
var load = new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = 150 })) };

var pdf = Image.Load("report.pdf", load);                // every page is a frame
pdf.Frames[0].ToImage().Save("page1.png");                      // the first page
pdf.Frames[0].ToImage().Resize(width: 400).Save("thumb.webp");  // drawn at 400 px, not drawn large and reduced
// Saving `pdf` itself writes every page, as an animation or a multi-page file where the format allows.
```

- A page is drawn in 128 px cells, on demand: a region of a page is byte for byte the same pixels as that part of the whole page, at any degree of parallelism.
- Fonts, images (JPEG, JPEG 2000 and JBIG2 decoded by OneImage), shadings, tiling patterns, transparency groups, soft masks and blend modes are drawn. Form fields and markup annotations without an appearance stream are drawn from the appearance OnePDF generates.
- Hostile files are bounded (`PdfRenderLimits`, `ImageLimits` and OnePDF's own limits) before memory is allocated; only typed exceptions leave the load and draw calls.

Known limitations:

- A soft mask's `/TR` transfer function is treated as the identity.
- Text in a non-embedded CJK font draws blank (there is no CJK fallback font yet).
- Check boxes, radio buttons and push buttons under `/NeedAppearances true` keep their stored appearance.
- Overprint is ignored, and blend modes inside a knockout group are not applied.

See the [PDF page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.8.0/docs/pdf.md) for the options, limits, the render-gate method and verdicts (PDFium, MuPDF and pdf.js are used as a signal against ISO 32000-2), and the full list of approximations.

Licensed under MIT or Apache-2.0.
