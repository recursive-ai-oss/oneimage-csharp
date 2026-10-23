using static OneImage.Pdf.Tests.SpecTruth.Spec;

namespace OneImage.Pdf.Tests.SpecTruth;

/// <summary>Axial and radial shadings with the function types of ISO 32000-2 7.10, evaluated at pixel centers (8.7.4.5).</summary>
public class ShadingTests
{
    private const string GrayRamp = "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >>";

    private static readonly int[] SampleBytes = [0, 0, 0, 255, 0, 0, 255, 255, 255];

    private static readonly (double, double)[] Corners = [(-0.5, -0.5), (0.5, -0.5), (-0.5, 0.5), (0.5, 0.5)];

    private delegate (double R, double G, double B)? Eval(double x, double y);

    /// <summary>
    /// Builds the expectation by evaluating the shading at each pixel center (user space, y up). Where the painted/unpainted decision
    /// differs between the center and a corner of the pixel the result is a partial pixel, which is not compared.
    /// </summary>
    private static Expected Paint(Eval f, (double R, double G, double B)? outside = null, Func<int, int, bool>? clip = null, double alpha = 1)
    {
        var e = new Expected(20, 20);
        for (int py = 0; py < 20; py++)
        {
            for (int px = 0; px < 20; px++)
            {
                if (clip is not null && !clip(px, py))
                {
                    continue;
                }

                double x = px + 0.5, y = 20 - py - 0.5;
                var c = f(x, y);
                bool partial = false;
                foreach ((double dx, double dy) in Corners)
                {
                    if ((f(x + (0.999 * dx), y + (0.999 * dy)) is null) != (c is null))
                    {
                        partial = true;
                    }
                }

                if (partial)
                {
                    e.IgnorePixel(px, py);
                    continue;
                }

                var v = c ?? outside;
                if (v is { } col)
                {
                    e.Over(px, py, (col.R * 255, col.G * 255, col.B * 255), alpha);
                }
            }
        }

        return e;
    }

    private static string AxialDict(string coords, string function, string extend = "[false false]", string extra = "") =>
        $"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords {coords} /Function {function} /Extend {extend} {extra} >>";

    private static byte[] Sh(string shading, string content = "/Sh0 sh", string[]? extra = null, string pageExtra = "") =>
        Page(content, $"/Shading << /Sh0 {shading} >> {pageExtra}", extra: extra);

    private static double Clamp01(double v) => Math.Min(1, Math.Max(0, v));

    private static (double, double, double) Gray3(double t) => (t, t, t);

    // Axial: t = ((p - p0) . (p1 - p0)) / |p1 - p0|^2 (8.7.4.5.2); the color is the function at t.
    private static double Axial(double x, double y, double x0, double y0, double x1, double y1)
    {
        double dx = x1 - x0, dy = y1 - y0;
        return (((x - x0) * dx) + ((y - y0) * dy)) / ((dx * dx) + (dy * dy));
    }

    [Fact]
    [Trait("Section", "8.7.4.5.3")]
    public void AxialHorizontalRamp()
    {
        byte[] pdf = Sh(AxialDict("[0 0 20 0]", GrayRamp));
        Check(pdf, Paint((x, y) => Gray3(Axial(x, y, 0, 0, 20, 0))), Tol.One);
    }

    [Fact]
    [Trait("Section", "8.7.4.5.3")]
    public void AxialDiagonalRamp()
    {
        byte[] pdf = Sh(AxialDict("[2 2 18 18]", GrayRamp, "[true true]"));
        Check(pdf, Paint((x, y) => Gray3(Clamp01(Axial(x, y, 2, 2, 18, 18)))), Tol.One);
    }

    [Fact]
    [Trait("Section", "8.7.4.5.3")]
    public void AxialVerticalRampFollowsUserSpaceUp()
    {
        byte[] pdf = Sh(AxialDict("[0 0 0 20]", GrayRamp));
        Check(pdf, Paint((x, y) => Gray3(Axial(x, y, 0, 0, 0, 20))), Tol.One);
    }

    // Extend: without it nothing is painted beyond the end points; with it the end colors continue.
    [Theory]
    [InlineData("[false false]")]
    [InlineData("[true false]")]
    [InlineData("[false true]")]
    [InlineData("[true true]")]
    [Trait("Section", "8.7.4.5.3")]
    public void AxialExtend(string extend)
    {
        bool before = extend.StartsWith("[true", StringComparison.Ordinal);
        bool after = extend.EndsWith("true]", StringComparison.Ordinal);
        byte[] pdf = Sh(AxialDict("[5 0 15 0]", GrayRamp, extend));
        Check(pdf, Paint((x, y) =>
        {
            double t = Axial(x, y, 5, 0, 15, 0);
            if ((t < 0 && !before) || (t > 1 && !after))
            {
                return null;
            }

            return Gray3(Clamp01(t));
        }), Tol.One);
    }

    // Function type 2: C0 + t^N (C1 - C0). With N = 2 the ramp is quadratic.
    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [Trait("Section", "7.10.3")]
    public void ExponentialFunction(double n)
    {
        string f = $"<< /FunctionType 2 /Domain [0 1] /C0 [0 0.2 1] /C1 [1 0.8 0] /N {N(n)} >>";
        byte[] pdf = Sh(AxialDict("[0 0 20 0]", f));
        Check(pdf, Paint((x, y) =>
        {
            double t = Math.Pow(Axial(x, y, 0, 0, 20, 0), n);
            return (t, 0.2 + (0.6 * t), 1 - t);
        }), Tol.One);
    }

    // Function type 3 stitches sub-functions at /Bounds, mapping each sub-domain through /Encode (here the second one runs backwards).
    [Fact]
    [Trait("Section", "7.10.4")]
    public void StitchingFunction()
    {
        string f = "<< /FunctionType 3 /Domain [0 1] /Bounds [0.25] /Encode [0 1 1 0] /Functions [" +
                   "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 0 0] /N 1 >> " +
                   "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [1 1 0] /N 1 >>] >>";
        byte[] pdf = Sh(AxialDict("[0 0 20 0]", f));
        Check(pdf, Paint((x, y) =>
        {
            double t = Axial(x, y, 0, 0, 20, 0);
            if (t < 0.25)
            {
                double u = t / 0.25;
                return (u, 0, 0);
            }

            // Second sub-function: input mapped from [0.25, 1] to Encode [1 0], i.e. reversed.
            double v = 1 - ((t - 0.25) / 0.75);
            return (v, v, 1 - v);
        }), Tol.One);
    }

    // Function type 0 (sampled): linear interpolation between the samples (7.10.2).
    [Fact]
    [Trait("Section", "7.10.2")]
    public void SampledFunction()
    {
        // Three RGB samples at t = 0, 0.5, 1: black, red, white.
        string samples = string.Concat(SampleBytes.Select(b => (char)b));
        string fn = Stream("/FunctionType 0 /Domain [0 1] /Range [0 1 0 1 0 1] /Size [3] /BitsPerSample 8", samples);
        byte[] pdf = Page("/Sh0 sh", "/Shading << /Sh0 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 20 0] /Function 5 0 R >> >>", extra: [fn]);
        Check(pdf, Paint((x, y) =>
        {
            double t = Axial(x, y, 0, 0, 20, 0);
            return t <= 0.5 ? (t / 0.5, 0, 0) : (1, (t - 0.5) / 0.5, (t - 0.5) / 0.5);
        }), Tol.One);
    }

    // A shading may use an array of one function per component (7.10.1 / Table 78).
    [Fact]
    [Trait("Section", "8.7.4.5.3")]
    public void FunctionArrayGivesOneComponentEach()
    {
        string f = "[<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> << /FunctionType 2 /Domain [0 1] /C0 [0.5] /C1 [0.5] /N 1 >>]";
        byte[] pdf = Sh(AxialDict("[0 0 20 0]", f));
        Check(pdf, Paint((x, y) => { double t = Axial(x, y, 0, 0, 20, 0); return (t, 1 - t, 0.5); }), Tol.One);
    }

    // /Domain [t0 t1] rescales the parametric variable.
    [Fact]
    [Trait("Section", "8.7.4.5.3")]
    public void ShadingDomainRescalesT()
    {
        // Type 2 does not normalise its input: y = C0 + x^N (C1 - C0) with x in [0 10], so C1 = 0.1 makes the ramp reach 1 at t = 10.
        string f = "<< /FunctionType 2 /Domain [0 10] /C0 [0 0 0] /C1 [0.1 0.1 0.1] /N 1 >>";
        byte[] pdf = Sh(AxialDict("[0 0 20 0]", f, extra: "/Domain [0 10]"));
        Check(pdf, Paint((x, y) => Gray3(Axial(x, y, 0, 0, 20, 0))), Tol.One);
    }

    // 8.7.4.3: the sh operator honors the current clip, the BBox entry, and constant alpha; Background is ignored by sh.
    [Fact]
    [Trait("Section", "8.7.4.2")]
    public void ShHonorsTheClip()
    {
        byte[] pdf = Sh(AxialDict("[0 0 20 0]", GrayRamp), "4 6 10 8 re W n /Sh0 sh");
        Check(pdf, Paint((x, y) => Gray3(Axial(x, y, 0, 0, 20, 0)), clip: (px, py) => px is >= 4 and < 14 && (20 - py - 1) is >= 6 and < 14), Tol.One);
    }

    [Fact]
    [Trait("Section", "8.7.4.5.1")]
    public void ShHonorsTheShadingBBox()
    {
        byte[] pdf = Sh(AxialDict("[0 0 20 0]", GrayRamp, extra: "/BBox [5 5 15 15]"));
        Check(pdf, Paint((x, y) => Gray3(Axial(x, y, 0, 0, 20, 0)), clip: (px, py) => px is >= 5 and < 15 && (20 - py - 1) is >= 5 and < 15), Tol.One);
    }

    [Fact]
    [Trait("Section", "8.7.4.2")]
    public void ShIgnoresBackground()
    {
        byte[] pdf = Sh(AxialDict("[5 0 15 0]", GrayRamp, extra: "/Background [1 0 0]"));
        Check(pdf, Paint((x, y) => { double t = Axial(x, y, 5, 0, 15, 0); return t is < 0 or > 1 ? null : Gray3(t); }), Tol.One);
    }

    [Fact]
    [Trait("Section", "11.6.4.4")]
    public void ShHonorsConstantAlpha()
    {
        byte[] pdf = Sh(AxialDict("[0 0 20 0]", GrayRamp), "/G gs /Sh0 sh", pageExtra: "/ExtGState << /G << /ca 0.5 >> >>");
        Check(pdf, Paint((x, y) => Gray3(Axial(x, y, 0, 0, 20, 0)), alpha: 0.5), Tol.One);
    }

    // The CTM at the time of sh positions the shading's coordinate system (8.7.4.2).
    [Fact]
    [Trait("Section", "8.7.4.2")]
    public void ShUsesTheCtm()
    {
        byte[] pdf = Sh(AxialDict("[0 0 10 0]", GrayRamp), "2 0 0 1 0 0 cm /Sh0 sh");
        Check(pdf, Paint((x, y) => Gray3(Axial(x / 2, y, 0, 0, 10, 0))), Tol.One);
    }

    // ---- radial (8.7.4.5.4) ----

    private static double? RadialT(double px, double py, double x0, double y0, double r0, double x1, double y1, double r1, bool ext0, bool ext1)
    {
        double dx = x1 - x0, dy = y1 - y0, dr = r1 - r0;
        double pdx = px - x0, pdy = py - y0;
        double a = (dx * dx) + (dy * dy) - (dr * dr);
        double b = (pdx * dx) + (pdy * dy) + (r0 * dr);
        double c = (pdx * pdx) + (pdy * pdy) - (r0 * r0);
        var roots = new List<double>();
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12)
            {
                roots.Add(c / (2 * b));
            }
        }
        else
        {
            double disc = (b * b) - (a * c);
            if (disc < 0)
            {
                return null;
            }

            double sq = Math.Sqrt(disc);
            roots.Add((b + sq) / a);
            roots.Add((b - sq) / a);
        }

        // The largest s whose circle has non-negative radius and lies in the (possibly extended) range wins.
        foreach (double s in roots.OrderByDescending(r => r))
        {
            if (r0 + (s * dr) < 0)
            {
                continue;
            }

            if ((s < 0 && !ext0) || (s > 1 && !ext1))
            {
                continue;
            }

            return s;
        }

        return null;
    }

    private static string RadialDict(string coords, string extend, string function = GrayRamp) =>
        $"<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords {coords} /Function {function} /Extend {extend} >>";

    [Fact]
    [Trait("Section", "8.7.4.5.4")]
    public void RadialConcentricCircles()
    {
        byte[] pdf = Sh(RadialDict("[10 10 0 10 10 8]", "[false false]"));
        Check(pdf, Paint((x, y) => RadialT(x, y, 10, 10, 0, 10, 10, 8, false, false) is { } s ? Gray3(Clamp01(s)) : null), Tol.One);
    }

    [Theory]
    [InlineData("[true true]")]
    [InlineData("[false true]")]
    [Trait("Section", "8.7.4.5.4")]
    public void RadialExtendBeyondTheOuterCircle(string extend)
    {
        bool e0 = extend.StartsWith("[true", StringComparison.Ordinal);
        byte[] pdf = Sh(RadialDict("[10 10 2 10 10 6]", extend));
        Check(pdf, Paint((x, y) => RadialT(x, y, 10, 10, 2, 10, 10, 6, e0, true) is { } s ? Gray3(Clamp01(s)) : null), Tol.One);
    }

    // Two circles that don't share a center: a cone. Points outside the cone are not painted.
    [Fact]
    [Trait("Section", "8.7.4.5.4")]
    public void RadialConeBetweenOffsetCircles()
    {
        byte[] pdf = Sh(RadialDict("[5 10 1 14 10 5]", "[false false]"));
        Check(pdf, Paint((x, y) => RadialT(x, y, 5, 10, 1, 14, 10, 5, false, false) is { } s ? Gray3(Clamp01(s)) : null), Tol.One);
    }

    [Fact]
    [Trait("Section", "8.7.4.5.4")]
    public void RadialConeExtended()
    {
        byte[] pdf = Sh(RadialDict("[5 10 1 14 10 5]", "[true true]"));
        Check(pdf, Paint((x, y) => RadialT(x, y, 5, 10, 1, 14, 10, 5, true, true) is { } s ? Gray3(Clamp01(s)) : null), Tol.One);
    }

    // ---- shading patterns (8.7.4.3) ----

    private static byte[] PatternFill(string shading, string matrix, string content) =>
        Page(content, $"/Pattern << /P0 << /PatternType 2 /Shading {shading} /Matrix {matrix} >> >>");

    // A shading pattern's matrix maps pattern space to the default coordinate space of the page, not to the CTM at the time of painting.
    [Fact]
    [Trait("Section", "8.7.2")]
    public void PatternMatrixIsRelativeToDefaultSpace()
    {
        byte[] pdf = PatternFill(AxialDict("[0 0 10 0]", GrayRamp), "[2 0 0 1 0 0]", "/Pattern cs /P0 scn 0 0 20 20 re f");
        Check(pdf, Paint((x, y) => Gray3(Axial(x / 2, y, 0, 0, 10, 0))), Tol.One);
    }

    [Fact]
    [Trait("Section", "8.7.2")]
    public void PatternIgnoresTheCtmAtPaintTime()
    {
        byte[] pdf = PatternFill(AxialDict("[0 0 20 0]", GrayRamp), "[1 0 0 1 0 0]", "q 0.5 0 0 0.5 3 3 cm /Pattern cs /P0 scn 0 0 30 30 re f Q");
        // The rectangle is (3,3)-(18,18) in default space; the ramp stays in default space.
        Check(pdf, Paint((x, y) => Gray3(Axial(x, y, 0, 0, 20, 0)), clip: (px, py) => px is >= 3 and < 18 && (20 - py - 1) is >= 3 and < 18), Tol.One);
    }

    // For a shading pattern, /Background fills the painted area outside the shading's extent (8.7.4.3 Table 76 Background).
    [Fact]
    [Trait("Section", "8.7.4.3")]
    public void PatternBackgroundFillsBeyondTheShading()
    {
        string sh = AxialDict("[5 0 15 0]", GrayRamp, extra: "/Background [1 0 0]");
        byte[] pdf = PatternFill(sh, "[1 0 0 1 0 0]", "/Pattern cs /P0 scn 0 0 20 20 re f");
        Check(pdf, Paint((x, y) => { double t = Axial(x, y, 5, 0, 15, 0); return t is < 0 or > 1 ? (1, 0, 0) : Gray3(t); }), Tol.One);
    }

    [Fact]
    [Trait("Section", "8.7.4.1")]
    public void PatternFillOnlyPaintsInsideThePath()
    {
        byte[] pdf = PatternFill(AxialDict("[0 0 20 0]", GrayRamp), "[1 0 0 1 0 0]", "/Pattern cs /P0 scn 4 4 8 8 re f");
        Check(pdf, Paint((x, y) => Gray3(Axial(x, y, 0, 0, 20, 0)), clip: (px, py) => px is >= 4 and < 12 && (20 - py - 1) is >= 4 and < 12), Tol.One);
    }

    // Stroking with a shading pattern uses the stroke color's pattern.
    [Fact]
    [Trait("Section", "8.7.4.1")]
    public void PatternAsStrokeColor()
    {
        byte[] pdf = PatternFill(AxialDict("[0 0 20 0]", GrayRamp), "[1 0 0 1 0 0]", "/Pattern CS /P0 SCN 4 w 0 10 m 20 10 l S");
        Check(pdf, Paint((x, y) => Gray3(Axial(x, y, 0, 0, 20, 0)), clip: (px, py) => (20 - py - 1) is >= 8 and < 12), Tol.One);
    }
}

/// <summary>Tiling patterns (ISO 32000-2 8.7.3).</summary>
public class TilingPatternTests
{
    private static byte[] Tiling(string dict, string patternContent, string content, string cs = "")
    {
        string pat = Stream($"/PatternType 1 {dict}", patternContent);
        return Page(content, "/Pattern << /P0 5 0 R >> " + cs, extra: [pat]);
    }

    private static Expected Cells(int step, Func<int, int, (double R, double G, double B)?> cell, int dx = 0, int dy = 0)
    {
        var e = new Expected(20, 20);
        for (int py = 0; py < 20; py++)
        {
            for (int px = 0; px < 20; px++)
            {
                int ux = (((px - dx) % step) + step) % step;
                int uy = (((20 - 1 - py - dy) % step) + step) % step;
                if (cell(ux, uy) is { } c)
                {
                    e.Over(px, py, c, 1);
                }
            }
        }

        return e;
    }

    private static (double, double, double)? RedBlue(int ux, int uy) =>
        ux < 2 && uy < 2 ? Spec.Rgb(1, 0, 0) : (ux >= 2 && uy >= 2 ? Spec.Rgb(0, 0, 1) : null);

    private const string TwoColorCell = "1 0 0 rg 0 0 2 2 re f 0 0 1 rg 2 2 2 2 re f";

    // 8.7.3.2: a 4 x 4 cell repeats from the pattern origin (the default-space origin when Matrix is the identity).
    [Fact]
    [Trait("Section", "8.7.3.2")]
    public void ColoredPatternRepeats()
    {
        byte[] pdf = Tiling("/PaintType 1 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>", TwoColorCell, "/Pattern cs /P0 scn 0 0 20 20 re f");
        Check(pdf, Cells(4, RedBlue), Tol.Exact);
    }

    // Pattern matrix translation moves the lattice.
    [Fact]
    [Trait("Section", "8.7.3.1")]
    public void PatternMatrixTranslatesTheLattice()
    {
        byte[] pdf = Tiling("/PaintType 1 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Matrix [1 0 0 1 1 3] /Resources << >>", TwoColorCell, "/Pattern cs /P0 scn 0 0 20 20 re f");
        Check(pdf, Cells(4, RedBlue, dx: 1, dy: 3), Tol.Exact);
    }

    // Pattern matrix scale enlarges the cell: 8 x 8 with 4 x 4 quadrants.
    [Fact]
    [Trait("Section", "8.7.3.1")]
    public void PatternMatrixScalesTheCell()
    {
        byte[] pdf = Tiling("/PaintType 1 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Matrix [2 0 0 2 0 0] /Resources << >>", TwoColorCell, "/Pattern cs /P0 scn 0 0 20 20 re f");
        Check(pdf, Cells(8, (ux, uy) => RedBlue(ux / 2, uy / 2)), Tol.Exact);
    }

    // 8.7.3.1: the pattern is positioned in default space and ignores the CTM at paint time.
    [Fact]
    [Trait("Section", "8.7.3.1")]
    public void PatternIgnoresTheCtm()
    {
        byte[] pdf = Tiling("/PaintType 1 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>", TwoColorCell, "q 3 0 0 3 0 0 cm /Pattern cs /P0 scn 0 0 6 6 re f Q");
        var plain = Cells(4, RedBlue);
        var e = new Expected(20, 20);
        for (int py = 0; py < 20; py++)
        {
            for (int px = 0; px < 20; px++)
            {
                if (px < 18 && (20 - py - 1) < 18)
                {
                    e[px, py] = plain[px, py];
                }
            }
        }

        Check(pdf, e, Tol.Exact);
    }

    // XStep/YStep larger than the BBox leave gaps between cells (the gaps are unpainted).
    [Fact]
    [Trait("Section", "8.7.3.2")]
    public void StepLargerThanBBoxLeavesGaps()
    {
        byte[] pdf = Tiling("/PaintType 1 /TilingType 1 /BBox [0 0 2 2] /XStep 5 /YStep 5 /Resources << >>", "0 0.5 0 rg 0 0 2 2 re f", "/Pattern cs /P0 scn 0 0 20 20 re f");
        Check(pdf, Cells(5, (ux, uy) => ux < 2 && uy < 2 ? Spec.Rgb(0, 0.5, 0) : null), Tol.One);
    }

    // The BBox clips the cell content: parts of the content outside it are not painted.
    [Fact]
    [Trait("Section", "8.7.3.2")]
    public void BBoxClipsTheCell()
    {
        byte[] pdf = Tiling("/PaintType 1 /TilingType 1 /BBox [0 0 2 2] /XStep 4 /YStep 4 /Resources << >>", "0 0 1 rg -4 -4 12 12 re f", "/Pattern cs /P0 scn 0 0 20 20 re f");
        Check(pdf, Cells(4, (ux, uy) => ux < 2 && uy < 2 ? Spec.Rgb(0, 0, 1) : null), Tol.Exact);
    }

    // 8.7.3.3 uncolored (PaintType 2): the cell is a mask painted in the color given with scn after the pattern's color space; color operators inside are ignored.
    [Fact]
    [Trait("Section", "8.7.3.3")]
    public void UncoloredPatternUsesTheSuppliedColor()
    {
        byte[] pdf = Tiling("/PaintType 2 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>", "1 0 0 rg 0 0 2 2 re f",
            "/CS1 cs 0 0.6 0 /P0 scn 0 0 20 20 re f", cs: "/ColorSpace << /CS1 [/Pattern /DeviceRGB] >>");
        Check(pdf, Cells(4, (ux, uy) => ux < 2 && uy < 2 ? Spec.Rgb(0, 0.6, 0) : null), Tol.Exact);
    }

    // Stroking with a tiling pattern.
    [Fact]
    [Trait("Section", "8.7.3.1")]
    public void TilingPatternAsStrokeColor()
    {
        byte[] pdf = Tiling("/PaintType 1 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>", TwoColorCell, "/Pattern CS /P0 SCN 8 w 0 10 m 20 10 l S");
        var plain = Cells(4, RedBlue);
        var e = new Expected(20, 20);
        for (int py = 0; py < 20; py++)
        {
            for (int px = 0; px < 20; px++)
            {
                int yUp = 20 - py - 1;
                if (yUp is >= 6 and < 14)
                {
                    e[px, py] = plain[px, py];
                }
            }
        }

        Check(pdf, e, Tol.Exact);
    }
}
