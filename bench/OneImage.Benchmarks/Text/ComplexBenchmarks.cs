using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OneImage.Text;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Ours = OneImage;
using OursMeasurer = OneImage.Text.TextMeasurer;
using OursOptions = OneImage.Text.TextOptions;
using SharpMeasurer = SixLabors.Fonts.TextMeasurer;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Text;

/// <summary>
/// A Devanagari and a Khmer paragraph (about 300 characters of the words in the committed shaping cases, in a mix of conjuncts, reph,
/// matras and signs) at 24 px, wrapped at 600 px, against SixLabors.Fonts 2.1.3 and ImageSharp.Drawing 2.1.7 with the matching Noto
/// subset. Words that make the reference throw are left out, so both libraries shape the same text. Measuring and drawing onto a
/// 1000 x 1000 RGBA image; OneImage draws on a Canvas in place, as ImageSharp mutates its image, and both rows clear it first.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public partial class ComplexBenchmarks : IDisposable
{
    private const int Size = 1000;
    private string _deva = null!;
    private string _khmer = null!;
    private OursOptions _devaOurs = null!;
    private OursOptions _khmerOurs = null!;
    private RichTextOptions _devaTheirs = null!;
    private RichTextOptions _khmerTheirs = null!;
    private Ours.Canvas _canvas = null!;
    private SharpRgba _sharp = null!;
    private readonly Ours.Rgba32 _white = new(255, 255, 255, 255);
    private readonly Ours.Rgba32 _black = new(0, 0, 0, 255);

    [GlobalSetup]
    public void Setup()
    {
        string root = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ComplexBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        (_deva, _devaOurs, _devaTheirs) = Prepare(root, "devanagari", "NotoSansDevanagari-Subset-Regular.ttf");
        (_khmer, _khmerOurs, _khmerTheirs) = Prepare(root, "khmer", "NotoSansKhmer-Subset-Regular.ttf");
        _canvas = new Ours.Canvas(Size, Size, Ours.PixelFormat.Rgba8);
        _sharp = new SharpRgba(Size, Size);
    }

    private static (string, OursOptions, RichTextOptions) Prepare(string root, string script, string fontFile)
    {
        string path = Path.Combine(root, "tests", "testdata", "fonts", fontFile);
        var ours = new OneImage.Text.FontCollection().Add(path).CreateFont(24);
        var theirs = new SixLabors.Fonts.FontCollection().Add(path, CultureInfo.InvariantCulture).CreateFont(24);
        var o = new OursOptions(ours) { Origin = new System.Numerics.Vector2(50, 50), WrappingLength = 600 };
        var t = new RichTextOptions(theirs) { Origin = new System.Numerics.Vector2(50, 50), WrappingLength = 600 };
        var words = new System.Collections.Generic.List<string>();
        foreach (string line in File.ReadLines(Path.Combine(root, "tests", "testdata", "complex", script + ".txt")))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            foreach (string w in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string text = Escape().Replace(w, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
                try
                {
                    _ = SharpMeasurer.MeasureAdvance(text, t);
                    words.Add(text);
                }
                catch (Exception)
                {
                    // the reference cannot shape this word; skip it
                }
            }
        }

        var sb = new StringBuilder();
        for (int i = 0; sb.Length < 300; i++)
        {
            sb.Append(words[(i * 7) % words.Count]).Append(' ');
        }

        return (sb.ToString(), o, t);
    }

    [GeneratedRegex(@"\\u([0-9A-Fa-f]{4})")]
    private static partial Regex Escape();

    [GlobalCleanup]
    public void Dispose()
    {
        _canvas?.Dispose();
        _sharp?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "OneImage: measure Devanagari paragraph")]
    public float MeasureDevaOurs() => OursMeasurer.MeasureAdvance(_deva, _devaOurs).Height;

    [Benchmark(Description = "SixLabors.Fonts: measure Devanagari paragraph")]
    public float MeasureDevaTheirs() => SharpMeasurer.MeasureAdvance(_deva, _devaTheirs).Height;

    [Benchmark(Description = "OneImage: draw Devanagari paragraph 24 px (canvas)")]
    public Ours.Rgba32 DrawDevaOurs()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(_deva, _devaOurs, _black);
        return _canvas[500, 100];
    }

    [Benchmark(Description = "ImageSharp.Drawing: draw Devanagari paragraph 24 px")]
    public SixLabors.ImageSharp.PixelFormats.Rgba32 DrawDevaTheirs()
    {
        _sharp.Mutate(c => c.Clear(SixLabors.ImageSharp.Color.White).DrawText(_devaTheirs, _deva, SixLabors.ImageSharp.Color.Black));
        return _sharp[500, 100];
    }

    [Benchmark(Description = "OneImage: measure Khmer paragraph")]
    public float MeasureKhmerOurs() => OursMeasurer.MeasureAdvance(_khmer, _khmerOurs).Height;

    [Benchmark(Description = "SixLabors.Fonts: measure Khmer paragraph")]
    public float MeasureKhmerTheirs() => SharpMeasurer.MeasureAdvance(_khmer, _khmerTheirs).Height;

    [Benchmark(Description = "OneImage: draw Khmer paragraph 24 px (canvas)")]
    public Ours.Rgba32 DrawKhmerOurs()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(_khmer, _khmerOurs, _black);
        return _canvas[500, 100];
    }

    [Benchmark(Description = "ImageSharp.Drawing: draw Khmer paragraph 24 px")]
    public SixLabors.ImageSharp.PixelFormats.Rgba32 DrawKhmerTheirs()
    {
        _sharp.Mutate(c => c.Clear(SixLabors.ImageSharp.Color.White).DrawText(_khmerTheirs, _khmer, SixLabors.ImageSharp.Color.Black));
        return _sharp[500, 100];
    }
}
