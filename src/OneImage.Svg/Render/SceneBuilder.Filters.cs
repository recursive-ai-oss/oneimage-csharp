using System.Globalization;
using OneImage.Svg.Dom;

namespace OneImage.Svg.Render;

// Filters: the filter property of an element (url references and CSS filter functions) resolved into filter stages once the element's
// bounding box is known (see FinishLayer).
internal sealed partial class SceneBuilder
{
    private enum FilterStatus
    {
        /// <summary>The element has no filter.</summary>
        None,

        /// <summary>The element has a filter that cannot be applied, which removes the element from the drawing.</summary>
        NotRendered,

        /// <summary>The element has filters to apply.</summary>
        Active,
    }

    /// <summary>A parsed CSS filter function: its name and arguments in user units (or the color of a shadow).</summary>
    private sealed class FilterFunction
    {
        public required string Name { get; init; }

        public double A { get; init; }

        public double B { get; init; }

        public double C { get; init; }

        public Rgba32 Color { get; init; }
    }

    /// <summary>The filter property of an element: references to filter elements and functions, in order.</summary>
    private sealed class FilterRequest
    {
        public required List<(SvgElement? Element, FilterFunction? Function)> Items { get; init; }
    }

    // Work allowed for finding recursive filter links, so a hostile document of many feImage elements over big subtrees stays cheap.
    private const int MaxRecursionScan = 4_000_000;

    private readonly HashSet<SvgElement> _activeFilters = [];
    private HashSet<SvgElement>? _brokenFilterLinks;
    private int _filterDepth;
    private static ImageConfiguration? s_rasterConfig;

    private static readonly HashSet<string> PrimitiveNames = new(StringComparer.Ordinal)
    {
        "feBlend", "feColorMatrix", "feComposite", "feConvolveMatrix", "feDropShadow", "feFlood", "feGaussianBlur", "feImage", "feMerge", "feOffset",
        "feComponentTransfer", "feMorphology", "feTile", "feTurbulence", "feDisplacementMap", "feDiffuseLighting", "feSpecularLighting",
    };

    // ---- the filter property ----

    private FilterStatus ParseFilterProperty(SvgElement e, string value, out FilterRequest? request)
    {
        request = null;
        _brokenFilterLinks ??= BreakFilterRecursion();
        if (_brokenFilterLinks.Contains(e))
        {
            return FilterStatus.None;
        }

        string v = value.Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return FilterStatus.None;
        }

        SvgStyle st = DefinedStyle(e);
        var items = new List<(SvgElement?, FilterFunction?)>();
        bool missing = false;
        int i = 0;
        while (i < v.Length)
        {
            if (char.IsWhiteSpace(v[i]))
            {
                i++;
                continue;
            }

            int open = v.IndexOf('(', i);
            if (open < 0)
            {
                return FilterStatus.None;
            }

            int close = MatchingParen(v, open);
            if (close < 0)
            {
                return FilterStatus.None;
            }

            string name = v[i..open].Trim().ToLowerInvariant();
            string args = v[(open + 1)..close].Trim();
            i = close + 1;
            if (name == "url")
            {
                string id = args.Trim('\'', '"').Trim();
                if (id.Length > 1 && id[0] == '#' && _doc.Ids.TryGetValue(id[1..], out SvgElement? target) && target.Name == "filter")
                {
                    if (_activeFilters.Contains(target))
                    {
                        // A filter that is (through an feImage) being applied to its own result: the reference is ignored.
                        return FilterStatus.None;
                    }

                    items.Add((target, null));
                }
                else
                {
                    missing = true;
                }

                continue;
            }

            FilterFunction? fn = ParseFunction(name, args, st);
            if (fn is null)
            {
                // One invalid function makes the whole declaration invalid, so it is ignored.
                return FilterStatus.None;
            }

            items.Add((null, fn));
        }

        if (items.Count == 0)
        {
            return missing ? FilterStatus.NotRendered : FilterStatus.None;
        }

        request = new FilterRequest { Items = items };
        return FilterStatus.Active;
    }

    /// <summary>
    /// Finds the <c>filter</c> properties that would make a filter draw itself: an <c>feImage</c> that references an element (or something inside it)
    /// whose own filter is the one the <c>feImage</c> belongs to. Such a property is ignored wherever it appears, so the element is drawn without it.
    /// </summary>
    private HashSet<SvgElement> BreakFilterRecursion()
    {
        var broken = new HashSet<SvgElement>();
        var images = new List<SvgElement>();
        var walk = new Stack<SvgElement>();
        walk.Push(_doc.Root);
        int budget = MaxRecursionScan;
        while (walk.Count > 0)
        {
            SvgElement n = walk.Pop();
            if (n.Name == "feImage" && n.Attr("href") is { Length: > 1 } h && h[0] == '#')
            {
                images.Add(n);
            }

            for (int i = n.Children.Count - 1; i >= 0; i--)
            {
                walk.Push(n.Children[i]);
            }
        }

        foreach (SvgElement image in images)
        {
            SvgElement? filter = image.Parent;
            if (filter is null || filter.Name != "filter" || filter.Id is null || !_doc.Ids.TryGetValue(image.Attr("href")![1..], out SvgElement? target))
            {
                continue;
            }

            string reference = "#" + filter.Id;
            walk.Push(target);
            while (walk.Count > 0)
            {
                SvgElement n = walk.Pop();
                if (--budget < 0)
                {
                    return broken;
                }

                if (n.Prop("filter") is { } fv && fv.Contains(reference, StringComparison.Ordinal))
                {
                    broken.Add(n);
                }

                foreach (SvgElement child in n.Children)
                {
                    walk.Push(child);
                }
            }
        }

        return broken;
    }

    private static int MatchingParen(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '(')
            {
                depth++;
            }
            else if (s[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // Splits function arguments on white space and commas, keeping parenthesized parts (rgb(...)) together.
    private static List<string>? SplitArgs(string args)
    {
        var list = new List<string>();
        int depth = 0;
        int start = -1;
        for (int i = 0; i <= args.Length; i++)
        {
            char ch = i < args.Length ? args[i] : ' ';
            if (depth == 0 && (char.IsWhiteSpace(ch) || ch == ','))
            {
                if (ch == ',')
                {
                    // Function arguments are separated by white space; a comma makes the function invalid.
                    return null;
                }

                if (start >= 0)
                {
                    list.Add(args[start..i]);
                    start = -1;
                }

                continue;
            }

            if (start < 0)
            {
                start = i;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
            }
        }

        return list;
    }

    private FilterFunction? ParseFunction(string name, string args, in SvgStyle st)
    {
        List<string>? a = SplitArgs(args);
        if (a is null)
        {
            return null;
        }

        switch (name)
        {
            case "blur":
            {
                if (a.Count > 1 || (a.Count == 1 && !TryFilterLength(a[0], st, out double r)))
                {
                    return null;
                }

                double radius = 0;
                if (a.Count == 1)
                {
                    TryFilterLength(a[0], st, out radius);
                }

                return radius < 0 ? null : new FilterFunction { Name = name, A = radius };
            }

            case "drop-shadow":
            {
                Rgba32 color = st.Color;
                var lengths = new List<double>();
                foreach (string token in a)
                {
                    if (token.Equals("currentcolor", StringComparison.OrdinalIgnoreCase))
                    {
                        color = st.Color;
                    }
                    else if (TryFilterLength(token, st, out double len))
                    {
                        lengths.Add(len);
                    }
                    else if (CssColor.TryParse(token, out Rgba32 c))
                    {
                        color = c;
                    }
                    else
                    {
                        return null;
                    }
                }

                if (lengths.Count > 3 || (lengths.Count is 1) || (lengths.Count == 3 && lengths[2] < 0))
                {
                    return null;
                }

                return new FilterFunction
                {
                    Name = name,
                    A = lengths.Count > 0 ? lengths[0] : 0,
                    B = lengths.Count > 1 ? lengths[1] : 0,
                    C = lengths.Count > 2 ? lengths[2] : 0,
                    Color = color,
                };
            }

            case "brightness" or "contrast" or "grayscale" or "invert" or "opacity" or "saturate" or "sepia":
            {
                if (a.Count > 1)
                {
                    return null;
                }

                double amount = 1;
                if (a.Count == 1 && !TryAmount(a[0], out amount))
                {
                    return null;
                }

                if (amount < 0)
                {
                    return null;
                }

                if (name is "grayscale" or "invert" or "opacity" or "sepia")
                {
                    amount = Math.Min(amount, 1);
                }

                return new FilterFunction { Name = name, A = amount };
            }

            case "hue-rotate":
            {
                if (a.Count > 1)
                {
                    return null;
                }

                double angle = 0;
                if (a.Count == 1 && !TryFilterAngle(a[0], out angle))
                {
                    return null;
                }

                return new FilterFunction { Name = name, A = angle };
            }

            default:
                return null;
        }
    }

    private bool TryFilterLength(string token, in SvgStyle st, out double value)
    {
        value = 0;
        if (!Length.TryParse(token, out Length l) || l.Unit == LengthUnit.Percent)
        {
            return false;
        }

        value = l.Unit switch
        {
            LengthUnit.Em => l.Value * st.FontSize,
            LengthUnit.Ex => l.Value * st.FontSize / 2,
            LengthUnit.Rem => l.Value * _rootFontSize,
            _ => l.Value,
        };
        return double.IsFinite(value);
    }

    private static bool TryAmount(string token, out double value)
    {
        value = 0;
        ReadOnlySpan<char> t = token.AsSpan();
        bool percent = t.EndsWith("%");
        if (percent)
        {
            t = t[..^1];
        }

        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            return false;
        }

        if (percent)
        {
            value /= 100;
        }

        return true;
    }

    private static bool TryFilterAngle(string token, out double degrees)
    {
        degrees = 0;
        int len = SvgSize.NumberLength(token);
        if (len == 0 || !double.TryParse(token.AsSpan(0, len), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            return false;
        }

        switch (token[len..].ToLowerInvariant())
        {
            case "": degrees = v; return v == 0; // a bare number is an angle only when it is zero
            case "deg": degrees = v; return true;
            case "grad": degrees = v * 0.9; return true;
            case "rad": degrees = v * 180 / Math.PI; return true;
            case "turn": degrees = v * 360; return true;
            default: return false;
        }
    }

    // ---- building the stages of a layer ----

    /// <summary>
    /// Resolves the filters of <paramref name="request"/> for a layer whose content is known, sets them on the layer and narrows the layer's
    /// bounds to the last filter's region. Returns false when a filter cannot be applied and the element is not drawn.
    /// </summary>
    private bool ApplyFilters(RLayer layer, FilterRequest request, Deco deco, Box? box)
    {
        if (!deco.Ctm.IsInvertible)
        {
            return false;
        }

        var stages = new List<RFilter>();
        (double X, double Y, double W, double H)? current = null;
        if (layer.Children.Count > 0)
        {
            (double x0, double y0, double x1, double y1) = Invert(deco.Ctm).TransformRect(layer.X0, layer.Y0, layer.X1, layer.Y1);
            current = (x0, y0, x1 - x0, y1 - y0);
        }

        foreach ((SvgElement? element, FilterFunction? fn) in request.Items)
        {
            RFilter? stage;
            if (element is not null)
            {
                stage = BuildFilterElement(element, deco, box);
            }
            else if (current is { } cur)
            {
                stage = BuildFunctionStage(fn!, deco, cur);
            }
            else
            {
                return false;
            }

            if (stage is null)
            {
                return false;
            }

            stages.Add(stage);
            current = stage.Region;
        }

        RFilter last = stages[^1];
        (double rx, double ry, double rw, double rh) = last.Region;
        (double sx0, double sy0, double sx1, double sy1) = deco.Ctm.TransformRect(rx, ry, rx + rw, ry + rh);
        if (!double.IsFinite(sx0 + sy0 + sx1 + sy1))
        {
            return false;
        }

        layer.Filters = [.. stages];
        layer.X0 = sx0 - 1;
        layer.Y0 = sy0 - 1;
        layer.X1 = sx1 + 1;
        layer.Y1 = sy1 + 1;
        return true;
    }

    private static RFilter? BuildFunctionStage(FilterFunction fn, Deco deco, (double X, double Y, double W, double H) cur)
    {
        // Functions have no region of their own: the content's box grown by what the function adds around it.
        double margin = fn.Name switch
        {
            "blur" => (3.2 * fn.A) + 4,
            "drop-shadow" => (3.2 * fn.C) + 4 + Math.Max(Math.Abs(fn.A), Math.Abs(fn.B)),
            _ => 0,
        };
        double x = cur.X - margin;
        double y = cur.Y - margin;
        double w = cur.W + (2 * margin);
        double h = cur.H + (2 * margin);
        if (!(w > 0) || !(h > 0) || !double.IsFinite(x + y + w + h))
        {
            return null;
        }

        FePrimitive prim = fn.Name switch
        {
            "blur" => new FeBlur { SigmaX = fn.A, SigmaY = fn.A, SubX = x, SubY = y, SubW = w, SubH = h },
            // A shadow with no offset and no blur sits exactly behind the element and adds nothing that shows.
            "drop-shadow" when fn.A == 0 && fn.B == 0 && fn.C == 0 => new FeColorMatrix { Matrix = IdentityMatrix, SubX = x, SubY = y, SubW = w, SubH = h },
            "drop-shadow" => new FeDropShadow { Dx = fn.A, Dy = fn.B, SigmaX = fn.C, SigmaY = fn.C, Color = fn.Color, SubX = x, SubY = y, SubW = w, SubH = h },
            _ => new FeColorMatrix { Matrix = FunctionMatrix(fn), SubX = x, SubY = y, SubW = w, SubH = h },
        };
        return new RFilter { Ctm = deco.Ctm, Region = (x, y, w, h), Primitives = [prim] };
    }

    private static double[] FunctionMatrix(FilterFunction fn)
    {
        double a = fn.A;
        switch (fn.Name)
        {
            case "brightness":
                return [a, 0, 0, 0, 0, 0, a, 0, 0, 0, 0, 0, a, 0, 0, 0, 0, 0, 1, 0];
            case "contrast":
            {
                double o = 0.5 - (0.5 * a);
                return [a, 0, 0, 0, o, 0, a, 0, 0, o, 0, 0, a, 0, o, 0, 0, 0, 1, 0];
            }

            case "invert":
            {
                double s = 1 - (2 * a);
                return [s, 0, 0, 0, a, 0, s, 0, 0, a, 0, 0, s, 0, a, 0, 0, 0, 1, 0];
            }

            case "opacity":
                return [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, a, 0];
            case "grayscale":
            {
                double g = 1 - a;
                return
                [
                    0.2126 + (0.7874 * g), 0.7152 - (0.7152 * g), 0.0722 - (0.0722 * g), 0, 0,
                    0.2126 - (0.2126 * g), 0.7152 + (0.2848 * g), 0.0722 - (0.0722 * g), 0, 0,
                    0.2126 - (0.2126 * g), 0.7152 - (0.7152 * g), 0.0722 + (0.9278 * g), 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }

            case "sepia":
            {
                double g = 1 - a;
                return
                [
                    0.393 + (0.607 * g), 0.769 - (0.769 * g), 0.189 - (0.189 * g), 0, 0,
                    0.349 - (0.349 * g), 0.686 + (0.314 * g), 0.168 - (0.168 * g), 0, 0,
                    0.272 - (0.272 * g), 0.534 - (0.534 * g), 0.131 + (0.869 * g), 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }

            case "saturate":
                return SaturateMatrix(a);
            default:
                return HueRotateMatrix(a);
        }
    }

    private static double[] SaturateMatrix(double s) =>
    [
        0.213 + (0.787 * s), 0.715 - (0.715 * s), 0.072 - (0.072 * s), 0, 0,
        0.213 - (0.213 * s), 0.715 + (0.285 * s), 0.072 - (0.072 * s), 0, 0,
        0.213 - (0.213 * s), 0.715 - (0.715 * s), 0.072 + (0.928 * s), 0, 0,
        0, 0, 0, 1, 0,
    ];

    private static double[] HueRotateMatrix(double degrees)
    {
        double rad = degrees * Math.PI / 180;
        double c = Math.Cos(rad);
        double s = Math.Sin(rad);
        return
        [
            0.213 + (c * 0.787) - (s * 0.213), 0.715 - (c * 0.715) - (s * 0.715), 0.072 - (c * 0.072) + (s * 0.928), 0, 0,
            0.213 - (c * 0.213) + (s * 0.143), 0.715 + (c * 0.285) + (s * 0.140), 0.072 - (c * 0.072) - (s * 0.283), 0, 0,
            0.213 - (c * 0.213) - (s * 0.787), 0.715 - (c * 0.715) + (s * 0.715), 0.072 + (c * 0.928) + (s * 0.072), 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    // ---- filter elements ----

    // The filter element and the ones its href chain leads to (cycles and non-filters end the chain).
    private List<SvgElement> FilterChain(SvgElement fe)
    {
        var chain = new List<SvgElement> { fe };
        SvgElement cur = fe;
        while (chain.Count <= _limits.MaxReferenceDepth && cur.Attr("href") is { Length: > 1 } href && href[0] == '#'
            && _doc.Ids.TryGetValue(href[1..], out SvgElement? next) && next.Name == "filter" && !chain.Contains(next))
        {
            chain.Add(next);
            cur = next;
        }

        return chain;
    }

    private static string? ChainAttr(List<SvgElement> chain, string name)
    {
        foreach (SvgElement f in chain)
        {
            if (f.Attr(name) is { } v)
            {
                return v;
            }
        }

        return null;
    }

    private RFilter? BuildFilterElement(SvgElement fe, Deco deco, Box? box)
    {
        List<SvgElement> chain = FilterChain(fe);
        SvgStyle fst = DefinedStyle(fe);
        bool objUnits = ChainAttr(chain, "filterUnits") != "userSpaceOnUse";
        bool primObj = ChainAttr(chain, "primitiveUnits") == "objectBoundingBox";
        if ((objUnits || primObj) && box is not { IsValid: true })
        {
            return null;
        }

        double rx, ry, rw, rh;
        if (objUnits)
        {
            Box b = box!.Value;
            rx = b.X + (Fraction(ChainAttr(chain, "x"), -0.1) * b.W);
            ry = b.Y + (Fraction(ChainAttr(chain, "y"), -0.1) * b.H);
            rw = Fraction(ChainAttr(chain, "width"), 1.2) * b.W;
            rh = Fraction(ChainAttr(chain, "height"), 1.2) * b.H;
        }
        else
        {
            rx = Resolve(FilterLength(ChainAttr(chain, "x"), new Length(-10, LengthUnit.Percent)), Axis.X, fst, deco.Vp);
            ry = Resolve(FilterLength(ChainAttr(chain, "y"), new Length(-10, LengthUnit.Percent)), Axis.Y, fst, deco.Vp);
            rw = Resolve(FilterLength(ChainAttr(chain, "width"), new Length(120, LengthUnit.Percent)), Axis.X, fst, deco.Vp);
            rh = Resolve(FilterLength(ChainAttr(chain, "height"), new Length(120, LengthUnit.Percent)), Axis.Y, fst, deco.Vp);
        }

        if (!(rw > 0) || !(rh > 0) || !double.IsFinite(rx + ry + rw + rh))
        {
            return null;
        }

        SvgElement? holder = chain.Find(f => f.Children.Exists(c => PrimitiveNames.Contains(c.Name)));
        if (holder is null)
        {
            return null;
        }

        if (++_filterDepth > _limits.MaxFilterDepth)
        {
            _filterDepth--;
            throw new ImageLimitException($"Filters are nested more than {_limits.MaxFilterDepth} deep.");
        }

        _activeFilters.Add(fe);
        try
        {
            var prims = new List<FePrimitive>();
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            var subs = new List<(double X, double Y, double W, double H)>();
            foreach (SvgElement child in holder.Children)
            {
                if (!PrimitiveNames.Contains(child.Name))
                {
                    continue;
                }

                if (prims.Count >= _limits.MaxFilterPrimitives)
                {
                    throw new ImageLimitException($"A filter has more than {_limits.MaxFilterPrimitives} primitives.");
                }

                Count();
                FePrimitive p = BuildPrimitive(child, prims.Count, names, primObj, box, (rx, ry, rw, rh), deco, prims);
                prims.Add(p);
                if (child.Attr("result") is { Length: > 0 } rn)
                {
                    names[rn] = prims.Count - 1;
                }
            }

            _ = subs;
            return new RFilter { Ctm = deco.Ctm, Region = (rx, ry, rw, rh), Primitives = [.. prims] };
        }
        finally
        {
            _activeFilters.Remove(fe);
            _filterDepth--;
        }
    }

    private static Length FilterLength(string? v, Length fallback) => v is not null && Length.TryParse(v, out Length l) ? l : fallback;

    private static int ResolveInput(string? value, int index, Dictionary<string, int> names)
    {
        string v = value?.Trim() ?? string.Empty;
        switch (v)
        {
            case "SourceGraphic": return FeIn.SourceGraphic;
            case "SourceAlpha": return FeIn.SourceAlpha;
            // These inputs are not supported and are read as the source graphic.
            case "BackgroundImage" or "BackgroundAlpha" or "FillPaint" or "StrokePaint": return FeIn.SourceGraphic;
        }

        if (v.Length > 0 && names.TryGetValue(v, out int named))
        {
            return named;
        }

        // No input, or one that names nothing: the previous result, or the source for the first primitive.
        return index == 0 ? FeIn.SourceGraphic : index - 1;
    }

    private static bool LinearInterpolation(SvgElement prim)
    {
        for (SvgElement? p = prim; p is not null; p = p.Parent)
        {
            switch (p.Prop("color-interpolation-filters"))
            {
                case "linearRGB": return true;
                case "sRGB" or "auto": return false;
            }
        }

        return true;
    }

    // A property that is not inherited, with `inherit` taking the parent's value.
    private static string? OwnProp(SvgElement e, string name)
    {
        for (SvgElement? p = e; p is not null; p = p.Parent)
        {
            string? v = p.Prop(name);
            if (v is null || v != "inherit")
            {
                return v;
            }
        }

        return null;
    }

    private (Rgba32 Color, float Opacity) FloodPaint(SvgElement prim)
    {
        Rgba32 color = new(0, 0, 0, 255);
        if (OwnProp(prim, "flood-color") is { } fc)
        {
            string t = fc.Trim();
            if (t.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
            {
                color = DefinedStyle(prim).Color;
            }
            else if (CssColor.TryParse(t, out Rgba32 parsed))
            {
                color = parsed;
            }
        }

        float opacity = 1;
        if (OwnProp(prim, "flood-opacity") is { } fo && TryAmount(fo.Trim(), out double o))
        {
            opacity = (float)Math.Clamp(o, 0, 1);
        }

        return (color, opacity);
    }

    private static double[]? Numbers(string? v)
    {
        if (v is null)
        {
            return null;
        }

        List<double> list = SvgValues.NumberList(v, out bool clean);
        return clean ? [.. list] : null;
    }

    private static double Number(string? v, double fallback)
    {
        double[]? n = Numbers(v);
        return n is { Length: 1 } ? n[0] : fallback;
    }

    private FePrimitive BuildPrimitive(SvgElement e, int index, Dictionary<string, int> names, bool primObj, Box? box, (double X, double Y, double W, double H) region, Deco deco, List<FePrimitive> prior)
    {
        bool linear = LinearInterpolation(e);
        int in1 = ResolveInput(e.Attr("in"), index, names);
        int in2 = ResolveInput(e.Attr("in2"), index, names);
        double bw = primObj ? box!.Value.W : 1;
        double bh = primObj ? box!.Value.H : 1;

        // Subregion: each of x, y, width, height that is missing takes the filter region's.
        SvgStyle est = DefinedStyle(e);
        (double sx, double sy, double sw, double sh, bool empty) = Subregion(e, primObj, box, region, region, est, deco);

        FePrimitive p;
        switch (e.Name)
        {
            case "feGaussianBlur":
            {
                (double dx, double dy) = StdDeviation(e.Attr("stdDeviation"), 0);
                p = new FeBlur { In1 = in1, Linear = linear, SigmaX = dx * bw, SigmaY = dy * bh, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
                break;
            }

            case "feOffset":
                p = new FeOffset { In1 = in1, Linear = linear, Dx = Number(e.Attr("dx"), 0) * bw, Dy = Number(e.Attr("dy"), 0) * bh, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
                break;
            case "feFlood":
            {
                (Rgba32 color, float opacity) = FloodPaint(e);
                p = new FeFlood { Linear = linear, Color = color, Opacity = opacity, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
                break;
            }

            case "feDropShadow":
            {
                (Rgba32 color, float opacity) = FloodPaint(e);
                (double sdx, double sdy) = StdDeviation(e.Attr("stdDeviation"), 2);
                p = new FeDropShadow
                {
                    In1 = in1,
                    Linear = linear,
                    Dx = Number(e.Attr("dx"), 2) * bw,
                    Dy = Number(e.Attr("dy"), 2) * bh,
                    SigmaX = sdx * bw,
                    SigmaY = sdy * bh,
                    Color = color,
                    Opacity = opacity,
                    SubX = sx,
                    SubY = sy,
                    SubW = sw,
                    SubH = sh,
                    EmptySub = empty,
                };
                break;
            }

            case "feMerge":
            {
                var layers = new List<int>();
                foreach (SvgElement node in e.Children)
                {
                    if (node.Name == "feMergeNode")
                    {
                        layers.Add(ResolveInput(node.Attr("in"), index, names));
                    }
                }

                p = new FeMerge { Layers = [.. layers], Linear = linear, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
                break;
            }

            case "feBlend":
                p = new FeBlend { In1 = in1, In2 = in2, Linear = linear, Mode = FilterBlendMode(e.Attr("mode")), SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
                break;
            case "feComposite":
            {
                FeCompositeOp op = e.Attr("operator")?.Trim() switch
                {
                    "in" => FeCompositeOp.In,
                    "out" => FeCompositeOp.Out,
                    "atop" => FeCompositeOp.Atop,
                    "xor" => FeCompositeOp.Xor,
                    "lighter" => FeCompositeOp.Lighter,
                    "arithmetic" => FeCompositeOp.Arithmetic,
                    _ => FeCompositeOp.Over,
                };
                p = new FeComposite
                {
                    In1 = in1,
                    In2 = in2,
                    Linear = linear,
                    Op = op,
                    K1 = Number(e.Attr("k1"), 0),
                    K2 = Number(e.Attr("k2"), 0),
                    K3 = Number(e.Attr("k3"), 0),
                    K4 = Number(e.Attr("k4"), 0),
                    SubX = sx,
                    SubY = sy,
                    SubW = sw,
                    SubH = sh,
                    EmptySub = empty,
                };
                break;
            }

            case "feColorMatrix":
                p = new FeColorMatrix { In1 = in1, Linear = linear, Matrix = ColorMatrixOf(e), SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
                break;
            case "feConvolveMatrix":
                p = ConvolveOf(e, in1, linear, sx, sy, sw, sh, empty);
                break;
            case "feImage":
                p = ImageOf(e, linear, sx, sy, sw, sh, empty, region, deco, box);
                break;
            default:
                p = BuildEffectPrimitive(e, in1, in2, linear, primObj, box, sx, sy, sw, sh, empty)
                    ?? new FeColorMatrix { In1 = in1, Linear = linear, Matrix = IdentityMatrix, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
                break;
        }

        (double tx, double ty, double tw, double th, bool tEmpty) = Subregion(e, primObj, box, region, DefaultSubregion(e, in1, in2, index, names, prior, region), est, deco);
        p.Tile = tEmpty ? (0, 0, 0, 0) : (tx, ty, tw, th);
        return p;
    }

    private static (double X, double Y) StdDeviation(string? value, double fallback)
    {
        if (value is null)
        {
            return (fallback, fallback);
        }

        double[]? n = Numbers(value);
        if (n is null || n.Length is 0 or > 2)
        {
            return (0, 0);
        }

        double x = n[0];
        double y = n.Length == 2 ? n[1] : n[0];
        return x < 0 || y < 0 ? (0, 0) : (x, y);
    }

    // The subregion a primitive gets for the values it does not set: the union of the subregions of the results it reads, or the filter
    // region when it reads a standard input or nothing (and for feTile, which fills the whole region).
    private static (double X, double Y, double W, double H) DefaultSubregion(SvgElement e, int in1, int in2, int index, Dictionary<string, int> names, List<FePrimitive> prior, (double X, double Y, double W, double H) region)
    {
        var refs = new List<int>();
        switch (e.Name)
        {
            case "feFlood" or "feImage" or "feTurbulence" or "feTile":
                return region;
            case "feMerge":
                foreach (SvgElement node in e.Children)
                {
                    if (node.Name == "feMergeNode")
                    {
                        refs.Add(ResolveInput(node.Attr("in"), index, names));
                    }
                }

                break;
            case "feBlend" or "feComposite" or "feDisplacementMap":
                refs.Add(in1);
                refs.Add(in2);
                break;
            default:
                refs.Add(in1);
                break;
        }

        if (refs.Count == 0 || refs.Exists(r => r < 0 || r >= prior.Count))
        {
            return region;
        }

        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (int r in refs)
        {
            (double qx, double qy, double qw, double qh) = prior[r].Tile;
            if (!(qw > 0) || !(qh > 0))
            {
                continue;
            }

            x0 = Math.Min(x0, qx);
            y0 = Math.Min(y0, qy);
            x1 = Math.Max(x1, qx + qw);
            y1 = Math.Max(y1, qy + qh);
        }

        return x1 > x0 && y1 > y0 ? (x0, y0, x1 - x0, y1 - y0) : region;
    }

    private (double X, double Y, double W, double H, bool Empty) Subregion(SvgElement e, bool primObj, Box? box, (double X, double Y, double W, double H) region, (double X, double Y, double W, double H) def, in SvgStyle st, Deco deco)
    {
        double x = def.X, y = def.Y, w = def.W, h = def.H;
        bool empty = false;
        if (primObj)
        {
            // With object bounding box units the fractions of feFlood, feOffset and feImage measure the object's box (and a subregion that sets
            // any of its four values measures the others as 0, 0, 100%, 100% of it); every other primitive's measure the filter region, which
            // is what the reference renderer does and what leaves a subregion that sets none equal to the region.
            Box b = box!.Value;
            (double ox, double oy, double ow, double oh) = e.Name is "feFlood" or "feOffset" or "feImage" ? (b.X, b.Y, b.W, b.H) : (region.X, region.Y, region.W, region.H);
            if (e.Attr("x") is not null || e.Attr("y") is not null || e.Attr("width") is not null || e.Attr("height") is not null)
            {
                (x, y, w, h) = (ox, oy, ow, oh);
            }

            if (e.Attr("x") is { } ax && Length.TryParse(ax, out Length lx))
            {
                x = ox + (FractionOf(lx) * ow);
            }

            if (e.Attr("y") is { } ay && Length.TryParse(ay, out Length ly))
            {
                y = oy + (FractionOf(ly) * oh);
            }

            if (e.Attr("width") is { } aw && Length.TryParse(aw, out Length lw))
            {
                w = FractionOf(lw) * ow;
            }

            if (e.Attr("height") is { } ah && Length.TryParse(ah, out Length lh))
            {
                h = FractionOf(lh) * oh;
            }
        }
        else
        {
            if (e.Attr("x") is { } ax && Length.TryParse(ax, out Length lx))
            {
                x = Resolve(lx, Axis.X, st, deco.Vp);
            }

            if (e.Attr("y") is { } ay && Length.TryParse(ay, out Length ly))
            {
                y = Resolve(ly, Axis.Y, st, deco.Vp);
            }

            if (e.Attr("width") is { } aw && Length.TryParse(aw, out Length lw))
            {
                w = Resolve(lw, Axis.X, st, deco.Vp);
            }

            if (e.Attr("height") is { } ah && Length.TryParse(ah, out Length lh))
            {
                h = Resolve(lh, Axis.Y, st, deco.Vp);
            }
        }

        if (!(w > 0) || !(h > 0) || !double.IsFinite(x + y + w + h))
        {
            empty = true;
            w = 0;
            h = 0;
        }

        return (x, y, w, h, empty);
    }

    private static double FractionOf(Length l) => l.Unit == LengthUnit.Percent ? l.Value / 100 : l.Value;

    private static FeBlendMode FilterBlendMode(string? v) => v?.Trim() switch
    {
        "multiply" => FeBlendMode.Multiply,
        "screen" => FeBlendMode.Screen,
        "overlay" => FeBlendMode.Overlay,
        "darken" => FeBlendMode.Darken,
        "lighten" => FeBlendMode.Lighten,
        "color-dodge" => FeBlendMode.ColorDodge,
        "color-burn" => FeBlendMode.ColorBurn,
        "hard-light" => FeBlendMode.HardLight,
        "soft-light" => FeBlendMode.SoftLight,
        "difference" => FeBlendMode.Difference,
        "exclusion" => FeBlendMode.Exclusion,
        "hue" => FeBlendMode.Hue,
        "saturation" => FeBlendMode.Saturation,
        "color" => FeBlendMode.Color,
        "luminosity" => FeBlendMode.Luminosity,
        _ => FeBlendMode.Normal,
    };

    private static readonly double[] IdentityMatrix = [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

    private static double[] ColorMatrixOf(SvgElement e)
    {
        string type = e.Attr("type")?.Trim() ?? "matrix";
        double[]? values = Numbers(e.Attr("values"));
        switch (type)
        {
            case "saturate":
                return values is { Length: 1 } ? SaturateMatrix(Math.Clamp(values[0], 0, 1)) : values is null ? IdentityMatrix : SaturateMatrix(1);
            case "hueRotate":
                return values is { Length: 1 } ? HueRotateMatrix(values[0]) : IdentityMatrix;
            case "luminanceToAlpha":
                return [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.2125, 0.7154, 0.0721, 0, 0];
            default:
                // matrix, and any type name that is not one of the others
                return values is { Length: 20 } ? values : IdentityMatrix;
        }
    }

    private static FePrimitive ConvolveOf(SvgElement e, int in1, bool linear, double sx, double sy, double sw, double sh, bool empty)
    {
        FePrimitive Transparent() => new FeMerge { Layers = [], Linear = linear, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };

        int ox = 3, oy = 3;
        if (e.Attr("order") is { } order)
        {
            double[]? n = Numbers(order);
            if (n is null || n.Length is 0 or > 2 || n.Any(v => v != Math.Floor(v) || v < 1))
            {
                return Transparent();
            }

            ox = (int)n[0];
            oy = n.Length == 2 ? (int)n[1] : ox;
        }

        // The kernel may not be larger than anything a document could use; an absurd order is an invalid primitive.
        if ((long)ox * oy > 4096)
        {
            return Transparent();
        }

        double[]? kernel = Numbers(e.Attr("kernelMatrix"));
        if (kernel is null || kernel.Length != ox * oy)
        {
            return Transparent();
        }

        double divisor = Number(e.Attr("divisor"), 0);
        if (e.Attr("divisor") is not null && divisor == 0)
        {
            return Transparent();
        }

        if (divisor == 0)
        {
            // A sum that is zero up to rounding (0.1 eight times minus 0.8) counts as zero.
            divisor = kernel.Sum();
            if (Math.Abs(divisor) < 1e-9)
            {
                divisor = 1;
            }
        }

        double bias = Number(e.Attr("bias"), 0);
        int tx = ox / 2;
        int ty = oy / 2;
        if (e.Attr("targetX") is { } tsx)
        {
            double t = Number(tsx, -1);
            if (t != Math.Floor(t) || t < 0 || t >= ox)
            {
                return Transparent();
            }

            tx = (int)t;
        }

        if (e.Attr("targetY") is { } tsy)
        {
            double t = Number(tsy, -1);
            if (t != Math.Floor(t) || t < 0 || t >= oy)
            {
                return Transparent();
            }

            ty = (int)t;
        }

        int edge = e.Attr("edgeMode")?.Trim() switch { "wrap" => 1, "none" => 2, _ => 0 };
        return new FeConvolve
        {
            In1 = in1,
            Linear = linear,
            OrderX = ox,
            OrderY = oy,
            Kernel = kernel,
            Divisor = divisor,
            Bias = bias,
            TargetX = tx,
            TargetY = ty,
            Edge = edge,
            PreserveAlpha = e.Attr("preserveAlpha")?.Trim() == "true",
            SubX = sx,
            SubY = sy,
            SubW = sw,
            SubH = sh,
            EmptySub = empty,
        };
    }

    // ---- feImage ----

    private FePrimitive ImageOf(SvgElement e, bool linear, double sx, double sy, double sw, double sh, bool empty, (double X, double Y, double W, double H) region, Deco deco, Box? box)
    {
        FePrimitive Nothing() => new FeImage { Linear = linear, SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = true };

        string? href = e.Attr("href")?.Trim();
        if (string.IsNullOrEmpty(href))
        {
            return Nothing();
        }

        if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (!DataUri.TryDecode(href, _limits.MaxImageBytes, out byte[] bytes))
            {
                return Nothing();
            }

            try
            {
                Image picture = Image.Load(bytes, new LoadOptions { Configuration = RasterConfig(), AutoOrient = true });
                return new FeImage { Linear = linear, Picture = picture, Aspect = AspectRatio.Parse(e.Attr("preserveAspectRatio")), SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
            }
            catch (ImageFormatException)
            {
                return Nothing();
            }
        }

        if (href[0] != '#' || !_doc.Ids.TryGetValue(href[1..], out SvgElement? target) || IsAncestorOrSelf(target, e) || target.Name == "filter")
        {
            return Nothing();
        }

        // An element reference is drawn like a use of it, in the user space of the filtered element with its origin at the subregion's.
        Affine m = deco.Ctm.Multiply(Affine.Translate(sx, sy));

        var nodes = new List<RNode>();
        var stack = new Stack<Frame>();
        stack.Push(new Frame { Kids = [target], Style = DefinedStyle(e), Ctm = m, Vp = deco.Vp, Out = nodes, LayerDepth = 0 });
        Run(stack);
        _ = region;
        _ = box;
        return new FeImage { Linear = linear, Nodes = [.. nodes], SubX = sx, SubY = sy, SubW = sw, SubH = sh, EmptySub = empty };
    }

    private static ImageConfiguration RasterConfig()
    {
        // The raster codecs only: a picture inside a filter never nests another SVG document.
        ImageConfiguration? c = s_rasterConfig;
        if (c is null)
        {
            c = new ImageConfiguration();
            foreach (IImageFormat f in ImageConfiguration.Default.Formats)
            {
                c.Add(f);
            }

            s_rasterConfig = c;
        }

        return c;
    }
}
