using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

/// <summary>
/// A node of the render tree: something to paint, with a conservative bounding box in scene space (the natural-size pixel space of the
/// document, after the root <c>viewBox</c> transform). Boxes let a region render skip everything that cannot touch it.
/// </summary>
internal abstract class RNode
{
    public double X0 { get; set; }

    public double Y0 { get; set; }

    public double X1 { get; set; }

    public double Y1 { get; set; }
}

/// <summary>Fill and stroke of one geometry. All transforms and viewport clips are already resolved; the style is final.</summary>
internal sealed class RPath : RNode
{
    private sealed record Cached(float Tolerance, Path Outline);

    private Cached? _outline;

    public required Path Geometry { get; init; }

    /// <summary>Maps the geometry's own coordinates to scene space.</summary>
    public required Dom.Affine Ctm { get; init; }

    /// <summary>The geometry the stroke is made from when it differs from the fill's (zero-length figures become closed so caps draw dots).</summary>
    public Path? StrokeGeometry { get; init; }

    public bool HasFill { get; init; }

    public Rgba32 FillColor { get; init; }

    public float FillOpacity { get; init; } = 1;

    public bool EvenOdd { get; init; }

    public bool HasStroke { get; init; }

    public Rgba32 StrokeColor { get; init; }

    public float StrokeOpacity { get; init; } = 1;

    public float StrokeWidth { get; init; }

    public StrokeCap Cap { get; init; }

    public StrokeJoin Join { get; init; }

    public float MiterLimit { get; init; } = 4;

    public float[]? Dash { get; init; }

    public float DashOffset { get; init; }

    public bool Antialias { get; init; } = true;

    /// <summary>The region the shape is limited to (a viewport), in scene space, or <see langword="null"/>.</summary>
    public Path? Clip { get; init; }

    /// <summary>
    /// The stroke outline in the geometry's own space for a flattening tolerance, kept for the next tile of the same render.
    /// </summary>
    public Path StrokeOutline(float tolerance)
    {
        Cached? cached = Volatile.Read(ref _outline);
        if (cached is not null && cached.Tolerance == tolerance)
        {
            return cached.Outline;
        }

        Path outline = (StrokeGeometry ?? Geometry).GenerateOutline(StrokeWidth, Join, Cap, MiterLimit, Dash, DashOffset, tolerance);
        Volatile.Write(ref _outline, new Cached(tolerance, outline));
        return outline;
    }
}

/// <summary>Children painted into their own layer and composited as one with an opacity (a group with <c>opacity</c>, a shape with both fill and stroke and <c>opacity</c>).</summary>
internal sealed class RLayer : RNode
{
    public required List<RNode> Children { get; init; }

    public float Opacity { get; init; } = 1;
}

/// <summary>The render tree of one document, independent of the output size.</summary>
internal sealed class SvgScene
{
    public SvgScene(double width, double height, RNode[] nodes, bool hasContent)
    {
        Width = width;
        Height = height;
        Nodes = nodes;
        HasContent = hasContent;
        Grid = nodes.Length >= BoundsGrid.MinNodes ? new BoundsGrid(nodes, width, height) : null;
    }

    /// <summary>Gets the natural width in user units (CSS pixels).</summary>
    public double Width { get; }

    /// <summary>Gets the natural height in user units.</summary>
    public double Height { get; }

    /// <summary>Gets the top-level nodes in paint order.</summary>
    public RNode[] Nodes { get; }

    public bool HasContent { get; }

    public BoundsGrid? Grid { get; }
}
