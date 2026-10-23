using System.Text;
using OneImage.Text.Fonts;
using SkiaSharp;

namespace OneImage.Text.Tests;

/// <summary>COLR version 1 paint graphs, against Skia's renders of the same font (black box) and by structure.</summary>
public class ColrV1Tests
{
    private const string File = "ColrV1-TestGlyphs.ttf";
    private const int Cell = 100;

    private static (float[] Mine, float[] Skia)? Render(int codePoint, float size, int palette = 0)
    {
        var fonts = new FontCollection();
        FontFamily fam = fonts.Add(TestFonts.PathOf(File));
        Font font = fam.CreateFont(size);
        string text = char.ConvertFromUtf32(codePoint);
        var o = new TextOptions(font) { Origin = new(10, 0), ColorPaletteIndex = palette };
        float baseline = TextMeasurer.Measure(text, o).Lines[0].Baseline;
        o.Origin = new(10, 80 - baseline);
        using var canvas = new Canvas(Cell, Cell, PixelFormat.Rgba8);
        canvas.Fill(new Rgba32(255, 255, 255, 255));
        canvas.DrawText(text, o, new Rgba32(0, 0, 0, 255));
        var px = new Rgba32[Cell * Cell];
        canvas.CopyTo<Rgba32>(px, new ImageRect(0, 0, Cell, Cell));
        var mine = new float[Cell * Cell * 3];
        for (int i = 0; i < px.Length; i++)
        {
            mine[(3 * i)] = px[i].R;
            mine[(3 * i) + 1] = px[i].G;
            mine[(3 * i) + 2] = px[i].B;
        }

        using var tf = SKTypeface.FromFile(TestFonts.PathOf(File));
        using var sk = new SKFont(tf, size) { Edging = SKFontEdging.Antialias, Subpixel = false, Hinting = SKFontHinting.None };
        using var surface = SKSurface.Create(new SKImageInfo(Cell, Cell, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        surface.Canvas.DrawText(text, 10, 80, SKTextAlign.Left, sk, paint);
        using var pix = surface.PeekPixels();
        ReadOnlySpan<byte> bytes = pix.GetPixelSpan();
        var skia = new float[Cell * Cell * 3];
        for (int i = 0; i < Cell * Cell; i++)
        {
            skia[(3 * i)] = bytes[4 * i];
            skia[(3 * i) + 1] = bytes[(4 * i) + 1];
            skia[(3 * i) + 2] = bytes[(4 * i) + 2];
        }

        return (mine, skia);
    }

    private static double Mean(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++)
        {
            s += Math.Abs(a[i] - b[i]);
        }

        return s / a.Length;
    }

    // Glyphs whose graphs contain a cycle: the font says nothing is drawn, we draw the plain outline instead.
    private static bool IsCycle(int cp) => cp is 0xF1100 or 0xF1101;

    [Fact]
    public void EveryGlyphOfTheTestFontMatchesSkia()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var face = TestFonts.Face(File);
        var sb = new StringBuilder();
        int n = 0;
        double total = 0;
        double worst = 0;
        int above = 0;
        for (int cp = 0xF0000; cp < 0xF2000; cp++)
        {
            if (face.GetGlyphIndex(cp) == 0 || IsCycle(cp))
            {
                continue;
            }

            var (mine, skia) = Render(cp, 64)!.Value;
            double d = Mean(mine, skia);
            n++;
            total += d;
            worst = Math.Max(worst, d);
            if (d > 1.0)
            {
                above++;
                sb.AppendLine($"U+{cp:X}: mean {d:F2}");
            }
        }

        TestReport.Line($"COLRV1-SKIA {n} glyphs at 64 px: mean level difference {total / n:F3}, worst {worst:F2}, {above} above 1.0");
        Assert.True(n >= 200, "too few glyphs");
        Assert.True(total / n < 0.6, $"mean {total / n:F3}");
        Assert.True(worst < 3.0, sb.ToString());
    }

    [Fact]
    public void ForegroundColorStopsTakeTheTextBrush()
    {
        // U+F0B01 is a gradient between two palette colors with the foreground color in the middle at 30 % alpha.
        var fonts = new FontCollection();
        Font font = fonts.Add(TestFonts.PathOf(File)).CreateFont(64);
        var o = new TextOptions(font) { Origin = new(10, 0) };
        o.Origin = new(10, 80 - TextMeasurer.Measure("\U000F0B01", o).Lines[0].Baseline);
        Rgba32 Middle(Rgba32 text)
        {
            using var c = new Canvas(100, 100, PixelFormat.Rgba8);
            c.Fill(new Rgba32(255, 255, 255, 255));
            c.DrawText("\U000F0B01", o, text);
            return c[50, 50];
        }

        Rgba32 red = Middle(new Rgba32(255, 0, 0, 255));
        Rgba32 blue = Middle(new Rgba32(0, 0, 255, 255));
        Assert.True(red.R > blue.R + 20, "the foreground stop is the text color");
        Assert.True(blue.B > red.B + 20);
    }

    [Fact]
    public void ColorFontSupportNoneDrawsThePlainOutline()
    {
        var fonts = new FontCollection();
        Font font = fonts.Add(TestFonts.PathOf(File)).CreateFont(64);
        var o = new TextOptions(font) { Origin = new(10, 10) };
        var off = o.Clone();
        off.ColorFontSupport = ColorFontSupport.None;
        using var a = new Canvas(100, 100, PixelFormat.Rgba8);
        using var b = new Canvas(100, 100, PixelFormat.Rgba8);
        a.DrawText("\U000F0100", off, new Rgba32(0, 0, 0, 255));
        b.DrawText("\U000F0100", o, new Rgba32(0, 0, 0, 255));
        Assert.NotEqual(a[40, 50], b[40, 50]);
        Assert.Equal(255, a[40, 50].A);
        Assert.Equal(new Rgba32(0, 0, 0, 255), a[40, 50]);
    }

    [Fact]
    public void CyclesDrawThePlainOutlineAndNeverHang()
    {
        var fonts = new FontCollection();
        Font font = fonts.Add(TestFonts.PathOf(File)).CreateFont(64);
        var o = new TextOptions(font) { Origin = new(10, 10) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var c = new Canvas(100, 100, PixelFormat.Rgba8);
        c.DrawText("\U000F1100\U000F1101", o, new Rgba32(0, 0, 0, 255));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        var face = TestFonts.Face(File);
        Assert.Null(face.Color!.GetProgram(face.GetGlyphIndex(0xF1100)));
        Assert.True(face.Color.HasPaintGraph(face.GetGlyphIndex(0xF1100)));
    }

    // The test font with its COLR table replaced: a hostile paint graph for the glyph of U+F0100.
    private static FontFace WithColr(Func<int, byte[]> build)
    {
        byte[] font = TestFonts.Bytes(File);
        int glyph = TestFonts.Face(File).GetGlyphIndex(0xF0100);
        byte[] colr = build(glyph);
        int n = (font[4] << 8) | font[5];
        var dir = new List<(string Tag, byte[] Data)>();
        for (int i = 0; i < n; i++)
        {
            int e = 12 + (16 * i);
            string tag = Encoding.ASCII.GetString(font, e, 4);
            int off = (font[e + 8] << 24) | (font[e + 9] << 16) | (font[e + 10] << 8) | font[e + 11];
            int len = (font[e + 12] << 24) | (font[e + 13] << 16) | (font[e + 14] << 8) | font[e + 15];
            dir.Add((tag, tag == "COLR" ? colr : font.AsSpan(off, len).ToArray()));
        }

        using var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U32(long v) { U16((int)(v >> 16) & 0xFFFF); U16((int)v & 0xFFFF); }
        ms.Write(font, 0, 4);
        U16(dir.Count);
        ms.Write(font, 6, 6);
        long at = 12 + (16L * dir.Count);
        foreach (var (tag, data) in dir)
        {
            ms.Write(Encoding.ASCII.GetBytes(tag));
            U32(0);
            U32(at);
            U32(data.Length);
            at += (data.Length + 3) & ~3;
        }

        foreach (var (_, data) in dir)
        {
            ms.Write(data);
            for (int p = data.Length; (p & 3) != 0; p++)
            {
                ms.WriteByte(0);
            }
        }

        return FontFile.Parse(ms.ToArray())[0];
    }

    private static byte[] Header(int baseList, int layerList)
    {
        var b = new byte[34];
        b[1] = 1;
        b[21] = (byte)layerList;
        b[17] = (byte)baseList;
        return b;
    }

    [Fact]
    public void ExponentialSharingIsRefusedByTheNodeBudget()
    {
        // Glyph -> 255 layers, each a reference back to the glyph: 255^depth paints unless something stops it.
        FontFace face = WithColr(g =>
        {
            var t = new byte[2048];
            Header(34, 50).CopyTo(t, 0);
            t[37] = 1;                      // one base glyph record
            t[38] = (byte)(g >> 8);
            t[39] = (byte)g;
            t[43] = 10;                     // its paint at 34 + 10
            t[44] = 1;                      // PaintColrLayers
            t[45] = 255;
            t[53] = 255;                    // layer list: 255 paints
            for (int i = 0; i < 255; i++)
            {
                int o = 54 + (4 * i);
                t[o + 2] = (byte)(1024 >> 8);
                t[o + 3] = 0;
            }

            t[1074] = 11;                   // PaintColrGlyph back to the glyph
            t[1075] = (byte)(g >> 8);
            t[1076] = (byte)g;
            return t;
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(face.Color!.GetProgram(face.GetGlyphIndex(0xF0100)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void DeepNestingIsRefusedByTheDepthLimit()
    {
        FontFace face = WithColr(g =>
        {
            var t = new byte[34 + 10 + (8 * 300) + 8];
            Header(34, 0).CopyTo(t, 0);
            t[37] = 1;
            t[38] = (byte)(g >> 8);
            t[39] = (byte)g;
            t[43] = 10;
            for (int i = 0; i < 300; i++)
            {
                int p = 44 + (8 * i);
                t[p] = 14;                  // PaintTranslate
                t[p + 3] = 8;               // child right after it
            }

            int last = 44 + (8 * 300);
            t[last] = 2;                    // PaintSolid
            t[last + 3] = 0x40;             // alpha 1.0
            return t;
        });
        Assert.Null(face.Color!.GetProgram(face.GetGlyphIndex(0xF0100)));

        // Within the limit the same shape is accepted.
        FontFace ok = WithColr(g =>
        {
            var t = new byte[34 + 10 + (8 * 20) + 8];
            Header(34, 0).CopyTo(t, 0);
            t[37] = 1;
            t[38] = (byte)(g >> 8);
            t[39] = (byte)g;
            t[43] = 10;
            for (int i = 0; i < 20; i++)
            {
                int p = 44 + (8 * i);
                t[p] = 14;
                t[p + 3] = 8;
            }

            int last = 44 + (8 * 20);
            t[last] = 2;
            t[last + 3] = 0x40;
            return t;
        });
        Assert.NotNull(ok.Color!.GetProgram(ok.GetGlyphIndex(0xF0100)));
    }

    [Fact]
    public void TruncatedTablesAreIgnored()
    {
        // A table that ends in the middle of a paint: the glyph has no usable graph and draws as its outline.
        FontFace face = WithColr(g =>
        {
            var t = new byte[46];
            Header(34, 0).CopyTo(t, 0);
            t[37] = 1;
            t[38] = (byte)(g >> 8);
            t[39] = (byte)g;
            t[43] = 10;
            t[44] = 4;                      // PaintLinearGradient cut short
            return t;
        });
        Assert.Null(face.Color!.GetProgram(face.GetGlyphIndex(0xF0100)));
    }
}
