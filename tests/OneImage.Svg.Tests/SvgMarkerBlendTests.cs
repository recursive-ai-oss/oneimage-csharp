using System.Text;
using OneImage.Svg;

namespace OneImage.Svg.Tests;

public class SvgMarkerBlendTests
{
    private const string Ns = SvgRenderTests.Ns;

    // A 100x100 drawing rendered at one pixel per unit; returns all pixels.
    private static Rgba32[] Render(string body, string extra = "") => SvgRenderTests.Pixels($"<svg {Ns} viewBox='0 0 100 100' {extra}>{body}</svg>", 100);

    private static Rgba32 At(Rgba32[] px, int x, int y) => px[(y * 100) + x];

    private static void IsRed(Rgba32 p) => Assert.True(p.R > 240 && p.G < 15 && p.B < 15 && p.A > 240, $"expected red, got {p}");

    private static void IsEmpty(Rgba32 p) => Assert.True(p.A < 8, $"expected nothing, got {p}");

    // A 10x10 red square marker, sized in user units and centered on the vertex.
    private const string Square = "<marker id='m' markerUnits='userSpaceOnUse' markerWidth='10' markerHeight='10' refX='5' refY='5'><rect width='10' height='10' fill='red'/></marker>";

    [Fact]
    public void StartMidAndEndMarkersSitOnTheirOwnVertices()
    {
        Rgba32[] px = Render($"{Square}<polyline points='20,30 50,30 80,30' fill='none' marker-start='url(#m)' marker-end='url(#m)'/>");
        IsRed(At(px, 20, 27));
        IsRed(At(px, 80, 27));
        IsEmpty(At(px, 50, 27));

        px = Render($"{Square}<polyline points='20,30 50,30 80,30' fill='none' marker-mid='url(#m)'/>");
        IsEmpty(At(px, 20, 27));
        IsRed(At(px, 50, 27));
        IsEmpty(At(px, 80, 27));
    }

    [Fact]
    public void TheMarkerShorthandInStyleAppliesToAllThreeAndIsIgnoredAsAnAttribute()
    {
        Rgba32[] px = Render($"{Square}<polyline points='20,30 50,30 80,30' fill='none' style='marker:url(#m)'/>");
        IsRed(At(px, 20, 27));
        IsRed(At(px, 50, 27));
        IsRed(At(px, 80, 27));
        px = Render($"{Square}<polyline points='20,30 50,30 80,30' fill='none' marker='url(#m)'/>");
        IsEmpty(At(px, 50, 27));
    }

    [Fact]
    public void MarkerUnitsDefaultToTheStrokeWidth()
    {
        // 5x5 marker scaled by a stroke width of 4 covers 20x20 units around the vertex.
        string body = "<marker id='m' markerWidth='5' markerHeight='5' refX='2.5' refY='2.5'><rect width='5' height='5' fill='red'/></marker>"
            + "<polyline points='30,50 70,50' fill='none' stroke='blue' stroke-width='4' marker-start='url(#m)'/>";
        Rgba32[] px = Render(body);
        IsRed(At(px, 22, 41));
        IsRed(At(px, 38, 59));
        IsEmpty(At(px, 20, 38));
        IsEmpty(At(px, 20, 62));
    }

    [Fact]
    public void AutoOrientTurnsTheMarkerAlongThePath()
    {
        string marker = "<marker id='m' markerUnits='userSpaceOnUse' markerWidth='20' markerHeight='4' refX='10' refY='2' orient='{0}'><rect width='20' height='4' fill='red'/></marker>";
        string path = "<polyline points='50,20 50,80' fill='none' marker-start='url(#m)'/>";
        Rgba32[] upright = Render(string.Format(marker, "auto") + path);
        IsRed(At(upright, 50, 28));      // the bar lies along the path: x 48..52, y 10..30
        IsEmpty(At(upright, 57, 20));
        Rgba32[] flat = Render(string.Format(marker, "0") + path);
        IsRed(At(flat, 57, 20));         // no turn: x 40..60, y 18..22
        IsEmpty(At(flat, 50, 28));
        Rgba32[] angled = Render(string.Format(marker, "90deg") + path);
        IsRed(At(angled, 50, 27));
        IsEmpty(At(angled, 57, 20));
    }

    [Fact]
    public void AutoStartReverseFlipsTheStartMarkerOnly()
    {
        // An arrow pointing along +x; the path runs right, so the start marker points back at 180 degrees.
        string body = "<marker id='m' markerUnits='userSpaceOnUse' markerWidth='10' markerHeight='10' refX='0' refY='5' orient='auto-start-reverse'><rect width='10' height='10' fill='red'/></marker>"
            + "<polyline points='30,50 70,50' fill='none' marker-start='url(#m)' marker-end='url(#m)'/>";
        Rgba32[] px = Render(body);
        IsRed(At(px, 25, 50));   // start: reversed, so the square extends to the left of the vertex
        IsEmpty(At(px, 35, 50));
        IsRed(At(px, 75, 50));   // end: extends to the right
    }

    [Fact]
    public void MarkersClipToTheirViewportUnlessOverflowIsVisible()
    {
        string content = "<rect x='-20' y='-20' width='60' height='60' fill='red'/>";
        string hidden = $"<marker id='m' markerUnits='userSpaceOnUse' markerWidth='10' markerHeight='10'>{content}</marker><polyline points='50,50 80,80' fill='none' marker-start='url(#m)'/>";
        Rgba32[] px = Render(hidden);
        IsRed(At(px, 55, 55));
        IsEmpty(At(px, 45, 45));
        string visible = hidden.Replace("<marker ", "<marker overflow='visible' ", StringComparison.Ordinal);
        px = Render(visible);
        IsRed(At(px, 45, 45));
    }

    [Fact]
    public void ViewBoxAndPreserveAspectRatioFitTheMarkerContent()
    {
        // A 20x10 viewport showing a 10x10 viewBox with xMinYMid meet: the square is 10 wide, starting at the viewport's left.
        string body = "<marker id='m' markerUnits='userSpaceOnUse' markerWidth='20' markerHeight='10' viewBox='0 0 10 10' preserveAspectRatio='xMinYMid meet'><rect width='10' height='10' fill='red'/></marker>"
            + "<polyline points='40,40 60,60' fill='none' marker-start='url(#m)'/>";
        Rgba32[] px = Render(body);
        IsRed(At(px, 45, 45));
        IsEmpty(At(px, 55, 45));
    }

    [Fact]
    public void CurvesAndArcsGetTheirDirectionsFromTheirTangents()
    {
        // A cubic that leaves upward and arrives going right: the start marker bar points up, the end bar points right.
        string marker = "<marker id='m' markerUnits='userSpaceOnUse' markerWidth='20' markerHeight='4' refX='10' refY='2' orient='auto'><rect width='20' height='4' fill='red'/></marker>";
        Rgba32[] px = Render(marker + "<path d='M 20 80 C 20 30 30 20 80 20' fill='none' marker-start='url(#m)' marker-end='url(#m)'/>");
        IsRed(At(px, 20, 72));    // vertical bar at the start
        IsEmpty(At(px, 12, 80));
        IsRed(At(px, 88, 20));    // horizontal bar at the end
        IsEmpty(At(px, 80, 28));
    }

    [Fact]
    public void ZeroLengthSegmentsBorrowTheDirectionOfTheirNeighbors()
    {
        // The repeated point gives the middle vertex no direction of its own; it follows the segment that has one.
        string marker = "<marker id='m' markerUnits='userSpaceOnUse' markerWidth='20' markerHeight='4' refX='10' refY='2' orient='auto'><rect width='20' height='4' fill='red'/></marker>";
        Rgba32[] px = Render(marker + "<path d='M 10 50 L 50 50 L 50 50 L 90 50' fill='none' marker-mid='url(#m)'/>");
        IsRed(At(px, 58, 50));
        IsEmpty(At(px, 50, 58));
    }

    [Fact]
    public void ShapesGetMarkersAtTheirOutlineVertices()
    {
        // SVG 2 puts markers on every basic shape: a rect has a vertex at each corner and its start and end both sit on the first.
        Rgba32[] px = Render($"{Square}<rect x='30' y='30' width='40' height='40' fill='none' marker-start='url(#m)' marker-mid='url(#m)' marker-end='url(#m)'/>");
        IsRed(At(px, 30, 30));
        IsRed(At(px, 70, 30));
        IsRed(At(px, 70, 70));
        IsRed(At(px, 30, 70));
        IsEmpty(At(px, 50, 50));
    }

    [Fact]
    public void PaintOrderPlacesMarkersBetweenFillAndStroke()
    {
        string shape = "<polygon points='20,20 80,20 80,80 20,80' fill='green' stroke='none' marker-start='url(#m)' style='paint-order:{0}'/>";
        Rgba32[] over = Render(Square + string.Format(shape, "normal"));
        IsRed(At(over, 22, 22));
        Rgba32[] under = Render(Square + string.Format(shape, "markers"));
        Assert.True(At(under, 22, 22).G > 100 && At(under, 22, 22).R < 15, $"expected green over the marker, got {At(under, 22, 22)}");
        IsRed(At(under, 17, 17));     // the part outside the fill still shows
    }

    [Fact]
    public void ContextPaintsTakeTheShapesFillAndStroke()
    {
        string body = "<marker id='m' markerUnits='userSpaceOnUse' markerWidth='10' markerHeight='10' refX='5' refY='5'><rect width='10' height='10' fill='context-stroke'/></marker>"
            + "<polyline points='30,50 70,50' fill='none' stroke='red' marker-start='url(#m)'/>";
        IsRed(At(Render(body), 28, 47));
    }

    [Fact]
    public void ShapeOpacityAppliesToTheShapeAndItsMarkersTogether()
    {
        // The marker overlaps the shape: as one half-transparent group the overlap is no denser than the rest.
        string body = $"{Square}<polygon points='30,30 70,30 70,70 30,70' fill='red' opacity='0.5' marker-start='url(#m)'/>";
        Rgba32[] px = Render(body);
        Assert.InRange(At(px, 32, 32).A, 126, 129);
        Assert.InRange(At(px, 50, 50).A, 126, 129);
    }

    [Fact]
    public void AMarkerInsideItselfTerminates()
    {
        string body = "<marker id='m' markerUnits='userSpaceOnUse' markerWidth='10' markerHeight='10' refX='5' refY='5'><path d='M 2 2 L 8 8' stroke='red' marker-start='url(#m)' marker-end='url(#m)'/></marker>"
            + "<path d='M 30 30 L 70 70' stroke='blue' marker-start='url(#m)' marker-end='url(#m)'/>";
        Rgba32[] px = Render(body);
        Assert.True(At(px, 30, 30).R > 200);
    }

    [Fact]
    public void ManyVerticesCountAgainstTheInstanceLimit()
    {
        var points = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            points.Append(CultureInfo(i)).Append(',').Append(CultureInfo(i % 7)).Append(' ');
        }

        string svg = $"<svg {Ns} viewBox='0 0 100 100'>{Square}<polyline points='{points}' fill='none' marker-mid='url(#m)'/></svg>";
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(new SvgFormat(new SvgLimits { MaxInstances = 500 })) };
        Assert.Throws<ImageLimitException>(() => Image.Load(Encoding.UTF8.GetBytes(svg), options).Resize(width: 100).ToArray<Rgba32>());
    }

    private static string CultureInfo(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- mix-blend-mode and isolation ----

    private const string Backdrop = "<rect width='100' height='100' fill='rgb(128,128,128)'/>";

    [Fact]
    public void MultiplyBlendsAgainstWhatIsBehind()
    {
        // 128/255 times 128/255 is 64/255 on every channel.
        Rgba32[] px = Render($"{Backdrop}<rect width='50' height='100' fill='rgb(128,128,128)' style='mix-blend-mode:multiply'/>");
        Assert.InRange(At(px, 10, 50).R, 63, 66);
        Assert.InRange(At(px, 80, 50).R, 127, 129);
    }

    [Fact]
    public void BlendModeWorksOnGroupsAndWithOpacity()
    {
        Rgba32[] px = Render($"{Backdrop}<g style='mix-blend-mode:difference' opacity='0.5'><rect width='50' height='100' fill='white'/></g>");
        // difference with white is 127 on gray 128; half opacity mixes that with the backdrop: about 127.5 and 128 -> 128.
        Assert.InRange(At(px, 10, 50).R, 126, 130);
        px = Render($"{Backdrop}<g style='mix-blend-mode:difference'><rect width='50' height='100' fill='rgb(128,128,128)'/></g>");
        Assert.True(At(px, 10, 50).R < 3);
    }

    [Fact]
    public void IsolationKeepsABlendInsideTheGroup()
    {
        // Inside an isolated group the multiply sees only the group (transparent), so the rectangle keeps its own color.
        string inner = "<rect width='50' height='100' fill='rgb(128,128,128)' style='mix-blend-mode:multiply'/>";
        Rgba32[] isolated = Render($"{Backdrop}<g style='isolation:isolate'>{inner}</g>");
        Assert.InRange(isolated[(50 * 100) + 10].R, 127, 129);
        Rgba32[] open = Render($"{Backdrop}<g>{inner}</g>");
        Assert.InRange(open[(50 * 100) + 10].R, 63, 66);
    }

    [Fact]
    public void BlendAndIsolationAreIgnoredAsPresentationAttributes()
    {
        Rgba32[] px = Render($"{Backdrop}<rect width='50' height='100' fill='rgb(128,128,128)' mix-blend-mode='multiply'/>");
        Assert.InRange(px[(50 * 100) + 10].R, 127, 129);
    }

    [Fact]
    public void UnknownBlendModeIsIgnored()
    {
        Rgba32[] px = Render($"{Backdrop}<rect width='50' height='100' fill='rgb(10,20,30)' style='mix-blend-mode:bogus'/>");
        Assert.Equal(new Rgba32(10, 20, 30, 255), px[(50 * 100) + 10]);
    }

    [Fact]
    public void NestedBlendLayersStayInsideTheLayerLimit()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            sb.Append("<g style='mix-blend-mode:multiply'>");
        }

        sb.Append("<rect width='10' height='10'/>");
        for (int i = 0; i < 40; i++)
        {
            sb.Append("</g>");
        }

        Assert.Throws<ImageLimitException>(() => Render(sb.ToString()));
    }

    // ---- shape-rendering ----

    [Fact]
    public void CrispEdgesTurnOffAntiAliasing()
    {
        // A rectangle at a half-pixel edge: anti-aliased it leaves a partial pixel, crisp it leaves only full or empty ones.
        Rgba32[] smooth = Render("<rect x='10.5' y='10' width='30' height='30' fill='red'/>");
        Assert.InRange(At(smooth, 10, 20).A, 100, 160);
        Rgba32[] crisp = Render("<rect x='10.5' y='10' width='30' height='30' fill='red' shape-rendering='crispEdges'/>");
        Assert.True(At(crisp, 10, 20).A is 0 or 255);
        Assert.True(At(crisp, 40, 20).A is 0 or 255);
    }
}
