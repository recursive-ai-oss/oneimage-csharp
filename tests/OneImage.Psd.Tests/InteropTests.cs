using ImageMagick;
using OneImage.Drawing;
using OneImage.Layers;

namespace OneImage.Psd.Tests;

/// <summary>A PSD written here is read by ImageMagick (a separate implementation, run as a black box) and its merged image is compared with ours.</summary>
public class InteropTests
{
    private static Rgba32[] MagickPixels(byte[] file, out int width, out int height)
    {
        using var img = new MagickImage(file, new MagickReadSettings { Format = MagickFormat.Psd });
        width = (int)img.Width;
        height = (int)img.Height;
        using IPixelCollection<byte> px = img.GetPixelsUnsafe();
        byte[] raw = px.ToByteArray(PixelMapping.RGBA)!;
        var result = new Rgba32[width * height];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new Rgba32(raw[i * 4], raw[i * 4 + 1], raw[i * 4 + 2], raw[i * 4 + 3]);
        }

        return result;
    }

    [Theory]
    [InlineData(PsdCompression.Rle, false)]
    [InlineData(PsdCompression.Raw, false)]
    [InlineData(PsdCompression.Rle, true)]
    public void ImageMagickReadsTheMergedImageWeWrote(PsdCompression compression, bool psb)
    {
        using PsdFile f = TestDocuments.Build(PixelFormat.Rgba8);
        f.Document.Background = new Rgba32(250, 250, 250, 255);
        byte[] file = Util.Save(f, new PsdWriteOptions { Compression = compression, Large = psb });

        Rgba32[] theirs = MagickPixels(file, out int w, out int h);
        Rgba32[] ours = f.Document.Render().ToArray<Rgba32>();
        Assert.Equal(f.Document.Width, w);
        Assert.Equal(f.Document.Height, h);
        Assert.True(ours.AsSpan().SequenceEqual(theirs), "the merged image read by ImageMagick differs from the render");
    }

    /// <summary>A document with every kind of style and fill the model can write, as it is saved by the authoring paths.</summary>
    internal static PsdFile AuthoredDocument(bool fills = true)
    {
        var doc = new Document(160, 110, new DocumentOptions { Format = PixelFormat.Rgba8 });
        var file = PsdFile.FromDocument(doc);
        doc.Background = new Rgba32(255, 255, 255, 255);
        PixelLayer shape = doc.Add(doc.CreatePixelLayer("styled shape", 100, 70, 30, 20));
        shape.Pixels.Fill(new Rgba32(220, 90, 40, 255), Drawing.Path.Circle(50, 35, 30));
        shape.Style = new LayerStyle(
        [
            new DropShadowEffect { Distance = 6, Size = 5, Opacity = 0.6f, Angle = 120 },
            new OuterGlowEffect { Color = new Rgba32(255, 240, 120, 255), Size = 8, Opacity = 0.7f },
            new ColorOverlayEffect { Color = new Rgba32(30, 120, 240, 255), Opacity = 0.3f },
            new InnerGlowEffect { Size = 6, Color = new Rgba32(255, 255, 255, 255) },
            new InnerShadowEffect { Distance = 3, Size = 4 },
            new StrokeEffect { Size = 3, Position = StrokePosition.Outside, Color = new Rgba32(20, 20, 20, 255) },
        ]);
        if (!fills)
        {
            return file;
        }

        FillLayer grad = doc.Add(doc.CreateFillLayer("gradient", Drawing.Brush.LinearGradient(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(160, 110), [new Drawing.ColorStop(0, new Rgba32(255, 0, 80, 255)), new Drawing.ColorStop(1, new Rgba32(0, 80, 255, 255))])));
        grad.Opacity = 102 / 255f;
        grad.BlendMode = BlendMode.Overlay;
        FillLayer rad = doc.Add(doc.CreateFillLayer("radial", Drawing.Brush.RadialGradient(new System.Numerics.Vector2(80, 55), 60, [new Drawing.ColorStop(0, new Rgba32(255, 255, 255, 255)), new Drawing.ColorStop(1, new Rgba32(255, 255, 255, 0))])));
        rad.Opacity = 153 / 255f;
        FillLayer pat = doc.Add(doc.CreateFillLayer("hatch", Drawing.Brush.Hatch(Drawing.HatchStyle.ForwardDiagonal, new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255))));
        pat.Opacity = 51 / 255f;
        pat.BlendMode = BlendMode.Multiply;
        doc.Add(doc.CreateFillLayer("raster", Drawing.Brush.LinearGradient(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(20, 0), [new Drawing.ColorStop(0, new Rgba32(0, 0, 0, 255)), new Drawing.ColorStop(1, new Rgba32(255, 255, 255, 255))], Drawing.GradientRepetition.Reflect))).Opacity = 38 / 255f;
        return file;
    }

    /// <summary>Writes the authored document as a sample file for other readers when ONEIMAGE_PSD_AUTHORED_OUT names a path.</summary>
    [Fact]
    public void WriteAuthoredSample()
    {
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_PSD_AUTHORED_OUT");
        if (path != null)
        {
            using PsdFile f = AuthoredDocument();
            File.WriteAllBytes(path, Util.Save(f));
            using PsdFile styled = AuthoredDocument(fills: false);
            File.WriteAllBytes(path + ".styles.psd", Util.Save(styled));

            // One file per fill kind, drawn at full opacity.
            var fills = new (string Name, Brush Brush)[]
            {
                ("fill-linear", Brush.LinearGradient(new System.Numerics.Vector2(10, 5), new System.Numerics.Vector2(150, 100), [new ColorStop(0, new Rgba32(255, 0, 80, 255)), new ColorStop(0.5f, new Rgba32(250, 220, 40, 255)), new ColorStop(1, new Rgba32(0, 80, 255, 255))])),
                ("fill-horizontal", Brush.LinearGradient(new System.Numerics.Vector2(20, 0), new System.Numerics.Vector2(130, 0), [new ColorStop(0, new Rgba32(255, 0, 80, 255)), new ColorStop(0.5f, new Rgba32(250, 220, 40, 255)), new ColorStop(1, new Rgba32(0, 80, 255, 255))])),
                ("fill-vertical", Brush.LinearGradient(new System.Numerics.Vector2(0, 90), new System.Numerics.Vector2(0, 10), [new ColorStop(0, new Rgba32(255, 0, 80, 255)), new ColorStop(1, new Rgba32(0, 80, 255, 255))])),
                ("fill-radial", Brush.RadialGradient(new System.Numerics.Vector2(80, 55), 60, [new ColorStop(0, new Rgba32(255, 255, 255, 255)), new ColorStop(1, new Rgba32(20, 20, 160, 255))])),
                ("fill-hatch", Brush.Hatch(HatchStyle.ForwardDiagonal, new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255))),
            };
            foreach ((string name, Brush brush) in fills)
            {
                var d = new Document(160, 110);
                using var pf = PsdFile.FromDocument(d);
                d.Add(d.CreateFillLayer(name, brush));
                File.WriteAllBytes(path + "." + name + ".psd", Util.Save(pf));
            }

            // One file per effect kind, for readers that draw effects.
            using PsdFile whole = AuthoredDocument(fills: false);
            LayerStyle all = whole.Document.Root.Children[0].Style!;
            using (PsdFile none = AuthoredDocument(fills: false))
            {
                none.Document.Root.Children[0].Style = null;
                File.WriteAllBytes(path + ".none.psd", Util.Save(none));
            }

            foreach (LayerEffect e in all.Effects)
            {
                using PsdFile one = AuthoredDocument(fills: false);
                one.Document.Root.Children[0].Style = new LayerStyle([e]);
                File.WriteAllBytes(path + "." + e.Name + ".psd", Util.Save(one));
            }
        }
    }

    [Fact]
    public void ImageMagickReadsAuthoredStylesAndFills()
    {
        using PsdFile f = AuthoredDocument();
        byte[] file = Util.Save(f);
        Rgba32[] theirs = MagickPixels(file, out int w, out int h);
        Rgba32[] ours = f.Document.Render().ToArray<Rgba32>();
        Assert.Equal(f.Document.Width, w);
        Assert.Equal(f.Document.Height, h);
        Assert.True(ours.AsSpan().SequenceEqual(theirs), "the merged image read by ImageMagick differs from the render");

        // The layers are read too: each fill layer arrives with the raster that is stored beside its settings.
        using var layers = new MagickImageCollection(file, new MagickReadSettings { Format = MagickFormat.Psd });
        string report = Environment.GetEnvironmentVariable("ONEIMAGE_PSD_INTEROP_LAYERS") ?? "";
        if (report.Length > 0)
        {
            File.WriteAllText(report, string.Join(Environment.NewLine, layers.Select(static l => $"{l.Width}x{l.Height} page={l.Page.X},{l.Page.Y} label={l.Label}")));
        }

        Assert.True(layers.Count >= 5, $"{layers.Count} images");

        // The stored raster of a gradient fill is the gradient drawn over the document.
        MagickImage gradient = (MagickImage)layers.First(static l => l.Label == "gradient");
        using IPixelCollection<byte> gp = gradient.GetPixelsUnsafe();
        byte[] raw = gp.ToByteArray(PixelMapping.RGBA)!;
        using var drawn = new Canvas(w, h, PixelFormat.Rgba8);
        drawn.Fill(((FillLayer)f.Document.Root.Children[1]).Brush, Drawing.Path.Rectangle(0, 0, w, h), antialias: false, composition: AlphaComposition.Src);
        Rgba32[] expected = drawn.Snapshot().ToArray<Rgba32>();
        for (int i = 0; i < expected.Length; i++)
        {
            // ImageMagick folds the layer's opacity (102 of 255) into the alpha it reports.
            Assert.Equal(new Rgba32(expected[i].R, expected[i].G, expected[i].B, 102), new Rgba32(raw[i * 4], raw[i * 4 + 1], raw[i * 4 + 2], raw[i * 4 + 3]));
        }
    }

    [Fact]
    public void ImageMagickAndWeReadTheStoredCompositeOfCorpusFilesAlike()
    {
        string? report = Environment.GetEnvironmentVariable("ONEIMAGE_PSD_INTEROP_REPORT");
        if (Util.CorpusDir == null || report == null)
        {
            return;
        }

        var lines = new List<string>();
        int same = 0;
        int compared = 0;
        foreach (string path in Util.CorpusFiles().Where(static p => new FileInfo(p).Length < 3_000_000))
        {
            string rel = System.IO.Path.GetRelativePath(Util.CorpusDir, path);
            try
            {
                byte[] file = File.ReadAllBytes(path);
                Rgba32[] theirs = MagickPixels(file, out int w, out int h);
                Image img = Util.LoadComposite(file);
                if (img.Width != w || img.Height != h)
                {
                    lines.Add($"{rel}\tsize");
                    continue;
                }

                Rgba32[] ours = img.ToArray<Rgba32>();
                compared++;
                long diff = 0;
                int max = 0;
                for (int i = 0; i < ours.Length; i++)
                {
                    Rgba32 a = ours[i];
                    Rgba32 b = theirs[i];
                    int d = Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));
                    if (d != 0)
                    {
                        diff++;
                        max = Math.Max(max, d);
                    }
                }

                if (diff == 0)
                {
                    same++;
                }
                else
                {
                    lines.Add($"{rel}\t{img.Format}\tdiffering={diff}\tmax={max}");
                }
            }
            catch (Exception ex) when (ex is ImageException or MagickException)
            {
                lines.Add($"{rel}\tskipped\t{ex.GetType().Name}");
            }
        }

        File.WriteAllText(report, $"compared {compared}, identical {same}\n" + string.Join('\n', lines));
    }
}
