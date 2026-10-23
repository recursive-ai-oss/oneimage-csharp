# OneImage.Pdf

PDF page rendering for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp): a PDF is an ordinary image format whose frames are pages. Parsing and interpretation come from [OnePDF](https://www.nuget.org/packages/RecursiveAI.OnePdf); drawing is OneImage's own engine, in safe C# with no native code.

```sh
dotnet add package RecursiveAI.OneImage.Pdf
```

```csharp
using OneImage;
using OneImage.Pdf;

// Register the format first; it is not in the default configuration.
var load = new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = 150 })) };

Image.Load("report.pdf", load).Save("page1.png");                       // every page is a frame
Image.Load("report.pdf", load).Resize(width: 400).Save("thumb.webp");  // drawn at 400 px, not drawn large and reduced
```

- A page is drawn in 128 px cells, on demand: a region of a page is byte for byte the same pixels as that part of the whole page, at any degree of parallelism.
- Fonts, images (JPEG and JBIG2 decoded by OneImage), shadings, tiling patterns, transparency groups, soft masks and blend modes are drawn. Form fields and markup annotations without an appearance stream are drawn from the appearance OnePDF generates.
- Hostile files are bounded (`PdfRenderLimits`, `ImageLimits` and OnePDF's own limits) before memory is allocated; only typed exceptions leave the load and draw calls.
- JPEG 2000 images are not drawn yet.

See the [PDF page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/docs/pdf.md) for the options, limits, render-gate method and verdicts (PDFium, MuPDF and pdf.js as a signal), and the known limitations.

Licensed under MIT or Apache-2.0.
