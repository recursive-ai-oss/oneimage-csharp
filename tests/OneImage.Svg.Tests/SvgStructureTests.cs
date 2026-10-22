using System.Text;
using OneImage.Svg;

namespace OneImage.Svg.Tests;

/// <summary>Conditional processing (<c>switch</c>, <c>systemLanguage</c>), <c>transform-origin</c>, and <c>image</c> with data URIs.</summary>
public class SvgStructureTests
{
    private const string Ns = SvgRenderTests.Ns;

    private static readonly Rgba32 Green = new(0, 128, 0, 255);
    private static readonly Rgba32 Red = new(255, 0, 0, 255);
    private static readonly Rgba32 Blue = new(0, 0, 255, 255);

    private static Rgba32[] Render(string svg, SvgFormat? format = null, int? size = null)
    {
        var options = format is null ? SvgRenderTests.Options : new LoadOptions { Configuration = new ImageConfiguration().Add(format) };
        Image image = Image.Load(Encoding.UTF8.GetBytes(svg), options);
        return (size is { } s ? image.Resize(s, s, Fit.Stretch) : image).ToArray<Rgba32>();
    }

    private static Rgba32 Center(string body, string attrs = "width='20' height='20'", SvgFormat? format = null) =>
        Render($"<svg {Ns} {attrs}>{body}</svg>", format)[(10 * 20) + 10];

    // ---- switch and conditional attributes ----

    [Fact]
    public void SwitchRendersTheFirstChildWhoseConditionsHold()
    {
        string rect(string color, string cond) => $"<rect width='20' height='20' fill='{color}' {cond}/>";
        Assert.Equal(Green, Center($"<switch>{rect("red", "requiredExtensions='http://example.org/x'")}{rect("green", string.Empty)}{rect("red", string.Empty)}</switch>"));
        Assert.Equal(Green, Center($"<switch>{rect("red", "systemLanguage='is'")}{rect("green", "systemLanguage='en-GB, fr'")}{rect("red", string.Empty)}</switch>"));
        Assert.Equal(Green, Center($"<switch><title>t</title><foo/>{rect("green", string.Empty)}</switch>"));
        // The chosen child is rendered even when display:none hides it; nothing else is.
        Assert.Equal(0, Center($"<switch><rect width='20' height='20' fill='red' display='none'/>{rect("red", string.Empty)}</switch>").A);
        // No child qualifies: nothing is drawn.
        Assert.Equal(0, Center($"<switch>{rect("red", "systemLanguage='ru'")}</switch>").A);
    }

    [Fact]
    public void SwitchActsAsAGroup()
    {
        Assert.Equal(Green, Center("<switch fill='green'><rect width='20' height='20'/></switch>"));
        Assert.Equal(Green, Center("<switch transform='translate(10 0)'><rect width='20' height='20' fill='green'/></switch>"));
        Assert.Equal(0, Render($"<svg {Ns} width='20' height='20'><switch transform='translate(10 0)'><rect width='10' height='20' fill='green'/></switch></svg>")[(10 * 20) + 5].A);
    }

    [Fact]
    public void ConditionalAttributesHideElementsOutsideSwitchToo()
    {
        Assert.Equal(0, Center("<rect width='20' height='20' systemLanguage='ru-RU'/>").A);
        Assert.Equal(Green, Center("<rect width='20' height='20' fill='green' systemLanguage='en-US, ru'/>"));
        Assert.Equal(0, Center("<rect width='20' height='20' requiredExtensions=''/>").A);
        Assert.Equal(Green, Center("<rect width='20' height='20' fill='green' requiredFeatures='http://www.w3.org/TR/SVG11/feature#Shape'/>"));
        Assert.Equal(0, Center("<g systemLanguage='fr'><rect width='20' height='20'/></g>").A);
        Assert.Equal(0, Center("<rect width='20' height='20' systemLanguage=''/>").A);

        // On the root, the whole image is empty.
        Assert.All(Render($"<svg {Ns} width='4' height='4' systemLanguage='fr'><rect width='4' height='4'/></svg>"), p => Assert.Equal(0, p.A));
    }

    [Fact]
    public void SystemLanguageUsesTheConfiguredLanguages()
    {
        string body = "<rect width='20' height='20' fill='green' systemLanguage='de'/>";
        Assert.Equal(0, Center(body).A); // default: en
        Assert.Equal(Green, Center(body, format: new SvgFormat(languages: ["fr", "de"])));
        Assert.Equal(Green, Center("<rect width='20' height='20' fill='green' systemLanguage='DE-ch'/>", format: new SvgFormat(languages: ["de"])));
        Assert.Equal(0, Center("<rect width='20' height='20' fill='green' systemLanguage='dex'/>", format: new SvgFormat(languages: ["de"])).A);
    }

    // ---- transform-origin ----

    [Theory]
    [InlineData("center", 20, 20)]
    [InlineData("50% 50%", 20, 20)]
    [InlineData("left top", 0, 0)]
    [InlineData("top left", 0, 0)]
    [InlineData("right bottom", 40, 40)]
    [InlineData("bottom", 20, 40)]
    [InlineData("25% top", 10, 0)]
    [InlineData("4px 6px", 4, 6)]
    [InlineData("right", 40, 20)]
    public void TransformOriginMovesThePointATransformActsAround(string origin, double ox, double oy)
    {
        // A square centered at (20, 20) shrunk by half around the origin (relative to the 40 x 40 viewBox) has its center halfway to it.
        var px = Render($"<svg {Ns} viewBox='0 0 40 40' width='40' height='40'><rect x='16' y='16' width='8' height='8' fill='green' transform='scale(0.5)' transform-origin='{origin}'/></svg>");
        int cx = (int)(ox + ((20 - ox) / 2));
        int cy = (int)(oy + ((20 - oy) / 2));
        Assert.Equal(Green, px[(cy * 40) + cx]);
        if (cx != 20 || cy != 20)
        {
            Assert.Equal(0, px[(20 * 40) + 20].A);
        }
    }

    [Fact]
    public void RootTransformOriginIsRelativeToTheViewport()
    {
        // 20 x 20 viewport, scale(0.5) around its top-left corner: the content ends up in the upper-left quarter.
        var px = Render($"<svg {Ns} width='20' height='20' transform='scale(0.5)' transform-origin='0 0'><rect width='20' height='20' fill='green'/></svg>");
        Assert.Equal(Green, px[(3 * 20) + 3]);
        Assert.Equal(0, px[(15 * 20) + 15].A);
        // Without an origin the root scales around its center.
        var centered = Render($"<svg {Ns} width='20' height='20' transform='scale(0.5)'><rect width='20' height='20' fill='green'/></svg>");
        Assert.Equal(Green, centered[(10 * 20) + 10]);
        Assert.Equal(0, centered[(2 * 20) + 2].A);
    }

    // ---- images ----

    // A 2 x 2 picture: red, green / blue, red.
    private static string PngUri()
    {
        Image img = Image.FromPixels<Rgba32>([Red, Green, Blue, Red], 2, 2);
        using var ms = new MemoryStream();
        img.Save(ms, PngFormat.Instance);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static string Svg(string body, string attrs = "viewBox='0 0 20 20' width='20' height='20'") => $"<svg {Ns} {attrs}>{body}</svg>";

    [Fact]
    public void DrawsARasterFromADataUri()
    {
        string uri = PngUri();
        Rgba32[] px = Render(Svg($"<image x='0' y='0' width='20' height='20' xlink:href='{uri}' image-rendering='pixelated'/>"));
        Assert.Equal(Red, px[(2 * 20) + 2]);
        Assert.Equal(Green, px[(2 * 20) + 17]);
        Assert.Equal(Blue, px[(17 * 20) + 2]);
        Assert.Equal(Red, px[(17 * 20) + 17]);

        // Base64 broken across lines (as in a hand-edited file) and plain href work the same.
        string wrapped = uri.Insert(40, "\n   ").Insert(70, "\n");
        Assert.Equal(Red, Render(Svg($"<image width='20' height='20' href='{wrapped}' image-rendering='pixelated'/>"))[(2 * 20) + 2]);
    }

    [Fact]
    public void ImagesAreSmoothedUnlessAskedNot()
    {
        string uri = PngUri();
        Rgba32[] smooth = Render(Svg($"<image width='20' height='20' href='{uri}'/>"));
        Rgba32[] nearest = Render(Svg($"<image width='20' height='20' href='{uri}' image-rendering='optimizeSpeed'/>"));
        // On the seam between the red and green halves the smooth version blends them.
        Rgba32 s = smooth[(3 * 20) + 10];
        Rgba32 n = nearest[(3 * 20) + 10];
        Assert.True(s.R > 40 && s.R < 215 && s.G > 20, s.ToString());
        Assert.True(n == Red || n == Green, n.ToString());
    }

    [Fact]
    public void PreserveAspectRatioPlacesAndClipsThePicture()
    {
        string uri = PngUri(); // square
        // meet in a 20 x 10 box: the picture is 10 x 10 centered, so the left and right 5 columns stay empty.
        Rgba32[] meet = Render(Svg($"<image width='20' height='10' href='{uri}' image-rendering='pixelated'/>", "viewBox='0 0 20 10' width='20' height='10'"));
        Assert.Equal(0, meet[(5 * 20) + 2].A);
        Assert.Equal(Red, meet[(2 * 20) + 7]);
        Assert.Equal(0, meet[(5 * 20) + 17].A);

        // slice fills the box and cuts the overflow: only the top part of the square shows with xMidYMin.
        Rgba32[] slice = Render(Svg($"<image width='20' height='10' href='{uri}' preserveAspectRatio='xMidYMin slice' image-rendering='pixelated'/>", "viewBox='0 0 20 10' width='20' height='10'"));
        Assert.Equal(Red, slice[(2 * 20) + 2]);
        Assert.Equal(Green, slice[(2 * 20) + 17]);
        Assert.Equal(Red, slice[(9 * 20) + 2]); // still in the red quadrant: rows 0..9 of 20 are the top half

        // none stretches.
        Rgba32[] none = Render(Svg($"<image width='20' height='10' href='{uri}' preserveAspectRatio='none' image-rendering='pixelated'/>", "viewBox='0 0 20 10' width='20' height='10'"));
        Assert.Equal(Green, none[(2 * 20) + 17]);
        Assert.Equal(Blue, none[(7 * 20) + 2]);
    }

    [Fact]
    public void MissingSizeComesFromThePicture()
    {
        string uri = PngUri();
        // Only width: the height follows the picture's aspect ratio (square); no size at all: the picture's own 2 x 2.
        Rgba32[] w = Render(Svg($"<image width='10' href='{uri}' image-rendering='pixelated'/>"));
        Assert.Equal(Blue, w[(8 * 20) + 2]);
        Assert.Equal(0, w[(12 * 20) + 2].A);
        Rgba32[] none = Render(Svg($"<image href='{uri}' image-rendering='pixelated'/>"));
        Assert.Equal(Red, none[0]);
        Assert.Equal(0, none[(5 * 20) + 5].A);
    }

    [Fact]
    public void DrawsANestedSvgDocumentAsVectors()
    {
        string inner = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><circle cx='5' cy='5' r='5' fill='blue'/></svg>";
        string uri = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(inner));
        // Drawn at 200 px it is still smooth and sharp: the circle's edge is anti-aliased, not blocky.
        Rgba32[] px = Render(Svg($"<image width='20' height='20' href='{uri}'/>"), size: 200);
        Assert.Equal(Blue, px[(100 * 200) + 100]);
        Assert.Equal(0, px[(2 * 200) + 2].A);
        // Percent-encoded text, no base64.
        string text = "data:image/svg+xml;utf8," + Uri.EscapeDataString(inner);
        Assert.Equal(Blue, Render(Svg($"<image width='20' height='20' href='{text}'/>"))[(10 * 20) + 10]);
        // The nested document is cut to its own bounds.
        string big = "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect x='-50' y='-50' width='200' height='200' fill='blue'/></svg>";
        Rgba32[] cut = Render(Svg($"<image x='5' y='5' width='10' height='10' href='data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(big))}'/>"));
        Assert.Equal(Blue, cut[(10 * 20) + 10]);
        Assert.Equal(0, cut[(2 * 20) + 2].A);
        Assert.Equal(0, cut[(18 * 20) + 18].A);
    }

    [Fact]
    public void ImageTransformAndOpacityApply()
    {
        string uri = PngUri();
        Rgba32[] moved = Render(Svg($"<image width='10' height='10' href='{uri}' transform='translate(10 10)' image-rendering='pixelated'/>"));
        Assert.Equal(0, moved[(2 * 20) + 2].A);
        Assert.Equal(Red, moved[(12 * 20) + 12]);
        Rgba32[] half = Render(Svg($"<image width='20' height='20' href='{uri}' opacity='0.5' image-rendering='pixelated'/>"));
        Assert.InRange(half[(2 * 20) + 2].A, 126, 129);
        Assert.Equal(0, Render(Svg($"<image width='20' height='20' href='{uri}' visibility='hidden'/>"))[(2 * 20) + 2].A);
        Assert.Equal(0, Render(Svg($"<image width='20' height='20' href='{uri}' display='none'/>"))[(2 * 20) + 2].A);
    }

    [Fact]
    public void ExternalAndBrokenImagesAreDangling()
    {
        Assert.Equal(0, Center(Svg("<image width='20' height='20' href='file:///etc/passwd'/>", "width='20' height='20'")).A);
        Assert.Equal(0, Center("<image width='20' height='20' href='http://example.com/a.png'/>").A);
        Assert.Equal(0, Center("<image width='20' height='20' href='data:image/png;base64,AAAA'/>").A);
        Assert.Equal(0, Center("<image width='20' height='20' href='data:image/png;base64,@@@@'/>").A);
        Assert.Equal(0, Center("<image width='20' height='20'/>").A);
        // A truncated picture decodes to nothing and does not fail the document.
        string uri = PngUri();
        string cut = uri[..(uri.Length - 30)];
        Assert.Equal(Green, Center($"<rect width='20' height='20' fill='green'/><image width='20' height='20' href='{cut}'/>"));
    }

    [Fact]
    public void ImageLimitsFailClosed()
    {
        string uri = PngUri();
        var small = new SvgFormat(new SvgLimits { MaxImageBytes = 20 });
        Assert.Throws<ImageLimitException>(() => Render(Svg($"<image width='20' height='20' href='{uri}'/>"), small));

        var total = new SvgFormat(new SvgLimits { MaxTotalImageBytes = uri.Length });
        Assert.Throws<ImageLimitException>(() => Render(Svg($"<image width='10' height='10' href='{uri}'/><image width='10' height='10' href='{uri}'/>"), total));

        // A picture reached through many use elements is read, and counted, once.
        string fan = string.Concat(Enumerable.Range(0, 50).Select(i => $"<use href='#s' x='{i % 5}' y='{i / 5}' width='1' height='1'/>"));
        Assert.Equal(Red, Render(Svg($"<symbol id='s'><image width='100%' height='100%' href='{uri}' image-rendering='pixelated'/></symbol>{fan}"), total, size: 20)[(1 * 20) + 1]);

        // Images nested in images, deeper than the limit.
        string doc = Svg($"<image width='20' height='20' href='{uri}'/>");
        for (int i = 0; i < 4; i++)
        {
            doc = Svg($"<image width='20' height='20' href='data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(doc))}'/>");
        }

        Assert.Throws<ImageLimitException>(() => Render(doc));
        Render(doc, new SvgFormat(new SvgLimits { MaxImageDepth = 8 }));

        // A header that promises more pixels than the load limits allow never gets allocated.
        Image huge = Image.FromPixels<Rgba32>(new Rgba32[64 * 64], 64, 64);
        using var ms = new MemoryStream();
        huge.Save(ms, PngFormat.Instance);
        string hugeUri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        var options = new LoadOptions { Configuration = SvgRenderTests.Config, Limits = new ImageLimits { MaxPixelsPerFrame = 1000 } };
        Image outer = Image.Load(Encoding.UTF8.GetBytes(Svg($"<image width='20' height='20' href='{hugeUri}'/>")), options);
        Assert.Throws<ImageLimitException>(() => outer.ToArray<Rgba32>());
    }

    [Fact]
    public void SymbolSizedByUseGivesPercentLengthsItsViewport()
    {
        string uri = PngUri();
        string body = $"<symbol id='s'><image width='100%' height='100%' href='{uri}' image-rendering='pixelated'/></symbol><use href='#s' width='20' height='20'/>";
        Rgba32[] px = Render(Svg(body));
        Assert.Equal(Red, px[(2 * 20) + 2]);
        Assert.Equal(Green, px[(2 * 20) + 17]);
    }

    [Fact]
    public void TheSeedDocumentRendersWhatItSays()
    {
        // tests/testdata/svg/css-structure.svg mixes sheets, switch, transform-origin and images; it is also a fuzzing seed.
        string path = System.IO.Path.Combine(SvgRenderTests.RepoRoot, "tests", "testdata", "svg", "css-structure.svg");
        Rgba32[] px = Image.Load(path, SvgRenderTests.Options).ToArray<Rgba32>();
        Assert.Equal(200 * 200, px.Length);
        Assert.Equal(Blue, px[(30 * 200) + 130]);               // g#g1 > rect:nth-child(2n+1) with !important beats the rule order
        Assert.Equal(Red, px[(30 * 200) + 80]);                 // fill: unset inherits the group's red
        Assert.Equal(255, px[(30 * 200) + 175].R);              // the circle, halved by [data-k^=v] ~ circle
        Assert.InRange(px[(30 * 200) + 175].A, 126, 129);
        Assert.Equal(0, px[(170 * 200) + 20].A);                // the hidden image
        Assert.Equal(0, px[(170 * 200) + 60].A);                // the external image is never fetched
    }
}
