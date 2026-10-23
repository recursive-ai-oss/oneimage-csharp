using OneImage.Layers;

namespace OneImage.Psd.Tests;

/// <summary>Layer styles that are created or edited in the model are written as effects blocks and read back.</summary>
public class StyleWriteTests
{
    private static string Style(Layer l) =>
        string.Join('\n', l.Describe().Parameters.Where(static p => p.Name.StartsWith("style", StringComparison.Ordinal)).Select(static p => p.Name + "=" + p.Value));

    private static (PsdFile File, PixelLayer Layer) NewDocument(LayerStyle? style)
    {
        var doc = new Document(120, 90, new DocumentOptions { Format = PixelFormat.Rgba8 });
        var file = PsdFile.FromDocument(doc);
        PixelLayer l = doc.Add(doc.CreatePixelLayer("shape", 70, 50, 25, 20));
        l.Pixels.Fill(new Rgba32(220, 90, 40, 255), Drawing.Path.Circle(35, 25, 22));
        l.Style = style;
        return (file, l);
    }

    // Reading gives the effects back grouped by kind in a fixed order (shadows, outer glow, overlay, satin, inner glow, inner shadow, stroke).
    private static LayerStyle EveryKind() => new(
    [
        new DropShadowEffect { Blend = BlendMode.Multiply, Color = new Rgba32(10, 20, 30, 255), Opacity = 0.65f, Angle = 135.5f, Distance = 7.25f, Spread = 0.3f, Size = 6, Contour = new EffectContour([new ContourPoint(0, 0), new ContourPoint(0.3f, 0.7f), new ContourPoint(1, 1)]), KnockedOut = false },
        new DropShadowEffect { Color = new Rgba32(200, 0, 0, 255), Distance = 2, Size = 1, Enabled = false },
        new OuterGlowEffect { Color = new Rgba32(250, 240, 120, 255), Opacity = 0.8f, Spread = 0.1f, Size = 9, Range = 0.35f, Contour = new EffectContour([new ContourPoint(0, 0.2f), new ContourPoint(0.5f, 0.9f), new ContourPoint(1, 0)]) },
        new ColorOverlayEffect { Color = new Rgba32(30, 120, 240, 255), Blend = BlendMode.Hue, Opacity = 0.25f },
        new SatinEffect { Color = new Rgba32(0, 0, 40, 255), Angle = 19, Distance = 11, Size = 14, Inverted = false, Opacity = 0.5f },
        new InnerGlowEffect { Color = new Rgba32(255, 255, 255, 255), Source = GlowSource.Center, Choke = 0.2f, Size = 7, Range = 0.6f },
        new InnerShadowEffect { Blend = BlendMode.Overlay, Opacity = 0.4f, Angle = -45, Distance = 3, Choke = 0.15f, Size = 4 },
        new StrokeEffect { Color = new Rgba32(0, 0, 0, 255), Size = 3, Position = StrokePosition.Inside, Opacity = 0.9f },
    ]);

    [Fact]
    public void EveryEffectKindSurvivesASaveAndLoad()
    {
        (PsdFile file, PixelLayer layer) = NewDocument(EveryKind());
        using (file)
        {
            byte[] first = Util.Save(file);
            using PsdFile loaded = PsdFile.Load(first);
            Layer back = loaded.Document.Root.Children[0];
            Assert.Equal(Style(layer), Style(back));
            Assert.Equal(file.Document.Render().ToArray<Rgba32>(), loaded.Document.Render().ToArray<Rgba32>());

            // The loaded style was read from the block we wrote, so it is written back as it is.
            byte[] second = Util.Save(loaded);
            Assert.True(first.AsSpan().SequenceEqual(second), "second write differs from the first");
        }
    }

    [Fact]
    public void SeveralEffectsOfAKindUseTheListForm()
    {
        (PsdFile file, _) = NewDocument(new LayerStyle([new DropShadowEffect { Distance = 1 }, new DropShadowEffect { Distance = 9, Color = new Rgba32(255, 0, 0, 255) }, new StrokeEffect { Size = 2 }]));
        using (file)
        {
            byte[] data = FxBlock(Util.Save(file));
            DescNode root = DescNode.Parse(data.AsSpan(8), PsdLimits.Default);
            Assert.Equal(2, root.Get("dropShadowMulti")!.List.Count);
            Assert.Null(root.Get("DrSh"));
            Assert.NotNull(root.Get("FrFX"));
            Assert.True(root.Get("masterFXSwitch")!.Bool);
        }
    }

    [Fact]
    public void ARemovedStyleLeavesNoEffectsBlock()
    {
        (PsdFile file, _) = NewDocument(new LayerStyle([new DropShadowEffect()]));
        using (file)
        {
            using PsdFile loaded = PsdFile.Load(Util.Save(file));
            loaded.Document.Root.Children[0].Style = null;
            byte[] saved = Util.Save(loaded);
            Assert.Equal(-1, saved.AsSpan().IndexOf("lfx2"u8));
            using PsdFile back = PsdFile.Load(saved);
            Assert.Null(back.Document.Root.Children[0].Style);
        }
    }

    [Fact]
    public void ItemsTheModelDoesNotCarrySurviveAnEdit()
    {
        // A block as Photoshop writes it: a drop shadow with grain, a gradient overlay and bevel the model cannot hold, and a disabled satin.
        (PsdFile origin, _) = NewDocument(new LayerStyle([new DropShadowEffect { Distance = 4, Size = 3 }, new SatinEffect { Enabled = false, Size = 5 }]));
        byte[] block;
        using (origin)
        {
            block = FxBlock(Util.Save(origin));
        }

        DescNode root = DescNode.Parse(block.AsSpan(8), PsdLimits.Default);
        DescNode shadow = root.Get("DrSh")!;
        shadow.Items[shadow.IndexOf("Nose")] = new("Nose", DescNode.Unitf("#Prc", 17));
        root.Items.Insert(root.IndexOf("DrSh") + 1, new("GrFl", Unmodelled("GrFl")));
        root.Add("ebbl", Unmodelled("ebbl"));
        root.Add("numModifyingFX", DescNode.Long_(3));
        byte[] handmade = Wrap(root);

        using PsdFile f = PsdFile.Load(Fx.File(handmade));
        Layer l = f.Document.Root.Children[0];
        LayerEffect oldShadow = l.Style!.Effects.OfType<DropShadowEffect>().Single();
        Assert.Equal(2, l.Style.Effects.Count); // the shadow and the disabled satin
        Assert.Equal(2, f.GetInfo(l).EffectsNotDrawn); // gradient overlay and bevel

        // Edit: keep the shadow object, drop the satin, add a stroke.
        l.Style = new LayerStyle([oldShadow, new StrokeEffect { Size = 6, Color = new Rgba32(1, 2, 3, 255) }]);
        byte[] saved = Util.Save(f);
        DescNode now = DescNode.Parse(FxBlock(saved).AsSpan(8), PsdLimits.Default);
        Assert.Equal(["Scl ", "masterFXSwitch", "DrSh", "GrFl", "FrFX", "ebbl", "numModifyingFX"], now.Items.Select(static i => i.Key));
        Assert.Equal(17, now.Get("DrSh")!.Get("Nose")!.Double); // written back as it was, grain and all
        Assert.Equal(Bytes(shadow), Bytes(now.Get("DrSh")!));
        Assert.Equal(Bytes(root.Get("GrFl")!), Bytes(now.Get("GrFl")!));
        Assert.Equal(Bytes(root.Get("ebbl")!), Bytes(now.Get("ebbl")!));
        Assert.Equal(4, now.Get("numModifyingFX")!.Long); // shadow, stroke, gradient overlay and bevel are enabled
        Assert.Null(now.Get("ChFX"));

        using PsdFile again = PsdFile.Load(saved);
        Layer back = again.Document.Root.Children[0];
        Assert.Equal(Style(l), Style(back));
        Assert.Equal(2, again.GetInfo(back).EffectsNotDrawn);
        Assert.True(saved.AsSpan().SequenceEqual(Util.Save(again)));
    }

    [Fact]
    public void AnEditedStyleReplacesTheLegacyTwinToo()
    {
        // A file whose effects are also stored in the legacy block: the legacy block would keep showing the old effects, so it goes.
        (PsdFile origin, _) = NewDocument(new LayerStyle([new DropShadowEffect()]));
        byte[] block;
        using (origin)
        {
            block = FxBlock(Util.Save(origin));
        }

        Hostile.Rec rec = Hostile.Pixel(4, 4);
        rec.Blocks.Add(("lfx2", block));
        rec.Blocks.Add(("lrFX", [0, 0, 0, 0, 0, 0]));
        using PsdFile f = PsdFile.Load(Hostile.File(16, 16, [rec]));
        Layer l = f.Document.Root.Children[0];
        Assert.Equal(["lfx2", "lrFX"], f.GetInfo(l).Blocks.Select(static b => b.Key).Where(static k => k is "lfx2" or "lrFX"));
        byte[] untouched = Util.Save(f);
        Assert.True(untouched.AsSpan().IndexOf("lrFX"u8) >= 0);

        l.Style = new LayerStyle([new ColorOverlayEffect { Color = new Rgba32(9, 9, 9, 255) }]);
        byte[] saved = Util.Save(f);
        Assert.Equal(-1, saved.AsSpan().IndexOf("lrFX"u8));
        using PsdFile back = PsdFile.Load(saved);
        Assert.Equal(Style(l), Style(back.Document.Root.Children[0]));
    }

    [Fact]
    public void ABlockSwitchedOffDoesNotComeBackWithANewStyle()
    {
        DescNode off = new DescNode { Type = "Objc", Name = DescNode.CountedString("", true), ClassId = "null" }
            .Add("Scl ", DescNode.Unitf("#Prc", 100)).Add("masterFXSwitch", DescNode.Bool_(false)).Add("ebbl", Unmodelled("ebbl"));
        using PsdFile f = PsdFile.Load(Fx.File(Wrap(off)));
        Layer l = f.Document.Root.Children[0];
        Assert.Null(l.Style);
        l.Style = new LayerStyle([new StrokeEffect { Size = 2 }]);
        DescNode now = DescNode.Parse(FxBlock(Util.Save(f)).AsSpan(8), PsdLimits.Default);
        Assert.Null(now.Get("ebbl"));
        Assert.True(now.Get("masterFXSwitch")!.Bool);
    }

    [Fact]
    public void PhotoshopKeepsOneGlowAndSatinPerLayer()
    {
        (PsdFile file, _) = NewDocument(new LayerStyle([new OuterGlowEffect(), new OuterGlowEffect { Size = 3 }]));
        using (file)
        {
            ImageOperationException e = Assert.Throws<ImageOperationException>(() => Util.Save(file));
            Assert.Contains("outer-glow", e.Message);
        }
    }

    [Fact]
    public void AGlowAddedBesideTheDisabledOneFromTheFileReplacesIt()
    {
        // A glow the way Photoshop writes a switched-off one.
        (PsdFile origin, _) = NewDocument(new LayerStyle([new OuterGlowEffect { Enabled = false }]));
        byte[] block;
        using (origin)
        {
            block = FxBlock(Util.Save(origin));
        }

        using PsdFile f = PsdFile.Load(Fx.File(block));
        Layer l = f.Document.Root.Children[0];
        Assert.False(l.Style!.Effects.Single().Enabled);
        l.Style = new LayerStyle([l.Style.Effects[0], new OuterGlowEffect { Size = 12 }]);
        using PsdFile back = PsdFile.Load(Util.Save(f));
        OuterGlowEffect g = Assert.IsType<OuterGlowEffect>(Assert.Single(back.Document.Root.Children[0].Style!.Effects));
        Assert.True(g.Enabled);
        Assert.Equal(12f, g.Size);
    }

    [Fact]
    public void OutOfRangeAnglesAreWrittenAsTheSameDirection()
    {
        (PsdFile file, _) = NewDocument(new LayerStyle([new DropShadowEffect { Angle = 725 }]));
        using (file)
        {
            using PsdFile back = PsdFile.Load(Util.Save(file));
            Assert.Equal(5f, Assert.IsType<DropShadowEffect>(Assert.Single(back.Document.Root.Children[0].Style!.Effects)).Angle);
        }
    }

    /// <summary>Takes every corpus file with effects, edits the style without changing it (a new <see cref="LayerStyle"/> around the same effects), and checks nothing is lost.</summary>
    [Fact]
    public void CorpusStylesSurviveAnEditThatChangesNothing()
    {
        if (Util.CorpusDir == null)
        {
            return;
        }

        var failures = new List<string>();
        int checkedLayers = 0;
        foreach (string path in Util.CorpusFiles())
        {
            string rel = System.IO.Path.GetRelativePath(Util.CorpusDir, path);
            try
            {
                using PsdFile f = PsdFile.Load(path);
                var edited = new List<(Layer Layer, DescNode Before)>();
                Walk(f.Document.Root, l =>
                {
                    PsdLayerInfo info = f.GetInfo(l);
                    if (l.Style != null && info.SourceFx?.Block != null)
                    {
                        edited.Add((l, DescNode.Parse(info.SourceFx.Block.Data.Span.Slice(8), PsdLimits.Default)));
                        l.Style = new LayerStyle(l.Style.Effects) { ShapeFromMask = l.Style.ShapeFromMask };
                    }
                });
                if (edited.Count == 0)
                {
                    continue;
                }

                string[] styles = [.. edited.Select(static e => Style(e.Layer))];
                byte[] saved = Util.Save(f);
                using PsdFile back = PsdFile.Load(saved);
                int i = 0;
                foreach ((Layer l, DescNode before) in edited)
                {
                    Layer b = Find(back.Document.Root, l.Name, f.Document.Root, l);
                    if (Style(b) != styles[i++])
                    {
                        failures.Add($"{rel} '{l.Name}': style differs");
                        continue;
                    }

                    DescNode after = DescNode.Parse(back.GetInfo(b).SourceFx!.Block!.Data.Span.Slice(8), PsdLimits.Default);
                    // Every root item of the original is still there with the same content (order of the kinds may be Photoshop's own).
                    foreach ((string key, DescNode node) in before.Items)
                    {
                        if (key == "numModifyingFX")
                        {
                            continue;
                        }

                        DescNode? kept = after.Get(key);
                        if (kept == null || !Bytes(kept).AsSpan().SequenceEqual(Bytes(node)))
                        {
                            failures.Add($"{rel} '{l.Name}': item {key} changed");
                        }
                    }

                    checkedLayers++;
                }

                byte[] again = Util.Save(back);
                if (!saved.AsSpan().SequenceEqual(again))
                {
                    failures.Add(rel + ": second write differs");
                }
            }
            catch (ImageFormatException)
            {
                // Files the reader refuses (multichannel) have no layers to edit.
            }
            catch (Exception ex)
            {
                failures.Add(rel + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} problems over {checkedLayers} layers:\n" + string.Join('\n', failures.Take(20)));
        Assert.True(checkedLayers > 20);
    }

    private static void Walk(GroupLayer g, Action<Layer> visit)
    {
        foreach (Layer l in g.Children)
        {
            visit(l);
            if (l is GroupLayer sub)
            {
                Walk(sub, visit);
            }
        }
    }

    // Layers are matched by their position in the tree, which writing keeps.
    private static Layer Find(GroupLayer root, string name, GroupLayer originalRoot, Layer original)
    {
        var path = new List<int>();
        Locate(originalRoot, original, path);
        Layer l = root;
        foreach (int i in path)
        {
            l = ((GroupLayer)l).Children[i];
        }

        _ = name;
        return l;
    }

    private static bool Locate(GroupLayer g, Layer target, List<int> path)
    {
        for (int i = 0; i < g.Children.Count; i++)
        {
            path.Add(i);
            if (ReferenceEquals(g.Children[i], target) || (g.Children[i] is GroupLayer sub && Locate(sub, target, path)))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private static byte[] Bytes(DescNode n)
    {
        var holder = new DescNode { Type = "Objc", Name = DescNode.CountedString("", true), ClassId = "null" }.Add("x", n);
        var ms = new MemoryStream();
        holder.Write(ms);
        return ms.ToArray();
    }

    private static byte[] Wrap(DescNode root)
    {
        var ms = new MemoryStream();
        ms.Write([0, 0, 0, 0, 0, 0, 0, 16]);
        root.Write(ms);
        return ms.ToArray();
    }

    // An effect item with a few fields the model has no place for.
    private static DescNode Unmodelled(string cls) =>
        new DescNode { Type = "Objc", Name = DescNode.CountedString("", true), ClassId = cls }
            .Add("enab", DescNode.Bool_(true)).Add("present", DescNode.Bool_(true)).Add("showInDialog", DescNode.Bool_(true))
            .Add("Md  ", DescNode.Enum("BlnM", "Nrml")).Add("Opct", DescNode.Unitf("#Prc", 100)).Add("Algn", DescNode.Bool_(true))
            .Add("Grad", new DescNode { Type = "Objc", Name = DescNode.CountedString("Custom", true), ClassId = "Grdn" }.Add("Intr", DescNode.Dbl(4096)));

    /// <summary>The data of the first <c>lfx2</c> block in a saved file: found by walking the layer records through the library's own reader.</summary>
    private static byte[] FxBlock(byte[] file)
    {
        using PsdFile f = PsdFile.Load(file);
        Layer l = f.Document.Root.Children[0];
        return f.GetInfo(l).Blocks.First(static b => b.Key is "lfx2").Data.ToArray();
    }
}
