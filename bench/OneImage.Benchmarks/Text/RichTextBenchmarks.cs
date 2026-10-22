using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Text;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Ours = OneImage;
using OursOptions = OneImage.Text.TextOptions;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Text;

/// <summary>
/// Rich text, font fallback and text on a path against SixLabors.Fonts 2.1.3 and ImageSharp.Drawing 2.1.7, with the same font files for
/// both. "Path" rows lay the text out and build the glyph outlines; "draw" rows paint them onto a 1000 × 1000 RGBA image (OneImage on a
/// Canvas in place, as ImageSharp mutates its image).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class RichTextBenchmarks : IDisposable
{
    private const int Size = 1000;

    private const string Rich =
        "Typography is the craft of arranging type so that written language is legible, readable and appealing when displayed. " +
        "The arrangement of type involves selecting typefaces, point sizes, line lengths, line spacing and letter spacing, and " +
        "adjusting the space between pairs of letters. The term typography is also applied to the style and appearance of letters.";

    private const string Fallback =
        "Typography \u3042\u3044\u3046 is the craft of arranging type \u3048\u304A so that written language is legible, readable " +
        "\u304B\u304D\u304F and appealing when displayed. The arrangement of type involves selecting typefaces, point sizes, line " +
        "lengths \u3053\u3055\u3057 and line spacing, and adjusting the space between pairs of letters. \u3059\u305B\u305D";

    private const string OnPath = "Typography is the craft of arranging type so that written language is legible and appealing.";

    private OursOptions _richOurs = null!;
    private RichTextOptions _richTheirs = null!;
    private OursOptions _fallbackOurs = null!;
    private RichTextOptions _fallbackTheirs = null!;
    private OursOptions _pathOurs = null!;
    private RichTextOptions _pathTheirs = null!;
    private IPath _theirRoute = null!;
    private Ours.Canvas _canvas = null!;
    private SharpRgba _sharp = null!;
    private readonly Ours.Rgba32 _white = new(255, 255, 255, 255);
    private readonly Ours.Rgba32 _black = new(0, 0, 0, 255);

    [GlobalSetup]
    public void Setup()
    {
        string regular = FindFont("NotoSans-Subset-Regular.ttf");
        string bold = FindFont("NotoSans-Subset-Bold-kern.ttf");
        string cjk = FindFont("NotoSansCJK-Subset.otf");

        var fonts = new OneImage.Text.FontCollection();
        FontFamily noto = fonts.Add(regular);
        fonts.Add(bold);
        FontFamily cjkFamily = fonts.Add(cjk);
        var sharpRegular = new SixLabors.Fonts.FontCollection().Add(regular, CultureInfo.InvariantCulture);
        var sharpBoldCollection = new SixLabors.Fonts.FontCollection();
        var sharpBold = sharpBoldCollection.Add(bold, CultureInfo.InvariantCulture);
        var sharpCjk = new SixLabors.Fonts.FontCollection().Add(cjk, CultureInfo.InvariantCulture);

        // Rich text: every word gets a run; a mix of bold, large, raised and underlined words.
        _richOurs = new OursOptions(noto.CreateFont(22)) { Origin = new Vector2(30, 30), WrappingLength = 900 };
        _richTheirs = new RichTextOptions(sharpRegular.CreateFont(22)) { Origin = new Vector2(30, 30), WrappingLength = 900 };
        var theirRuns = new System.Collections.Generic.List<RichTextRun>();
        int pos = 0;
        int word = 0;
        foreach (string w in Rich.Split(' '))
        {
            int end = pos + w.Length;
            switch (word % 5)
            {
                case 0:
                    _richOurs.TextRuns.Add(new TextRun(pos, end) { Font = noto.CreateFont(22, FontStyle.Bold), TextDecorations = TextDecorations.Underline });
                    theirRuns.Add(new RichTextRun { Start = pos, End = end, Font = sharpBold.CreateFont(22, SixLabors.Fonts.FontStyle.Bold), TextDecorations = SixLabors.Fonts.TextDecorations.Underline });
                    break;
                case 2:
                    _richOurs.TextRuns.Add(new TextRun(pos, end) { Font = noto.CreateFont(32), TextDecorations = TextDecorations.Strikeout });
                    theirRuns.Add(new RichTextRun { Start = pos, End = end, Font = sharpRegular.CreateFont(32), TextDecorations = SixLabors.Fonts.TextDecorations.Strikeout });
                    break;
                case 3:
                    _richOurs.TextRuns.Add(new TextRun(pos, end) { TextAttributes = TextAttributes.Superscript, TextDecorations = TextDecorations.Overline });
                    theirRuns.Add(new RichTextRun { Start = pos, End = end, TextAttributes = SixLabors.Fonts.TextAttributes.Superscript, TextDecorations = SixLabors.Fonts.TextDecorations.Overline });
                    break;
            }

            pos = end + 1;
            word++;
        }

        _richTheirs.TextRuns = theirRuns;

        _fallbackOurs = new OursOptions(noto.CreateFont(24)) { Origin = new Vector2(30, 30), WrappingLength = 900 };
        _fallbackOurs.FallbackFontFamilies.Add(cjkFamily);
        _fallbackTheirs = new RichTextOptions(sharpRegular.CreateFont(24)) { Origin = new Vector2(30, 30), WrappingLength = 900, FallbackFontFamilies = [sharpCjk] };

        // Text along a wavy line of 200 segments.
        var points = new Vector2[200];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new Vector2(30 + (i * 4.5f), 400 + (float)(120 * Math.Sin(i * 0.05)));
        }

        _pathOurs = new OursOptions(noto.CreateFont(28)) { Path = Ours.Drawing.Path.Polyline(points) };
        _theirRoute = new SixLabors.ImageSharp.Drawing.Path(new LinearLineSegment(points[0], points[1], [.. points.Skip(2).Select(p => (SixLabors.ImageSharp.PointF)p)]));
        _pathTheirs = new RichTextOptions(sharpRegular.CreateFont(28)) { Path = _theirRoute };

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

    private static string FindFont(string name)
    {
        string root = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(RichTextBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        return System.IO.Path.Combine(root, "tests", "testdata", "fonts", name);
    }

    [Benchmark(Description = "OneImage: rich text with decorations, as path")]
    [BenchmarkCategory("rich")]
    public int RichPathOurs() => OneImage.Text.TextBuilder.GeneratePath(Rich, _richOurs).Verbs.Length;

    [Benchmark(Description = "ImageSharp.Drawing: rich text with decorations, as path")]
    [BenchmarkCategory("rich")]
    public int RichPathTheirs() => SixLabors.ImageSharp.Drawing.TextBuilder.GenerateGlyphs(Rich, _richTheirs).Bounds.GetHashCode();

    [Benchmark(Description = "OneImage: rich text with decorations, draw on 1000x1000")]
    [BenchmarkCategory("rich")]
    public Ours.Rgba32 RichDrawOurs()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(Rich, _richOurs, _black);
        return _canvas[500, 100];
    }

    [Benchmark(Description = "ImageSharp.Drawing: rich text with decorations, draw on 1000x1000")]
    [BenchmarkCategory("rich")]
    public SixLabors.ImageSharp.PixelFormats.Rgba32 RichDrawTheirs()
    {
        _sharp.Mutate(c => c.Clear(SixLabors.ImageSharp.Color.White).DrawText(_richTheirs, Rich, SixLabors.ImageSharp.Color.Black));
        return _sharp[500, 100];
    }

    [Benchmark(Description = "OneImage: fallback text, as path")]
    [BenchmarkCategory("fallback")]
    public int FallbackPathOurs() => OneImage.Text.TextBuilder.GeneratePath(Fallback, _fallbackOurs).Verbs.Length;

    [Benchmark(Description = "ImageSharp.Drawing: fallback text, as path")]
    [BenchmarkCategory("fallback")]
    public int FallbackPathTheirs() => SixLabors.ImageSharp.Drawing.TextBuilder.GenerateGlyphs(Fallback, _fallbackTheirs).Bounds.GetHashCode();

    [Benchmark(Description = "OneImage: fallback text, draw on 1000x1000")]
    [BenchmarkCategory("fallback")]
    public Ours.Rgba32 FallbackDrawOurs()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(Fallback, _fallbackOurs, _black);
        return _canvas[500, 100];
    }

    [Benchmark(Description = "ImageSharp.Drawing: fallback text, draw on 1000x1000")]
    [BenchmarkCategory("fallback")]
    public SixLabors.ImageSharp.PixelFormats.Rgba32 FallbackDrawTheirs()
    {
        _sharp.Mutate(c => c.Clear(SixLabors.ImageSharp.Color.White).DrawText(_fallbackTheirs, Fallback, SixLabors.ImageSharp.Color.Black));
        return _sharp[500, 100];
    }

    [Benchmark(Description = "OneImage: text on a path (200 segments), as path")]
    [BenchmarkCategory("onpath")]
    public int OnPathPathOurs() => OneImage.Text.TextBuilder.GeneratePath(OnPath, _pathOurs).Verbs.Length;

    [Benchmark(Description = "ImageSharp.Drawing: text on a path (200 segments), as path")]
    [BenchmarkCategory("onpath")]
    public int OnPathPathTheirs() => SixLabors.ImageSharp.Drawing.TextBuilder.GenerateGlyphs(OnPath, _theirRoute, _pathTheirs).Bounds.GetHashCode();

    [Benchmark(Description = "OneImage: text on a path, draw on 1000x1000")]
    [BenchmarkCategory("onpath")]
    public Ours.Rgba32 OnPathDrawOurs()
    {
        _canvas.Clear(_white);
        _canvas.DrawText(OnPath, _pathOurs, _black);
        return _canvas[500, 400];
    }

    [Benchmark(Description = "ImageSharp.Drawing: text on a path, draw on 1000x1000")]
    [BenchmarkCategory("onpath")]
    public SixLabors.ImageSharp.PixelFormats.Rgba32 OnPathDrawTheirs()
    {
        _sharp.Mutate(c => c.Clear(SixLabors.ImageSharp.Color.White).DrawText(_pathTheirs, OnPath, SixLabors.ImageSharp.Color.Black));
        return _sharp[500, 400];
    }
}
