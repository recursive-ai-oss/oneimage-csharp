using static OneImage.Pdf.Tests.SpecTruth.Spec;

namespace OneImage.Pdf.Tests.SpecTruth;

/// <summary>Constant alpha, blend modes and soft masks on flat colors (ISO 32000-2 11.3, 11.6.4, 11.6.5).</summary>
public class TransparencyTests
{
    private static string Gs(params string[] entries) =>
        "/ExtGState << " + string.Join(' ', entries.Select((e, i) => $"/GS{i} << {e} >>")) + " >>";

    // 11.3.7.2 / 11.7.4: source-over with constant shape/opacity: result = (1 - a) * backdrop + a * source.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    [Trait("Section", "11.6.4.4")]
    public void ConstantFillAlpha(double a)
    {
        byte[] pdf = Page("/GS0 gs 1 0 0 rg 5 5 10 10 re f", Gs($"/ca {N(a)}"));
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Rgb(1, 0, 0), a), Tol.One);
    }

    // The alpha constant applies over a colored backdrop, not just white.
    [Fact]
    [Trait("Section", "11.6.4.4")]
    public void FillAlphaOverColoredBackdrop()
    {
        byte[] pdf = Page("0 0.4 0.8 rg 0 0 20 20 re f /GS0 gs 1 0.6 0 rg 5 5 10 10 re f", Gs("/ca 0.4"));
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Rgb(0, 0.4, 0.8)).Rect(5, 5, 15, 15, Rgb(1, 0.6, 0), 0.4), Tol.One);
    }

    // 11.6.4.4: ca is the non-stroking constant and CA the stroking one; a fill is not affected by CA.
    [Fact]
    [Trait("Section", "11.6.4.4")]
    public void StrokeAlphaDoesNotAffectFill()
    {
        byte[] pdf = Page("/GS0 gs 0 g 0 0 8 8 re f 4 w 0 14 m 20 14 l S", Gs("/CA 0.5"));
        Check(pdf, new Expected(20, 20).Rect(0, 0, 8, 8, Gray(0)).Rect(0, 12, 20, 16, Gray(0), 0.5), Tol.One);
    }

    [Fact]
    [Trait("Section", "11.6.4.4")]
    public void FillAlphaDoesNotAffectStroke()
    {
        byte[] pdf = Page("/GS0 gs 0 g 0 0 8 8 re f 4 w 0 14 m 20 14 l S", Gs("/ca 0.5"));
        Check(pdf, new Expected(20, 20).Rect(0, 0, 8, 8, Gray(0), 0.5).Rect(0, 12, 20, 16, Gray(0)), Tol.One);
    }

    // Two overlapping translucent fills accumulate: the overlap is (1-a)^2 backdrop + ...
    [Fact]
    [Trait("Section", "11.3.7.2")]
    public void OverlappingTranslucentFillsAccumulate()
    {
        byte[] pdf = Page("/GS0 gs 1 0 0 rg 2 2 10 10 re f 0 0 1 rg 8 8 10 10 re f", Gs("/ca 0.5"));
        Check(pdf, new Expected(20, 20).Rect(2, 2, 12, 12, Rgb(1, 0, 0), 0.5).Rect(8, 8, 18, 18, Rgb(0, 0, 1), 0.5), Tol.One);
    }

    // 8.4.5/11.6.4: the alpha is part of the graphics state, so Q restores it.
    [Fact]
    [Trait("Section", "8.4.5")]
    public void AlphaRestoredByQ()
    {
        byte[] pdf = Page("q /GS0 gs Q 1 0 0 rg 5 5 10 10 re f", Gs("/ca 0.5"));
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Rgb(1, 0, 0)), Tol.Exact);
    }

    // 8.4.5 Table 58: the later gs overrides ca only where it names it; other parameters keep their value.
    [Fact]
    [Trait("Section", "8.4.5")]
    public void GsKeepsUnnamedParameters()
    {
        byte[] pdf = Page("/GS0 gs /GS1 gs 1 0 0 rg 5 5 10 10 re f", Gs("/ca 0.5", "/CA 0.2"));
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Rgb(1, 0, 0), 0.5), Tol.One);
    }

    // Alpha of 0 paints nothing at all.
    [Fact]
    [Trait("Section", "11.6.4.4")]
    public void ZeroAlphaPaintsNothing() => Check(Page("/GS0 gs 0 g 0 0 20 20 re f", Gs("/ca 0")), new Expected(20, 20), Tol.Exact);

    // Colors for blend tests: an opaque backdrop everywhere and a source in the middle; both whole levels.
    private static readonly (double R, double G, double B) Backdrop = Rgb(0.8, 0.4, 0.2);
    private static readonly (double R, double G, double B) Source = Rgb(0.2, 0.6, 0.8);

    public static IEnumerable<object[]> SeparableModes() => BlendMath.Separable.Select(m => new object[] { m });

    public static IEnumerable<object[]> NonSeparableModes() => BlendMath.NonSeparable.Select(m => new object[] { m });

    private static (double, double, double) Unit((double R, double G, double B) c) => (c.R / 255, c.G / 255, c.B / 255);

    private static (double R, double G, double B) Mix(string mode, double alpha)
    {
        var cb = Unit(Backdrop);
        var cs = Unit(Source);
        var b = BlendMath.Blend(mode, cb, cs);
        return (255 * (((1 - alpha) * cb.Item1) + (alpha * b.R)), 255 * (((1 - alpha) * cb.Item2) + (alpha * b.G)), 255 * (((1 - alpha) * cb.Item3) + (alpha * b.B)));
    }

    private static void BlendCase(string mode, double alpha)
    {
        // 11.3.5: with an opaque backdrop and an opaque source, Cr = (1 - alpha_s) * Cb + alpha_s * B(Cb, Cs).
        string alphaEntry = alpha < 1 ? $" /ca {N(alpha)}" : "";
        byte[] pdf = Page("0.8 0.4 0.2 rg 0 0 20 20 re f /GS0 gs 0.2 0.6 0.8 rg 5 5 10 10 re f", Gs($"/BM /{mode}{alphaEntry}"));
        var e = new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop);
        e.Rect(5, 5, 15, 15, Mix(mode, alpha), 1);
        Check(pdf, e, Tol.One);
    }

    [Theory]
    [MemberData(nameof(SeparableModes))]
    [Trait("Section", "11.3.5.2")]
    public void SeparableBlendMode(string mode) => BlendCase(mode, 1);

    [Theory]
    [MemberData(nameof(NonSeparableModes))]
    [Trait("Section", "11.3.5.3")]
    public void NonSeparableBlendMode(string mode) => BlendCase(mode, 1);

    [Theory]
    [InlineData("Multiply")]
    [InlineData("Screen")]
    [InlineData("Difference")]
    [InlineData("Luminosity")]
    [Trait("Section", "11.3.6")]
    public void BlendModeWithConstantAlpha(string mode) => BlendCase(mode, 0.5);

    // 11.6.3 Table 58 /BM: an array lists modes in order of preference; the first one the reader knows is used.
    [Fact]
    [Trait("Section", "11.6.3")]
    public void BlendModeArrayUsesFirstKnown()
    {
        byte[] pdf = Page("0.8 0.4 0.2 rg 0 0 20 20 re f /GS0 gs 0.2 0.6 0.8 rg 5 5 10 10 re f", Gs("/BM [/NoSuchMode /Multiply]"));
        var e = new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(5, 5, 15, 15, Mix("Multiply", 1));
        Check(pdf, e, Tol.One);
    }

    // 11.6.3: /Compatible is the same as Normal.
    [Fact]
    [Trait("Section", "11.6.3")]
    public void CompatibleIsNormal()
    {
        byte[] pdf = Page("0.8 0.4 0.2 rg 0 0 20 20 re f /GS0 gs 0.2 0.6 0.8 rg 5 5 10 10 re f", Gs("/BM /Compatible"));
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(5, 5, 15, 15, Source), Tol.Exact);
    }

    // 11.3.5: blend modes apply to strokes as well as fills.
    [Fact]
    [Trait("Section", "11.3.5")]
    public void BlendModeAppliesToStrokes()
    {
        byte[] pdf = Page("0.8 0.4 0.2 rg 0 0 20 20 re f /GS0 gs 0.2 0.6 0.8 RG 4 w 0 10 m 20 10 l S", Gs("/BM /Multiply"));
        var e = new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(0, 8, 20, 12, Mix("Multiply", 1));
        Check(pdf, e, Tol.One);
    }

    // ---- soft masks (11.6.5) ----

    private const string MaskForm = "/Type /XObject /Subtype /Form /BBox [0 0 20 20]";

    private static byte[] Masked(string maskSpec, string maskCs, string maskContent, string content = "/GS0 gs 1 0 0 rg 0 0 20 20 re f")
    {
        string f = Stream($"{MaskForm} /Group << /S /Transparency /CS /{maskCs} >>", maskContent);
        return Page(content, $"/ExtGState << /GS0 << /SMask << /Type /Mask {maskSpec} /G 5 0 R >> >> >>", extra: [f]);
    }

    // 11.6.5.2 Luminosity: the mask value is the luminosity of the group composited over its backdrop color (black by default).
    [Fact]
    [Trait("Section", "11.6.5.2")]
    public void LuminosityMaskOverBlackBackdrop()
    {
        byte[] pdf = Masked("/S /Luminosity", "DeviceGray", "0.5 g 5 5 10 10 re f");
        var e = new Expected(20, 20).Rect(5, 5, 15, 15, Rgb(1, 0, 0), 0.5);
        Check(pdf, e, Tol.One);
    }

    // /BC gives the backdrop color: with white, the area outside the group is fully visible.
    [Fact]
    [Trait("Section", "11.6.5.2")]
    public void LuminosityMaskBackdropColor()
    {
        byte[] pdf = Masked("/S /Luminosity /BC [1]", "DeviceGray", "0.5 g 5 5 10 10 re f");
        // Outside: mask 1 (red). Inside: mask 0.5, over white.
        var exp = new Expected(20, 20).Rect(0, 0, 20, 20, Rgb(1, 0, 0)).Rect(5, 5, 15, 15, Gray(1)).Rect(5, 5, 15, 15, Rgb(1, 0, 0), 0.5);
        Check(pdf, exp, Tol.One);
    }

    // 11.6.5.2: a colored group is converted to luminosity (0.30 R + 0.59 G + 0.11 B); the specification gives the
    // weights only through the DeviceRGB to DeviceGray conversion, so allow 2 levels.
    [Fact]
    [Trait("Section", "11.6.5.2")]
    public void LuminosityMaskOfAColoredGroup()
    {
        byte[] pdf = Masked("/S /Luminosity", "DeviceRGB", "0.2 0.4 0.8 rg 5 5 10 10 re f");
        double m = (0.3 * 0.2) + (0.59 * 0.4) + (0.11 * 0.8);
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Rgb(1, 0, 0), m), 2);
    }

    // 11.6.5.2 Alpha: the mask is the group's opacity; its colors don't matter.
    [Fact]
    [Trait("Section", "11.6.5.2")]
    public void AlphaMaskIsGroupOpacity()
    {
        byte[] pdf = Masked("/S /Alpha", "DeviceGray", "0 g 5 5 10 10 re f");
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Rgb(1, 0, 0)), Tol.Exact);
    }

    [Fact]
    [Trait("Section", "11.6.5.2")]
    public void AlphaMaskWithTranslucentGroupContent()
    {
        // The mask form carries its own ExtGState for ca.
        string f = Stream($"{MaskForm} /Group << /S /Transparency /CS /DeviceGray >> /Resources << /ExtGState << /GS0 << /ca 0.4 >> >> >>", "/GS0 gs 1 g 5 5 10 10 re f");
        byte[] pdf = Page("/GS0 gs 1 0 0 rg 0 0 20 20 re f", "/ExtGState << /GS0 << /SMask << /Type /Mask /S /Alpha /G 5 0 R >> >> >>", extra: [f]);
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Rgb(1, 0, 0), 0.4), Tol.One);
    }

    // The mask multiplies with constant alpha: effective opacity is ca * mask.
    [Fact]
    [Trait("Section", "11.6.4.4")]
    public void SoftMaskMultipliesWithConstantAlpha()
    {
        string f = Stream($"{MaskForm} /Group << /S /Transparency /CS /DeviceGray >>", "0.5 g 5 5 10 10 re f");
        byte[] pdf = Page("/GS0 gs 1 0 0 rg 0 0 20 20 re f", "/ExtGState << /GS0 << /ca 0.5 /SMask << /Type /Mask /S /Luminosity /G 5 0 R >> >> >>", extra: [f]);
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Rgb(1, 0, 0), 0.25), Tol.One);
    }

    // 11.6.5.1: /SMask /None turns the mask off again.
    [Fact]
    [Trait("Section", "11.6.5.1")]
    public void SoftMaskNoneResets()
    {
        string f = Stream($"{MaskForm} /Group << /S /Transparency /CS /DeviceGray >>", "0.5 g 5 5 10 10 re f");
        byte[] pdf = Page("/GS0 gs /GS1 gs 1 0 0 rg 0 0 20 20 re f", "/ExtGState << /GS0 << /SMask << /Type /Mask /S /Luminosity /G 5 0 R >> >> /GS1 << /SMask /None >> >>", extra: [f]);
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Rgb(1, 0, 0)), Tol.Exact);
    }

    // 8.4.2: Q restores the soft mask.
    [Fact]
    [Trait("Section", "11.6.5.1")]
    public void SoftMaskRestoredByQ()
    {
        byte[] pdf = Masked("/S /Luminosity", "DeviceGray", "0.5 g 5 5 10 10 re f", content: "q /GS0 gs Q 1 0 0 rg 0 0 20 20 re f");
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Rgb(1, 0, 0)), Tol.Exact);
    }

    // 11.6.5.2: the mask's coordinate system is the CTM in effect when gs is executed, not when the object is painted.
    [Fact]
    [Trait("Section", "11.6.5.2")]
    public void SoftMaskUsesCtmAtTheTimeOfGs()
    {
        byte[] pdf = Masked("/S /Luminosity", "DeviceGray", "0.5 g 5 5 10 10 re f", content: "q 1 0 0 1 8 0 cm /GS0 gs 1 0 0 1 -8 0 cm 1 0 0 rg 0 0 20 20 re f Q");
        Check(pdf, new Expected(20, 20).Rect(13, 5, 20, 15, Rgb(1, 0, 0), 0.5), Tol.One);
    }

    // 11.6.5.2 /TR: a transfer function applied to the mask values (here inverting). Luminosity 0.25 becomes 0.75.
    [Fact(Skip = "Known gap: soft-mask /TR is treated as identity (docs/pdf.md, Known approximations)")]
    [Trait("Section", "11.6.5.2")]
    public void SoftMaskTransferFunction()
    {
        string f = Stream($"{MaskForm} /Group << /S /Transparency /CS /DeviceGray >>", "0.2 g 5 5 10 10 re f");
        byte[] pdf = Page("/GS0 gs 1 0 0 rg 0 0 20 20 re f",
            "/ExtGState << /GS0 << /SMask << /Type /Mask /S /Luminosity /G 5 0 R /TR << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> >> >> >>",
            extra: [f]);
        // Outside the group the backdrop luminosity 0 maps through TR to 1: red everywhere; inside 0.2 maps to 0.8.
        var e = new Expected(20, 20).Rect(0, 0, 20, 20, Rgb(1, 0, 0)).Rect(5, 5, 15, 15, Gray(1)).Rect(5, 5, 15, 15, Rgb(1, 0, 0), 0.8);
        Check(pdf, e, Tol.One);
    }
}

/// <summary>Transparency groups, form XObjects and annotations (ISO 32000-2 8.10, 11.4, 11.6.6, 12.5.5).</summary>
public class GroupTests
{
    private const string GroupHead = "/Type /XObject /Subtype /Form /BBox [0 0 20 20]";
    private static readonly (double R, double G, double B) Backdrop = Rgb(0.8, 0.4, 0.2);
    private static readonly (double R, double G, double B) Source = Rgb(0.2, 0.6, 0.6);

    private static (double R, double G, double B) MultiplyOf((double R, double G, double B) cb, (double R, double G, double B) cs) =>
        (cb.R * cs.R / 255, cb.G * cs.G / 255, cb.B * cs.B / 255);

    private static byte[] WithGroup(string groupDict, string formContent, string pageContent, string pageGs = "")
    {
        string form = Stream($"{GroupHead} /Group << /S /Transparency {groupDict} >> /Resources << /ExtGState << /M << /BM /Multiply >> /H << /ca 0.5 >> >> >>", formContent);
        return Page("0.8 0.4 0.2 rg 0 0 20 20 re f " + pageContent, $"/XObject << /F0 5 0 R >> /ExtGState << /G << {pageGs} >> >>", extra: [form]);
    }

    // 11.4.5/11.4.6 isolated group: its contents blend against a transparent backdrop, so Multiply inside it has nothing to multiply with.
    [Fact]
    [Trait("Section", "11.4.5")]
    public void IsolatedGroupDoesNotSeeTheBackdrop()
    {
        byte[] pdf = WithGroup("/I true", "/M gs 0.2 0.6 0.6 rg 5 5 10 10 re f", "/F0 Do");
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(5, 5, 15, 15, Source), Tol.One);
    }

    // Non-isolated group: its initial backdrop is the parent's, so Multiply inside blends with the page.
    [Fact]
    [Trait("Section", "11.4.5")]
    public void NonIsolatedGroupBlendsWithTheBackdrop()
    {
        byte[] pdf = WithGroup("/I false", "/M gs 0.2 0.6 0.6 rg 5 5 10 10 re f", "/F0 Do");
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(5, 5, 15, 15, MultiplyOf(Backdrop, Source)), Tol.One);
    }

    // Group opacity 0.5 over a non-isolated group: (1-a) Cb + a B(Cb, Cs), because the group result still contains the backdrop's influence.
    [Fact]
    [Trait("Section", "11.6.6")]
    public void NonIsolatedGroupWithGroupOpacity()
    {
        byte[] pdf = WithGroup("/I false", "/M gs 0.2 0.6 0.6 rg 5 5 10 10 re f", "/G gs /F0 Do", pageGs: "/ca 0.5");
        var mix = MultiplyOf(Backdrop, Source);
        var e = new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(5, 5, 15, 15, mix, 0.5);
        Check(pdf, e, Tol.One);
    }

    [Fact]
    [Trait("Section", "11.6.6")]
    public void IsolatedGroupWithGroupOpacity()
    {
        byte[] pdf = WithGroup("/I true", "/M gs 0.2 0.6 0.6 rg 5 5 10 10 re f", "/G gs /F0 Do", pageGs: "/ca 0.5");
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(5, 5, 15, 15, Source, 0.5), Tol.One);
    }

    // 11.6.6: the group is composited with the blend mode current when it is painted.
    [Fact]
    [Trait("Section", "11.6.6")]
    public void GroupIsCompositedWithTheCurrentBlendMode()
    {
        byte[] pdf = WithGroup("/I true", "0.2 0.6 0.6 rg 5 5 10 10 re f", "/G gs /F0 Do", pageGs: "/BM /Multiply");
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(5, 5, 15, 15, MultiplyOf(Backdrop, Source)), Tol.One);
    }

    // Knockout (11.4.6): each object composites against the group's initial backdrop, so the later one replaces the earlier in the overlap.
    [Theory]
    [InlineData("/I false /K true")]
    [InlineData("/I true /K true")]
    [Trait("Section", "11.4.6")]
    public void KnockoutGroup(string dict)
    {
        byte[] pdf = WithGroup(dict, "/H gs 1 0 0 rg 2 2 10 10 re f 0 0 1 rg 8 8 10 10 re f", "/F0 Do");
        var red = Rgb(1, 0, 0);
        var blue = Rgb(0, 0, 1);
        var e = new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(2, 2, 12, 12, red, 0.5).Rect(8, 8, 18, 18, blue, 0.5);
        // In the overlap the red contribution is knocked out: Cb blended once with blue.
        e.Rect(8, 8, 12, 12, Backdrop, 1).Rect(8, 8, 12, 12, blue, 0.5);
        Check(pdf, e, Tol.One);
    }

    // Control: the same content in a non-knockout group accumulates in the overlap.
    [Fact]
    [Trait("Section", "11.4.6")]
    public void NonKnockoutGroupAccumulates()
    {
        byte[] pdf = WithGroup("/I false /K false", "/H gs 1 0 0 rg 2 2 10 10 re f 0 0 1 rg 8 8 10 10 re f", "/F0 Do");
        var e = new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(2, 2, 12, 12, Rgb(1, 0, 0), 0.5).Rect(8, 8, 18, 18, Rgb(0, 0, 1), 0.5);
        Check(pdf, e, Tol.One);
    }

    // 11.6.6: when a group is painted, its contents start with alpha 1 and Normal blending: the page's ca applies once to the whole group,
    // so in the overlap of two opaque objects only the top one shows (at half opacity).
    [Fact]
    [Trait("Section", "11.6.6")]
    public void GroupOpacityAppliesToTheWholeGroup()
    {
        byte[] pdf = WithGroup("/I true", "1 0 0 rg 2 2 10 10 re f 0 0 1 rg 8 8 10 10 re f", "/G gs /F0 Do", pageGs: "/ca 0.5");
        var e = new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(2, 2, 12, 12, Rgb(1, 0, 0), 0.5).Rect(8, 8, 18, 18, Rgb(0, 0, 1), 0.5);
        e.Rect(8, 8, 12, 12, Backdrop, 1).Rect(8, 8, 12, 12, Rgb(0, 0, 1), 0.5);
        Check(pdf, e, Tol.One);
    }

    // 11.6.6: a form XObject without a /Group is not a group; the alpha applies to each of its objects, which then accumulate.
    [Fact]
    [Trait("Section", "11.6.6")]
    public void PlainFormInheritsAlphaPerObject()
    {
        string form = Stream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "1 0 0 rg 2 2 10 10 re f 0 0 1 rg 8 8 10 10 re f");
        byte[] pdf = Page("0.8 0.4 0.2 rg 0 0 20 20 re f /GS gs /F0 Do", "/XObject << /F0 5 0 R >> /ExtGState << /GS << /ca 0.5 >> >>", extra: [form]);
        var e = new Expected(20, 20).Rect(0, 0, 20, 20, Backdrop).Rect(2, 2, 12, 12, Rgb(1, 0, 0), 0.5).Rect(8, 8, 18, 18, Rgb(0, 0, 1), 0.5);
        Check(pdf, e, Tol.One);
    }

    // 8.10.1: a form's BBox clips its contents, in form space (after /Matrix).
    [Fact]
    [Trait("Section", "8.10.1")]
    public void FormBBoxClips()
    {
        string form = Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 8] /Matrix [1 0 0 1 4 6]", "0 g -5 -5 40 40 re f");
        byte[] pdf = Page("/F0 Do", "/XObject << /F0 5 0 R >>", extra: [form]);
        Check(pdf, new Expected(20, 20).Rect(4, 6, 14, 14, Gray(0)), Tol.Exact);
    }

    // 8.10.1: the form matrix is applied before the CTM that is current at Do.
    [Fact]
    [Trait("Section", "8.10.1")]
    public void FormMatrixConcatenatesWithCtm()
    {
        string form = Stream("/Type /XObject /Subtype /Form /BBox [0 0 4 4] /Matrix [2 0 0 2 0 0]", "0 g 0 0 4 4 re f");
        byte[] pdf = Page("1 0 0 1 3 5 cm /F0 Do", "/XObject << /F0 5 0 R >>", extra: [form]);
        Check(pdf, new Expected(20, 20).Rect(3, 5, 11, 13, Gray(0)), Tol.Exact);
    }

    // 8.10.1: graphics state changes inside a form don't leak out (the form is bracketed by q/Q).
    [Fact]
    [Trait("Section", "8.10.1")]
    public void FormStateDoesNotLeak()
    {
        string form = Stream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "1 0 0 rg 3 w 2 0 0 2 0 0 cm 1 1 2 2 re f");
        byte[] pdf = Page("/F0 Do 10 10 4 4 re f", "/XObject << /F0 5 0 R >>", extra: [form]);
        Check(pdf, new Expected(20, 20).Rect(2, 2, 6, 6, Rgb(1, 0, 0)).Rect(10, 10, 14, 14, Gray(0)), Tol.Exact);
    }

    // ---- annotations (12.5.5) ----

    private static byte[] Annot(string annotDict, string apDict, string apContent)
    {
        string ap = Stream($"/Type /XObject /Subtype /Form {apDict}", apContent);
        return Doc(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents 4 0 R /Resources << >> /Annots [5 0 R] >>",
            Stream("", "0.8 g 0 0 20 20 re f"),
            $"<< /Type /Annot /Subtype /Stamp {annotDict} /AP << /N 6 0 R >> >>",
            ap);
    }

    // 12.5.5 algorithm 8.1: the appearance's transformed BBox is scaled and translated to fill /Rect.
    [Fact]
    [Trait("Section", "12.5.5")]
    public void AppearanceFillsRect()
    {
        byte[] pdf = Annot("/Rect [4 4 14 12] /F 4", "/BBox [0 0 5 4]", "1 0 0 rg 0 0 5 4 re f 0 0 1 rg 0 0 1 1 re f");
        var e = new Expected(20, 20, 0.8 * 255).Rect(4, 4, 14, 12, Rgb(1, 0, 0)).Rect(4, 4, 6, 6, Rgb(0, 0, 1));
        Check(pdf, e, Tol.One);
    }

    // The appearance matrix is applied to the BBox first; here a quarter turn, so the blue mark moves to the lower-right corner.
    [Fact]
    [Trait("Section", "12.5.5")]
    public void AppearanceMatrixIsAppliedBeforeFitting()
    {
        byte[] pdf = Annot("/Rect [4 4 14 12] /F 4", "/BBox [0 0 4 5] /Matrix [0 1 -1 0 0 0]", "1 0 0 rg 0 0 4 5 re f 0 0 1 rg 0 0 1 1 re f");
        var e = new Expected(20, 20, 0.8 * 255).Rect(4, 4, 14, 12, Rgb(1, 0, 0)).Rect(12, 4, 14, 6, Rgb(0, 0, 1));
        Check(pdf, e, Tol.One);
    }

    // 12.5.3 Table 165: the Hidden flag (bit 2) and NoView (bit 6) suppress the annotation on screen.
    [Theory]
    [InlineData(2)]
    [InlineData(32)]
    [Trait("Section", "12.5.3")]
    public void HiddenAnnotationsAreNotDrawn(int flags)
    {
        byte[] pdf = Annot($"/Rect [4 4 14 12] /F {flags}", "/BBox [0 0 5 4]", "1 0 0 rg 0 0 5 4 re f");
        Check(pdf, new Expected(20, 20, 0.8 * 255), Tol.One);
    }

    // The renderer's Annotations option: None leaves the page content alone.
    [Fact]
    [Trait("Section", "12.5.2")]
    public void AnnotationsCanBeSwitchedOff()
    {
        byte[] pdf = Annot("/Rect [4 4 14 12] /F 4", "/BBox [0 0 5 4]", "1 0 0 rg 0 0 5 4 re f");
        Check(pdf, new Expected(20, 20, 0.8 * 255), Tol.One, new PdfRenderOptions { Dpi = 72, Annotations = PdfAnnotationRendering.None });
    }

    // Annotations are drawn after the page content, so they sit on top of it.
    [Fact]
    [Trait("Section", "12.5.5")]
    public void AnnotationsPaintAfterPageContent()
    {
        byte[] pdf = Annot("/Rect [0 0 20 20] /F 4", "/BBox [0 0 20 20]", "0 0 1 rg 8 8 4 4 re f");
        var e = new Expected(20, 20, 0.8 * 255).Rect(8, 8, 12, 12, Rgb(0, 0, 1));
        Check(pdf, e, Tol.One);
    }
}
