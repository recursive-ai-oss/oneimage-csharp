# OneImage.Svg

SVG support for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp): registers SVG (and gzip-compressed `.svgz`) as an image format. Pure C# with no dependency beyond OneImage, OneImage.Text and .NET.

```csharp
using OneImage;
using OneImage.Svg;

var config = new ImageConfiguration().Add(SvgFormat.Instance);
var load = new LoadOptions { Configuration = config };
ImageInfo info = Image.Identify("logo.svg", load);                       // natural size, no rendering
Image.Load("logo.svg", load).Resize(width: 400).Save("logo-400.png");     // drawn at 400 px, not scaled up from the natural size
Image.Thumbnail("logo.svg", 64, upscale: true, loadOptions: load).Save("logo-64.webp");
```

This package is under construction and not published yet. It draws shapes and paths with solid fills and strokes (width, caps, joins, dashes), opacity, transforms, `viewBox`, nested `svg`, `use` and `symbol` at any size, one region at a time. Gradients, patterns, markers, clipping, masks, filters, text, images and style sheets are still to come.
External references (other files, URLs), scripts, animation and `foreignObject` are never loaded. The XML parser refuses DTDs and entity declarations.
See `docs/svg.md` in the repository for the design.
