using System.IO;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using OneImage.Svg;
using SkiaSharp;
using Svg.Skia;

namespace OneImage.Benchmarks;

/// <summary>
/// Paint servers: a 1000 x 800 drawing of about 600 shapes filled and stroked with 56 gradients (both unit systems, transforms, spread
/// methods, focal points, opacity stops) and one of about 250 shapes filled with 26 patterns (rotated, skewed, nested, bounding-box and
/// view-box tiles), both drawn at 1024 px wide (from <c>tools/gen_svg_paint.py</c>). "load + render" parses and draws, "render" draws a
/// document that was loaded before. Svg.Skia is used through its public API with SkiaSharp as the rasterizer.
/// </summary>
[MemoryDiagnoser]
public class SvgPaintBenchmarks
{
    private byte[] _gradients = null!;
    private byte[] _patterns = null!;
    private string _gradientsText = null!;
    private string _patternsText = null!;
    private LoadOptions _options = null!;
    private Image _gradientsLoaded = null!;
    private Image _patternsLoaded = null!;
    private SKPicture _gradientsPicture = null!;
    private SKPicture _patternsPicture = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = CustomAttributeExtensions.GetCustomAttributes<AssemblyMetadataAttribute>(typeof(SvgPaintBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        string dir = Path.Combine(root, "tests", "testdata", "svg");
        _gradients = File.ReadAllBytes(Path.Combine(dir, "gradients.svg"));
        _patterns = File.ReadAllBytes(Path.Combine(dir, "patterns.svg"));
        _gradientsText = System.Text.Encoding.UTF8.GetString(_gradients);
        _patternsText = System.Text.Encoding.UTF8.GetString(_patterns);
        _options = new LoadOptions { Configuration = new ImageConfiguration().Add(SvgFormat.Instance) };
        _gradientsLoaded = Image.Load(_gradients, _options);
        _patternsLoaded = Image.Load(_patterns, _options);
        _gradientsPicture = Parse(_gradientsText);
        _patternsPicture = Parse(_patternsText);
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

    [Benchmark(Description = "gradients -> 1024, OneImage: load + render")]
    public Rgba32 GradientsLoadRender() => Image.Load(_gradients, _options).Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "gradients -> 1024, OneImage: render")]
    public Rgba32 GradientsRender() => _gradientsLoaded.Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "gradients -> 1024, Svg.Skia: load + render")]
    public int GradientsSkiaLoadRender() => Draw(Parse(_gradientsText), 1024);

    [Benchmark(Description = "gradients -> 1024, Svg.Skia: render")]
    public int GradientsSkiaRender() => Draw(_gradientsPicture, 1024);

    [Benchmark(Description = "patterns -> 1024, OneImage: load + render")]
    public Rgba32 PatternsLoadRender() => Image.Load(_patterns, _options).Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "patterns -> 1024, OneImage: render")]
    public Rgba32 PatternsRender() => _patternsLoaded.Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "patterns -> 1024, Svg.Skia: load + render")]
    public int PatternsSkiaLoadRender() => Draw(Parse(_patternsText), 1024);

    [Benchmark(Description = "patterns -> 1024, Svg.Skia: render")]
    public int PatternsSkiaRender() => Draw(_patternsPicture, 1024);
}
