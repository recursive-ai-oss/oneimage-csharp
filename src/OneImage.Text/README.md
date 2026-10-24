# OneImage.Text

Fonts and text for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp): load TrueType, OpenType (CFF), TrueType Collection, WOFF and WOFF2 fonts, draw COLR color glyphs, measure and lay out text, and draw it on an `Image` or `Canvas` through the vector drawing layer. Pure C# with no dependency beyond OneImage and .NET.

```csharp
using OneImage;
using OneImage.Drawing;
using OneImage.Text;

var fonts = new FontCollection();
FontFamily family = fonts.Add("Roboto-Regular.ttf");
Font font = family.CreateFont(32);

Image img = Image.Load("photo.jpg")
    .DrawText("Hello, world", new TextOptions(font) { Origin = new(40, 60) }, Brush.Solid(new Rgba32(255, 255, 255, 255)));
```

Text is shaped with OpenType GSUB/GPOS (ligatures, kerning, marks) for Latin, Greek, Cyrillic, Arabic, Hebrew, the Indic scripts, Thai, Lao and the other complex scripts (with the Unicode bidirectional algorithm, so mixed left-to-right and right-to-left paragraphs lay out correctly) and wrapped with the Unicode line breaking algorithm. Vertical and bottom-to-top layout, TrueType hinting (opt-in) and COLR version 0 and 1 color glyphs are supported, as are rich-text runs, decorations, font fallback, text on a path and installed fonts. The [text guide](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.9.0/docs/text.md) covers the API, units, defaults and the differences from SixLabors.Fonts.
