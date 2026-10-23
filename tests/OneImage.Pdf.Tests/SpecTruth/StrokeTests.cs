using static OneImage.Pdf.Tests.SpecTruth.Spec;

namespace OneImage.Pdf.Tests.SpecTruth;

/// <summary>Stroking: width, caps, joins, miter limit and dash patterns on axis-aligned geometry (ISO 32000-2 8.4.3, 8.5.3.2).</summary>
public class StrokeTests
{
    private static readonly (double R, double G, double B) Black = Gray(0);

    // 8.4.3.2: the line is centered on the path, line-width wide; butt caps (the default) end flush with the endpoints.
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [Trait("Section", "8.4.3.2")]
    public void HorizontalLineWidth(int w)
    {
        byte[] pdf = Page($"0 g {w} w 3 10 m 17 10 l S");
        Check(pdf, new Expected(20, 20).Rect(3, 10 - (w / 2.0), 17, 10 + (w / 2.0), Black), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.4.3.2")]
    public void VerticalLine()
    {
        byte[] pdf = Page("0 g 4 w 10 3 m 10 17 l S");
        Check(pdf, new Expected(20, 20).Rect(8, 3, 12, 17, Black), Tol.Exact);
    }

    // 8.4.3.2: a width of 0 is the thinnest line the device can render, one device pixel. Centered on a pixel row it fills exactly that row.
    [Fact]
    [Trait("Section", "8.4.3.2")]
    public void ZeroWidthIsOneDevicePixel()
    {
        byte[] pdf = Page("0 g 0 w 3 10.5 m 17 10.5 l S");
        Check(pdf, new Expected(20, 20).Rect(3, 10, 17, 11, Black), Tol.One);
    }

    // 8.4.3.3 Table 54, line cap styles: 0 butt, 1 round, 2 projecting square.
    [Fact]
    [Trait("Section", "8.4.3.3")]
    public void ProjectingSquareCapExtendsHalfAWidth()
    {
        byte[] pdf = Page("0 g 4 w 2 J 6 10 m 14 10 l S");
        Check(pdf, new Expected(20, 20).Rect(4, 8, 16, 12, Black), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.4.3.3")]
    public void ButtCapIsFlush()
    {
        byte[] pdf = Page("0 g 4 w 0 J 6 10 m 14 10 l S");
        Check(pdf, new Expected(20, 20).Rect(6, 8, 14, 12, Black), Tol.Exact);
    }

    // A round cap adds a half disc at each end: the total covered area is length * width + pi r^2 (flattening tolerance applies).
    [Fact]
    [Trait("Section", "8.4.3.3")]
    public void RoundCapsAddHalfDiscs()
    {
        byte[] pdf = Page("0 g 6 w 1 J 7 10 m 13 10 l S");
        double area = BlackArea(Pix(pdf), 20, 0, 0, 20, 20);
        double exact = (6 * 6) + (Math.PI * 9);
        Assert.InRange(area, exact - 1.5, exact + 0.8);
        // The centre of the cap is fully covered, a point just beyond the radius is not.
        Rgba32[] px = Pix(pdf);
        Pixel(px, 20, 5, 9, Black, Tol.Exact); // 1.5 beyond the end... inside the radius of 3
        Pixel(px, 20, 2, 9, Gray(1), Tol.Exact);
    }

    // 8.4.3.3: for a degenerate subpath, only a round cap marks the page (a filled circle); butt and square produce no output.
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [Trait("Section", "8.5.3.2")]
    public void ZeroLengthSubpathWithButtOrSquareCapMarksNothing(int cap)
    {
        byte[] pdf = Page($"0 g 6 w {cap} J 10 10 m 10 10 l S");
        Assert.Equal(0, BlackArea(Pix(pdf), 20, 0, 0, 20, 20), 6);
    }

    [Fact]
    [Trait("Section", "8.5.3.2")]
    public void ZeroLengthSubpathWithRoundCapIsADisc()
    {
        byte[] pdf = Page("0 g 6 w 1 J 10 10 m 10 10 l S");
        Assert.InRange(BlackArea(Pix(pdf), 20, 0, 0, 20, 20), (Math.PI * 9) - 1.2, (Math.PI * 9) + 0.8);
    }

    // Joins at a right angle. Centerlines (4,4)->(4,12)->(14,12), width 4. The bars alone cover 68 square units;
    // the missing outer corner square is 4: miter fills all of it, bevel half of it (a triangle of area 2), round a quarter disc (pi).
    private const string Corner = "0 g 4 w 4 4 m 4 12 l 14 12 l S";

    [Fact]
    [Trait("Section", "8.4.3.4")]
    public void MiterJoinFillsTheCorner()
    {
        byte[] pdf = Page("0 j " + Corner);
        Check(pdf, new Expected(20, 20).Rect(2, 4, 6, 14, Black).Rect(2, 10, 14, 14, Black), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.4.3.4")]
    public void BevelJoinCutsTheCorner()
    {
        byte[] pdf = Page("2 j " + Corner);
        Rgba32[] px = Pix(pdf);
        Assert.InRange(BlackArea(px, 20, 0, 0, 20, 20), 70 - 0.8, 70 + 0.8);
        // The corner pixel's outer corner (2..3 x 13..14 from the bevel's diagonal) is not covered: pixel (2, 6) from the top is x 2..3, y 13..14.
        Pixel(px, 20, 2, 6, Gray(1), Tol.Edge);
    }

    [Fact]
    [Trait("Section", "8.4.3.4")]
    public void RoundJoinAddsAQuarterDisc()
    {
        byte[] pdf = Page("1 j " + Corner);
        double area = BlackArea(Pix(pdf), 20, 0, 0, 20, 20);
        Assert.InRange(area, 68 + Math.PI - 0.8, 68 + Math.PI + 0.5);
    }

    // 8.4.3.5: the miter length / line width ratio at a right angle is sqrt(2) ~ 1.414: with a limit below that the join becomes a bevel.
    [Theory]
    [InlineData("1.4 M", 70.0)]
    [InlineData("1.5 M", 72.0)]
    [InlineData("10 M", 72.0)]
    [Trait("Section", "8.4.3.5")]
    public void MiterLimit(string setting, double expectedArea)
    {
        byte[] pdf = Page("0 j " + setting + " " + Corner);
        Assert.InRange(BlackArea(Pix(pdf), 20, 0, 0, 20, 20), expectedArea - 0.8, expectedArea + 0.8);
    }

    // 8.5.3.2: a closed rectangle stroked with miter joins covers the outer rectangle minus the inner one.
    [Fact]
    [Trait("Section", "8.4.3.4")]
    public void StrokedRectangleRing()
    {
        byte[] pdf = Page("0 g 2 w 4 4 12 12 re S");
        Check(pdf, new Expected(20, 20).Rect(3, 3, 17, 17, Black).Rect(5, 5, 15, 15, Gray(1)), Tol.Exact);
    }

    // 8.5.3.2: with h the closing segment is joined; without it the start corner is capped. With butt caps an open rectangle leaves a notch.
    [Fact]
    [Trait("Section", "8.4.3.4")]
    public void OpenSubpathHasNoJoinAtTheStart()
    {
        byte[] pdf = Page("0 g 2 w 4 4 m 16 4 l 16 16 l 4 16 l 4 4 l S");
        // The start/end point (4,4): the first segment (butt) ends flush at x=4 so the outer corner square (3..4, 3..4) is empty.
        Check(pdf, new Expected(20, 20).Rect(4, 3, 17, 5, Black).Rect(15, 3, 17, 17, Black).Rect(3, 15, 17, 17, Black).Rect(3, 4, 5, 17, Black), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.4.3.4")]
    public void ClosedSubpathJoinsTheStart()
    {
        byte[] pdf = Page("0 g 2 w 4 4 m 16 4 l 16 16 l 4 16 l h S");
        Check(pdf, new Expected(20, 20).Rect(3, 3, 17, 17, Black).Rect(5, 5, 15, 15, Gray(1)), Tol.Exact);
    }

    // 8.4.3.2 / 8.3.4: a stroke is drawn in the CTM in effect when it is painted; a non-uniform scale makes the pen elliptical.
    [Fact]
    [Trait("Section", "8.4.3.2")]
    public void NonUniformScaleWidensTheStroke()
    {
        byte[] pdf = Page("0 g 2 0 0 1 0 0 cm 2 w 2 3 m 2 17 l S 3 10 m 8 10 l S");
        // Vertical line: width 2 * 2 = 4 px (x 2*2 +- 2 -> 2..6); horizontal line keeps height 2 px: x 6..16, y 9..11.
        Check(pdf, new Expected(20, 20).Rect(2, 3, 6, 17, Black).Rect(6, 9, 16, 11, Black), Tol.Exact);
    }

    // 8.4.3.6 dash patterns: [on off] phase d. A 2-wide horizontal line from x=0 to 20.
    [Theory]
    [InlineData("[4 2] 0 d", new double[] { 0, 4, 6, 10, 12, 16, 18, 20 })]
    [InlineData("[4 2] 1 d", new double[] { 0, 3, 5, 9, 11, 15, 17, 20 })]
    [InlineData("[4 2] 6 d", new double[] { 0, 4, 6, 10, 12, 16, 18, 20 })]
    [InlineData("[3] 0 d", new double[] { 0, 3, 6, 9, 12, 15, 18, 20 })]
    [InlineData("[3] 2 d", new double[] { 0, 1, 4, 7, 10, 13, 16, 19 })]
    [InlineData("[5 3 2] 0 d", new double[] { 0, 5, 8, 10, 15, 18 })]
    [InlineData("[2 3] 11 d", new double[] { 0, 1, 4, 6, 9, 11, 14, 16, 19, 20 })]
    [InlineData("[2] 1 d", new double[] { 0, 1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 20 })]
    [InlineData("[] 0 d", new double[] { 0, 20 })]
    [Trait("Section", "8.4.3.6")]
    public void DashPattern(string dash, double[] runs)
    {
        byte[] pdf = Page($"0 g 2 w {dash} 0 10 m 20 10 l S");
        var e = new Expected(20, 20);
        for (int i = 0; i + 1 < runs.Length; i += 2)
        {
            e.Rect(runs[i], 9, runs[i + 1], 11, Black);
        }

        Check(pdf, e, Tol.Exact);
    }

    // 8.4.3.6: dash lengths are in user space units, so they scale with the CTM; line width does too.
    [Fact]
    [Trait("Section", "8.4.3.6")]
    public void DashScalesWithTheMatrix()
    {
        byte[] pdf = Page("0 g 2 0 0 2 0 0 cm 1 w [2 1] 0 d 0 5 m 10 5 l S");
        Check(pdf, new Expected(20, 20).Rect(0, 9, 4, 11, Black).Rect(6, 9, 10, 11, Black).Rect(12, 9, 16, 11, Black).Rect(18, 9, 20, 11, Black), Tol.Exact);
    }

    // Projecting-square caps apply to every dash, so each dash grows by half the width at both ends (8.4.3.6: "caps are applied to each dash").
    [Fact]
    [Trait("Section", "8.4.3.6")]
    public void DashesGetCaps()
    {
        byte[] pdf = Page("0 g 2 w 2 J [2 4] 0 d 2 10 m 19 10 l S");
        // Dashes of the path start at x=2: 2..4, 8..10, 14..16 (the next would start at 20, past the end): each extended by 1 on both sides.
        Check(pdf, new Expected(20, 20).Rect(1, 9, 5, 11, Black).Rect(7, 9, 11, 11, Black).Rect(13, 9, 17, 11, Black), Tol.Exact);
    }

    // 8.5.3.2: a zero-length dash still gets its line caps, oriented along the path: a projecting square cap makes a w x w square,
    // a round cap a disc (unlike a zero-length subpath, which marks the page only with round caps).
    [Fact]
    [Trait("Section", "8.5.3.2")]
    public void ZeroLengthDashWithSquareCapMakesASquare()
    {
        byte[] pdf = Page("0 g 2 w 2 J [0 4] 0 d 2 10 m 20 10 l S");
        var e = new Expected(20, 20);
        for (int x = 2; x < 20; x += 4)
        {
            e.Rect(x - 1, 9, x + 1, 11, Black);
        }

        Check(pdf, e, Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.5.3.2")]
    public void ZeroLengthDashWithRoundCapMakesADisc()
    {
        byte[] pdf = Page("0 g 2 w 1 J [0 4] 0 d 2 10 m 20 10 l S");
        double area = BlackArea(Pix(pdf), 20, 0, 0, 20, 20);
        // Flattening is implementation-defined; an 8-gon (the coarsest within 0.1 px of a unit circle) has 90% of the area.
        Assert.InRange(area, (5 * Math.PI * 0.88), (5 * Math.PI) + 0.4);
    }

    // q/Q restores the dash pattern and the other line parameters.
    [Fact]
    [Trait("Section", "8.4.2")]
    public void LineParametersRestoredByQ()
    {
        byte[] pdf = Page("0 g 2 w q [2 2] 0 d 6 w 2 J Q 3 10 m 17 10 l S");
        Check(pdf, new Expected(20, 20).Rect(3, 9, 17, 11, Black), Tol.Exact);
    }

    // 11.6.4.4: CA applies to strokes; overlapping parts of one stroke are not double painted (the stroke is one object).
    [Fact]
    [Trait("Section", "11.7.4.4")]
    public void SelfOverlappingStrokeIsPaintedOnce()
    {
        byte[] pdf = Page("/GS0 gs 0 g 4 w 2 10 m 18 10 l 10 10 l 10 2 l S", "/ExtGState << /GS0 << /CA 0.5 >> >>");
        // Path goes right then back over itself to the middle, then down. The doubled part (10..18) must not darken twice.
        var e = new Expected(20, 20).Rect(2, 8, 18, 12, Black, 0.5).Rect(8, 2, 12, 8, Black, 0.5);
        // Skip the join region where the vertical arm meets the horizontal ones (stroke geometry there is implementation detail).
        e.Ignore((x, y) => x is >= 7 and <= 12 && y is >= 7 and <= 13);
        Check(pdf, e, Tol.One);
    }

    // 11.7.4.4: with B the fill and stroke are one object, a non-isolated knockout group: where the stroke overlaps the fill it replaces it
    // (blue at 50% over the page), it is not composited on top of the fill.
    [Fact]
    [Trait("Section", "11.7.4.4")]
    public void FillAndStrokeKnockOutEachOther()
    {
        byte[] pdf = Page("/G gs 1 0 0 rg 0 0 1 RG 4 w 6 6 8 8 re B", "/ExtGState << /G << /ca 0.5 /CA 0.5 >> >>");
        var e = new Expected(20, 20).Rect(4, 4, 16, 16, Rgb(0, 0, 1), 0.5).Rect(8, 8, 12, 12, Gray(1)).Rect(8, 8, 12, 12, Rgb(1, 0, 0), 0.5);
        Check(pdf, e, Tol.One);
    }

    // 8.5.3.1 Table 59: s closes and strokes (the corner is joined, as with h S); b closes, fills and strokes.
    [Fact]
    [Trait("Section", "8.5.3.1")]
    public void CloseAndStrokeJoinsTheStart()
    {
        byte[] pdf = Page("0 g 2 w 4 4 m 16 4 l 16 16 l 4 16 l s");
        Check(pdf, new Expected(20, 20).Rect(3, 3, 17, 17, Black).Rect(5, 5, 15, 15, Gray(1)), Tol.Exact);
    }

    // 8.5.3.1: B paints the fill first and the stroke on top of it (opaque case).
    [Theory]
    [InlineData("B")]
    [InlineData("b")]
    [InlineData("B*")]
    [InlineData("b*")]
    [Trait("Section", "8.5.3.1")]
    public void FillThenStroke(string op)
    {
        byte[] pdf = Page($"1 0 0 rg 0 0 1 RG 4 w 6 6 8 8 re {op}");
        Check(pdf, new Expected(20, 20).Rect(6, 6, 14, 14, Rgb(1, 0, 0)).Rect(4, 4, 16, 16, Rgb(0, 0, 1)).Rect(8, 8, 12, 12, Gray(1)).Rect(8, 8, 12, 12, Rgb(1, 0, 0)), Tol.Exact);
    }
}
