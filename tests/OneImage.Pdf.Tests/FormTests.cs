using System.Globalization;

namespace OneImage.Pdf.Tests;

/// <summary>Form fields without an appearance stream are drawn from the values the form holds.</summary>
public class FormTests
{
    private const int PageHeight = 200;

    private static byte[] Form(string textValue, string checkState, string comboValue)
    {
        var o = new List<byte[]>
        {
            TestPdf.Ascii("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R 7 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> /NeedAppearances true >> >>"),
            TestPdf.Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            TestPdf.Ascii(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 {PageHeight}] /Contents 4 0 R /Resources << /Font << /Helv 8 0 R >> >> /Annots [5 0 R 6 0 R 7 0 R] >>")),
            TestPdf.Stream("", TestPdf.Ascii("0.9 g 0 0 200 20 re f")),
            TestPdf.Ascii($"<< /Type /Annot /Subtype /Widget /F 4 /T (name) /Rect [20 150 180 175] /FT /Tx /V ({textValue}) /DA (/Helv 12 Tf 0 g) /P 3 0 R >>"),
            TestPdf.Ascii($"<< /Type /Annot /Subtype /Widget /F 4 /T (agree) /Rect [20 110 40 130] /FT /Btn /V /{checkState} /AS /{checkState} /DA (/ZaDb 0 Tf 0 g) /MK << /CA (4) /BC [0 0 0] >> /P 3 0 R >>"),
            TestPdf.Ascii($"<< /Type /Annot /Subtype /Widget /F 4 /T (pick) /Rect [20 60 180 85] /FT /Ch /Ff 131072 /V ({comboValue}) /Opt [(Alpha) (Bravo) (Charlie)] /DA (/Helv 12 Tf 0 g) /P 3 0 R >>"),
            TestPdf.Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        };
        return TestPdf.Build(o);
    }

    private static Rgba32[] Render(byte[] pdf, bool generate)
    {
        Image img = RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72, GenerateAppearances = generate });
        return img.ToArray<Rgba32>();
    }

    /// <summary>Counts pixels that are not white inside a field's rectangle (PDF points, origin bottom left; the page is drawn at 72 dpi).</summary>
    private static int Ink(Rgba32[] px, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = PageHeight - y1; y < PageHeight - y0; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                Rgba32 p = px[(y * 200) + x];
                if (p.R < 200 || p.G < 200 || p.B < 200)
                {
                    n++;
                }
            }
        }

        return n;
    }

    [Fact]
    public void FieldValuesAreVisible()
    {
        byte[] filled = Form("Hello Form", "Yes", "Bravo");
        byte[] blank = Form("", "Off", "");
        Rgba32[] on = Render(filled, generate: true);
        Rgba32[] empty = Render(blank, generate: true);
        Assert.True(Ink(on, 20, 150, 180, 175) > 40, "the text field value is not drawn");
        Assert.Equal(0, Ink(empty, 20, 150, 180, 175));
        Assert.True(Ink(on, 20, 110, 40, 130) > 10, "the checked box is not drawn");
        Assert.True(Ink(on, 20, 60, 180, 85) > 30, "the choice value is not drawn");
        Assert.Equal(0, Ink(empty, 20, 60, 180, 85));
    }

    [Fact]
    public void GeneratedAppearancesCanBeTurnedOff()
    {
        Rgba32[] off = Render(Form("Hello Form", "Yes", "Bravo"), generate: false);
        Assert.Equal(0, Ink(off, 20, 150, 180, 175));
        Assert.Equal(0, Ink(off, 20, 60, 180, 85));
    }

    [Fact]
    public void NoAnnotationsDrawsNoFields()
    {
        Image img = RenderTests.Load(Form("Hello Form", "Yes", "Bravo"), new PdfRenderOptions { Dpi = 72, Annotations = PdfAnnotationRendering.None });
        Assert.Equal(0, Ink(img.ToArray<Rgba32>(), 20, 150, 180, 175));
    }

    private static byte[] File(string name) => System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestPaths.RepoRoot, "testdata", "pdf", name));

    private static int InkAt(Rgba32[] px, int width, int height, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = height - y1; y < height - y0; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                Rgba32 p = px[(y * width) + x];
                if (p.R < 230 || p.G < 230 || p.B < 230)
                {
                    n++;
                }
            }
        }

        return n;
    }

    [Fact]
    public void FilledFormShowsTextChoiceAndCheckFields()
    {
        // text fields and the check box have appearance streams; the combo and the list box have none and are generated.
        byte[] pdf = File("form-filled.pdf");
        Rgba32[] on = RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        Rgba32[] off = RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72, GenerateAppearances = false }).ToArray<Rgba32>();
        Assert.True(InkAt(on, 612, 792, 72, 600, 272, 620) > 30, "text field");
        Assert.True(InkAt(on, 612, 792, 72, 540, 86, 554) > 30, "checked box");
        Assert.True(InkAt(on, 612, 792, 72, 470, 200, 490) > 30, "combo box");
        Assert.True(InkAt(on, 612, 792, 72, 400, 200, 460) > 300, "list box");
        Assert.Equal(0, InkAt(off, 612, 792, 72, 470, 200, 490));
        Assert.Equal(0, InkAt(off, 612, 792, 72, 400, 200, 460));
    }

    [Fact]
    public void FormThatNeedsAppearancesDrawsItsFields()
    {
        Rgba32[] px = RenderTests.Load(File("form-widgets-need-appearances.pdf"), new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        Assert.True(InkAt(px, 612, 792, 40, 704, 240, 728) > 30);
        Assert.True(InkAt(px, 612, 792, 40, 416, 240, 476) > 300);
    }
}
