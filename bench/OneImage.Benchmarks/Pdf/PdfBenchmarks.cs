using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Pdf;

namespace OneImage.Benchmarks.Pdf;

/// <summary>
/// A whole PDF page to RGBA at 150 dpi, from the file's bytes (parse, interpret, draw). The files are named by the environment variable
/// <c>ONEIMAGE_PDF_BENCH</c> (a path list separated by ':'); without it the committed form file is drawn. PDFium through pypdfium2 is timed
/// by <c>tools/pdf-gate/bench_pdfium.py</c> on the same files, for information.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 2, iterationCount: 10)]
public class PdfPageBenchmarks
{
    private byte[] _bytes = null!;
    private ImageConfiguration _config = null!;
    private Rgba32[] _out = null!;
    private int _w, _h;

    [Params(0, 1, 2)]
    public int File { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string[] files = Environment.GetEnvironmentVariable("ONEIMAGE_PDF_BENCH")?.Split(':', StringSplitOptions.RemoveEmptyEntries)
            ?? new[] { Path.Combine(AppContext.BaseDirectory, "../../../../", "testdata/pdf/form-widgets-need-appearances.pdf") };
        _bytes = System.IO.File.ReadAllBytes(files[Math.Min(File, files.Length - 1)]);
        _config = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = 150 }));
        Image probe = Image.Load(_bytes, new LoadOptions { Configuration = _config });
        _w = probe.Width;
        _h = probe.Height;
        _out = new Rgba32[_w * _h];
    }

    [Benchmark(Description = "PDF page at 150 dpi to Rgba32")]
    public Rgba32 Render()
    {
        Image image = Image.Load(_bytes, new LoadOptions { Configuration = _config });
        image.CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, _w, _h));
        return _out[^1];
    }
}
