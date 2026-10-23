using System.Buffers.Binary;
using OneImage.Layers;

namespace OneImage.Psd.Tests;

/// <summary>Bevel and emboss, gradient and pattern overlays, gradient and pattern strokes and gradient glows: written from the model, read back, and hostile versions of their descriptors.</summary>
public class PaintStyleTests
{
    // ---- save / load ----

    private static string Style(Layer l) =>
        string.Join('\n', l.Describe().Parameters.Where(static p => p.Name.StartsWith("style", StringComparison.Ordinal)).Select(static p => p.Name + "=" + p.Value));

    private static EffectGradient Gradient(bool linear = false) => new(
        [new GradientColorStop(0, 0.5f, new RgbaFloat(1, 0.2f, 0, 1)), new GradientColorStop(0.25f, 0.3f, new RgbaFloat(0.1f, 0.9f, 0.3f, 1)), new GradientColorStop(1, 0.5f, new RgbaFloat(0, 0, 1, 1))],
        [new GradientOpacityStop(0, 0.5f, 1), new GradientOpacityStop(0.75f, 0.5f, 0.5f), new GradientOpacityStop(1, 0.5f, 0.9f)],
        smoothness: 0.5f,
        linearLight: linear);

    private static EffectPattern Pattern()
    {
        byte[] px = new byte[8 * 6 * 4];
        for (int i = 0; i < 48; i++)
        {
            px[i * 4] = (byte)(i * 5);
            px[(i * 4) + 1] = (byte)(255 - (i * 3));
            px[(i * 4) + 2] = (byte)((i % 8) * 30);
            px[(i * 4) + 3] = 255;
        }

        return new EffectPattern(8, 6, px, "11111111-2222-3333-4444-555555555555");
    }

    private static ImageRect AlphaBounds(PixelLayer l)
    {
        Rgba32[] px = new Rgba32[l.Pixels.Width * l.Pixels.Height];
        l.Pixels.CopyTo<Rgba32>(px, new ImageRect(0, 0, l.Pixels.Width, l.Pixels.Height));
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int i = 0; i < px.Length; i++)
        {
            if (px[i].A != 0)
            {
                int x = i % l.Pixels.Width, y = i / l.Pixels.Width;
                x0 = Math.Min(x0, x);
                x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y);
                y1 = Math.Max(y1, y);
            }
        }

        return new ImageRect(l.X + x0, l.Y + y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private static void RoundTrip(LayerStyle style)
    {
        var doc = new Document(120, 90, new DocumentOptions { Format = PixelFormat.Rgba8 });
        var file = PsdFile.FromDocument(doc);
        PixelLayer l = doc.Add(doc.CreatePixelLayer("shape", 70, 50, 25, 20));
        l.Pixels.Fill(new Rgba32(220, 90, 40, 255), Drawing.Path.Circle(35, 25, 22));
        // Aligned gradients and patterns are laid out on the shape's tight bounds, which the reader derives from the pixels; the model is told the same.
        l.Style = new LayerStyle(style.Effects) { AlignBox = AlphaBounds(l) };
        using (file)
        {
            byte[] first = Util.Save(file);
            using PsdFile loaded = PsdFile.Load(first);
            Layer back = loaded.Document.Root.Children[0];
            Assert.NotNull(back.Style);
            Assert.Equal(0, loaded.GetInfo(back).EffectsNotDrawn);
            Assert.Equal(Style(l), Style(back));
            Assert.Equal(file.Document.Render().ToArray<Rgba32>(), loaded.Document.Render().ToArray<Rgba32>());
            Assert.True(first.AsSpan().SequenceEqual(Util.Save(loaded)), "second write differs from the first");
        }
    }

    [Theory]
    [InlineData(EffectGradientStyle.Linear)]
    [InlineData(EffectGradientStyle.Radial)]
    [InlineData(EffectGradientStyle.Angle)]
    [InlineData(EffectGradientStyle.Reflected)]
    [InlineData(EffectGradientStyle.Diamond)]
    public void AGradientOverlayOfEveryStyleSurvives(EffectGradientStyle style) =>
        RoundTrip(new LayerStyle([new GradientOverlayEffect
        {
            Blend = BlendMode.Overlay, Opacity = 0.8f,
            Fill = new EffectGradientFill(Gradient(linear: style == EffectGradientStyle.Radial), style, angle: 37.5f, scale: 1.4f, reverse: true, alignWithLayer: true, offsetX: 0.1f, offsetY: -0.05f),
        }]));

    [Fact]
    public void AGradientOverlayNotAlignedWithTheLayerSurvives() =>
        RoundTrip(new LayerStyle([new GradientOverlayEffect { Fill = new EffectGradientFill(Gradient(), EffectGradientStyle.Linear, angle: 90, alignWithLayer: false) }]));

    [Fact]
    public void APatternOverlayWithAPatternTheDocumentLacksSurvives() =>
        RoundTrip(new LayerStyle([new PatternOverlayEffect { Blend = BlendMode.Multiply, Opacity = 0.6f, Fill = new EffectPatternFill(Pattern(), scale: 1.5f, linkWithLayer: false, phaseX: 3, phaseY: 5) }]));

    [Fact]
    public void AGradientStrokeSurvives() =>
        RoundTrip(new LayerStyle([new StrokeEffect { Size = 5, Position = StrokePosition.Center, Opacity = 0.9f, Gradient = new EffectGradientFill(Gradient(), EffectGradientStyle.Reflected, angle: -20, scale: 0.8f) }]));

    [Fact]
    public void AShapeBurstGradientStrokeSurvives() =>
        RoundTrip(new LayerStyle([new StrokeEffect { Size = 6, Position = StrokePosition.Inside, Gradient = new EffectGradientFill(Gradient(), EffectGradientStyle.ShapeBurst) }]));

    [Fact]
    public void APatternStrokeSurvives() =>
        RoundTrip(new LayerStyle([new StrokeEffect { Size = 4, Position = StrokePosition.Outside, PatternFill = new EffectPatternFill(Pattern(), scale: 2, linkWithLayer: true, phaseX: 1, phaseY: 2) }]));

    [Fact]
    public void GradientGlowsSurvive() =>
        RoundTrip(new LayerStyle(
        [
            new OuterGlowEffect { Color = new Rgba32(0, 0, 0, 255), Size = 9, Spread = 0.1f, Range = 0.4f, Opacity = 0.8f, Gradient = Gradient() },
            new InnerGlowEffect { Color = new Rgba32(0, 0, 0, 255), Size = 7, Choke = 0.2f, Source = GlowSource.Center, Gradient = Gradient(linear: true) },
        ]));

    [Theory]
    [InlineData(BevelStyle.InnerBevel, BevelTechnique.Smooth)]
    [InlineData(BevelStyle.InnerBevel, BevelTechnique.ChiselHard)]
    [InlineData(BevelStyle.OuterBevel, BevelTechnique.ChiselSoft)]
    [InlineData(BevelStyle.Emboss, BevelTechnique.Smooth)]
    [InlineData(BevelStyle.PillowEmboss, BevelTechnique.ChiselHard)]
    [InlineData(BevelStyle.StrokeEmboss, BevelTechnique.Smooth)]
    public void ABevelOfEveryStyleAndTechniqueSurvives(BevelStyle style, BevelTechnique technique) =>
        RoundTrip(new LayerStyle(
        [
            new StrokeEffect { Size = 3, Position = StrokePosition.Outside },
            new BevelEmbossEffect
            {
                Style = style, Technique = technique, Depth = 2.5f, Size = 8, Soften = 2, Up = false, Angle = 117, Altitude = 41,
                HighlightBlend = BlendMode.Screen, HighlightColor = new Rgba32(250, 240, 200, 255), HighlightOpacity = 0.6f,
                ShadowBlend = BlendMode.Multiply, ShadowColor = new Rgba32(20, 10, 40, 255), ShadowOpacity = 0.45f,
            },
        ]));

    [Fact]
    public void ABevelWithAGlossContourSurvives() =>
        RoundTrip(new LayerStyle([new BevelEmbossEffect { Size = 6, GlossContour = new EffectContour([new ContourPoint(0, 0), new ContourPoint(0.4f, 0.8f), new ContourPoint(1, 1)]) }]));

    // ---- hostile descriptors ----

    private static void Color(DescriptorWriter w, string key, double v) =>
        w.Obj(key, "RGBC", 3, () =>
        {
            w.Num("Rd  ", v);
            w.Num("Grn ", v);
            w.Num("Bl  ", v);
        });

    private static void Common(DescriptorWriter w, bool color = false)
    {
        w.Bool("enab", true);
        w.Enum("Md  ", "BlnM", "Nrml");
        w.Unit("Opct", "#Prc", 100);
        if (color)
        {
            Color(w, "Clr ", 0);
        }
    }

    private static void Grad(DescriptorWriter w, int stops, int opacities = 2, double location = 0, string form = "CstS", string? method = null)
    {
        if (method != null)
        {
            w.Enum("gradientsInterpolationMethod", "gradientInterpolationMethodType", method);
        }

        w.Obj("Grad", "Grdn", form == "CstS" ? 5 : 3, () =>
        {
            w.Text("Nm  ", "g");
            w.Enum("GrdF", "GrdF", form);
            if (form != "CstS")
            {
                w.Num("RndS", 1);
                return;
            }

            w.Num("Intr", 4096);
            w.List("Clrs", stops, () =>
            {
                for (int i = 0; i < stops; i++)
                {
                    w.Raw("Objc");
                    w.Header("Clrt");
                    w.Count(4);
                    Color(w, "Clr ", i % 256);
                    w.Enum("Type", "Clry", "UsrS");
                    w.Long("Lctn", (int)(i == 0 ? location : Math.Min(4096, i * 16)));
                    w.Long("Mdpn", 50);
                }
            });
            w.List("Trns", opacities, () =>
            {
                for (int i = 0; i < opacities; i++)
                {
                    w.Raw("Objc");
                    w.Header("TrnS");
                    w.Count(3);
                    w.Unit("Opct", "#Prc", 100);
                    w.Long("Lctn", Math.Min(4096, i * 16));
                    w.Long("Mdpn", 50);
                }
            });
        });
    }

    private static byte[] GradientOverlay(int stops = 2, int opacities = 2, double angle = 90, double scale = 100, double location = 0, string type = "Lnr ", string form = "CstS", string? method = null) =>
        Fx.Block(w =>
        {
            w.Bool("masterFXSwitch", true);
            w.Obj("GrFl", "GrFl", 8, () =>
            {
                Common(w);
                w.Unit("Angl", "#Ang", angle);
                w.Enum("Type", "GrdT", type);
                w.Unit("Scl ", "#Prc", scale);
                w.Bool("Algn", true);
                Grad(w, stops, opacities, location, form, method);
            });
        }, 2);

    private static byte[] PatternOverlay(string id, double scale = 100, double phase = 0) =>
        Fx.Block(w =>
        {
            w.Bool("masterFXSwitch", true);
            w.Obj("patternFill", "patternFill", 7, () =>
            {
                Common(w);
                w.Unit("Scl ", "#Prc", scale);
                w.Bool("Algn", true);
                w.Obj("phase", "Pnt ", 2, () =>
                {
                    w.Unit("Hrzn", "#Pxl", phase);
                    w.Unit("Vrtc", "#Pxl", phase);
                });
                w.Obj("Ptrn", "Ptrn", 2, () =>
                {
                    w.Text("Nm  ", "p");
                    w.Text("Idnt", id + "\0");
                });
            });
        }, 2);

    private static byte[] Bevel(double angle = 120, double altitude = 30, double depth = 100, double size = 5, double soften = 0, string style = "InrB", string technique = "SfBL") =>
        Fx.Block(w =>
        {
            w.Bool("masterFXSwitch", true);
            w.Obj("ebbl", "ebbl", 14, () =>
            {
                w.Bool("enab", true);
                w.Enum("hglM", "BlnM", "Scrn");
                Color(w, "hglC", 255);
                w.Unit("hglO", "#Prc", 75);
                w.Enum("sdwM", "BlnM", "Mltp");
                Color(w, "sdwC", 0);
                w.Unit("sdwO", "#Prc", 75);
                w.Enum("bvlT", "bvlT", technique);
                w.Enum("bvlS", "BESl", style);
                w.Unit("lagl", "#Ang", angle);
                w.Unit("Lald", "#Ang", altitude);
                w.Unit("srgR", "#Prc", depth);
                w.Unit("blur", "#Pxl", size);
                w.Unit("Sftn", "#Pxl", soften);
            });
        }, 2);

    private static byte[] StrokeWith(Action<DescriptorWriter> paint) =>
        Fx.Block(w =>
        {
            w.Bool("masterFXSwitch", true);
            w.Obj("FrFX", "FrFX", 9, () =>
            {
                Common(w);
                w.Unit("Sz  ", "#Pxl", 3);
                w.Enum("Styl", "FStl", "OutF");
                w.Enum("PntT", "FrFl", "GrFl");
                w.Unit("Angl", "#Ang", 90);
                w.Enum("Type", "GrdT", "Lnr ");
                paint(w);
            });
        }, 2);

    private static byte[] GlowWith(int stops) =>
        Fx.Block(w =>
        {
            w.Bool("masterFXSwitch", true);
            w.Obj("OrGl", "OrGl", 8, () =>
            {
                Common(w);
                w.Unit("blur", "#Pxl", 5);
                w.Unit("Ckmt", "#Pxl", 0);
                w.Unit("Inpr", "#Prc", 50);
                w.Enum("GlwT", "BETE", "SfBL");
                Grad(w, stops);
            });
        }, 2);

    private static byte[] Doc(byte[] lfx2, byte[]? patterns = null)
    {
        var doc = new Document(16, 16);
        var f = PsdFile.FromDocument(doc);
        PixelLayer pl = doc.Add(doc.CreatePixelLayer("l", 8, 8, 2, 2));
        pl.Pixels.Fill(new Rgba32(200, 50, 50, 255), Drawing.Path.Circle(4, 4, 4));
        f.GetInfo(pl).Blocks.Add(new PsdBlock("lfx2", lfx2));
        if (patterns != null)
        {
            f.DocumentBlocks.Add(new PsdBlock("Patt", patterns));
        }

        return Util.Save(f);
    }

    // Loads and renders; the effect is either drawn (skipped == 0) or left out and counted.
    private static void Check(byte[] lfx2, int skipped, byte[]? patterns = null, PsdLimits? limits = null, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        using PsdFile f = PsdFile.Load(Doc(lfx2, patterns), new PsdReadOptions { PsdLimits = limits });
        f.Document.Render().ToArray<Rgba32>();
        Layer l = f.Document.Root.Children[0];
        Assert.True(skipped == f.GetInfo(l).EffectsNotDrawn, $"line {line}: skipped {f.GetInfo(l).EffectsNotDrawn}, expected {skipped}");
        Assert.Equal(skipped == 0, l.Style != null);
    }

    [Fact]
    public void HonestPaintEffectsLoad()
    {
        Check(GradientOverlay(), 0);
        Check(Bevel(), 0);
        Check(StrokeWith(w => Grad(w, 2)), 0);
        Check(GlowWith(2), 0);
        Check(PatternOverlay("pat-1"), 0, FillDocs.PatternResource("pat-1", 4, 4, static (c, x, y) => (byte)(c + x + y)));
    }

    [Theory]
    [InlineData(257, 2)]
    [InlineData(2, 257)]
    [InlineData(5000, 5000)]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    public void GradientStopCountsOutsideTheLimitSkipTheOverlay(int stops, int opacities) => Check(GradientOverlay(stops, opacities), 1);

    [Fact]
    public void ADeclaredStopCountBeyondTheDescriptorItemLimitIsATypedError()
    {
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Doc(GradientOverlay(100_000, 2))));
        Assert.Throws<ImageLimitException>(() => PsdFile.Load(Doc(GradientOverlay(2, 100_000))));
    }

    [Fact]
    public void AStopCountOnTheEdgeStillLoads() => Check(GradientOverlay(256, 256), 0);

    [Theory]
    [InlineData(5000, 2)]
    [InlineData(2, 5000)]
    public void GradientStopCountsOutsideTheLimitSkipAStrokeAndAGlow(int stops, int opacities)
    {
        Check(StrokeWith(w => Grad(w, stops, opacities)), 1);
        if (opacities == 2)
        {
            Check(GlowWith(stops), 1);
        }
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(1e9, 100)]
    [InlineData(-3601, 100)]
    [InlineData(90, 0)]
    [InlineData(90, 1e9)]
    [InlineData(90, -100)]
    [InlineData(90, double.PositiveInfinity)]
    public void GradientAnglesAndScalesOutOfRangeSkipTheOverlay(double angle, double scale) => Check(GradientOverlay(angle: angle, scale: scale), 1);

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    [InlineData(2_000_000_000)]
    public void AStopLocationOutOfRangeSkipsTheOverlay(double location) => Check(GradientOverlay(location: location), 1);

    [Fact]
    public void GradientKindsThatAreNotReproducedAreSkipped()
    {
        Check(GradientOverlay(type: "Bogs"), 1);
        Check(GradientOverlay(form: "ClNs"), 1);
        Check(GradientOverlay(method: "Prcp"), 1);
    }

    [Theory]
    [InlineData(double.NaN, 30, 100, 5, 0)]
    [InlineData(1e9, 30, 100, 5, 0)]
    [InlineData(120, 91, 100, 5, 0)]
    [InlineData(120, -1, 100, 5, 0)]
    [InlineData(120, 30, 1001, 5, 0)]
    [InlineData(120, 30, -1, 5, 0)]
    [InlineData(120, 30, 100, 251, 0)]
    [InlineData(120, 30, 100, 1e9, 0)]
    [InlineData(120, 30, 100, 5, 17)]
    [InlineData(120, 30, 100, 5, double.NaN)]
    public void BevelsOutOfRangeSkipTheEffectBeforeAnythingIsAllocated(double angle, double altitude, double depth, double size, double soften) =>
        Check(Bevel(angle, altitude, depth, size, soften), 1);

    [Fact]
    public void ABevelWithTheLargestSizeAndDepthLoads() => Check(Bevel(depth: 1000, size: 250, soften: 16), 0);

    [Fact]
    public void ABevelWithAnUnknownStyleOrTechniqueIsSkipped()
    {
        Check(Bevel(style: "Nope"), 1);
        Check(Bevel(technique: "Nope"), 1);
    }

    [Theory]
    [InlineData(1e9)]
    [InlineData(-1e9)]
    [InlineData(double.NaN)]
    public void APatternPhaseOutOfRangeSkipsTheOverlay(double phase) =>
        Check(PatternOverlay("pat-1", phase: phase), 1, FillDocs.PatternResource("pat-1", 4, 4, static (_, _, _) => 7));

    [Theory]
    [InlineData(0)]
    [InlineData(0.001)]
    [InlineData(100_000)]
    [InlineData(double.PositiveInfinity)]
    public void APatternScaleOutOfRangeSkipsTheOverlay(double scale) =>
        Check(PatternOverlay("pat-1", scale), 1, FillDocs.PatternResource("pat-1", 4, 4, static (_, _, _) => 7));

    [Fact]
    public void APatternThatTheDocumentDoesNotHoldIsSkipped()
    {
        Check(PatternOverlay("nowhere"), 1);
        Check(PatternOverlay("nowhere"), 1, FillDocs.PatternResource("another", 4, 4, static (_, _, _) => 7));
    }

    [Fact]
    public void AnOversizedPatternIsRefusedBeforeItsPixelsAreAllocated()
    {
        // Wider than the side limit, and over the pixel limit while within the side limit; both fail on the size alone.
        Check(PatternOverlay("big"), 1, FillDocs.PatternResource("big", 16385, 1, static (_, _, _) => 7));
        Check(PatternOverlay("big"), 1, FillDocs.PatternResource("big", 4100, 4100, static (_, _, _) => 7));

        // A size that the data does not back is refused before the buffer exists: patch the first channel's rectangle of a small pattern to 4000 x 4000.
        byte[] res = FillDocs.PatternResource("liar", 4, 4, static (_, _, _) => 7);
        byte[] needle = [0, 0, 0, 4, 0, 0, 0, 4]; // bottom = 4, right = 4 after top = left = 0
        int patched = 0;
        for (int at = 0; at + 8 <= res.Length; at++)
        {
            if (res.AsSpan(at, 8).SequenceEqual(needle))
            {
                BinaryPrimitives.WriteInt32BigEndian(res.AsSpan(at), 4000);
                BinaryPrimitives.WriteInt32BigEndian(res.AsSpan(at + 4), 4000);
                patched++;
            }
        }

        Assert.True(patched >= 3);
        Check(PatternOverlay("liar"), 1, res);
    }

    [Fact]
    public void ATruncatedPatternResourceIsSkipped()
    {
        byte[] res = FillDocs.PatternResource("cut", 4, 4, static (_, _, _) => 7);
        for (int n = 0; n < res.Length; n += 7)
        {
            using PsdFile f = PsdFile.Load(Doc(PatternOverlay("cut"), res[..n]));
            f.Document.Render().ToArray<Rgba32>();
        }
    }

    [Fact]
    public void TruncatedAndGarbagePaintItemsOnlyEverGiveTypedErrors()
    {
        byte[][] goods =
        [
            GradientOverlay(3, 3, method: "Lnr "),
            Bevel(),
            PatternOverlay("p"),
            StrokeWith(w => Grad(w, 3)),
            GlowWith(3),
        ];
        byte[] pattern = FillDocs.PatternResource("p", 4, 4, static (c, x, y) => (byte)(c + x + y));
        var rng = new Random(2024);
        foreach (byte[] good in goods)
        {
            for (int n = 0; n < good.Length; n++)
            {
                Probe(good[..n]);
            }

            for (int round = 0; round < 150; round++)
            {
                byte[] b = (byte[])good.Clone();
                for (int k = 0; k < 4; k++)
                {
                    b[rng.Next(b.Length)] = (byte)rng.Next(256);
                }

                Probe(b);
            }
        }

        void Probe(byte[] block)
        {
            try
            {
                using PsdFile f = PsdFile.Load(Doc(block, pattern));
                f.Document.Render().ToArray<Rgba32>();
            }
            catch (ImageException)
            {
            }
        }
    }

    [Fact]
    public void MorePaintEffectsThanTheLimitAreCountedNotDrawn()
    {
        byte[] block = Fx.Block(w =>
        {
            w.Bool("masterFXSwitch", true);
            w.List("gradientFillMulti", 10, () =>
            {
                for (int i = 0; i < 10; i++)
                {
                    w.Obj("", "GrFl", 8, () =>
                    {
                        Common(w);
                        w.Unit("Angl", "#Ang", 90);
                        w.Enum("Type", "GrdT", "Lnr ");
                        w.Unit("Scl ", "#Prc", 100);
                        w.Bool("Algn", true);
                        Grad(w, 2);
                    });
                }
            });
        }, 2);
        using PsdFile f = PsdFile.Load(Doc(block), new PsdReadOptions { PsdLimits = new PsdLimits { MaxEffectsPerLayer = 3 } });
        f.Document.Render().ToArray<Rgba32>();
        Layer l = f.Document.Root.Children[0];
        Assert.Equal(3, l.Style!.Effects.Count);
        Assert.Equal(7, f.GetInfo(l).EffectsNotDrawn);
    }
}
