using System.IO;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using OneImage.Svg;
using SkiaSharp;
using Svg.Skia;

namespace OneImage.Benchmarks;

/// <summary>
/// Loading and rendering static SVG: a 24 x 24 icon drawn at 256 px and a busy 1000 x 800 illustration (about 3,000 shapes,
/// transforms, dashed strokes, group opacity and symbol instances, from <c>tools/gen_svg_illustration.py</c>) drawn at 1024 px wide.
/// "load + render" parses the document and draws it; "render" draws a document that was loaded before. Svg.Skia is used through
/// its public API with SkiaSharp as the rasterizer.
/// </summary>
[MemoryDiagnoser]
public class SvgBenchmarks
{
    private byte[] _icon = null!;
    private byte[] _illustration = null!;
    private string _iconText = null!;
    private string _illustrationText = null!;
    private LoadOptions _options = null!;
    private Image _iconLoaded = null!;
    private Image _illustrationLoaded = null!;
    private SKPicture _iconPicture = null!;
    private SKPicture _illustrationPicture = null!;
    private byte[] _cssHeavy = null!;
    private byte[] _cssFree = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = CustomAttributeExtensions.GetCustomAttributes<AssemblyMetadataAttribute>(typeof(SvgBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        string dir = Path.Combine(root, "tests", "testdata", "svg");
        _icon = File.ReadAllBytes(Path.Combine(dir, "icon-24.svg"));
        _illustration = File.ReadAllBytes(Path.Combine(dir, "illustration.svg"));
        _iconText = System.Text.Encoding.UTF8.GetString(_icon);
        _illustrationText = System.Text.Encoding.UTF8.GetString(_illustration);
        _options = new LoadOptions { Configuration = new ImageConfiguration().Add(SvgFormat.Instance) };
        _iconLoaded = Image.Load(_icon, _options);
        _illustrationLoaded = Image.Load(_illustration, _options);
        (_cssHeavy, _cssFree) = CssDocuments();
        _iconPicture = Parse(_iconText);
        _illustrationPicture = Parse(_illustrationText);
    }

    private static SKPicture Parse(string text)
    {
        var svg = new SKSvg();
        return svg.FromSvg(text) ?? throw new InvalidOperationException("Svg.Skia could not read the document");
    }

    private static int Draw(SKPicture picture, int width)
    {
        float scale = width / picture.CullRect.Width;
        int height = (int)Math.Ceiling(picture.CullRect.Height * scale);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        return bitmap.GetPixel(width / 2, height / 2).Alpha;
    }

    [Benchmark(Description = "icon 24 -> 256, OneImage: load + render")]
    public Rgba32 IconLoadRender() => Image.Load(_icon, _options).Resize(256, 256, Fit.Stretch).ToArray<Rgba32>()[0];

    [Benchmark(Description = "icon 24 -> 256, OneImage: render")]
    public Rgba32 IconRender() => _iconLoaded.Resize(256, 256, Fit.Stretch).ToArray<Rgba32>()[0];

    [Benchmark(Description = "icon 24 -> 256, Svg.Skia: load + render")]
    public int IconSkiaLoadRender() => Draw(Parse(_iconText), 256);

    [Benchmark(Description = "icon 24 -> 256, Svg.Skia: render")]
    public int IconSkiaRender() => Draw(_iconPicture, 256);

    [Benchmark(Description = "illustration -> 1024, OneImage: load + render")]
    public Rgba32 IllustrationLoadRender() => Image.Load(_illustration, _options).Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "illustration -> 1024, OneImage: render")]
    public Rgba32 IllustrationRender() => _illustrationLoaded.Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "illustration -> 1024, Svg.Skia: load + render")]
    public int IllustrationSkiaLoadRender() => Draw(Parse(_illustrationText), 1024);

    [Benchmark(Description = "illustration -> 1024, Svg.Skia: render")]
    public int IllustrationSkiaRender() => Draw(_illustrationPicture, 1024);

    // 20,000 shapes in 400 nested groups of classes, with 400 style rules over them (type, class, id, attribute, child, descendant and
    // structural selectors), against the same drawing with every declaration written on the element.
    private static (byte[] WithSheet, byte[] Inline) CssDocuments()
    {
        var rules = new System.Text.StringBuilder();
        var withSheet = new System.Text.StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1000 1000' width='1000' height='1000'><style>");
        var inline = new System.Text.StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1000 1000' width='1000' height='1000'>");
        for (int c = 0; c < 100; c++)
        {
            rules.Append(FormattableString.Invariant($".c{c} {{ fill: #{(c * 2654435 & 0xFFFFFF):x6}; stroke: #333 }} g.row{c} > rect:nth-child(odd) {{ opacity: 0.{1 + (c % 9)} }} #r{c * 200} {{ stroke-width: 2 }} svg g.row{c} rect[data-k='{c % 7}'] {{ stroke: black }}\n"));
        }

        withSheet.Append(rules).Append("</style>");
        for (int g = 0; g < 100; g++)
        {
            withSheet.Append(FormattableString.Invariant($"<g class='row{g}'>"));
            inline.Append("<g>");
            for (int r = 0; r < 200; r++)
            {
                int id = (g * 200) + r;
                int x = r * 5 % 990;
                int y = g * 10 % 990;
                withSheet.Append(FormattableString.Invariant($"<rect id='r{id}' class='c{(g + r) % 100}' data-k='{r % 7}' x='{x}' y='{y}' width='4' height='4'/>"));
                string fill = $"#{(((g + r) % 100) * 2654435 & 0xFFFFFF):x6}";
                string opacity = r % 2 == 0 ? $" opacity='0.{1 + (g % 9)}'" : string.Empty;
                string sw = id % 200 == 0 && id / 200 < 100 ? " stroke-width='2'" : string.Empty;
                inline.Append(FormattableString.Invariant($"<rect x='{x}' y='{y}' width='4' height='4' fill='{fill}' stroke='#333'{opacity}{sw}/>"));
            }

            withSheet.Append("</g>");
            inline.Append("</g>");
        }

        withSheet.Append("</svg>");
        inline.Append("</svg>");
        return (System.Text.Encoding.UTF8.GetBytes(withSheet.ToString()), System.Text.Encoding.UTF8.GetBytes(inline.ToString()));
    }

    private Rgba32 FirstPixel(byte[] doc)
    {
        // One pixel is enough to make the load parse the document, run the cascade and build the render tree.
        var one = new Rgba32[1];
        Image.Load(doc, _options).CopyPixelsTo<Rgba32>(one, new ImageRect(0, 0, 1, 1));
        return one[0];
    }

    [Benchmark(Description = "CSS-heavy, 20k shapes + 400 rules: parse, cascade, render tree")]
    public Rgba32 CssHeavy() => FirstPixel(_cssHeavy);

    [Benchmark(Description = "same drawing without a style sheet: parse, render tree")]
    public Rgba32 CssFree() => FirstPixel(_cssFree);
}
