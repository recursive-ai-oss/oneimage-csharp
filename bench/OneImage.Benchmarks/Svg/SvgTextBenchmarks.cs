using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Attributes;
using OneImage.Svg;

namespace OneImage.Benchmarks;

/// <summary>
/// Text in SVG: a 800 x 1000 page of 40 lines, each a text element of several tspans (bold, colored, sized) with per-character
/// dx and rotate on every third line, and a block of text along a curve, drawn at 800 px wide. "load + render" parses, lays out and draws; "render" draws a
/// document loaded before. Both use the Noto Sans subsets that ship in the tests. The document is generated here. No other library is
/// compared: Svg.Skia takes its fonts from the system and would not set the same text.
/// </summary>
[MemoryDiagnoser]
public class SvgTextBenchmarks
{
    private string _text = null!;
    private LoadOptions _options = null!;
    private Image _loaded = null!;

    internal static string Document()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 800 1000' font-family='Noto Sans' font-size='16'><rect width='800' height='1000' fill='#fffff8'/>");
        sb.Append("<defs><path id='p' d='M 40 960 C 240 880 400 1040 760 940'/></defs>");
        for (int i = 0; i < 40; i++)
        {
            int y = 24 + (i * 22);
            string attrs = i % 3 == 0 ? " dx='0 1 0 2 0 1 0 2' rotate='0 3 -3 0 2 -2'" : string.Empty;
            sb.Append(string.Create(inv, $"<text x='20' y='{y}'{attrs}>Line {i}: <tspan font-weight='bold' fill='#a33'>the quick brown</tspan> fox <tspan font-size='20' fill='#36a'>jumps</tspan> over the lazy dog, AVA To fi ffl</text>"));
        }

        sb.Append("<text font-size='22' fill='#063'><textPath href='#p'>Text on a path: the quick brown fox jumps over the lazy dog, and then some more words to fill the curve</textPath></text></svg>");
        return sb.ToString();
    }

    [GlobalSetup]
    public void Setup()
    {
        string dir = Path.Combine(FindRoot(), "tests", "testdata", "fonts");
        var fonts = new SvgFontOptions { UseSystemFonts = false };
        fonts.AddFont(Path.Combine(dir, "NotoSans-Subset-Regular.ttf"));
        fonts.AddFont(Path.Combine(dir, "NotoSans-Subset-Bold-kern.ttf"));
        _text = Document();
        _options = new LoadOptions { Configuration = new ImageConfiguration().Add(new SvgFormat(fonts: fonts)) };
        _loaded = Image.Load(Encoding.UTF8.GetBytes(_text), _options);
    }

    private static string FindRoot() =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SvgTextBenchmarks).Assembly).First(a => a.Key == "RepoRoot").Value!;

    [Benchmark(Description = "text page (40 lines of tspans + a path) -> 800: load + render")]
    public Rgba32 LoadRender() => Image.Load(Encoding.UTF8.GetBytes(_text), _options).Resize(width: 800).ToArray<Rgba32>()[0];

    [Benchmark(Description = "text page (40 lines of tspans + a path) -> 800: render")]
    public Rgba32 Render() => _loaded.Resize(width: 800).ToArray<Rgba32>()[0];
}
