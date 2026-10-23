using OneImage.Layers;

namespace OneImage.Psd.Tests;

/// <summary>Builds layer-effects blocks (<c>lfx2</c>) by hand, honest or hostile.</summary>
internal static class Fx
{
    public static byte[] Block(Action<DescriptorWriter> items, int count)
    {
        byte[] d = DescriptorWriter.Block(w =>
        {
            w.Count(count);
            items(w);
        });
        return [0, 0, 0, 0, .. d]; // effects version, then the descriptor
    }

    /// <summary>One drop shadow item with the given distance and blur (and an optional contour of <paramref name="contourPoints"/> points).</summary>
    public static void Shadow(DescriptorWriter w, double distance = 5, double blur = 4, int contourPoints = 0, bool enabled = true)
    {
        w.Obj("", "DrSh", contourPoints > 0 ? 9 : 8, () =>
        {
            w.Bool("enab", enabled);
            w.Enum("Md  ", "BlnM", "Mltp");
            w.Obj("Clr ", "RGBC", 3, () =>
            {
                w.Num("Rd  ", 0);
                w.Num("Grn ", 0);
                w.Num("Bl  ", 0);
            });
            w.Unit("Opct", "#Prc", 75);
            w.Unit("lagl", "#Ang", 120);
            w.Unit("Dstn", "#Pxl", distance);
            w.Unit("Ckmt", "#Pxl", 0);
            w.Unit("blur", "#Pxl", blur);
            if (contourPoints > 0)
            {
                w.Obj("TrnS", "ShpC", 1, () =>
                    w.List("Crv ", contourPoints, () =>
                    {
                        for (int i = 0; i < contourPoints; i++)
                        {
                            w.Obj("", "CrPt", 2, () =>
                            {
                                w.Num("Hrzn", i % 256);
                                w.Num("Vrtc", i % 256);
                            });
                        }
                    }));
            }
        });
    }

    public static byte[] Shadows(int n, double distance = 5, double blur = 4, int contourPoints = 0, bool enabled = true) =>
        Block(w =>
        {
            w.Bool("masterFXSwitch", true);
            w.List("dropShadowMulti", n, () =>
            {
                for (int i = 0; i < n; i++)
                {
                    Shadow(w, distance, blur, contourPoints, enabled);
                }
            });
        }, 2);

    public static Hostile.Rec Layer(byte[] lfx2)
    {
        Hostile.Rec r = Hostile.Pixel(4, 4);
        r.Blocks.Add(("lfx2", lfx2));
        return r;
    }

    public static byte[] File(byte[] lfx2) => Hostile.File(16, 16, [Layer(lfx2)]);
}

public class EffectsTests
{
    private static PsdFile Load(byte[] lfx2, PsdLimits? limits = null)
    {
        PsdFile f = PsdFile.Load(Fx.File(lfx2), new PsdReadOptions { PsdLimits = limits });
        f.Document.Render().ToArray<Rgba32>();
        return f;
    }

    private static (Layer Layer, PsdLayerInfo Info) Only(PsdFile f)
    {
        Layer l = f.Document.Root.Children[0];
        return (l, f.GetInfo(l));
    }

    [Fact]
    public void AnHonestShadowBecomesAStyle()
    {
        using PsdFile f = Load(Fx.Shadows(1));
        (Layer l, PsdLayerInfo info) = Only(f);
        DropShadowEffect e = Assert.IsType<DropShadowEffect>(Assert.Single(l.Style!.Effects));
        Assert.Equal(5f, e.Distance);
        Assert.Equal(4f, e.Size);
        Assert.Equal(0, info.EffectsNotDrawn);
    }

    [Fact]
    public void MoreEnabledEffectsThanTheLimitAreNotDrawn()
    {
        using (PsdFile f = Load(Fx.Shadows(40)))
        {
            (Layer l, PsdLayerInfo info) = Only(f);
            Assert.Equal(32, l.Style!.Effects.Count);
            Assert.Equal(8, info.EffectsNotDrawn);
        }

        using (PsdFile f = Load(Fx.Shadows(5), new PsdLimits { MaxEffectsPerLayer = 2 }))
        {
            (Layer l, PsdLayerInfo info) = Only(f);
            Assert.Equal(2, l.Style!.Effects.Count);
            Assert.Equal(3, info.EffectsNotDrawn);
        }

        // Disabled effects do not count against the limit.
        using (PsdFile f = Load(Fx.Shadows(100, enabled: false)))
        {
            Assert.Null(Only(f).Layer.Style);
        }
    }

    [Theory]
    [InlineData(1e9, 4)]
    [InlineData(5, 1e9)]
    [InlineData(5, 251)]
    [InlineData(30001, 4)]
    [InlineData(-1, 4)]
    [InlineData(double.NaN, 4)]
    [InlineData(5, double.PositiveInfinity)]
    public void OutOfRangeSizesAndDistancesSkipTheEffectBeforeAnythingIsAllocated(double distance, double blur)
    {
        using PsdFile f = Load(Fx.Shadows(1, distance, blur));
        (Layer l, PsdLayerInfo info) = Only(f);
        Assert.Null(l.Style);
        Assert.Equal(1, info.EffectsNotDrawn);
    }

    [Fact]
    public void TheLargestAllowedSizeStillRenders()
    {
        using PsdFile f = Load(Fx.Shadows(1, distance: 30000, blur: 250));
        Assert.NotNull(Only(f).Layer.Style);
    }

    [Fact]
    public void AHugeContourIsNotDrawn()
    {
        using PsdFile f = Load(Fx.Shadows(1, contourPoints: 10_000));
        (Layer l, PsdLayerInfo info) = Only(f);
        Assert.Null(l.Style);
        Assert.Equal(1, info.EffectsNotDrawn);

        // Points that are not increasing are refused too.
        using PsdFile g = Load(Fx.Shadows(1, contourPoints: 300));
        Assert.Null(Only(g).Layer.Style);
    }

    [Fact]
    public void ADeclaredCountFarBeyondTheDataIsALimitError()
    {
        byte[] huge = Fx.Block(w =>
        {
            w.Bool("masterFXSwitch", true);
            w.List("dropShadowMulti", 1 << 30, () => { });
        }, 2);
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Fx.File(huge)));
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Fx.File(Fx.Block(_ => { }, int.MaxValue))));
    }

    [Fact]
    public void DeepNestingIsBoundedByTheDescriptorLimit()
    {
        byte[] Nested(int depth)
        {
            byte[] d = Hostile.NestedDescriptor(depth);
            return [0, 0, 0, 0, .. d];
        }

        var limits = new PsdLimits { MaxDescriptorDepth = 8 };
        using (PsdFile.Load(Fx.File(Nested(5)), new PsdReadOptions { PsdLimits = limits }))
        {
        }

        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Fx.File(Nested(20)), new PsdReadOptions { PsdLimits = limits }));
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Fx.File(Nested(100_000))));
    }

    [Fact]
    public void TruncatedAndGarbageBlocksNeverEscapeAsAnythingButTypedErrors()
    {
        byte[] good = Fx.Shadows(2, contourPoints: 4);
        for (int n = 0; n < good.Length; n++)
        {
            Probe(good[..n]);
        }

        var rng = new Random(12345);
        for (int round = 0; round < 300; round++)
        {
            byte[] b = (byte[])good.Clone();
            for (int k = 0; k < 4; k++)
            {
                b[rng.Next(b.Length)] = (byte)rng.Next(256);
            }

            Probe(b);
            byte[] noise = new byte[rng.Next(1, 200)];
            rng.NextBytes(noise);
            Probe(noise);
        }

        static void Probe(byte[] block)
        {
            try
            {
                using PsdFile f = PsdFile.Load(Fx.File(block));
                f.Document.Render().ToArray<Rgba32>();
            }
            catch (ImageException)
            {
            }
        }
    }

    [Fact]
    public void AnUnchangedStyleIsWrittenBackByteForByte()
    {
        byte[] lfx2 = Fx.Shadows(2, contourPoints: 3);
        byte[] first;
        using (PsdFile f = PsdFile.Load(Fx.File(lfx2)))
        {
            Assert.NotNull(Only(f).Layer.Style);
            first = Util.Save(f);
        }

        Assert.True(first.AsSpan().IndexOf(lfx2) >= 0, "the effects block must appear unchanged in the saved file");
        using PsdFile g = PsdFile.Load(first);
        Assert.Equal(lfx2, g.GetInfo(g.Document.Root.Children[0]).Blocks.First(static b => b.Key == "lfx2").Data.ToArray());
        Assert.Equal(first, Util.Save(g));
    }

    [Fact]
    public void ChangingAStyleCannotBeSavedAndSaysSo()
    {
        using PsdFile f = PsdFile.Load(Fx.File(Fx.Shadows(1)));
        Layer l = Only(f).Layer;
        l.Style = new LayerStyle([new ColorOverlayEffect { Color = new Rgba32(255, 0, 0, 255) }]);
        Assert.Throws<ImageOperationException>(() => Util.Save(f));
    }
}

/// <summary>Pins the stroke files of the sample set, which Photoshop's stored composites check the drawn strokes against. Skipped without the sample files.</summary>
public class EffectCorpusTests
{
    // Mean absolute error (levels, 0..255) against the stored composite. Before strokes were drawn these files measured 14-44.
    [Theory]
    [InlineData("effects/antialiased-stroke-edge.psd", 0.5)]
    [InlineData("effects/center-stroke-sizes.psd", 0.5)]
    [InlineData("effects/feathered-stroke.psd", 0.5)]
    [InlineData("effects/group-clips-child-stroke.psd", 0.5)]
    [InlineData("effects/inset-stroke-sizes.psd", 0.5)]
    [InlineData("effects/outside-stroke.psd", 0.5)]
    [InlineData("effects/shape-stroke-raster-mask.psd", 0.5)]
    [InlineData("effects/stroke-effect-transparent-shape.psd", 0.5)]
    public void DrawnStrokesAgreeWithTheStoredComposite(string name, double limit)
    {
        if (Util.CorpusDir == null)
        {
            return;
        }

        string path = System.IO.Path.Combine(Util.CorpusDir, name);
        Assert.True(File.Exists(path), "sample file missing: " + name);
        using PsdFile f = PsdFile.Load(path);
        (double mean, _, _, _, _) = Util.Diff(Util.LoadComposite(path), f.Document.Render());
        Assert.True(mean <= limit, $"{name}: mean error {mean:F3} exceeds {limit}");
    }
}
