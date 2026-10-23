using OneImage.Layers;
using static OneImage.Tests.Layers.LayerCompositeTests;

namespace OneImage.Tests.Layers;

/// <summary>Gradient and pattern overlays, gradient strokes and glows, and bevel and emboss.</summary>
public class LayerStylePaintTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);
    private static readonly Rgba32 Gray = new(128, 128, 128, 255);
    private static readonly Rgba32 Red = new(255, 0, 0, 255);

    private static Rgba32[] Square(int w, int h, int x0, int y0, int size, Rgba32 color)
    {
        var px = new Rgba32[w * h];
        for (int y = y0; y < y0 + size; y++)
        {
            for (int x = x0; x < x0 + size; x++)
            {
                px[(y * w) + x] = color;
            }
        }

        return px;
    }

    private static Document Doc(int w, int h, Rgba32 background) => new(w, h, new DocumentOptions { Background = background });

    private static EffectGradient RedToGreen() =>
        new([new GradientColorStop(0, 0.5f, new RgbaFloat(1, 0, 0, 1)), new GradientColorStop(1, 0.5f, new RgbaFloat(0, 1, 0, 1))], smoothness: 0f);

    [Fact]
    public void AGradientOverlayRunsAlongItsAngleOverTheShapeAndKeepsOutsideUntouched()
    {
        using Document doc = Doc(64, 64, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(64, 64, 10, 10, 40, new Rgba32(0, 0, 255, 255)), 64, 64, 0, 0);
        l.Style = new LayerStyle([new GradientOverlayEffect { Fill = new EffectGradientFill(RedToGreen(), angle: 0) }]) { AlignBox = new ImageRect(10, 10, 40, 40) };
        Rgba32[] px = Pixels(doc.Render());

        Assert.Equal(White, px[(30 * 64) + 9]);
        Assert.Equal(White, px[(30 * 64) + 50]);
        Rgba32 left = px[(30 * 64) + 10];
        Rgba32 middle = px[(30 * 64) + 30];
        Rgba32 right = px[(30 * 64) + 49];
        Assert.Equal(Red, left);
        Assert.InRange(middle.R, 120, 135);
        Assert.InRange(middle.G, 120, 135);
        Assert.InRange(right.G, 245, 255);
        Assert.InRange(right.R, 0, 10);
        Assert.Equal(0, middle.B);
    }

    [Fact]
    public void TheGradientsOpacityStopsFadeTheOverlayIntoTheLayer()
    {
        using Document doc = Doc(64, 64, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(64, 64, 10, 10, 40, new Rgba32(0, 0, 255, 255)), 64, 64, 0, 0);
        var fade = new EffectGradient([new GradientColorStop(0, 0.5f, new RgbaFloat(1, 0, 0, 1)), new GradientColorStop(1, 0.5f, new RgbaFloat(1, 0, 0, 1))],
            [new GradientOpacityStop(0, 0.5f, 0), new GradientOpacityStop(1, 0.5f, 1)], smoothness: 0f);
        l.Style = new LayerStyle([new GradientOverlayEffect { Fill = new EffectGradientFill(fade, angle: 0) }]) { AlignBox = new ImageRect(10, 10, 40, 40) };
        Rgba32[] px = Pixels(doc.Render());
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[(30 * 64) + 10]);
        Assert.InRange(px[(30 * 64) + 49].R, 245, 255);
        Assert.InRange(px[(30 * 64) + 30].B, 100, 155);
    }

    [Fact]
    public void AReversedGradientAndAnUnalignedOneUseTheDocumentBox()
    {
        using Document doc = Doc(64, 64, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(64, 64, 0, 0, 64, new Rgba32(0, 0, 255, 255)), 64, 64, 0, 0);
        l.Style = new LayerStyle([new GradientOverlayEffect { Fill = new EffectGradientFill(RedToGreen(), angle: 0, reverse: true, alignWithLayer: false) }]) { AlignBox = new ImageRect(20, 20, 10, 10) };
        Rgba32[] px = Pixels(doc.Render());
        Assert.InRange(px[(30 * 64) + 0].G, 250, 255); // reversed: green at the left of the document, not of the small box
        Assert.InRange(px[(30 * 64) + 63].R, 245, 255);
    }

    [Fact]
    public void APatternOverlayTilesFromTheLayersCorner()
    {
        using Document doc = Doc(64, 64, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(64, 64, 10, 10, 40, new Rgba32(0, 0, 255, 255)), 64, 64, 0, 0);
        byte[] tile = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 0, 255, 255, 255, 0, 255]; // red green / black yellow
        l.Style = new LayerStyle([new PatternOverlayEffect { Fill = new EffectPatternFill(new EffectPattern(2, 2, tile, "t")) }]) { AlignBox = new ImageRect(10, 10, 40, 40) };
        Rgba32[] px = Pixels(doc.Render());

        // The tile starts one pixel right of the shape's corner and at its top edge.
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[(10 * 64) + 11]);
        Assert.Equal(new Rgba32(0, 255, 0, 255), px[(10 * 64) + 12]);
        Assert.Equal(new Rgba32(0, 0, 0, 255), px[(11 * 64) + 11]);
        Assert.Equal(new Rgba32(255, 255, 0, 255), px[(11 * 64) + 12]);
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[(10 * 64) + 13]);
        Assert.Equal(White, px[(10 * 64) + 9]);
    }

    [Fact]
    public void AGradientStrokePaintsTheRingAndShapeBurstFollowsTheEdgeDistance()
    {
        using Document doc = Doc(80, 80, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(80, 80, 20, 20, 40, new Rgba32(0, 0, 255, 255)), 80, 80, 0, 0);
        var burst = new EffectGradientFill(RedToGreen(), EffectGradientStyle.ShapeBurst);
        l.Style = new LayerStyle([new StrokeEffect { Position = StrokePosition.Inside, Size = 10, Gradient = burst }]);
        Rgba32[] px = Pixels(doc.Render());
        Rgba32 edge = px[(40 * 80) + 20];
        Rgba32 deeper = px[(40 * 80) + 27];
        Assert.True(edge.R > 200 && edge.G < 60, $"edge {edge}");
        Assert.True(deeper.G > edge.G + 100 && deeper.R < edge.R - 100, $"deeper {deeper}");
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[(40 * 80) + 40]);
    }

    [Fact]
    public void AGradientGlowStartsWithItsFirstColorAtTheShape()
    {
        using Document doc = Doc(100, 100, new Rgba32(0, 0, 0, 255));
        PixelLayer l = AddPixels(doc, null, "box", Square(100, 100, 30, 30, 40, new Rgba32(0, 0, 255, 255)), 100, 100, 0, 0);
        l.Style = new LayerStyle([new OuterGlowEffect { Size = 20, Spread = 0.5f, Range = 1f, Opacity = 1f, Blend = BlendMode.Normal, Gradient = RedToGreen() }]);
        Rgba32[] px = Pixels(doc.Render());
        Rgba32 near = px[(50 * 100) + 28];
        Rgba32 far = px[(50 * 100) + 14];
        Assert.True(near.R > near.G, $"near {near}");
        Assert.True(far.G > far.R, $"far {far}");
    }

    private static int Luma(Rgba32 c) => c.R + c.G + c.B;

    [Fact]
    public void AnInnerBevelLightsTheEdgeFacingTheLightAndShadesTheOppositeOne()
    {
        using Document doc = Doc(80, 80, Gray);
        PixelLayer l = AddPixels(doc, null, "box", Square(80, 80, 20, 20, 40, Gray), 80, 80, 0, 0);
        l.Style = new LayerStyle([new BevelEmbossEffect { Technique = BevelTechnique.ChiselHard, Size = 6, Angle = 90, Altitude = 30 }]);
        Rgba32[] px = Pixels(doc.Render());

        int flat = Luma(px[(40 * 80) + 40]);
        Assert.Equal(Luma(Gray), flat);
        Assert.True(Luma(px[(22 * 80) + 40]) > flat + 30, "the top edge faces the light");
        Assert.True(Luma(px[(57 * 80) + 40]) < flat - 30, "the bottom edge faces away");
        Assert.True(Luma(px[(40 * 80) + 22]) < flat - 30, "a steep slope that faces sideways is darker than the flat middle, as with any diffuse light");
        Assert.Equal(Luma(Gray), Luma(px[(10 * 80) + 40])); // nothing outside an inner bevel

        // Down turns the slope over.
        l.Style = new LayerStyle([new BevelEmbossEffect { Technique = BevelTechnique.ChiselHard, Size = 6, Angle = 90, Altitude = 30, Up = false }]);
        px = Pixels(doc.Render());
        Assert.True(Luma(px[(22 * 80) + 40]) < flat - 30);
        Assert.True(Luma(px[(57 * 80) + 40]) > flat + 30);
    }

    [Fact]
    public void AnOuterBevelAndAnEmbossDrawOutsideTheShapeToo()
    {
        using Document doc = Doc(80, 80, Gray);
        PixelLayer l = AddPixels(doc, null, "box", Square(80, 80, 20, 20, 40, Gray), 80, 80, 0, 0);
        l.Style = new LayerStyle([new BevelEmbossEffect { Style = BevelStyle.OuterBevel, Technique = BevelTechnique.ChiselHard, Size = 6, Angle = 90 }]);
        Rgba32[] px = Pixels(doc.Render());
        Assert.True(Luma(px[(17 * 80) + 40]) > Luma(Gray) + 30, "outside the top edge");
        Assert.Equal(Luma(Gray), Luma(px[(23 * 80) + 40])); // the inside is not touched by an outer bevel

        l.Style = new LayerStyle([new BevelEmbossEffect { Style = BevelStyle.Emboss, Technique = BevelTechnique.Smooth, Size = 6, Angle = 90 }]);
        px = Pixels(doc.Render());
        Assert.True(Luma(px[(18 * 80) + 40]) > Luma(Gray), "emboss lights outside the edge");
        Assert.True(Luma(px[(21 * 80) + 40]) > Luma(Gray), "and inside it");
    }

    [Fact]
    public void ABevelDoesNotTouchADocumentWithoutOne()
    {
        using Document a = Doc(40, 40, White);
        AddPixels(a, null, "box", Square(40, 40, 10, 10, 20, Red), 40, 40, 0, 0);
        using Document b = Doc(40, 40, White);
        PixelLayer l = AddPixels(b, null, "box", Square(40, 40, 10, 10, 20, Red), 40, 40, 0, 0);
        l.Style = new LayerStyle([new BevelEmbossEffect { Enabled = false }, new GradientOverlayEffect { Enabled = false, Fill = new EffectGradientFill(RedToGreen()) }]);
        AssertSame(Pixels(a.Render()), Pixels(b.Render()));
    }

    [Fact]
    public void AnEditNextToBevelsAndOverlaysRecomputesTheTilesTheyReach()
    {
        const int W = 768, H = 768;

        static Document Build(Rgba32[] px)
        {
            var doc = new Document(W, H, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
            PixelLayer l = AddPixels(doc, null, "box", px, W, H, 0, 0);
            l.Style = new LayerStyle([
                new GradientOverlayEffect { Opacity = 0.6f, Fill = new EffectGradientFill(RedToGreen(), EffectGradientStyle.Radial, alignWithLayer: false) },
                new BevelEmbossEffect { Style = BevelStyle.Emboss, Technique = BevelTechnique.Smooth, Size = 40, Soften = 3 },
                new BevelEmbossEffect { Technique = BevelTechnique.ChiselSoft, Size = 25, Angle = 20 },
                new StrokeEffect { Size = 5, Position = StrokePosition.Inside, Gradient = new EffectGradientFill(RedToGreen(), EffectGradientStyle.ShapeBurst) },
            ]);
            return doc;
        }

        Rgba32[] start = Square(W, H, 200, 200, 300, new Rgba32(30, 60, 200, 255));
        using Document doc = Build(start);
        Pixels(doc.Render());
        PixelLayer layer = (PixelLayer)doc.Root.Children[0];
        long computed = doc.TilesComputed;

        // A bite out of the box near the middle of its right edge: the bevel inside the box reaches 40 px around it, into the neighbouring tiles.
        layer.Pixels.Fill(default(Rgba32), OneImage.Drawing.Path.Rectangle(480, 330, 20, 40), antialias: false, composition: AlphaComposition.Src);
        Rgba32[] incremental = Pixels(doc.Render());
        long recomputed = doc.TilesComputed - computed;

        Rgba32[] edited = Square(W, H, 200, 200, 300, new Rgba32(30, 60, 200, 255));
        for (int y = 330; y < 370; y++)
        {
            for (int x = 480; x < 500; x++)
            {
                edited[(y * W) + x] = default;
            }
        }

        using Document fresh = Build(edited);
        AssertSame(Pixels(fresh.Render()), incremental);
        Assert.True(recomputed >= 2 && recomputed < 36, $"{recomputed} tiles were recomputed");
    }

    [Fact]
    public void AnEditInsideAnInnerEffectsFootprintReachesAcrossATileEdge()
    {
        // An inner bevel at a tile boundary: the edit sits in one tile, the lighting it changes in the next.
        const int W = 384, H = 256;

        static Document Build(Rgba32[] px)
        {
            var doc = new Document(W, H, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
            PixelLayer l = AddPixels(doc, null, "box", px, W, H, 0, 0);
            l.Style = new LayerStyle([new BevelEmbossEffect { Technique = BevelTechnique.ChiselHard, Size = 30 }, new InnerShadowEffect { Distance = 0, Size = 30 }]);
            return doc;
        }

        Rgba32[] start = Square(W, H, 20, 20, 200, new Rgba32(30, 60, 200, 255));
        start.AsSpan().Clear();
        for (int y = 20; y < 220; y++)
        {
            for (int x = 20; x < 360; x++)
            {
                start[(y * W) + x] = new Rgba32(30, 60, 200, 255);
            }
        }

        using Document doc = Build(start);
        Pixels(doc.Render());
        PixelLayer layer = (PixelLayer)doc.Root.Children[0];
        layer.Pixels.Fill(default(Rgba32), OneImage.Drawing.Path.Rectangle(125, 100, 3, 3), antialias: false, composition: AlphaComposition.Src);
        Rgba32[] incremental = Pixels(doc.Render());

        Rgba32[] edited = (Rgba32[])start.Clone();
        for (int y = 100; y < 103; y++)
        {
            for (int x = 125; x < 128; x++)
            {
                edited[(y * W) + x] = default;
            }
        }

        using Document fresh = Build(edited);
        AssertSame(Pixels(fresh.Render()), incremental);
    }

    [Fact]
    public void ThePaintEffectsAreBoundedByTheirLimits()
    {
        RgbaFloat c = new(1, 0, 0, 1);
        Assert.Throws<ArgumentException>(() => new EffectGradient([]));
        Assert.Throws<ArgumentException>(() => new EffectGradient(Enumerable.Range(0, EffectGradient.MaxStops + 1).Select(static i => new GradientColorStop(i / 300f, 0.5f, new RgbaFloat(1, 0, 0, 1)))));
        Assert.Throws<ArgumentException>(() => new EffectGradient([new GradientColorStop(1.5f, 0.5f, c)]));
        Assert.Throws<ArgumentException>(() => new EffectGradient([new GradientColorStop(0, 0.5f, new RgbaFloat(float.NaN, 0, 0, 1))]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectGradient([new GradientColorStop(0, 0.5f, c)], smoothness: 2f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectGradientFill(RedToGreen(), scale: 0.05f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectGradientFill(RedToGreen(), angle: float.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new EffectPattern(0, 4, []));
        Assert.Throws<ArgumentException>(() => new EffectPattern(4, 4, new byte[10]));
        Assert.Throws<ArgumentException>(() => new EffectPattern(1 << 13, 1 << 13, new byte[16]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectPatternFill(new EffectPattern(1, 1, new byte[4]), scale: 20f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectPatternFill(new EffectPattern(1, 1, new byte[4]), phaseX: 1e9f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BevelEmbossEffect { Size = 251 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BevelEmbossEffect { Depth = 11 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BevelEmbossEffect { Soften = 17 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BevelEmbossEffect { Altitude = 91 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BevelEmbossEffect { HighlightOpacity = 1.1f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BevelEmbossEffect { Angle = float.NaN });
    }

    [Fact]
    public void ABevelAtTheLargestSizeAllocatesAFixedAmountOfWork()
    {
        using Document doc = Doc(300, 300, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(300, 300, 100, 100, 100, Red), 300, 300, 0, 0);
        Pixels(doc.Render());
        l.Style = new LayerStyle([new BevelEmbossEffect { Size = LayerEffect.MaxSize, Soften = BevelEmbossEffect.MaxSoften, Depth = BevelEmbossEffect.MaxDepth, Style = BevelStyle.PillowEmboss }]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Pixels(doc.Render());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256L << 20, $"{allocated} bytes allocated");
    }

    [Fact]
    public void TheNewEffectsAreDescribed()
    {
        using Document doc = Doc(32, 32, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(32, 32, 4, 4, 8, Red), 32, 32, 0, 0);
        l.Style = new LayerStyle([
            new GradientOverlayEffect { Fill = new EffectGradientFill(RedToGreen(), EffectGradientStyle.Diamond, angle: 45, scale: 1.5f, reverse: true) },
            new PatternOverlayEffect { Fill = new EffectPatternFill(new EffectPattern(1, 1, new byte[4], "abc"), scale: 2f, phaseX: 3) },
            new BevelEmbossEffect { Style = BevelStyle.PillowEmboss, Technique = BevelTechnique.ChiselSoft, Up = false, GlossContour = new EffectContour([new ContourPoint(0, 0), new ContourPoint(0.5f, 0.75f), new ContourPoint(1, 1)]) },
        ]);
        Dictionary<string, object> d = l.Describe().Parameters.ToDictionary(static p => p.Name, static p => p.Value);
        Assert.Equal("gradient-overlay", d["style.0.kind"]);
        Assert.Equal("diamond", d["style.0.gradient-style"]);
        Assert.Equal(45.0, d["style.0.gradient-angle"]);
        Assert.Equal(true, d["style.0.gradient-reverse"]);
        Assert.Equal("pattern-overlay", d["style.1.kind"]);
        Assert.Equal("abc", d["style.1.pattern"]);
        Assert.Equal(2.0, d["style.1.pattern-scale"]);
        Assert.Equal("bevel-emboss", d["style.2.kind"]);
        Assert.Equal("pillow-emboss", d["style.2.style"]);
        Assert.Equal("chisel-soft", d["style.2.technique"]);
        Assert.Equal("down", d["style.2.direction"]);
        Assert.Equal("0,0;0.5,0.75;1,1", d["style.2.gloss-contour"]);
    }
}
