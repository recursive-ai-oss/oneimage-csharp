using System.Diagnostics;
using System.Text;
using OneImage.Svg.Dom;
using OneImage.Svg.Render;
using OneImage.Svg.Xml;
using Xunit;

namespace OneImage.Svg.Tests;

/// <summary>clip-path, mask and overflow clipping.</summary>
[Collection(SvgSerial.Name)]
public class SvgClipMaskTests
{
    private const string Ns = SvgRenderTests.Ns;

    // A 20x20 user-unit document rendered at 20 px: one pixel per unit, so the center of unit (x, y) is the pixel (x, y).
    private static Rgba32[] Render(string body, string attrs = "viewBox='0 0 20 20'") => SvgRenderTests.Pixels($"<svg {Ns} {attrs}>{body}</svg>", 20);

    private static Rgba32 At(Rgba32[] px, int x, int y) => px[(y * 20) + x];

    private static void Painted(Rgba32 p) => Assert.Equal(255, p.A);

    private static void Clear(Rgba32 p) => Assert.Equal(0, p.A);

    private const string Quarter = "<clipPath id='c'><rect x='0' y='0' width='10' height='10'/></clipPath>";

    [Fact]
    public void ClipPathKeepsOnlyWhatIsInsideItsShapes()
    {
        Rgba32[] px = Render(Quarter + "<rect width='20' height='20' fill='red' clip-path='url(#c)'/>");
        Painted(At(px, 5, 5));
        Clear(At(px, 15, 5));
        Clear(At(px, 5, 15));
    }

    [Fact]
    public void ClipPathOnAGroupClipsEveryChildAndKeepsGroupOpacity()
    {
        Rgba32[] px = Render(Quarter + "<g clip-path='url(#c)' opacity='0.5'><rect width='20' height='20' fill='red'/><rect width='20' height='20' fill='red'/></g>");
        Assert.InRange(At(px, 5, 5).A, 127, 128);
        Clear(At(px, 15, 15));
    }

    [Fact]
    public void ClipPathUnitsObjectBoundingBoxScalesToTheElement()
    {
        // The left half of the element's box, for a shape and for a group (which needs a layer to know its box).
        string clip = "<clipPath id='c' clipPathUnits='objectBoundingBox'><rect width='0.5' height='1'/></clipPath>";
        Rgba32[] shape = Render(clip + "<rect x='4' y='4' width='12' height='12' fill='red' clip-path='url(#c)'/>");
        Painted(At(shape, 8, 10));
        Clear(At(shape, 12, 10));
        Rgba32[] group = Render(clip + "<g clip-path='url(#c)'><rect x='4' y='4' width='6' height='12' fill='red'/><rect x='10' y='4' width='6' height='12' fill='red'/></g>");
        Painted(At(group, 8, 10));
        Clear(At(group, 12, 10));
    }

    [Fact]
    public void ClipRuleOfEachChildAndTransformOfTheClipPathApply()
    {
        // An even-odd square-in-square leaves a hole; the clipPath's transform moves it.
        string star = "<path d='M 0 0 H 12 V 12 H 0 Z M 4 4 H 8 V 8 H 4 Z' clip-rule='evenodd'/>";
        Rgba32[] px = Render($"<clipPath id='c' transform='translate(4 4)'>{star}</clipPath><rect width='20' height='20' fill='red' clip-path='url(#c)'/>");
        Painted(At(px, 5, 5));
        Clear(At(px, 9, 9)); // the hole at 8..12
        Clear(At(px, 2, 2)); // outside the translated shape
        Painted(At(px, 14, 14));
        Rgba32[] nonzero = Render($"<clipPath id='c'><path d='M 0 0 H 12 V 12 H 0 Z M 4 4 H 8 V 8 H 4 Z'/></clipPath><rect width='20' height='20' fill='red' clip-path='url(#c)'/>");
        Painted(At(nonzero, 6, 6)); // same winding, no hole
    }

    [Fact]
    public void ClipPathsNestOnTheClipPathAndOnItsChildren()
    {
        string small = "<clipPath id='small'><rect x='0' y='0' width='10' height='20'/></clipPath>";
        Rgba32[] onSelf = Render(small + "<clipPath id='c' clip-path='url(#small)'><rect width='20' height='10'/></clipPath><rect width='20' height='20' fill='red' clip-path='url(#c)'/>");
        Painted(At(onSelf, 5, 5));
        Clear(At(onSelf, 15, 5));
        Clear(At(onSelf, 5, 15));
        Rgba32[] onChild = Render(small + "<clipPath id='c'><rect width='20' height='10' clip-path='url(#small)'/></clipPath><rect width='20' height='20' fill='red' clip-path='url(#c)'/>");
        Painted(At(onChild, 5, 5));
        Clear(At(onChild, 15, 5));
    }

    [Fact]
    public void UseInsideAClipPathPointsAtAShapeOnly()
    {
        string defs = "<rect id='r' width='10' height='10'/><g id='g'><rect width='10' height='10'/></g>";
        Rgba32[] shape = Render($"<defs>{defs}</defs><clipPath id='c'><use href='#r' x='5' y='5'/></clipPath><rect width='20' height='20' fill='red' clip-path='url(#c)'/>");
        Painted(At(shape, 8, 8));
        Clear(At(shape, 2, 2));
        Rgba32[] group = Render($"<defs>{defs}</defs><clipPath id='c'><use href='#g'/></clipPath><rect width='20' height='20' fill='red' clip-path='url(#c)'/>");
        Clear(At(group, 5, 5)); // a group is not a valid clipPath child, so nothing is kept
    }

    [Fact]
    public void InvalidReferencesFollowTheErrorRules()
    {
        Painted(At(Render("<rect width='20' height='20' fill='red' clip-path='url(#nothing)'/>"), 5, 5)); // dangling: ignored
        Painted(At(Render("<rect id='x' width='5' height='5'/><rect width='20' height='20' fill='red' clip-path='url(#x)'/>"), 15, 15)); // not a clipPath: ignored
        Clear(At(Render("<clipPath id='c'/><rect width='20' height='20' fill='red' clip-path='url(#c)'/>"), 5, 5)); // empty: nothing survives
        Clear(At(Render("<clipPath id='c' transform='matrix(0 0 0 0 0 0)'><rect width='20' height='20'/></clipPath><rect width='20' height='20' fill='red' clip-path='url(#c)'/>"), 5, 5));
        Clear(At(Render("<clipPath id='c'><rect width='20' height='20' display='none'/><rect width='20' height='20' visibility='hidden'/></clipPath><rect width='20' height='20' fill='red' clip-path='url(#c)'/>"), 5, 5));
    }

    [Fact]
    public void ClipCyclesAreBrokenAndDrawTheRest()
    {
        // The link back to a clip that is being resolved is ignored, so the cycle contributes nothing more and nothing hangs.
        Rgba32[] self = Render("<clipPath id='c' clip-path='url(#c)'><rect width='10' height='10'/></clipPath><rect width='20' height='20' fill='red' clip-path='url(#c)'/>");
        Painted(At(self, 5, 5));
        Clear(At(self, 15, 15));
        Rgba32[] pair = Render("<clipPath id='a'><rect width='15' height='15' clip-path='url(#b)'/></clipPath><clipPath id='b'><rect width='10' height='20' clip-path='url(#a)'/></clipPath><rect width='20' height='20' fill='red' clip-path='url(#a)'/>");
        Painted(At(pair, 5, 5));
        Clear(At(pair, 12, 5));
    }

    [Fact]
    public void ClipPathOnTheRootAndOnNestedViewports()
    {
        Rgba32[] root = SvgRenderTests.Pixels($"<svg {Ns} viewBox='0 0 20 20' clip-path='url(#c)'>{Quarter}<rect width='20' height='20' fill='red'/></svg>", 20);
        Painted(At(root, 5, 5));
        Clear(At(root, 15, 15));
        // Overflow: a nested svg clips by default, `visible` and `inherit` from a visible parent do not.
        string inner = "<rect x='-5' y='-5' width='30' height='30' fill='red'/>";
        Clear(At(Render($"<svg x='5' y='5' width='10' height='10'>{inner}</svg>"), 2, 2));
        Painted(At(Render($"<svg x='5' y='5' width='10' height='10' overflow='visible'>{inner}</svg>"), 2, 2));
        Painted(At(Render($"<svg x='5' y='5' width='10' height='10' overflow='inherit'>{inner}</svg>", "viewBox='0 0 20 20' overflow='visible'"), 2, 2));
        Clear(At(Render($"<svg x='5' y='5' width='10' height='10' overflow='inherit'>{inner}</svg>"), 2, 2));
    }

    // ---- masks ----

    private static string Mask(string attrs, string content) => $"<mask id='m' {attrs}>{content}</mask>";

    [Fact]
    public void LuminanceMaskScalesCoverageByLuminanceTimesAlpha()
    {
        string content = "<rect width='20' height='10' fill='white'/><rect y='10' width='20' height='5' fill='#808080'/><rect y='15' width='20' height='5' fill='white' fill-opacity='0.25'/>";
        Rgba32[] px = Render(Mask("maskUnits='userSpaceOnUse' width='20' height='20'", content) + "<rect width='20' height='20' fill='red' mask='url(#m)'/>");
        Assert.Equal(255, At(px, 5, 5).A);
        Assert.InRange(At(px, 5, 12).A, 127, 129);
        Assert.InRange(At(px, 5, 17).A, 63, 65);
    }

    [Fact]
    public void MaskTypeAlphaUsesOnlyAlphaAndInvalidFallsBackToLuminance()
    {
        string content = "<rect width='20' height='20' fill='black'/>";
        string target = "<rect width='20' height='20' fill='red' mask='url(#m)'/>";
        Painted(At(Render(Mask("mask-type='alpha'", content) + target), 10, 10));
        Clear(At(Render(Mask("mask-type='luminance'", content) + target), 10, 10));
        Clear(At(Render(Mask("mask-type='bogus'", content) + target), 10, 10));
        Painted(At(Render(Mask("style='mask-type:alpha'", content) + target), 10, 10));
    }

    [Fact]
    public void MaskRegionDefaultsToTheBoundingBoxPlusTenPercent()
    {
        // The element is 10 wide at x=5; the default region reaches from 4 to 16, whatever the mask content covers.
        Rgba32[] px = Render(Mask(string.Empty, "<rect width='20' height='20' fill='white'/>") + "<rect x='5' y='5' width='10' height='10' fill='red' mask='url(#m)'/>");
        Painted(At(px, 5, 5));
        Painted(At(px, 14, 14));
        Rgba32[] narrow = Render(Mask("x='0' y='0' width='0.5' height='1'", "<rect width='20' height='20' fill='white'/>") + "<rect x='5' y='5' width='10' height='10' fill='red' mask='url(#m)'/>");
        Painted(At(narrow, 7, 8));
        Clear(At(narrow, 12, 8));
        Rgba32[] user = Render(Mask("maskUnits='userSpaceOnUse' x='5' y='5' width='4' height='20'", "<rect width='20' height='20' fill='white'/>") + "<rect width='20' height='20' fill='red' mask='url(#m)'/>");
        Painted(At(user, 6, 8));
        Clear(At(user, 12, 8));
    }

    [Fact]
    public void MaskContentUnitsObjectBoundingBoxUsesFractions()
    {
        Rgba32[] px = Render(Mask("maskContentUnits='objectBoundingBox'", "<rect width='0.5' height='1' fill='white'/>") + "<rect x='4' y='4' width='12' height='12' fill='red' mask='url(#m)'/>");
        Painted(At(px, 7, 10));
        Clear(At(px, 13, 10));
    }

    [Fact]
    public void MaskOnAGroupAppliesToTheWholeGroupInItsOwnCoordinateSystem()
    {
        string group = "<g mask='url(#m)' transform='translate(4 0)'><rect width='6' height='20' fill='red'/><rect x='6' width='6' height='20' fill='blue'/></g>";
        Rgba32[] px = Render(Mask("maskUnits='userSpaceOnUse' x='0' y='0' width='9' height='20'", "<rect width='20' height='20' fill='white'/>") + group);
        // The mask region is 0..9 in the group's space, i.e. 4..13 on the canvas.
        Assert.Equal(255, At(px, 6, 10).R);
        Painted(At(px, 11, 10));
        Clear(At(px, 15, 10));
        Clear(At(px, 2, 10));
    }

    [Fact]
    public void MasksNestOnTheMaskAndInsideItsContent()
    {
        string small = "<mask id='half' maskUnits='userSpaceOnUse' width='10' height='20'><rect width='20' height='20' fill='white'/></mask>";
        string target = "<rect width='20' height='20' fill='red' mask='url(#m)'/>";
        Rgba32[] onSelf = Render(small + Mask("mask='url(#half)' maskUnits='userSpaceOnUse' width='20' height='20'", "<rect width='20' height='20' fill='white'/>") + target);
        Painted(At(onSelf, 5, 5));
        Clear(At(onSelf, 15, 5));
        Rgba32[] inContent = Render(small + Mask("maskUnits='userSpaceOnUse' width='20' height='20'", "<rect width='20' height='20' fill='white' mask='url(#half)'/>") + target);
        Painted(At(inContent, 5, 5));
        Clear(At(inContent, 15, 5));
    }

    [Fact]
    public void MaskCyclesTerminateAndTheFirstMaskKeepsItsContent()
    {
        string content = "<rect width='20' height='20' fill='white'";
        Rgba32[] self = Render($"<mask id='m' mask='url(#m)' maskUnits='userSpaceOnUse' width='20' height='20'>{content}/></mask><rect width='20' height='20' fill='red' mask='url(#m)'/>");
        Painted(At(self, 5, 5));
        string pair = $"<mask id='a' maskUnits='userSpaceOnUse' width='20' height='20'>{content} mask='url(#b)'/></mask><mask id='b' maskUnits='userSpaceOnUse' width='20' height='20'>{content} mask='url(#a)'/></mask>";
        Rgba32[] cyc = Render(pair + "<rect width='20' height='20' fill='red' mask='url(#a)'/><rect y='10' width='20' height='10' fill='blue' mask='url(#b)'/>");
        Painted(At(cyc, 5, 5)); // the link closing the cycle is dropped everywhere: a is plain white under b's white
        Painted(At(cyc, 5, 15));
    }

    [Fact]
    public void MaskWithoutUsableContentOrBoxHidesTheElement()
    {
        string target = "<rect width='20' height='20' fill='red' mask='url(#m)'/>";
        Clear(At(Render("<mask id='m' maskUnits='userSpaceOnUse' width='20' height='20'/>" + target), 5, 5)); // no content
        Clear(At(Render(Mask(string.Empty, "<rect width='20' height='20' fill='white'/>") + "<line x1='0' y1='5' x2='20' y2='5' stroke='red' mask='url(#m)'/>"), 5, 5)); // zero-height box
        Painted(At(Render("<rect width='20' height='20' fill='red' mask='url(#none)'/>"), 5, 5)); // dangling: ignored
    }

    [Fact]
    public void ClipAndMaskAndOpacityCombine()
    {
        string px = Mask("maskUnits='userSpaceOnUse' width='20' height='20'", "<rect width='20' height='20' fill='white'/>")
            + Quarter + "<g opacity='0.5' mask='url(#m)' clip-path='url(#c)'><rect width='20' height='20' fill='red'/></g>";
        Rgba32[] r = Render(px);
        Assert.InRange(At(r, 5, 5).A, 127, 128);
        Clear(At(r, 15, 15));
    }

    // ---- regions, culling, limits ----

    private static SvgScene BuildScene(string svg)
    {
        XmlDocument xml = XmlParser.Parse(Encoding.UTF8.GetBytes(svg), SvgLimits.Default);
        return SceneBuilder.Build(SvgDocument.Build(xml), SvgLimits.Default, CancellationToken.None);
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
                Pixels[(y * Width) + i] = new Rgba32(rows[i * 4], rows[(i * 4) + 1], rows[(i * 4) + 2], rows[(i * 4) + 3]);
            }
        }
    }

    private static string Busy()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 1000 1000'>");
        sb.Append("<clipPath id='ring'><circle cx='500' cy='500' r='420'/><circle cx='200' cy='200' r='90' clip-rule='evenodd'/></clipPath>");
        sb.Append("<mask id='fade' maskUnits='userSpaceOnUse' x='0' y='0' width='1000' height='1000'><rect width='1000' height='1000' fill='white'/><circle cx='500' cy='500' r='200' fill='#404040'/></mask>");
        sb.Append("<g clip-path='url(#ring)'>");
        for (int i = 0; i < 400; i++)
        {
            sb.Append($"<rect x='{(i % 20) * 50}' y='{(i / 20) * 50}' width='40' height='40' fill='rgb({i % 256},{(i * 7) % 256},120)'/>");
        }

        sb.Append("</g><g mask='url(#fade)' transform='rotate(7 500 500)'>");
        for (int i = 0; i < 100; i++)
        {
            sb.Append($"<circle cx='{(i % 10) * 100 + 50}' cy='{(i / 10) * 100 + 50}' r='45' fill='orange'/>");
        }

        sb.Append("</g></svg>");
        return sb.ToString();
    }

    [Fact]
    public void ARegionOfAClippedAndMaskedDocumentEqualsTheSameRegionOfTheFullRender()
    {
        SvgScene scene = BuildScene(Busy());
        const int Size = 1024;
        var full = new Capture(Size, Size);
        SceneRenderer.Render(scene, Size, Size, new ImageRect(0, 0, Size, Size), full, new SvgLimits(), CancellationToken.None);
        var region = new ImageRect(300, 130, 200, 260);
        var part = new Capture(region.Width, region.Height);
        SceneRenderer.Render(scene, Size, Size, region, part, new SvgLimits(), CancellationToken.None);
        int worst = 0;
        for (int y = 0; y < region.Height; y++)
        {
            for (int x = 0; x < region.Width; x++)
            {
                Rgba32 a = part.Pixels[(y * region.Width) + x];
                Rgba32 b = full.Pixels[((region.Y + y) * Size) + region.X + x];
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
            }
        }

        Assert.True(worst <= 1, $"region differs by {worst}");
        Assert.Contains(full.Pixels, p => p.A > 0);
    }

    [Fact]
    public void ClippedAndMaskedRegionsDrawOnlyTheNodesTheyIntersect()
    {
        // 2500 shapes inside a clipped group and 2500 inside a masked one, each group's clip/mask covering one corner.
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 1000 1000'>");
        sb.Append("<clipPath id='c'><rect width='100' height='100'/></clipPath><mask id='m' maskUnits='userSpaceOnUse' x='900' y='900' width='100' height='100'><rect width='1000' height='1000' fill='white'/></mask>");
        sb.Append("<g clip-path='url(#c)'>");
        for (int i = 0; i < 2500; i++)
        {
            sb.Append($"<rect x='{(i % 50) * 20}' y='{(i / 50) * 20}' width='15' height='15' fill='red'/>");
        }

        sb.Append("</g><g mask='url(#m)'>");
        for (int i = 0; i < 2500; i++)
        {
            sb.Append($"<rect x='{(i % 50) * 20}' y='{(i / 50) * 20}' width='15' height='15' fill='blue'/>");
        }

        sb.Append("</g></svg>");
        SvgScene scene = BuildScene(sb.ToString());
        const int Size = 1000;
        var centre = new Capture(100, 100);
        long before = SceneRenderer.PathsDrawn;
        SceneRenderer.Render(scene, Size, Size, new ImageRect(400, 400, 100, 100), centre, new SvgLimits(), CancellationToken.None);
        Assert.Equal(0, SceneRenderer.PathsDrawn - before); // neither the clip nor the mask reaches the center: nothing is rasterized

        var corner = new Capture(100, 100);
        before = SceneRenderer.PathsDrawn;
        SceneRenderer.Render(scene, Size, Size, new ImageRect(0, 0, 100, 100), corner, new SvgLimits(), CancellationToken.None);
        long drawn = SceneRenderer.PathsDrawn - before;
        Assert.InRange(drawn, 1, 40);
        Assert.Contains(corner.Pixels, p => p.A > 0);
    }

    [Fact]
    public void DeeplyChainedMasksAndClipsFailClosedAndFast()
    {
        var sw = Stopwatch.StartNew();

        // mask -> mask -> mask ... through the mask attribute.
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'>");
        for (int i = 0; i < 500; i++)
        {
            sb.Append($"<mask id='m{i}' {(i < 499 ? $"mask='url(#m{i + 1})'" : string.Empty)}><rect width='20' height='20' fill='white'/></mask>");
        }

        sb.Append("<rect width='20' height='20' fill='red' mask='url(#m0)'/></svg>");
        Assert.Throws<ImageLimitException>(() => SvgRenderTests.Pixels(sb.ToString(), 20));

        // clipPath -> clipPath -> ...
        sb.Clear().Append($"<svg {Ns} viewBox='0 0 20 20'>");
        for (int i = 0; i < 500; i++)
        {
            sb.Append($"<clipPath id='c{i}' {(i < 499 ? $"clip-path='url(#c{i + 1})'" : string.Empty)}><rect width='20' height='20'/></clipPath>");
        }

        sb.Append("<rect width='20' height='20' fill='red' clip-path='url(#c0)'/></svg>");
        Assert.Throws<ImageLimitException>(() => SvgRenderTests.Pixels(sb.ToString(), 20));

        // Groups that each need a mask layer, nested.
        sb.Clear().Append($"<svg {Ns} viewBox='0 0 20 20'><mask id='m'><rect width='20' height='20' fill='white'/></mask>");
        for (int i = 0; i < 300; i++)
        {
            sb.Append("<g mask='url(#m)'>");
        }

        sb.Append("<rect width='20' height='20' fill='red'/>");
        sb.Append(string.Concat(Enumerable.Repeat("</g>", 300))).Append("</svg>");
        Assert.Throws<ImageLimitException>(() => SvgRenderTests.Pixels(sb.ToString(), 20));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void AHugeClipPathFanOutStopsAtTheInstanceBudget()
    {
        // One clip of 1000 shapes in bounding-box units (resolved again for every element with a new box), used by 800 elements.
        // Each clip child is charged as 16 instances, so the 500,000 budget ends it after about 30,000 resolutions.
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 20 20'>");
        sb.Append("<clipPath id='c' clipPathUnits='objectBoundingBox'>").Append(string.Concat(Enumerable.Repeat("<rect width='0.5' height='0.5'/>", 1000))).Append("</clipPath>");
        for (int i = 0; i < 800; i++)
        {
            sb.Append($"<rect width='{10 + (i * 0.01)}' height='20' clip-path='url(#c)'/>"); // a different box each time, so nothing is shared
        }

        sb.Append("</svg>");
        var sw = Stopwatch.StartNew();
        ImageLimitException ex = Assert.Throws<ImageLimitException>(() => SvgRenderTests.Pixels(sb.ToString(), 20));
        Assert.Contains("instantiates more than", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    }
}
