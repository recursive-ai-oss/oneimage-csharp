using static OneImage.Pdf.Tests.SpecTruth.Spec;

namespace OneImage.Pdf.Tests.SpecTruth;

/// <summary>Solid fills in the device and ICC color spaces (ISO 32000-2 8.6) painted over the whole page.</summary>
public class ColorFillTests
{
    private static void Whole(string content, string resources, (double R, double G, double B) c, double tol, string[]? extra = null)
    {
        byte[] pdf = Page(content + " 0 0 20 20 re f", resources, extra: extra);
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, c), tol);
    }

    // 8.6.4.2 DeviceGray: g = 0 black .. 1 white. Values k/255 land exactly on a level.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(0.4)]
    [InlineData(0.6)]
    [InlineData(1.0)]
    [Trait("Section", "8.6.4.2")]
    public void DeviceGray(double g) => Whole($"{N(g)} g", "", Gray(g), Tol.Exact);

    // 8.6.4.3 DeviceRGB. Exact for components that are whole levels.
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0.2, 0.4, 0.8)]
    [InlineData(0.6, 0.2, 1)]
    [Trait("Section", "8.6.4.3")]
    public void DeviceRgb(double r, double g, double b) => Whole($"{N(r)} {N(g)} {N(b)} rg", "", Rgb(r, g, b), Tol.Exact);

    // 8.6.4.4 DeviceCMYK. The specification gives no conversion; the library's documented one (generic CMYK profile) is the
    // expectation, obtained through the public color API, to within 2 levels (it uses an exact float path, the API a table).
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0.4, 0.2, 0.6, 0.1)]
    [Trait("Section", "8.6.4.4")]
    public void DeviceCmyk(double c, double m, double y, double k)
    {
        byte[] pdf = Page($"{N(c)} {N(m)} {N(y)} {N(k)} k 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Cmyk(c, m, y, k)), 2);
    }

    // 8.6.4.4: CMYK with no ink at all is paper white, whatever the conversion.
    [Fact]
    [Trait("Section", "8.6.4.4")]
    public void CmykNoInkIsWhite() => Whole("0 0 0 0 k", "", Gray(1), Tol.One);

    // 8.6.5.5 ICCBased with an sRGB profile and N 3: the components are already sRGB.
    [Theory]
    [InlineData(1, 0.2, 0.2)]
    [InlineData(0.2, 0.4, 0.8)]
    [InlineData(0, 0, 0)]
    [Trait("Section", "8.6.5.5")]
    public void IccBasedSrgb(double r, double g, double b)
    {
        string icc = Stream("/N 3 /Alternate /DeviceRGB", System.Text.Encoding.Latin1.GetString(ColorProfile.Srgb.IccBytes.Span));
        byte[] pdf = Doc(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents 4 0 R /Resources << /ColorSpace << /CS0 [/ICCBased 5 0 R] >> >> >>",
            Stream("", $"/CS0 cs {N(r)} {N(g)} {N(b)} sc 0 0 20 20 re f"),
            icc);
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Rgb(r, g, b)), Tol.One);
    }

    // 8.6.5.5 ICCBased gray (N 1) with a gray profile whose curve is sRGB's: device gray values pass through to sRGB output.
    [Fact]
    [Trait("Section", "8.6.5.5")]
    public void IccBasedGray()
    {
        string icc = Stream("/N 1 /Alternate /DeviceGray", System.Text.Encoding.Latin1.GetString(ColorProfile.Gray.IccBytes.Span));
        byte[] pdf = Doc(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents 4 0 R /Resources << /ColorSpace << /CS0 [/ICCBased 5 0 R] >> >> >>",
            Stream("", "/CS0 cs 0.4 sc 0 0 20 20 re f"),
            icc);
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Gray(0.4)), Tol.One);
    }

    // 8.6.6.3 Indexed: the sample selects a palette entry in the base space.
    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(1, 0, 255, 0)]
    [InlineData(2, 0, 0, 255)]
    [Trait("Section", "8.6.6.3")]
    public void IndexedColor(int index, int r, int g, int b)
    {
        byte[] pdf = Doc(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents 4 0 R /Resources << /ColorSpace << /CS0 [/Indexed /DeviceRGB 2 <FF000000FF000000FF>] >> >> >>",
            Stream("", $"/CS0 cs {index} sc 0 0 20 20 re f"));
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Rgb8(r, g, b)), Tol.Exact);
    }

    // 8.6.6.4 Separation with a type 2 tint transform: tint t gives C0 + t (C1 - C0) in the alternate space.
    [Theory]
    [InlineData(0.0, 1.0, 1.0)]
    [InlineData(0.5, 0.5, 0.5)]
    [InlineData(1.0, 0.0, 0.0)]
    [Trait("Section", "8.6.6.4")]
    public void SeparationTintTransform(double tint, double g, double b)
    {
        byte[] pdf = Doc(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents 4 0 R /Resources << /ColorSpace << /CS0 [/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>] >> >> >>",
            Stream("", $"/CS0 cs {N(tint)} sc 0 0 20 20 re f"));
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Rgb(1, g, b)), Tol.One);
    }

    // 8.6.8: sc/scn with no color space set uses the current one; the initial fill and stroke color is black in DeviceGray.
    [Fact]
    [Trait("Section", "8.6.4.2")]
    public void InitialColorIsBlack() => Whole("", "", Gray(0), Tol.Exact);

    // 8.4.2 Table 52: g sets the fill color only; a stroke drawn after it still uses the stroke color (initial black).
    [Fact]
    [Trait("Section", "8.4.2")]
    public void StrokeAndFillColorsAreIndependent()
    {
        byte[] pdf = Page("1 0 0 rg 0 0 1 RG 4 w 0 10 m 20 10 l S 0 0 6 6 re f");
        Expected e = new Expected(20, 20).Rect(0, 8, 20, 12, Rgb(0, 0, 1)).Rect(0, 0, 6, 6, Rgb(1, 0, 0));
        Check(pdf, e, Tol.Exact);
    }

    // 8.4.4: q/Q saves and restores the color along with the rest of the graphics state.
    [Fact]
    [Trait("Section", "8.4.2")]
    public void ColorRestoredByQ()
    {
        byte[] pdf = Page("0 1 0 rg q 1 0 0 rg 0 0 5 5 re f Q 10 10 5 5 re f");
        Expected e = new Expected(20, 20).Rect(0, 0, 5, 5, Rgb(1, 0, 0)).Rect(10, 10, 15, 15, Rgb(0, 1, 0));
        Check(pdf, e, Tol.Exact);
    }
}

/// <summary>Rectangles and polygons: pixel-aligned ones are exact; others are checked through their covered area (ISO 32000-2 8.5.3, 8.5.3.3).</summary>
public class ShapeTests
{
    // 8.5.3.1: re appends x y w h; pixel row 0 is the top of the page, so user y grows upward.
    [Theory]
    [InlineData(0, 0, 5, 5)]
    [InlineData(15, 15, 5, 5)]
    [InlineData(3, 7, 10, 2)]
    [InlineData(19, 0, 1, 20)]
    [Trait("Section", "8.5.3.1")]
    public void AlignedRectangle(int x, int y, int w, int h)
    {
        byte[] pdf = Page($"0.2 0.4 0.6 rg {x} {y} {w} {h} re f");
        Check(pdf, new Expected(20, 20).Rect(x, y, x + w, y + h, Rgb(0.2, 0.4, 0.6)), Tol.Exact);
    }

    // 8.5.3.1: a negative width or height reverses the direction but covers the same area.
    [Fact]
    [Trait("Section", "8.5.3.1")]
    public void NegativeRectangleExtent()
    {
        byte[] pdf = Page("1 0 0 rg 10 10 -4 -6 re f");
        Check(pdf, new Expected(20, 20).Rect(6, 4, 10, 10, Rgb(1, 0, 0)), Tol.Exact);
    }

    // 8.5.3.2/11.7.4: fractional edges. Edge pixels carry the covered area fraction (anti-aliasing is implementation-defined; only the area is checked).
    [Theory]
    [InlineData(2.25, 3.5, 7.5, 9.25)]
    [InlineData(0.5, 0.5, 19.5, 19.5)]
    [InlineData(5.1, 5.1, 5.9, 14.9)]
    [Trait("Section", "8.5.3.2")]
    public void FractionalRectangleCoverage(double x0, double y0, double x1, double y1)
    {
        byte[] pdf = Page($"0 g {N(x0)} {N(y0)} {N(x1 - x0)} {N(y1 - y0)} re f");
        Check(pdf, new Expected(20, 20).Rect(x0, y0, x1, y1, Gray(0)), Tol.Edge);
    }

    // The covered area of a right triangle, summed over all pixels (diagonal edges: the total is what is specified).
    [Fact]
    [Trait("Section", "8.5.3.2")]
    public void TriangleArea()
    {
        byte[] pdf = Page("0 g 2 2 m 18 2 l 2 18 l h f");
        double area = BlackArea(Pix(pdf), 20, 0, 0, 20, 20);
        Assert.InRange(area, 128 - 1.0, 128 + 1.0);
    }

    // A circle from four cubic Béziers (kappa 0.5522847498) has area within 0.03% of pi r^2.
    [Fact]
    [Trait("Section", "8.5.2.2")]
    public void CircleArea()
    {
        const double k = 0.5522847498 * 8;
        byte[] pdf = Page($"0 g 18 10 m 18 {N(10 + k)} {N(10 + k)} 18 10 18 c {N(10 - k)} 18 2 {N(10 + k)} 2 10 c 2 {N(10 - k)} {N(10 - k)} 2 10 2 c {N(10 + k)} 2 18 {N(10 - k)} 18 10 c h f");
        double area = BlackArea(Pix(pdf), 20, 0, 0, 20, 20);
        // Curve flattening is implementation-defined (flatness tolerance, 10.6.5); an inscribed polygon within 0.1 px of the curve
        // loses at most about 0.07 px x perimeter (50 px) = 3.5 px^2.
        Assert.InRange(area, (Math.PI * 64) - 3.5, (Math.PI * 64) + 1.5);
    }

    // 8.5.3.1: a closed polygon spelled with m/l/h equals the re rectangle.
    [Fact]
    [Trait("Section", "8.5.2.1")]
    public void PolygonRectangleEqualsRe()
    {
        byte[] pdf = Page("0 0 1 rg 4 4 m 12 4 l 12 9 l 4 9 l h f");
        Check(pdf, new Expected(20, 20).Rect(4, 4, 12, 9, Rgb(0, 0, 1)), Tol.Exact);
    }

    // 8.5.3.3: an open subpath is closed implicitly by a fill.
    [Fact]
    [Trait("Section", "8.5.3.3")]
    public void FillClosesOpenSubpaths()
    {
        byte[] pdf = Page("0 0 1 rg 4 4 m 12 4 l 12 9 l 4 9 l f");
        Check(pdf, new Expected(20, 20).Rect(4, 4, 12, 9, Rgb(0, 0, 1)), Tol.Exact);
    }

    // 8.5.3.3: n ends the path without painting.
    [Fact]
    [Trait("Section", "8.5.3.1")]
    public void NoOpPathPaintsNothing() => Check(Page("0 g 2 2 10 10 re n"), new Expected(20, 20), Tol.Exact);

    // 8.5.3.2: a fill of a subpath with a single point or zero area paints nothing.
    [Fact]
    [Trait("Section", "8.5.3.3")]
    public void ZeroAreaFillPaintsNothing() => Check(Page("0 g 2 2 m 18 2 l f 5 5 0 8 re f"), new Expected(20, 20), Tol.Exact);

    // 8.5.3.3 fill rules, 8.5.3.3.2 non-zero winding / 8.5.3.3.3 even-odd. Two same-direction squares overlap in (6..12)x(6..12).
    private const string TwoOverlapping = "2 2 10 10 re 6 6 10 10 re";

    [Fact]
    [Trait("Section", "8.5.3.3.2")]
    public void NonZeroWindingFillsOverlap()
    {
        byte[] pdf = Page($"0 g {TwoOverlapping} f");
        Check(pdf, new Expected(20, 20).Rect(2, 2, 12, 12, Gray(0)).Rect(6, 6, 16, 16, Gray(0)), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.5.3.3.3")]
    public void EvenOddLeavesOverlapEmpty()
    {
        byte[] pdf = Page($"0 g {TwoOverlapping} f*");
        var e = new Expected(20, 20).Rect(2, 2, 12, 12, Gray(0)).Rect(6, 6, 16, 16, Gray(0));
        e.Fill((x, y) => (x is >= 6 and < 12 && y is >= 8 and < 14) ? Gray(1) : e[x, y]);
        Check(pdf, e, Tol.Exact);
    }

    // The same two rectangles drawn in opposite directions (re with a negative width): non-zero winding leaves the overlap empty.
    [Fact]
    [Trait("Section", "8.5.3.3.2")]
    public void NonZeroOppositeDirectionsCancel()
    {
        byte[] pdf = Page("0 g 2 2 10 10 re 16 6 -10 10 re f");
        var e = new Expected(20, 20).Rect(2, 2, 12, 12, Gray(0)).Rect(6, 6, 16, 16, Gray(0));
        e.Fill((x, y) => (x is >= 6 and < 12 && y is >= 8 and < 14) ? Gray(1) : e[x, y]);
        Check(pdf, e, Tol.Exact);
    }

    // Concentric squares in the same direction: non-zero fills the whole, even-odd leaves a hole.
    [Theory]
    [InlineData("f", false)]
    [InlineData("f*", true)]
    [InlineData("B", false)]
    [InlineData("B*", true)]
    [Trait("Section", "8.5.3.3")]
    public void ConcentricSquares(string op, bool hole)
    {
        // Stroke width 0 is not used: B/B* strokes with the default 1 unit line, drawn in the stroke color white-on-white would hide; use stroke gray 1.
        byte[] pdf = Page($"0 g 1 G 0 w 2 2 16 16 re 7 7 6 6 re {op}");
        var e = new Expected(20, 20).Rect(2, 2, 18, 18, Gray(0));
        if (hole)
        {
            e.Rect(7, 7, 13, 13, Gray(1));
        }

        // The hairline stroke is device-thin (about one pixel) and white: skip the boundary pixels when it is present.
        if (op.StartsWith('B'))
        {
            Rgba32[] px = Pix(pdf);
            Pixel(px, 20, 10, 3, Gray(0), Tol.Exact);
            Pixel(px, 20, 3, 10, Gray(0), Tol.Exact);
            Pixel(px, 20, 10, 10, hole ? Gray(1) : Gray(0), Tol.Exact);
            return;
        }

        Check(pdf, e, Tol.Exact);
    }

    // 8.5.3.3.2: the inner square drawn in the opposite direction is a hole under non-zero winding.
    [Fact]
    [Trait("Section", "8.5.3.3.2")]
    public void NonZeroInnerOppositeDirectionIsAHole()
    {
        byte[] pdf = Page("0 g 2 2 16 16 re 13 7 -6 6 re f");
        Check(pdf, new Expected(20, 20).Rect(2, 2, 18, 18, Gray(0)).Rect(7, 7, 13, 13, Gray(1)), Tol.Exact);
    }

    // A pentagram drawn in one stroke winds twice in the middle: non-zero fills it, even-odd leaves the centre empty.
    [Theory]
    [InlineData("f", true)]
    [InlineData("f*", false)]
    [Trait("Section", "8.5.3.3")]
    public void PentagramCentre(string op, bool centreFilled)
    {
        // Points on a circle of radius 9 around (10,10), visited every second vertex.
        var sb = new System.Text.StringBuilder("0 g ");
        for (int i = 0; i < 5; i++)
        {
            double a = Math.PI / 2 + (i * 4 * Math.PI / 5);
            sb.Append(N(10 + (9 * Math.Cos(a)))).Append(' ').Append(N(10 + (9 * Math.Sin(a)))).Append(i == 0 ? " m " : " l ");
        }

        sb.Append("h ").Append(op);
        Rgba32[] px = Pix(Page(sb.ToString()));
        Pixel(px, 20, 10, 10, centreFilled ? Gray(0) : Gray(1), Tol.Exact);
        // A point in the top arm (above the inner pentagon) is inside under both rules.
        Pixel(px, 20, 10, 5, Gray(0), Tol.Exact);
    }
}

/// <summary>The current transformation matrix (ISO 32000-2 8.3.4, 8.3.3).</summary>
public class MatrixTests
{
    private static void Run(string cm, double x0, double y0, double x1, double y1)
    {
        byte[] pdf = Page($"0.2 0.4 0.8 rg {cm} cm 1 2 3 4 re f");
        Check(pdf, new Expected(20, 20).Rect(x0, y0, x1, y1, Rgb(0.2, 0.4, 0.8)), Tol.Exact);
    }

    // The rectangle is (1,2)-(4,6) before the matrix. Matrix [a b c d e f] maps (x,y) to (ax+cy+e, bx+dy+f) (8.3.4).
    [Fact] [Trait("Section", "8.3.4")] public void Translate() => Run("1 0 0 1 5 3", 6, 5, 9, 9);
    [Fact] [Trait("Section", "8.3.4")] public void Scale() => Run("2 0 0 3 0 0", 2, 6, 8, 18);
    [Fact] [Trait("Section", "8.3.4")] public void NegativeScaleMirrorsX() => Run("-1 0 0 1 20 0", 16, 2, 19, 6);
    [Fact] [Trait("Section", "8.3.4")] public void FlipY() => Run("1 0 0 -1 0 20", 1, 14, 4, 18);
    [Fact] [Trait("Section", "8.3.4")] public void Rotate90() => Run("0 1 -1 0 20 0", 14, 1, 18, 4);
    [Fact] [Trait("Section", "8.3.4")] public void Rotate180() => Run("-1 0 0 -1 20 20", 16, 14, 19, 18);
    [Fact] [Trait("Section", "8.3.4")] public void Rotate270() => Run("0 -1 1 0 0 20", 2, 16, 6, 19);

    // 8.3.4: a later cm applies first to the points: scale 2, then translate by 5 gives x' = 2x + 5.
    [Fact]
    [Trait("Section", "8.3.4")]
    public void ConcatenationOrder()
    {
        byte[] pdf = Page("0 g 1 0 0 1 5 0 cm 2 0 0 2 0 0 cm 1 1 2 2 re f");
        Check(pdf, new Expected(20, 20).Rect(7, 2, 11, 6, Gray(0)), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.3.4")]
    public void ConcatenationOrderRotateThenTranslate()
    {
        // Translate first (outer), then rotate 90: the rectangle is rotated about the new origin (5,0).
        byte[] pdf = Page("0 g 1 0 0 1 5 0 cm 0 1 -1 0 0 0 cm 1 2 3 4 re f");
        // (x,y) -> (-y+5, x): x in [5-6,5-2] = [-1,3] -> clipped by page; y in [1,4]
        Check(pdf, new Expected(20, 20).Rect(-1, 1, 3, 4, Gray(0)), Tol.Exact);
    }

    // 8.4.2/8.3.4: q/Q restores the matrix.
    [Fact]
    [Trait("Section", "8.4.2")]
    public void QRestoresMatrix()
    {
        byte[] pdf = Page("0 g q 2 0 0 2 0 0 cm Q 1 1 3 3 re f");
        Check(pdf, new Expected(20, 20).Rect(1, 1, 4, 4, Gray(0)), Tol.Exact);
    }

    // A rotation by 45 degrees turns a 10 x 10 square into a diamond of the same area.
    [Fact]
    [Trait("Section", "8.3.4")]
    public void Rotate45KeepsArea()
    {
        const double c = 0.70710678118;
        byte[] pdf = Page($"0 g {N(c)} {N(c)} {N(-c)} {N(c)} 10 2 cm 0 0 10 10 re f");
        double area = BlackArea(Pix(pdf), 20, 0, 0, 20, 20);
        Assert.InRange(area, 99, 101);
        // The diamond's corners: bottom at (10,2), right at (10+7.07, 9.07), top at (10, 16.14).
        Rgba32[] px = Pix(pdf);
        Pixel(px, 20, 10, 10, Gray(0), Tol.Exact);
        Pixel(px, 20, 1, 10, Gray(1), Tol.Exact);
    }

    // 8.3.4 with a non-uniform scale then a stroke: line width is in user space, so it scales with the matrix.
    [Fact]
    [Trait("Section", "8.4.3.2")]
    public void LineWidthScalesWithMatrix()
    {
        // 1-unit wide vertical line under a 4x horizontal scale is 4 pixels wide.
        byte[] pdf = Page("0 g 4 0 0 1 0 0 cm 1 w 2.5 2 m 2.5 18 l S");
        Check(pdf, new Expected(20, 20).Rect(8, 2, 12, 18, Gray(0)), Tol.Exact);
    }
}

/// <summary>Page geometry: MediaBox, CropBox, /Rotate and UserUnit (ISO 32000-2 7.7.3.3, 14.11.2).</summary>
public class PageGeometryTests
{
    private static byte[] Boxed(string boxes, string content) => Boxes(boxes, content);

    // The base rectangle for all rotations: a 20 x 30 page with a 6 x 4 block at (2,3) and a small 2 x 2 marker at (16,26).
    private const string Content = "1 0 0 rg 2 3 6 4 re f 0 0 1 rg 16 26 2 2 re f";

    private static (double X0, double Y0, double X1, double Y1) Rot(int rot, double w, double h, double x0, double y0, double x1, double y1) => rot switch
    {
        0 => (x0, y0, x1, y1),
        // Clockwise quarter turns, derived from image coordinates: 90 -> (x,y) maps to (y, w-x).
        90 => (y0, w - x1, y1, w - x0),
        180 => (w - x1, h - y1, w - x0, h - y0),
        270 => (h - y1, x0, h - y0, x1),
        _ => throw new ArgumentOutOfRangeException(nameof(rot)),
    };

    // 7.7.3.3 Table 31 /Rotate: the page is rotated clockwise by this many degrees when displayed.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [Trait("Section", "7.7.3.3")]
    public void PageRotate(int rotate, int effective)
    {
        double w = 20, h = 30;
        byte[] pdf = Page(Content, w: w, h: h, pageExtra: $"/Rotate {rotate}");
        bool swap = effective is 90 or 270;
        int ow = (int)(swap ? h : w), oh = (int)(swap ? w : h);
        var e = new Expected(ow, oh);
        var r1 = Rot(effective, w, h, 2, 3, 8, 7);
        var r2 = Rot(effective, w, h, 16, 26, 18, 28);
        e.Rect(r1.X0, r1.Y0, r1.X1, r1.Y1, Rgb(1, 0, 0)).Rect(r2.X0, r2.Y0, r2.X1, r2.Y1, Rgb(0, 0, 1));
        Check(pdf, e, Tol.Exact);
    }

    // 7.7.3.3: MediaBox with a non-zero origin; user space (10,20) is the lower-left of the output.
    [Fact]
    [Trait("Section", "7.7.3.3")]
    public void MediaBoxOffset()
    {
        byte[] pdf = Boxed("/MediaBox [10 20 40 60]", "1 0 0 rg 12 25 8 5 re f 0 0 1 rg 38 58 2 2 re f");
        // Output is 30 x 40. The page also carries the default MediaBox from the helper; the later key wins in a dictionary.
        var e = new Expected(30, 40).Rect(2, 5, 10, 10, Rgb(1, 0, 0)).Rect(28, 38, 30, 40, Rgb(0, 0, 1));
        Check(pdf, e, Tol.Exact);
    }

    // 7.9.5: a rectangle may name any two diagonally opposite corners.
    [Fact]
    [Trait("Section", "7.9.5")]
    public void MediaBoxWithReversedCorners()
    {
        byte[] pdf = Boxed("/MediaBox [40 60 10 20]", "1 0 0 rg 12 25 8 5 re f");
        Check(pdf, new Expected(30, 40).Rect(2, 5, 10, 10, Rgb(1, 0, 0)), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "7.7.3.3")]
    public void MediaBoxWithNegativeOrigin()
    {
        byte[] pdf = Boxed("/MediaBox [-10 -10 10 10]", "0 g -10 -10 5 5 re f 0 0 5 5 re f");
        Check(pdf, new Expected(20, 20).Rect(0, 0, 5, 5, Gray(0)).Rect(10, 10, 15, 15, Gray(0)), Tol.Exact);
    }

    // 14.11.2: the CropBox is the region displayed (default renderer option); content outside it is not drawn.
    [Fact]
    [Trait("Section", "14.11.2")]
    public void CropBoxSelectsVisibleRegion()
    {
        byte[] pdf = Boxed("/MediaBox [0 0 100 100] /CropBox [15 25 35 55]", "1 0 0 rg 20 30 5 5 re f 0 g 0 0 100 10 re f");
        Check(pdf, new Expected(20, 30).Rect(5, 5, 10, 10, Rgb(1, 0, 0)), Tol.Exact);
    }

    // 7.7.3.3: the CropBox and MediaBox can both be offset and rotated together.
    [Fact]
    [Trait("Section", "7.7.3.3")]
    public void CropBoxWithRotate()
    {
        byte[] pdf = Boxed("/MediaBox [0 0 100 100] /CropBox [10 10 30 40] /Rotate 90", "1 0 0 rg 12 12 4 2 re f");
        // Cropped page is 20 x 30; the block is at (2,2)-(6,4) in it. Rotated 90 clockwise: (x,y)->(y, w-x) with w=20.
        var r = Rot(90, 20, 30, 2, 2, 6, 4);
        Check(pdf, new Expected(30, 20).Rect(r.X0, r.Y0, r.X1, r.Y1, Rgb(1, 0, 0)), Tol.Exact);
    }

    // 7.7.3.3 Table 31 /UserUnit (PDF 1.6): one default user space unit is UserUnit/72 inch, so 2 doubles the size.
    [Fact]
    [Trait("Section", "7.7.3.3")]
    public void UserUnitScalesThePage()
    {
        byte[] pdf = Page("1 0 0 rg 2 3 3 2 re f", w: 10, h: 10, pageExtra: "/UserUnit 2");
        Check(pdf, new Expected(20, 20).Rect(4, 6, 10, 10, Rgb(1, 0, 0)), Tol.Exact);
    }

    // 8.3.2.3: the default coordinate system has its origin at the lower-left of the displayed page, also when the dpi differs.
    [Fact]
    [Trait("Section", "8.3.2.3")]
    public void DpiScalesTheOutput()
    {
        byte[] pdf = Page("1 0 0 rg 2 3 4 4 re f", w: 10, h: 10);
        Rgba32[] px = Pix(pdf, new PdfRenderOptions { Dpi = 144 });
        Assert.Equal(20 * 20, px.Length);
        // 144 dpi: 2 pixels per unit. The block spans x 4..12, y (from top) 20-14..20-6 = 6..14.
        var e = new Expected(20, 20).Rect(4, 6, 12, 14, Rgb(1, 0, 0));
        Check(px, e, Tol.Exact);
    }
}

/// <summary>Clipping paths (ISO 32000-2 8.5.4).</summary>
public class ClipTests
{
    [Fact]
    [Trait("Section", "8.5.4")]
    public void RectangularClip()
    {
        byte[] pdf = Page("4 4 8 8 re W n 0 g 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(4, 4, 12, 12, Gray(0)), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.5.4")]
    public void NestedClipsIntersect()
    {
        byte[] pdf = Page("2 2 10 10 re W n 8 8 10 10 re W n 0 g 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(8, 8, 12, 12, Gray(0)), Tol.Exact);
    }

    // 8.5.4: the clip only ever shrinks; a later larger W n cannot widen it.
    [Fact]
    [Trait("Section", "8.5.4")]
    public void ClipCannotGrow()
    {
        byte[] pdf = Page("4 4 8 8 re W n 0 0 20 20 re W n 0 g 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(4, 4, 12, 12, Gray(0)), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.5.4")]
    public void EvenOddClipMakesARing()
    {
        byte[] pdf = Page("2 2 16 16 re 7 7 6 6 re W* n 0 g 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(2, 2, 18, 18, Gray(0)).Rect(7, 7, 13, 13, Gray(1)), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.5.4")]
    public void NonZeroClipOfSameDirectionSquaresHasNoHole()
    {
        byte[] pdf = Page("2 2 16 16 re 7 7 6 6 re W n 0 g 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(2, 2, 18, 18, Gray(0)), Tol.Exact);
    }

    // 8.5.4: Q restores the clip made inside q.
    [Fact]
    [Trait("Section", "8.5.4")]
    public void ClipRestoredByQ()
    {
        byte[] pdf = Page("q 4 4 8 8 re W n Q 0 g 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Gray(0)), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "8.5.4")]
    public void NestedQRestoresOnlyInnerClip()
    {
        byte[] pdf = Page("2 2 14 14 re W n q 8 8 10 10 re W n Q 0 g 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(2, 2, 16, 16, Gray(0)), Tol.Exact);
    }

    // 8.5.4: the clip takes effect after the painting operator that follows W, so that fill itself is not clipped.
    [Fact]
    [Trait("Section", "8.5.4")]
    public void ClipAppliesAfterThePaintingOperator()
    {
        // The stroke is painted before the clip takes effect, so it reaches outside the rectangle (width 2: one unit each side).
        // The following fill is clipped.
        byte[] pdf = Page("4 4 8 8 re W 0 g 2 w S 1 0 0 rg 0 0 20 20 re f");
        var e = new Expected(20, 20).Rect(3, 3, 13, 13, Gray(0)).Rect(5, 5, 11, 11, Gray(1));
        e.Rect(4, 4, 12, 12, Rgb(1, 0, 0));
        Check(pdf, e, Tol.Exact);
    }

    // The clip is in effect for strokes too.
    [Fact]
    [Trait("Section", "8.5.4")]
    public void ClipAffectsStroke()
    {
        byte[] pdf = Page("0 0 10 20 re W n 0 g 4 w 0 10 m 20 10 l S");
        Check(pdf, new Expected(20, 20).Rect(0, 8, 10, 12, Gray(0)), Tol.Exact);
    }

    // The clip is in user space at the time it is defined: a later cm doesn't move it.
    [Fact]
    [Trait("Section", "8.5.4")]
    public void ClipIsFixedWhenDefined()
    {
        byte[] pdf = Page("2 2 6 6 re W n 1 0 0 1 8 8 cm 0 g -8 -8 40 40 re f");
        Check(pdf, new Expected(20, 20).Rect(2, 2, 8, 8, Gray(0)), Tol.Exact);
    }

    // Anti-aliased clip edge: a half-covered pixel column gets half the paint (area tolerance).
    [Fact]
    [Trait("Section", "8.5.4")]
    public void FractionalClipCoverage()
    {
        byte[] pdf = Page("4.5 4 7 8 re W n 0 g 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(4.5, 4, 11.5, 12, Gray(0)), Tol.Edge);
    }
}
