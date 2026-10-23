using System.Numerics;
using OneImage.Drawing;
using OneImage.Svg.Dom;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

/// <summary>
/// Turns a document into a render tree: resolves styles, units, transforms, <c>viewBox</c>es, <c>use</c>/<c>symbol</c> instances and
/// shapes into paths with final geometry and paint, in scene space. It walks the document with an explicit stack (document nesting
/// and <c>use</c> chains never grow the call stack) and counts every instantiated element against <see cref="SvgLimits.MaxInstances"/>.
/// </summary>
internal sealed partial class SceneBuilder
{
    private enum Axis
    {
        X,
        Y,
        Other,
    }

    private readonly record struct Viewport(double Width, double Height, double X = 0, double Y = 0);

    private sealed class Frame
    {
        public required IReadOnlyList<SvgElement> Kids { get; init; }

        public int Next { get; set; }

        public SvgStyle Style;

        public Affine Ctm { get; init; }

        public Viewport Vp { get; init; }

        public Path? Clip { get; init; }

        public required List<RNode> Out { get; init; }

        /// <summary>The layer these kids paint into (closed on pop), and the list the layer joins.</summary>
        public RLayer? Layer { get; init; }

        public List<RNode>? LayerParent { get; init; }

        public int LayerDepth { get; init; }

        /// <summary>Clip and mask still to apply to the layer when this frame closes (see <c>FinishLayer</c>).</summary>
        public Deco? Deco { get; init; }

        /// <summary>Runs when the frame has closed (mask content frames use it to continue the chain).</summary>
        public Action<Stack<Frame>>? After { get; set; }

        public int UseDepth { get; init; }

        /// <summary>For a <c>use</c> frame: the target to forget when the frame closes, and the size the use element gave.</summary>
        public SvgElement? UseTarget { get; init; }

        public Length? UseWidth { get; init; }

        public Length? UseHeight { get; init; }

        /// <summary>The fill and stroke a <c>use</c> element offers its content as <c>context-fill</c> and <c>context-stroke</c> (inherited by nested frames; markers will set theirs the same way).</summary>
        public ResolvedPaint? CtxFill { get; init; }

        public ResolvedPaint? CtxStroke { get; init; }

        /// <summary>For a <c>use</c> frame whose context paints depend on its box: filled in when the frame closes.</summary>
        public BBoxHolder? UseBox { get; init; }

        /// <summary>Index in <see cref="Out"/> where this frame's own nodes start.</summary>
        public int OutStart { get; init; }

        /// <summary>Set on the frame of a shape that has markers and on the frames of the marker instances drawn for it.</summary>
        public MarkerFrame? Marker { get; init; }
    }

    // The marker state of a frame, kept apart so frames of everything else stay small (a use fan-out makes many of them).
    private sealed class MarkerFrame
    {
        /// <summary>For the frame of a shape that has markers: the parts of the shape (fill, stroke, markers) in <c>paint-order</c>, appended to the frame's output when it closes.</summary>
        public List<RNode>[]? Parts { get; init; }

        /// <summary>For that frame: the markers still to be instantiated, one at a time so a marker that contains itself is seen as active.</summary>
        public MarkerPlan? Pending { get; init; }

        /// <summary>For a marker instance frame: the marker element to forget when the frame closes.</summary>
        public SvgElement? Target { get; init; }

        /// <summary>For a marker instance frame: the <c>use</c> chain of the shape the marker belongs to, put back when the frame closes (a marker's content starts a chain of its own, since markers are guarded separately).</summary>
        public HashSet<SvgElement>? SavedUseChain { get; init; }
    }

    private const double AntialiasPad = 0.5;

    /// <summary>What a document and the documents nested in it share: the instance count and the bytes of embedded images.</summary>
    private sealed class Budget
    {
        public int Instances;
        public long ImageBytes;
    }

    private static readonly string[] DefaultLanguages = ["en"];
    private readonly HashSet<SvgElement> _activeMarkers = [];
    private readonly Dictionary<SvgElement, List<MarkerVertex>> _markerVertices = [];

    private readonly SvgDocument _doc;
    private readonly SvgLimits _limits;
    private readonly CancellationToken _cancel;
    private readonly ImageLimits _imageLimits;
    private readonly IReadOnlyList<string> _languages;
    private readonly int _imageDepth;
    private readonly Budget _budget;
    private readonly Dictionary<SvgElement, Picture?> _pictures = [];
    private HashSet<SvgElement> _active = [];
    private readonly Dictionary<SvgElement, (Path? Path, Path? Dotted)> _pathCache = [];
    private double _rootFontSize = SvgStyle.DefaultFontSize;
    private bool _hasContent;

    private SceneBuilder(SvgDocument doc, SvgLimits limits, ImageLimits imageLimits, IReadOnlyList<string> languages, int imageDepth, Budget budget, CancellationToken cancel)
    {
        _doc = doc;
        _limits = limits;
        _cancel = cancel;
        _imageLimits = imageLimits;
        _languages = languages;
        _imageDepth = imageDepth;
        _budget = budget;
    }

    /// <summary>Builds the scene of a document.</summary>
    /// <exception cref="ImageFormatException">The root size is malformed.</exception>
    /// <exception cref="ImageLimitException">An instantiation, depth or size limit is exceeded.</exception>
    /// <param name="doc">The styled document.</param>
    /// <param name="limits">The document limits.</param>
    /// <param name="cancel">Cancellation.</param>
    /// <param name="imageLimits">The limits that bound the decoding of embedded raster images; the defaults when <see langword="null"/>.</param>
    /// <param name="languages">The languages <c>systemLanguage</c> is matched against; <c>en</c> when <see langword="null"/>.</param>
    public static SvgScene Build(SvgDocument doc, SvgLimits limits, CancellationToken cancel, ImageLimits? imageLimits = null, IReadOnlyList<string>? languages = null) =>
        new SceneBuilder(doc, limits, imageLimits ?? ImageLimits.Default, languages ?? DefaultLanguages, 0, new Budget(), cancel).BuildRoot();

    private SvgScene BuildRoot()
    {
        SvgElement root = _doc.Root;
        SvgStyle st = SvgStyle.Compute(root, SvgStyle.Initial, SvgStyle.DefaultFontSize);
        _rootFontSize = st.FontSize;
        st = SvgStyle.Compute(root, SvgStyle.Initial, _rootFontSize);
        (double? w, double? h) = SvgSize.Partial(root.Attr("width"), root.Attr("height"), root.Attr("viewBox"));
        bool hasViewBox = SvgValues.TryViewBox(root.Attr("viewBox"), out double vx, out double vy, out double vw, out double vh);

        Affine ctm = Affine.Identity;
        Viewport vp;
        if (hasViewBox)
        {
            vp = new Viewport(vw, vh, vx, vy);
            if (w is { } ww && h is { } hh)
            {
                ctm = AspectRatio.Parse(root.Attr("preserveAspectRatio")).Fit(vx, vy, vw, vh, ww, hh);
            }
        }
        else
        {
            vp = new Viewport(w ?? 100, h ?? 100);
        }

        _rootVp = vp;

        if (!st.Displayed || !Conditions(root))
        {
            return new SvgScene(w ?? 1, h ?? 1, [], false);
        }

        if ((root.Prop("transform") ?? root.Attr("transform")) is { } ts && SvgValues.TryTransform(ts, out Affine t))
        {
            if (!t.IsInvertible)
            {
                return new SvgScene(w ?? 1, h ?? 1, [], false);
            }

            // CSS gives the root element's transform an origin at the center of its box, unless transform-origin says otherwise;
            // percentages in it refer to the viewport.
            double ox = (w ?? vp.Width) / 2;
            double oy = (h ?? vp.Height) / 2;
            if (root.Prop("transform-origin") is { } rootOrigin)
            {
                _ = TryOrigin(rootOrigin, 0, 0, w ?? vp.Width, h ?? vp.Height, st, out ox, out oy);
            }

            ctm = Affine.Translate(ox, oy).Multiply(t).Multiply(Affine.Translate(-ox, -oy)).Multiply(ctm);
        }

        Path? rootClip = null;
        if (!Decorate(root, ctm, vp, 0, ref rootClip, out Deco? rootDeco))
        {
            return new SvgScene(w ?? 1, h ?? 1, [], false);
        }

        var top = new List<RNode>();
        List<RNode> output = top;
        RLayer? layer = null;
        if (st.NeedsLayer || rootDeco is not null)
        {
            layer = new RLayer { Children = [], Opacity = (float)st.Opacity, Blend = st.Blend, Plus = st.Plus };
            output = layer.Children;
        }

        var stack = new Stack<Frame>();
        stack.Push(new Frame { Kids = root.Children, Style = st, Ctm = ctm, Vp = vp, Clip = rootClip, Out = output, Layer = layer, LayerParent = top, LayerDepth = layer is null ? 0 : 1, Deco = rootDeco });
        Run(stack);

        RNode[] nodes = [.. top];
        double x1 = 0, y1 = 0;
        foreach (RNode n in nodes)
        {
            x1 = Math.Max(x1, n.ContentX1);
            y1 = Math.Max(y1, n.ContentY1);
        }

        double width = w ?? Math.Max(x1, 1e-3);
        double height = h ?? Math.Max(y1, 1e-3);
        if (root.Prop("background-color") is { } bg && CssColor.TryParse(bg, out Rgba32 bgColor) && bgColor.A > 0)
        {
            // The root's background paints the whole output, under everything.
            var fillRect = new RPath { Geometry = Path.Rectangle(0, 0, (float)width, (float)height), Ctm = Affine.Identity, HasFill = true, FillColor = bgColor, X0 = 0, Y0 = 0, X1 = width, Y1 = height, ContentX1 = width, ContentY1 = height };
            nodes = [fillRect, .. nodes];
        }

        return new SvgScene(width, height, nodes, _hasContent);
    }

    private void Run(Stack<Frame> stack)
    {
        while (stack.Count > 0)
        {
            Frame f = stack.Peek();
            if (f.Next >= f.Kids.Count)
            {
                if (f.Marker?.Pending is not null && PushNextMarker(stack, f))
                {
                    continue;
                }

                stack.Pop();
                Close(stack, f);
                continue;
            }

            SvgElement child = f.Kids[f.Next++];
            if ((_budget.Instances & 0x3FF) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            Process(stack, f, child);
        }
    }

    private void Close(Stack<Frame> stack, Frame f)
    {
        if (f.UseBox is { Needed: true } box)
        {
            Measure(f, box);
        }

        if (f.UseTarget is not null)
        {
            _active.Remove(f.UseTarget);
        }

        if (f.Marker is { } marker)
        {
            if (marker.Target is not null)
            {
                _activeMarkers.Remove(marker.Target);
            }

            if (marker.SavedUseChain is not null)
            {
                _active = marker.SavedUseChain;
            }

            if (marker.Parts is not null)
            {
                foreach (List<RNode> part in marker.Parts)
                {
                    f.Out.AddRange(part);
                }
            }
        }

        if (f.Layer is { } layer && (layer.Children.Count > 0 || f.Deco?.Filter is not null))
        {
            Union(layer, layer.Children);
            if (f.Deco is { } deco)
            {
                FinishLayer(stack, layer, deco, f.LayerParent!, f.LayerDepth);
            }
            else
            {
                f.LayerParent!.Add(layer);
            }
        }

        f.After?.Invoke(stack);
    }

    private static void Union(RNode into, List<RNode> children)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        double cx = 0, cy = 0;
        foreach (RNode c in children)
        {
            cx = Math.Max(cx, c.ContentX1);
            cy = Math.Max(cy, c.ContentY1);
            x0 = Math.Min(x0, c.X0);
            y0 = Math.Min(y0, c.Y0);
            x1 = Math.Max(x1, c.X1);
            y1 = Math.Max(y1, c.Y1);
        }

        into.X0 = x0;
        into.Y0 = y0;
        into.X1 = x1;
        into.Y1 = y1;
        into.ContentX1 = cx;
        into.ContentY1 = cy;
    }

    private void Count()
    {
        if (++_budget.Instances > _limits.MaxInstances)
        {
            throw new ImageLimitException($"The SVG instantiates more than {_limits.MaxInstances} elements (check use references that multiply).");
        }
    }

    private void Process(Stack<Frame> stack, Frame f, SvgElement e)
    {
        if (e.Attrs.Count > 0 && !Conditions(e))
        {
            return;
        }

        switch (e.Name)
        {
            case "switch":
                Count();
                PushSwitch(stack, f, e);
                break;
            case "image":
                Count();
                EmitImage(f, e);
                break;
            case "g" or "a":
                Count();
                PushGroup(stack, f, e);
                break;
            case "svg":
                Count();
                PushViewport(stack, f, e, symbol: false, ReferenceEquals(f.UseTarget, e) ? f.UseWidth : null, ReferenceEquals(f.UseTarget, e) ? f.UseHeight : null);
                break;
            case "symbol" when ReferenceEquals(f.UseTarget, e):
                Count();
                PushViewport(stack, f, e, symbol: true, f.UseWidth, f.UseHeight);
                break;
            case "use":
                Count();
                PushUse(stack, f, e);
                break;
            case "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path":
                Count();
                EmitShape(stack, f, e);
                break;
        }
    }

    // Style, display and transform of an element about to open or draw: false when it renders nothing.
    private bool Begin(Frame f, SvgElement e, out SvgStyle st, out Affine ctm, bool ignoreTransform = false) =>
        BeginIn(f.Style, f.Ctm, f.Vp, e, out st, out ctm, ignoreTransform);

    private bool BeginIn(in SvgStyle parent, Affine parentCtm, Viewport vp, SvgElement e, out SvgStyle st, out Affine ctm, bool ignoreTransform = false)
    {
        st = SvgStyle.Compute(e, parent, _rootFontSize);
        ctm = parentCtm;
        if (!st.Displayed)
        {
            return false;
        }

        // The transform property (a style declaration) wins over the attribute; CSS spells lengths and angles with units.
        string? ts = ignoreTransform ? null : e.Prop("transform")?.Replace("px", string.Empty, StringComparison.Ordinal).Replace("deg", string.Empty, StringComparison.Ordinal) ?? e.Attr("transform");
        if (ts is not null && SvgValues.TryTransform(ts, out Affine t))
        {
            if (!t.IsInvertible)
            {
                return false;
            }

            // transform-origin moves the point the transform acts around; its reference box is the nearest viewBox.
            if (e.Prop("transform-origin") is { } origin)
            {
                t = AroundOrigin(t, origin, vp.X, vp.Y, vp.Width, vp.Height, st);
            }

            ctm = ctm.Multiply(t);
        }

        // Numbers beyond what the drawing code takes (or that overflowed while composing) draw nothing.
        return ctm.IsFinite && Math.Abs(ctm.A) <= Coord.Limit && Math.Abs(ctm.B) <= Coord.Limit && Math.Abs(ctm.C) <= Coord.Limit
            && Math.Abs(ctm.D) <= Coord.Limit && Math.Abs(ctm.E) <= Coord.Limit && Math.Abs(ctm.F) <= Coord.Limit;
    }

    private static void Open(Stack<Frame> stack, Frame parent, IReadOnlyList<SvgElement> kids, in SvgStyle st, Affine ctm, Viewport vp, Path? clip, bool layered, SvgElement? useTarget = null, Length? useW = null, Length? useH = null, int useDepth = -1, int maxLayers = 0, ResolvedPaint? ctxFill = null, ResolvedPaint? ctxStroke = null, BBoxHolder? useBox = null, Deco? deco = null)
    {
        List<RNode> output = parent.Out;
        RLayer? layer = null;
        int depth = parent.LayerDepth;
        if (layered)
        {
            if (depth + 1 > maxLayers)
            {
                throw new ImageLimitException($"Groups with opacity are nested more than {maxLayers} deep.");
            }

            layer = new RLayer { Children = [], Opacity = (float)st.Opacity, Blend = st.Blend, Plus = st.Plus };
            output = layer.Children;
            depth++;
        }

        stack.Push(new Frame
        {
            Kids = kids,
            Style = st,
            Ctm = ctm,
            Vp = vp,
            Clip = clip,
            Out = output,
            Layer = layer,
            LayerParent = parent.Out,
            LayerDepth = depth,
            Deco = deco,
            UseDepth = useDepth >= 0 ? useDepth : parent.UseDepth,
            UseTarget = useTarget,
            UseWidth = useW,
            UseHeight = useH,
            CtxFill = ctxFill ?? parent.CtxFill,
            CtxStroke = ctxStroke ?? parent.CtxStroke,
            UseBox = useBox,
            OutStart = layer is null ? parent.Out.Count : 0,
        });
    }

    private void PushGroup(Stack<Frame> stack, Frame f, SvgElement e)
    {
        if (!Begin(f, e, out SvgStyle st, out Affine ctm) || st.Opacity <= 0)
        {
            return;
        }

        Path? clip = f.Clip;
        if (!Decorate(e, ctm, f.Vp, f.UseDepth, ref clip, out Deco? deco))
        {
            return;
        }

        Open(stack, f, e.Children, st, ctm, f.Vp, clip, st.NeedsLayer || deco is not null, maxLayers: _limits.MaxLayerDepth, deco: deco);
    }

    private void PushUse(Stack<Frame> stack, Frame f, SvgElement e)
    {
        string? href = e.Attr("href");
        if (href is null || href.Length < 2 || href[0] != '#' || !_doc.Ids.TryGetValue(href[1..], out SvgElement? target))
        {
            return;
        }

        // A reference to itself or to something it is inside of, or around again within a chain, draws nothing.
        if (_active.Contains(target) || IsAncestorOrSelf(target, e) || f.UseDepth + 1 > _limits.MaxReferenceDepth)
        {
            if (f.UseDepth + 1 > _limits.MaxReferenceDepth && !_active.Contains(target) && !IsAncestorOrSelf(target, e))
            {
                throw new ImageLimitException($"use references are chained more than {_limits.MaxReferenceDepth} deep.");
            }

            return;
        }

        if (!Begin(f, e, out SvgStyle st, out Affine ctm) || st.Opacity <= 0)
        {
            return;
        }

        Path? useClip = f.Clip;
        if (!Decorate(e, ctm, f.Vp, f.UseDepth + 1, ref useClip, out Deco? useDeco))
        {
            return;
        }

        double x = Resolve(ParseLength(e.Attr("x")), Axis.X, st, f.Vp);
        double y = Resolve(ParseLength(e.Attr("y")), Axis.Y, st, f.Vp);
        if (x != 0 || y != 0)
        {
            ctm = ctm.Multiply(Affine.Translate(x, y));
        }

        Length? uw = Length.TryParse(e.Attr("width") ?? string.Empty, out Length lw) ? lw : null;
        Length? uh = Length.TryParse(e.Attr("height") ?? string.Empty, out Length lh) ? lh : null;
        _active.Add(target);
        (ResolvedPaint ctxFill, ResolvedPaint ctxStroke, BBoxHolder useBox) = ContextPaints(f, st, ctm);
        Open(stack, f, [target], st, ctm, f.Vp, useClip, st.NeedsLayer || useDeco is not null, target, uw, uh, f.UseDepth + 1, _limits.MaxLayerDepth, ctxFill, ctxStroke, useBox, useDeco);
    }

    private static bool IsAncestorOrSelf(SvgElement candidate, SvgElement of)
    {
        for (SvgElement? p = of; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, candidate))
            {
                return true;
            }
        }

        return false;
    }

    // A nested svg, or a symbol instantiated by use: a new viewport with its own coordinate system and (by default) a clip.
    private void PushViewport(Stack<Frame> stack, Frame f, SvgElement e, bool symbol, Length? useW, Length? useH)
    {
        if (symbol)
        {
            // A symbol only renders through use; its style comes from the use element. SVG 1.1 gives it no transform, and neither do we.
            if (!Begin(f, e, out SvgStyle sst, out Affine sctm, ignoreTransform: true) || sst.Opacity <= 0)
            {
                return;
            }

            OpenViewport(stack, f, e, sst, sctm, 0, 0, useW ?? new Length(100, LengthUnit.Percent), useH ?? new Length(100, LengthUnit.Percent));
            return;
        }

        if (!Begin(f, e, out SvgStyle st, out Affine ctm) || st.Opacity <= 0)
        {
            return;
        }

        double x = Resolve(ParseLength(e.Attr("x")), Axis.X, st, f.Vp);
        double y = Resolve(ParseLength(e.Attr("y")), Axis.Y, st, f.Vp);
        Length lw = useW ?? (Length.TryParse(e.Attr("width") ?? string.Empty, out Length a) ? a : new Length(100, LengthUnit.Percent));
        Length lh = useH ?? (Length.TryParse(e.Attr("height") ?? string.Empty, out Length b) ? b : new Length(100, LengthUnit.Percent));
        OpenViewport(stack, f, e, st, ctm, x, y, lw, lh);
    }

    private void OpenViewport(Stack<Frame> stack, Frame f, SvgElement e, in SvgStyle st, Affine ctm, double x, double y, Length lw, Length lh)
    {
        double w = Resolve(lw, Axis.X, st, f.Vp);
        double h = Resolve(lh, Axis.Y, st, f.Vp);
        if (!(w > 0) || !(h > 0))
        {
            return;
        }

        Affine inner = ctm.Multiply(Affine.Translate(x, y));
        Viewport vp = new(w, h);
        if (SvgValues.TryViewBox(e.Attr("viewBox"), out double vx, out double vy, out double vw, out double vh))
        {
            inner = inner.Multiply(AspectRatio.Parse(e.Attr("preserveAspectRatio")).Fit(vx, vy, vw, vh, w, h));
            vp = new Viewport(vw, vh, vx, vy);
        }

        Path? clip = f.Clip;
        if (!Decorate(e, ctm, f.Vp, f.UseDepth, ref clip, out Deco? deco))
        {
            return;
        }

        clip = OverflowClip(clip, e, ctm, x, y, w, h);

        Open(stack, f, e.Children, st, inner, vp, clip, st.NeedsLayer || deco is not null, maxLayers: _limits.MaxLayerDepth, deco: deco);
    }

    private static Length ParseLength(string? v) => v is not null && Length.TryParse(v, out Length l) ? l : Length.Zero;

    private double Resolve(Length l, Axis axis, in SvgStyle st, Viewport vp) => l.Unit switch
    {
        LengthUnit.Px => l.Value,
        LengthUnit.Em => l.Value * st.FontSize,
        LengthUnit.Ex => l.Value * st.FontSize / 2,
        LengthUnit.Rem => l.Value * _rootFontSize,
        _ => l.Value / 100 * (axis == Axis.X ? vp.Width : axis == Axis.Y ? vp.Height : Math.Sqrt(((vp.Width * vp.Width) + (vp.Height * vp.Height)) / 2)),
    };

    private double Num(SvgElement e, string name, Axis axis, in SvgStyle st, Viewport vp) => Resolve(ParseLength(e.Attr(name)), axis, st, vp);

    // ---- conditional processing ----

    // SVG 1.1 section 5.8: an element with requiredFeatures, requiredExtensions or systemLanguage renders only when all of them are true.
    // This renderer supports no extensions, so any requiredExtensions is false; requiredFeatures was removed in SVG 2 and counts as true,
    // except that an empty value is false as in SVG 1.1.
    private bool Conditions(SvgElement e)
    {
        if (e.Attrs.ContainsKey("requiredExtensions"))
        {
            return false;
        }

        if (e.Attrs.TryGetValue("requiredFeatures", out string? features) && features.Trim().Length == 0)
        {
            return false;
        }

        return !e.Attrs.TryGetValue("systemLanguage", out string? languages) || LanguageMatches(languages);
    }

    // A language tag in the value matches a configured language when they are equal, or when the configured one is a prefix of the tag
    // followed by '-' (so "en" matches "en-GB"), ignoring case. An empty list is false.
    private bool LanguageMatches(string value)
    {
        foreach (Range r in value.AsSpan().Split(','))
        {
            ReadOnlySpan<char> tag = value.AsSpan(r).Trim();
            if (tag.IsEmpty)
            {
                continue;
            }

            foreach (string lang in _languages)
            {
                if (tag.Equals(lang, StringComparison.OrdinalIgnoreCase)
                    || (tag.Length > lang.Length && tag[lang.Length] == '-' && tag[..lang.Length].Equals(lang, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSwitchCandidate(string name) =>
        name is "a" or "g" or "image" or "svg" or "switch" or "text" or "use" or "foreignObject" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path";

    // The first direct child that can be rendered and whose conditions hold is the only one that is; the switch itself behaves as a group.
    private void PushSwitch(Stack<Frame> stack, Frame f, SvgElement e)
    {
        if (!Begin(f, e, out SvgStyle st, out Affine ctm) || st.Opacity <= 0)
        {
            return;
        }

        foreach (SvgElement child in e.Children)
        {
            if (IsSwitchCandidate(child.Name) && Conditions(child))
            {
                Open(stack, f, [child], st, ctm, f.Vp, f.Clip, st.Opacity < 1, maxLayers: _limits.MaxLayerDepth);
                return;
            }
        }
    }

    // ---- transform-origin ----

    // The transform t acting around the point transform-origin names in the reference box (unchanged when the value is invalid or zero).
    private Affine AroundOrigin(Affine t, string origin, double rx, double ry, double rw, double rh, in SvgStyle st) =>
        TryOrigin(origin, rx, ry, rw, rh, st, out double ox, out double oy) && (ox != 0 || oy != 0)
            ? Affine.Translate(ox, oy).Multiply(t).Multiply(Affine.Translate(-ox, -oy))
            : t;

    private enum OriginKind
    {
        Length,
        Left,
        Right,
        Top,
        Bottom,
        Center,
    }

    private static OriginKind OriginWord(string t) => t switch
    {
        "left" => OriginKind.Left,
        "right" => OriginKind.Right,
        "top" => OriginKind.Top,
        "bottom" => OriginKind.Bottom,
        "center" => OriginKind.Center,
        _ => OriginKind.Length,
    };

    // CSS Transforms 2: one to three values (a third is a length along z and is ignored); keywords and lengths or percentages of the
    // reference box. A single value is the horizontal one, unless it is top or bottom.
    private bool TryOrigin(string value, double rx, double ry, double rw, double rh, in SvgStyle st, out double ox, out double oy)
    {
        ox = 0;
        oy = 0;
        string[] t = value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (t.Length is 0 or > 3)
        {
            return false;
        }

        OriginKind ka = OriginWord(t[0]);
        OriginKind kb = t.Length >= 2 ? OriginWord(t[1]) : OriginKind.Center;
        string sa = t[0];
        string sb = t.Length >= 2 ? t[1] : "center";
        if (t.Length == 1 && ka is OriginKind.Top or OriginKind.Bottom)
        {
            (ka, kb) = (OriginKind.Center, ka);
            (sa, sb) = ("center", t[0]);
        }
        else if (ka is OriginKind.Top or OriginKind.Bottom || kb is OriginKind.Left or OriginKind.Right)
        {
            (ka, kb) = (kb, ka);
            (sa, sb) = (sb, sa);
        }

        if (ka is OriginKind.Top or OriginKind.Bottom || kb is OriginKind.Left or OriginKind.Right)
        {
            return false;
        }

        if (!OriginOffset(ka, sa, rw, st, out double dx) || !OriginOffset(kb, sb, rh, st, out double dy))
        {
            return false;
        }

        ox = rx + dx;
        oy = ry + dy;
        return double.IsFinite(ox) && double.IsFinite(oy);
    }

    private bool OriginOffset(OriginKind kind, string text, double size, in SvgStyle st, out double offset)
    {
        offset = 0;
        switch (kind)
        {
            case OriginKind.Left or OriginKind.Top:
                return true;
            case OriginKind.Right or OriginKind.Bottom:
                offset = size;
                return true;
            case OriginKind.Center:
                offset = size / 2;
                return true;
        }

        if (!Length.TryParse(text, out Length l))
        {
            return false;
        }

        offset = l.Unit == LengthUnit.Percent ? l.Value / 100 * size : Resolve(l, Axis.Other, st, new Viewport(0, 0));
        return true;
    }

    // ---- images ----

    // An image with a data: URI: a raster decoded by the library's own codecs, or a nested SVG document. Any other href is dangling.
    private void EmitImage(Frame f, SvgElement e)
    {
        if (!Begin(f, e, out SvgStyle st, out Affine ctm) || st.Opacity <= 0 || !st.Visible)
        {
            return;
        }

        // A picture is read once per element, however many times use instantiates it.
        if (!_pictures.TryGetValue(e, out Picture? picture))
        {
            picture = LoadPicture(e);
            _pictures[e] = picture;
        }

        if (picture is null)
        {
            return;
        }

        RasterSource? raster = picture.Raster;
        SvgScene? nested = picture.Nested;
        double iw = picture.Width;
        double ih = picture.Height;
        if (!(iw > 0) || !(ih > 0))
        {
            return;
        }

        double x = Resolve(ParseLength(e.Attr("x")), Axis.X, st, f.Vp);
        double y = Resolve(ParseLength(e.Attr("y")), Axis.Y, st, f.Vp);

        // A missing or auto width or height comes from the picture: both from its size, one from the other through its aspect ratio.
        double w = Length.TryParse(e.Attr("width") ?? string.Empty, out Length lw) ? Resolve(lw, Axis.X, st, f.Vp) : double.NaN;
        double h = Length.TryParse(e.Attr("height") ?? string.Empty, out Length lh) ? Resolve(lh, Axis.Y, st, f.Vp) : double.NaN;
        if (double.IsNaN(w) && double.IsNaN(h))
        {
            (w, h) = (iw, ih);
        }
        else if (double.IsNaN(w))
        {
            w = h * iw / ih;
        }
        else if (double.IsNaN(h))
        {
            h = w * ih / iw;
        }

        if (!(w > 0) || !(h > 0) || !double.IsFinite(w) || !double.IsFinite(h))
        {
            return;
        }

        AspectRatio ar = AspectRatio.Parse(e.Attr("preserveAspectRatio"));
        Affine placed = ctm.Multiply(Affine.Translate(x, y)).Multiply(ar.Fit(0, 0, iw, ih, w, h));
        if (!placed.IsFinite || Math.Abs(placed.A) > Coord.Limit || Math.Abs(placed.D) > Coord.Limit || Math.Abs(placed.E) > Coord.Limit || Math.Abs(placed.F) > Coord.Limit)
        {
            return;
        }

        // A sliced picture overflows its viewport and is cut to it.
        Path? clip = f.Clip;
        if (ar.Slice && !ar.None)
        {
            Path box = Path.Rectangle(Coord.F(x), Coord.F(y), Coord.F(w), Coord.F(h)).Transform(ctm.ToMatrix3x2());
            clip = clip is null ? box : clip.Intersect(box);
        }

        (double bx0, double by0, double bx1, double by1) = placed.TransformRect(0, 0, iw, ih);
        var node = new RImage
        {
            Width = iw,
            Height = ih,
            Ctm = placed,
            Raster = raster,
            Nested = nested,
            Clip = clip,
            Opacity = (float)st.Opacity,
            Pixelated = st.Pixelated,
            ContentX1 = bx1,
            ContentY1 = by1,
            X0 = bx0 - 1,
            Y0 = by0 - 1,
            X1 = bx1 + 1,
            Y1 = by1 + 1,
        };

        if (clip is not null)
        {
            System.Drawing.RectangleF cb = clip.Bounds;
            node.X0 = Math.Max(node.X0, cb.Left - 1);
            node.Y0 = Math.Max(node.Y0, cb.Top - 1);
            node.X1 = Math.Min(node.X1, cb.Right + 1);
            node.Y1 = Math.Min(node.Y1, cb.Bottom + 1);
            if (node.X0 > node.X1 || node.Y0 > node.Y1)
            {
                return;
            }
        }

        _hasContent = true;
        f.Out.Add(node);
    }

    private sealed record Picture(RasterSource? Raster, SvgScene? Nested, double Width, double Height);

    // Reads the data: URI of an image element; null when there is no usable picture (the image is dangling).
    private Picture? LoadPicture(SvgElement e)
    {
        if (e.Attr("href") is not { } href || !DataUri.TryDecode(href, _limits.MaxImageBytes, out byte[] data))
        {
            return null;
        }

        _budget.ImageBytes += data.Length;
        if (_budget.ImageBytes > _limits.MaxTotalImageBytes)
        {
            throw new ImageLimitException($"The embedded images of the document are larger than {_limits.MaxTotalImageBytes} bytes in total.");
        }

        if (SvgSniffer.Matches(data.AsSpan(0, Math.Min(data.Length, 4096))))
        {
            if (_imageDepth >= _limits.MaxImageDepth)
            {
                throw new ImageLimitException($"Images are nested more than {_limits.MaxImageDepth} deep.");
            }

            SvgScene? scene = LoadNested(data);
            return scene is null ? null : new Picture(null, scene, scene.Width, scene.Height);
        }

        try
        {
            Image raster = Image.Load(data, new LoadOptions { Limits = _imageLimits });
            return new Picture(new RasterSource(raster), null, raster.Width, raster.Height);
        }
        catch (ImageFormatException)
        {
            return null;
        }
    }

    // The render tree of a nested SVG image, sharing this document's budgets; null when the data is not a usable SVG document.
    private SvgScene? LoadNested(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            byte[] bytes = SvgSource.Read(stream, _limits, _imageLimits, 0, out _);
            Xml.XmlDocument xml = Xml.XmlParser.Parse(bytes, _limits);
            SvgDocument doc = SvgDocument.Build(xml, _limits);
            return new SceneBuilder(doc, _limits, _imageLimits, _languages, _imageDepth + 1, _budget, _cancel).BuildRoot();
        }
        catch (ImageFormatException)
        {
            return null;
        }
    }

    // ---- shapes ----

    private void EmitShape(Stack<Frame> stack, Frame f, SvgElement e)
    {
        if (!Begin(f, e, out SvgStyle st, out Affine ctm) || st.Opacity <= 0)
        {
            return;
        }

        Path? geometry = Geometry(e, st, f.Vp, out Path? dotted);
        if (geometry is null || geometry.IsEmpty)
        {
            return;
        }

        _hasContent = true;
        bool isLine = e.Name is "line";

        BBoxHolder? bbox = null;
        ResolvedPaint fillPaint = st.Visible ? ResolvePaint(st.Fill, f, st, ctm, geometry, ref bbox) : ResolvedPaint.None;
        bool fill = fillPaint.Kind != ResolvedKind.None;
        Rgba32 fillColor = fillPaint.Color;
        bool stroke = false;
        float width = 0;
        ResolvedPaint strokePaint = ResolvedPaint.None;
        if (st.Visible)
        {
            strokePaint = ResolvePaint(st.Stroke, f, st, ctm, geometry, ref bbox);
            if (strokePaint.Kind != ResolvedKind.None)
            {
                width = Math.Min(Coord.F(Resolve(st.StrokeWidth, Axis.Other, st, f.Vp)), Pen.MaxWidth);
                stroke = width > 0 && float.IsFinite(width);
            }
        }

        Rgba32 strokeColor = strokePaint.Color;

        if (isLine)
        {
            fill = false;
        }

        // Markers of the shape's vertices (path, line, polyline and polygon only), drawn even when the shape itself paints nothing.
        MarkerPlan? markers = PlanMarkers(e, st, f, ctm, fillPaint, strokePaint);
        if (!fill && !stroke && markers is null && e.Prop("filter") is null)
        {
            return;
        }

        float[]? dash = stroke ? ResolveDash(st, f.Vp) : null;
        float dashOffset = dash is null ? 0 : Coord.F(Resolve(st.DashOffset, Axis.Other, st, f.Vp));
        float miter = (float)Math.Clamp(st.MiterLimit, 1, 1e6);
        StrokeJoin join = st.LineJoin switch { 1 => StrokeJoin.Round, 2 => StrokeJoin.Bevel, _ => StrokeJoin.Miter };
        StrokeCap cap = st.LineCap switch { 1 => StrokeCap.Round, 2 => StrokeCap.Square, _ => StrokeCap.Butt };

        Path? clip = f.Clip;
        if (!Decorate(e, ctm, f.Vp, f.UseDepth, ref clip, out Deco? deco, BoxOf(geometry.Bounds)))
        {
            return;
        }

        bool layered = markers is not null ? (st.Opacity < 1 || (st.Blend != BlendMode.Normal || st.Plus) || deco is not null) : ((st.Opacity < 1 && fill && stroke) || (st.Blend != BlendMode.Normal || st.Plus) || deco is not null);
        float groupOpacity = layered ? 1f : (float)st.Opacity;
        RPath? node = new RPath
        {
            Geometry = geometry,
            StrokeGeometry = stroke && cap != StrokeCap.Butt ? dotted : null,
            Ctm = ctm,
            HasFill = fill,
            FillColor = fillColor,
            FillPaint = fillPaint.Server,
            StrokePaint = strokePaint.Server,
            StrokeFirst = st.StrokeFirst,
            FillOpacity = (float)st.FillOpacity * groupOpacity,
            EvenOdd = st.FillEvenOdd,
            HasStroke = stroke,
            StrokeColor = strokeColor,
            StrokeOpacity = (float)st.StrokeOpacity * groupOpacity,
            StrokeWidth = width,
            Cap = cap,
            Join = join,
            MiterLimit = miter,
            Dash = dash,
            DashOffset = dashOffset,
            Antialias = st.Antialias,
            Clip = clip,
        };

        System.Drawing.RectangleF b = geometry.Bounds;
        (double bx0, double by0, double bx1, double by1) = ctm.TransformRect(b.Left, b.Top, b.Right, b.Bottom);
        double pad = AntialiasPad;
        if (stroke)
        {
            double reach = width / 2 * (join == StrokeJoin.Miter ? Math.Max(miter, 1.5) : 1) * (cap == StrokeCap.Square ? 1.5 : 1);
            pad += reach * ctm.MaxScale;
        }

        double half = stroke ? width / 2 * ctm.MaxScale : 0;
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
                if (markers is null)
                {
                    return;
                }

                node = null;
            }
        }

        if (markers is not null)
        {
            markers.Clip = clip;
            EmitMarkers(stack, f, st, ctm, markers, node, layered, deco);
        }
        else if (layered)
        {
            var layer = new RLayer { Children = [node!], Opacity = (float)st.Opacity, Blend = st.Blend, Plus = st.Plus, X0 = node!.X0, Y0 = node.Y0, X1 = node.X1, Y1 = node.Y1, ContentX1 = node.ContentX1, ContentY1 = node.ContentY1 };
            if (deco is not null)
            {
                FinishLayer(stack, layer, deco, f.Out, f.LayerDepth + 1);
            }
            else
            {
                f.Out.Add(layer);
            }
        }
        else
        {
            f.Out.Add(node!);
        }
    }

    private float[]? ResolveDash(in SvgStyle st, Viewport vp)
    {
        Length[]? src = st.DashArray;
        if (src is null || src.Length == 0 || src.Length > Pen.MaxDashEntries / 2)
        {
            return null;
        }

        var list = new float[src.Length % 2 == 0 ? src.Length : src.Length * 2];
        double sum = 0;
        for (int i = 0; i < src.Length; i++)
        {
            double v = Resolve(src[i], Axis.Other, st, vp);
            if (!(v >= 0) || !double.IsFinite(v))
            {
                return null;
            }

            list[i] = Coord.F(v);
            sum += v;
        }

        if (!(sum > 0))
        {
            return null;
        }

        if (src.Length % 2 == 1)
        {
            Array.Copy(list, 0, list, src.Length, src.Length);
        }

        return list;
    }

    private Path? Geometry(SvgElement e, in SvgStyle st, Viewport vp, out Path? dotted)
    {
        dotted = null;
        switch (e.Name)
        {
            case "path":
                if (_pathCache.TryGetValue(e, out (Path? Path, Path? Dotted) cached))
                {
                    dotted = cached.Dotted;
                    return cached.Path;
                }

                string? d = e.Attr("d");
                Path? parsed = null;
                if (d is not null)
                {
                    PathData.Parse(d, out Path p, out dotted);
                    parsed = p;
                }

                _pathCache[e] = (parsed, dotted);
                return parsed;
            case "rect":
                return Rect(e, st, vp);
            case "circle":
            {
                double r = Num(e, "r", Axis.Other, st, vp);
                return r > 0 ? Path.Circle(Coord.F(Num(e, "cx", Axis.X, st, vp)), Coord.F(Num(e, "cy", Axis.Y, st, vp)), Coord.F(r)) : null;
            }

            case "ellipse":
            {
                (double rx, double ry) = Radii(e, st, vp, "rx", "ry", false);
                return rx > 0 && ry > 0 ? Path.Ellipse(Coord.F(Num(e, "cx", Axis.X, st, vp)), Coord.F(Num(e, "cy", Axis.Y, st, vp)), Coord.F(rx), Coord.F(ry)) : null;
            }

            case "line":
            {
                var pb = new PathBuilder();
                pb.MoveTo(Coord.F(Num(e, "x1", Axis.X, st, vp)), Coord.F(Num(e, "y1", Axis.Y, st, vp)));
                float lx2 = Coord.F(Num(e, "x2", Axis.X, st, vp));
                float ly2 = Coord.F(Num(e, "y2", Axis.Y, st, vp));
                pb.LineTo(lx2, ly2);
                Path line = pb.Build();
                if (line.Bounds.Width == 0 && line.Bounds.Height == 0)
                {
                    dotted = new PathBuilder().MoveTo(lx2, ly2).LineTo(lx2, ly2).Close().Build();
                }

                return line;
            }

            case "polyline" or "polygon":
            {
                if (e.Attr("points") is not { } pts)
                {
                    return null;
                }

                List<double> n = SvgValues.NumberList(pts, out _);
                int count = n.Count / 2;
                if (count < 2)
                {
                    return null;
                }

                var pb = new PathBuilder();
                pb.MoveTo(Coord.F(n[0]), Coord.F(n[1]));
                for (int i = 1; i < count; i++)
                {
                    pb.LineTo(Coord.F(n[2 * i]), Coord.F(n[(2 * i) + 1]));
                }

                if (e.Name == "polygon")
                {
                    pb.Close();
                }

                return pb.Build();
            }

            default:
                return null;
        }
    }

    // rx/ry with the SVG 2 "auto" rule: a missing or invalid radius takes the other's value.
    private (double Rx, double Ry) Radii(SvgElement e, in SvgStyle st, Viewport vp, string nx, string ny, bool zeroIsAuto)
    {
        double? rx = RadiusOrNull(e, nx, Axis.X, st, vp);
        double? ry = RadiusOrNull(e, ny, Axis.Y, st, vp);
        _ = zeroIsAuto;
        if (rx is null && ry is null)
        {
            return (0, 0);
        }

        return (rx ?? ry!.Value, ry ?? rx!.Value);
    }

    private double? RadiusOrNull(SvgElement e, string name, Axis axis, in SvgStyle st, Viewport vp)
    {
        string? v = e.Attr(name);
        if (v is null || !Length.TryParse(v, out Length l) || v.Trim() == "auto")
        {
            return null;
        }

        double r = Resolve(l, axis, st, vp);
        return r >= 0 ? r : null;
    }

    private Path? Rect(SvgElement e, in SvgStyle st, Viewport vp)
    {
        double x = Num(e, "x", Axis.X, st, vp);
        double y = Num(e, "y", Axis.Y, st, vp);
        double w = Num(e, "width", Axis.X, st, vp);
        double h = Num(e, "height", Axis.Y, st, vp);
        if (!(w > 0) || !(h > 0))
        {
            return null;
        }

        double? rxo = RadiusOrNull(e, "rx", Axis.X, st, vp);
        double? ryo = RadiusOrNull(e, "ry", Axis.Y, st, vp);
        double rx = rxo ?? ryo ?? 0;
        double ry = ryo ?? rxo ?? 0;
        rx = Math.Min(rx, w / 2);
        ry = Math.Min(ry, h / 2);
        if (!(rx > 0) || !(ry > 0))
        {
            return Path.Rectangle(Coord.F(x), Coord.F(y), Coord.F(w), Coord.F(h));
        }

        var b = new PathBuilder();
        b.MoveTo(Coord.F(x + rx), Coord.F(y));
        b.LineTo(Coord.F(x + w - rx), Coord.F(y));
        b.ArcTo(Coord.F(rx), Coord.F(ry), 0, false, true, Coord.F(x + w), Coord.F(y + ry));
        b.LineTo(Coord.F(x + w), Coord.F(y + h - ry));
        b.ArcTo(Coord.F(rx), Coord.F(ry), 0, false, true, Coord.F(x + w - rx), Coord.F(y + h));
        b.LineTo(Coord.F(x + rx), Coord.F(y + h));
        b.ArcTo(Coord.F(rx), Coord.F(ry), 0, false, true, Coord.F(x), Coord.F(y + h - ry));
        b.LineTo(Coord.F(x), Coord.F(y + ry));
        b.ArcTo(Coord.F(rx), Coord.F(ry), 0, false, true, Coord.F(x + rx), Coord.F(y));
        b.Close();
        return b.Build();
    }
}
