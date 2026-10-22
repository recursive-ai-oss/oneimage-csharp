using System.Globalization;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Attributes;
using OneImage.Svg;
using SkiaSharp;
using Svg.Skia;

namespace OneImage.Benchmarks;

/// <summary>
/// A clip- and mask-heavy 1000 x 1000 document drawn at 1024 px wide: 40 groups of about 30 shapes, each clipped by a star, a ring or a
/// nested clip (user-space and bounding-box units), 40 groups behind luminance and alpha masks (bounding-box and user-space units, a
/// mask with its own mask), and 20 opacity groups under both. The document is generated here, with fixed numbers.
/// </summary>
[MemoryDiagnoser]
public class SvgClipMaskBenchmarks
{
    private string _text = null!;
    private byte[] _bytes = null!;
    private LoadOptions _options = null!;
    private Image _loaded = null!;
    private SKPicture _picture = null!;

    internal static string Document()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1000 1000'>");
        sb.Append("<rect width='1000' height='1000' fill='#fafaf5'/>");
        sb.Append("<clipPath id='star'><path d='M 50 5 L 61 38 L 95 38 L 67 58 L 78 92 L 50 71 L 22 92 L 33 58 L 5 38 L 39 38 Z' clip-rule='evenodd'/></clipPath>");
        sb.Append("<clipPath id='ring'><circle cx='50' cy='50' r='46'/><circle cx='50' cy='50' r='24' clip-rule='evenodd'/></clipPath>");
        sb.Append("<clipPath id='box' clipPathUnits='objectBoundingBox'><rect x='0.1' y='0.1' width='0.8' height='0.8' rx='0.2'/></clipPath>");
        sb.Append("<clipPath id='nest' clip-path='url(#star)'><circle cx='50' cy='50' r='40'/></clipPath>");
        sb.Append("<mask id='lum' maskUnits='objectBoundingBox'><rect width='1000' height='1000' fill='#303030'/><circle cx='50' cy='50' r='34' fill='white'/></mask>");
        sb.Append("<mask id='alpha' mask-type='alpha' maskContentUnits='objectBoundingBox'><circle cx='0.5' cy='0.5' r='0.45' fill='black' fill-opacity='0.8'/></mask>");
        sb.Append("<mask id='chain' mask='url(#alpha)' maskUnits='userSpaceOnUse' x='-50' y='-50' width='200' height='200'><rect width='100' height='100' fill='white'/></mask>");
        string[] clips = ["star", "ring", "box", "nest"];
        string[] masks = ["lum", "alpha", "chain"];
        for (int i = 0; i < 100; i++)
        {
            int cx = i % 10 * 100;
            int cy = i / 10 * 100;
            string attrs = i < 40 ? $"clip-path='url(#{clips[i % 4]})'"
                : i < 80 ? $"mask='url(#{masks[i % 3]})'"
                : $"opacity='0.8' clip-path='url(#{clips[i % 4]})' mask='url(#{masks[i % 3]})'";
            sb.Append(string.Create(inv, $"<g transform='translate({cx} {cy})' {attrs}>"));
            for (int k = 0; k < 30; k++)
            {
                int x = (k * 37) % 90;
                int y = (k * 53) % 90;
                sb.Append(string.Create(inv, $"<rect x='{x}' y='{y}' width='{8 + (k % 5)}' height='{8 + (k % 7)}' rx='2' fill='rgb({(i * 9) % 256},{(k * 8) % 256},{(i + k * 11) % 256})'/>"));
            }

            sb.Append("</g>");
        }

        return sb.Append("</svg>").ToString();
    }

    [GlobalSetup]
    public void Setup()
    {
        _text = Document();
        _bytes = Encoding.UTF8.GetBytes(_text);
        _options = new LoadOptions { Configuration = new ImageConfiguration().Add(SvgFormat.Instance) };
        _loaded = Image.Load(_bytes, _options);
        _picture = Parse(_text);
    }

    private static SKPicture Parse(string text) => new SKSvg().FromSvg(text) ?? throw new InvalidOperationException("Svg.Skia could not read the document");

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

    [Benchmark(Description = "clip+mask 1000 -> 1024, OneImage: load + render")]
    public Rgba32 LoadRender() => Image.Load(_bytes, _options).Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "clip+mask 1000 -> 1024, OneImage: render")]
    public Rgba32 Render() => _loaded.Resize(width: 1024).ToArray<Rgba32>()[0];

    [Benchmark(Description = "clip+mask 1000 -> 1024, Svg.Skia: load + render")]
    public int SkiaLoadRender() => Draw(Parse(_text), 1024);

    [Benchmark(Description = "clip+mask 1000 -> 1024, Svg.Skia: render")]
    public int SkiaRender() => Draw(_picture, 1024);
}
