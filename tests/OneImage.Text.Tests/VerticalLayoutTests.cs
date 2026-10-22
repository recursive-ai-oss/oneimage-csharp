using System.Drawing;

namespace OneImage.Text.Tests;

public class VerticalLayoutTests
{
    private static TextOptions Cjk(LayoutMode mode, float size = 40, float wrap = 0) => new(TestFonts.NotoCjk(size)) { LayoutMode = mode, WrappingLength = wrap, Origin = new(100, 100) };

    [Fact]
    public void BottomToTopStacksLinesUpward()
    {
        var down = TextMeasurer.GetCharacterBounds("A\nA", new TextOptions(TestFonts.Noto(40)) { Origin = new(100, 100) });
        var up = TextMeasurer.GetCharacterBounds("A\nA", new TextOptions(TestFonts.Noto(40)) { Origin = new(100, 100), LayoutMode = LayoutMode.HorizontalBottomTop });
        Assert.Equal(down[0].Ink.Y, up[1].Ink.Y, 0.01);
        Assert.Equal(down[1].Ink.Y, up[0].Ink.Y, 0.01);
        Assert.Equal(down[0].Ink.X, up[0].Ink.X, 0.01);
        Assert.Equal(TextMeasurer.Measure("A\nB", new TextOptions(TestFonts.Noto(40))).Advance, TextMeasurer.Measure("A\nB", new TextOptions(TestFonts.Noto(40)) { LayoutMode = LayoutMode.HorizontalBottomTop }).Advance);
    }

    [Fact]
    public void ColumnsAdvanceByTheVerticalMetrics()
    {
        // The CJK test font has vhea/vmtx: every glyph advances 1000 units. The Latin font has none: a line height (1362 units) is used.
        Assert.Equal(new SizeF(40, 120), TextMeasurer.Measure("\u65e5\u672c\u8a9e", Cjk(LayoutMode.VerticalLeftRight)).Advance);
        var latin = new TextOptions(TestFonts.Noto(40)) { LayoutMode = LayoutMode.VerticalLeftRight };
        SizeF a = TextMeasurer.Measure("ABC", latin).Advance;
        Assert.Equal(40, a.Width, 0.01);
        Assert.Equal(3 * 54.48f, a.Height, 0.01);
    }

    [Fact]
    public void ColumnsStackToTheRightOrTheLeft()
    {
        var lr = TextMeasurer.GetCharacterBounds("\u65e5\n\u672c", Cjk(LayoutMode.VerticalLeftRight));
        var rl = TextMeasurer.GetCharacterBounds("\u65e5\n\u672c", Cjk(LayoutMode.VerticalRightLeft));
        Assert.True(lr[0].Bounds.X < lr[1].Bounds.X);
        Assert.True(rl[0].Bounds.X > rl[1].Bounds.X);
        Assert.Equal(lr[0].Bounds.X, rl[1].Bounds.X, 0.01);
    }

    [Fact]
    public void WrappingLengthLimitsTheColumnHeight()
    {
        TextMetrics m = TextMeasurer.Measure("AB CD EF", Cjk(LayoutMode.VerticalLeftRight, wrap: 100));
        Assert.Equal(3, m.LineCount);
        Assert.Equal(120, m.Advance.Width, 0.01);
        Assert.True(m.Advance.Height <= 100);
    }

    [Fact]
    public void MixedModeLaysLatinDownAndKeepsIdeographsUpright()
    {
        var upright = TextMeasurer.GetCharacterBounds("A", Cjk(LayoutMode.HorizontalTopBottom))[0].Ink;
        var mixed = TextMeasurer.GetCharacterBounds("A\u65e5", Cjk(LayoutMode.VerticalMixedLeftRight));
        var all = TextMeasurer.GetCharacterBounds("A\u65e5", Cjk(LayoutMode.VerticalLeftRight));
        // Turned a quarter: the width and height swap, and the letters read down the column.
        Assert.Equal(upright.Width, mixed[0].Ink.Height, 0.01);
        Assert.Equal(upright.Height, mixed[0].Ink.Width, 0.01);
        Assert.Equal(upright.Width, all[0].Ink.Width, 0.01);
        Assert.Equal(all[1].Ink.Width, mixed[1].Ink.Width, 0.01);
        // The advance of a rotated letter is its horizontal advance.
        Assert.True(mixed[1].Ink.Y < all[1].Ink.Y);
    }

    [Fact]
    public void VerticalFormsReplaceHorizontalOnes()
    {
        // Some punctuation of the CJK font has vert forms: turning the feature off changes at least one glyph's ink box.
        var forms = new List<int>();
        for (int cp = 0x2010; cp < 0x3100; cp++)
        {
            if (TestFonts.Face("NotoSansCJK-Subset.otf").GetGlyphIndex(cp) == 0)
            {
                continue;
            }

            var on = Cjk(LayoutMode.VerticalLeftRight);
            var off = Cjk(LayoutMode.VerticalLeftRight);
            off.Features.Add(FontFeature.Off("vert"));
            off.Features.Add(FontFeature.Off("vrt2"));
            string s = char.ConvertFromUtf32(cp);
            if (TextMeasurer.GetCharacterBounds(s, on)[0].Ink != TextMeasurer.GetCharacterBounds(s, off)[0].Ink)
            {
                forms.Add(cp);
            }
        }

        Assert.NotEmpty(forms);
    }

    [Fact]
    public void DrawsInsideTheColumn()
    {
        var opts = Cjk(LayoutMode.VerticalRightLeft, 30);
        opts.Origin = new(100, 20);
        opts.HorizontalAlignment = HorizontalAlignment.Right;
        using var canvas = new Canvas(160, 200, PixelFormat.Rgba8);
        canvas.DrawText("\u65e5\u672c\n\u8a9e", opts, new Rgba32(255, 255, 255, 255));
        var px = new Rgba32[160 * 200];
        canvas.CopyTo<Rgba32>(px, new ImageRect(0, 0, 160, 200));
        int minX = 999, maxX = -1, minY = 999, maxY = -1;
        for (int y = 0; y < 200; y++)
        {
            for (int x = 0; x < 160; x++)
            {
                if (px[(y * 160) + x].A > 0)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        // Two 30 px columns right-aligned at x = 100: the text lies in [40, 100] and starts at the origin's y.
        Assert.InRange(minX, 38, 46);
        Assert.InRange(maxX, 90, 100);
        Assert.InRange(minY, 20, 28);
        Assert.InRange(maxY, 70, 82);
    }

    [Fact]
    public void HugeVerticalTextLaysOutQuickly()
    {
        string text = string.Concat(Enumerable.Repeat("\u65e5\u672cA b\u8a9e", 40_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TextMetrics m = TextMeasurer.Measure(text, Cjk(LayoutMode.VerticalMixedRightLeft, 16, 400));
        Assert.True(m.LineCount > 100);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
    }
}
