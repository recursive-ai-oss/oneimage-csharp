# OneImage.Psd

Photoshop PSD and PSB support for [OneImage](https://github.com/recursive-ai-oss/oneimage-csharp): read the merged composite as an image, or load the whole layer tree into a OneImage layered document, edit it and save it again. Pure C# with no dependency beyond OneImage and .NET.

```sh
dotnet add package RecursiveAI.OneImage.Psd
```

```csharp
using OneImage;
using OneImage.Psd;

// The merged composite, like any other format (register the format first; it is not in the default configuration).
var load = new LoadOptions { Configuration = new ImageConfiguration().Add(PsdFormat.Instance) };
Image.Load("poster.psd", load).Resize(width: 800).Save("poster.webp");

// The layers.
using PsdFile file = PsdFile.Load("poster.psd");
foreach (var layer in file.Document.Root.Children)
    Console.WriteLine($"{layer.Name} {layer.BlendMode} {file.GetInfo(layer).Kind}");
file.Document.Render().Save("flattened.png");
file.Save("edited.psd");
```

See `docs/psd.md` in the repository for what is modelled, what is kept as opaque blocks and what is dropped.
