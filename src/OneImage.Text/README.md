# OneImage.Text

Fonts and text for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp): load TrueType, OpenType (CFF), TrueType Collection and WOFF fonts, measure and lay out text, and draw it on an `Image` or `Canvas` through the vector drawing layer. Pure C# with no dependency beyond OneImage and .NET.

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

See `docs/text.md` in the repository for the API shape, units and defaults.
