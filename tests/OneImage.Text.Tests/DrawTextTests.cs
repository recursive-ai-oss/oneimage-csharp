using System.Numerics;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage.Text.Tests;

public class DrawTextTests
{
    private static readonly Rgba32 White = new(255, 255, 255, 255);
    private static readonly Rgba32 Black = new(0, 0, 0, 255);

    private static Image Blank(int w, int h)
    {
        var px = new Rgba32[w * h];
        Array.Fill(px, White);
        return Image.FromPixels<Rgba32>(px, w, h);
    }

    private static int InkPixels(Image img) => img.ToArray<Rgba32>().Count(p => p.R < 128);

    [Fact]
    public void DrawTextEqualsFillingTheGeneratedPath()
    {
        var o = new TextOptions(TestFonts.Noto(30)) { Origin = new Vector2(10, 12) };
        Rgba32[] a = Blank(300, 80).DrawText("Hello", o, Black).ToArray<Rgba32>();
        Rgba32[] b = Blank(300, 80).Fill(Black, TextBuilder.GeneratePath("Hello", o)).ToArray<Rgba32>();
        Assert.Equal(b, a);
        Assert.Contains(a, p => p.R < 128);
    }

    [Fact]
    public void CanvasDrawsInPlaceLikeTheImage()
    {
        var o = new TextOptions(TestFonts.Noto(24)) { Origin = new Vector2(8, 8) };
        using var canvas = new Canvas(200, 60, PixelFormat.Rgba8);
        canvas.Clear(White);
        canvas.DrawText("Canvas text", o, Black);
        Rgba32[] expected = Blank(200, 60).DrawText("Canvas text", o, Black).ToArray<Rgba32>();
        Assert.Equal(expected, canvas.Snapshot().ToArray<Rgba32>());
    }

    [Fact]
    public void BrushAndPenBothPaintAndTheOutlineIsOverTheFill()
    {
        var o = new TextOptions(TestFonts.Noto(60)) { Origin = new Vector2(10, 5) };
        Image fillOnly = Blank(200, 100).DrawText("Ho", o, Brush.Solid(new Rgba32(255, 0, 0, 255)));
        Image both = Blank(200, 100).DrawText("Ho", o, Brush.Solid(new Rgba32(255, 0, 0, 255)), Pen.Solid(new Rgba32(0, 0, 255, 255), 3));
        Image penOnly = Blank(200, 100).DrawText("Ho", o, null, Pen.Solid(new Rgba32(0, 0, 255, 255), 3));
        Rgba32[] f = fillOnly.ToArray<Rgba32>();
        Rgba32[] b = both.ToArray<Rgba32>();
        Rgba32[] p = penOnly.ToArray<Rgba32>();
        Assert.Contains(b, x => x is { R: 255, G: 0, B: 0 });
        Assert.Contains(b, x => x is { R: 0, G: 0, B: 255 });
        Assert.DoesNotContain(p, x => x is { R: 255, G: 0, B: 0 });
        Assert.Contains(p, x => x is { R: 0, G: 0, B: 255 });
        Assert.NotEqual(f, b);
    }

    [Fact]
    public void NeedsABrushOrAPen()
    {
        var o = new TextOptions(TestFonts.Noto(10));
        Assert.Throws<ArgumentException>(() => Blank(10, 10).DrawText("x", o, null));
        Assert.Throws<ArgumentNullException>(() => Blank(10, 10).DrawText(null!, o, Black));
        Assert.Throws<ArgumentNullException>(() => Blank(10, 10).DrawText("x", null!, Black));
    }

    [Fact]
    public void EmptyTextDrawsNothingAndMeasuresZero()
    {
        var o = new TextOptions(TestFonts.Noto(20));
        Assert.True(TextBuilder.GeneratePath(string.Empty, o).IsEmpty);
        Assert.Equal(0, TextMeasurer.CountLines(string.Empty, o));
        Assert.Equal(default, TextMeasurer.MeasureAdvance(string.Empty, o));
        Assert.Equal(Blank(20, 20).ToArray<Rgba32>(), Blank(20, 20).DrawText(string.Empty, o, Black).ToArray<Rgba32>());
    }

    [Fact]
    public void PathBoundsAgreeWithMeasuredInkBounds()
    {
        var o = new TextOptions(TestFonts.Noto(40)) { Origin = new Vector2(30, 20), HorizontalAlignment = HorizontalAlignment.Center };
        const string Text = "Wavy Rag\u00e9\nsecond line";
        System.Drawing.RectangleF m = TextMeasurer.MeasureBounds(Text, o);
        System.Drawing.RectangleF p = TextBuilder.GeneratePath(Text, o).Bounds;

        // The path's own bounds come from flattening at 0.1 px; the measured box uses exact curve extents.
        Assert.InRange(Math.Abs(m.X - p.X), 0, 0.15);
        Assert.InRange(Math.Abs(m.Y - p.Y), 0, 0.15);
        Assert.InRange(Math.Abs(m.Right - p.Right), 0, 0.15);
        Assert.InRange(Math.Abs(m.Bottom - p.Bottom), 0, 0.15);
    }

    [Fact]
    public void CffTextDrawsAndMeasures()
    {
        var o = new TextOptions(TestFonts.NotoCjk(40)) { Origin = new Vector2(5, 5) };
        const string Text = "\u65e5\u672c\u8a9e\u3042A";
        TextMetrics m = TextMeasurer.Measure(Text, o);
        Assert.Equal(1, m.LineCount);
        Assert.InRange(m.Advance.Width, 150, 230);
        Assert.True(InkPixels(Blank(260, 60).DrawText(Text, o, Black)) > 500);
    }

    [Fact]
    public void GlyphPathMatchesItsAdvanceAndScale()
    {
        Font f = TestFonts.Noto(100);
        Path h = f.GetGlyphPath('H');
        System.Drawing.RectangleF b = h.Bounds;

        // Noto Sans H: stem at x=96, cap height 714, advance 741 (font units at 1000/em).
        Assert.InRange(b.Bottom, -0.2, 0.2);
        Assert.InRange(b.Top, -71.6, -71.2);
        Assert.Equal(74.1f, f.GetAdvance('H'), 3);
        Assert.True(f.GetGlyphPath(' ').IsEmpty);
    }

    [Fact]
    public void LineBreaksAndWrappingBehave()
    {
        var o = new TextOptions(TestFonts.Noto(20));
        Assert.Equal(2, TextMeasurer.CountLines("a\r\nb", o));
        Assert.Equal(2, TextMeasurer.CountLines("a\rb", o));
        Assert.Equal(2, TextMeasurer.CountLines("a\u2028b", o));
        Assert.Equal(1, TextMeasurer.CountLines("a\n", o));
        Assert.Equal(3, TextMeasurer.CountLines("a\n\nb", o));

        // An unbreakable word longer than the limit keeps its own line and overflows.
        o.WrappingLength = 40;
        TextMetrics m = TextMeasurer.Measure("Supercalifragilistic is long", o);
        Assert.Equal(3, m.LineCount);
        Assert.True(m.Advance.Width > 40);

        // A hyphen between letters is a break opportunity; one before a digit is not.
        o.WrappingLength = 60;
        Assert.True(TextMeasurer.CountLines("well-known multi-part", o) >= 2);
        o.WrappingLength = 25;
        Assert.Equal(1, TextMeasurer.CountLines("x-1234", o));
    }

    [Fact]
    public void TabsAdvanceByTabWidthSpaces()
    {
        Font f = TestFonts.Noto(20);
        float space = f.GetAdvance(' ');
        float a = f.GetAdvance('a');
        var o = new TextOptions(f) { TabWidth = 3 };
        Assert.Equal(a + (3 * space) + a, TextMeasurer.MeasureAdvance("a\ta", o).Width, 3);
        o.TabWidth = 0;
        Assert.Equal(2 * a, TextMeasurer.MeasureAdvance("a\ta", o).Width, 3);
    }

    [Fact]
    public void LineSpacingDpiAndOriginMoveTextAsDocumented()
    {
        Font f = TestFonts.Noto(20);
        var o = new TextOptions(f);
        TextMetrics one = TextMeasurer.Measure("H\nH", o);
        Assert.Equal(40, one.Advance.Height, 3);
        Assert.Equal(20, one.Lines[0].LineHeight, 3);
        Assert.Equal(20, one.Lines[1].Baseline - one.Lines[0].Baseline, 3);

        o.LineSpacing = 2;
        Assert.Equal(80, TextMeasurer.MeasureAdvance("H\nH", o).Height, 3);
        o.LineSpacing = 1;
        o.Dpi = 144;
        Assert.Equal(2 * TextMeasurer.MeasureAdvance("H", new TextOptions(f)).Width, TextMeasurer.MeasureAdvance("H", o).Width, 3);

        // The baseline sits where the font's ascent, centered in the one-em line box, puts it: 1069 - (1362 - 1000) / 2 = 888 units.
        o.Dpi = 72;
        o.Origin = new Vector2(50, 70);
        Assert.Equal(70 + 17.76f, TextMeasurer.Measure("H", o).Lines[0].Baseline, 3);
    }

    [Fact]
    public void VerticalAndHorizontalAlignmentMoveTheBlockAroundTheOrigin()
    {
        Font f = TestFonts.Noto(20);
        var o = new TextOptions(f) { Origin = new Vector2(100, 100) };
        float top = TextMeasurer.Measure("Hi\nthere", o).Lines[0].Baseline;
        o.VerticalAlignment = VerticalAlignment.Center;
        Assert.Equal(top - 20, TextMeasurer.Measure("Hi\nthere", o).Lines[0].Baseline, 3);
        o.VerticalAlignment = VerticalAlignment.Bottom;
        Assert.Equal(top - 40, TextMeasurer.Measure("Hi\nthere", o).Lines[0].Baseline, 3);

        float w = TextMeasurer.MeasureAdvance("there", o).Width;
        o.HorizontalAlignment = HorizontalAlignment.Center;
        Assert.Equal(100 - (w / 2), TextMeasurer.Measure("there", o).Lines[0].Left, 3);
        o.HorizontalAlignment = HorizontalAlignment.Right;
        Assert.Equal(100 - w, TextMeasurer.Measure("there", o).Lines[0].Left, 3);
    }

    [Fact]
    public void OptionsValidateTheirValues()
    {
        var o = new TextOptions(TestFonts.Noto(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => o.LineSpacing = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.Dpi = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.TabWidth = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.WrappingLength = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.Origin = new Vector2(float.PositiveInfinity, 0));
        Assert.Throws<ArgumentNullException>(() => o.Font = null!);
        Assert.Throws<ArgumentOutOfRangeException>(() => TestFonts.Noto(0));
        Assert.Throws<ImageLimitException>(() => TextMeasurer.Measure(new string('a', TextLayout_MaxLength() + 1), o));
        TextOptions c = o.Clone();
        c.LineSpacing = 3;
        Assert.Equal(1f, o.LineSpacing);
    }

    private static int TextLayout_MaxLength() => 1 << 22;

    [Fact]
    public void ParallelMeasuringAndDrawingIsConsistent()
    {
        var o = new TextOptions(TestFonts.Noto(22)) { WrappingLength = 200 };
        const string Text = "Parallel readers share one face: the outline cache and the kerning lookups must be safe.";
        System.Drawing.RectangleF expected = TextMeasurer.MeasureBounds(Text, o);
        Parallel.For(0, 64, _ =>
        {
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(expected, TextMeasurer.MeasureBounds(Text, o.Clone()));
                Assert.False(TextBuilder.GeneratePath(Text, o).IsEmpty);
            }
        });
    }
}
