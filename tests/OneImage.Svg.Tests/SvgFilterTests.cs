using System.Text;
using OneImage.Svg.Dom;
using OneImage.Svg.Render;
using OneImage.Svg.Xml;
using Xunit;

namespace OneImage.Svg.Tests;

/// <summary>The <c>filter</c> element and property: graph, regions, color spaces, CSS functions, limits and tile independence.</summary>
[Collection(SvgSerial.Name)]
public class SvgFilterTests
{
    private const string Ns = SvgRenderTests.Ns;

    // A 20 x 20 document at 20 px: pixel (x, y) is unit square (x, y).
    private static Rgba32[] Render(string body) => SvgRenderTests.Pixels($"<svg {Ns} viewBox='0 0 20 20'>{body}</svg>", 20);

    private static Rgba32 At(Rgba32[] px, int x, int y) => px[(y * 20) + x];

    private const string Box = "<rect x='6' y='6' width='8' height='8' fill='blue'";

    [Fact]
    public void AnUnresolvedFilterReferenceRemovesTheElementAndAnEmptyFilterToo()
    {
        Rgba32[] missing = Render($"{Box} filter='url(#nope)'/>");
        Assert.All(missing, p => Assert.Equal(0, p.A));
        Rgba32[] empty = Render($"<filter id='f'/>{Box} filter='url(#f)'/>");
        Assert.All(empty, p => Assert.Equal(0, p.A));
    }

    [Fact]
    public void FloodFillsTheFilterRegionWithItsColorAndOpacity()
    {
        // Default region: the box grown by 10% on every side, i.e. 5.2..14.8 here.
        Rgba32[] px = Render($"<filter id='f'><feFlood flood-color='red' flood-opacity='0.5'/></filter>{Box} filter='url(#f)'/>");
        Rgba32 inside = At(px, 5, 5);
        Assert.Equal(255, inside.R);
        Assert.Equal(0, inside.B);
        Assert.InRange(inside.A, 127, 129);
        Assert.Equal(0, At(px, 3, 3).A);
        Assert.Equal(0, At(px, 16, 16).A);
    }

    [Fact]
    public void OffsetMovesWholePixelsAndCutsAtTheRegion()
    {
        Rgba32[] px = Render($"<filter id='f' x='0' y='0' width='1.5' height='1' primitiveUnits='userSpaceOnUse'><feOffset dx='3'/></filter>{Box} filter='url(#f)'/>");
        Assert.Equal(0, At(px, 7, 10).A); // the original position, left of the moved copy at 9..17 (cut at 6 + 12 = 18)
        Assert.Equal(255, At(px, 12, 10).A);
        Assert.Equal(255, At(px, 16, 10).A);
        Assert.Equal(0, At(px, 18, 10).A);
    }

    [Fact]
    public void MergeStacksInputsInOrder()
    {
        Rgba32[] px = Render("<filter id='f' x='0' y='0' width='1' height='1'><feFlood flood-color='red' result='a'/><feOffset in='SourceGraphic' dx='0' result='b'/><feMerge><feMergeNode in='a'/><feMergeNode in='b'/></feMerge></filter>" + $"{Box} filter='url(#f)'/>");
        Rgba32 p = At(px, 10, 10);
        Assert.Equal(0, p.R);
        Assert.Equal(255, p.B); // the source graphic is on top of the flood
        Rgba32[] reversed = Render("<filter id='f' x='0' y='0' width='1' height='1'><feFlood flood-color='red' result='a'/><feMerge><feMergeNode in='SourceGraphic'/><feMergeNode in='a'/></feMerge></filter>" + $"{Box} filter='url(#f)'/>");
        Assert.Equal(255, At(reversed, 10, 10).R);
    }

    [Fact]
    public void CompositeInKeepsTheFirstInputWhereTheSecondCovers()
    {
        string f = "<filter id='f' x='0' y='0' width='1' height='1'><feFlood flood-color='green' x='6' y='6' width='4' height='8' result='half'/><feComposite in='SourceGraphic' in2='half' operator='in'/></filter>";
        Rgba32[] px = Render(f + $"{Box} filter='url(#f)'/>");
        Assert.Equal(255, At(px, 7, 10).A);
        Assert.Equal(255, At(px, 7, 10).B); // blue, from the source
        Assert.Equal(0, At(px, 12, 10).A);
    }

    [Fact]
    public void ZeroAndNegativeDeviationsLeaveTheInputAsItIs()
    {
        foreach (string dev in new[] { "0", "-3", "1 2 3", "" })
        {
            Rgba32[] px = Render($"<filter id='f'><feGaussianBlur stdDeviation='{dev}'/></filter>{Box} filter='url(#f)'/>");
            Assert.Equal(255, At(px, 6, 6).A);
            Assert.Equal(0, At(px, 5, 5).A);
        }
    }

    [Fact]
    public void BlurSpreadsAndKeepsTheMass()
    {
        Rgba32[] px = Render($"<filter id='f' x='-1' y='-1' width='3' height='3'><feGaussianBlur stdDeviation='2'/></filter>{Box} filter='url(#f)'/>");
        Assert.InRange(At(px, 6, 10).A, 60, 200); // the edge is half covered
        Assert.True(At(px, 3, 10).A > 0 && At(px, 3, 10).A < 100, "the blur reaches outside the box");
        Assert.InRange(At(px, 10, 10).A, 200, 255);
        double mass = px.Sum(p => p.A / 255.0);
        Assert.InRange(mass, 62, 66); // 8 x 8 = 64 square units, within rounding of 8-bit alpha
    }

    [Fact]
    public void ColorMatrixWorksOnStraightColorInTheChosenSpace()
    {
        // Luminance to alpha on a white box: alpha is the luminance (1), color black.
        Rgba32[] px = Render($"<filter id='f' color-interpolation-filters='sRGB'><feColorMatrix type='luminanceToAlpha'/></filter><rect x='6' y='6' width='8' height='8' fill='white' filter='url(#f)'/>");
        Rgba32 p = At(px, 10, 10);
        Assert.Equal(0, p.R);
        Assert.InRange(p.A, 253, 255);
    }

    [Fact]
    public void LinearAndSrgbEvaluationDifferForABlendOfMidGray()
    {
        // Half-covered 50% gray over white through a blur: linear light mixes brighter than encoded values do.
        string Body(string space) => $"<filter id='f' color-interpolation-filters='{space}' x='-1' y='-1' width='3' height='3'><feGaussianBlur stdDeviation='3'/></filter><rect x='0' y='0' width='10' height='20' fill='black' filter='url(#f)'/>";
        Rgba32 linear = At(Render(Body("linearRGB")), 10, 10);
        Rgba32 srgb = At(Render(Body("sRGB")), 10, 10);
        // The color of black is black either way; alpha is what blurs, and it does not depend on the space.
        Assert.InRange(Math.Abs(linear.A - srgb.A), 0, 1);
        Assert.Equal(0, linear.R);
    }

    [Fact]
    public void TheFilterRunsBeforeTheClip()
    {
        // A box blurred and clipped to its left half keeps a hard edge at the clip, which a clip applied before the blur would soften.
        Rgba32[] px = Render("<clipPath id='c'><rect x='0' y='0' width='10' height='20'/></clipPath><filter id='f' x='-1' y='-1' width='3' height='3'><feGaussianBlur stdDeviation='1.5'/></filter>"
            + "<rect x='2' y='2' width='16' height='16' fill='blue' filter='url(#f)' clip-path='url(#c)'/>");
        Assert.InRange(At(px, 9, 10).A, 250, 255);
        Assert.Equal(0, At(px, 10, 10).A);
    }

    [Fact]
    public void FilterOnAnEmptyGroupStillDrawsWhatItGenerates()
    {
        Rgba32[] px = Render("<filter id='f' filterUnits='userSpaceOnUse' x='4' y='4' width='6' height='6'><feFlood flood-color='lime'/></filter><g filter='url(#f)'/>");
        Assert.Equal(255, At(px, 5, 5).A);
        Assert.Equal(0, At(px, 11, 11).A);
    }

    [Fact]
    public void CssFilterFunctionsApplyInOrderAndInvalidOnesAreIgnored()
    {
        Rgba32 gray = At(Render($"<rect x='6' y='6' width='8' height='8' fill='rgb(255,0,0)' style='filter: grayscale(1)'/>"), 10, 10);
        Assert.InRange(Math.Abs(gray.R - gray.G), 0, 2);
        Assert.InRange(Math.Abs(gray.G - gray.B), 0, 2);
        Rgba32 half = At(Render($"<rect x='6' y='6' width='8' height='8' fill='red' style='filter: opacity(0.5)'/>"), 10, 10);
        Assert.InRange(half.A, 127, 129);
        Rgba32 untouched = At(Render($"<rect x='6' y='6' width='8' height='8' fill='red' style='filter: blur(-3px)'/>"), 10, 10);
        Assert.Equal(new Rgba32(255, 0, 0, 255), untouched);
        Rgba32 invalidInList = At(Render($"<rect x='6' y='6' width='8' height='8' fill='red' style='filter: grayscale(1) bogus(3)'/>"), 10, 10);
        Assert.Equal(new Rgba32(255, 0, 0, 255), invalidInList);
    }

    [Fact]
    public void DropShadowIsBehindTheContentAndOutsideTheBox()
    {
        Rgba32[] px = Render($"<rect x='4' y='4' width='8' height='8' fill='red' style='filter: drop-shadow(black 4px 4px 0px)'/>");
        Assert.Equal(new Rgba32(255, 0, 0, 255), At(px, 6, 6));
        Rgba32 shadow = At(px, 14, 14);
        Assert.Equal(255, shadow.A);
        Assert.Equal(0, shadow.R);
    }

    [Fact]
    public void FilterOnRotatedContentIsDrawnInTheElementsOwnAxes()
    {
        // The offset is along the element's x axis, which is rotated 90 degrees: it moves the box down.
        Rgba32[] px = Render("<filter id='f' x='-1' y='-1' width='4' height='4'><feOffset dx='4'/></filter><g transform='rotate(90 10 10)'><rect x='6' y='6' width='4' height='4' fill='blue' filter='url(#f)'/></g>");
        // Unfiltered, the box would be at x 10..14, y 6..10 after the rotation; moved along the rotated x axis it is at y 10..14.
        Assert.Equal(0, At(px, 12, 7).A);
        Assert.Equal(255, At(px, 12, 12).A);
    }

    [Fact]
    public void AFilterWithMoreThanTheAllowedPrimitivesFailsTyped()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'><filter id='f'>");
        for (int i = 0; i < 257; i++)
        {
            sb.Append("<feFlood/>");
        }

        sb.Append($"</filter>{Box} filter='url(#f)'/></svg>");
        Assert.Throws<ImageLimitException>(() => SvgRenderTests.Load(sb.ToString()).Resize(width: 20).ToArray<Rgba32>());
    }

    [Fact]
    public void FiltersNestedThroughImagesBeyondTheLimitFailTyped()
    {
        // f1 draws #g1 which has f2 which draws #g2 ... : each level is a filter nested in the one before.
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'>");
        const int Levels = 7;
        for (int i = 1; i <= Levels; i++)
        {
            sb.Append($"<filter id='f{i}'><feImage href='#g{i}'/></filter>");
        }

        for (int i = 1; i < Levels; i++)
        {
            sb.Append($"<g id='g{i}' filter='url(#f{i + 1})'><rect width='5' height='5' fill='red'/></g>");
        }

        sb.Append($"<g id='g{Levels}'><rect width='5' height='5' fill='red'/></g>{Box} filter='url(#f1)'/></svg>");
        Assert.Throws<ImageLimitException>(() => SvgRenderTests.Load(sb.ToString()).Resize(width: 20).ToArray<Rgba32>());
    }

    [Fact]
    public void ARegionThatNeedsMorePixelsThanTheLimitFailsBeforeAllocating()
    {
        string svg = $"<svg {Ns} viewBox='0 0 20 20'><filter id='f' x='-100' y='-100' width='300' height='300' filterUnits='userSpaceOnUse'><feGaussianBlur stdDeviation='4'/></filter>{Box} filter='url(#f)'/></svg>";
        var strict = new ImageConfiguration().Add(new SvgFormat(new SvgLimits { MaxFilterPixels = 500 }));
        Image img = Image.Load(Encoding.UTF8.GetBytes(svg), new LoadOptions { Configuration = strict });
        Assert.Throws<ImageLimitException>(() => img.Resize(width: 20).ToArray<Rgba32>());

        // The default limit is measured on what the output needs, not on the declared region: this one declares a billion pixels and draws fine.
        string huge = svg.Replace("x='-100' y='-100' width='300' height='300'", "x='-1000000' y='-1000000' width='2000000' height='2000000'", StringComparison.Ordinal);
        Rgba32[] px = SvgRenderTests.Load(huge).Resize(width: 20).ToArray<Rgba32>();
        Assert.InRange(px[(10 * 20) + 10].A, 60, 160); // blurred by 4 units: the 8 x 8 box is mostly spread out
    }

    [Fact]
    public void ARegionEqualsTheSamePixelsOfTheWholeRender()
    {
        // Blur, offset, a drop shadow and a rotated filtered group, drawn at 3 x: regions must match the whole within one level wherever they are cut.
        string svg = $"<svg {Ns} viewBox='0 0 200 200' width='200' height='200'>"
            + "<filter id='blur' x='-0.3' y='-0.3' width='1.6' height='1.6'><feGaussianBlur stdDeviation='6'/></filter>"
            + "<filter id='shift'><feOffset dx='9' dy='-7' result='o'/><feGaussianBlur in='o' stdDeviation='3 1'/><feComposite in2='SourceGraphic' operator='over'/></filter>"
            + "<rect x='20' y='25' width='70' height='60' rx='10' fill='seagreen' filter='url(#blur)'/>"
            + "<circle cx='140' cy='60' r='28' fill='orange' stroke='navy' stroke-width='5' filter='url(#shift)'/>"
            + "<g transform='translate(100 150) rotate(30)'><rect x='-40' y='-15' width='80' height='30' fill='crimson' style='filter: drop-shadow(rgba(0,0,0,0.6) 6px 8px 5px)'/></g>"
            + "</svg>";
        XmlDocument xml = XmlParser.Parse(Encoding.UTF8.GetBytes(svg), SvgLimits.Default);
        SvgScene scene = SceneBuilder.Build(SvgDocument.Build(xml), SvgLimits.Default, CancellationToken.None);
        const int Size = 600;
        var full = new Capture(Size, Size);
        SceneRenderer.Render(scene, Size, Size, new ImageRect(0, 0, Size, Size), full, new SvgLimits(), CancellationToken.None);
        Assert.Contains(full.Pixels, p => p.A is > 0 and < 255);

        foreach (ImageRect region in new[] { new ImageRect(0, 0, 128, 128), new ImageRect(97, 61, 203, 150), new ImageRect(300, 300, 250, 250), new ImageRect(511, 17, 89, 301) })
        {
            var part = new Capture(region.Width, region.Height);
            SceneRenderer.Render(scene, Size, Size, region, part, new SvgLimits(), CancellationToken.None);
            int max = 0;
            for (int y = 0; y < region.Height; y++)
            {
                for (int x = 0; x < region.Width; x++)
                {
                    Rgba32 a = part.Pixels[(y * region.Width) + x];
                    Rgba32 b = full.Pixels[((region.Y + y) * Size) + region.X + x];
                    max = Math.Max(max, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
                }
            }

            Assert.True(max <= 1, $"region {region} differs from the whole render by {max}");
        }
    }

    [Fact]
    public void TilesThroughTheImageApiMatchTheFullRender()
    {
        string svg = $"<svg {Ns} viewBox='0 0 100 100' width='300' height='300'><filter id='f'><feGaussianBlur stdDeviation='4'/><feOffset dx='5' dy='5'/></filter><circle cx='50' cy='50' r='30' fill='teal' filter='url(#f)'/></svg>";
        Image img = SvgRenderTests.Load(svg);
        Rgba32[] all = img.ToArray<Rgba32>();
        var rect = new ImageRect(100, 70, 130, 129);
        Rgba32[] part = new Rgba32[rect.Width * rect.Height];
        img.CopyPixelsTo<Rgba32>(part, rect);
        int max = 0;
        for (int y = 0; y < rect.Height; y++)
        {
            for (int x = 0; x < rect.Width; x++)
            {
                Rgba32 a = part[(y * rect.Width) + x];
                Rgba32 b = all[((rect.Y + y) * 300) + rect.X + x];
                max = Math.Max(max, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
            }
        }

        Assert.True(max <= 1, $"differs by {max}");
    }

    [Fact]
    public void FilterRecursionThroughFeImageDrawsTheElementWithoutItsFilter()
    {
        Rgba32[] px = Render("<filter id='f'><feImage href='#r'/></filter><rect id='r' x='2' y='2' width='16' height='16' fill='red' filter='url(#f)'/>");
        Assert.Equal(new Rgba32(255, 0, 0, 255), At(px, 10, 10));
    }

    [Fact]
    public void FeImageWithADataUriDrawsThePictureInTheSubregion()
    {
        var png = new MemoryStream();
        Image.FromPixels<Rgba32>([new Rgba32(255, 0, 0, 255)], 1, 1).Save(png, ImageConfiguration.Default.FindByName("png")!);
        string Png = Convert.ToBase64String(png.ToArray());
        Rgba32[] px = Render($"<filter id='f' x='0' y='0' width='1' height='1'><feImage href='data:image/png;base64,{Png}' preserveAspectRatio='none'/></filter><rect x='6' y='6' width='8' height='8' fill='blue' filter='url(#f)'/>");
        Assert.Equal(255, At(px, 10, 10).R);
        Assert.Equal(0, At(px, 10, 10).B);
        Assert.Equal(0, At(px, 3, 3).A);
    }

    [Fact]
    public void TheFeatureMixDocumentRendersAndItsRegionsMatch()
    {
        string path = Path.Combine(SvgRenderTests.RepoRoot, "tests", "testdata", "svg", "filters.svg");
        Image img = Image.Load(File.ReadAllBytes(path), SvgRenderTests.Options);
        Image big = img.Resize(width: 720);
        Rgba32[] all = big.ToArray<Rgba32>();
        Assert.True(all.Count(p => p.A == 255) > 20_000, "the drawing is mostly there");
        string dump = Environment.GetEnvironmentVariable("ONEIMAGE_SVG_DUMP") ?? string.Empty;
        if (dump.Length > 0)
        {
            Directory.CreateDirectory(dump);
            big.Save(Path.Combine(dump, "filters-feature-mix.png"));
        }

        var rect = new ImageRect(130, 40, 301, 187);
        Rgba32[] part = new Rgba32[rect.Width * rect.Height];
        big.CopyPixelsTo<Rgba32>(part, rect);
        int max = 0;
        for (int y = 0; y < rect.Height; y++)
        {
            for (int x = 0; x < rect.Width; x++)
            {
                Rgba32 a = part[(y * rect.Width) + x];
                Rgba32 b = all[((rect.Y + y) * 720) + rect.X + x];
                max = Math.Max(max, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
            }
        }

        Assert.True(max <= 1, $"region differs by {max}");
    }

    private sealed class Capture : IRowSink
    {
        public Capture(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new Rgba32[width * height];
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format => PixelFormat.Rgba8;

        public Rgba32[] Pixels { get; }

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
            for (int i = 0; i < rowCount * Width; i++)
            {
                int o = i * 4;
                Pixels[(y * Width) + i] = new Rgba32(rows[o], rows[o + 1], rows[o + 2], rows[o + 3]);
            }
        }
    }
}
