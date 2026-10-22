using System.Text;
using OneImage.Svg;

namespace OneImage.Svg.Tests;

public class SvgPaintServerTests
{
    private const string Ns = SvgRenderTests.Ns;

    // Renders a 100x100 viewBox drawing at 100 px (one pixel per unit) and returns the pixel at (x, y).
    private static Rgba32 Px(string body, int x, int y, string extra = "")
    {
        Rgba32[] px = SvgRenderTests.Pixels($"<svg {Ns} viewBox='0 0 100 100' {extra}>{body}</svg>", 100);
        return px[(y * 100) + x];
    }

    private static void Near(Rgba32 expected, Rgba32 actual, int tolerance = 2)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance && Math.Abs(expected.A - actual.A) <= tolerance, $"expected {expected}, got {actual}");
    }

    private const string BlackToWhite = "<stop offset='0' stop-color='black'/><stop offset='1' stop-color='white'/>";

    [Fact]
    public void LinearGradientRunsAcrossTheBoundingBoxByDefault()
    {
        string body = $"<linearGradient id='g'>{BlackToWhite}</linearGradient><rect x='20' y='10' width='60' height='80' fill='url(#g)'/>";
        Near(new Rgba32(128, 128, 128, 255), Px(body, 50, 50), 3);
        Assert.True(Px(body, 21, 50).R < 8);
        Assert.True(Px(body, 78, 50).R > 247);
        Assert.Equal(0, Px(body, 10, 50).A);
    }

    [Fact]
    public void UserSpaceUnitsAndGradientTransformPlaceTheGradient()
    {
        // Horizontal ramp from x=0 to x=100, turned by gradientTransform into a vertical ramp.
        string body = $"<linearGradient id='g' gradientUnits='userSpaceOnUse' x1='0' x2='100' gradientTransform='rotate(90)'>{BlackToWhite}</linearGradient><rect width='100' height='100' fill='url(#g)'/>";
        Near(new Rgba32(64, 64, 64, 255), Px(body, 10, 25), 3);
        Near(new Rgba32(64, 64, 64, 255), Px(body, 90, 25), 3);
        Near(new Rgba32(191, 191, 191, 255), Px(body, 50, 75), 3);
    }

    [Theory]
    [InlineData("pad", 10, 0)]
    [InlineData("pad", 90, 255)]
    [InlineData("repeat", 70, 105)]   // 1.41 periods wraps to 0.41
    [InlineData("reflect", 70, 150)] // 1.41 periods reflects back to 0.59
    public void SpreadMethodsContinueTheGradient(string spread, int x, int gray)
    {
        string body = $"<linearGradient id='g' x2='0.5' spreadMethod='{spread}'>{BlackToWhite}</linearGradient><rect width='100' height='100' fill='url(#g)'/>";
        if (spread == "pad" && x == 10)
        {
            gray = 51;
        }

        Near(new Rgba32((byte)gray, (byte)gray, (byte)gray, 255), Px(body, x, 50), 4);
    }

    [Fact]
    public void StopOpacityBlendsStraightColors()
    {
        // Red opaque to blue transparent: the middle is half-transparent purple (straight interpolation), not red.
        string body = "<linearGradient id='g'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue' stop-opacity='0'/></linearGradient><rect width='100' height='100' fill='url(#g)'/>";
        Rgba32 p = Px(body, 50, 50);
        Assert.InRange(p.A, 126, 129);
        Assert.InRange(p.R, 126, 129);
        Assert.InRange(p.B, 126, 129);
    }

    [Fact]
    public void StopsInheritThroughHrefAndLoopsAreSafe()
    {
        string body = $"<linearGradient id='base'>{BlackToWhite}</linearGradient><linearGradient id='g' xlink:href='#base' x2='0.5'/><linearGradient id='a' xlink:href='#b'>{BlackToWhite}</linearGradient><linearGradient id='b' xlink:href='#a'/>"
            + "<rect width='50' height='100' fill='url(#g)'/><rect x='50' width='50' height='100' fill='url(#b)'/>";
        Near(new Rgba32(128, 128, 128, 255), Px(body, 12, 50), 4);   // x2 from the referrer, stops from the target
        Near(new Rgba32(128, 128, 128, 255), Px(body, 75, 50), 4);   // the loop still finds its stops
    }

    [Fact]
    public void DegenerateGradientsFollowTheSpecification()
    {
        const string two = "<stop offset='0' stop-color='red'/><stop offset='1' stop-color='lime'/>";
        // One stop: that color. No stops with a fallback: the fallback. No stops without: nothing.
        Near(new Rgba32(0, 0, 255, 255), Px("<linearGradient id='g'><stop offset='0.3' stop-color='blue'/></linearGradient><rect width='100' height='100' fill='url(#g)'/>", 50, 50));
        Near(new Rgba32(0, 128, 0, 255), Px("<linearGradient id='g'/><rect width='100' height='100' fill='url(#g) green'/>", 50, 50));
        Assert.Equal(0, Px("<linearGradient id='g'/><rect width='100' height='100' fill='url(#g)'/>", 50, 50).A);

        // Start equal to end, and radius zero: the last stop.
        Near(new Rgba32(0, 255, 0, 255), Px($"<linearGradient id='g' x1='0.5' x2='0.5'>{two}</linearGradient><rect width='100' height='100' fill='url(#g)'/>", 50, 50));
        Near(new Rgba32(0, 255, 0, 255), Px($"<radialGradient id='g' r='0'>{two}</radialGradient><rect width='100' height='100' fill='url(#g)'/>", 50, 50));

        // A box without area cannot place a bounding-box gradient: the fallback paints the line instead.
        Near(new Rgba32(0, 128, 0, 255), Px($"<linearGradient id='g'>{two}</linearGradient><line x1='10' y1='50' x2='90' y2='50' stroke='url(#g) green' stroke-width='10'/>", 50, 50));

        // A reference that is not a paint server uses the fallback.
        Near(new Rgba32(0, 128, 0, 255), Px("<rect id='r' width='1' height='1'/><rect width='100' height='100' fill='url(#r) green'/>", 50, 50));
    }

    [Fact]
    public void RadialGradientGrowsFromTheCenter()
    {
        string body = $"<radialGradient id='g'>{BlackToWhite}</radialGradient><rect width='100' height='100' fill='url(#g)'/>";
        Assert.True(Px(body, 50, 50).R < 6);
        Near(new Rgba32(128, 128, 128, 255), Px(body, 75, 50), 4);
        Assert.True(Px(body, 2, 2).R > 250);
    }

    [Fact]
    public void FocalPointShiftsTheStartAndAConeLeavesTheRestUnpainted()
    {
        const string stops = "<stop offset='0' stop-color='black'/><stop offset='1' stop-color='white'/>";

        // Focal point toward the right edge: the dark spot sits right of center, so the left half is lighter at the same distance.
        string focal = $"<radialGradient id='g' fx='0.8' fy='0.5'>{stops}</radialGradient><rect width='100' height='100' fill='url(#g)'/>";
        Assert.True(Px(focal, 80, 50).R < 8);
        Assert.True(Px(focal, 20, 50).R > Px(focal, 80, 50).R + 100);

        // A focal circle outside the end circle forms a cone, outside which nothing is painted (SVG 2).
        string cone = $"<radialGradient id='g' gradientUnits='userSpaceOnUse' cx='10' cy='10' r='20' fx='90' fy='90'>{stops}</radialGradient><rect width='100' height='100' fill='url(#g)'/>";
        Assert.Equal(0, Px(cone, 90, 10).A);
        Assert.Equal(255, Px(cone, 40, 40).A);
    }

    [Fact]
    public void GradientsApplyToStrokesAndFillOpacity()
    {
        string body = $"<linearGradient id='g'>{BlackToWhite}</linearGradient><rect x='20' y='20' width='60' height='60' fill='none' stroke='url(#g)' stroke-width='10'/>";
        Rgba32 left = Px(body, 20, 50);
        Rgba32 right = Px(body, 79, 50);
        Assert.True(left.R < 30 && right.R > 225, $"{left} {right}");
        string translucent = $"<linearGradient id='g'>{BlackToWhite}</linearGradient><rect width='100' height='100' fill='url(#g)' fill-opacity='0.5'/>";
        Assert.InRange(Px(translucent, 50, 50).A, 126, 129);
    }

    [Fact]
    public void PatternRepeatsItsTileInUserSpace()
    {
        // 20x20 tile with a red 10x10 square in its corner.
        string body = "<pattern id='p' patternUnits='userSpaceOnUse' width='20' height='20'><rect width='10' height='10' fill='red'/></pattern><rect width='100' height='100' fill='url(#p)'/>";
        Near(new Rgba32(255, 0, 0, 255), Px(body, 5, 5));
        Near(new Rgba32(255, 0, 0, 255), Px(body, 45, 65));
        Assert.Equal(0, Px(body, 15, 5).A);
        Assert.Equal(0, Px(body, 55, 55).A);
    }

    [Fact]
    public void PatternTileInBoundingBoxUnitsStartsAtTheBox()
    {
        string body = "<pattern id='p' width='0.5' height='0.5' x='0' y='0'><rect width='10' height='10' fill='blue'/></pattern><rect x='20' y='20' width='60' height='60' fill='url(#p)'/>";
        Near(new Rgba32(0, 0, 255, 255), Px(body, 25, 25));
        Assert.Equal(0, Px(body, 40, 25).A);
        Near(new Rgba32(0, 0, 255, 255), Px(body, 55, 55));   // second tile begins at 20 + 30
    }

    [Fact]
    public void PatternContentUnitsViewBoxAndTransformPlaceTheContent()
    {
        // Content in box fractions.
        string obb = "<pattern id='p' width='1' height='1' patternContentUnits='objectBoundingBox'><rect width='0.5' height='0.5' fill='red'/></pattern><rect x='20' y='20' width='60' height='60' fill='url(#p)'/>";
        Near(new Rgba32(255, 0, 0, 255), Px(obb, 30, 30));
        Assert.Equal(0, Px(obb, 70, 70).A);

        // A viewBox fitted into the tile.
        string vb = "<pattern id='p' patternUnits='userSpaceOnUse' width='50' height='50' viewBox='0 0 10 10'><rect width='5' height='5' fill='lime'/></pattern><rect width='100' height='100' fill='url(#p)'/>";
        Near(new Rgba32(0, 255, 0, 255), Px(vb, 10, 10));
        Assert.Equal(0, Px(vb, 30, 30).A);
        Near(new Rgba32(0, 255, 0, 255), Px(vb, 60, 60));

        // patternTransform moves the whole tiling.
        string tr = "<pattern id='p' patternUnits='userSpaceOnUse' width='20' height='20' patternTransform='translate(10 0)'><rect width='10' height='10' fill='red'/></pattern><rect width='100' height='100' fill='url(#p)'/>";
        Assert.Equal(0, Px(tr, 5, 5).A);
        Near(new Rgba32(255, 0, 0, 255), Px(tr, 15, 5));
    }

    [Fact]
    public void RotatedPatternIsSmoothedAndKeepsItsColors()
    {
        string body = "<pattern id='p' patternUnits='userSpaceOnUse' width='20' height='20' patternTransform='rotate(45)'><rect width='20' height='20' fill='red'/></pattern><rect width='100' height='100' fill='url(#p)'/>";
        Near(new Rgba32(255, 0, 0, 255), Px(body, 50, 50));
        Near(new Rgba32(255, 0, 0, 255), Px(body, 20, 80));
    }

    [Fact]
    public void PatternsInheritAttributesAndChildrenThroughHref()
    {
        string body = "<pattern id='base' patternUnits='userSpaceOnUse' width='20' height='20'><rect width='10' height='10' fill='red'/></pattern><pattern id='p' xlink:href='#base' width='40' height='40'/><rect width='100' height='100' fill='url(#p)'/>";
        Near(new Rgba32(255, 0, 0, 255), Px(body, 5, 5));
        Assert.Equal(0, Px(body, 25, 5).A);   // the tile is 40 wide, from the referrer
        Near(new Rgba32(255, 0, 0, 255), Px(body, 45, 45));
    }

    [Fact]
    public void NestedPatternsAndRecursiveOnesDraw()
    {
        string nested = "<pattern id='inner' patternUnits='userSpaceOnUse' width='10' height='10'><rect width='5' height='10' fill='red'/></pattern>"
            + "<pattern id='outer' patternUnits='userSpaceOnUse' width='40' height='40'><rect width='40' height='40' fill='url(#inner)'/></pattern><rect width='100' height='100' fill='url(#outer)'/>";
        Near(new Rgba32(255, 0, 0, 255), Px(nested, 2, 2));
        Assert.Equal(0, Px(nested, 7, 2).A);
        Near(new Rgba32(255, 0, 0, 255), Px(nested, 52, 52));

        // A pattern whose content paints with the pattern itself paints nothing there; the rest still draws.
        string loop = "<pattern id='p' patternUnits='userSpaceOnUse' width='20' height='20'><rect width='10' height='10' fill='url(#p)'/><rect x='10' y='10' width='10' height='10' fill='blue'/></pattern><rect width='100' height='100' fill='url(#p)'/>";
        Assert.Equal(0, Px(loop, 5, 5).A);
        Near(new Rgba32(0, 0, 255, 255), Px(loop, 15, 15));
    }

    [Fact]
    public void PatternsWithoutAreaOrContentFallBack()
    {
        Near(new Rgba32(0, 128, 0, 255), Px("<pattern id='p' width='0' height='20' patternUnits='userSpaceOnUse'><rect width='5' height='5'/></pattern><rect width='100' height='100' fill='url(#p) green'/>", 50, 50));
        Near(new Rgba32(0, 128, 0, 255), Px("<pattern id='p' width='20' height='20' patternUnits='userSpaceOnUse'/><rect width='100' height='100' fill='url(#p) green'/>", 50, 50));
    }

    [Fact]
    public void PaintOrderMovesTheStrokeUnderTheFill()
    {
        const string shape = "x='20' y='20' width='60' height='60' fill='red' stroke='blue' stroke-width='20'";
        // On the edge the stroke's inner half overlaps the fill: normally the stroke is on top, with paint-order="stroke" the fill is.
        Near(new Rgba32(0, 0, 255, 255), Px($"<rect {shape}/>", 25, 50));
        Near(new Rgba32(255, 0, 0, 255), Px($"<rect {shape} paint-order='stroke'/>", 25, 50));
        Near(new Rgba32(255, 0, 0, 255), Px($"<g paint-order='stroke markers fill'><rect {shape}/></g>", 25, 50));
        Near(new Rgba32(0, 0, 255, 255), Px($"<rect {shape} paint-order='stroke' style='paint-order: normal'/>", 25, 50));
        Near(new Rgba32(0, 0, 255, 255), Px($"<rect {shape} paint-order='stroke stroke'/>", 25, 50)); // invalid: ignored
    }

    [Fact]
    public void UseOffersItsPaintsAsContextFillAndStroke()
    {
        string body = "<defs><rect id='r' width='40' height='40' fill='context-stroke' stroke='context-fill' stroke-width='10'/></defs><use xlink:href='#r' x='30' y='30' fill='red' stroke='blue'/>";
        Near(new Rgba32(0, 0, 255, 255), Px(body, 50, 50));   // fill is the use's stroke
        Near(new Rgba32(255, 0, 0, 255), Px(body, 30, 50));   // stroke is the use's fill
        Assert.Equal(0, Px("<rect width='40' height='40' fill='context-fill'/>", 20, 20).A);   // no context: nothing
    }

    [Fact]
    public void ContextPaintCanBeAGradientOverTheUseBox()
    {
        // The gradient spans the box of everything the use drew (the two rectangles), not each shape's own box.
        string body = $"<linearGradient id='g'>{BlackToWhite}</linearGradient><defs><g id='two'><rect width='20' height='20' fill='context-fill'/><rect x='80' width='20' height='20' fill='context-fill'/></g></defs><use xlink:href='#two' fill='url(#g)'/>";
        Assert.True(Px(body, 10, 10).R < 40);
        Assert.True(Px(body, 90, 10).R > 215);
    }

    [Fact]
    public void HostilePaintServersDoNotThrowOrHang()
    {
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 100 100'><linearGradient id='g'>");
        for (int i = 0; i < 70000; i++)
        {
            sb.Append("<stop offset='").Append((i % 1000) / 1000.0).Append("' stop-color='red'/>");
        }

        sb.Append("</linearGradient>")
            .Append("<linearGradient id='t' x2='1e-9'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient>")
            .Append("<linearGradient id='h' x1='1e30' x2='-1e30' gradientTransform='scale(1e30)'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient>")
            .Append("<radialGradient id='r' r='1e30' fr='1e30' gradientTransform='matrix(1e-30 0 0 1e-30 0 0)'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></radialGradient>")
            .Append("<pattern id='huge' patternUnits='userSpaceOnUse' width='1e9' height='1e9'><rect width='1e9' height='1e9' fill='red'/></pattern>")
            .Append("<pattern id='tiny' patternUnits='userSpaceOnUse' width='1e-9' height='1e-9'><rect width='1e-9' height='1e-9' fill='red'/></pattern>")
            .Append("<rect width='20' height='20' fill='url(#g)'/><rect x='20' width='20' height='20' fill='url(#t)'/><rect x='40' width='20' height='20' fill='url(#h)'/>")
            .Append("<rect x='60' width='20' height='20' fill='url(#r)'/><rect y='20' width='50' height='50' fill='url(#huge)'/><rect x='50' y='20' width='50' height='50' fill='url(#tiny)'/></svg>");
        Image img = Image.Load(Encoding.UTF8.GetBytes(sb.ToString()), SvgRenderTests.Options);
        Rgba32[] px = img.Resize(width: 100).ToArray<Rgba32>();
        Assert.Equal(255, px[(10 * 100) + 10].A);
    }

    [Fact]
    public void PatternChainsPastTheReferenceDepthAreRefused()
    {
        // Pattern i fills a rectangle with pattern i+1: the nesting is bounded by the reference depth limit.
        var sb = new StringBuilder($"<svg {Ns} viewBox='0 0 100 100'>");
        for (int i = 0; i < 60; i++)
        {
            sb.Append($"<pattern id='p{i}' patternUnits='userSpaceOnUse' width='50' height='50'><rect width='50' height='50' fill='url(#p{i + 1})'/></pattern>");
        }

        sb.Append("<pattern id='p60' patternUnits='userSpaceOnUse' width='50' height='50'><rect width='10' height='10' fill='red'/></pattern><rect width='100' height='100' fill='url(#p0)'/></svg>");
        Assert.Throws<ImageLimitException>(() => Image.Load(Encoding.UTF8.GetBytes(sb.ToString()), SvgRenderTests.Options).Resize(width: 100).ToArray<Rgba32>());
    }

    [Fact]
    public void PatternTilePixelLimitCoarsensTheTileInsteadOfRefusing()
    {
        // A 20x20 tile seen at 5x is 100x100 pixels; with a 64-pixel allowance it is drawn as 8x8 and stretched, so the layout is right and the edges are soft.
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(new SvgFormat(new SvgLimits { MaxPatternTilePixels = 64 })) };
        string svg = $"<svg {Ns} viewBox='0 0 100 100'><pattern id='p' patternUnits='userSpaceOnUse' width='20' height='20'><rect width='10' height='10' fill='red'/></pattern><rect width='100' height='100' fill='url(#p)'/></svg>";
        Rgba32[] px = Image.Load(Encoding.UTF8.GetBytes(svg), options).Resize(width: 500).ToArray<Rgba32>();
        Near(new Rgba32(255, 0, 0, 255), px[(120 * 500) + 120], 3);   // inside the red square of the tile at (20..30, 20..30) user units
        Assert.Equal(0, px[(170 * 500) + 170].A);                     // between squares
        Near(new Rgba32(255, 0, 0, 255), px[(320 * 500) + 320], 3);
    }

    [Fact]
    public void OneRenderOfAGradientDocumentEqualsItsTiles()
    {
        // Tiles of a region render equal the same pixels of the whole render, for gradients and patterns alike.
        string svg = $"<svg {Ns} viewBox='0 0 100 100'><linearGradient id='g' gradientTransform='rotate(20)'>{BlackToWhite}</linearGradient>"
            + "<pattern id='p' patternUnits='userSpaceOnUse' width='13' height='9' patternTransform='rotate(10)'><circle cx='5' cy='4' r='3' fill='navy'/></pattern>"
            + "<rect width='100' height='100' fill='url(#g)'/><circle cx='50' cy='50' r='40' fill='url(#p)' stroke='url(#g)' stroke-width='6'/></svg>";
        Image img = Image.Load(Encoding.UTF8.GetBytes(svg), SvgRenderTests.Options).Resize(width: 400);
        Rgba32[] whole = img.ToArray<Rgba32>();
        var region = new ImageRect(130, 170, 150, 140);
        Rgba32[] part = new Rgba32[region.Width * region.Height];
        img.CopyPixelsTo<Rgba32>(part, region);
        for (int y = 0; y < region.Height; y++)
        {
            for (int x = 0; x < region.Width; x++)
            {
                Rgba32 a = whole[((region.Y + y) * 400) + region.X + x];
                Rgba32 b = part[(y * region.Width) + x];
                Assert.True(Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1 && Math.Abs(a.A - b.A) <= 1, $"({x},{y}) {a} {b}");
            }
        }
    }
}
