using System.Security.Cryptography;
using System.Text;

namespace OneImage.Pdf.Tests;

/// <summary>JPEG 2000 images in a PDF: drawn through the decoder, with the colour space the file states, and left out when damaged.</summary>
public class JpxTests
{
    private static byte[] Data(string name) => File.ReadAllBytes(Path.Combine(TestPaths.RepoRoot, "testdata", "jpx", "gen", name));

    // SHA-256 of the 8-bit samples the corpus generator (tools/gen-jpx-corpus.py) encoded losslessly, so these are the exact original pixels.
    private const string RgbDefaultPixels = "40ed7c137af4d5cc0026dee9face1ed39fd1a1465da410cbed12b42f4b415b6a";
    private const string Gray1ResPixels = "278e0856409a2c49bbfc790a00e21ca37d9ebae71c1d957c27262e777da3c563";

    /// <summary>A page the size of the picture, drawn one pixel to one point over a red background.</summary>
    private static byte[] Pdf(byte[] jpx, int w, int h, string colorSpace = "")
    {
        string im = $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} {colorSpace} /BitsPerComponent 8 /Filter /JPXDecode /Length {jpx.Length} >>\nstream\n{Encoding.Latin1.GetString(jpx)}\nendstream";
        return TestPdf.Page($"1 0 0 rg 0 0 {w} {h} re f q {w} 0 0 {h} 0 0 cm /Im Do Q", "/XObject << /Im 5 0 R >>", width: w, height: h, extra: [im]);
    }

    private static string RgbHash(Rgba32[] px)
    {
        var rgb = new byte[px.Length * 3];
        for (int i = 0; i < px.Length; i++)
        {
            rgb[3 * i] = px[i].R;
            rgb[(3 * i) + 1] = px[i].G;
            rgb[(3 * i) + 2] = px[i].B;
        }

        return Convert.ToHexStringLower(SHA256.HashData(rgb));
    }

    private static string GrayHash(Rgba32[] px) => Convert.ToHexStringLower(SHA256.HashData(px.Select(p => p.R).ToArray()));

    [Theory]
    [InlineData("")]
    [InlineData("/ColorSpace /DeviceRGB")]
    public void LosslessRgbDrawsTheEncodedPixels(string colorSpace)
    {
        Rgba32[] px = RenderTests.Load(Pdf(Data("rgb-default.j2k"), 97, 61, colorSpace), new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        Assert.Equal(97 * 61, px.Length);
        Assert.Equal(RgbDefaultPixels, RgbHash(px));
    }

    [Fact]
    public void LosslessGrayUsesTheStatedColorSpace()
    {
        Rgba32[] px = RenderTests.Load(Pdf(Data("gray-1res.j2k"), 64, 48), new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        Assert.All(px, p => Assert.True(p.R == p.G && p.G == p.B));
        Assert.Equal(Gray1ResPixels, GrayHash(px));
    }

    [Fact]
    public void DamagedHeaderLeavesTheImageOutAndTheRestOfThePageDraws()
    {
        byte[] jpx = Data("rgb-default.j2k");
        Rgba32[] px = RenderTests.Load(Pdf(jpx[..40], 97, 61), new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        Assert.All(px, p => Assert.Equal(new Rgba32(255, 0, 0, 255), p));
    }

    [Fact]
    public void CutShortTileDataLeavesTheImageOut()
    {
        byte[] jpx = Data("rgb-default.j2k");
        Rgba32[] px = RenderTests.Load(Pdf(jpx[..(jpx.Length / 2)], 97, 61), new PdfRenderOptions { Dpi = 72 }).ToArray<Rgba32>();
        Assert.All(px, p => Assert.Equal(new Rgba32(255, 0, 0, 255), p));
    }
}
