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
    private Brush? _fillBrush;
    private Brush? _strokeBrush;

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

    /// <summary>The flat fill color as a brush, made once and shared by every tile (brushes are immutable).</summary>
    public Brush FillBrush => _fillBrush ??= Brush.Solid(FillColor);

    /// <summary>The flat stroke color as a brush, made once and shared by every tile.</summary>
    public Brush StrokeBrush => _strokeBrush ??= Brush.Solid(StrokeColor);

    /// <summary>A copy that paints only the fill or only the stroke (<c>paint-order</c> puts markers between them).</summary>
    public RPath Part(bool fill)
    {
        return new RPath
        {
            Geometry = Geometry,
            Ctm = Ctm,
            StrokeGeometry = StrokeGeometry,
            HasFill = fill && HasFill,
            FillColor = FillColor,
            FillPaint = FillPaint,
            StrokePaint = StrokePaint,
            StrokeFirst = false,
            FillOpacity = FillOpacity,
            EvenOdd = EvenOdd,
            HasStroke = !fill && HasStroke,
            StrokeColor = StrokeColor,
            StrokeOpacity = StrokeOpacity,
            StrokeWidth = StrokeWidth,
            Cap = Cap,
            Join = Join,
            MiterLimit = MiterLimit,
            Dash = Dash,
            DashOffset = DashOffset,
            Antialias = Antialias,
            Clip = Clip,
            X0 = X0,
            Y0 = Y0,
            X1 = X1,
            Y1 = Y1,
            ContentX1 = ContentX1,
            ContentY1 = ContentY1,
        };
    }

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

    /// <summary>Gets or sets the filters (the <c>filter</c> property) that run on the layer's content before the clip, mask and opacity: each one's output is the next one's input.</summary>
    public RFilter[]? Filters { get; set; }

    /// <summary>Gets a value indicating whether, when this layer is a mask, its alpha is the coverage (<c>mask-type: alpha</c>) instead of its luminance.</summary>
    public bool MaskAlpha { get; init; }

    /// <summary>How the finished group blends with what is behind it (<c>mix-blend-mode</c>); normal is plain source-over.</summary>
    public BlendMode Blend { get; init; }

    /// <summary>Gets a value indicating whether the group adds to what is behind it (<c>mix-blend-mode: plus-lighter</c>).</summary>
    public bool Plus { get; init; }
}

/// <summary>
/// A raster picture read from a <c>data:</c> URI: the decoded-on-demand image and its pixels as the renderer samples them, shared by every
/// node that draws it (a picture reached through many <c>use</c> elements is decoded once).
/// </summary>
internal sealed class RasterSource
{
    private readonly object _gate = new();
    private SampledPixels? _prepared;
    private bool _failed;

    public RasterSource(Image image) => Image = image;

    /// <summary>Gets the picture; its pixels are not read until <see cref="Prepared"/> asks.</summary>
    public Image Image { get; }

    /// <summary>
    /// Returns the raster as 8-bit straight RGBA, in sRGB, shrunk to <paramref name="width"/> x <paramref name="height"/> when that is smaller than
    /// the picture (so a picture drawn small is filtered, not skipped over), kept for the next tile at the same size. A picture that cannot be
    /// decoded draws nothing (<see langword="null"/>), now and for every later tile.
    /// </summary>
    public SampledPixels? Prepared(int width, int height)
    {
        lock (_gate)
        {
            if (_failed)
            {
                return null;
            }

            if (_prepared is not null && _prepared.Width == width && _prepared.Height == height)
            {
                return _prepared;
            }

            try
            {
                Image src = Image;
                if (src.Metadata.EmbeddedColorProfile is not null || src.Format.Model == ColorModel.Cmyk)
                {
                    try
                    {
                        src = src.ConvertColor(ColorProfile.Srgb);
                    }
                    catch (ImageException)
                    {
                        // A profile that cannot be used: the pixels are taken as they are.
                    }
                }

                if (width != src.Width || height != src.Height)
                {
                    // Shrinking uses the library's default filter, wide enough not to alias; on encoded values, like the rest of the drawing.
                    src = src.Resize(width, height, Fit.Stretch, linear: false);
                }

                return _prepared = new SampledPixels(src.ToArray<Rgba32>(), width, height);
            }
            catch (ImageFormatException)
            {
                // A truncated or corrupt picture is a dangling image: nothing is drawn.
                _failed = true;
                return null;
            }
        }
    }
}

/// <summary>
/// A picture: a raster image or a nested SVG document placed by <see cref="Ctm"/>, which maps the picture's own coordinates (pixels for a
/// raster, user units for a nested document) to scene space.
/// </summary>
internal sealed class RImage : RNode
{
    /// <summary>Gets the picture's size in its own coordinates.</summary>
    public required double Width { get; init; }

    public required double Height { get; init; }

    public required Dom.Affine Ctm { get; init; }

    /// <summary>Gets the raster; <see langword="null"/> for a nested document.</summary>
    public RasterSource? Raster { get; init; }

    /// <summary>Gets the nested document's render tree; <see langword="null"/> for a raster.</summary>
    public SvgScene? Nested { get; init; }

    /// <summary>The region the picture is limited to (a viewport that cuts a sliced picture), in scene space, or <see langword="null"/>.</summary>
    public Path? Clip { get; init; }

    public float Opacity { get; init; } = 1;

    /// <summary>Gets whether the picture is sampled without smoothing (<c>image-rendering: optimizeSpeed</c>, <c>pixelated</c>, <c>crisp-edges</c>).</summary>
    public bool Pixelated { get; init; }
}

/// <summary>The pixels of a raster picture ready to be sampled.</summary>
internal sealed record SampledPixels(Rgba32[] Pixels, int Width, int Height);

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
