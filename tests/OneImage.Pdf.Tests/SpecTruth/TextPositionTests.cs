using System.Text;
using static OneImage.Pdf.Tests.SpecTruth.Spec;

namespace OneImage.Pdf.Tests.SpecTruth;

/// <summary>
/// Text positioning with Type 3 fonts whose glyphs are plain rectangles, so every glyph's place follows from the text state
/// alone and no font rasterization is involved (ISO 32000-2 9.2 to 9.4, 9.6.5).
/// </summary>
public class TextPositionTests
{
    private static readonly (double R, double G, double B) Black = Gray(0);

    /// <summary>
    /// Glyph 'a' fills the whole em square, 'b' only its lower-left quarter, 'c' is a red square drawn with d0 (colored), 'd' a square
    /// that tries to set its own color under d1 (which must be ignored), and code 32 is an empty glyph of the same width.
    /// All advance 1 em (width 10 glyph units at FontMatrix 0.1).
    /// </summary>
    private static string[] Font(string fontMatrix = "[0.1 0 0 0.1 0 0]", int width = 10)
    {
        var widths = new StringBuilder();
        for (int code = 32; code <= 100; code++)
        {
            widths.Append(code is 32 or 97 or 98 or 99 or 100 ? width : 0).Append(' ');
        }

        return
        [
            $"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 10 10] /FontMatrix {fontMatrix} /CharProcs << /a 6 0 R /b 7 0 R /sp 8 0 R /c 9 0 R /d 10 0 R >> " +
            $"/Encoding << /Type /Encoding /Differences [32 /sp 97 /a /b /c /d] >> /FirstChar 32 /LastChar 100 /Widths [{widths}] /Resources << >> >>",
            Stream("", $"{width} 0 0 0 10 10 d1 0 0 10 10 re f"),
            Stream("", $"{width} 0 0 0 10 10 d1 0 0 5 5 re f"),
            Stream("", $"{width} 0 0 0 0 0 d1"),
            Stream("", $"{width} 0 d0 1 0 0 rg 0 0 10 10 re f"),
            Stream("", $"{width} 0 0 0 10 10 d1 1 0 0 rg 0 0 10 10 re f"),
        ];
    }

    private static byte[] Text(string content, string fontMatrix = "[0.1 0 0 0.1 0 0]", string resources = "") =>
        Page(content, "/Font << /F1 5 0 R >> " + resources, w: 40, h: 20, extra: Font(fontMatrix));

    private static Expected E() => new(40, 20);

    // 9.4.4 / 9.6.5: a glyph is drawn in glyph space mapped by the font matrix, then Tfs, then Tm; the next glyph starts one advance later.
    [Fact]
    [Trait("Section", "9.4.4")]
    public void TjAdvancesByTheGlyphWidth()
    {
        // 'a' fills 2..6; 'b' is the lower-left quarter of its cell at x=6.
        Check(Text("BT /F1 4 Tf 2 2 Td (ab) Tj ET"), E().Rect(2, 2, 6, 6, Black).Rect(6, 2, 8, 4, Black), Tol.Exact);
    }

    // 9.4.2: Td moves to the start of the next line relative to the start of the current line, not the current point.
    [Fact]
    [Trait("Section", "9.4.2")]
    public void TdIsRelativeToTheLineStart()
    {
        Check(Text("BT /F1 4 Tf 2 2 Td (aa) Tj 8 4 Td (b) Tj ET"), E().Rect(2, 2, 6, 6, Black).Rect(6, 2, 10, 6, Black).Rect(10, 6, 12, 8, Black), Tol.Exact);
    }

    // 9.4.2: Tm replaces the text matrix. A quarter turn counter-clockwise sends text-space x to device y, so glyphs advance upward.
    [Fact]
    [Trait("Section", "9.4.2")]
    public void RotatedTextMatrixAdvancesAlongTheBaseline()
    {
        // (x,y) -> (-y + 12, x + 2). 'a' covers text (0..4, 0..4) -> x 8..12, y 2..6. 'b' sits at text x=4: quarter (4..6, 0..2) -> x 10..12, y 6..8.
        Check(Text("BT /F1 4 Tf 0 1 -1 0 12 2 Tm (ab) Tj ET"), E().Rect(8, 2, 12, 6, Black).Rect(10, 6, 12, 8, Black), Tol.Exact);
    }

    // 9.4.2: Tm may scale; the font size multiplies with it.
    [Fact]
    [Trait("Section", "9.4.2")]
    public void TextMatrixScalesWithFontSize()
    {
        Check(Text("BT /F1 2 Tf 2 0 0 3 4 3 Tm (aa) Tj ET"), E().Rect(4, 3, 8, 9, Black).Rect(8, 3, 12, 9, Black), Tol.Exact);
    }

    // 9.3.2 character spacing Tc is added after every glyph, in unscaled text space units.
    [Fact]
    [Trait("Section", "9.3.2")]
    public void CharacterSpacing()
    {
        Check(Text("BT /F1 4 Tf 1.5 Tc 2 2 Td (aaa) Tj ET"), E().Rect(2, 2, 6, 6, Black).Rect(7.5, 2, 11.5, 6, Black).Rect(13, 2, 17, 6, Black), Tol.Exact);
    }

    // 9.3.3 word spacing Tw is added after code 32 only.
    [Fact]
    [Trait("Section", "9.3.3")]
    public void WordSpacingAppliesOnlyToTheSpaceCode()
    {
        Check(Text("BT /F1 4 Tf 3 Tw 2 2 Td (a a) Tj ET"), E().Rect(2, 2, 6, 6, Black).Rect(13, 2, 17, 6, Black), Tol.Exact);
    }

    // 9.3.4 horizontal scaling Tz: glyphs and advances shrink to half; Tc and Tw scale with it as well (9.4.4: tx = (w0 Tfs + Tc + Tw) Th).
    [Fact]
    [Trait("Section", "9.3.4")]
    public void HorizontalScaling()
    {
        Check(Text("BT /F1 4 Tf 50 Tz 2 2 Td (aa) Tj ET"), E().Rect(2, 2, 4, 6, Black).Rect(4, 2, 6, 6, Black), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "9.4.4")]
    public void HorizontalScalingAppliesToCharacterSpacing()
    {
        // advance = (4 + 2) * 0.5 = 3.
        Check(Text("BT /F1 4 Tf 50 Tz 2 Tc 2 2 Td (aa) Tj ET"), E().Rect(2, 2, 4, 6, Black).Rect(5, 2, 7, 6, Black), Tol.Exact);
    }

    // 9.3.7 text rise Ts moves glyphs up (in unscaled text space units), without moving the baseline for the following glyphs.
    [Fact]
    [Trait("Section", "9.3.7")]
    public void TextRise()
    {
        Check(Text("BT /F1 4 Tf 3 Ts 2 2 Td (a) Tj 0 Ts (a) Tj ET"), E().Rect(2, 5, 6, 9, Black).Rect(6, 2, 10, 6, Black), Tol.Exact);
    }

    // 9.4.3 TJ: a number moves the next glyph left by number/1000 * Tfs (right if negative).
    [Theory]
    [InlineData("-500", 8.0)]
    [InlineData("500", 4.0)]
    [InlineData("0", 6.0)]
    [Trait("Section", "9.4.3")]
    public void TjArrayAdjustment(string adjust, double secondX)
    {
        Check(Text($"BT /F1 4 Tf 2 2 Td [(a) {adjust} (b)] TJ ET"), E().Rect(2, 2, 6, 6, Black).Rect(secondX, 2, secondX + 2, 4, Black), Tol.Exact);
    }

    // With Tz the adjustment scales too.
    [Fact]
    [Trait("Section", "9.4.3")]
    public void TjArrayAdjustmentWithHorizontalScaling()
    {
        // a is 2 wide at 50%; -500 moves right by 0.5 * 4 * 0.5 = 1: b at 2 + 2 + 1 = 5; the quarter is 1 wide, 2 high.
        Check(Text("BT /F1 4 Tf 50 Tz 2 2 Td [(a) -500 (b)] TJ ET"), E().Rect(2, 2, 4, 6, Black).Rect(5, 2, 6, 4, Black), Tol.Exact);
    }

    // 9.3.5 / 9.4.2 leading, T*, ', " and TD (TD also sets the leading to -ty).
    [Fact]
    [Trait("Section", "9.4.2")]
    public void TStarUsesTheLeading()
    {
        Check(Text("BT /F1 4 Tf 5 TL 2 14 Td (a) Tj T* (b) Tj T* (a) Tj ET"), E().Rect(2, 14, 6, 18, Black).Rect(2, 9, 4, 11, Black).Rect(2, 4, 6, 8, Black), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "9.4.3")]
    public void QuoteOperatorsMoveToTheNextLine()
    {
        // ' moves down by the leading and shows; " also sets word and character spacing (aw ac string).
        Check(Text("BT /F1 4 Tf 6 TL 2 14 Td (a) Tj (b) ' 0 1 (aa) \" ET"),
            E().Rect(2, 14, 6, 18, Black).Rect(2, 8, 4, 10, Black).Rect(2, 2, 6, 6, Black).Rect(7, 2, 11, 6, Black), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "9.4.2")]
    public void TdCapitalSetsTheLeading()
    {
        Check(Text("BT /F1 4 Tf 2 14 Td 0 -6 TD (a) Tj T* (a) Tj ET"), E().Rect(2, 8, 6, 12, Black).Rect(2, 2, 6, 6, Black), Tol.Exact);
    }

    // 9.4.1: Tm and the line matrix start from identity at each BT, but Tf, Tc and the other text state parameters belong to the graphics state and persist.
    [Fact]
    [Trait("Section", "9.4.1")]
    public void TextStatePersistsButTheMatrixResetsAtBt()
    {
        Check(Text("BT /F1 4 Tf 1 Tc 20 10 Td ET BT 2 2 Td (aa) Tj ET"), E().Rect(2, 2, 6, 6, Black).Rect(7, 2, 11, 6, Black), Tol.Exact);
    }

    // The CTM applies to text like any other drawing.
    [Fact]
    [Trait("Section", "9.4.4")]
    public void CtmAppliesToText()
    {
        Check(Text("2 0 0 2 0 0 cm BT /F1 2 Tf 1 1 Td (ab) Tj ET"), E().Rect(2, 2, 6, 6, Black).Rect(6, 2, 8, 4, Black), Tol.Exact);
    }

    // 9.6.5: the font matrix need not be uniform; the advance is the glyph width mapped by the matrix's horizontal scale.
    [Fact]
    [Trait("Section", "9.6.5")]
    public void NonUniformFontMatrix()
    {
        // FontMatrix [0.05 0 0 0.1]: glyph 'a' is 0.5 em wide, 1 em high; advance 0.5 em.
        Check(Text("BT /F1 4 Tf 2 2 Td (aa) Tj ET", "[0.05 0 0 0.1 0 0]"), E().Rect(2, 2, 4, 6, Black).Rect(4, 2, 6, 6, Black), Tol.Exact);
    }

    // 9.6.5: a glyph with d1 is a mask in the current fill color; color operators in it have no effect.
    [Fact]
    [Trait("Section", "9.6.5")]
    public void D1GlyphUsesTheCurrentFillColor()
    {
        Check(Text("0 0 1 rg BT /F1 4 Tf 2 2 Td (d) Tj ET"), E().Rect(2, 2, 6, 6, Rgb(0, 0, 1)), Tol.Exact);
    }

    // 9.6.5: a glyph with d0 may set its own color.
    [Fact]
    [Trait("Section", "9.6.5")]
    public void D0GlyphKeepsItsOwnColor()
    {
        Check(Text("0 0 1 rg BT /F1 4 Tf 2 2 Td (c) Tj ET"), E().Rect(2, 2, 6, 6, Rgb(1, 0, 0)), Tol.Exact);
    }

    // 9.3.6: for Type 3 fonts only render mode 3 (invisible) has an effect; the others, including stroke and clip, paint the glyph as a fill.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(7, true)]
    [Trait("Section", "9.3.6")]
    public void RenderModeOnType3(int mode, bool visible)
    {
        var e = E();
        if (visible)
        {
            e.Rect(2, 2, 6, 6, Black);
        }

        Check(Text($"BT /F1 4 Tf {mode} Tr 2 2 Td (a) Tj ET"), e, Tol.Exact);
    }

    // 9.3.6: render mode 7 does not clip Type 3 text, so later painting is unclipped.
    [Fact]
    [Trait("Section", "9.3.6")]
    public void ClipModeDoesNotClipType3Text()
    {
        Check(Text("BT /F1 4 Tf 7 Tr 2 2 Td (a) Tj ET 0 0 1 rg 10 10 8 8 re f"), E().Rect(2, 2, 6, 6, Black).Rect(10, 10, 18, 18, Rgb(0, 0, 1)), Tol.Exact);
    }

    // 9.3.6: with a standard font, mode 7 adds the glyph outline to the clip, applied at ET. The outline comes from the font program, so
    // only bounds are checked: painted pixels lie inside the glyph's cell, and a thin stem ("I") covers a small part of it.
    [Fact]
    [Trait("Section", "9.3.6")]
    public void ClipModeClipsToTheGlyphOutline()
    {
        byte[] pdf = Page("BT /F2 20 Tf 7 Tr 4 4 Td (I) Tj ET 0 g 0 0 40 40 re f", "/Font << /F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>", w: 40, h: 40);
        Rgba32[] px = Pix(pdf);
        double area = BlackArea(px, 40, 0, 0, 40, 40);
        // Helvetica "I": advance 0.278 em, stem about 0.1 em wide and 0.72 em high at 20 pt = about 29 px^2.
        Assert.InRange(area, 15, 45);
        // Everything painted lies within the advance box (4..9.6) and the cap height (4..19), plus a pixel of antialiasing.
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                if (px[(y * 40) + x].G < 255)
                {
                    double yUp = 40 - y;
                    Assert.True(x >= 3 && x <= 10 && yUp >= 4 && yUp <= 20, $"stray pixel at ({x},{y})");
                }
            }
        }
    }
}
