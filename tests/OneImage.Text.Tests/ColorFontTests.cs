using System.Numerics;
using OneImage.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Sharp = SixLabors.ImageSharp;
using Brush = OneImage.Drawing.Brush;
using Pen = OneImage.Drawing.Pen;

namespace OneImage.Text.Tests;

/// <summary>COLR version 0 color glyphs, against SixLabors.ImageSharp.Drawing 2.1.7 with MicrosoftColrFormat enabled.</summary>
public class ColorFontTests
{
    private const string V0 = "OneImageColorTest-V0.ttf";
    private const string V1 = "OneImageColorTest-V1.ttf";
    private const string Sample = "A\u2600\u2601\u2602\u2603\u2604\u2610";
    private static readonly Rgba32 White = new(255, 255, 255, 255);
    private static readonly Rgba32 Red = new(255, 0, 0, 255);

    private static Font Load(string file, float size)
    {
        var c = new FontCollection();
        return c.Add(TestFonts.PathOf(file)).CreateFont(size);
    }

    private static Image Blank(int w, int h)
    {
        var px = new Rgba32[w * h];
        Array.Fill(px, White);
        return Image.FromPixels<Rgba32>(px, w, h);
    }

    private static TextOptions Options(string file, float size, ColorFontSupport support, int palette = 0) =>
        new(Load(file, size)) { Origin = new Vector2(10, 10), ColorFontSupport = support, ColorPaletteIndex = palette };

    private static byte[] RenderReference(string file, float size, string text, bool withPen, bool pensOnly = false)
    {
        var font = OneImage.Text.Tests.Reference.Font(file, size);
        var o = new RichTextOptions(font) { Origin = new Vector2(10, 10), ColorFontSupport = SixLabors.Fonts.ColorFontSupport.MicrosoftColrFormat };
        using var img = new Sharp.Image<Sharp.PixelFormats.Rgba32>(900, 140, Sharp.Color.White);
        if (pensOnly)
        {
            img.Mutate(c => c.DrawText(o, text, Pens.Solid(Sharp.Color.Black, 3)));
        }
        else if (withPen)
        {
            img.Mutate(c => c.DrawText(o, text, Brushes.Solid(Sharp.Color.Red), Pens.Solid(Sharp.Color.Black, 3)));
        }
        else
        {
            img.Mutate(c => c.DrawText(o, text, Sharp.Color.Red));
        }

        var res = new byte[900 * 140 * 3];
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                Span<Sharp.PixelFormats.Rgba32> row = rows.GetRowSpan(y);
                for (int x = 0; x < 900; x++)
                {
                    int i = ((y * 900) + x) * 3;
                    res[i] = row[x].R;
                    res[i + 1] = row[x].G;
                    res[i + 2] = row[x].B;
                }
            }
        });
        return res;
    }

    private static byte[] Rgb(Image img) => img.ToArray<Rgba32>().SelectMany(p => new[] { p.R, p.G, p.B }).ToArray();

    private static (double Mean, int Max) Compare(byte[] a, byte[] b)
    {
        long sum = 0;
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            sum += d;
            max = Math.Max(max, d);
        }

        return ((double)sum / a.Length, max);
    }

    [Theory]
    [InlineData("brush", false, false)]
    [InlineData("brush+pen", true, false)]
    [InlineData("pen only", false, true)]
    public void RenderingAgreesWithTheReference(string label, bool withPen, bool penOnly)
    {
        TextOptions o = Options(V0, 100, ColorFontSupport.MicrosoftColrFormat);
        Image img = Blank(900, 140);
        Brush brush = Brush.Solid(Red);
        Pen pen = Pen.Solid(new Rgba32(0, 0, 0, 255), 3);
        img = penOnly ? img.DrawText(Sample, o, null, pen) : withPen ? img.DrawText(Sample, o, brush, pen) : img.DrawText(Sample, o, brush);
        byte[] theirs = RenderReference(V0, 100, Sample, withPen, penOnly);
        if (Environment.GetEnvironmentVariable("DUMPPNG") is { } dir)
        {
            Rgba32[] mine = img.ToArray<Rgba32>();
            var stack = new Rgba32[900 * 280];
            Array.Copy(mine, stack, mine.Length);
            for (int i = 0; i < 900 * 140; i++)
            {
                stack[(900 * 140) + i] = new Rgba32(theirs[3 * i], theirs[(3 * i) + 1], theirs[(3 * i) + 2], 255);
            }

            Image.FromPixels<Rgba32>(stack, 900, 280).Save(System.IO.Path.Combine(dir, "color-" + label.Replace(' ', '_').Replace('+', '_') + ".png"));
        }

        (double mean, int max) = Compare(Rgb(img), theirs);
        TestReport.Line($"COLORFONT {label}: mean {mean:F4} levels, max {max}");
        if (!withPen && !penOnly)
        {
            Assert.True(mean < 0.2, $"mean {mean}");
            Assert.True(max < 140, $"max {max}");
            return;
        }

        // The reference puts strokes half a pixel right and down of where we do (SVG placement), which dominates the difference.
        Assert.True(mean < 3.5, $"mean {mean}");

        // Outlines go on top of all layers: the ring does not hide the foreground bar's outline, in either.
        static int Black(Func<int, int, (int R, int G, int B)> px) =>
            Enumerable.Range(195, 30).SelectMany(x => Enumerable.Range(30, 50).Select(y => (x, y))).Count(t => px(t.x, t.y) is { R: < 60, G: < 60, B: < 60 });
        Rgba32[] mine2 = img.ToArray<Rgba32>();
        int ours = Black((x, y) => (mine2[(y * 900) + x].R, mine2[(y * 900) + x].G, mine2[(y * 900) + x].B));
        int refs = Black((x, y) => (theirs[((y * 900) + x) * 3], theirs[(((y * 900) + x) * 3) + 1], theirs[(((y * 900) + x) * 3) + 2]));
        Assert.True(Math.Abs(ours - refs) <= Math.Max(8, refs / 5), $"black pixels under the ring: ours {ours}, reference {refs}");
    }

    [Fact]
    public void ColorOffIsTheMonochromePath()
    {
        TextOptions off = Options(V0, 100, ColorFontSupport.None);
        Rgba32[] a = Blank(900, 140).DrawText(Sample, off, Red).ToArray<Rgba32>();
        Rgba32[] b = Blank(900, 140).Fill(Red, TextBuilder.GeneratePath(Sample, off)).ToArray<Rgba32>();
        Assert.Equal(b, a);
        Assert.Equal(ColorFontSupport.None, new TextOptions(Load(V0, 20)).ColorFontSupport);
        Assert.All(a, p => Assert.Equal(p.G, p.B));   // red on white only: no layer colors
    }

    [Fact]
    public void ColorOnForAFontWithoutColorGlyphsChangesNothing()
    {
        var o = new TextOptions(TestFonts.Noto(30)) { Origin = new Vector2(5, 5), ColorFontSupport = ColorFontSupport.MicrosoftColrFormat };
        var plain = new TextOptions(TestFonts.Noto(30)) { Origin = new Vector2(5, 5) };
        Assert.Equal(Blank(300, 60).DrawText("Hello", plain, Red).ToArray<Rgba32>(), Blank(300, 60).DrawText("Hello", o, Red).ToArray<Rgba32>());
    }

    [Fact]
    public void TextWithoutColorGlyphsInAColorFontIsThePlainPath()
    {
        TextOptions on = Options(V0, 60, ColorFontSupport.MicrosoftColrFormat);
        TextOptions off = Options(V0, 60, ColorFontSupport.None);
        Assert.Equal(Blank(300, 100).DrawText("AA A", off, Red).ToArray<Rgba32>(), Blank(300, 100).DrawText("AA A", on, Red).ToArray<Rgba32>());
    }

    [Fact]
    public void PaletteChoiceChangesTheLayerColors()
    {
        Rgba32[] p0 = Blank(900, 140).DrawText("\u2600", Options(V0, 100, ColorFontSupport.MicrosoftColrFormat, 0), Red).ToArray<Rgba32>();
        Rgba32[] p1 = Blank(900, 140).DrawText("\u2600", Options(V0, 100, ColorFontSupport.MicrosoftColrFormat, 1), Red).ToArray<Rgba32>();
        Rgba32[] far = Blank(900, 140).DrawText("\u2600", Options(V0, 100, ColorFontSupport.MicrosoftColrFormat, 7), Red).ToArray<Rgba32>();
        static bool Has(Rgba32[] px, int r, int g, int b) => px.Any(p => Math.Abs(p.R - r) < 3 && Math.Abs(p.G - g) < 3 && Math.Abs(p.B - b) < 3);
        Assert.True(Has(p0, 230, 26, 26) && Has(p0, 26, 153, 51) && Has(p0, 26, 51, 230));
        Assert.True(Has(p1, 26, 230, 230) && Has(p1, 230, 26, 204) && Has(p1, 77, 77, 77));
        Assert.Equal(p0, far);      // past the last palette: palette 0
        Assert.Equal(2, Load(V0, 20).ColorPaletteCount);
        Assert.Equal(0, TestFonts.Noto(20).ColorPaletteCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(V0, 20, ColorFontSupport.None).ColorPaletteIndex = -1);
    }

    [Fact]
    public void ForegroundLayersUseTheBrushAndTheOthersDoNot()
    {
        TextOptions o = Options(V0, 100, ColorFontSupport.MicrosoftColrFormat);
        Rgba32[] a = Blank(300, 140).DrawText("\u2601", o, new Rgba32(0, 0, 255, 255)).ToArray<Rgba32>();
        Rgba32[] b = Blank(300, 140).DrawText("\u2601", o, new Rgba32(0, 255, 0, 255)).ToArray<Rgba32>();
        Assert.Contains(a, p => p is { R: 0, G: 0, B: 255 });
        Assert.DoesNotContain(a, p => p is { R: 0, G: 255, B: 0 });
        Assert.Contains(b, p => p is { R: 0, G: 255, B: 0 });
        // The ring layer is palette entry 3 (255, 204, 0) whatever the brush is.
        Assert.Contains(a, p => p is { R: 255, G: 204, B: 0 });
        Assert.Contains(b, p => p is { R: 255, G: 204, B: 0 });
    }

    [Fact]
    public void CanvasMatchesImage()
    {
        TextOptions o = Options(V0, 80, ColorFontSupport.MicrosoftColrFormat);
        using var canvas = new Canvas(900, 140, PixelFormat.Rgba8);
        canvas.Clear(White);
        canvas.DrawText(Sample, o, Red);
        Assert.Equal(Blank(900, 140).DrawText(Sample, o, Red).ToArray<Rgba32>(), canvas.Snapshot().ToArray<Rgba32>());
    }

    [Fact]
    public void OpacityAppliesToColorLayers()
    {
        TextOptions o = Options(V0, 100, ColorFontSupport.MicrosoftColrFormat);
        Rgba32[] half = Blank(300, 140).DrawText("\u2600", o, Brush.Solid(Red), opacity: 0.5f).ToArray<Rgba32>();
        Assert.DoesNotContain(half, p => p is { R: 230, G: 26, B: 26 });
        Assert.Contains(half, p => p.R > 200 && p.G > 100 && p.G < 160);
    }

    [Fact]
    public void Version1FontsPaintTheirVersion0LayersAndFallBackToTheOutline()
    {
        TextOptions o = Options(V1, 100, ColorFontSupport.MicrosoftColrFormat);
        Rgba32[] px = Blank(900, 140).DrawText(Sample, o, Red).ToArray<Rgba32>();
        Rgba32[] v0 = Blank(900, 140).DrawText(Sample, Options(V0, 100, ColorFontSupport.MicrosoftColrFormat), Red).ToArray<Rgba32>();
        Assert.Equal(v0, px);      // the V1-only glyph (U+E010) is the plain red outline in both
        Assert.False(Load(V1, 20).HasColorGlyph(0x2610));
        Assert.True(Load(V1, 20).HasColorGlyph(0x2600));
    }

    [Fact]
    public void TextAsPathStaysMonochrome()
    {
        TextOptions on = Options(V0, 100, ColorFontSupport.MicrosoftColrFormat);
        TextOptions off = Options(V0, 100, ColorFontSupport.None);
        Assert.Equal(TextBuilder.GeneratePath(Sample, off).ToSvg(), TextBuilder.GeneratePath(Sample, on).ToSvg());
    }

    [Fact]
    public void DamagedColorTablesDrawAsPlainGlyphs()
    {
        byte[] good = TestFonts.Bytes(V0);
        var rng = new Random(5);
        int colorChanged = 0;
        for (int i = 0; i < 300; i++)
        {
            byte[] bad = (byte[])good.Clone();
            // The COLR and CPAL tables are in the last bytes of the file.
            for (int k = 0; k < 1 + (i % 3); k++)
            {
                bad[bad.Length - 1 - rng.Next(260)] ^= (byte)(1 << rng.Next(8));
            }

            try
            {
                var c = new FontCollection();
                var o = new TextOptions(c.Add(bad).CreateFont(60)) { ColorFontSupport = ColorFontSupport.MicrosoftColrFormat };
                _ = Blank(400, 100).DrawText(Sample, o, Red).ToArray<Rgba32>();
                colorChanged++;
            }
            catch (FontFormatException)
            {
            }
        }

        Assert.True(colorChanged > 0);
    }
}
