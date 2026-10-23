using OneImage.Codecs.Jbig2;

namespace OneImage.Tests.Jbig2;

/// <summary>The examples of T.88 annex H: the arithmetic coder test sequence and the three-page example stream.</summary>
public class Jbig2AnnexHTests
{
    private static readonly byte[] s_mqPlain =
    [
        0x00, 0x02, 0x00, 0x51, 0x00, 0x00, 0x00, 0xC0, 0x03, 0x52, 0x87, 0x2A, 0xAA, 0xAA, 0xAA, 0xAA,
        0x82, 0xC0, 0x20, 0x00, 0xFC, 0xD7, 0x9E, 0xF6, 0xBF, 0x7F, 0xED, 0x90, 0x4F, 0x46, 0xA3, 0xBF,
    ];

    private static readonly byte[] s_mqCoded =
    [
        0x84, 0xC7, 0x3B, 0xFC, 0xE1, 0xA1, 0x43, 0x04, 0x02, 0x20, 0x00, 0x00, 0x41, 0x0D, 0xBB, 0x86,
        0xF4, 0x31, 0x7F, 0xFF, 0x88, 0xFF, 0x37, 0x47, 0x1A, 0xDB, 0x6A, 0xDF, 0xFF, 0xAC,
    ];

    internal static byte[] AnnexH() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "jbig2", "annex-h.jb2"));

    [Fact]
    public void ArithmeticDecoderMatchesTheTestSequence()
    {
        var mq = new Jbig2ArithmeticDecoder(s_mqCoded, 0, s_mqCoded.Length);
        byte[] cx = new byte[1];
        var result = new byte[32];
        for (int i = 0; i < 256; i++)
        {
            int d = mq.Decode(cx, 0);
            result[i >> 3] |= (byte)(d << (7 - (i & 7)));
        }

        Assert.Equal(s_mqPlain, result);
    }

    [Fact]
    public void Page1HuffmanAndMmrEqualsPage2Arithmetic()
    {
        byte[] file = AnnexH();
        Jbig2Page p1 = Jbig2Codec.DecodeFile(file, 1);
        Jbig2Page p2 = Jbig2Codec.DecodeFile(file, 2);
        Assert.Equal((64, 56), (p1.Width, p1.Height));
        Assert.Equal((64, 56), (p2.Width, p2.Height));
        Assert.Equal(p1.Data, p2.Data);
        Assert.Contains(p1.Data, b => b != 0);
    }

    [Fact]
    public void Page3RefinementAndAggregationDrawTheSameTextAsPage1()
    {
        byte[] file = AnnexH();
        Jbig2Page p1 = Jbig2Codec.DecodeFile(file, 1);
        Jbig2Page p3 = Jbig2Codec.DecodeFile(file, 3);
        Assert.Equal((37, 8), (p3.Width, p3.Height));

        // Page 1 draws the same text region (Figure H.5) with its top left pixel at (4, 1).
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 37; x++)
            {
                int a = (p3.Data[(y * p3.Stride) + (x >> 3)] >> (7 - (x & 7))) & 1;
                int b = (p1.Data[((y + 1) * p1.Stride) + ((x + 4) >> 3)] >> (7 - ((x + 4) & 7))) & 1;
                Assert.True(a == b, $"pixel ({x},{y})");
            }
        }
    }

    [Fact]
    public void PagesRenderAsExpected()
    {
        byte[] file = AnnexH();
        Jbig2Page p1 = Jbig2Codec.DecodeFile(file, 1);
        string art = Ascii(p1);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "jbig2-annexh-page1.txt"), art);
        Assert.StartsWith("................................................................\n", art);
    }

    internal static string Ascii(Jbig2Page p)
    {
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < p.Height; y++)
        {
            for (int x = 0; x < p.Width; x++)
            {
                sb.Append(((p.Data[(y * p.Stride) + (x >> 3)] >> (7 - (x & 7))) & 1) != 0 ? '#' : '.');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
