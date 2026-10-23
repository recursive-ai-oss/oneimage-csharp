using System.IO;
using BenchmarkDotNet.Attributes;
using OneImage.Codecs.Jbig2;

namespace OneImage.Benchmarks.Jbig2;

/// <summary>
/// JBIG2 page decodes. Uses the small streams under tests/testdata/jbig2 (scanned-page text with shared symbol
/// dictionaries, a mostly blank page, a generic-region page) and the Annex H example.
/// </summary>
[MemoryDiagnoser]
public class Jbig2Benchmarks
{
    private byte[] _stream = null!;
    private byte[] _globals = null!;

    [Params("714748-174", "714558-003", "966625-006", "504344-001")]
    public string Page { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        string dir = FindTestData();
        _stream = File.ReadAllBytes(Path.Combine(dir, "pdf-" + Page + ".jb2e"));
        string g = Path.Combine(dir, "pdf-" + Page + ".jb2g");
        _globals = File.Exists(g) ? File.ReadAllBytes(g) : [];
    }

    [Benchmark]
    public int Decode() => Jbig2Codec.Decode(_stream, _globals).Data.Length;

    private static string FindTestData()
    {
        string? env = Environment.GetEnvironmentVariable("ONEIMAGE_JBIG2_DIR");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        for (string? d = Directory.GetCurrentDirectory(); d is not null; d = Path.GetDirectoryName(d))
        {
            string p = Path.Combine(d, "tests", "testdata", "jbig2");
            if (Directory.Exists(p))
            {
                return p;
            }
        }

        throw new DirectoryNotFoundException("tests/testdata/jbig2 not found above the working directory (set ONEIMAGE_JBIG2_DIR to tests/testdata/jbig2)");
    }
}
