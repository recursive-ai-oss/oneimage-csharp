# OneImage.Svg

SVG for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp): registers SVG (and gzip-compressed `.svgz`) as an image format that is drawn directly at the size you ask for. Pure C# with no dependency beyond OneImage, OneImage.Text and .NET.

```sh
dotnet add package RecursiveAI.OneImage.Svg
```

```csharp
using OneImage;
using OneImage.Svg;

var config = new ImageConfiguration().Add(SvgFormat.Instance);   // not in ImageConfiguration.Default, so it trims away when unused
var load = new LoadOptions { Configuration = config };

ImageInfo info = Image.Identify("logo.svg", load);                       // natural size, nothing drawn
Image.Load("logo.svg", load).Resize(width: 400).Save("logo-400.png");     // drawn at 400 px, not scaled up from the natural size
Image.Thumbnail("logo.svg", 64, upscale: true, loadOptions: load).Save("logo-64.webp");
```

The output is 8-bit sRGB RGBA, transparent where nothing is drawn. Only the shapes under a requested region are drawn for it, so large renders stream in tiles.

## What it draws

Static SVG 1.1 and SVG Tiny-style content, plus the parts of SVG 2 and CSS that documents rely on: shapes and paths with fills and strokes (caps, joins, dashes, markers), gradients and patterns, opacity, blend modes and isolated groups, transforms, nested `svg`, `use` and `symbol`, clip paths and masks, `style` sheets with the usual selectors, `switch`, `data:` images (any raster format OneImage reads, and nested SVG), the filter primitives and CSS filter functions, and text including `textPath`, bidirectional text, vertical writing modes and decorations.

Text needs fonts. Add font files through `SvgFontOptions` (TrueType, OpenType/CFF, collections, WOFF and WOFF2), or let it use the installed ones:

```csharp
var fonts = new SvgFontOptions { UseSystemFonts = false };
fonts.AddFont("Roboto-Regular.ttf");
var config = new ImageConfiguration().Add(new SvgFormat(fonts: fonts));
```

Text whose font cannot be found draws nothing. The default font size (CSS `medium`) is 16 pixels, as in browsers; `SvgFontOptions.DefaultFontSize` changes it.

## What it never does

External references (other files, URLs), `@font-face`, scripts, animation (SMIL) and `foreignObject` are never loaded or run, and the XML parser refuses DTDs and entity declarations, so a document cannot read files, reach the network or expand entities. A reference that cannot be resolved is treated as SVG's error handling says (the element is not drawn).

## Limits

Documents are untrusted input. `SvgLimits` bounds document size, element count and nesting, attributes and names, style sheet size and matching work, `use` fan-out and reference depth, layers, pattern tiles, embedded images, filter size, text and path segments. A document that exceeds one fails with `ImageLimitException`, before the memory it would need is allocated; the load call's `ImageLimits` (pixels, dimension, expansion ratio) apply on top. Pass your own with `new SvgFormat(new SvgLimits { ... })`.

The [SVG page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.4.0/docs/svg.md) in the repository covers the design, the limits table, how SVG features map to OneImage operations, and the conformance results.
