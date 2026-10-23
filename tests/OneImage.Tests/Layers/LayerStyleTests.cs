using OneImage.Drawing;
using OneImage.Layers;
using static OneImage.Tests.Layers.LayerCompositeTests;

namespace OneImage.Tests.Layers;

public class LayerStyleTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);
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

    [Fact]
    public void OutsideStrokeIsAnExactRingOnWholePixelEdges()
    {
        using Document doc = Doc(64, 64, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(64, 64, 20, 20, 24, new Rgba32(0, 0, 255, 255)), 64, 64, 0, 0);
        l.Style = new LayerStyle([new StrokeEffect { Position = StrokePosition.Outside, Size = 3, Color = Red }]);
        Rgba32[] px = Pixels(doc.Render());

        // Straight edges: three full pixels outside, none beyond, the box itself untouched.
        Assert.Equal(Red, px[(32 * 64) + 17]);
        Assert.Equal(Red, px[(32 * 64) + 19]);
        Assert.Equal(White, px[(32 * 64) + 16]);
        Assert.Equal(Red, px[(17 * 64) + 32]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[(32 * 64) + 20]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[(32 * 64) + 43]);
        Assert.Equal(Red, px[(32 * 64) + 44]);
        Assert.Equal(White, px[(32 * 64) + 47]);
    }

    [Fact]
    public void InsideStrokeReplacesThePixelsAtTheEdgeAndLeavesTheMiddle()
    {
        using Document doc = Doc(64, 64, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(64, 64, 20, 20, 24, new Rgba32(0, 0, 255, 255)), 64, 64, 0, 0);
        l.Style = new LayerStyle([new StrokeEffect { Position = StrokePosition.Inside, Size = 2, Color = Red }]);
        Rgba32[] px = Pixels(doc.Render());
        Assert.Equal(Red, px[(32 * 64) + 20]);
        Assert.Equal(Red, px[(32 * 64) + 21]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[(32 * 64) + 22]);
        Assert.Equal(White, px[(32 * 64) + 19]);
    }

    [Fact]
    public void ColorOverlayRecolorsThePixelsAndKeepsTheirAlpha()
    {
        // A half-transparent edge pixel stays half-transparent: the overlay replaces its color, it does not add coverage.
        var px = new Rgba32[8 * 8];
        for (int y = 0; y < 8; y++)
        {
            px[(y * 8) + 3] = new Rgba32(0, 0, 255, 128);
            for (int x = 4; x < 8; x++)
            {
                px[(y * 8) + x] = new Rgba32(0, 0, 255, 255);
            }
        }

        using Document doc = Doc(8, 8, White);
        PixelLayer l = AddPixels(doc, null, "edge", px, 8, 8, 0, 0);
        l.Style = new LayerStyle([new ColorOverlayEffect { Color = Red }]);
        Rgba32[] outPx = Pixels(doc.Render());
        Assert.Equal(Red, outPx[(4 * 8) + 5]);
        // 50 % red over white: (255, 127, 127).
        Rgba32 edge = outPx[(4 * 8) + 3];
        Assert.Equal(255, edge.R);
        Assert.InRange(edge.G, 126, 128);
        Assert.InRange(edge.B, 126, 128);
        Assert.Equal(White, outPx[(4 * 8) + 1]);
    }

    [Fact]
    public void DropShadowFallsAwayFromTheLightAndIsHiddenUnderTheLayer()
    {
        using Document doc = Doc(200, 200, White);
        // Light from the top (90 degrees): the shadow falls straight down.
        PixelLayer l = AddPixels(doc, null, "box", Square(200, 200, 70, 70, 60, Red), 200, 200, 0, 0);
        l.Style = new LayerStyle([new DropShadowEffect { Angle = 90, Distance = 20, Size = 0, Opacity = 1f, Blend = BlendMode.Normal, Color = new Rgba32(0, 0, 0, 255) }]);
        Rgba32[] px = Pixels(doc.Render());
        Assert.Equal(new Rgba32(0, 0, 0, 255), px[(140 * 200) + 100]); // below the box, inside the shifted copy
        Assert.Equal(White, px[(155 * 200) + 100]);                    // beyond the shifted copy
        Assert.Equal(White, px[(100 * 200) + 60]);                     // left of it: nothing
        Assert.Equal(Red, px[(100 * 200) + 100]);                      // the layer is on top
    }

    [Fact]
    public void ZeroFillOpacityKeepsTheShadowOnlyOutsideTheShape()
    {
        using Document doc = Doc(200, 200, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(200, 200, 70, 70, 60, Red), 200, 200, 0, 0);
        l.FillOpacity = 0f;
        l.Style = new LayerStyle([new DropShadowEffect { Angle = 90, Distance = 20, Size = 0, Opacity = 1f, Blend = BlendMode.Normal, Color = new Rgba32(0, 0, 0, 255) }]);
        Rgba32[] px = Pixels(doc.Render());
        Assert.Equal(White, px[(100 * 200) + 100]);                    // knocked out under the (invisible) layer
        Assert.Equal(new Rgba32(0, 0, 0, 255), px[(140 * 200) + 100]);

        l.Style = new LayerStyle([new DropShadowEffect { Angle = 90, Distance = 20, Size = 0, Opacity = 1f, Blend = BlendMode.Normal, Color = new Rgba32(0, 0, 0, 255), KnockedOut = false }]);
        px = Pixels(doc.Render());
        Assert.Equal(new Rgba32(0, 0, 0, 255), px[(100 * 200) + 100]); // shows through
    }

    [Fact]
    public void LayerOpacityScalesEffectsButFillOpacityDoesNotScaleAStroke()
    {
        using Document doc = Doc(64, 64, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(64, 64, 20, 20, 24, new Rgba32(0, 0, 255, 255)), 64, 64, 0, 0);
        l.FillOpacity = 0f;
        l.Style = new LayerStyle([new StrokeEffect { Position = StrokePosition.Outside, Size = 3, Color = Red }]);
        Rgba32[] px = Pixels(doc.Render());
        Assert.Equal(Red, px[(32 * 64) + 18]);
        Assert.Equal(White, px[(32 * 64) + 30]); // the invisible fill

        l.Opacity = 0.5f;
        px = Pixels(doc.Render());
        Rgba32 half = px[(32 * 64) + 18];
        Assert.Equal(255, half.R);
        Assert.InRange(half.G, 126, 128);
    }

    [Fact]
    public void ADisabledEffectDrawsNothing()
    {
        using Document doc = Doc(64, 64, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(64, 64, 20, 20, 24, Red), 64, 64, 0, 0);
        Rgba32[] plain = Pixels(doc.Render());
        l.Style = new LayerStyle([new StrokeEffect { Enabled = false, Size = 5, Color = new Rgba32(0, 255, 0, 255) }]);
        AssertSame(plain, Pixels(doc.Render()));
    }

    [Fact]
    public void ADocumentWithoutStylesRendersAsBefore()
    {
        using Document a = Doc(300, 200, White);
        using Document b = Doc(300, 200, White);
        foreach (Document d in new[] { a, b })
        {
            AddPixels(d, null, "n", Noise(300, 200, 5), 300, 200, 0, 0).Opacity = 0.7f;
        }

        b.Root.Children[0].Style = new LayerStyle([]);
        AssertSame(Pixels(a.Render()), Pixels(b.Render()));
    }

    [Fact]
    public void AnEditNextToAnEffectRecomputesTheTilesTheEffectReaches()
    {
        const int W = 1024, H = 1024;

        static Document Build(Rgba32[] px)
        {
            var doc = new Document(W, H, new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
            PixelLayer l = AddPixels(doc, null, "box", px, W, H, 0, 0);
            l.Style = new LayerStyle([
                new DropShadowEffect { Angle = 180, Distance = 90, Size = 40, Opacity = 0.8f },
                new OuterGlowEffect { Size = 30, Color = new Rgba32(255, 0, 0, 255), Blend = BlendMode.Normal },
                new StrokeEffect { Size = 6, Color = new Rgba32(0, 128, 0, 255) },
            ]);
            return doc;
        }

        Rgba32[] start = Square(W, H, 400, 400, 120, new Rgba32(30, 60, 200, 255));
        using Document doc = Build(start);
        Pixels(doc.Render());
        PixelLayer layer = (PixelLayer)doc.Root.Children[0];
        long computed = doc.TilesComputed;

        // Grow the box on its right edge: the shadow falls right (light from the left), 90 px, so tiles up to x = 520 + 90 + 40 + 2 change.
        layer.Pixels.Fill(new Rgba32(30, 60, 200, 255), OneImage.Drawing.Path.Rectangle(520, 400, 70, 120), antialias: false);
        Rgba32[] incremental = Pixels(doc.Render());
        long recomputed = doc.TilesComputed - computed;

        Rgba32[] edited = Square(W, H, 400, 400, 120, new Rgba32(30, 60, 200, 255));
        for (int y = 400; y < 520; y++)
        {
            for (int x = 520; x < 590; x++)
            {
                edited[(y * W) + x] = new Rgba32(30, 60, 200, 255);
            }
        }

        using Document fresh = Build(edited);
        AssertSame(Pixels(fresh.Render()), incremental);

        // The edit alone touches the tile column of x = 512..639; its shadow reaches the next columns, but not the whole document.
        Assert.True(recomputed > 2, $"only {recomputed} tiles were recomputed");
        Assert.True(recomputed < 64, $"{recomputed} of 64 tiles were recomputed");
    }

    [Fact]
    public void ChangingTheStyleRecomputesWhatTheOldAndNewEffectsReach()
    {
        using Document doc = Doc(512, 512, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(512, 512, 200, 200, 60, Red), 512, 512, 0, 0);
        Pixels(doc.Render());
        long before = doc.TilesComputed;
        l.Style = new LayerStyle([new OuterGlowEffect { Size = 100, Color = new Rgba32(0, 0, 255, 255), Blend = BlendMode.Normal, Opacity = 1 }]);
        Rgba32[] px = Pixels(doc.Render());
        Assert.True(doc.TilesComputed > before + 1);
        Assert.NotEqual(White, px[(230 * 512) + 150]); // inside the glow, left of the box
        l.Style = null;
        px = Pixels(doc.Render());
        Assert.Equal(White, px[(230 * 512) + 150]);
    }

    [Fact]
    public void EffectsAreBoundedByTheirLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DropShadowEffect { Size = 251 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DropShadowEffect { Distance = 30001 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DropShadowEffect { Spread = 1.5f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeEffect { Size = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColorOverlayEffect { Opacity = 2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new InnerShadowEffect { Angle = float.NaN });
        Assert.Throws<ArgumentException>(() => new LayerStyle(Enumerable.Range(0, LayerStyle.MaxEffects + 1).Select(static _ => (LayerEffect)new ColorOverlayEffect())));
        Assert.Throws<ArgumentException>(() => new EffectContour([new ContourPoint(0, 0)]));
        Assert.Throws<ArgumentException>(() => new EffectContour([new ContourPoint(0, 0), new ContourPoint(0, 1)]));
    }

    [Fact]
    public void AHugeDistanceAllocatesNoMoreThanASmallOne()
    {
        // A shadow 30,000 px away reaches nothing of a 300 px document; the work depends on the blur size, not on the distance.
        using Document doc = Doc(300, 300, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(300, 300, 100, 100, 60, Red), 300, 300, 0, 0);
        Rgba32[] plain = Pixels(doc.Render());
        l.Style = new LayerStyle([new DropShadowEffect { Distance = LayerEffect.MaxDistance, Size = LayerEffect.MaxSize, Angle = 0 }]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Rgba32[] px = Pixels(doc.Render());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        AssertSame(plain, px);
        Assert.True(allocated < 256L << 20, $"{allocated} bytes allocated");
    }

    [Fact]
    public void StyleParametersAreDescribed()
    {
        using Document doc = Doc(32, 32, White);
        PixelLayer l = AddPixels(doc, null, "box", Square(32, 32, 4, 4, 8, Red), 32, 32, 0, 0);
        l.Style = new LayerStyle([new DropShadowEffect { Angle = 90, Distance = 7, Contour = new EffectContour([new ContourPoint(0, 0), new ContourPoint(0.5f, 0.75f), new ContourPoint(1, 1)]) }, new StrokeEffect { Position = StrokePosition.Center }]);
        Dictionary<string, object> d = l.Describe().Parameters.ToDictionary(static p => p.Name, static p => p.Value);
        Assert.Equal(2L, d["style.count"]);
        Assert.Equal("drop-shadow", d["style.0.kind"]);
        Assert.Equal("multiply", d["style.0.blend"]);
        Assert.Equal(7.0, d["style.0.distance"]);
        Assert.Equal("0,0;0.5,0.75;1,1", d["style.0.contour"]);
        Assert.Equal("stroke", d["style.1.kind"]);
        Assert.Equal("center", d["style.1.position"]);
    }

    [Fact]
    public void EffectsOfAClippedLayerAreClippedToItsBase()
    {
        using Document doc = Doc(160, 128, White);
        AddPixels(doc, null, "base", Square(160, 128, 20, 20, 100, new Rgba32(0, 0, 255, 255)), 160, 128, 0, 0);
        PixelLayer top = AddPixels(doc, null, "top", Square(160, 128, 30, 30, 20, new Rgba32(0, 255, 0, 255)), 160, 128, 0, 0);
        top.ClipToBelow = true;
        top.Style = new LayerStyle([new DropShadowEffect { Angle = 180, Distance = 50, Size = 0, Opacity = 1, Blend = BlendMode.Normal }]);
        Rgba32[] px = Pixels(doc.Render());
        // Light from the left: the shadow lands 50 px to the right, on the base, and is cut off where the base ends.
        Assert.Equal(new Rgba32(0, 0, 0, 255), px[(40 * 160) + 90]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), px[(100 * 160) + 90]);
        Assert.Equal(White, px[(40 * 160) + 130]);

        top.Style = new LayerStyle([new DropShadowEffect { Angle = 180, Distance = 105, Size = 0, Opacity = 1, Blend = BlendMode.Normal }]);
        px = Pixels(doc.Render());
        Assert.Equal(White, px[(40 * 160) + 140]); // beyond the base: nothing
    }
}
