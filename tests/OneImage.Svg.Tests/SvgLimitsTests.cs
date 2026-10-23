using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OneImage.Svg;

namespace OneImage.Svg.Tests;

/// <summary>
/// Hostile documents for every <see cref="SvgLimits"/> entry. Each test builds a document just past one limit, checks that loading and
/// drawing it fails with <see cref="ImageLimitException"/> (or, where the limit degrades, that it still draws), and that the refusal is
/// cheap: time and allocation are bounded generously, since the point is "not proportional to what the document asked for".
/// Set ONEIMAGE_LIMIT_LOG to a file path to append one line of measurements per case.
/// </summary>
[Collection(SvgSerial.Name)]
public class SvgLimitsTests
{
    private const string Ns = SvgRenderTests.Ns;
    private const long AllocationBound = 900L << 20;
    private static readonly TimeSpan TimeBound = TimeSpan.FromSeconds(60);

    private static string Svg(string body, string attrs = "viewBox='0 0 20 20'") => $"<svg {Ns} {attrs}>{body}</svg>";

    private static LoadOptions With(SvgLimits limits, ImageLimits? imageLimits = null) =>
        new() { Configuration = new ImageConfiguration().Add(new SvgFormat(limits)), Limits = imageLimits ?? ImageLimits.Default };

    /// <summary>Loads and renders <paramref name="data"/> at 20 pixels wide.</summary>
    private static void Draw(byte[] data, LoadOptions options) =>
        Image.Load(data, options).Resize(width: 20).ToArray<Rgba32>();

    private static void Draw(string svg, LoadOptions? options = null) => Draw(Encoding.UTF8.GetBytes(svg), options ?? SvgRenderTests.Options);

    /// <summary>Runs <paramref name="action"/>, expecting the limit exception, and records and bounds what it cost.</summary>
    private static ImageLimitException Refused(string limit, Action action, long allocationBound = AllocationBound)
    {
        ImageLimitException ex = Measure(limit, () => Assert.Throws<ImageLimitException>(action), out _, allocationBound);
        return ex;
    }

    private static T Measure<T>(string limit, Func<T> action, out (TimeSpan Time, long Bytes) cost, long allocationBound = AllocationBound)
    {
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        T result = action();
        sw.Stop();
        long bytes = GC.GetTotalAllocatedBytes(true) - before;
        cost = (sw.Elapsed, bytes);
        if (Environment.GetEnvironmentVariable("ONEIMAGE_LIMIT_LOG") is { Length: > 0 } log)
        {
            File.AppendAllText(log, $"{limit}\t{sw.Elapsed.TotalMilliseconds:F0} ms\t{bytes / 1048576.0:F1} MiB\n");
        }

        Assert.True(sw.Elapsed < TimeBound, $"{limit}: took {sw.Elapsed}");
        Assert.True(bytes < allocationBound, $"{limit}: allocated {bytes >> 20} MiB");
        return result;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gz.Write(data);
        }

        return ms.ToArray();
    }

    // ---- source -------------------------------------------------------------------------------------------------

    [Fact]
    public void MaxDocumentBytesPlainFile()
    {
        byte[] data = Encoding.UTF8.GetBytes($"<svg {Ns} width='4' height='4'>" + new string(' ', (32 << 20) + 1) + "</svg>");
        Refused(nameof(SvgLimits.MaxDocumentBytes) + " plain", () => Draw(data, SvgRenderTests.Options));
    }

    [Fact]
    public void MaxDocumentBytesGzipOutput()
    {
        // 40 MiB of spaces is a 40 KiB file: the cap applies to what the gzip stream produces, not to the file.
        byte[] gz = Gzip(Encoding.UTF8.GetBytes($"<svg {Ns} width='4' height='4'>" + new string(' ', 40 << 20) + "</svg>"));
        Assert.True(gz.Length < 100_000);
        LoadOptions options = new() { Configuration = SvgRenderTests.Config, Limits = ImageLimits.Unrestricted };
        Refused(nameof(SvgLimits.MaxDocumentBytes) + " gzip", () => Draw(gz, options));
    }

    [Fact]
    public void GzipExpansionRatio()
    {
        // The ratio is checked against the load call's ImageLimits while inflating, long before the document cap is reached.
        byte[] gz = Gzip(Encoding.UTF8.GetBytes($"<svg {Ns} width='4' height='4'>" + new string(' ', 8 << 20) + "</svg>"));
        var options = new LoadOptions { Configuration = SvgRenderTests.Config, Limits = new ImageLimits { MaxExpansionRatio = 50 } };
        Refused("gzip expansion ratio", () => Draw(gz, options));
    }

    [Fact]
    public void MaxRootTagScanBytes()
    {
        // A root start tag that does not end inside the scan window: Identify reads no further than the window.
        byte[] data = Encoding.UTF8.GetBytes($"<svg {Ns} width='4' height='4' data-x='" + new string('x', (1 << 20) + 16) + "'/>");
        Refused(nameof(SvgLimits.MaxRootTagScanBytes), () => Image.Identify(data, SvgRenderTests.Options));
        // Within the window it identifies.
        byte[] ok = Encoding.UTF8.GetBytes($"<svg {Ns} width='4' height='4' data-x='" + new string('x', 1 << 19) + "'/>");
        Assert.Equal(4, Image.Identify(ok, SvgRenderTests.Options).Width);
    }

    // ---- XML ----------------------------------------------------------------------------------------------------

    [Fact]
    public void MaxElements()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'>");
        for (int i = 0; i < 500_000; i++)
        {
            sb.Append("<g/>"); // 500,000 children plus the root is one past the limit
        }

        Refused(nameof(SvgLimits.MaxElements), () => Draw(sb.Append("</svg>").ToString()));
    }

    [Fact]
    public void MaxDepth()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'>");
        sb.Insert(sb.Length, "<g>", 300).Append("<rect width='5' height='5'/>").Insert(sb.Length, "</g>", 300);
        Refused(nameof(SvgLimits.MaxDepth), () => Draw(sb.Append("</svg>").ToString()));
        // A million levels is refused at 256, with no recursion.
        var deep = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'>");
        deep.Insert(deep.Length, "<g>", 1_000_000);
        Refused(nameof(SvgLimits.MaxDepth) + " 1M open tags", () => Draw(deep.ToString()));
    }

    [Fact]
    public void MaxAttributesPerElement()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'><rect");
        for (int i = 0; i < 600; i++)
        {
            sb.Append(" a").Append(i).Append("='1'");
        }

        Refused(nameof(SvgLimits.MaxAttributesPerElement), () => Draw(sb.Append("/></svg>").ToString()));
    }

    [Fact]
    public void MaxNameLength()
    {
        Refused(nameof(SvgLimits.MaxNameLength) + " element", () => Draw(Svg($"<{new string('a', 300)}/>")));
        Refused(nameof(SvgLimits.MaxNameLength) + " attribute", () => Draw(Svg($"<rect {new string('a', 300)}='1'/>")));
        Refused(nameof(SvgLimits.MaxNameLength) + " prefix", () => Draw(Svg($"<{new string('p', 300)}:rect xmlns:{new string('p', 300)}='urn:x'/>")));
    }

    [Fact]
    public void MaxNamespaceBindings()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'><g");
        for (int i = 0; i < 300; i++)
        {
            sb.Append(" xmlns:n").Append(i).Append("='urn:n").Append(i).Append('\'');
        }

        Refused(nameof(SvgLimits.MaxNamespaceBindings), () => Draw(sb.Append("/></svg>").ToString()));

        // Bindings nested down the tree add up while they are in scope.
        var nested = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'>");
        for (int i = 0; i < 260; i++)
        {
            nested.Append("<g xmlns:m").Append(i).Append("='urn:m").Append(i).Append("'>");
        }

        nested.Insert(nested.Length, "</g>", 260);
        Refused(nameof(SvgLimits.MaxNamespaceBindings) + " nested", () => Draw(nested.Append("</svg>").ToString()));
    }

    [Fact]
    public void MaxPathSegments()
    {
        // A path attribute of about 5.7 million segments fits the 32 MiB document cap and is refused while it is read.
        var sb = new StringBuilder(31 << 20);
        sb.Append($"<svg {Ns} viewBox='0 0 20 20'><path stroke='black' fill='none' d='M0 0");
        while (sb.Length < 30 << 20)
        {
            sb.Append(" l1 1 l-1 0");
        }

        sb.Append("'/></svg>");
        string doc = sb.ToString();
        Refused(nameof(SvgLimits.MaxPathSegments), () => Draw(doc));

        // The points of a polyline count too, and the total is for the document: three polylines of 1.5 million points each.
        string points = string.Join(' ', Enumerable.Repeat("1,1 2,2", 750_000));
        string poly = Svg(string.Concat(Enumerable.Repeat($"<polyline points='{points}' fill='none' stroke='black'/>", 3)));
        Refused(nameof(SvgLimits.MaxPathSegments) + " polylines", () => Draw(poly));

        // Below the limit a large path draws (600,000 points here).
        string some = string.Join(' ', Enumerable.Repeat("1,1 2,2", 300_000));
        Rgba32[] px = Measure(nameof(SvgLimits.MaxPathSegments) + " 600k points drawn", () => SvgRenderTests.Pixels(Svg($"<polyline points='{some}' fill='none' stroke='black'/>"), 20), out _);
        Assert.Equal(20 * 20, px.Length);
    }

    // ---- CSS ----------------------------------------------------------------------------------------------------

    [Fact]
    public void MaxCssBytes()
    {
        string sheet = "<style>" + new string(' ', (2 << 20) + 1) + "</style>";
        Refused(nameof(SvgLimits.MaxCssBytes) + " style element", () => Draw(Svg(sheet + "<rect width='5' height='5'/>")));
        var attrs = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            attrs.Append("<rect width='1' height='1' style='fill:red;").Append(new string(' ', 60_000)).Append("'/>");
        }

        Refused(nameof(SvgLimits.MaxCssBytes) + " style attributes", () => Draw(Svg(attrs.ToString())));
    }

    [Fact]
    public void MaxCssRules()
    {
        var sb = new StringBuilder("<style>");
        for (int i = 0; i < 20_001; i++)
        {
            sb.Append(".c").Append(i).Append("{fill:red}");
        }

        Refused(nameof(SvgLimits.MaxCssRules), () => Draw(Svg(sb.Append("</style><rect width='5' height='5'/>").ToString())));
        // One rule per selector of a list.
        var list = new StringBuilder("<style>");
        for (int i = 0; i < 20_001; i++)
        {
            list.Append(i == 0 ? ".c" : ",.c").Append(i);
        }

        Refused(nameof(SvgLimits.MaxCssRules) + " selector list", () => Draw(Svg(list.Append("{fill:red}</style><rect width='5' height='5'/>").ToString())));
    }

    [Fact]
    public void MaxCssMatchSteps()
    {
        // A descendant chain that cannot match, against deep groups and many shapes: every shape retries the chain at every ancestor.
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'><style>");
        sb.Append("z g g g g g g g g g g g g g g g g rect{fill:red}</style>");
        sb.Insert(sb.Length, "<g>", 200);
        for (int i = 0; i < 100_000; i++)
        {
            sb.Append("<rect width='1' height='1'/>");
        }

        sb.Insert(sb.Length, "</g>", 200).Append("</svg>");
        Refused(nameof(SvgLimits.MaxCssMatchSteps), () => Draw(sb.ToString()));
    }

    // ---- render tree --------------------------------------------------------------------------------------------

    [Fact]
    public void MaxInstances()
    {
        // Ten levels of ten uses each: 10^10 copies if drawn.
        var sb = new StringBuilder("<defs><g id='l0'><rect width='1' height='1'/></g>");
        for (int i = 1; i <= 9; i++)
        {
            sb.Append($"<g id='l{i}'>");
            for (int j = 0; j < 10; j++)
            {
                sb.Append($"<use href='#l{i - 1}'/>");
            }

            sb.Append("</g>");
        }

        sb.Append("</defs><use href='#l9'/>");
        // Each level sits inside the reference depth, so it is the instance budget that stops it.
        Refused(nameof(SvgLimits.MaxInstances), () => Draw(Svg(sb.ToString())));
    }

    [Fact]
    public void MaxReferenceDepthUse()
    {
        var sb = new StringBuilder("<defs><g id='u0'><rect width='5' height='5'/></g>");
        for (int i = 1; i <= 100; i++)
        {
            sb.Append($"<use id='u{i}' href='#u{i - 1}'/>");
        }

        Refused(nameof(SvgLimits.MaxReferenceDepth) + " use", () => Draw(Svg(sb.Append("</defs><use href='#u100'/>").ToString())));
    }

    [Fact]
    public void MaxReferenceDepthClipPath()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            sb.Append($"<clipPath id='c{i}'{(i < 99 ? $" clip-path='url(#c{i + 1})'" : string.Empty)}><rect width='20' height='20'/></clipPath>");
        }

        Refused(nameof(SvgLimits.MaxReferenceDepth) + " clip-path", () => Draw(Svg(sb.Append("<rect width='20' height='20' clip-path='url(#c0)'/>").ToString())));
    }

    [Fact]
    public void MaxReferenceDepthPattern()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            sb.Append($"<pattern id='p{i}' width='10' height='10' patternUnits='userSpaceOnUse'><rect width='5' height='5' fill='{(i < 99 ? $"url(#p{i + 1})" : "red")}'/></pattern>");
        }

        Refused(nameof(SvgLimits.MaxReferenceDepth) + " pattern", () => Draw(Svg(sb.Append("<rect width='20' height='20' fill='url(#p0)'/>").ToString())));
    }

    [Fact]
    public void MaxLayerDepth()
    {
        var sb = new StringBuilder();
        sb.Insert(0, "<g opacity='0.5'>", 100).Append("<rect width='20' height='20'/>").Insert(sb.Length, "</g>", 100);
        Refused(nameof(SvgLimits.MaxLayerDepth), () => Draw(Svg(sb.ToString())));
    }

    [Fact]
    public void MaxPatternTilePixelsDegradesInsteadOfFailing()
    {
        // The tile would be far past 16 megapixels; it is drawn coarser and the document still renders.
        string svg = Svg("<pattern id='p' width='20000' height='20000' patternUnits='userSpaceOnUse'><rect width='20000' height='20000' fill='red'/></pattern>" +
            "<rect width='20' height='20' fill='url(#p)'/>", "viewBox='0 0 20 20' width='20' height='20'");
        Rgba32[] px = Measure(nameof(SvgLimits.MaxPatternTilePixels), () => SvgRenderTests.Load(svg).Resize(width: 20).ToArray<Rgba32>(), out _);
        Assert.Equal(255, px[(10 * 20) + 10].A);
        // The same tile at a 16000-pixel output is 16000 x 800 times larger; rendering a corner of it stays cheap too.
        Image big = SvgRenderTests.Load(svg.Replace("width='20' height='20'", "width='16000' height='16000'", StringComparison.Ordinal));
        var corner = new Rgba32[16 * 16];
        Measure(nameof(SvgLimits.MaxPatternTilePixels) + " 16000 px output corner", () =>
        {
            big.CopyPixelsTo<Rgba32>(corner, new ImageRect(0, 0, 16, 16));
            return 0;
        }, out _);
        Assert.Equal(255, corner[0].A);
    }

    // ---- images -------------------------------------------------------------------------------------------------

    private static string DataUri(int bytes) => "data:image/png;base64," + Convert.ToBase64String(new byte[bytes]);

    [Fact]
    public void MaxImageBytes()
    {
        string uri = DataUri((16 << 20) + 1);
        Refused(nameof(SvgLimits.MaxImageBytes), () => Draw(Svg($"<image width='20' height='20' href='{uri}'/>")));
    }

    [Fact]
    public void MaxTotalImageBytes()
    {
        // Scaled down from 64 MiB so the document stays under the document cap: three 1 MiB pictures against a 2 MiB total.
        string uri = DataUri(1 << 20);
        var options = With(new SvgLimits { MaxTotalImageBytes = 2 << 20 });
        var body = new StringBuilder();
        for (int i = 0; i < 3; i++)
        {
            body.Append($"<image width='5' height='5' x='{i * 5}' href='{uri}'/>"); // each URI differs only by position, not by payload, so make the payloads distinct
            uri = DataUri((1 << 20) + i + 1);
        }

        Refused(nameof(SvgLimits.MaxTotalImageBytes) + " (scaled)", () => Draw(Encoding.UTF8.GetBytes(Svg(body.ToString())), options));
    }

    [Fact]
    public void MaxImageDepth()
    {
        string doc = Svg("<rect width='5' height='5' fill='red'/>");
        for (int i = 0; i < 5; i++)
        {
            doc = Svg($"<image width='20' height='20' href='data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(doc))}'/>");
        }

        Refused(nameof(SvgLimits.MaxImageDepth), () => Draw(doc));
    }

    // ---- filters ------------------------------------------------------------------------------------------------

    [Fact]
    public void MaxFilterPrimitives()
    {
        var sb = new StringBuilder("<filter id='f'>");
        sb.Insert(sb.Length, "<feFlood/>", 257);
        Refused(nameof(SvgLimits.MaxFilterPrimitives), () => Draw(Svg(sb.Append("</filter><rect width='20' height='20' filter='url(#f)'/>").ToString())));
    }

    [Fact]
    public void MaxFilterDepth()
    {
        var sb = new StringBuilder();
        const int Levels = 8;
        for (int i = 1; i <= Levels; i++)
        {
            sb.Append($"<filter id='f{i}'><feImage href='#g{i}'/></filter>");
        }

        for (int i = 1; i < Levels; i++)
        {
            sb.Append($"<g id='g{i}' filter='url(#f{i + 1})'><rect width='5' height='5' fill='red'/></g>");
        }

        sb.Append($"<g id='g{Levels}'><rect width='5' height='5' fill='red'/></g><rect width='20' height='20' filter='url(#f1)'/>");
        Refused(nameof(SvgLimits.MaxFilterDepth), () => Draw(Svg(sb.ToString())));
    }

    [Fact]
    public void MaxFilterPixels()
    {
        // a wrapping convolution reads the whole filter region. At a 9000 x 9000 output the region is 81 megapixels, past the 64-megapixel default; the
        // refusal comes before any buffer for it exists, so only the (lazy) output copy of a small region is ever touched.
        string svg = Svg("<filter id='f' x='0' y='0' width='1' height='1'><feConvolveMatrix order='3' kernelMatrix='0 0 0 0 1 0 0 0 0' edgeMode='wrap'/></filter><rect width='20' height='20' filter='url(#f)'/>",
            "viewBox='0 0 20 20' width='9000' height='9000'");
        Image img = SvgRenderTests.Load(svg);
        var px = new Rgba32[16 * 16];
        Refused(nameof(SvgLimits.MaxFilterPixels), () => img.CopyPixelsTo<Rgba32>(px, new ImageRect(0, 0, 16, 16)));
    }

    // ---- text ---------------------------------------------------------------------------------------------------

    [Fact]
    public void MaxTextCharacters()
    {
        string body = "<text>" + new string('a', 1_000_001) + "</text>";
        Refused(nameof(SvgLimits.MaxTextCharacters), () => Draw(Svg(body)));
        // Counted again for every copy a use makes: 2,000 uses of 1,000 characters.
        string fan = "<defs><text id='t'>" + new string('b', 1000) + "</text></defs>" + string.Concat(Enumerable.Repeat("<use href='#t'/>", 2000));
        // Glyph outlines are the heaviest instances (about 2.4 KB each), so this refusal, which the instance budget makes at 500,000 glyphs, allocates the most.
        Refused(nameof(SvgLimits.MaxTextCharacters) + " through use", () => Draw(Svg(fan)), 3L << 30);
    }

    [Theory]
    [InlineData("repro-huge-font-size-attribute.svg")]
    [InlineData("repro-huge-font-size-nested-tspan.svg")]
    [InlineData("repro-huge-rect-width.svg")]
    public void FuzzFoundDocumentsRenderOrFailTyped(string name)
    {
        // A font size no float can hold (found by fuzzing) used to escape as an ArgumentOutOfRangeException from the font code.
        var fonts = new SvgFontOptions { UseSystemFonts = false };
        fonts.AddFont(Path.Combine(AppContext.BaseDirectory, "testdata", "fonts", "NotoSans-Subset-Regular.ttf"));
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(new SvgFormat(fonts: fonts)) };
        byte[] data = File.ReadAllBytes(Path.Combine(SvgRenderTests.RepoRoot, "tests", "testdata", "svg", name));
        try
        {
            Draw(data, options);
        }
        catch (Exception ex) when (ex is ImageFormatException or ImageLimitException)
        {
            // A typed refusal is an acceptable answer; any other exception fails the test.
        }
    }

    [Fact]
    public void AFontSizeBeyondFloatRangeStillDrawsText()
    {
        var fonts = new SvgFontOptions { UseSystemFonts = false };
        fonts.AddFont(Path.Combine(AppContext.BaseDirectory, "testdata", "fonts", "NotoSans-Subset-Regular.ttf"));
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(new SvgFormat(fonts: fonts)) };
        foreach (string size in new[] { "1e300", "1e-300" })
        {
            Draw(Encoding.UTF8.GetBytes(Svg($"<text font-family='Noto Sans' font-size='{size}'>A</text>")), options);
        }
    }

    [Theory]
    [InlineData("skewX(90)")]
    [InlineData("skewY(90)")]
    [InlineData("scale(1 1e12)")]
    [InlineData("matrix(1 0 1e15 1 0 0)")]
    public void ADegeneratePatternTransformStaysCheap(string transform)
    {
        // A transform that stretches one axis of the tile by a huge factor (the first one was found by fuzzing, and took over a minute)
        // is capped by tile side as well as by tile area, so the cost does not depend on the factor.
        string svg = Svg($"<pattern id='p' width='40' height='25' patternUnits='userSpaceOnUse' patternTransform='{transform}'>" +
            "<rect width='100' height='40' fill='none' stroke='green'/></pattern><rect x='20' y='20' width='160' height='160' rx='21' fill='url(#p)' stroke='blue'/>", "viewBox='0 0 200 200'");
        Measure("pattern transform " + transform, () => SvgRenderTests.Pixels(svg, 200), out _);
    }

    [Fact]
    public void TheFuzzFoundSkewedPatternRepro()
    {
        string svg = File.ReadAllText(Path.Combine(SvgRenderTests.RepoRoot, "tests", "testdata", "svg", "repro-pattern-skew-90.svg"));
        Measure("repro-pattern-skew-90", () => SvgRenderTests.Pixels(svg, 200), out _);
    }
}
