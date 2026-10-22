# OneImage.Svg

SVG support for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp): registers SVG (and gzip-compressed `.svgz`) as an image format. Pure C# with no dependency beyond OneImage, OneImage.Text and .NET.

```csharp
using OneImage;
using OneImage.Svg;

var config = new ImageConfiguration().Add(SvgFormat.Instance);
ImageInfo info = Image.Identify("logo.svg", new LoadOptions { Configuration = config });   // natural size, no rendering
```

This package is under construction: files are detected and their natural size is read, but rendering is not available yet.
External references (other files, URLs), scripts, animation and `foreignObject` are never loaded. The XML parser refuses DTDs and entity declarations.
See `docs/svg.md` in the repository for the design.
