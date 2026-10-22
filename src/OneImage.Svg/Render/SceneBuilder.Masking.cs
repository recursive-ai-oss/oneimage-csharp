using OneImage.Drawing;
using OneImage.Svg.Dom;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

// Clipping and masking: clip-path and mask references on elements, resolved into scene-space clip regions and mask layers.
internal sealed partial class SceneBuilder
{
    // Clip regions are flattened in scene space (the natural-size pixel grid); a render at several times that size still sees edges
    // within a tenth of a pixel at 5x.
    private const float ClipTolerance = 0.02f;
    private const int MaxCachedClips = 4096;

    // Resolving a clip child is a polygon boolean operation, a hundred times a plain shape's cost, so it is charged as several instances.
    private const int ClipChildCost = 16;

    private readonly record struct Box(double X, double Y, double W, double H)
    {
        public bool IsValid => W > 0 && H > 0 && double.IsFinite(W) && double.IsFinite(H) && double.IsFinite(X) && double.IsFinite(Y);
    }

    /// <summary>What an element asks for besides opacity: a clip that needs its bounding box, and/or a mask.</summary>
    private sealed class Deco
    {
        public SvgElement? ClipEl { get; init; }

        public SvgElement? MaskEl { get; init; }

        /// <summary>The element's user space in scene space (the clip and the mask are defined in it).</summary>
        public Affine Ctm { get; init; }

        public Viewport Vp { get; init; }

        /// <summary>The element's object bounding box in its user space when it is known up front (a shape); otherwise measured from the children.</summary>
        public Box? Box { get; init; }

        public int UseDepth { get; init; }
    }

    private readonly HashSet<SvgElement> _activeClips = [];
    private readonly HashSet<SvgElement> _activeMasks = [];
    private readonly Dictionary<SvgElement, SvgStyle> _definedStyles = [];
    private readonly Dictionary<(SvgElement, Affine, Box?), Path> _clipCache = [];
    private HashSet<SvgElement>? _brokenMaskLinks;

    // The computed style of an element reached through its own ancestors (clipPath and mask content inherit from where it is defined).
    private SvgStyle DefinedStyle(SvgElement e)
    {
        if (_definedStyles.TryGetValue(e, out SvgStyle cached))
        {
            return cached;
        }

        var chain = new Stack<SvgElement>();
        SvgElement? p = e;
        SvgStyle parent = SvgStyle.Initial;
        while (p is not null)
        {
            if (_definedStyles.TryGetValue(p, out SvgStyle c))
            {
                parent = c;
                break;
            }

            chain.Push(p);
            p = p.Parent;
        }

        SvgStyle result = parent;
        while (chain.Count > 0)
        {
            SvgElement n = chain.Pop();
            result = SvgStyle.Compute(n, result, _rootFontSize);
            _definedStyles[n] = result;
        }

        return result;
    }

    // url(#id) of the expected element kind; anything else (none, a missing id, an external or wrongly typed target) is no reference.
    private SvgElement? Lookup(string? value, string kind)
    {
        if (value is null)
        {
            return null;
        }

        string v = value.Trim();
        if (!v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int close = v.IndexOf(')', 4);
        if (close < 0)
        {
            return null;
        }

        string inner = v[4..close].Trim().Trim('\'', '"').Trim();
        return inner.Length > 1 && inner[0] == '#' && _doc.Ids.TryGetValue(inner[1..], out SvgElement? target) && target.Name == kind ? target : null;
    }

    // The mask an element refers to, unless that link closes a cycle of masks (see BreakMaskCycles).
    private SvgElement? MaskRef(SvgElement e)
    {
        SvgElement? mk = Lookup(e.Prop("mask"), "mask");
        if (mk is null)
        {
            return null;
        }

        _brokenMaskLinks ??= BreakMaskCycles();
        return _brokenMaskLinks.Contains(e) ? null : mk;
    }

    /// <summary>
    /// Finds the <c>mask</c> links that close a cycle (a mask whose own content or own <c>mask</c> leads back to it) and returns the elements that carry
    /// them; such a link is ignored for good, wherever the mask is used. Masks are visited in document order and the link found closing a cycle
    /// first is the one dropped, so a cycle of two masks leaves the one that is visited first complete. One depth-first pass with an explicit stack.
    /// </summary>
    private HashSet<SvgElement> BreakMaskCycles()
    {
        var broken = new HashSet<SvgElement>();
        var masks = new List<SvgElement>();
        var walk = new Stack<SvgElement>();
        walk.Push(_doc.Root);
        while (walk.Count > 0)
        {
            SvgElement n = walk.Pop();
            if (n.Name == "mask")
            {
                masks.Add(n);
            }

            for (int i = n.Children.Count - 1; i >= 0; i--)
            {
                walk.Push(n.Children[i]);
            }
        }

        var state = new Dictionary<SvgElement, int>(); // 1 on the current path, 2 finished
        foreach (SvgElement start in masks)
        {
            if (state.ContainsKey(start))
            {
                continue;
            }

            var path = new Stack<(SvgElement Mask, List<(SvgElement Owner, SvgElement Target)> Links, int Next)>();
            state[start] = 1;
            path.Push((start, MaskLinks(start), 0));
            while (path.Count > 0)
            {
                (SvgElement mask, List<(SvgElement Owner, SvgElement Target)> links, int next) = path.Pop();
                if (next >= links.Count)
                {
                    state[mask] = 2;
                    continue;
                }

                path.Push((mask, links, next + 1));
                (SvgElement owner, SvgElement target) = links[next];
                if (!state.TryGetValue(target, out int s))
                {
                    state[target] = 1;
                    path.Push((target, MaskLinks(target), 0));
                }
                else if (s == 1)
                {
                    broken.Add(owner);
                }
            }
        }

        return broken;
    }

    // Every mask reference made by a mask element or by anything inside it: the owner of the attribute and the mask it names.
    private List<(SvgElement Owner, SvgElement Target)> MaskLinks(SvgElement mask)
    {
        var links = new List<(SvgElement, SvgElement)>();
        var walk = new Stack<SvgElement>();
        walk.Push(mask);
        while (walk.Count > 0)
        {
            SvgElement n = walk.Pop();
            if (Lookup(n.Prop("mask"), "mask") is { } target)
            {
                links.Add((n, target));
            }

            for (int i = n.Children.Count - 1; i >= 0; i--)
            {
                walk.Push(n.Children[i]);
            }
        }

        return links;
    }

    private static Affine Invert(Affine m)
    {
        double det = m.Determinant;
        return new Affine(m.D / det, -m.B / det, -m.C / det, m.A / det, ((m.C * m.F) - (m.D * m.E)) / det, ((m.B * m.E) - (m.A * m.F)) / det);
    }

    private static Box? BoxOf(System.Drawing.RectangleF b) => new Box(b.Left, b.Top, b.Width, b.Height);

    private static Path RectPath(Box b, Affine toScene) =>
        Path.Rectangle(Coord.F(b.X), Coord.F(b.Y), Coord.F(b.W), Coord.F(b.H)).Transform(toScene.ToMatrix3x2());

    // ---- viewport clipping ----

    /// <summary>
    /// The clip a viewport-establishing element (nested <c>svg</c>, <c>symbol</c>, and the marker and pattern builders) gives its content: the
    /// viewport rectangle (in the user space <paramref name="toScene"/> maps to scene space) intersected with the clip already in force,
    /// unless <c>overflow</c> lets the content show. The legacy <c>clip</c> property is not used, as in the reference renderer.
    /// </summary>
    private static Path? OverflowClip(Path? clip, SvgElement e, Affine toScene, double x, double y, double w, double h)
    {
        if (OverflowVisible(e))
        {
            return clip;
        }

        Path rect = Path.Rectangle(Coord.F(x), Coord.F(y), Coord.F(w), Coord.F(h)).Transform(toScene.ToMatrix3x2());
        return clip is null ? rect : clip.Intersect(rect);
    }

    // overflow is not inherited, but `inherit` takes the parent's value; elements that establish a viewport hide the overflow unless told otherwise.
    private static bool OverflowVisible(SvgElement e)
    {
        for (SvgElement? p = e; p is not null; p = p.Parent)
        {
            string? v = p.Prop("overflow");
            if (v is null)
            {
                return p.Name is not ("svg" or "symbol" or "marker" or "pattern");
            }

            if (v != "inherit")
            {
                return v is "visible" or "auto";
            }
        }

        return true;
    }

    // ---- element-level entry points ----

    /// <summary>
    /// Looks at the <c>clip-path</c> and <c>mask</c> of an element about to render. A clip whose reference box is not needed becomes part
    /// of <paramref name="clip"/> right away (so children inherit it and no layer is needed); a clip that needs the bounding box of a group, and
    /// every mask, come back as a <paramref name="deco"/> that the element's layer completes once its children exist.
    /// Returns false when the clip leaves nothing to draw.
    /// </summary>
    private bool Decorate(SvgElement e, Affine ctm, Viewport vp, int useDepth, ref Path? clip, out Deco? deco, Box? box = null)
    {
        deco = null;
        SvgElement? cp = Lookup(e.Prop("clip-path"), "clipPath");
        SvgElement? mk = MaskRef(e);
        SvgElement? deferredClip = null;
        if (cp is not null)
        {
            if (box is null && ClipChainNeedsBox(cp))
            {
                deferredClip = cp;
            }
            else
            {
                Path region = ClipRegion(cp, ctm, vp, box, 0);
                if (region.IsEmpty)
                {
                    return false;
                }

                clip = clip is null ? region : clip.Intersect(region);
                if (clip.IsEmpty)
                {
                    return false;
                }
            }
        }

        if (deferredClip is not null || mk is not null)
        {
            deco = new Deco { ClipEl = deferredClip, MaskEl = mk, Ctm = ctm, Vp = vp, Box = box, UseDepth = useDepth };
        }

        return true;
    }

    // A clipPath, or one it links to through its own clip-path, in objectBoundingBox units.
    private bool ClipChainNeedsBox(SvgElement cp)
    {
        SvgElement? c = cp;
        for (int i = 0; c is not null && i <= _limits.MaxReferenceDepth; i++)
        {
            if (c.Attr("clipPathUnits") == "objectBoundingBox")
            {
                return true;
            }

            c = Lookup(c.Prop("clip-path"), "clipPath");
            if (ReferenceEquals(c, cp))
            {
                break;
            }
        }

        return false;
    }

    // The clip of one owner element (a shape inside a clipPath, or a clipPath itself) or null for none; recursion into a clip that is already being
    // resolved ignores the link, which is what breaks cycles.
    private Path? ClipOf(SvgElement owner, Affine ctm, Viewport vp, Box? box, int depth)
    {
        SvgElement? cp = Lookup(owner.Prop("clip-path"), "clipPath");
        if (cp is null || _activeClips.Contains(cp))
        {
            return null;
        }

        return ClipRegion(cp, ctm, vp, box, depth);
    }

    /// <summary>
    /// The region a clipPath keeps, in scene space; <see cref="Path.Empty"/> when it keeps nothing. <paramref name="ctm"/> is the
    /// user space of the element that references it. Nested clips make the calls recurse, bounded by the reference-depth limit (a constant, not the
    /// document's depth).
    /// </summary>
    private Path ClipRegion(SvgElement cp, Affine ctm, Viewport vp, Box? box, int depth)
    {
        if (depth >= _limits.MaxReferenceDepth)
        {
            throw new ImageLimitException($"clip-path references are chained more than {_limits.MaxReferenceDepth} deep.");
        }

        // The same clip under the same transform (and box, when its units use one) resolves to the same region.
        bool cacheable = _activeClips.Count == 0;
        (SvgElement, Affine, Box?) key = (cp, ctm, cp.Attr("clipPathUnits") == "objectBoundingBox" ? box : null);
        if (cacheable && _clipCache.TryGetValue(key, out Path? hit))
        {
            return hit;
        }

        _activeClips.Add(cp);
        try
        {
            Path result = BuildClip(cp, ctm, vp, box, depth);
            if (cacheable && _clipCache.Count < MaxCachedClips)
            {
                _clipCache[key] = result;
            }

            return result;
        }
        finally
        {
            _activeClips.Remove(cp);
        }
    }

    private Path BuildClip(SvgElement cp, Affine ctm, Viewport vp, Box? box, int depth)
    {
        SvgStyle cpStyle = DefinedStyle(cp);
        Affine m = ctm;
        if (cp.Attr("transform") is { } ts && SvgValues.TryTransform(ts, out Affine t))
        {
            if (!t.IsInvertible)
            {
                return Path.Empty;
            }

            m = m.Multiply(t);
        }

        if (cp.Attr("clipPathUnits") == "objectBoundingBox")
        {
            if (box is not { IsValid: true } b)
            {
                return Path.Empty;
            }

            m = m.Multiply(new Affine(b.W, 0, 0, b.H, b.X, b.Y));
        }

        if (!m.IsFinite)
        {
            return Path.Empty;
        }

        var pieces = new List<Path>();
        foreach (SvgElement child in cp.Children)
        {
            _instances += ClipChildCost - 1;
            Count();
            Path? piece = ClipChild(child, cpStyle, m, vp, depth);
            if (piece is { IsEmpty: false })
            {
                pieces.Add(piece);
            }
        }

        Path union = pieces.Count == 0 ? Path.Empty : PathBoolean.UnionAll(pieces.ToArray(), IntersectionRule.NonZero, ClipTolerance);
        if (!union.IsEmpty && ClipOf(cp, ctm, vp, box, depth + 1) is { } own)
        {
            union = own.IsEmpty ? Path.Empty : union.Intersect(own);
        }

        return union;
    }

    // One child of a clipPath: a basic shape, or a use that points straight at one (text joins here when it exists). Everything else contributes nothing.
    private Path? ClipChild(SvgElement child, in SvgStyle parentStyle, Affine m, Viewport vp, int depth)
    {
        SvgElement shape = child;
        SvgStyle st;
        Affine cm;
        Affine useM = m;
        SvgElement? useEl = null;
        if (child.Name == "use")
        {
            if (!BeginIn(parentStyle, m, child, out SvgStyle ust, out Affine ucm, false) || child.Attr("href") is not { Length: > 1 } href || href[0] != '#'
                || !_doc.Ids.TryGetValue(href[1..], out SvgElement? target) || !IsShape(target) || IsAncestorOrSelf(target, child))
            {
                return null;
            }

            useEl = child;
            useM = ucm;
            double x = Resolve(ParseLength(child.Attr("x")), Axis.X, ust, vp);
            double y = Resolve(ParseLength(child.Attr("y")), Axis.Y, ust, vp);
            if (x != 0 || y != 0)
            {
                ucm = ucm.Multiply(Affine.Translate(x, y));
            }

            shape = target;
            if (!BeginIn(ust, ucm, shape, out st, out cm, false))
            {
                return null;
            }
        }
        else
        {
            if (!IsShape(child) || !BeginIn(parentStyle, m, child, out st, out cm, false))
            {
                return null;
            }
        }

        // visibility: a hidden child draws nothing and so clips nothing away either.
        if (!st.Visible)
        {
            return null;
        }

        Path? geometry = Geometry(shape, st, vp, out _);
        if (geometry is null || geometry.IsEmpty)
        {
            return null;
        }

        Path piece = geometry.Transform(cm.ToMatrix3x2()).Resolve(st.ClipEvenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero, ClipTolerance);
        Box? box = BoxOf(geometry.Bounds);
        if (ClipOf(shape, cm, vp, box, depth + 1) is { } own)
        {
            piece = own.IsEmpty || piece.IsEmpty ? Path.Empty : piece.Intersect(own);
        }

        if (useEl is not null && !piece.IsEmpty && ClipOf(useEl, useM, vp, box, depth + 1) is { } useClip)
        {
            piece = useClip.IsEmpty ? Path.Empty : piece.Intersect(useClip);
        }

        return piece;
    }

    private static bool IsShape(SvgElement e) => e.Name is "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path";

    // ---- layers: complete a decorated layer once its children are known ----

    /// <summary>The object bounding box of the children of a layer in the user space of the element, measured on the nodes.</summary>
    private static Box? MeasureBox(List<RNode> children, Affine ctm)
    {
        if (!ctm.IsInvertible)
        {
            return null;
        }

        Affine inv = Invert(ctm);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        var stack = new Stack<List<RNode>>();
        stack.Push(children);
        while (stack.Count > 0)
        {
            foreach (RNode n in stack.Pop())
            {
                if (n is RLayer layer)
                {
                    stack.Push(layer.Children);
                    continue;
                }

                // A shape goes through the one combined matrix, so a shape drawn in the element's own space gets its exact box (a
                // box of a rotated box would grow); other nodes are measured by their scene bounds.
                (double u0, double v0, double u1, double v1) = n is RPath p
                    ? inv.Multiply(p.Ctm).TransformRect(p.Geometry.Bounds.Left, p.Geometry.Bounds.Top, p.Geometry.Bounds.Right, p.Geometry.Bounds.Bottom)
                    : inv.TransformRect(n.X0, n.Y0, n.X1, n.Y1);
                x0 = Math.Min(x0, u0);
                y0 = Math.Min(y0, v0);
                x1 = Math.Max(x1, u1);
                y1 = Math.Max(y1, v1);
            }
        }

        return x0 <= x1 && y0 <= y1 ? new Box(x0, y0, x1 - x0, y1 - y0) : null;
    }

    private static bool Narrow(RNode node, RNode by, double slack)
    {
        node.X0 = Math.Max(node.X0, by.X0 - slack);
        node.Y0 = Math.Max(node.Y0, by.Y0 - slack);
        node.X1 = Math.Min(node.X1, by.X1 + slack);
        node.Y1 = Math.Min(node.Y1, by.Y1 + slack);
        return node.X0 <= node.X1 && node.Y0 <= node.Y1;
    }

    /// <summary>
    /// Completes <paramref name="layer"/>: applies the deferred clip and the mask chain, then hands it to <paramref name="parent"/>. A layer
    /// whose clip, mask or mask content leaves nothing is dropped. Mask content is built by frames pushed on <paramref name="stack"/>, so
    /// this returns before the layer is final; the layer joins <paramref name="parent"/> when the last frame closes.
    /// </summary>
    private void FinishLayer(Stack<Frame> stack, RLayer layer, Deco deco, List<RNode> parent, int layerDepth)
    {
        Box? box = deco.Box;
        if (box is null && (deco.ClipEl is not null || deco.MaskEl is not null))
        {
            box = MeasureBox(layer.Children, deco.Ctm);
        }

        if (deco.ClipEl is { } cp)
        {
            Path region = ClipRegion(cp, deco.Ctm, deco.Vp, box, 0);
            if (region.IsEmpty)
            {
                return;
            }

            layer.ClipPath = region;
            System.Drawing.RectangleF rb = region.Bounds;
            var rbNode = new RLayer { Children = [], X0 = rb.Left - 1, Y0 = rb.Top - 1, X1 = rb.Right + 1, Y1 = rb.Bottom + 1 };
            if (!Narrow(layer, rbNode, 0))
            {
                return;
            }
        }

        if (deco.MaskEl is { } mk)
        {
            BuildMask(stack, layer, mk, deco, box, layerDepth, [], () => parent.Add(layer));
        }
        else
        {
            parent.Add(layer);
        }
    }

    // Builds the mask layer of `target` from mask element `m`, then (when m itself has a mask) the next one, then calls done.
    private void BuildMask(Stack<Frame> stack, RLayer target, SvgElement m, Deco deco, Box? box, int depth, List<SvgElement> held, Action done)
    {
        if (depth + 1 > _limits.MaxLayerDepth)
        {
            throw new ImageLimitException($"Masks and clips are nested more than {_limits.MaxLayerDepth} deep.");
        }

        if (_activeMasks.Contains(m))
        {
            // Recursion: the link is ignored, as for clip paths.
            ReleaseMasks(held);
            done();
            return;
        }

        SvgStyle mst = DefinedStyle(m);
        bool objUnits = m.Attr("maskUnits") != "userSpaceOnUse";
        bool contentObj = m.Attr("maskContentUnits") == "objectBoundingBox";
        if ((objUnits || contentObj) && box is not { IsValid: true })
        {
            // The mask needs the bounding box of an element that has none: the element is not drawn.
            ReleaseMasks(held);
            return;
        }

        double rx, ry, rw, rh;
        if (objUnits)
        {
            Box b = box!.Value;
            rx = b.X + (Fraction(m.Attr("x"), -0.1) * b.W);
            ry = b.Y + (Fraction(m.Attr("y"), -0.1) * b.H);
            rw = Fraction(m.Attr("width"), 1.2) * b.W;
            rh = Fraction(m.Attr("height"), 1.2) * b.H;
        }
        else
        {
            rx = Resolve(Length.TryParse(m.Attr("x") ?? string.Empty, out Length lx) ? lx : new Length(-10, LengthUnit.Percent), Axis.X, mst, deco.Vp);
            ry = Resolve(Length.TryParse(m.Attr("y") ?? string.Empty, out Length ly) ? ly : new Length(-10, LengthUnit.Percent), Axis.Y, mst, deco.Vp);
            rw = Resolve(Length.TryParse(m.Attr("width") ?? string.Empty, out Length lw) ? lw : new Length(120, LengthUnit.Percent), Axis.X, mst, deco.Vp);
            rh = Resolve(Length.TryParse(m.Attr("height") ?? string.Empty, out Length lh) ? lh : new Length(120, LengthUnit.Percent), Axis.Y, mst, deco.Vp);
        }

        if (!(rw > 0) || !(rh > 0) || !double.IsFinite(rx + ry + rw + rh))
        {
            ReleaseMasks(held);
            return;
        }

        Affine content = deco.Ctm;
        if (contentObj)
        {
            Box b = box!.Value;
            content = content.Multiply(new Affine(b.W, 0, 0, b.H, b.X, b.Y));
        }

        Path region = RectPath(new Box(rx, ry, rw, rh), deco.Ctm);
        var layer = new RLayer
        {
            Children = [],
            MaskAlpha = m.Prop("mask-type") == "alpha",
        };
        target.Mask = layer;
        _activeMasks.Add(m);
        held.Add(m);
        var frame = new Frame
        {
            Kids = m.Children,
            Style = mst,
            Ctm = content,
            Vp = deco.Vp,
            Clip = region,
            Out = layer.Children,
            LayerDepth = depth + 1,
            UseDepth = deco.UseDepth,
        };
        frame.After = s =>
        {
            if (layer.Children.Count == 0)
            {
                // A mask with nothing in it hides the element.
                ReleaseMasks(held);
                return;
            }

            Union(layer, layer.Children);
            if (!Narrow(target, layer, 0))
            {
                ReleaseMasks(held);
                return;
            }

            SvgElement? next = MaskRef(m);
            if (next is not null)
            {
                BuildMask(s, layer, next, deco, box, depth + 1, held, done);
                return;
            }

            ReleaseMasks(held);
            done();
        };
        stack.Push(frame);
    }

    private void ReleaseMasks(List<SvgElement> held)
    {
        foreach (SvgElement e in held)
        {
            _activeMasks.Remove(e);
        }

        held.Clear();
    }

    // A mask region coordinate in objectBoundingBox units: a number or a percentage is a fraction of the box.
    private static double Fraction(string? v, double fallback)
    {
        if (v is null || !Length.TryParse(v, out Length l))
        {
            return fallback;
        }

        return l.Unit == LengthUnit.Percent ? l.Value / 100 : l.Value;
    }
}
