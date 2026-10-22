using System.Globalization;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Text;
using SixLabors.ImageSharp.Drawing.Processing;
using OursMeasurer = OneImage.Text.TextMeasurer;
using SharpMeasurer = SixLabors.Fonts.TextMeasurer;
using OursOptions = OneImage.Text.TextOptions;

namespace OneImage.Benchmarks.Text;

/// <summary>
/// OpenType shaping with the default features (ccmp, locl, liga, calt, kern, mark, mkmk...) against SixLabors.Fonts 2.1.3 on the same
/// text and font: a ligature-heavy Latin paragraph, and a Latin/Greek/Cyrillic paragraph with combining accents. Measuring includes
/// shaping and line breaking; the path rows add the glyph outlines. The "no layout features" row turns every GSUB/GPOS feature off,
/// which shows what shaping itself costs on top of character-map lookup and advances.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class ShapingBenchmarks
{
    private const string Latin =
        "The official affix of an efficient office is often a fluffy waffle; a first flight offers fine fish and flowing, soft, " +
        "shuffling chaff. Affluent staffers on the off-shift sniff out stiff fixes and flaws in every refit, from the first draft " +
        "to the final proof. ";

    private const string Marks =
        "Cafe\u0301 re\u0301sume\u0301 na\u0131\u0308ve Z\u0075\u0308rich Ha\u0301lo \u03B1\u0301\u03B2\u03B3 \u03BA\u03CC\u03C3\u03BC\u03B5 " +
        "\u0438\u0306 \u0430\u0301\u0431\u0432 \u043F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440 e\u0323\u0301 o\u0302\u0301 a\u0328 ";

    private string _latin = null!;
    private string _marks = null!;
    private OursOptions _ours = null!;
    private OursOptions _oursPlain = null!;
    private SixLabors.Fonts.TextOptions _theirs = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ShapingBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        string path = Path.Combine(root, "tests", "testdata", "fonts", "NotoSans-Shaping-Subset.ttf");
        _latin = string.Concat(Enumerable.Repeat(Latin, 3));
        _marks = string.Concat(Enumerable.Repeat(Marks, 6));
        OneImage.Text.Font ours = new OneImage.Text.FontCollection().Add(path).CreateFont(20);
        SixLabors.Fonts.Font theirs = new SixLabors.Fonts.FontCollection().Add(path, CultureInfo.InvariantCulture).CreateFont(20);
        _ours = new OursOptions(ours) { Origin = new System.Numerics.Vector2(20, 20), WrappingLength = 700 };
        _oursPlain = _ours.Clone();
        foreach (string tag in new[] { "ccmp", "locl", "rlig", "liga", "clig", "calt", "rclt", "kern", "mark", "mkmk", "curs" })
        {
            _oursPlain.Features.Add(FontFeature.Off(tag));
        }

        _theirs = new SixLabors.Fonts.TextOptions(theirs) { Origin = new System.Numerics.Vector2(20, 20), WrappingLength = 700 };
    }

    [Benchmark(Description = "OneImage: measure ligature paragraph")]
    [BenchmarkCategory("latin")]
    public float LatinMeasureOurs() => OursMeasurer.MeasureAdvance(_latin, _ours).Height;

    [Benchmark(Description = "SixLabors.Fonts: measure ligature paragraph")]
    [BenchmarkCategory("latin")]
    public float LatinMeasureTheirs() => SharpMeasurer.MeasureAdvance(_latin, _theirs).Height;

    [Benchmark(Description = "OneImage: measure ligature paragraph, no layout features")]
    [BenchmarkCategory("latin")]
    public float LatinMeasurePlainOurs() => OursMeasurer.MeasureAdvance(_latin, _oursPlain).Height;

    [Benchmark(Description = "OneImage: ligature paragraph as path")]
    [BenchmarkCategory("latin")]
    public int LatinPathOurs() => TextBuilder.GeneratePath(_latin, _ours).Verbs.Length;

    [Benchmark(Description = "ImageSharp.Drawing: ligature paragraph as path")]
    [BenchmarkCategory("latin")]
    public int LatinPathTheirs() => SixLabors.ImageSharp.Drawing.TextBuilder.GenerateGlyphs(_latin, _theirs).Bounds.GetHashCode();

    [Benchmark(Description = "OneImage: measure accents paragraph")]
    [BenchmarkCategory("marks")]
    public float MarksMeasureOurs() => OursMeasurer.MeasureAdvance(_marks, _ours).Height;

    [Benchmark(Description = "SixLabors.Fonts: measure accents paragraph")]
    [BenchmarkCategory("marks")]
    public float MarksMeasureTheirs() => SharpMeasurer.MeasureAdvance(_marks, _theirs).Height;

    [Benchmark(Description = "OneImage: accents paragraph as path")]
    [BenchmarkCategory("marks")]
    public int MarksPathOurs() => TextBuilder.GeneratePath(_marks, _ours).Verbs.Length;

    [Benchmark(Description = "ImageSharp.Drawing: accents paragraph as path")]
    [BenchmarkCategory("marks")]
    public int MarksPathTheirs() => SixLabors.ImageSharp.Drawing.TextBuilder.GenerateGlyphs(_marks, _theirs).Bounds.GetHashCode();
}
