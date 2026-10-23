using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using OneImage.Svg;
using SkiaSharp;
using Svg.Skia;

namespace OneImage.Benchmarks;

/// <summary>
/// Filters: a 1000 x 1000 drawing of 400 shapes under one group blurred with a standard deviation of 8 (units) and the same group with a
/// drop shadow, both drawn at 1024 px wide, and a 24 x 24 icon with a blur filter drawn at 256 px. "load + render" parses and draws,
/// "render" draws a document loaded before; Svg.Skia is used through its public API. The documents are generated here.
/// </summary>
[MemoryDiagnoser]
public class SvgFilterBenchmarks
{
    private string _blurText = null!;
    private string _shadowText = null!;
    private string _iconText = null!;
    private LoadOptions _options = null!;
    private Image _blurLoaded = null!;
    private Image _shadowLoaded = null!;
    private Image _iconLoaded = null!;
    private SKPicture _blurPicture = null!;
    private SKPicture _shadowPicture = null!;
    private SKPicture _iconPicture = null!;

    private static string Shapes()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        for (int i = 0; i < 400; i++)
        {
            int x = (i * 97) % 940;
            int y = (i * 61) % 940;
            sb.Append(string.Create(inv, $"<rect x='{x}' y='{y}' width='{30 + (i % 40)}' height='{30 + (i % 31)}' rx='6' fill='rgb({(i * 7) % 256},{(i * 13) % 256},{(i * 29) % 256})' fill-opacity='0.8'/>"));
        }

        return sb.ToString();
    }

    internal static string BlurDocument() =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1000 1000'><rect width='1000' height='1000' fill='#fafaf5'/>"
        + "<filter id='b' x='-0.05' y='-0.05' width='1.1' height='1.1'><feGaussianBlur stdDeviation='8'/></filter>"
        + "<g filter='url(#b)'>" + Shapes() + "</g></svg>";

    internal static string ShadowDocument() =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1000 1000'><rect width='1000' height='1000' fill='#fafaf5'/>"
        + "<filter id='s' x='-0.05' y='-0.05' width='1.1' height='1.1'><feDropShadow dx='10' dy='12' stdDeviation='8' flood-color='#000' flood-opacity='0.5'/></filter>"
        + "<g filter='url(#s)'>" + Shapes() + "</g></svg>";

    internal static string IconDocument() =>
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><filter id='b'><feGaussianBlur stdDeviation='1.2'/></filter>"
        + "<circle cx='12' cy='12' r='8' fill='#2e8b57' filter='url(#b)'/><path d='M7 12l3 3 7-7' fill='none' stroke='#fff' stroke-width='2'/></svg>";

    [GlobalSetup]
    public void Setup()
    {
        _blurText = BlurDocument();
        _shadowText = ShadowDocument();
        _iconText = IconDocument();
        _options = new LoadOptions { Configuration = new ImageConfiguration().Add(SvgFormat.Instance) };
        _blurLoaded = Image.Load(Encoding.UTF8.GetBytes(_blurText), _options);
        _shadowLoaded = Image.Load(Encoding.UTF8.GetBytes(_shadowText), _options);
        _iconLoaded = Image.Load(Encoding.UTF8.GetBytes(_iconText), _options);
        _blurPicture = Parse(_blurText);
        _shadowPicture = Parse(_shadowText);
        _iconPicture = Parse(_iconText);
    }

    private static SKPicture Parse(string text) =>
        new SKSvg().FromSvg(text) ?? throw new InvalidOperationException("Svg.Skia could not read the document");

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

    private Rgba32 Render(string text, int width) => Image.Load(Encoding.UTF8.GetBytes(text), _options).Resize(width: width).ToArray<Rgba32>()[0];

    [Benchmark(Description = "blur sigma 8 -> 1024, OneImage: load + render")]
    public Rgba32 BlurLoadRender() => Render(_blurText, 1024);

    [Benchmark(Description = "blur sigma 8 -> 1024, OneImage: render")]
    public Rgba32 BlurRender() => _blurLoaded.Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "blur sigma 8 -> 1024, Svg.Skia: render")]
    public int BlurSkiaRender() => Draw(_blurPicture, 1024);

    [Benchmark(Description = "drop shadow -> 1024, OneImage: load + render")]
    public Rgba32 ShadowLoadRender() => Render(_shadowText, 1024);

    [Benchmark(Description = "drop shadow -> 1024, OneImage: render")]
    public Rgba32 ShadowRender() => _shadowLoaded.Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "drop shadow -> 1024, Svg.Skia: render")]
    public int ShadowSkiaRender() => Draw(_shadowPicture, 1024);

    [Benchmark(Description = "icon with blur -> 256, OneImage: load + render")]
    public Rgba32 IconLoadRender() => Render(_iconText, 256);

    [Benchmark(Description = "icon with blur -> 256, OneImage: render")]
    public Rgba32 IconRender() => _iconLoaded.Resize(width: 256).ToArray<Rgba32>()[0];

    [Benchmark(Description = "icon with blur -> 256, Svg.Skia: render")]
    public int IconSkiaRender() => Draw(_iconPicture, 256);
}
