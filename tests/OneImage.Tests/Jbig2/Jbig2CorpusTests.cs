using System.Text;
using OneImage.Codecs.Jbig2;

namespace OneImage.Tests.Jbig2;

/// <summary>
/// Decodes JBIG2 streams extracted from real PDF files and compares them with the images a separate PDF image
/// extractor (poppler's <c>pdfimages</c>, used as a black box) produced from the same files. The extracted streams
/// are not part of the repository: <c>tools/jbig2-corpus.sh</c> builds the directory, and the test runs when the
/// <c>ONEIMAGE_JBIG2_CORPUS</c> environment variable names it.
/// </summary>
public class Jbig2CorpusTests
{
    [Fact]
    public void RealPdfStreamsMatchTheReferenceDecoder()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_JBIG2_CORPUS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        var table = new StringBuilder();
        int ok = 0;
        int bad = 0;
        var failures = new List<string>();
        foreach (string stream in Directory.EnumerateFiles(dir, "*.jb2e").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(stream);
            int dash = name.LastIndexOf('-');
            string reference = Path.Combine(dir, name[..dash] + "-ref-" + name[(dash + 1)..] + ".pbm");
            string globalsPath = Path.ChangeExtension(stream, ".jb2g");
            byte[] globals = File.Exists(globalsPath) ? File.ReadAllBytes(globalsPath) : [];
            (int rw, int rh, byte[] rdata) = ReadPbm(reference);
            string result;
            try
            {
                Jbig2Page page = Jbig2Codec.Decode(File.ReadAllBytes(stream), globals);
                if (page.Width != rw || page.Height != rh)
                {
                    result = $"SIZE {page.Width}x{page.Height} vs {rw}x{rh}";
                }
                else if (page.Data.AsSpan().SequenceEqual(rdata))
                {
                    result = "match";
                    ok++;
                }
                else if (IsInverted(page, rdata))
                {
                    // The extractor applies /Decode [1 0] or /ImageMask polarity; the decoder returns 1 = black.
                    result = "match-inverted";
                    ok++;
                }
                else
                {
                    result = "DIFF";
                }
            }
            catch (ImageException e)
            {
                result = "ERROR " + e.GetType().Name + ": " + e.Message;
            }

            if (result != "match" && result != "match-inverted")
            {
                bad++;
                failures.Add(name + " " + result);
            }

            table.Append(name).Append(' ').Append(rw).Append('x').Append(rh).Append(' ').Append(result).Append('\n');
        }

        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("ONEIMAGE_JBIG2_TABLE") ?? Path.GetTempPath(), "jbig2-corpus.txt"), table.ToString());
        Assert.True(bad == 0, $"{ok} matched, {bad} did not:\n" + string.Join('\n', failures.Take(40)));
    }

    private static bool IsInverted(Jbig2Page page, byte[] reference)
    {
        for (int y = 0; y < page.Height; y++)
        {
            for (int x = 0; x < page.Width; x++)
            {
                int i = y * page.Stride + (x >> 3);
                int m = 0x80 >> (x & 7);
                if (((page.Data[i] ^ reference[i]) & m) == 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static (int Width, int Height, byte[] Data) ReadPbm(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int p = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)b[p]))
            {
                p++;
            }

            int s = p;
            while (!char.IsWhiteSpace((char)b[p]))
            {
                p++;
            }

            return Encoding.ASCII.GetString(b, s, p - s);
        }

        Assert.Equal("P4", Token());
        int w = int.Parse(Token(), System.Globalization.CultureInfo.InvariantCulture);
        int h = int.Parse(Token(), System.Globalization.CultureInfo.InvariantCulture);
        p++;
        return (w, h, b[p..]);
    }
}
