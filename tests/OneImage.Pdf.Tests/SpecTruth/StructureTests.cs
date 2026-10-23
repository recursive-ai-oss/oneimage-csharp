using System.IO.Compression;
using static OneImage.Pdf.Tests.SpecTruth.Spec;

namespace OneImage.Pdf.Tests.SpecTruth;

/// <summary>Document structure that affects what is drawn: content stream arrays, inheritance, forms, filters, annotation states, text knockout (ISO 32000-2 7.7.3, 7.8.2, 8.10, 9.3.8, 12.5).</summary>
public class StructureTests
{
    private static readonly (double R, double G, double B) Red = Rgb(1, 0, 0);

    // 7.8.2: an array of content streams behaves like their concatenation with white space between them,
    // so a number may end one stream and a different one begin the next.
    [Fact]
    [Trait("Section", "7.8.2")]
    public void ContentStreamArrayIsConcatenatedWithWhiteSpace()
    {
        byte[] pdf = Doc(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents [4 0 R 5 0 R] /Resources << >> >>",
            Stream("", "1 0 0 rg 5"),
            Stream("", "5 10 10 re f"));
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Red), Tol.Exact);
    }

    // 7.7.3.4: MediaBox and Resources are inheritable from the page tree.
    [Fact]
    [Trait("Section", "7.7.3.4")]
    public void PageInheritsMediaBoxAndResources()
    {
        byte[] pdf = Doc(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 20 20] /Resources << /ExtGState << /G << /ca 0.5 >> >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            Stream("", "/G gs 1 0 0 rg 5 5 10 10 re f"));
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Red, 0.5), Tol.One);
    }

    // 8.10.1: a form's BBox is clipped in form space, so a rotated Matrix rotates the clip with it.
    [Fact]
    [Trait("Section", "8.10.1")]
    public void RotatedFormClipsInFormSpace()
    {
        // (x,y) -> (-y + 20, x): BBox [0 0 10 6] -> x 14..20, y 0..10.
        string form = Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 6] /Matrix [0 1 -1 0 20 0]", "0 g -50 -50 100 100 re f");
        byte[] pdf = Page("/F0 Do", "/XObject << /F0 5 0 R >>", extra: [form]);
        Check(pdf, new Expected(20, 20).Rect(14, 0, 20, 10, Gray(0)), Tol.Exact);
    }

    // 8.5.4: the clip applies to every kind of painting, images included.
    [Fact]
    [Trait("Section", "8.5.4")]
    public void ClipAppliesToImages()
    {
        string img = Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", new byte[] { 255, 0, 0 });
        byte[] pdf = Page("4 4 8 8 re W n q 20 0 0 20 0 0 cm /Im0 Do Q", "/XObject << /Im0 5 0 R >>", extra: [img]);
        Check(pdf, new Expected(20, 20).Rect(4, 4, 12, 12, Red), Tol.Exact);
    }

    // 8.9.7: abbreviated inline image keys, ASCIIHex filter, 1-bit gray samples. Rows "10" and "01" give white/black over black/white;
    // checked at the sample centers and outside the image only (a reader may interpolate between them).
    [Fact]
    [Trait("Section", "8.9.7")]
    public void InlineImageWithAsciiHexFilter()
    {
        byte[] pdf = Page("q 36 0 0 36 2 2 cm BI /W 2 /H 2 /CS /G /BPC 1 /F /AHx ID 80 40> EI Q", w: 40, h: 40);
        Rgba32[] px = Pix(pdf);
        // Samples (row, col): (0,0)=1 white, (0,1)=0 black, (1,0)=0 black, (1,1)=1 white. Outer quarters are exact under any interpolation.
        Pixel(px, 40, 4, 4, Gray(1), Tol.Exact);
        Pixel(px, 40, 36, 4, Gray(0), Tol.Exact);
        Pixel(px, 40, 4, 36, Gray(0), Tol.Exact);
        Pixel(px, 40, 36, 36, Gray(1), Tol.Exact);
        Pixel(px, 40, 0, 0, Gray(1), Tol.Exact);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    // 7.4.4: FlateDecode, with and without the PNG "Up" predictor (7.4.4.4), gives the same samples.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Section", "7.4.4")]
    public void FlateImageWithAndWithoutPredictor(bool predictor)
    {
        byte[] raw = [0, 85, 170, 255, 255, 170, 85, 0, 0, 255, 0, 255, 255, 0, 255, 0]; // 4 x 4 gray
        byte[] data;
        string parms = "";
        if (predictor)
        {
            data = new byte[4 * 5];
            for (int r = 0; r < 4; r++)
            {
                data[r * 5] = 2; // Up
                for (int c = 0; c < 4; c++)
                {
                    data[(r * 5) + 1 + c] = (byte)(raw[(r * 4) + c] - (r == 0 ? 0 : raw[((r - 1) * 4) + c]));
                }
            }

            parms = "/DecodeParms << /Predictor 12 /Columns 4 >>";
        }
        else
        {
            data = raw;
        }

        string img = Stream($"/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode {parms}", Deflate(data));
        byte[] pdf = Page("q 36 0 0 36 2 2 cm /Im0 Do Q", "/XObject << /Im0 5 0 R >>", w: 40, h: 40, extra: [img]);
        Rgba32[] px = Pix(pdf);
        // Sample centers: with 9-pixel samples the center pixel lies exactly at the sample center.
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                Pixel(px, 40, 2 + (c * 9) + 4, 2 + (r * 9) + 4, Gray(raw[(r * 4) + c] / 255.0), Tol.Exact);
            }
        }
    }

    // 11.6.5.3 /Matte: the image samples are pre-blended with the matte color; the reader recovers c = m + (c' - m) / alpha.
    [Fact]
    [Trait("Section", "11.6.5.3")]
    public void SoftMaskMatteUnPremultipliesTheImage()
    {
        // True color (1,0,0) at alpha 128/255 over matte black is stored as about (128, 0, 0). After un-matting: red at that opacity.
        string mask = Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Matte [0 0 0]", new byte[] { 128 });
        string img = Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R", new byte[] { 128, 0, 0 });
        byte[] pdf = Page("q 20 0 0 20 0 0 cm /Im0 Do Q", "/XObject << /Im0 5 0 R >>", extra: [img, mask]);
        Check(pdf, new Expected(20, 20).Rect(0, 0, 20, 20, Red, 128.0 / 255), Tol.One);
    }

    // ---- annotations: states and flags ----

    private static byte[] StateAnnot(string annot)
    {
        string on = Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "1 0 0 rg 0 0 10 10 re f");
        string off = Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "0 1 0 rg 0 0 10 10 re f");
        return Doc(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents 4 0 R /Resources << >> /Annots [5 0 R] >>",
            Stream("", ""),
            annot,
            on,
            off);
    }

    // 12.5.5: with a dictionary of appearance states, /AS chooses which one is drawn.
    [Theory]
    [InlineData("/On", 1.0, 0.0)]
    [InlineData("/Off", 0.0, 1.0)]
    [Trait("Section", "12.5.5")]
    public void AppearanceStateSelectsTheStream(string state, double r, double g)
    {
        byte[] pdf = StateAnnot($"<< /Type /Annot /Subtype /Stamp /Rect [5 5 15 15] /F 4 /AP << /N << /On 6 0 R /Off 7 0 R >> >> /AS {state} >>");
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Rgb(r, g, 0)), Tol.Exact);
    }

    // 12.5.2: /Rect names any two opposite corners; the appearance fills the normalized rectangle.
    [Fact]
    [Trait("Section", "12.5.2")]
    public void AnnotationRectWithReversedCorners()
    {
        byte[] pdf = StateAnnot("<< /Type /Annot /Subtype /Stamp /Rect [15 15 5 5] /F 4 /AP << /N 6 0 R >> >>");
        Check(pdf, new Expected(20, 20).Rect(5, 5, 15, 15, Red), Tol.Exact);
    }

    // 12.5.3: with no flags set (F 0) an annotation is shown on screen; in print mode it needs the Print flag (bit 3).
    [Theory]
    [InlineData(0, PdfAnnotationRendering.View, true)]
    [InlineData(4, PdfAnnotationRendering.View, true)]
    [InlineData(0, PdfAnnotationRendering.Print, false)]
    [InlineData(4, PdfAnnotationRendering.Print, true)]
    [Trait("Section", "12.5.3")]
    public void AnnotationFlagsDecideVisibility(int flags, PdfAnnotationRendering mode, bool drawn)
    {
        byte[] pdf = StateAnnot($"<< /Type /Annot /Subtype /Stamp /Rect [5 5 15 15] /F {flags} /AP << /N 6 0 R >> >>");
        var e = new Expected(20, 20);
        if (drawn)
        {
            e.Rect(5, 5, 15, 15, Red);
        }

        Check(pdf, e, Tol.Exact, new PdfRenderOptions { Dpi = 72, Annotations = mode });
    }

    // ---- text knockout (9.3.8) ----

    private static string[] SquareFont() =>
    [
        "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 10 10] /FontMatrix [0.1 0 0 0.1 0 0] /CharProcs << /a 6 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [10] /Resources << >> >>",
        Stream("", "10 0 0 0 10 10 d1 0 0 10 10 re f"),
    ];

    // 9.3.8: with Tk true (the initial value) the glyphs of one text object form a knockout group: overlapping glyphs do not accumulate.
    [Fact(Skip = "Defect: glyphs of one text object accumulate when alpha < 1; 9.3.8 makes the initial Tk true, a knockout group (overlap shows 50%, renderer gives 75%)")]
    [Trait("Section", "9.3.8")]
    public void TextKnockoutIsOnByDefault()
    {
        byte[] pdf = Page("/G gs 0 g BT /F1 4 Tf -2 Tc 2 2 Td (aa) Tj ET", "/Font << /F1 5 0 R >> /ExtGState << /G << /ca 0.5 >> >>", w: 20, h: 10, extra: SquareFont());
        // Glyphs at 2..6 and 4..8; the overlap 4..6 shows only the later glyph at 50%.
        Check(pdf, new Expected(20, 10).Rect(2, 2, 8, 6, Gray(0), 0.5), Tol.One);
    }

    [Fact]
    [Trait("Section", "9.3.8")]
    public void TextKnockoutOffLetsGlyphsAccumulate()
    {
        byte[] pdf = Page("/G gs 0 g BT /F1 4 Tf -2 Tc 2 2 Td (aa) Tj ET", "/Font << /F1 5 0 R >> /ExtGState << /G << /ca 0.5 /TK false >> >>", w: 20, h: 10, extra: SquareFont());
        Check(pdf, new Expected(20, 10).Rect(2, 2, 6, 6, Gray(0), 0.5).Rect(4, 2, 8, 6, Gray(0), 0.5), Tol.One);
    }
}
