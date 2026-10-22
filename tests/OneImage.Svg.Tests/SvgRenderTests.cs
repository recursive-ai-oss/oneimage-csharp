using System.Reflection;
using System.Text;
using OneImage.Svg;

namespace OneImage.Svg.Tests;

public class SvgRenderTests
{
    internal static readonly ImageConfiguration Config = new ImageConfiguration().Add(SvgFormat.Instance);
    internal static readonly LoadOptions Options = new() { Configuration = Config };

    internal const string Ns = "xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'";

    internal static string RepoRoot { get; } = typeof(SvgRenderTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;

    internal static Image Load(string svg) => Image.Load(Encoding.UTF8.GetBytes(svg), Options);

    internal static Rgba32[] Pixels(string svg, int width) => Load(svg).Resize(width: width).ToArray<Rgba32>();

    private static Rgba32 At(Rgba32[] px, int width, int x, int y) => px[(y * width) + x];

    [Fact]
    public void RendersAFilledRectangleAtNaturalSize()
    {
        Image img = Load($"<svg {Ns} width='20' height='10'><rect x='2' y='2' width='6' height='6' fill='#ff0000'/></svg>");
        Assert.Equal((20, 10), (img.Width, img.Height));
        Rgba32[] px = img.ToArray<Rgba32>();
        Assert.Equal(new Rgba32(255, 0, 0, 255), At(px, 20, 4, 4));
        Assert.Equal(new Rgba32(0, 0, 0, 0), At(px, 20, 15, 5));
    }

    [Fact]
    public void ZeroLengthSubpathsWithRoundCapsDrawDots()
    {
        Rgba32[] px = Pixels($"<svg {Ns} viewBox='0 0 20 20'><path d='M 10 10 L 10 10' stroke='green' stroke-width='10' stroke-linecap='round'/></svg>", 20);
        Assert.Equal(255, At(px, 20, 10, 10).A);
        Assert.Equal(0, At(px, 20, 1, 1).A);
    }

    private static Rgba32 Px(string body, int x, int y, string attrs = "viewBox='0 0 20 20'", int size = 20) =>
        At(Pixels($"<svg {Ns} {attrs}>{body}</svg>", size), size, x, y);

    [Fact]
    public void GroupOpacityCompositesTheGroupOnceNotEachChild()
    {
        // Two overlapping red rectangles in a half-transparent group: the overlap is no denser than the rest.
        string body = "<g opacity='0.5'><rect width='12' height='20' fill='red'/><rect x='8' width='12' height='20' fill='red'/></g>";
        Rgba32 single = Px(body, 2, 10);
        Rgba32 overlap = Px(body, 10, 10);
        Assert.InRange(single.A, 127, 128);
        Assert.Equal(single, overlap);
        Assert.Equal(255, single.R);
    }

    [Fact]
    public void ShapeOpacityCoversFillAndStrokeAsOne()
    {
        // Stroke over fill inside one shape: with opacity the stroke does not show the fill through it.
        string body = "<rect x='5' y='5' width='10' height='10' fill='blue' stroke='red' stroke-width='4' opacity='0.5'/>";
        Rgba32 edge = Px(body, 5, 10); // on the stroke, where the stroke covers the fill's edge
        Assert.InRange(edge.A, 126, 129);
        Assert.True(edge.R > 250 && edge.B < 5, edge.ToString());
    }

    [Fact]
    public void FillAndStrokeOpacitiesApplyIndependently()
    {
        string body = "<rect x='4' y='4' width='12' height='12' fill='#00ff00' fill-opacity='0.5' stroke='#0000ff' stroke-opacity='0.25' stroke-width='2'/>";
        Assert.InRange(Px(body, 10, 10).A, 127, 128);
        Assert.InRange(Px(body, 3, 10).A, 63, 64);
    }

    [Theory]
    [InlineData("fill='green'", 0, 128, 0)]
    [InlineData("fill='#0f0'", 0, 255, 0)]
    [InlineData("fill='#00FF0080'", 0, 255, 0)]
    [InlineData("fill='rgb(0 0 255)'", 0, 0, 255)]
    [InlineData("fill='rgb(0%, 100%, 0%)'", 0, 255, 0)]
    [InlineData("fill='hsl(120, 100%, 25%)'", 0, 128, 0)]
    [InlineData("fill='hsl(0.5turn 100% 50%)'", 0, 255, 255)]
    [InlineData("fill='hwb(0 0% 0%)'", 255, 0, 0)]
    [InlineData("fill='currentColor' color='blue'", 0, 0, 255)]
    [InlineData("fill='url(#nothing) #00f'", 0, 0, 255)]
    public void ColorSyntaxes(string attrs, int r, int g, int b)
    {
        Rgba32 p = Px($"<rect width='20' height='20' {attrs}/>", 10, 10);
        Assert.Equal((r, g, b), (p.R, p.G, p.B));
    }

    [Fact]
    public void InvalidColorsFallBackToTheInheritedValue()
    {
        Assert.Equal(new Rgba32(0, 0, 0, 255), Px("<rect width='20' height='20' fill='nonsense'/>", 10, 10));
        Assert.Equal(new Rgba32(0, 0, 0, 255), Px("<rect width='20' height='20' fill='rgb(0, 50%, 0)'/>", 10, 10));
    }

    [Fact]
    public void StyleAttributeBeatsPresentationAttributesAndInherits()
    {
        string body = "<g fill='red' style='fill: lime; stroke:blue /* c */; stroke-width:2'><rect x='4' y='4' width='12' height='12'/><rect x='0' y='0' width='2' height='2' style='fill:inherit;stroke:none' fill='yellow'/></g>";
        Assert.Equal(new Rgba32(0, 255, 0, 255), Px(body, 10, 10));
        Assert.Equal(new Rgba32(0, 0, 255, 255), Px(body, 4, 10));
        Assert.Equal(new Rgba32(0, 255, 0, 255), Px(body, 0, 0));
    }

    [Fact]
    public void DisplayNoneAndHiddenVisibility()
    {
        string body = "<rect width='10' height='20' fill='red' display='none'/><g visibility='hidden'><rect x='10' width='10' height='10' fill='red'/><rect x='10' y='10' width='10' height='10' fill='blue' visibility='visible'/></g>";
        Assert.Equal(0, Px(body, 2, 2).A);
        Assert.Equal(0, Px(body, 15, 5).A);
        Assert.Equal(new Rgba32(0, 0, 255, 255), Px(body, 15, 15));
    }

    [Fact]
    public void TransformsComposeFromOutsideIn()
    {
        string body = "<g transform='translate(10 0)'><rect width='5' height='5' fill='red' transform='scale(2)'/></g>";
        Assert.Equal(new Rgba32(255, 0, 0, 255), Px(body, 15, 5));
        Assert.Equal(0, Px(body, 5, 5).A);
        Assert.Equal(0, Px(body, 15, 12).A);
    }

    [Fact]
    public void UnitsResolveAgainstFontSizeAndViewport()
    {
        // x=2em with font-size 3 -> 6; width 50% of 20 -> 10; y 1in -> 96 (outside), so use mm: 5.29mm ~ 20px.
        string body = "<g font-size='3'><rect x='2em' width='50%' height='20' fill='red'/></g>";
        Assert.Equal(0, Px(body, 5, 5).A);
        Assert.Equal(255, Px(body, 7, 5).A);
        Assert.Equal(255, Px(body, 15, 5).A);
        Assert.Equal(0, Px(body, 17, 5).A);
    }

    [Fact]
    public void ViewBoxAndPreserveAspectRatio()
    {
        // A 10x5 box in a 20x20 viewport: meet scales by 2 and centers vertically; slice scales by 4 and crops sides.
        string rect = "<rect width='10' height='5' fill='red'/>";
        string meet = "width='20' height='20' viewBox='0 0 10 5'";
        Assert.Equal(255, Px(rect, 10, 10, meet).A);
        Assert.Equal(0, Px(rect, 10, 4, meet).A);
        string slice = "width='20' height='20' viewBox='0 0 10 5' preserveAspectRatio='xMinYMin slice'";
        Assert.Equal(255, Px(rect, 10, 19, slice).A);
        string none = "width='20' height='20' viewBox='0 0 10 5' preserveAspectRatio='none'";
        Assert.Equal(255, Px(rect, 19, 19, none).A);
    }

    [Fact]
    public void NestedSvgPlacesAndClips()
    {
        string body = "<svg x='5' y='5' width='10' height='10' viewBox='0 0 5 5'><rect width='50' height='50' fill='red'/></svg>";
        Assert.Equal(255, Px(body, 10, 10).A);
        Assert.Equal(0, Px(body, 2, 2).A);
        Assert.Equal(0, Px(body, 17, 17).A);
        string visible = "<svg x='5' y='5' width='10' height='10' overflow='visible'><rect width='50' height='50' fill='red'/></svg>";
        Assert.Equal(255, Px(visible, 17, 17).A);
    }

    [Fact]
    public void UseInstantiatesGroupsShapesAndSymbols()
    {
        string body = "<defs><rect id='r' width='4' height='4' fill='red'/><symbol id='s' viewBox='0 0 2 2'><rect width='2' height='2' fill='blue'/></symbol></defs>" +
            "<use href='#r' x='10' y='1'/><use href='#s' x='1' y='10' width='8' height='8'/><use xlink:href='#r' x='10' y='10' fill='green'/>";
        Assert.Equal(new Rgba32(255, 0, 0, 255), Px(body, 11, 2));
        Assert.Equal(0, Px(body, 3, 3).A);
        Assert.Equal(new Rgba32(0, 0, 255, 255), Px(body, 4, 14));
        Assert.Equal(0, Px(body, 10, 6).A);
        // fill on the use is inherited by the (unstyled) referenced shape only when the shape has no fill of its own.
        Assert.Equal(new Rgba32(255, 0, 0, 255), Px(body, 11, 11));
    }

    [Fact]
    public void NaturalSizeFallsBackToTheContentBounds()
    {
        Image img = Load($"<svg {Ns}><rect x='5' y='5' width='30' height='10'/></svg>");
        Assert.Equal((35, 15), (img.Width, img.Height));
        Image half = Load($"<svg {Ns} width='50'><rect width='10' height='10'/></svg>");
        Assert.Equal((50, 10), (half.Width, half.Height));
        Assert.Throws<ImageFormatException>(() => Load($"<svg {Ns}/>"));
    }

    [Fact]
    public void PathDataIsRenderedUpToTheFirstError()
    {
        string body = "<path d='M 2 2 H 18 V 18 H 2 Z L 10 xx' fill='red'/>";
        Assert.Equal(255, Px(body, 10, 10).A);
        Assert.Equal(0, Px("<path d='L 2 2 H 18 V 18' fill='red'/>", 10, 10).A);
    }

    [Fact]
    public void RoundedRectanglesAndDashes()
    {
        Assert.Equal(0, Px("<rect width='20' height='20' rx='10' fill='red'/>", 0, 0).A);
        Assert.Equal(255, Px("<rect width='20' height='20' rx='10' fill='red'/>", 10, 10).A);
        string dashed = "<line x1='0' y1='10' x2='20' y2='10' stroke='red' stroke-width='4' stroke-dasharray='5 5'/>";
        Assert.Equal(255, Px(dashed, 2, 10).A);
        Assert.Equal(0, Px(dashed, 7, 10).A);
        Assert.Equal(255, Px(dashed, 12, 10).A);
    }

    [Fact]
    public void FillRuleChangesTheHole()
    {
        string star = "M 10 1 L 4 19 L 19 8 L 1 8 L 16 19 Z";
        Assert.Equal(255, Px($"<path d='{star}' fill='red' fill-rule='nonzero'/>", 10, 10).A);
        Assert.Equal(0, Px($"<path d='{star}' fill='red' fill-rule='evenodd'/>", 10, 10).A);
    }

    /// <summary>Documents that once crashed or hung the renderer (found by fuzzing): each must now draw, or fail with a typed error.</summary>
    [Fact]
    public void FuzzRepros()
    {
        string[] files = Directory.GetFiles(Path.Combine(RepoRoot, "tests", "testdata", "svg"), "*.svg");
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            try
            {
                _ = Image.Load(file, Options).Resize(40, 24, Fit.Stretch).ToArray<Rgba32>();
            }
            catch (ImageException)
            {
            }
        }
    }

    [Theory]
    [InlineData("icon-24.svg", 256)]
    [InlineData("illustration.svg", 1024)]
    [InlineData("illustration.svg", 1000)]
    public void RendersTheSampleDocuments(string name, int width)
    {
        Image img = Image.Load(Path.Combine(RepoRoot, "tests", "testdata", "svg", name), Options).Resize(width: width);
        Rgba32[] px = img.ToArray<Rgba32>();
        Assert.Equal(width, img.Width);
        Assert.True(px.Count(p => p.A == 255) > px.Length / 2);
        if (Environment.GetEnvironmentVariable("ONEIMAGE_SVG_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            img.Save(Path.Combine(dump, $"sample-{Path.GetFileNameWithoutExtension(name)}-{width}.png"));
        }
    }
}

