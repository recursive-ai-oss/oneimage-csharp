using OneImage.Layers;
using static OneImage.Tests.Layers.LayerCompositeTests;

namespace OneImage.Tests.Layers;

public class LayerClipRunTests
{
    private static readonly Rgba32 Bg = new(200, 100, 50, 255);
    private const int W = 300, H = 200;

    /// <summary>A base that covers the left two thirds with a soft-edged alpha ramp, so the clip shape is not a hard rectangle.</summary>
    private static Rgba32[] BaseShape()
    {
        var px = new Rgba32[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                byte a = (byte)(x > 180 ? 255 - ((x - 180) * 12) : 255);
                px[(y * W) + x] = new Rgba32(30, 90, 160, a);
            }
        }

        return px;
    }

    private static Document NewDoc() => new(W, H, new DocumentOptions { Background = Bg });

    [Fact]
    public void PassThroughGroupInAClipRunEqualsItsLayersClippedOneByOne()
    {
        Rgba32[] c1 = Noise(W, H, 11);
        Rgba32[] c2 = Noise(250, 150, 12);

        using Document flat = NewDoc();
        AddPixels(flat, null, "base", BaseShape(), W, H, 0, 0);
        AddPixels(flat, null, "c1", c1, W, H, 0, 0).ClipToBelow = true;
        PixelLayer f2 = AddPixels(flat, null, "c2", c2, 250, 150, 20, 10);
        f2.ClipToBelow = true;
        f2.BlendMode = BlendMode.Multiply;

        using Document grouped = NewDoc();
        AddPixels(grouped, null, "base", BaseShape(), W, H, 0, 0);
        GroupLayer g = grouped.Add(grouped.CreateGroup("g", passThrough: true));
        g.ClipToBelow = true;
        AddPixels(grouped, g, "c1", c1, W, H, 0, 0);
        AddPixels(grouped, g, "c2", c2, 250, 150, 20, 10).BlendMode = BlendMode.Multiply;

        AssertSame(Pixels(flat.Render()), Pixels(grouped.Render()));
    }

    [Fact]
    public void PassThroughGroupOpacityMixesTheSurfaceBeforeAndAfter()
    {
        Rgba32[] c1 = Noise(W, H, 21, opaque: true);

        using Document without = NewDoc();
        AddPixels(without, null, "base", BaseShape(), W, H, 0, 0);

        using Document full = NewDoc();
        AddPixels(full, null, "base", BaseShape(), W, H, 0, 0);
        AddPixels(full, null, "c1", c1, W, H, 0, 0).ClipToBelow = true;

        using Document half = NewDoc();
        AddPixels(half, null, "base", BaseShape(), W, H, 0, 0);
        GroupLayer g = half.Add(half.CreateGroup("g", passThrough: true));
        g.ClipToBelow = true;
        g.Opacity = 0.5f;
        AddPixels(half, g, "c1", c1, W, H, 0, 0);

        Rgba32[] a = Pixels(without.Render());
        Rgba32[] b = Pixels(full.Render());
        Rgba32[] m = Pixels(half.Render());
        for (int i = 0; i < m.Length; i++)
        {
            Assert.InRange(m[i].R, ((a[i].R + b[i].R) / 2) - 2, ((a[i].R + b[i].R) / 2) + 2);
            Assert.InRange(m[i].G, ((a[i].G + b[i].G) / 2) - 2, ((a[i].G + b[i].G) / 2) + 2);
            Assert.InRange(m[i].B, ((a[i].B + b[i].B) / 2) - 2, ((a[i].B + b[i].B) / 2) + 2);
        }
    }

    [Fact]
    public void ClippedPassThroughGroupWithAMaskKeepsTheBaseShape()
    {
        Rgba32[] c1 = Noise(W, H, 31, opaque: true);
        using Document doc = NewDoc();
        AddPixels(doc, null, "base", BaseShape(), W, H, 0, 0);
        GroupLayer g = doc.Add(doc.CreateGroup("g", passThrough: true));
        g.ClipToBelow = true;
        AddPixels(doc, g, "c1", c1, W, H, 0, 0);
        LayerMask m = g.AddMask(revealAll: false, area: doc.Bounds);
        m.Pixels.Fill(new Rgba32(255, 255, 255, 255), OneImage.Drawing.Path.Rectangle(0, 0, 100, H), antialias: false);

        Rgba32[] px = Pixels(doc.Render());
        Assert.Equal(c1[10], px[10]);                                    // inside base and mask: the clipped content
        Assert.Equal(new Rgba32(30, 90, 160, 255), px[150]);             // inside base, outside mask: the base alone
        Assert.Equal(Bg, px[250]);                                       // outside the base: the backdrop, whatever the group holds
    }

    [Fact]
    public void ClippedIsolatedGroupBlendsWithItsOwnModeOntoTheBase()
    {
        Rgba32[] c1 = Noise(W, H, 41, opaque: true);

        using Document viaLayer = NewDoc();
        AddPixels(viaLayer, null, "base", BaseShape(), W, H, 0, 0);
        PixelLayer l = AddPixels(viaLayer, null, "c1", c1, W, H, 0, 0);
        l.ClipToBelow = true;
        l.BlendMode = BlendMode.Multiply;
        l.Opacity = 0.7f;

        using Document viaGroup = NewDoc();
        AddPixels(viaGroup, null, "base", BaseShape(), W, H, 0, 0);
        GroupLayer g = viaGroup.Add(viaGroup.CreateGroup("g", passThrough: false));
        g.ClipToBelow = true;
        g.BlendMode = BlendMode.Multiply;
        g.Opacity = 0.7f;
        AddPixels(viaGroup, g, "c1", c1, W, H, 0, 0);

        AssertSame(Pixels(viaLayer.Render()), Pixels(viaGroup.Render()));
    }

    [Fact]
    public void HiddenOrFullyTransparentBaseHidesEverythingClippedToIt()
    {
        Rgba32[] c1 = Noise(W, H, 51, opaque: true);
        foreach (int kind in new[] { 0, 1 })
        {
            using Document doc = NewDoc();
            PixelLayer b = AddPixels(doc, null, "base", BaseShape(), W, H, 0, 0);
            AddPixels(doc, null, "c1", c1, W, H, 0, 0).ClipToBelow = true;
            GroupLayer g = doc.Add(doc.CreateGroup("g", passThrough: true));
            g.ClipToBelow = true;
            AddPixels(doc, g, "c2", c1, W, H, 0, 0);
            if (kind == 0)
            {
                b.Visible = false;
            }
            else
            {
                b.Opacity = 0f;
            }

            Assert.All(Pixels(doc.Render()), p => Assert.Equal(Bg, p));
        }
    }
}
