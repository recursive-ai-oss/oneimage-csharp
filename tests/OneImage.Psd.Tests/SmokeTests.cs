using OneImage.Layers;

namespace OneImage.Psd.Tests;

public class SmokeTests
{
    [Fact]
    public void SmallDocumentWritesReadsAndWritesAgainIdentically()
    {
        var doc = new Document(64, 48, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
        PixelLayer layer = doc.Add(doc.CreatePixelLayer("layer", 40, 30, 5, 6));
        layer.Pixels.Clear(new Rgba32(200, 30, 30, 255));
        layer.Opacity = 0.5f;
        layer.BlendMode = BlendMode.Multiply;
        doc.Add(doc.CreateFillLayer("tint", new Rgba32(0, 0, 255, 255))).Opacity = 0.1f;
        using var file = PsdFile.FromDocument(doc);
        byte[] first = Util.Save(file);
        using PsdFile loaded = PsdFile.Load(first);
        byte[] second = Util.Save(loaded);
        using PsdFile loaded2 = PsdFile.Load(second);
        Assert.Equal(2, loaded.Document.Root.Children.Count);

        // The document background is not part of the layers, so the merged image differs from the first file's; after that, writing is stable.
        Assert.True(second.AsSpan().SequenceEqual(Util.Save(loaded2)), "a written file does not write the same again");
        Image merged = Image.Load(first, new LoadOptions { Configuration = new ImageConfiguration().Add(PsdFormat.Instance) });
        Assert.True(merged.ToArray<Rgba32>().AsSpan().SequenceEqual(doc.Render().ToArray<Rgba32>()), "merged differs from render");
    }
}
