# RecursiveAI.OneImage.Metafile

Windows metafile support for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/README.md).

- **EMF as an image format.** `new ImageConfiguration().Add(MetafileFormat.Instance)` lets `Image.Load` read `.emf` files. The picture is drawn at the size you ask for (`Resize`/`Thumbnail` rasterize at the final size instead of scaling pixels), with the drawing engine's anti-aliasing.
- **A drawing-command stream.** `MetafileDocument.Parse(bytes)` resolves every record (mapping modes, world transforms, objects, paths, regions, raster operations) into a short list of device-independent commands: fill a path, stroke a path, draw a bitmap, draw text, change the clip. Walk it with a `DrawingCommandVisitor` to convert a picture to another vector format or to your own renderer.
- **Safe on hostile files.** Sizes are checked against `MetafileLimits` before anything is allocated, and nothing in a file can run code, read other files or reach the network. Anything not interpreted is reported as a diagnostic instead of being dropped silently.

```csharp
var config = new ImageConfiguration().Add(MetafileFormat.Instance);
Image.Load("drawing.emf", new LoadOptions { Configuration = config }).Resize(width: 800).Save("drawing.png");

MetafileDocument doc = MetafileDocument.Load("drawing.emf");
doc.Play(new MyVisitor());
```

Text needs fonts: pass a `MetafileFontOptions` (your own `FontCollection`, system fonts, substitutions) to `MetafileFormat`. The package depends on `RecursiveAI.OneImage` and `RecursiveAI.OneImage.Text` at exactly the same version.

See [docs/metafile.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/docs/metafile.md) for the command model, the record coverage and the known gaps.

Licensed under MIT OR Apache-2.0.
