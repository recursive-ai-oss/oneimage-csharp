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
}
