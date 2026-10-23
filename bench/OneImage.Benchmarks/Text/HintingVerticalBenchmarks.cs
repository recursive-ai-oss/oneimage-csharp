using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Ours = OneImage;
using OursMeasurer = OneImage.Text.TextMeasurer;
using SharpMeasurer = SixLabors.Fonts.TextMeasurer;
using OursOptions = OneImage.Text.TextOptions;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Text;

/// <summary>
/// Hinted drawing (a Latin paragraph of about 440 characters at 12 px, hinting Standard, wrapped at 600 px), vertical measuring (a
/// Japanese paragraph with some Latin in mixed right-to-left columns) and COLR version 1 drawing, against SixLabors.Fonts 2.1.3 and
/// ImageSharp.Drawing 2.1.7. ImageSharp has no COLR version 1 support, so that row is ours alone. Drawing is onto a 1000 x 1000
/// RGBA image that is cleared first; OneImage draws on a Canvas in place, as ImageSharp mutates its image.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class HintingVerticalBenchmarks : IDisposable
{
    private const int Size = 1000;
    private const string Latin = "Sphinx of black quartz, judge my vow. Pack my box with five dozen liquor jugs. How vexingly quick daft zebras jump! The five boxing wizards jump quickly. Sphinx of black quartz, judge my vow. Pack my box with five dozen liquor jugs. How vexingly quick daft zebras jump! The five boxing wizards jump quickly. Sphinx of black quartz, judge my vow. Pack my box with five dozen liquor jugs.";
    private const string Japanese = "\u65e5\u672c\u8a9e\u306e\u6587\u7ae0\u3092\u7e26\u306b\u4e26\u3079\u307e\u3059 ABC \u65e5\u672c\u8a9e\u306e\u6587\u7ae0\u3092\u7e26\u306b\u4e26\u3079\u307e\u3059 123 \u65e5\u672c\u8a9e\u306e\u6587\u7ae0\u3092\u7e26\u306b\u4e26\u3079\u307e\u3059\u3002";

    private OursOptions _hinted = null!;
    private OursOptions _plain = null!;
    private RichTextOptions _theirsHinted = null!;
    private OursOptions _verticalOurs = null!;
    private RichTextOptions _verticalTheirs = null!;
    private OursOptions _color = null!;
    private string _colorText = null!;
    private Ours.Canvas _canvas = null!;
    private SharpRgba _sharp = null!;
    private readonly Ours.Rgba32 _white = new(255, 255, 255, 255);
    private readonly Ours.Rgba32 _black = new(0, 0, 0, 255);

    [GlobalSetup]
    public void Setup()
    {
        string root = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(HintingVerticalBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        string fonts = Path.Combine(root, "tests", "testdata", "fonts");
        string hinted = Path.Combine(fonts, "NotoSans-Hinted-Subset.ttf");
        string cjk = Path.Combine(fonts, "NotoSansCJK-Subset.otf");
        string colr = Path.Combine(fonts, "ColrV1-TestGlyphs.ttf");

        var ours = new OneImage.Text.FontCollection().Add(hinted).CreateFont(12);
        _hinted = new OursOptions(ours) { Origin = new System.Numerics.Vector2(20, 20), WrappingLength = 600, HintingMode = HintingMode.Standard };
        _plain = new OursOptions(ours) { Origin = new System.Numerics.Vector2(20, 20), WrappingLength = 600 };
        var theirs = new SixLabors.Fonts.FontCollection().Add(hinted, CultureInfo.InvariantCulture).CreateFont(12);
        _theirsHinted = new RichTextOptions(theirs) { Origin = new System.Numerics.Vector2(20, 20), WrappingLength = 600, HintingMode = SixLabors.Fonts.HintingMode.Standard };

        _verticalOurs = new OursOptions(new OneImage.Text.FontCollection().Add(cjk).CreateFont(24)) { LayoutMode = OneImage.Text.LayoutMode.VerticalMixedRightLeft, WrappingLength = 300, Origin = new System.Numerics.Vector2(500, 20) };
        _verticalTheirs = new RichTextOptions(new SixLabors.Fonts.FontCollection().Add(cjk, CultureInfo.InvariantCulture).CreateFont(24)) { LayoutMode = SixLabors.Fonts.LayoutMode.VerticalMixedRightLeft, WrappingLength = 300, Origin = new System.Numerics.Vector2(500, 20) };

        _color = new OursOptions(new OneImage.Text.FontCollection().Add(colr).CreateFont(64)) { Origin = new System.Numerics.Vector2(20, 20), WrappingLength = 900 };
        var sb = new System.Text.StringBuilder();
        for (int cp = 0xF0000; cp < 0xF0400; cp += 17)
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }

        _colorText = sb.ToString();
        _canvas = new Ours.Canvas(Size, Size, Ours.PixelFormat.Rgba8);
        _sharp = new SharpRgba(Size, Size);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _canvas?.Dispose();
        _sharp?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "OneImage: draw Latin paragraph 12 px, hinting Standard")]
    public Ours.Rgba32 HintedOurs()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(Latin, _hinted, _black);
        return _canvas[100, 30];
    }

    [Benchmark(Description = "ImageSharp.Drawing: draw Latin paragraph 12 px, HintingMode.Standard")]
    public SixLabors.ImageSharp.PixelFormats.Rgba32 HintedTheirs()
    {
        _sharp.Mutate(c => c.Clear(SixLabors.ImageSharp.Color.White).DrawText(_theirsHinted, Latin, SixLabors.ImageSharp.Color.Black));
        return _sharp[100, 30];
    }

    [Benchmark(Description = "OneImage: draw Latin paragraph 12 px, hinting None")]
    public Ours.Rgba32 UnhintedOurs()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(Latin, _plain, _black);
        return _canvas[100, 30];
    }

    [Benchmark(Description = "OneImage: measure vertical mixed Japanese paragraph (advance)")]
    public float VerticalMeasureOurs() => OursMeasurer.MeasureAdvance(Japanese, _verticalOurs).Width;

    [Benchmark(Description = "SixLabors.Fonts: measure vertical mixed Japanese paragraph (advance)")]
    public float VerticalMeasureTheirs() => SharpMeasurer.MeasureAdvance(Japanese, _verticalTheirs).Width;

    [Benchmark(Description = "OneImage: draw COLR v1 glyphs 64 px (canvas; no ImageSharp equivalent)")]
    public Ours.Rgba32 ColorV1Ours()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(_colorText, _color, _black);
        return _canvas[100, 30];
    }
}
