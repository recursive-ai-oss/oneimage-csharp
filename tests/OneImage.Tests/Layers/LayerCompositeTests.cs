using OneImage.Drawing;
using OneImage.Layers;

namespace OneImage.Tests.Layers;

public class LayerCompositeTests
{
    internal static Rgba32[] Noise(int w, int h, int seed, bool opaque = false)
    {
        var rng = new Random(seed);
        var px = new Rgba32[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), opaque ? (byte)255 : (byte)(rng.Next(4) == 0 ? 0 : rng.Next(256)));
        }

        return px;
    }

    internal static Image Solid(int w, int h, Rgba32 c)
    {
        var px = new Rgba32[w * h];
        Array.Fill(px, c);
        return Image.FromPixels<Rgba32>(px, w, h);
    }

    internal static PixelLayer AddPixels(Document doc, GroupLayer? group, string name, Rgba32[] px, int w, int h, int x, int y)
    {
        PixelLayer l = doc.CreatePixelLayer(name, w, h, x, y);
        l.Pixels.CopyFrom<Rgba32>(px, new ImageRect(0, 0, w, h));
        (group ?? doc.Root).Add(l);
        return l;
    }

    internal static Rgba32[] Pixels(Image img) => img.ToArray<Rgba32>();

    internal static void AssertSame(Rgba32[] expected, Rgba32[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (!expected[i].Equals(actual[i]))
            {
                Assert.Fail($"pixel {i}: expected {expected[i]} got {actual[i]}");
            }
        }
    }

    public static IEnumerable<object[]> Modes() => Enum.GetValues<BlendMode>().Distinct().Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(Modes))]
    public void StackEqualsCompositeChainForEveryBlendMode(BlendMode mode)
    {
        const int W = 300, H = 200;
        foreach (Rgba32 bg in new[] { new Rgba32(0, 0, 0, 0), new Rgba32(90, 140, 200, 255), new Rgba32(30, 60, 10, 128) })
        {
            using var doc = new Document(W, H, new DocumentOptions { Background = bg });
            Image reference = Solid(W, H, bg);
            (int w, int h, int x, int y, float op, BlendMode m, int seed)[] spec =
            [
                (W, H, 0, 0, 1f, BlendMode.Normal, 1),
                (180, 150, 40, 30, 0.7f, mode, 2),
                (200, 120, -50, 90, 0.4f, mode, 3),
                (100, 100, 250, 150, 1f, BlendMode.Multiply, 4),
            ];
            foreach (var s in spec)
            {
                Rgba32[] px = Noise(s.w, s.h, s.seed);
                AddPixels(doc, null, "l" + s.seed, px, s.w, s.h, s.x, s.y).Also(l =>
                {
                    l.Opacity = s.op;
                    l.BlendMode = s.m;
                });
                reference = reference.Composite(Image.FromPixels<Rgba32>(px, s.w, s.h), s.x, s.y, s.op, false, s.m);
            }

            AssertSame(Pixels(reference), Pixels(doc.Render()));
        }
    }

    [Fact]
    public void FillOpacityMultipliesOpacity()
    {
        using var doc = new Document(64, 64, new DocumentOptions { Background = new Rgba32(10, 20, 30, 255) });
        PixelLayer l = AddPixels(doc, null, "a", Noise(64, 64, 9), 64, 64, 0, 0);
        l.Opacity = 0.5f;
        l.FillOpacity = 0.5f;
        Image expected = Solid(64, 64, new Rgba32(10, 20, 30, 255)).Composite(Image.FromPixels<Rgba32>(Noise(64, 64, 9), 64, 64), 0, 0, 0.25f);
        AssertSame(Pixels(expected), Pixels(doc.Render()));
    }

    [Fact]
    public void HiddenLayersAndZeroOpacityDoNothing()
    {
        using var doc = new Document(200, 200, new DocumentOptions { Background = new Rgba32(1, 2, 3, 255) });
        AddPixels(doc, null, "a", Noise(200, 200, 1), 200, 200, 0, 0).Visible = false;
        AddPixels(doc, null, "b", Noise(200, 200, 2), 200, 200, 0, 0).Opacity = 0;
        AssertSame(Pixels(Solid(200, 200, new Rgba32(1, 2, 3, 255))), Pixels(doc.Render()));
    }

    [Fact]
    public void RegionRenderEqualsWholeRender()
    {
        using var doc = new Document(500, 400, new DocumentOptions { Background = new Rgba32(200, 200, 200, 255) });
        AddPixels(doc, null, "a", Noise(300, 300, 1), 300, 300, 100, 20);
        GroupLayer g = doc.CreateGroup("g", passThrough: false);
        doc.Add(g);
        AddPixels(doc, g, "b", Noise(250, 250, 2), 250, 250, 200, 150).BlendMode = BlendMode.Overlay;
        g.Opacity = 0.6f;
        doc.Add(doc.CreateAdjustmentLayer("adj", Adjustment.Invert())).Opacity = 0.5f;
        Image whole = doc.Render();
        Rgba32[] all = Pixels(whole);
        var region = new ImageRect(130, 90, 257, 190);
        var part = new Rgba32[region.Width * region.Height];
        doc.Render().CopyPixelsTo<Rgba32>(part, region);
        for (int y = 0; y < region.Height; y++)
        {
            for (int x = 0; x < region.Width; x++)
            {
                Assert.Equal(all[((region.Y + y) * 500) + region.X + x], part[(y * region.Width) + x]);
            }
        }
    }

    [Fact]
    public void LinearBlendingEqualsLinearCompositeChain()
    {
        using var doc = new Document(150, 150, new DocumentOptions { Background = new Rgba32(100, 100, 100, 255), BlendInLinearLight = true });
        Rgba32[] px = Noise(100, 100, 5);
        AddPixels(doc, null, "a", px, 100, 100, 20, 30).Opacity = 0.8f;
        Image expected = Solid(150, 150, new Rgba32(100, 100, 100, 255)).Composite(Image.FromPixels<Rgba32>(px, 100, 100), 20, 30, 0.8f, true);
        AssertSame(Pixels(expected), Pixels(doc.Render()));
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
