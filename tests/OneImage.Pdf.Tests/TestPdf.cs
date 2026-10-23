using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace OneImage.Pdf.Tests;

/// <summary>Assembles small PDF files for tests: page content plus extra objects, with a correct cross-reference table.</summary>
internal static class TestPdf
{
    /// <summary>One page of <paramref name="width"/> x <paramref name="height"/> points. Extra objects start at number 5 (1 catalog, 2 pages, 3 page, 4 content).</summary>
    public static byte[] Page(string content, string resources = "", double width = 200, double height = 200, string[]? extra = null, string pageExtra = "")
    {
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R /Resources << {resources} >> {pageExtra} >>")),
            Stream("", Ascii(content)),
        };
        if (extra is not null)
        {
            foreach (string e in extra)
            {
                objects.Add(Ascii(e));
            }
        }

        return Build(objects);
    }

    public static byte[] Stream(string dictEntries, byte[] data) =>
        Concat(Ascii($"<< {dictEntries} /Length {data.Length} >>\nstream\n"), data, Ascii("\nendstream"));

    public static byte[] Deflated(string dictEntries, byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return Stream(dictEntries + " /Filter /FlateDecode", ms.ToArray());
    }

    /// <summary>Several pages, each with its own content and size.</summary>
    public static byte[] Pages(params (string Content, double Width, double Height)[] pages)
    {
        var objects = new List<byte[]> { Ascii("<< /Type /Catalog /Pages 2 0 R >>"), null! };
        var kids = new StringBuilder();
        for (int i = 0; i < pages.Length; i++)
        {
            int pageNo = 3 + (i * 2);
            kids.Append(CultureInfo.InvariantCulture, $"{pageNo} 0 R ");
            objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pages[i].Width} {pages[i].Height}] /Contents {pageNo + 1} 0 R /Resources << >> >>")));
            objects.Add(Stream("", Ascii(pages[i].Content)));
        }

        objects[1] = Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} >>");
        return Build(objects);
    }

    public static byte[] Build(List<byte[]> objects)
    {
        using var ms = new MemoryStream();
        void W(byte[] b) => ms.Write(b);
        W(Ascii("%PDF-1.7\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W(Ascii($"{i + 1} 0 obj\n"));
            W(objects[i]);
            W(Ascii("\nendobj\n"));
        }

        long xref = ms.Position;
        W(Ascii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (long o in offsets)
        {
            W(Ascii(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n"));
        }

        W(Ascii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return ms.ToArray();
    }

    public static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}

/// <summary>Where tests and tools find the repository and scratch space.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;
}
