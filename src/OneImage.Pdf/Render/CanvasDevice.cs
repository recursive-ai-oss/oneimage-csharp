using System.Drawing;
using System.Numerics;
using OneImage.Drawing;
using OnePdf;
using DrawingPath = OneImage.Drawing.Path;

namespace OneImage.Pdf.Render;

/// <summary>
/// Draws what a display list replay says onto a canvas of one cell. Coordinates arrive in the page's interpreted output space (points,
/// y down) and are mapped to cell pixels by <c>view</c>. All geometry is converted in double precision after the cell origin has been
/// subtracted, and shapes are rasterized by exact area, so a cell is drawn the same way whatever else is being drawn.
/// </summary>
/// <remarks>
/// Transparency: a group, or any paint under a soft mask, is drawn into a transparent cell-sized layer and composited with the group's
/// opacity, blend mode and mask. A non-isolated group whose composite would change nothing is painted straight onto its parent; other
/// non-isolated groups are treated as isolated, and knockout groups as non-knockout.
/// </remarks>
internal sealed partial class CanvasDevice : IPdfDevice, IDisposable
{
    private readonly PageRender _page;
    private readonly PdfMatrix _view;
    private readonly Canvas _root;
    private readonly int _w, _h;
    private readonly CancellationToken _ct;
    private readonly int _depth;
    private readonly Rgba32? _tint;
    private readonly List<Frame> _frames = [];
    private readonly List<ClipState> _clips = [];
    private readonly Dictionary<int, Canvas> _masks = [];
    private Canvas _target;
    private ClipState _clip = ClipState.None;
    private PathBuilder? _textClip;
    private bool _disposed;

    public CanvasDevice(PageRender page, in PdfMatrix view, Canvas root, int depth, Rgba32? tint, CancellationToken ct)
    {
        _page = page;
        _view = view;
        _root = root;
        _target = root;
        _w = root.Width;
        _h = root.Height;
        _ct = ct;
        _depth = depth;
        _tint = tint;
    }

    private PdfRenderLimits Limits => _page.Limits;

    private bool Skip => _clip.Empty;

    /// <summary>Gives back the layers and masks of a replay that ended early (an exception) or normally.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Frame f in _frames)
        {
            f.Layer?.Dispose();
        }

        _frames.Clear();
        foreach (Canvas c in _masks.Values)
        {
            c.Dispose();
        }

        _masks.Clear();
    }

    public void BeginPage(in PdfPageInfo page)
    {
    }

    public void EndPage()
    {
    }

    // ---- paths ----

    public void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        if (Skip)
        {
            return;
        }

        _ct.ThrowIfCancellationRequested();
        PdfMatrix m = state.Ctm.Then(_view);
        DrawingPath? dev = PathConvert.ToPath(path, m);
        if (dev is not null)
        {
            Paint(dev, evenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero, state.FillColor, state.FillAlpha, state);
        }
    }

    public void StrokePath(in PdfPath path, in PdfGraphicsState state)
    {
        if (Skip)
        {
            return;
        }

        _ct.ThrowIfCancellationRequested();
        PdfMatrix m = state.Ctm.Then(_view);
        DrawingPath? outline = StrokeOutline(path, state, m);
        if (outline is not null)
        {
            Paint(outline, IntersectionRule.NonZero, state.StrokeColor, state.StrokeAlpha, state);
        }
    }

    /// <summary>The stroke of a user-space path as a device-space outline to fill non-zero, or null when it draws nothing.</summary>
    private static DrawingPath? StrokeOutline(in PdfPath path, in PdfGraphicsState state, in PdfMatrix m)
    {
        double scale = PathConvert.MeanScale(m);
        if (!(scale > 1e-9) || !double.IsFinite(scale))
        {
            return null;
        }

        DrawingPath? user = PathConvert.ToPath(path, PdfMatrix.Identity, clamp: false);
        DrawingPath? outline = user is null ? null : StrokeUserPath(user, state, m, scale);
        if (state.LineCap != 1)
        {
            return outline;
        }

        // 8.5.3.2: a subpath that never leaves its first point, stroked with round caps, paints a disc of the line width. The
        // stroker has no direction to cap, so the discs are added here, as ellipses in user space (the CTM may be skewed).
        DrawingPath? discs = ZeroLengthDiscs(path, state, m, scale);
        if (discs is null)
        {
            return outline;
        }

        return outline is null ? discs : DrawingPath.Combine(outline, discs);
    }

    private static DrawingPath? ZeroLengthDiscs(in PdfPath path, in PdfGraphicsState state, in PdfMatrix m, double scale)
    {
        double width = Math.Max(state.LineWidth, 1.0 / scale);
        if (!double.IsFinite(width) || width * scale > 1e7)
        {
            return null;
        }

        ReadOnlySpan<byte> verbs = path.VerbBytes.Span;
        ReadOnlySpan<double> pts = path.Points.Span;
        PathBuilder? b = null;
        int p = 0;
        double sx = 0, sy = 0;
        bool inSub = false, moved = false, drawn = false;
        void Flush()
        {
            if (inSub && drawn && !moved)
            {
                b ??= new PathBuilder();
                b.AddPath(DrawingPath.Circle((float)sx, (float)sy, (float)(width / 2)));
            }
        }

        for (int i = 0; i < verbs.Length; i++)
        {
            switch ((PdfPathVerb)verbs[i])
            {
                case PdfPathVerb.Move:
                    Flush();
                    if (p + 2 > pts.Length) { return Finish(b, m); }
                    sx = pts[p]; sy = pts[p + 1]; p += 2;
                    inSub = true; moved = false; drawn = false;
                    break;
                case PdfPathVerb.Line:
                    if (p + 2 > pts.Length) { return Finish(b, m); }
                    moved |= pts[p] != sx || pts[p + 1] != sy; drawn = true; p += 2;
                    break;
                case PdfPathVerb.Curve:
                    if (p + 6 > pts.Length) { return Finish(b, m); }
                    for (int k = 0; k < 6; k += 2) { moved |= pts[p + k] != sx || pts[p + k + 1] != sy; }
                    drawn = true; p += 6;
                    break;
                case PdfPathVerb.Close:
                    drawn = true;
                    break;
                default:
                    break;
            }
        }

        Flush();
        return Finish(b, m);

        static DrawingPath? Finish(PathBuilder? builder, in PdfMatrix map)
        {
            if (builder is null)
            {
                return null;
            }

            DrawingPath dev = builder.Build().Transform(PathConvert.ToMatrix(map));
            return dev.IsEmpty ? null : dev;
        }
    }

    private static DrawingPath? StrokeUserPath(DrawingPath user, in PdfGraphicsState state, in PdfMatrix m, double scale)
    {
        // A line thinner than a pixel is drawn one pixel wide (width 0 means exactly that: the thinnest line the device can draw).
        double minWidth = 1.0 / scale;
        double width = state.LineWidth >= minWidth ? state.LineWidth : minWidth;
        if (!double.IsFinite(width) || width * scale > 1e7)
        {
            return null;
        }

        float[] dash = Dashes(state, user, scale);
        StrokeJoin join = state.LineJoin switch { 1 => StrokeJoin.Round, 2 => StrokeJoin.Bevel, _ => StrokeJoin.Miter };
        StrokeCap cap = state.LineCap switch { 1 => StrokeCap.Round, 2 => StrokeCap.Square, _ => StrokeCap.Butt };
        double miter = double.IsFinite(state.MiterLimit) && state.MiterLimit >= 1 ? state.MiterLimit : 10;
        double tolerance = DrawingPath.DefaultTolerance / scale;
        DrawingPath outline = StrokeBuilder.Build(user, width, join, cap, miter, dash, state.DashPhase, tolerance);
        if (outline.IsEmpty)
        {
            return null;
        }

        DrawingPath dev = outline.Transform(PathConvert.ToMatrix(m));
        return dev.IsEmpty ? null : dev;
    }

    private static float[] Dashes(in PdfGraphicsState state, DrawingPath user, double scale)
    {
        ReadOnlySpan<double> d = state.Dash.Span;
        if (d.IsEmpty || d.Length > 1024)
        {
            return [];
        }

        double sum = 0;
        foreach (double v in d)
        {
            if (!(v >= 0) || !double.IsFinite(v))
            {
                return [];
            }

            sum += v;
        }

        // All-zero dashes, dashes far below a pixel and dash counts that would bury the rasterizer draw a solid line instead.
        double devPeriod = sum * scale;
        if (!(sum > 0) || devPeriod < 0.05 || (user.Length * scale / devPeriod * d.Length) > 2_000_000)
        {
            return [];
        }

        var result = new float[d.Length % 2 == 0 ? d.Length : d.Length * 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (float)d[i % d.Length];
        }

        return result;
    }

    // ---- painting a shape ----

    /// <summary>Fills a device-space shape with the paint of a PDF color (solid, shading pattern or tiling pattern).</summary>
    private void Paint(DrawingPath shape, IntersectionRule rule, in PdfColor color, double alpha, in PdfGraphicsState state)
    {
        Brush? brush = MakeBrush(color);
        if (brush is null)
        {
            return;
        }

        PaintBrush(brush, shape, rule, alpha, state);
    }

    private void PaintBrush(Brush brush, DrawingPath shape, IntersectionRule rule, double alpha, in PdfGraphicsState state)
    {
        if (!(alpha > 0))
        {
            return;
        }

        Canvas? layer = BeginMasked(state);
        Canvas canvas = layer ?? _target;
        BlendMode blend = layer is null ? ParseBlend(state.BlendMode) : BlendMode.Normal;
        // 11.4.6: in a knockout group each object is composited against the group's initial backdrop, not against the objects
        // before it, so inside the shape it replaces them (Src), weighted by coverage at the edges. Blend modes inside a knockout
        // group are not applied (they would need the initial backdrop of a non-isolated group).
        bool knock = layer is null && InKnockoutGroup;
        try
        {
            canvas.Fill(brush, shape, true, rule, (float)Math.Min(alpha, 1), knock ? BlendMode.Normal : blend, knock ? AlphaComposition.Src : AlphaComposition.SrcOver, false, null, _clip.Path, _clip.Rule);
            if (layer is null)
            {
                ShapeShadow?.Fill(brush, shape, true, rule, (float)Math.Min(alpha, 1), BlendMode.Normal, AlphaComposition.SrcOver, false, null, _clip.Path, _clip.Rule);
            }
        }
        catch
        {
            layer?.Dispose();
            throw;
        }

        if (layer is not null)
        {
            CompositeLayer(layer, 1, ParseBlend(state.BlendMode), state.SoftMaskId);
        }
    }

    private Brush? MakeBrush(in PdfColor color)
    {
        if (_tint is { } tint)
        {
            return Brush.Solid(tint);
        }

        if (color.Space.Kind != PdfColorSpaceKind.Pattern)
        {
            return Brush.Solid(PdfColors.ToRgba(color));
        }

        return color.Pattern switch
        {
            PdfShadingPattern sp => ShadingPatternBrush(sp),
            PdfTilingPattern tp => TilingPatternBrush(tp, color),
            _ => null,
        };
    }

    private ShadingBrush ShadingPatternBrush(PdfShadingPattern pattern)
    {
        PdfShading s = pattern.Shading;
        Span<double> bg = stackalloc double[32];
        Rgba32? background = s.TryGetBackground(bg) ? PdfColors.ToRgba(s.FinalSpace, bg[..s.ColorComponentCount]) : null;
        return new ShadingBrush(s, pattern.Matrix.Then(_view), s.ShadingType is 2 or 3 ? _page.GetLut(s) : null, background, _w, _h);
    }

    /// <summary>The paint of a mask-layer rectangle: lays down a layer when the state has a soft mask that is known here.</summary>
    private Canvas? BeginMasked(in PdfGraphicsState state)
    {
        if (state.SoftMaskId == 0 || !_masks.ContainsKey(state.SoftMaskId))
        {
            return null;
        }

        return new Canvas(_w, _h, PixelFormat.Rgba8);
    }

    // ---- clipping ----

    public void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        if (_clips.Count >= Limits.MaxClipDepth)
        {
            throw new ImageLimitException($"Clips are nested deeper than {Limits.MaxClipDepth}.");
        }

        _clips.Add(_clip);
        if (_clip.Empty)
        {
            return;
        }

        PdfMatrix m = state.Ctm.Then(_view);
        IntersectionRule rule = evenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero;
        if (TryRect(path, m, out RectangleF rect))
        {
            _clip = _clip.IntersectRect(rect);
            return;
        }

        DrawingPath? dev = PathConvert.ToPath(path, m);
        _clip = dev is null ? ClipState.Nothing : _clip.IntersectPath(dev, rule);
    }

    public void PushTextClip(in PdfGraphicsState state)
    {
        if (_clips.Count >= Limits.MaxClipDepth)
        {
            throw new ImageLimitException($"Clips are nested deeper than {Limits.MaxClipDepth}.");
        }

        _clips.Add(_clip);
        PathBuilder? b = _textClip;
        _textClip = null;
        if (_clip.Empty)
        {
            return;
        }

        DrawingPath glyphs = b?.Build() ?? DrawingPath.Empty;
        _clip = glyphs.IsEmpty ? ClipState.Nothing : _clip.IntersectPath(glyphs, IntersectionRule.NonZero);
    }

    public void PopClip()
    {
        if (_clips.Count > 0)
        {
            _clip = _clips[^1];
            _clips.RemoveAt(_clips.Count - 1);
        }
    }

    /// <summary>True when the path is one axis-aligned rectangle after mapping through <paramref name="m"/>.</summary>
    private static bool TryRect(in PdfPath path, in PdfMatrix m, out RectangleF rect)
    {
        rect = default;
        ReadOnlySpan<byte> v = path.VerbBytes.Span;
        ReadOnlySpan<double> p = path.Points.Span;
        int n = v.Length;
        if (n < 4 || n > 6 || v[0] != (byte)PdfPathVerb.Move)
        {
            return false;
        }

        int lines = 0;
        for (int i = 1; i < n; i++)
        {
            if (v[i] == (byte)PdfPathVerb.Line)
            {
                lines++;
            }
            else if (!(v[i] == (byte)PdfPathVerb.Close && i == n - 1))
            {
                return false;
            }
        }

        if (lines is not (3 or 4) || p.Length != (lines + 1) * 2)
        {
            return false;
        }

        Span<double> x = stackalloc double[5];
        Span<double> y = stackalloc double[5];
        for (int i = 0; i <= lines; i++)
        {
            (x[i], y[i]) = m.Transform(p[i * 2], p[(i * 2) + 1]);
            if (!double.IsFinite(x[i] + y[i]))
            {
                return false;
            }
        }

        if (lines == 4 && (Math.Abs(x[4] - x[0]) > 1e-6 || Math.Abs(y[4] - y[0]) > 1e-6))
        {
            return false;
        }

        // Four corners, each step along one axis only and the axes alternating.
        const double Eps = 1e-6;
        bool horizontalFirst = Math.Abs(y[1] - y[0]) < Eps && Math.Abs(x[2] - x[1]) < Eps && Math.Abs(y[3] - y[2]) < Eps && Math.Abs(x[0] - x[3]) < Eps;
        bool verticalFirst = Math.Abs(x[1] - x[0]) < Eps && Math.Abs(y[2] - y[1]) < Eps && Math.Abs(x[3] - x[2]) < Eps && Math.Abs(y[0] - y[3]) < Eps;
        if (!horizontalFirst && !verticalFirst)
        {
            return false;
        }

        double x0 = Math.Min(Math.Min(x[0], x[1]), Math.Min(x[2], x[3])), x1 = Math.Max(Math.Max(x[0], x[1]), Math.Max(x[2], x[3]));
        double y0 = Math.Min(Math.Min(y[0], y[1]), Math.Min(y[2], y[3])), y1 = Math.Max(Math.Max(y[0], y[1]), Math.Max(y[2], y[3]));
        const double Big = 1 << 24;
        rect = new RectangleF((float)Math.Clamp(x0, -Big, Big), (float)Math.Clamp(y0, -Big, Big), (float)(Math.Clamp(x1, -Big, Big) - Math.Clamp(x0, -Big, Big)), (float)(Math.Clamp(y1, -Big, Big) - Math.Clamp(y0, -Big, Big)));
        return true;
    }

    /// <summary>The clip in force: nothing, a rectangle, or a path with the rule to fill it by. Intersecting keeps it as one simple shape.</summary>
    private sealed class ClipState
    {
        public static readonly ClipState None = new(null, IntersectionRule.NonZero, null, false);
        public static readonly ClipState Nothing = new(null, IntersectionRule.NonZero, null, true);

        private ClipState(DrawingPath? path, IntersectionRule rule, RectangleF? rect, bool empty)
        {
            Path = path;
            Rule = rule;
            Rect = rect;
            Empty = empty;
        }

        public DrawingPath? Path { get; }

        public IntersectionRule Rule { get; }

        public RectangleF? Rect { get; }

        public bool Empty { get; }

        public ClipState IntersectRect(RectangleF r)
        {
            if (Empty)
            {
                return this;
            }

            if (Rect is { } cur)
            {
                var i = RectangleF.Intersect(cur, r);
                return i.Width <= 0 || i.Height <= 0 ? Nothing : FromRect(i);
            }

            if (Path is null)
            {
                return r.Width <= 0 || r.Height <= 0 ? Nothing : FromRect(r);
            }

            return IntersectPath(DrawingPath.Rectangle(r.X, r.Y, r.Width, r.Height), IntersectionRule.NonZero);
        }

        public ClipState IntersectPath(DrawingPath p, IntersectionRule rule)
        {
            if (Empty)
            {
                return this;
            }

            if (Path is null)
            {
                return new ClipState(p, rule, null, false);
            }

            DrawingPath both = Path.Intersect(p, Rule, rule);
            return both.IsEmpty ? Nothing : new ClipState(both, IntersectionRule.NonZero, null, false);
        }

        private static ClipState FromRect(RectangleF r) =>
            new(DrawingPath.Rectangle(r.X, r.Y, r.Width, r.Height), IntersectionRule.NonZero, r, false);
    }

    internal static BlendMode ParseBlend(string name) => name switch
    {
        "Multiply" => BlendMode.Multiply,
        "Screen" => BlendMode.Screen,
        "Overlay" => BlendMode.Overlay,
        "Darken" => BlendMode.Darken,
        "Lighten" => BlendMode.Lighten,
        "ColorDodge" => BlendMode.ColorDodge,
        "ColorBurn" => BlendMode.ColorBurn,
        "HardLight" => BlendMode.HardLight,
        "SoftLight" => BlendMode.SoftLight,
        "Difference" => BlendMode.Difference,
        "Exclusion" => BlendMode.Exclusion,
        "Hue" => BlendMode.Hue,
        "Saturation" => BlendMode.Saturation,
        "Color" => BlendMode.Color,
        "Luminosity" => BlendMode.Luminosity,
        _ => BlendMode.Normal,
    };

    // ---- marked content and annotations carry no pixels ----

    public void BeginMarkedContent(string tag, PdfDictionary? properties)
    {
    }

    public void EndMarkedContent()
    {
    }

    public void BeginAnnotation(in PdfAnnotationInfo annotation)
    {
    }

    public void EndAnnotation()
    {
    }

    // A Type 3 glyph's content arrives as ordinary drawing calls whose CTM already includes the font matrix.
    public void BeginType3Glyph(in PdfType3GlyphInfo glyph, in PdfGraphicsState state)
    {
    }

    public void EndType3Glyph()
    {
    }
}
