using System.Globalization;
using OneImage.Drawing;
using OneImage.Svg.Dom;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

// Paint servers: gradients and patterns, href inheritance, fallbacks and context paints.
internal sealed partial class SceneBuilder
{
    private sealed class GradientDef
    {
        public required bool Radial { get; init; }

        public required bool BBoxUnits { get; init; }

        public required Affine Transform { get; init; }

        public required GradientRepetition Spread { get; init; }

        public required ColorStop[] Stops { get; init; }

        // Raw attribute text (null = not given anywhere in the href chain).
        public string? X1 { get; init; }

        public string? Y1 { get; init; }

        public string? X2 { get; init; }

        public string? Y2 { get; init; }

        public string? Cx { get; init; }

        public string? Cy { get; init; }

        public string? R { get; init; }

        public string? Fx { get; init; }

        public string? Fy { get; init; }

        public string? Fr { get; init; }
    }

    private readonly Dictionary<SvgElement, SvgStyle> _styleCache = [];
    private readonly Dictionary<SvgElement, GradientDef> _gradients = [];
    private readonly Dictionary<SvgElement, RNode[]> _patternContent = [];
    private readonly HashSet<SvgElement> _activeServers = [];
    private const int MaxStops = 1 << 16;
    private int _serverDepth;
    private Viewport _rootVp = new(100, 100);

    /// <summary>The computed style of any element, from the root down (what its children would inherit).</summary>
    private SvgStyle StyleOf(SvgElement e)
    {
        if (_styleCache.TryGetValue(e, out SvgStyle cached))
        {
            return cached;
        }

        var chain = new Stack<SvgElement>();
        for (SvgElement? p = e; p is not null && !_styleCache.ContainsKey(p); p = p.Parent)
        {
            chain.Push(p);
        }

        SvgStyle st = SvgStyle.Initial;
        if (chain.Count > 0 && chain.Peek().Parent is { } above)
        {
            st = _styleCache[above];
        }

        while (chain.Count > 0)
        {
            SvgElement n = chain.Pop();
            st = SvgStyle.Compute(n, st, _rootFontSize);
            _styleCache[n] = st;
        }

        return st;
    }

    private SvgElement? HrefTarget(SvgElement e) =>
        e.Attr("href") is { Length: > 1 } href && href[0] == '#' && _doc.Ids.TryGetValue(href[1..], out SvgElement? t) ? t : null;

    private static bool IsGradient(SvgElement e) => e.Name is "linearGradient" or "radialGradient";

    /// <summary>
    /// Turns a <c>fill</c> or <c>stroke</c> value into something drawable. A reference to a paint server that exists is resolved
    /// (an unusable one paints nothing); a reference that is dangling or points at another kind of element uses its fallback.
    /// </summary>
    /// <param name="p">The value.</param>
    /// <param name="f">The frame the element is in (its viewport and context paints).</param>
    /// <param name="st">The element's style.</param>
    /// <param name="ctm">The element's coordinate system in scene space; gradients and patterns live in it.</param>
    /// <param name="geometry">The element's geometry, for its bounding box; <see langword="null"/> when <paramref name="bbox"/> is given.</param>
    /// <param name="bbox">The bounding box holder, made from <paramref name="geometry"/> on first need.</param>
    private ResolvedPaint ResolvePaint(Paint p, Frame f, in SvgStyle st, Affine ctm, Path? geometry, ref BBoxHolder? bbox)
    {
        switch (p.Kind)
        {
            case PaintKind.Color:
                return ResolvedPaint.Solid(p.Color);
            case PaintKind.CurrentColor:
                return ResolvedPaint.Solid(st.Color);
            case PaintKind.ContextFill:
                return f.CtxFill ?? ResolvedPaint.None;
            case PaintKind.ContextStroke:
                return f.CtxStroke ?? ResolvedPaint.None;
            case PaintKind.Server:
                if (p.ServerId is not null && _doc.Ids.TryGetValue(p.ServerId, out SvgElement? server) && (IsGradient(server) || server.Name == "pattern")
                    && _activeServers.Add(server))
                {
                    try
                    {
                        if (bbox is null && geometry is not null)
                        {
                            bbox = BBoxHolder.Of(geometry);
                        }

                        // A server that cannot paint (no stops, an empty box, no content, a loop through its own content) is handled like a dangling reference.
                        if (bbox is not null && (IsGradient(server) ? BuildGradient(server, st, f.Vp, ctm, bbox) : BuildPattern(server, st, f.Vp, ctm, bbox)) is { } built)
                        {
                            return built;
                        }
                    }
                    finally
                    {
                        _activeServers.Remove(server);
                    }
                }

                return p.Fallback switch
                {
                    PaintKind.Color => ResolvedPaint.Solid(p.FallbackColor),
                    PaintKind.CurrentColor => ResolvedPaint.Solid(st.Color),
                    _ => ResolvedPaint.None,
                };
            default:
                return ResolvedPaint.None;
        }
    }

    // ---- gradients ----

    private GradientDef GradientOf(SvgElement e)
    {
        if (_gradients.TryGetValue(e, out GradientDef? cached))
        {
            return cached;
        }

        // The element, then what it refers to, and so on: a loop or a non-gradient ends the chain.
        var chain = new List<SvgElement>();
        var seen = new HashSet<SvgElement>();
        for (SvgElement? cur = e; cur is not null && IsGradient(cur) && seen.Add(cur); cur = HrefTarget(cur))
        {
            chain.Add(cur);
        }

        string? Attr(string name, bool sameKindOnly)
        {
            foreach (SvgElement c in chain)
            {
                if ((!sameKindOnly || c.Name == e.Name) && c.Attr(name) is { } v)
                {
                    return v;
                }
            }

            return null;
        }

        SvgElement? stopsFrom = chain.Find(c => c.Children.Exists(k => k.Name == "stop"));
        var stops = new List<ColorStop>();
        if (stopsFrom is not null)
        {
            float last = 0;
            foreach (SvgElement s in stopsFrom.Children)
            {
                if (s.Name != "stop")
                {
                    continue;
                }

                // The brush takes at most 65,536 stops; a gradient has no use for more than the pixels it spans.
                if (stops.Count >= MaxStops)
                {
                    break;
                }

                Count();
                float offset = ParseOffset(s.Attr("offset"));
                offset = Math.Max(offset, last);
                last = offset;
                stops.Add(new ColorStop(offset, StopColor(s)));
            }
        }

        Affine transform = Affine.Identity;
        if (Attr("gradientTransform", false) is { } ts && SvgValues.TryTransform(ts, out Affine t) && t.IsInvertible)
        {
            transform = t;
        }

        string? spread = Attr("spreadMethod", false);
        var def = new GradientDef
        {
            Radial = e.Name == "radialGradient",
            BBoxUnits = Attr("gradientUnits", false)?.Trim() != "userSpaceOnUse",
            Transform = transform,
            Spread = spread?.Trim() switch { "reflect" => GradientRepetition.Reflect, "repeat" => GradientRepetition.Repeat, _ => GradientRepetition.None },
            Stops = [.. stops],
            X1 = Attr("x1", true),
            Y1 = Attr("y1", true),
            X2 = Attr("x2", true),
            Y2 = Attr("y2", true),
            Cx = Attr("cx", true),
            Cy = Attr("cy", true),
            R = Attr("r", true),
            Fx = Attr("fx", true),
            Fy = Attr("fy", true),
            Fr = Attr("fr", true),
        };
        _gradients[e] = def;
        return def;
    }

    private static float ParseOffset(string? text)
    {
        if (text is null)
        {
            return 0;
        }

        ReadOnlySpan<char> t = text.AsSpan().Trim();
        bool percent = t.EndsWith("%");
        if (percent)
        {
            t = t[..^1];
        }

        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            return 0;
        }

        return (float)Math.Clamp(percent ? v / 100 : v, 0, 1);
    }

    // stop-color is not inherited, but "inherit" takes the parent's value; currentColor is the stop's own color property. stop-opacity multiplies in.
    private Rgba32 StopColor(SvgElement stop)
    {
        Rgba32 color = new(0, 0, 0, 255);
        string? value = stop.Prop("stop-color");
        for (SvgElement? n = stop.Parent; value == "inherit" && n is not null; n = n.Parent)
        {
            value = n.Prop("stop-color");
        }

        if (value is not null && value != "inherit")
        {
            if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
            {
                color = StyleOf(stop).Color;
            }
            else if (CssColor.TryParse(value, out Rgba32 c))
            {
                color = c;
            }
        }

        string? o = stop.Prop("stop-opacity");
        for (SvgElement? n = stop.Parent; o == "inherit" && n is not null; n = n.Parent)
        {
            o = n.Prop("stop-opacity");
        }

        if (o is not null && o != "inherit")
        {
            ReadOnlySpan<char> t = o.AsSpan().Trim();
            bool percent = t.EndsWith("%");
            if (percent)
            {
                t = t[..^1];
            }

            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
            {
                double a = Math.Clamp(percent ? v / 100 : v, 0, 1);
                color = new Rgba32(color.R, color.G, color.B, (byte)Math.Round(color.A * a));
            }
        }

        return color;
    }

    private double GradientLength(string? text, double fallbackPercent, Axis axis, bool bboxUnits, in SvgStyle st, Viewport vp)
    {
        Length l = text is not null && Length.TryParse(text, out Length parsed) ? parsed : new Length(fallbackPercent, LengthUnit.Percent);
        return bboxUnits ? (l.Unit == LengthUnit.Percent ? l.Value / 100 : Resolve(l, axis, st, vp)) : Resolve(l, axis, st, vp);
    }

    private ResolvedPaint? BuildGradient(SvgElement el, in SvgStyle st, Viewport vp, Affine ctm, BBoxHolder bbox)
    {
        GradientDef g = GradientOf(el);
        if (g.Stops.Length == 0)
        {
            return null;
        }

        Rgba32 lastColor = g.Stops[^1].Color;
        if (g.Stops.Length == 1)
        {
            return ResolvedPaint.Solid(lastColor);
        }

        if (g.BBoxUnits)
        {
            bbox.Needed = true;
            if (bbox.Known && !bbox.IsUsable)
            {
                return null;
            }
        }

        bool u = g.BBoxUnits;
        BBoxHolder? holder = u ? bbox : null;
        if (!g.Radial)
        {
            double x1 = GradientLength(g.X1, 0, Axis.X, u, st, vp), y1 = GradientLength(g.Y1, 0, Axis.Y, u, st, vp);
            double x2 = GradientLength(g.X2, 100, Axis.X, u, st, vp), y2 = GradientLength(g.Y2, 0, Axis.Y, u, st, vp);
            if (!AllFinite(x1, y1, x2, y2))
            {
                return null;
            }

            // Both ends at one point: the area is painted with the last stop.
            float fdx = Coord.F(x2) - Coord.F(x1), fdy = Coord.F(y2) - Coord.F(y1);
            if (!(((double)fdx * fdx) + ((double)fdy * fdy) >= 1e-12))
            {
                return ResolvedPaint.Solid(lastColor);
            }

            return ResolvedPaint.FromServer(new RGradientPaint { Radial = false, Stops = g.Stops, Spread = g.Spread, BaseCtm = ctm, GradientTransform = g.Transform, BBox = holder, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
        }

        double cx = GradientLength(g.Cx, 50, Axis.X, u, st, vp), cy = GradientLength(g.Cy, 50, Axis.Y, u, st, vp);
        double r = GradientLength(g.R, 50, Axis.Other, u, st, vp);
        double fx = g.Fx is null ? cx : GradientLength(g.Fx, 50, Axis.X, u, st, vp);
        double fy = g.Fy is null ? cy : GradientLength(g.Fy, 50, Axis.Y, u, st, vp);
        double fr = g.Fr is null ? 0 : GradientLength(g.Fr, 0, Axis.Other, u, st, vp);
        if (!AllFinite(cx, cy, r, fx, fy, fr))
        {
            return null;
        }

        // No radius (or a negative one) leaves nothing to grade: the last stop fills the area.
        if (!(r > 0))
        {
            return ResolvedPaint.Solid(lastColor);
        }

        return ResolvedPaint.FromServer(new RGradientPaint { Radial = true, Stops = g.Stops, Spread = g.Spread, BaseCtm = ctm, GradientTransform = g.Transform, BBox = holder, X1 = cx, Y1 = cy, R = r, Fx = fx, Fy = fy, Fr = Math.Max(fr, 0) });
    }

    private static bool AllFinite(params ReadOnlySpan<double> v)
    {
        foreach (double d in v)
        {
            if (!double.IsFinite(d) || Math.Abs(d) > Coord.Limit)
            {
                return false;
            }
        }

        return true;
    }

    // ---- patterns ----

    private ResolvedPaint? BuildPattern(SvgElement el, in SvgStyle st, Viewport vp, Affine ctm, BBoxHolder bbox)
    {
        var chain = new List<SvgElement>();
        var seen = new HashSet<SvgElement>();
        for (SvgElement? cur = el; cur is not null && cur.Name == "pattern" && seen.Add(cur); cur = HrefTarget(cur))
        {
            chain.Add(cur);
        }

        string? Attr(string name)
        {
            foreach (SvgElement c in chain)
            {
                if (c.Attr(name) is { } v)
                {
                    return v;
                }
            }

            return null;
        }

        SvgElement? contentEl = chain.Find(c => c.Children.Count > 0);
        if (contentEl is null)
        {
            return null;
        }

        bool tileBBox = Attr("patternUnits")?.Trim() != "userSpaceOnUse";
        bool contentBBox = Attr("patternContentUnits")?.Trim() == "objectBoundingBox";
        bool hasViewBox = SvgValues.TryViewBox(Attr("viewBox"), out double vx, out double vy, out double vw, out double vh);
        bool needsBox = tileBBox || (contentBBox && !hasViewBox);
        if (needsBox)
        {
            bbox.Needed = true;
            if (bbox.Known && !bbox.IsUsable)
            {
                return null;
            }
        }

        double x = GradientLength(Attr("x"), 0, Axis.X, tileBBox, st, vp);
        double y = GradientLength(Attr("y"), 0, Axis.Y, tileBBox, st, vp);
        double w = GradientLength(Attr("width"), 0, Axis.X, tileBBox, st, vp);
        double h = GradientLength(Attr("height"), 0, Axis.Y, tileBBox, st, vp);
        if (!AllFinite(x, y, w, h) || !(w > 0) || !(h > 0))
        {
            return null;
        }

        Affine patternTransform = Affine.Identity;
        if (Attr("patternTransform") is { } ts && SvgValues.TryTransform(ts, out Affine t) && t.IsInvertible)
        {
            patternTransform = t;
        }

        if (hasViewBox && (!(vw > 0) || !(vh > 0) || !AllFinite(vx, vy, vw, vh)))
        {
            return null;
        }

        RNode[] nodes = PatternContent(contentEl);
        if (nodes.Length == 0)
        {
            return null;
        }

        return ResolvedPaint.FromServer(new RPatternPaint
        {
            Content = nodes,
            BaseCtm = ctm,
            PatternTransform = patternTransform,
            X = x,
            Y = y,
            Width = w,
            Height = h,
            TileInBox = tileBBox,
            ContentInBox = contentBBox,
            HasViewBox = hasViewBox,
            ViewX = vx,
            ViewY = vy,
            ViewWidth = vw,
            ViewHeight = vh,
            Aspect = AspectRatio.Parse(Attr("preserveAspectRatio")),
            BBox = needsBox ? bbox : null,
            Limits = _limits,
        });
    }

    // The pattern's content as render nodes in the content's own coordinates; built once per element when it is not nested in another pattern's build.
    private RNode[] PatternContent(SvgElement contentEl)
    {
        if (_patternContent.TryGetValue(contentEl, out RNode[]? cached))
        {
            return cached;
        }

        if (_serverDepth + 1 > _limits.MaxReferenceDepth)
        {
            throw new ImageLimitException($"Patterns are nested more than {_limits.MaxReferenceDepth} deep.");
        }

        bool added = _activeServers.Add(contentEl);
        _serverDepth++;
        try
        {
            var top = new List<RNode>();
            var stack = new Stack<Frame>();
            stack.Push(new Frame { Kids = contentEl.Children, Style = StyleOf(contentEl), Ctm = Affine.Identity, Vp = _rootVp, Out = top, LayerParent = top });
            Run(stack);
            RNode[] nodes = [.. top];
            if (_serverDepth == 1)
            {
                _patternContent[contentEl] = nodes;
            }

            return nodes;
        }
        finally
        {
            _serverDepth--;
            if (added)
            {
                _activeServers.Remove(contentEl);
            }
        }
    }

    // ---- context paints: a use element's fill and stroke, available to its content as context-fill and context-stroke ----

    /// <summary>Resolves the paints a <c>use</c> element offers to its content.</summary>
    private (ResolvedPaint Fill, ResolvedPaint Stroke, BBoxHolder Box) ContextPaints(Frame f, in SvgStyle st, Affine ctm)
    {
        BBoxHolder? box = new();
        ResolvedPaint fill = ResolvePaint(st.Fill, f, st, ctm, null, ref box);
        ResolvedPaint stroke = ResolvePaint(st.Stroke, f, st, ctm, null, ref box);
        return (fill, stroke, box!);
    }

    // The box of what a use element drew, in the use element's coordinates, for paints that depend on it.
    private static void Measure(Frame f, BBoxHolder box)
    {
        Affine inv = f.Ctm.IsInvertible ? RPatternPaint.Invert(f.Ctm) : Affine.Identity;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        var work = new Stack<RNode>();
        for (int i = f.Out.Count - 1; i >= f.OutStart; i--)
        {
            work.Push(f.Out[i]);
        }

        while (work.Count > 0)
        {
            switch (work.Pop())
            {
                case RPath p when !p.Geometry.IsEmpty:
                    System.Drawing.RectangleF b = p.Geometry.Transform(inv.Multiply(p.Ctm).ToMatrix3x2()).Bounds;
                    x0 = Math.Min(x0, b.Left);
                    y0 = Math.Min(y0, b.Top);
                    x1 = Math.Max(x1, b.Right);
                    y1 = Math.Max(y1, b.Bottom);
                    break;
                case RLayer l:
                    foreach (RNode c in l.Children)
                    {
                        work.Push(c);
                    }

                    break;
            }
        }

        if (x1 >= x0 && y1 >= y0)
        {
            box.X = x0;
            box.Y = y0;
            box.Width = x1 - x0;
            box.Height = y1 - y0;
        }

        box.Known = true;
    }
}
