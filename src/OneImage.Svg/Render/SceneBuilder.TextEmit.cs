using OneImage.Drawing;
using OneImage.Svg.Dom;
using OneImage.Text;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

// Text: from placed clusters to render nodes (glyph outlines, decorations) and to clip geometry.
internal sealed partial class SceneBuilder
{
    /// <summary>How the glyphs of one span are painted: resolved fill and stroke with the stroke's shape.</summary>
    private sealed class SpanPaint
    {
        public bool OptimizeSpeed;
        public bool HasFill;
        public Rgba32 FillColor;
        public RPaint? FillServer;
        public bool HasStroke;
        public Rgba32 StrokeColor;
        public RPaint? StrokeServer;
        public float Width;
        public StrokeCap Cap;
        public StrokeJoin Join;
        public float Miter = 4;
        public float[]? Dash;
        public float DashOffset;
    }

    private readonly Dictionary<TSpan, SpanPaint> _spanPaints = [];

    private SpanPaint PaintOfSpan(TSpan sp, Frame f, Affine ctm, ref BBoxHolder? bbox)
    {
        if (_spanPaints.TryGetValue(sp, out SpanPaint? cached))
        {
            return cached;
        }

        SvgStyle st = sp.St;
        var result = new SpanPaint { OptimizeSpeed = sp.Ts.OptimizeSpeed };
        if (st.Visible)
        {
            ResolvedPaint fill = ResolvePaint(st.Fill, f, st, ctm, null, ref bbox);
            result.HasFill = fill.Kind != ResolvedKind.None;
            result.FillColor = fill.Color;
            result.FillServer = fill.Server;
            ResolvedPaint stroke = ResolvePaint(st.Stroke, f, st, ctm, null, ref bbox);
            if (stroke.Kind != ResolvedKind.None)
            {
                float width = Math.Min(Coord.F(Resolve(st.StrokeWidth, Axis.Other, st, f.Vp)), Pen.MaxWidth);
                result.HasStroke = width > 0 && float.IsFinite(width);
                result.StrokeColor = stroke.Color;
                result.StrokeServer = stroke.Server;
                result.Width = width;
                result.Dash = result.HasStroke ? ResolveDash(st, f.Vp) : null;
                result.DashOffset = result.Dash is null ? 0 : Coord.F(Resolve(st.DashOffset, Axis.Other, st, f.Vp));
                result.Miter = (float)Math.Clamp(st.MiterLimit, 1, 1e6);
                result.Join = st.LineJoin switch { 1 => StrokeJoin.Round, 2 => StrokeJoin.Bevel, _ => StrokeJoin.Miter };
                result.Cap = st.LineCap switch { 1 => StrokeCap.Round, 2 => StrokeCap.Square, _ => StrokeCap.Butt };
            }
        }

        _spanPaints[sp] = result;
        return result;
    }

    // The cluster's outline in its own space; the cluster's matrix carries its horizontal scale (textLength with lengthAdjust).
    private static Path GlyphGeometry(Placed p) => p.Cluster.GetOutline();

    // The bounds of everything the text draws, in the text element's user space; null when nothing has ink.
    private static Box? InkBox(List<Placed> placed)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (Placed p in placed)
        {
            if (p.Skip || !p.Span.St.Visible)
            {
                continue;
            }

            Path outline = p.Cluster.GetOutline();
            if (outline.IsEmpty)
            {
                continue;
            }

            System.Drawing.RectangleF b = outline.Bounds;
            (double ax, double ay, double bx, double by) = p.Matrix.TransformRect(b.Left, b.Top, b.Right, b.Bottom);
            x0 = Math.Min(x0, ax);
            y0 = Math.Min(y0, ay);
            x1 = Math.Max(x1, bx);
            y1 = Math.Max(y1, by);
        }

        return x1 >= x0 && y1 >= y0 && double.IsFinite(x0 + y0 + x1 + y1) ? new Box(x0, y0, x1 - x0, y1 - y0) : null;
    }

    // The box paint servers measure text against: each glyph's advance wide and the font's ascent plus descent tall, not the ink, so a gradient
    // spans the line rather than the letters.
    private static Box? LineBox(List<Placed> placed)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (Placed p in placed)
        {
            if (p.Skip || !p.Span.St.Visible)
            {
                continue;
            }

            FontMetrics fm = p.Font.Metrics;
            double scale = p.Font.Size / (double)fm.UnitsPerEm;
            double asc = Math.Abs(fm.Ascender) * scale;
            double desc = Math.Abs(fm.Descender) * scale;
            (double ax, double ay, double bx, double by) = p.Matrix.TransformRect(0, -asc, p.Cluster.Advance, desc);
            x0 = Math.Min(x0, ax);
            y0 = Math.Min(y0, ay);
            x1 = Math.Max(x1, bx);
            y1 = Math.Max(y1, by);
        }

        return x1 >= x0 && y1 >= y0 && double.IsFinite(x0 + y0 + x1 + y1) ? new Box(x0, y0, x1 - x0, y1 - y0) : null;
    }

    private void EmitText(Stack<Frame> stack, Frame f, SvgElement e)
    {
        if (!Begin(f, e, out SvgStyle st, out Affine ctm) || st.Opacity <= 0)
        {
            return;
        }

        TextModel? m = Collect(e, st, f.Vp);
        if (m is null || m.Length == 0)
        {
            return;
        }

        List<Placed> placed = LayoutText(m, f.Vp);
        if (placed.Count == 0)
        {
            return;
        }

        Box? ink = InkBox(placed);
        Box? lineBox = LineBox(placed) ?? ink;
        BBoxHolder? bbox = lineBox is { } ib ? new BBoxHolder { Known = true, X = ib.X, Y = ib.Y, Width = ib.W, Height = ib.H } : new BBoxHolder { Known = true };
        Path? clip = f.Clip;
        if (!Decorate(e, ctm, f.Vp, f.UseDepth, ref clip, out Deco? deco, ink))
        {
            return;
        }

        var nodes = new List<RNode>();
        BuildTextNodes(f, ctm, m, placed, clip, ref bbox, nodes);
        if (nodes.Count == 0)
        {
            return;
        }

        _hasContent = true;
        bool layered = st.Opacity < 1 || deco is not null;
        if (!layered)
        {
            f.Out.AddRange(nodes);
            return;
        }

        var layer = new RLayer { Children = nodes, Opacity = (float)st.Opacity };
        Union(layer, nodes);
        if (deco is not null)
        {
            FinishLayer(stack, layer, deco, f.Out, f.LayerDepth + 1);
        }
        else
        {
            f.Out.Add(layer);
        }
    }

    private void BuildTextNodes(Frame f, Affine ctm, TextModel m, List<Placed> placed, Path? clip, ref BBoxHolder? bbox, List<RNode> nodes)
    {
        _ = m;
        var below = new List<RNode>();
        var first = new List<RNode>();
        var second = new List<RNode>();
        var above = new List<RNode>();

        foreach (Placed p in placed)
        {
            if (p.Skip || !p.Span.St.Visible)
            {
                continue;
            }

            Path outline = p.Cluster.GetOutline();
            if (outline.IsEmpty)
            {
                continue;
            }

            SpanPaint sp = PaintOfSpan(p.Span, f, ctm, ref bbox);
            if (!sp.HasFill && !sp.HasStroke)
            {
                continue;
            }

            Path geometry = GlyphGeometry(p);
            Affine full = ctm.Multiply(p.Matrix);
            if (!full.IsFinite || Math.Abs(full.A) > Coord.Limit || Math.Abs(full.D) > Coord.Limit || Math.Abs(full.E) > Coord.Limit || Math.Abs(full.F) > Coord.Limit)
            {
                continue;
            }

            SvgStyle st = p.Span.St;
            bool strokeFirst = st.StrokeFirst;
            RPath? fillNode = sp.HasFill ? MakeNode(geometry, full, sp, st, fill: true, clip) : null;
            RPath? strokeNode = sp.HasStroke ? MakeNode(geometry, full, sp, st, fill: false, clip) : null;
            if (strokeFirst)
            {
                AddIf(first, strokeNode);
                AddIf(second, fillNode);
            }
            else
            {
                AddIf(first, fillNode);
                AddIf(second, strokeNode);
            }
        }

        BuildDecorations(f, ctm, placed, clip, ref bbox, below, above);
        nodes.AddRange(below);
        nodes.AddRange(first);
        nodes.AddRange(second);
        nodes.AddRange(above);
    }

    private static void AddIf(List<RNode> list, RNode? node)
    {
        if (node is not null)
        {
            list.Add(node);
        }
    }

    private static RPath? MakeNode(Path geometry, Affine full, SpanPaint sp, in SvgStyle st, bool fill, Path? clip)
    {
        var node = new RPath
        {
            Geometry = geometry,
            Ctm = full,
            HasFill = fill,
            FillColor = sp.FillColor,
            FillPaint = sp.FillServer,
            FillOpacity = (float)st.FillOpacity,
            EvenOdd = st.FillEvenOdd,
            HasStroke = !fill,
            StrokeColor = sp.StrokeColor,
            StrokePaint = sp.StrokeServer,
            StrokeOpacity = (float)st.StrokeOpacity,
            StrokeWidth = sp.Width,
            Cap = sp.Cap,
            Join = sp.Join,
            MiterLimit = sp.Miter,
            Dash = sp.Dash,
            DashOffset = sp.DashOffset,
            Antialias = st.Antialias && !sp.OptimizeSpeed,
            StrokeFirst = false,
            Clip = clip,
        };

        System.Drawing.RectangleF b = geometry.Bounds;
        (double bx0, double by0, double bx1, double by1) = full.TransformRect(b.Left, b.Top, b.Right, b.Bottom);
        double pad = AntialiasPad;
        double half = 0;
        if (!fill)
        {
            double reach = sp.Width / 2 * (sp.Join == StrokeJoin.Miter ? Math.Max(sp.Miter, 1.5) : 1) * (sp.Cap == StrokeCap.Square ? 1.5 : 1);
            pad += reach * full.MaxScale;
            half = sp.Width / 2 * full.MaxScale;
        }

        node.ContentX1 = bx1 + half;
        node.ContentY1 = by1 + half;
        node.X0 = bx0 - pad;
        node.Y0 = by0 - pad;
        node.X1 = bx1 + pad;
        node.Y1 = by1 + pad;
        if (clip is not null)
        {
            System.Drawing.RectangleF cb = clip.Bounds;
            node.X0 = Math.Max(node.X0, cb.Left - 1);
            node.Y0 = Math.Max(node.Y0, cb.Top - 1);
            node.X1 = Math.Min(node.X1, cb.Right + 1);
            node.Y1 = Math.Min(node.Y1, cb.Bottom + 1);
            if (node.X0 > node.X1 || node.Y0 > node.Y1)
            {
                return null;
            }
        }

        return node;
    }

    // Decorations: one path per decorating element and kind, made of a bar under (or over, or through) each cluster, drawn with that element's paint.
    private void BuildDecorations(Frame f, Affine ctm, List<Placed> placed, Path? clip, ref BBoxHolder? bbox, List<RNode> below, List<RNode> above)
    {
        Dictionary<(TSpan, int), List<Bar>>? runs = null;
        foreach (Placed p in placed)
        {
            if (p.Skip)
            {
                continue;
            }

            for (TSpan? a = p.Span; a is not null; a = a.Parent)
            {
                if (!a.Ts.HasDecoration)
                {
                    continue;
                }

                FontMetrics fm = p.Font.Metrics;
                float s = p.Font.Size / (float)fm.UnitsPerEm;
                float width = (float)(p.Advance / p.XScale);
                if (!(width > 0) || !float.IsFinite(width))
                {
                    continue;
                }

                float thick = Math.Max(fm.UnderlineThickness * s, p.Font.Size / 24f);
                runs ??= [];
                if (a.Ts.Underline)
                {
                    float yc = -fm.UnderlinePosition * s;
                    Add(runs, a, 0, new Bar(p.Matrix, 0, yc - (thick / 2), width, thick));
                }

                if (a.Ts.Overline)
                {
                    float top = (-fm.Ascender * s) - (thick / 2);
                    Add(runs, a, 1, new Bar(p.Matrix, 0, top, width, thick));
                }

                if (a.Ts.LineThrough)
                {
                    float sthick = fm.StrikeoutSize > 0 ? fm.StrikeoutSize * s : thick;
                    float yc = -fm.StrikeoutPosition * s;
                    Add(runs, a, 2, new Bar(p.Matrix, 0, yc - (sthick / 2), width, sthick));
                }
            }
        }

        if (runs is null)
        {
            return;
        }

        foreach (((TSpan owner, int kind), List<Bar> bars) in runs)
        {
            SpanPaint sp = PaintOfSpan(owner, f, ctm, ref bbox);
            if (!sp.HasFill && !sp.HasStroke)
            {
                continue;
            }

            var rects = new List<Path>(bars.Count);
            foreach (Bar bar in bars)
            {
                rects.Add(Path.Rectangle(bar.X, bar.Y, bar.W, bar.H).Transform(bar.M.ToMatrix3x2()));
            }

            Path geometry = Path.Combine([.. rects]);
            SvgStyle st = owner.St;
            var node = new RPath
            {
                Geometry = geometry,
                Ctm = ctm,
                HasFill = sp.HasFill,
                FillColor = sp.FillColor,
                FillPaint = sp.FillServer,
                FillOpacity = (float)st.FillOpacity,
                HasStroke = sp.HasStroke,
                StrokeColor = sp.StrokeColor,
                StrokePaint = sp.StrokeServer,
                StrokeOpacity = (float)st.StrokeOpacity,
                StrokeWidth = sp.Width,
                Cap = sp.Cap,
                Join = sp.Join,
                MiterLimit = sp.Miter,
                Dash = sp.Dash,
                DashOffset = sp.DashOffset,
                Antialias = st.Antialias && !sp.OptimizeSpeed,
                StrokeFirst = st.StrokeFirst,
                Clip = clip,
            };

            System.Drawing.RectangleF b = geometry.Bounds;
            (double bx0, double by0, double bx1, double by1) = ctm.TransformRect(b.Left, b.Top, b.Right, b.Bottom);
            double pad = AntialiasPad + (sp.HasStroke ? (sp.Width / 2 * Math.Max(sp.Miter, 1.5) * ctm.MaxScale) : 0);
            node.X0 = bx0 - pad;
            node.Y0 = by0 - pad;
            node.X1 = bx1 + pad;
            node.Y1 = by1 + pad;
            node.ContentX1 = bx1;
            node.ContentY1 = by1;
            (kind == 2 ? above : below).Add(node);
        }
    }

    /// <summary>A decoration bar in the space of one cluster; <see cref="M"/> maps that space to the text element's.</summary>
    private readonly record struct Bar(Affine M, float X, float Y, float W, float H);

    // Bars of neighbouring clusters that meet on one line are joined into one, so a decoration is a single rectangle (no seams, no strokes between glyphs).
    private static void Add(Dictionary<(TSpan, int), List<Bar>> runs, TSpan owner, int kind, Bar bar)
    {
        if (!runs.TryGetValue((owner, kind), out List<Bar>? list))
        {
            runs[(owner, kind)] = list = [];
        }

        if (list.Count > 0)
        {
            Bar last = list[^1];
            (double ex, double ey) = last.M.Apply(last.X + last.W, last.Y);
            (double sx, double sy) = bar.M.Apply(bar.X, bar.Y);
            bool sameFrame = Math.Abs(last.M.A - bar.M.A) < 1e-9 && Math.Abs(last.M.B - bar.M.B) < 1e-9 && Math.Abs(last.M.C - bar.M.C) < 1e-9 && Math.Abs(last.M.D - bar.M.D) < 1e-9;
            if (sameFrame && Math.Abs(ex - sx) < 1e-6 && Math.Abs(ey - sy) < 1e-6 && last.Y == bar.Y && last.H == bar.H)
            {
                list[^1] = last with { W = last.W + bar.W };
                return;
            }
        }

        list.Add(bar);
    }

    // Text inside a clipPath: the outlines of its glyphs, united. The text's own clip-path and its decorations count; paint does not.
    private Path? TextClipPiece(SvgElement child, in SvgStyle parentStyle, Affine m, Viewport vp, int depth)
    {
        if (!BeginIn(parentStyle, m, vp, child, out SvgStyle st, out Affine cm, false))
        {
            return null;
        }

        TextModel? model = Collect(child, st, vp);
        if (model is null || model.Length == 0)
        {
            return null;
        }

        List<Placed> placed = LayoutText(model, vp);
        var pieces = new List<Path>();
        foreach (Placed p in placed)
        {
            if (p.Skip || !p.Span.St.Visible)
            {
                continue;
            }

            Path outline = GlyphGeometry(p);
            if (!outline.IsEmpty)
            {
                pieces.Add(outline.Transform(cm.Multiply(p.Matrix).ToMatrix3x2()));
            }
        }

        if (pieces.Count == 0)
        {
            return null;
        }

        Path piece = Path.Combine([.. pieces]).Resolve(IntersectionRule.NonZero, ClipTolerance);
        if (ClipOf(child, cm, vp, InkBox(placed), depth + 1) is { } own)
        {
            piece = own.IsEmpty || piece.IsEmpty ? Path.Empty : piece.Intersect(own);
        }

        return piece;
    }
}
