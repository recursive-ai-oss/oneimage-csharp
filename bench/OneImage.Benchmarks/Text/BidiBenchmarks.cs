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
/// A mixed Arabic and English paragraph (about 330 characters, numbers and brackets included) at 24 px, wrapped at 600 px, against
/// SixLabors.Fonts 2.1.3 and ImageSharp.Drawing 2.1.7 with one merged Noto Sans / Arabic / Hebrew subset. Both libraries pick the
/// paragraph direction from the text. Measuring and drawing onto a 1000 x 1000 RGBA image; OneImage draws on a Canvas in place, as
/// ImageSharp mutates its image, and both rows clear it first.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class BidiBenchmarks : IDisposable
{
    private const int Size = 1000;
    private const string Paragraph = "\u0645\u0631\u062D\u0628\u0627 \u0628\u0643\u0645 \u0641\u064A \u0647\u0630\u0627 \u0627\u0644\u0645\u0648\u0642\u0639 \u0627\u0644\u062C\u062F\u064A\u062F \u0627\u0644\u0630\u064A \u064A\u0639\u0631\u0636 \u0627\u0644\u0643\u062A\u0628 \u0648\u0627\u0644\u0645\u062C\u0644\u0627\u062A \u0648\u0627\u0644\u0635\u0648\u0631 \u0645\u0646 \u062C\u0645\u064A\u0639 \u0623\u0646\u062D\u0627\u0621 \u0627\u0644\u0639\u0627\u0644\u0645 \u0648\u0642\u062F \u0623\u0636\u0641\u0646\u0627 \u062E\u062F\u0645\u0627\u062A \u0643\u062B\u064A\u0631\u0629 \u0644\u0644\u0642\u0631\u0627\u0621 Welcome to our new shop: we sell 120 books, 45 magazines and 3,000 prints (see www.example.com) every day of the week. \u0645\u0631\u062D\u0628\u0627 \u0628\u0643\u0645 \u0641\u064A \u0647\u0630\u0627 \u0627\u0644\u0645\u0648\u0642\u0639 \u0627\u0644\u062C\u062F\u064A\u062F \u0627\u0644\u0630\u064A \u064A\u0639\u0631\u0636 \u0627\u0644\u0643\u062A\u0628 \u0648\u0627\u0644\u0645\u062C\u0644\u0627\u062A \u0648\u0627\u0644\u0635\u0648\u0631 \u0645\u0646 \u062C\u0645\u064A\u0639 \u0623\u0646\u062D\u0627\u0621 \u0627\u0644\u0639\u0627\u0644\u0645 \u0648\u0642\u062F \u0623\u0636\u0641\u0646\u0627 \u062E\u062F\u0645\u0627\u062A \u0643\u062B\u064A\u0631\u0629 \u0644\u0644\u0642\u0631\u0627\u0621 Welcome to our new shop: we sell 120 books, 45 magazines and 3,000 prints (see www.example.com) every day of the week.";

    private OursOptions _ours = null!;
    private RichTextOptions _theirs = null!;
    private Ours.Canvas _canvas = null!;
    private SharpRgba _sharp = null!;
    private readonly Ours.Rgba32 _white = new(255, 255, 255, 255);
    private readonly Ours.Rgba32 _black = new(0, 0, 0, 255);

    [GlobalSetup]
    public void Setup()
    {
        string root = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(BidiBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        string path = Path.Combine(root, "tests", "testdata", "fonts", "NotoSans-Bidi-Subset.ttf");
        var ours = new OneImage.Text.FontCollection().Add(path).CreateFont(24);
        var theirs = new SixLabors.Fonts.FontCollection().Add(path, CultureInfo.InvariantCulture).CreateFont(24);
        _ours = new OursOptions(ours) { Origin = new System.Numerics.Vector2(50, 50), WrappingLength = 600 };
        _theirs = new RichTextOptions(theirs) { Origin = new System.Numerics.Vector2(50, 50), WrappingLength = 600 };
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

    [Benchmark(Description = "OneImage: measure mixed Arabic/English paragraph (advance)")]
    public float MeasureOurs() => OursMeasurer.MeasureAdvance(Paragraph, _ours).Height;

    [Benchmark(Description = "SixLabors.Fonts: measure mixed Arabic/English paragraph (advance)")]
    public float MeasureTheirs() => SharpMeasurer.MeasureAdvance(Paragraph, _theirs).Height;

    [Benchmark(Description = "OneImage: mixed Arabic/English paragraph as path")]
    public int PathOurs() => TextBuilder.GeneratePath(Paragraph, _ours).Verbs.Length;

    [Benchmark(Description = "ImageSharp.Drawing: mixed Arabic/English paragraph as path")]
    public int PathTheirs() => SixLabors.ImageSharp.Drawing.TextBuilder.GenerateGlyphs(Paragraph, _theirs).Bounds.GetHashCode();

    [Benchmark(Description = "OneImage: draw mixed Arabic/English paragraph 24 px (canvas)")]
    public Ours.Rgba32 DrawOurs()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(Paragraph, _ours, _black);
        return _canvas[500, 100];
    }

    [Benchmark(Description = "ImageSharp.Drawing: draw mixed Arabic/English paragraph 24 px")]
    public SixLabors.ImageSharp.PixelFormats.Rgba32 DrawTheirs()
    {
        _sharp.Mutate(c => c.Clear(SixLabors.ImageSharp.Color.White).DrawText(_theirs, Paragraph, SixLabors.ImageSharp.Color.Black));
        return _sharp[500, 100];
    }
}
