using System.Globalization;
using OneImage.Drawing;
using OneImage.Svg.Dom;
using OneImage.Text;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Render;

// Text: collecting the characters of a text element, laying them out, and turning the placed clusters into render nodes.
internal sealed partial class SceneBuilder
{
    // Shaping options for kerning on or off, small capitals on or off and upright vertical forms, shared (shaping only reads them).
    private static readonly ShapingOptions[] ShapeOptions = MakeShapeOptions();

    private readonly Dictionary<SvgElement, TextStyle> _textStyles = [];

    // The use element that is instantiating an element right now. Text inherits its style from the use element's ancestors, not from the
    // ancestors of the referenced element. An element can only be instantiated once at a time (use cycles are cut), so one entry is enough.
    private readonly Dictionary<SvgElement, SvgElement> _useOf = [];

    private SvgElement? TextParent(SvgElement e) => _useOf.TryGetValue(e, out SvgElement? use) ? use : e.Parent;

    private void SetUseOf(SvgElement target, SvgElement? use)
    {
        if (use is null)
        {
            _useOf.Remove(target);
        }
        else
        {
            _useOf[target] = use;
        }

        // Styles computed through the old chain no longer apply.
        if (_textStyles.Count > 0)
        {
            _textStyles.Clear();
        }
    }

    private static ShapingOptions[] MakeShapeOptions()
    {
        var all = new ShapingOptions[8];
        for (int i = 0; i < 8; i++)
        {
            var o = new ShapingOptions { Kerning = (i & 1) != 0 ? KerningMode.None : KerningMode.Normal, Vertical = (i & 4) != 0 };
            if ((i & 2) != 0)
            {
                o.Features.Add(new FontFeature("smcp", 1));
            }

            all[i] = o;
        }

        return all;
    }

    /// <summary>The text style of any element, from the root down (what its children would inherit).</summary>
    private TextStyle TextStyleOf(SvgElement e)
    {
        if (_textStyles.TryGetValue(e, out TextStyle? cached))
        {
            return cached;
        }

        var chain = new Stack<SvgElement>();
        for (SvgElement? p = e; p is not null && !_textStyles.ContainsKey(p); p = TextParent(p))
        {
            chain.Push(p);
        }

        TextStyle ts = TextParent(chain.Peek()) is { } above ? _textStyles[above] : TextStyle.Initial;
        while (chain.Count > 0)
        {
            SvgElement n = chain.Pop();
            ts = TextStyle.Compute(n, ts, StyleOf(n), _rootVp.Width);
            _textStyles[n] = ts;
        }

        return ts;
    }

    // ---- collecting ----

    private sealed class CollectFrame
    {
        public required SvgElement El { get; init; }

        public required TSpan Span { get; init; }

        public int Next { get; set; }

        public double[]? X { get; init; }

        public double[]? Y { get; init; }

        public double[]? Dx { get; init; }

        public double[]? Dy { get; init; }

        public double[]? Rot { get; init; }
    }

    private bool _lastCollapsibleSpace;

    private TSpan NewSpan(TextModel m, SvgElement el, in SvgStyle st, TextStyle ts, TSpan? parent, bool isPath)
    {
        FontChain chain = _fonts.Chain(ts.Families, ts.Weight, ts.Italic, st.FontSize);
        var span = new TSpan
        {
            Id = m.Spans.Count,
            El = el,
            St = st,
            Ts = ts,
            Parent = parent,
            Start = m.Length,
            Chain = chain,
            Primary = chain.First,
        };
        span.PathSpan = isPath ? span : parent?.PathSpan;
        m.Spans.Add(span);
        return span;
    }

    private static double BaselineHeight(BaselineKind kind, Font? font)
    {
        if (font is null || kind is BaselineKind.Auto or BaselineKind.Alphabetic)
        {
            return 0;
        }

        FontMetrics fm = font.Metrics;
        double s = (double)font.Size / fm.UnitsPerEm;
        double asc = fm.Ascender * s;
        double desc = fm.Descender * s;
        return kind switch
        {
            BaselineKind.Hanging => asc * 0.8,
            BaselineKind.Ideographic => desc,
            BaselineKind.Mathematical => asc / 2,
            BaselineKind.Central => (asc + desc) / 2,
            BaselineKind.Middle => (fm.XHeight > 0 ? fm.XHeight * s : font.Size * 0.5) / 2,
            BaselineKind.TextBefore => asc,
            BaselineKind.TextAfter => desc,
            _ => 0,
        };
    }

    private void ComputeShift(TSpan span, TSpan? parent, Viewport vp)
    {
        double own = BaselineHeight(span.Ts.Dominant, span.Primary);
        if (parent is null)
        {
            // The reference renderer lets alignment-baseline on the text element itself set its baseline too.
            span.ShiftDown = span.Ts.Alignment != BaselineKind.Auto ? BaselineHeight(span.Ts.Alignment, span.Primary) : own;
            return;
        }

        BaselineKind align = span.Ts.Alignment != BaselineKind.Auto ? span.Ts.Alignment : span.Ts.Dominant;
        double bx = BaselineHeight(align, span.Primary);
        double bp = BaselineHeight(parent.Ts.Dominant, parent.Primary);
        double shift = parent.ShiftDown + (bx - bp);
        switch (span.Ts.Shift)
        {
            case ShiftKind.Sub:
            {
                double drop = parent.Primary is { } pf ? FontFaceInfo.GetSubscriptDrop(pf) : 0;
                shift += drop > 0 ? drop : span.St.FontSize * 0.2;
                break;
            }

            case ShiftKind.Super:
            {
                double raise = parent.Primary is { } pf ? FontFaceInfo.GetSuperscriptRaise(pf) : 0;
                shift -= raise > 0 ? raise : span.St.FontSize * 0.4;
                break;
            }

            case ShiftKind.Length:
            {
                Length l = span.Ts.ShiftLength;
                double up = l.Unit == LengthUnit.Percent ? l.Value / 100 * parent.St.FontSize : Resolve(l, Axis.Other, span.St, vp);
                shift -= up;
                break;
            }
        }

        span.ShiftDown = shift;
    }

    private static double[]? ParseLengthList(SceneBuilder self, string? text, Axis axis, in SvgStyle st, Viewport vp)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var list = new List<double>();
        foreach (Range r in text.AsSpan().SplitAny(" \t\n\r,"))
        {
            ReadOnlySpan<char> tok = text.AsSpan(r);
            if (tok.IsEmpty)
            {
                continue;
            }

            if (!Length.TryParse(tok, out Length l))
            {
                return null;
            }

            double v = self.Resolve(l, axis, st, vp);
            if (!double.IsFinite(v))
            {
                return null;
            }

            list.Add(v);
        }

        return list.Count == 0 ? null : [.. list];
    }

    private static double[]? ParseNumberList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var list = new List<double>();
        foreach (Range r in text.AsSpan().SplitAny(" \t\n\r,"))
        {
            ReadOnlySpan<char> tok = text.AsSpan(r);
            if (tok.IsEmpty)
            {
                continue;
            }

            if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            {
                return null;
            }

            list.Add(v);
        }

        return list.Count == 0 ? null : [.. list];
    }

    private CollectFrame OpenFrame(SvgElement el, TSpan span, Viewport vp, bool positions)
    {
        SvgStyle st = span.St;
        return new CollectFrame
        {
            El = el,
            Span = span,
            X = positions ? ParseLengthList(this, el.Attr("x"), Axis.X, st, vp) : null,
            Y = positions ? ParseLengthList(this, el.Attr("y"), Axis.Y, st, vp) : null,
            Dx = ParseLengthList(this, el.Attr("dx"), Axis.X, st, vp),
            Dy = ParseLengthList(this, el.Attr("dy"), Axis.Y, st, vp),
            Rot = ParseNumberList(el.Attr("rotate")),
        };
    }

    private void AppendText(TextModel m, TSpan span, string text)
    {
        _budget.TextChars += text.Length;
        if (_budget.TextChars > _limits.MaxTextCharacters)
        {
            throw new ImageLimitException($"The SVG has more than {_limits.MaxTextCharacters} characters of text.");
        }

        bool preserve = span.Ts.PreserveSpace;
        int id = span.Id;
        foreach (char raw in text)
        {
            char c = raw;
            if (c is '\n' or '\r' or '\t')
            {
                c = ' ';
            }

            if (preserve)
            {
                // A preserved space still ends the collapsing run: the next collapsible space after it is dropped.
                m.Append(c, id);
                _lastCollapsibleSpace = c == ' ';
                continue;
            }

            if (c == ' ')
            {
                if (_lastCollapsibleSpace)
                {
                    continue;
                }

                _lastCollapsibleSpace = true;
            }
            else
            {
                _lastCollapsibleSpace = false;
            }

            m.Append(c, id);
        }
    }

    // Gives the characters of an element their coordinates from its lists: the i-th addressable character of the element gets the i-th value.
    // Children close first, so a character keeps the value of the nearest element that has one.
    private static void AssignLists(TextModel m, CollectFrame f)
    {
        int end = m.Length;
        Assign(m, f.Span.Start, end, f.X, static (mm, i, v) => TextModel.SetIfUnset(ref mm.X, i, v), repeatLast: false);
        Assign(m, f.Span.Start, end, f.Y, static (mm, i, v) => TextModel.SetIfUnset(ref mm.Y, i, v), repeatLast: false);
        Assign(m, f.Span.Start, end, f.Dx, static (mm, i, v) => TextModel.SetIfUnset(ref mm.Dx, i, v), repeatLast: false);
        Assign(m, f.Span.Start, end, f.Dy, static (mm, i, v) => TextModel.SetIfUnset(ref mm.Dy, i, v), repeatLast: false);
        Assign(m, f.Span.Start, end, f.Rot, static (mm, i, v) => TextModel.SetIfUnset(ref mm.Rot, i, v), repeatLast: true);
    }

    private static void Assign(TextModel m, int from, int to, double[]? values, Action<TextModel, int, double> set, bool repeatLast)
    {
        if (values is null)
        {
            return;
        }

        int k = 0;
        for (int i = from; i < to; i++)
        {
            if (char.IsLowSurrogate(m.Text[i]) && i > from && char.IsHighSurrogate(m.Text[i - 1]))
            {
                continue;
            }

            if (k >= values.Length)
            {
                if (!repeatLast)
                {
                    return;
                }

                set(m, i, values[^1]);
                continue;
            }

            set(m, i, values[k++]);
        }
    }

    // The text content of the element a tref points at, descendants included, in document order.
    private string TrefText(SvgElement tref)
    {
        if (HrefTarget(tref) is not { } target)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        var seen = new HashSet<SvgElement> { target };
        var stack = new Stack<(SvgElement E, int I)>();
        stack.Push((target, 0));
        while (stack.Count > 0 && sb.Length <= _limits.MaxTextCharacters)
        {
            (SvgElement e, int i) = stack.Pop();
            if (e.TextContent is null || i >= e.TextContent.Count)
            {
                continue;
            }

            stack.Push((e, i + 1));
            TextNode n = e.TextContent[i];
            if (n.Text is { } t)
            {
                sb.Append(t);
            }
            else if (n.Element is { } c && seen.Add(c))
            {
                stack.Push((c, 0));
            }
        }

        return sb.ToString();
    }

    private TextModel? Collect(SvgElement textEl, in SvgStyle st, Viewport vp)
    {
        var m = new TextModel();
        SvgElement? textParent = TextParent(textEl);
        TextStyle parentTs = textParent is null ? TextStyle.Initial : TextStyleOf(textParent);
        TextStyle ts = TextStyle.Compute(textEl, parentTs, st, _rootVp.Width);

        // A decoration set on an ancestor of the text element applies to the text, drawn with the text element's own paint.
        for (SvgElement? anc = textParent; anc is not null; anc = TextParent(anc))
        {
            TextStyle at = TextStyleOf(anc);
            ts.Underline |= at.Underline;
            ts.Overline |= at.Overline;
            ts.LineThrough |= at.LineThrough;
        }

        TSpan root = NewSpan(m, textEl, st, ts, null, isPath: false);
        ComputeShift(root, null, vp);
        ApplyTextLength(root, vp);
        _lastCollapsibleSpace = true;
        var stack = new Stack<CollectFrame>();
        stack.Push(OpenFrame(textEl, root, vp, positions: true));
        int guard = 0;
        while (stack.Count > 0)
        {
            CollectFrame f = stack.Peek();
            List<TextNode> content = f.El.TextContent!;
            if (f.Next >= content.Count)
            {
                f.Span.End = m.Length;
                AssignLists(m, f);
                stack.Pop();
                continue;
            }

            if ((++guard & 0x3FF) == 0)
            {
                _cancel.ThrowIfCancellationRequested();
            }

            TextNode node = content[f.Next++];
            if (node.Text is { } text)
            {
                AppendText(m, f.Span, text);
                continue;
            }

            SvgElement child = node.Element!;
            if (child.Name is not ("tspan" or "textPath" or "a" or "tref") || (child.Attrs.Count > 0 && !Conditions(child)))
            {
                continue;
            }

            Count();
            SvgStyle cst = SvgStyle.Compute(child, f.Span.St, _rootFontSize);
            if (!cst.Displayed)
            {
                continue;
            }

            TextStyle cts = TextStyle.Compute(child, f.Span.Ts, cst, _rootVp.Width);
            bool isPath = child.Name == "textPath";
            TSpan span = NewSpan(m, child, cst, cts, f.Span, isPath);
            if (isPath)
            {
                // A textPath whose link does not lead to a shape draws nothing.
                if (!SetUpPath(span, vp))
                {
                    m.Spans.RemoveAt(m.Spans.Count - 1);
                    continue;
                }
            }

            ComputeShift(span, f.Span, vp);
            ApplyTextLength(span, vp);
            if (child.Name == "tref")
            {
                // The referenced text is set as if it were a tspan with the tref's own attributes.
                CollectFrame tf = OpenFrame(child, span, vp, positions: true);
                AppendText(m, span, TrefText(child));
                span.End = m.Length;
                AssignLists(m, tf);
                continue;
            }

            stack.Push(OpenFrame(child, span, vp, positions: !isPath));
        }

        // A collapsible space left at the end of the text is not part of it.
        if (_lastCollapsibleSpace && m.Length > 0 && m.Text[^1] == ' ')
        {
            m.Text.Length--;
        }

        return m;
    }

    private void ApplyTextLength(TSpan span, Viewport vp)
    {
        if (span.El.Attr("textLength") is { } tl && Length.TryParse(tl, out Length l))
        {
            double v = Resolve(l, Axis.X, span.St, vp);
            if (v >= 0 && double.IsFinite(v))
            {
                span.TextLength = v;
                span.StretchGlyphs = span.El.Attr("lengthAdjust")?.Trim() == "spacingAndGlyphs";
            }
        }
    }

    // Resolves a textPath's link into a route along the referenced shape.
    private bool SetUpPath(TSpan span, Viewport vp)
    {
        SvgElement el = span.El;
        if (HrefTarget(el) is not { } target || !IsShape(target))
        {
            return false;
        }

        SvgStyle tst = StyleOf(target);
        Path? geometry = Geometry(target, tst, vp, out _);
        if (geometry is null || geometry.IsEmpty)
        {
            return false;
        }

        if (target.Attr("transform") is { } ts && SvgValues.TryTransform(ts, out Affine t) && t.IsFinite)
        {
            geometry = geometry.Transform(t.ToMatrix3x2());
        }

        PathRoute? route = PathRoute.Create(geometry, reverse: el.Attr("side")?.Trim() == "right");
        if (route is null)
        {
            return false;
        }

        span.Route = route;
        if (el.Attr("startOffset") is { } so && Length.TryParse(so, out Length sl))
        {
            span.StartOffsetPercent = sl.Unit == LengthUnit.Percent;
            span.StartOffset = span.StartOffsetPercent ? sl.Value / 100 : Resolve(sl, Axis.X, span.St, vp);
        }

        return true;
    }
}
