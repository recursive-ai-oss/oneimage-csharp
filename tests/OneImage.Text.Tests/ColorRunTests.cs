using System.Numerics;
using OneImage.Drawing;

namespace OneImage.Text.Tests;

/// <summary>Color glyphs reached through rich-text runs and fallback faces.</summary>
public class ColorRunTests
{
    private const string Cloud = "\u2601";
    private static readonly Rgba32 Blue = new(0, 0, 255, 255);
    private static readonly Rgba32 Green = new(0, 255, 0, 255);
    private static readonly Rgba32 Red = new(255, 0, 0, 255);

    private static FontFamily ColorFamily() => new FontCollection().Add(TestFonts.PathOf("OneImageColorTest-V0.ttf"));

    private static Image Blank(int w, int h)
    {
        var px = new Rgba32[w * h];
        Array.Fill(px, new Rgba32(255, 255, 255, 255));
        return Image.FromPixels<Rgba32>(px, w, h);
    }

    [Fact]
    public void AColorFontInARunPaintsItsLayersWithTheRunsBrushForTheForegroundEntry()
    {
        FontFamily color = ColorFamily();
        var o = new TextOptions(TestFonts.Noto(30)) { Origin = new Vector2(5, 5) };
        o.TextRuns.Add(new TextRun(1, 2) { Font = color.CreateFont(100), Brush = Brush.Solid(Blue) });
        Rgba32[] px = Blank(300, 160).DrawText("A" + Cloud, o, Brush.Solid(Green)).ToArray<Rgba32>();
        Assert.Contains(px, p => p is { R: 0, G: 0, B: 255 });                 // the run's brush feeds the foreground layer
        Assert.DoesNotContain(px, p => p is { R: 255, G: 0, B: 0 });
        Assert.Contains(px, p => p is { R: 255, G: 204, B: 0 });               // a layer with its own color keeps it
        Assert.Contains(px, p => p is { R: 0, G: 255, B: 0 });                 // "A" uses the brush passed to the call
    }

    [Fact]
    public void ARunsPenOutlinesLastAndColoredLayersAreNotOutlined()
    {
        FontFamily color = ColorFamily();
        var o = new TextOptions(color.CreateFont(100)) { Origin = new Vector2(5, 5) };
        var black = new Rgba32(0, 0, 0, 255);
        o.TextRuns.Add(new TextRun(0, 1) { Brush = Brush.Solid(Blue), Pen = Pen.Solid(black, 4) });
        Rgba32[] px = Blank(200, 160).DrawText(Cloud, o, null).ToArray<Rgba32>();
        Rgba32[] noPen = Blank(200, 160).DrawText(Cloud, new TextOptions(o.Font) { Origin = o.Origin }, Brush.Solid(Blue)).ToArray<Rgba32>();
        Assert.Contains(px, p => p is { R: 0, G: 0, B: 0 });
        Assert.DoesNotContain(noPen, p => p is { R: 0, G: 0, B: 0 });
        Assert.Contains(px, p => p is { R: 255, G: 204, B: 0 });
    }

    [Fact]
    public void ColorGlyphsComeFromAFallbackFace()
    {
        var o = new TextOptions(TestFonts.Noto(100)) { Origin = new Vector2(5, 5) };
        o.FallbackFontFamilies.Add(ColorFamily());
        Rgba32[] px = Blank(300, 160).DrawText("A" + Cloud, o, Brush.Solid(Green)).ToArray<Rgba32>();
        Assert.Contains(px, p => p is { R: 255, G: 204, B: 0 });
        Assert.Contains(px, p => p is { R: 0, G: 255, B: 0 });

        // The same text with color off is the monochrome outline in the brush color only.
        o.ColorFontSupport = ColorFontSupport.None;
        Rgba32[] off = Blank(300, 160).DrawText("A" + Cloud, o, Brush.Solid(Green)).ToArray<Rgba32>();
        Assert.DoesNotContain(off, p => p is { R: 255, G: 204, B: 0 });
    }

    [Fact]
    public void FallbackFaceInsideARunUsesTheRunsPaintAndPalette()
    {
        var o = new TextOptions(TestFonts.Noto(30)) { Origin = new Vector2(5, 5), ColorPaletteIndex = 1 };
        o.FallbackFontFamilies.Add(ColorFamily());
        o.TextRuns.Add(new TextRun(0, 2) { Font = TestFonts.Noto(100), Brush = Brush.Solid(Red) });
        Rgba32[] px = Blank(300, 160).DrawText("\u2600" + Cloud, o, Brush.Solid(Green)).ToArray<Rgba32>();
        Assert.Contains(px, p => Math.Abs(p.R - 26) < 3 && Math.Abs(p.G - 230) < 3 && Math.Abs(p.B - 230) < 3);   // palette 1 colors
        Assert.Contains(px, p => p is { R: 255, G: 0, B: 0 });
        Assert.DoesNotContain(px, p => p is { R: 0, G: 255, B: 0 });
    }

    [Fact]
    public void CanvasAndImageAgreeOnColorRuns()
    {
        FontFamily color = ColorFamily();
        var o = new TextOptions(TestFonts.Noto(30)) { Origin = new Vector2(5, 5) };
        o.TextRuns.Add(new TextRun(1, 2) { Font = color.CreateFont(80), Brush = Brush.Solid(Blue), Pen = Pen.Solid(Green, 2) });
        Rgba32[] viaImage = Blank(250, 140).DrawText("A" + Cloud, o, Brush.Solid(Red)).ToArray<Rgba32>();
        using Canvas canvas = Blank(250, 140).ToCanvas();
        canvas.DrawText("A" + Cloud, o, Brush.Solid(Red));
        Assert.Equal(viaImage, canvas.Snapshot().ToArray<Rgba32>());
    }

    [Fact]
    public void TextAsPathStaysMonochromeWithRuns()
    {
        FontFamily color = ColorFamily();
        var o = new TextOptions(color.CreateFont(60)) { Origin = new Vector2(5, 5) };
        o.TextRuns.Add(new TextRun(0, 1) { Brush = Brush.Solid(Blue) });
        OneImage.Drawing.Path path = TextBuilder.GeneratePath(Cloud, o);
        Assert.False(path.IsEmpty);
        Rgba32[] px = Blank(200, 100).Fill(Red, path).ToArray<Rgba32>();
        Assert.DoesNotContain(px, p => p is { R: 255, G: 204, B: 0 });
    }
}
