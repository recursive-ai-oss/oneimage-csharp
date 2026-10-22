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

    /// <summary>Gets or sets the right edge of the painted content, stroke width included but not miters or anti-aliasing slack (what the natural size of a document without a size is measured from).</summary>
    public double ContentX1 { get; set; }

    /// <summary>Gets or sets the bottom edge of the painted content; see <see cref="ContentX1"/>.</summary>
    public double ContentY1 { get; set; }
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

    /// <summary>The paint server that paints the fill (a gradient or pattern), or <see langword="null"/> for the flat <see cref="FillColor"/>.</summary>
    public RPaint? FillPaint { get; init; }

    /// <summary>The paint server that paints the stroke, or <see langword="null"/> for the flat <see cref="StrokeColor"/>.</summary>
    public RPaint? StrokePaint { get; init; }

    /// <summary>Gets whether the stroke is painted before the fill (<c>paint-order</c>).</summary>
    public bool StrokeFirst { get; init; }

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

    /// <summary>Gets or sets a region (scene space) the composited layer is limited to: what a <c>clip-path</c> that needed its element's bounding box resolved to.</summary>
    public Path? ClipPath { get; set; }

    /// <summary>Gets or sets the mask the layer is composited through: another layer whose content gives the coverage (see <see cref="MaskAlpha"/>). Its own <see cref="Mask"/> narrows it further.</summary>
    public RLayer? Mask { get; set; }

    /// <summary>Gets a value indicating whether, when this layer is a mask, its alpha is the coverage (<c>mask-type: alpha</c>) instead of its luminance.</summary>
    public bool MaskAlpha { get; init; }
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
