using System.Security.Cryptography;
using OneImage.Codecs.Jbig2;

namespace OneImage.Tests.Jbig2;

/// <summary>
/// Small JBIG2 streams taken from public PDF files (with their shared globals where they have any). The expected hashes
/// are of the page bits (1 = black, padding bits zero) and were checked against the image another PDF tool extracted
/// from the same file.
/// </summary>
public class Jbig2PdfStreamTests
{
    [Theory]
    [InlineData("504344-001", 3600, 4576, "928d4c183a12cfc38162c392b3ab8b9a444da7e7ed8af20bdbd38d401790d7ab")]
    [InlineData("714558-003", 2089, 3152, "449a68b792bc71e9be061a67997540f32e242d4be916972facb221612253fd45")]
    [InlineData("714748-174", 2552, 3304, "c09c9c109fd68c80f374d888a10053bfa7e63b135cd5e221e01770d9ef510dca")]
    [InlineData("966625-006", 1321, 1814, "40fbdc106d0bbe9407aa814b102ff32ba965117486aa32245421867ce70903a9")]
    public void PageMatchesTheReference(string name, int width, int height, string sha256)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "jbig2");
        byte[] stream = File.ReadAllBytes(Path.Combine(dir, "pdf-" + name + ".jb2e"));
        string globalsPath = Path.Combine(dir, "pdf-" + name + ".jb2g");
        byte[] globals = File.Exists(globalsPath) ? File.ReadAllBytes(globalsPath) : [];

        Jbig2Page page = Jbig2Codec.Decode(stream, globals);

        Assert.Equal(width, page.Width);
        Assert.Equal(height, page.Height);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(page.Data)));
    }
}
