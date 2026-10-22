using System.IO;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using OneImage.Svg;
using SkiaSharp;
using Svg.Skia;

namespace OneImage.Benchmarks;

/// <summary>
/// Markers and blending: a 1000 x 800 drawing of 250 paths of 25 vertices with arrow, dot and square markers (<c>orient</c>, viewBox,
/// stroke-width units, context paint) and 60 groups blended with <c>mix-blend-mode</c> and <c>isolation</c>, and a polyline of 10,000
/// vertices with a marker on every vertex (from <c>tools/gen_svg_markers.py</c>), both drawn at 1024 px wide. "load + render" parses
/// and draws, "render" draws a document that was loaded before. Svg.Skia is used through its public API with SkiaSharp as the rasterizer.
/// </summary>
[MemoryDiagnoser]
public class SvgMarkerBenchmarks
{
    private byte[] _mixed = null!;
    private byte[] _polyline = null!;
    private string _mixedText = null!;
    private string _polylineText = null!;
    private LoadOptions _options = null!;
    private Image _mixedLoaded = null!;
    private Image _polylineLoaded = null!;
    private SKPicture _mixedPicture = null!;
    private SKPicture _polylinePicture = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = CustomAttributeExtensions.GetCustomAttributes<AssemblyMetadataAttribute>(typeof(SvgMarkerBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;
        string dir = Path.Combine(root, "tests", "testdata", "svg");
        _mixed = File.ReadAllBytes(Path.Combine(dir, "markers.svg"));
        _polyline = File.ReadAllBytes(Path.Combine(dir, "markers-10k.svg"));
        _mixedText = System.Text.Encoding.UTF8.GetString(_mixed);
        _polylineText = System.Text.Encoding.UTF8.GetString(_polyline);
        _options = new LoadOptions { Configuration = new ImageConfiguration().Add(SvgFormat.Instance) };
        _mixedLoaded = Image.Load(_mixed, _options);
        _polylineLoaded = Image.Load(_polyline, _options);
        _mixedPicture = Parse(_mixedText);
        _polylinePicture = Parse(_polylineText);
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

    [Benchmark(Description = "markers+blend -> 1024, OneImage: load + render")]
    public Rgba32 MixedLoadRender() => Image.Load(_mixed, _options).Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "markers+blend -> 1024, OneImage: render")]
    public Rgba32 MixedRender() => _mixedLoaded.Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "markers+blend -> 1024, Svg.Skia: load + render")]
    public int MixedSkiaLoadRender() => Draw(Parse(_mixedText), 1024);

    [Benchmark(Description = "markers+blend -> 1024, Svg.Skia: render")]
    public int MixedSkiaRender() => Draw(_mixedPicture, 1024);

    [Benchmark(Description = "10k-vertex polyline with markers -> 1024, OneImage: load + render")]
    public Rgba32 PolylineLoadRender() => Image.Load(_polyline, _options).Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "10k-vertex polyline with markers -> 1024, OneImage: render")]
    public Rgba32 PolylineRender() => _polylineLoaded.Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "10k-vertex polyline with markers -> 1024, Svg.Skia: load + render")]
    public int PolylineSkiaLoadRender() => Draw(Parse(_polylineText), 1024);

    [Benchmark(Description = "10k-vertex polyline with markers -> 1024, Svg.Skia: render")]
    public int PolylineSkiaRender() => Draw(_polylinePicture, 1024);
}
