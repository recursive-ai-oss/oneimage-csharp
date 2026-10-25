using System.IO.Compression;
using System.Text;

namespace OneImage.Svg.Tests;

/// <summary>The gzip trailer of an .svgz: a wrong CRC-32 or size is corruption, a missing or cut-short trailer is tolerated.</summary>
public class GzipTrailerTests
{
    private static byte[] Gz(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    private static byte[] Doc()
    {
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>");
        for (int i = 0; i < 200; i++)
        {
            sb.Append("<rect x='").Append(i % 10).Append("' y='1' width='1' height='1'/>");
        }

        return Encoding.ASCII.GetBytes(sb.Append("</svg>").ToString());
    }

    [Fact]
    public void MissingOrCutShortTrailerIsAccepted()
    {
        byte[] doc = Doc();
        byte[] gz = Gz(doc);
        for (int present = 0; present <= 7; present++)
        {
            Assert.Equal(doc, SvgSource.Gunzip(gz.AsSpan(0, gz.Length - (8 - present)), 1 << 20, 1100, strict: true, out _));
        }
    }

    [Fact]
    public void WrongTrailerIsRejected()
    {
        byte[] gz = Gz(Doc());
        for (int i = 1; i <= 8; i++)
        {
            byte[] bad = (byte[])gz.Clone();
            bad[^i] ^= 0x04;
            Assert.Throws<ImageFormatException>(() => SvgSource.Gunzip(bad, 1 << 20, 1100, strict: true, out _));
        }
    }

    [Fact]
    public void TruncatedDeflateDataIsStillAnError()
    {
        byte[] gz = Gz(Doc());
        Assert.Throws<ImageFormatException>(() => SvgSource.Gunzip(gz.AsSpan(0, gz.Length / 2), 1 << 20, 1100, strict: true, out _));
    }
}
