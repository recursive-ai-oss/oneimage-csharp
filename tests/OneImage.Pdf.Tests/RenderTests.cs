namespace OneImage.Pdf.Tests;

public class RenderTests
{
    internal static Image Load(byte[] pdf, PdfRenderOptions? options = null)
    {
        var config = new ImageConfiguration().Add(new PdfFormat(options));
        return Image.Load(pdf, new LoadOptions { Configuration = config });
    }

    [Fact]
    public void RendersAFilledRectangle()
    {
        byte[] pdf = TestPdf.Page("1 0 0 rg 50 50 100 100 re f", width: 200, height: 200);
        Image img = Load(pdf, new PdfRenderOptions { Dpi = 72 });
        Assert.Equal(200, img.Width);
        Assert.Equal(200, img.Height);
        Rgba32[] px = img.ToArray<Rgba32>();
        Assert.Equal(new Rgba32(255, 0, 0, 255), px[(100 * 200) + 100]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), px[(10 * 200) + 10]);
    }
}
