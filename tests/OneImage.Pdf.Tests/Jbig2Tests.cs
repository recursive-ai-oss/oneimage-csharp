using System.Text;

namespace OneImage.Pdf.Tests;

/// <summary>JBIG2 images in a PDF: polarity, globals, image masks and damaged streams.</summary>
public class Jbig2Tests
{
    private static string Latin(byte[] b) => Encoding.Latin1.GetString(b);

    private static byte[] Data(string name) => File.ReadAllBytes(Path.Combine(TestPaths.RepoRoot, "tests", "testdata", "jbig2", name));

    /// <summary>A page that draws the stream as a picture over a red square; the stream's page information segment comes first.</summary>
    private static byte[] Pdf(byte[] page, byte[]? globals, bool mask = false, string decode = "")
    {
        int w = (page[11] << 24) | (page[12] << 16) | (page[13] << 8) | page[14];
        int h = (page[15] << 24) | (page[16] << 16) | (page[17] << 8) | page[18];
        string parms = globals is null ? "" : " /DecodeParms << /JBIG2Globals 6 0 R >>";
        string kind = mask ? "/ImageMask true" : "/ColorSpace /DeviceGray /BitsPerComponent 1";
        string im = $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} {kind} {decode} /Filter /JBIG2Decode{parms} /Length {page.Length} >>\nstream\n{Latin(page)}\nendstream";
        var extra = new List<string> { im };
        if (globals is not null)
        {
            extra.Add($"<< /Length {globals.Length} >>\nstream\n{Latin(globals)}\nendstream");
        }

        return TestPdf.Page("1 0 0 rg 0 0 40 40 re f q 0 g 200 0 0 260 0 20 cm /Im Do Q", "/XObject << /Im 5 0 R >>", width: 200, height: 300, extra: extra.ToArray());
    }

    private static (int Black, int White) Count(Rgba32[] px)
    {
        int black = 0, white = 0;
        foreach (Rgba32 p in px)
        {
            if (p.R < 200 && p.G < 200 && p.B < 200 && p.R != 255)
            {
                black++;
            }
            else if (p.R > 230 && p.G > 230 && p.B > 230)
            {
                white++;
            }
        }

        return (black, white);
    }

    [Fact]
    public void ScannedPageWithGlobalsShowsDarkTextOnWhite()
    {
        byte[] pdf = Pdf(Data("pdf-714748-174.jb2e"), Data("pdf-714748-174.jb2g"));
        (int black, int white) = Count(RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>());
        // In JBIG2 1 is black; the PDF rule makes ink dark, so the page is mostly white with some black marks.
        Assert.InRange(black, 100, 30000);
        Assert.True(white > 30000);
    }

    [Fact]
    public void DecodeArrayFlipsTheSense()
    {
        byte[] page = Data("pdf-714748-174.jb2e"), globals = Data("pdf-714748-174.jb2g");
        (int black, _) = Count(RenderTests.Load(Pdf(page, globals), new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>());
        (int blackInverted, _) = Count(RenderTests.Load(Pdf(page, globals, decode: "/Decode [1 0]"), new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>());
        Assert.True(blackInverted > black * 2);
    }

    [Fact]
    public void ImageMaskPaintsInTheFillColor()
    {
        byte[] pdf = Pdf(Data("pdf-714748-174.jb2e"), Data("pdf-714748-174.jb2g"), mask: true);
        Rgba32[] px = RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        (int black, int white) = Count(px);
        Assert.True(black > 100);
        Assert.True(white > 30000);
    }

    [Fact]
    public void DamagedStreamIsLeftOutAndThePageIsDrawn()
    {
        byte[] page = Data("pdf-714748-174.jb2e");
        byte[] damaged = page[..(page.Length / 3)];
        byte[] pdf = Pdf(damaged, Data("pdf-714748-174.jb2g"));
        Rgba32[] px = RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        // The red square is there and nothing else was drawn (a half decoded page would show marks).
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[(290 * 200) + 10]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), px[(150 * 200) + 100]);
    }

    [Fact]
    public void PageWithoutGlobalsDecodes()
    {
        byte[] pdf = Pdf(Data("pdf-504344-001.jb2e"), null);
        Rgba32[] px = RenderTests.Load(pdf, new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        Assert.Equal(200 * 300, px.Length);
    }
}
