using OneImage.Svg.Xml;

namespace OneImage.Svg.Dom;

/// <summary>
/// An element of the SVG namespace. <see cref="Attrs"/> holds the plain attributes (geometry, references, <c>transform</c> ...);
/// <see cref="Props"/> holds the declared CSS properties after the cascade: presentation attributes first, then the declarations
/// of the <c>style</c> attribute on top. Style sheets, when they are supported, add their declarations between the two.
/// </summary>
internal sealed class SvgElement
{
    public SvgElement(string name, SvgElement? parent)
    {
        Name = name;
        Parent = parent;
    }

    public string Name { get; }

    public SvgElement? Parent { get; }

    public string? Id { get; set; }

    /// <summary>Gets the position among the parent's SVG children.</summary>
    public int Index { get; set; }

    /// <summary>Gets the whitespace-separated tokens of the <c>class</c> attribute, or <see langword="null"/> when there is none.</summary>
    public string[]? Classes { get; set; }

    /// <summary>Gets the parsed element this one was made from; kept only while style sheets are matched, for attribute selectors.</summary>
    public Xml.XmlElement? Source { get; set; }

    /// <summary>Gets the raw <c>style</c> attribute until the cascade has applied it.</summary>
    public string? StyleAttribute { get; set; }

    /// <summary>Gets the position among siblings of the same name (from 1), valid after <see cref="EnsureTypeIndex"/> on the parent.</summary>
    public int TypeIndex { get; set; }

    /// <summary>Gets how many siblings share this element's name, itself included, valid after <see cref="EnsureTypeIndex"/> on the parent.</summary>
    public int TypeCount { get; set; }

    private bool _typeIndexed;

    public SvgElement? PreviousSibling => Parent is null || Index == 0 ? null : Parent.Children[Index - 1];

    /// <summary>Numbers the children of this element within their type, once, for the <c>*-of-type</c> selectors.</summary>
    public void EnsureTypeIndex()
    {
        if (_typeIndexed)
        {
            return;
        }

        _typeIndexed = true;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SvgElement c in Children)
        {
            counts.TryGetValue(c.Name, out int k);
            counts[c.Name] = ++k;
            c.TypeIndex = k;
        }

        foreach (SvgElement c in Children)
        {
            c.TypeCount = counts[c.Name];
        }
    }

    public Dictionary<string, string> Attrs { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Props { get; } = new(StringComparer.Ordinal);

    public List<SvgElement> Children { get; } = [];

    /// <summary>Gets the character data and child elements of a text content element (<c>text</c>, <c>tspan</c>, <c>textPath</c>, <c>tref</c>, <c>a</c>) in document order; <see langword="null"/> for every other element.</summary>
    public List<TextNode>? TextContent { get; set; }

    public string? Attr(string name) => Attrs.TryGetValue(name, out string? v) ? v : null;

    public string? Prop(string name) => Props.TryGetValue(name, out string? v) ? v : null;
}

/// <summary>One item of a text content element: a run of character data, or a child element.</summary>
internal readonly record struct TextNode(string? Text, SvgElement? Element);

/// <summary>The typed document: SVG-namespace elements with their attributes and declared properties, and an id index.</summary>
internal sealed class SvgDocument
{
    private const string XlinkNamespace = "http://www.w3.org/1999/xlink";

    private static readonly HashSet<string> PresentationAttributes = new(StringComparer.Ordinal)
    {
        "alignment-baseline", "baseline-shift", "clip", "clip-path", "clip-rule", "color", "color-interpolation", "color-interpolation-filters",
        "color-rendering", "cursor", "direction", "display", "dominant-baseline", "fill", "fill-opacity", "fill-rule", "filter", "flood-color",
        "flood-opacity", "font-family", "font-size", "font-size-adjust", "font-stretch", "font-style", "font-variant", "font-weight",
        "glyph-orientation-horizontal", "glyph-orientation-vertical", "image-rendering", "letter-spacing", "lighting-color",
        "marker-end", "marker-mid", "marker-start", "mask", "mask-type", "opacity", "overflow", "paint-order", "pointer-events",
        "shape-rendering", "stop-color", "stop-opacity", "stroke", "stroke-dasharray", "stroke-dashoffset", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "stroke-opacity", "stroke-width", "text-anchor", "text-decoration", "text-rendering", "transform-box",
        "transform-origin", "unicode-bidi", "vector-effect", "visibility", "word-spacing", "writing-mode", "font", "kerning", "font-variant-caps", "white-space",
    };

    private static bool HasCharacterData(XmlElement x)
    {
        foreach (XmlNode node in x.Children)
        {
            if (node is XmlText t && !string.IsNullOrWhiteSpace(t.Value))
            {
                return true;
            }
        }

        return false;
    }

    private SvgDocument(SvgElement root, Dictionary<string, SvgElement> ids, int count)
    {
        Root = root;
        Ids = ids;
        ElementCount = count;
    }

    public SvgElement Root { get; }

    public Dictionary<string, SvgElement> Ids { get; }

    public int ElementCount { get; }

    /// <summary>
    /// Builds the typed document from the parsed XML tree (iteratively; nesting depth was bounded by the parser) and runs the cascade:
    /// for every element the declared properties are the presentation attributes, then the style sheet rules by specificity and source
    /// order, then the <c>style</c> attribute, with <c>!important</c> declarations applied after all of those in the same order.
    /// </summary>
    /// <param name="xml">The parsed document.</param>
    /// <param name="limits">The CSS limits; the defaults when <see langword="null"/>.</param>
    /// <exception cref="ImageFormatException">The root element is not an SVG <c>svg</c> element.</exception>
    /// <exception cref="ImageLimitException">The style sheets exceed the text, rule or matching-step limits.</exception>
    public static SvgDocument Build(XmlDocument xml, SvgLimits? limits = null)
    {
        limits ??= SvgLimits.Default;
        XmlElement xr = xml.Root;
        if (xr.LocalName != "svg" || xr.Namespace != SvgSize.SvgNamespace)
        {
            throw new ImageFormatException("The root element is not an SVG element.");
        }

        var ids = new Dictionary<string, SvgElement>(StringComparer.Ordinal);
        var all = new List<SvgElement>();
        bool anySheet = false;
        long cssChars = 0;
        var root = Create(xr, null, ids, ref cssChars);
        all.Add(root);
        var stack = new Stack<(XmlElement Xml, SvgElement Svg)>();
        stack.Push((xr, root));
        while (stack.Count > 0)
        {
            (XmlElement x, SvgElement e) = stack.Pop();
            if (e.Name == "style" && CssParser.IsCssType(x.GetAttribute(null, "type")))
            {
                string text = TextOf(x);
                cssChars += text.Length;
                CheckCssSize(cssChars, limits);
                anySheet |= text.Length > 0;
            }

            if (e.Name is "text" or "tspan" or "textPath" or "tref" or "a")
            {
                e.TextContent = [];
            }
            else if (e.Name is "rect" or "circle" or "ellipse" or "line" or "path" or "polygon" or "polyline" or "g" or "defs" or "symbol" or "title" or "desc" && HasCharacterData(x))
            {
                // A tref may point at any SVG element, so a known one that holds text of its own keeps it.
                e.TextContent = [];
            }

            foreach (XmlNode node in x.Children)
            {
                if (node is XmlElement child && child.Namespace == SvgSize.SvgNamespace)
                {
                    SvgElement c = Create(child, e, ids, ref cssChars);
                    CheckCssSize(cssChars, limits);
                    c.Index = e.Children.Count;
                    e.Children.Add(c);
                    all.Add(c);
                    stack.Push((child, c));
                    e.TextContent?.Add(new TextNode(null, c));
                }
                else if (node is XmlText text && e.TextContent is not null)
                {
                    e.TextContent.Add(new TextNode(text.Value, null));
                }
            }
        }

        // Sheets apply in document order, whatever order the tree walk met the style elements in.
        var rules = new List<CssRule>();
        if (anySheet)
        {
            foreach (string text in OrderedSheets(xr))
            {
                CssParser.Parse(text, rules, limits);
            }
        }

        CssMatcher? matcher = rules.Count > 0 ? new CssMatcher(rules, limits) : null;
        foreach (SvgElement e in all)
        {
            Cascade(e, matcher);
            if (e.Props.ContainsKey("font"))
            {
                TextFont.ExpandShorthand(e);
            }
        }

        // Selectors look at other elements' attributes, so the parsed tree is only let go once every element has been matched.
        foreach (SvgElement e in all)
        {
            e.Source = null;
            e.StyleAttribute = null;
        }

        return new SvgDocument(root, ids, all.Count);
    }

    private static void CheckCssSize(long chars, SvgLimits limits)
    {
        if (chars > limits.MaxCssBytes)
        {
            throw new ImageLimitException($"The style sheets and style attributes are larger than {limits.MaxCssBytes} characters.");
        }
    }

    private static string TextOf(XmlElement x)
    {
        System.Text.StringBuilder? sb = null;
        string? first = null;
        foreach (XmlNode n in x.Children)
        {
            if (n is XmlText t)
            {
                if (first is null)
                {
                    first = t.Value;
                }
                else
                {
                    sb ??= new System.Text.StringBuilder(first);
                    sb.Append(t.Value);
                }
            }
        }

        return sb?.ToString() ?? first ?? string.Empty;
    }

    // The texts of the style elements in document order (the tree was walked depth first from a stack, which visits siblings in reverse).
    private static List<string> OrderedSheets(XmlElement rootXml)
    {
        var stack = new Stack<XmlElement>();
        stack.Push(rootXml);
        var found = new List<string>();
        while (stack.Count > 0)
        {
            XmlElement x = stack.Pop();
            if (x.LocalName == "style" && x.Namespace == SvgSize.SvgNamespace && CssParser.IsCssType(x.GetAttribute(null, "type")))
            {
                string t = TextOf(x);
                if (t.Length > 0)
                {
                    found.Add(t);
                }
            }

            for (int i = x.Children.Count - 1; i >= 0; i--)
            {
                if (x.Children[i] is XmlElement c)
                {
                    stack.Push(c);
                }
            }
        }

        return found;
    }

    private static void Cascade(SvgElement e, CssMatcher? matcher)
    {
        List<(string Name, string Value, bool Important)>? inline = e.StyleAttribute is null ? null : StyleDeclarations.ParseFull(e.StyleAttribute);
        List<CssRule>? hits = matcher?.Match(e);
        bool anyImportant = false;
        if (hits is { Count: > 0 })
        {
            foreach (CssRule r in hits)
            {
                foreach (CssDeclaration d in r.Declarations)
                {
                    if (d.Important)
                    {
                        anyImportant = true;
                    }
                    else
                    {
                        e.Props[d.Name] = d.Value;
                    }
                }
            }
        }

        if (inline is not null)
        {
            foreach ((string name, string value, bool important) in inline)
            {
                if (important)
                {
                    anyImportant = true;
                }
                else
                {
                    e.Props[name] = value;
                }
            }
        }

        if (!anyImportant)
        {
            return;
        }

        if (hits is not null)
        {
            foreach (CssRule r in hits)
            {
                foreach (CssDeclaration d in r.Declarations)
                {
                    if (d.Important)
                    {
                        e.Props[d.Name] = d.Value;
                    }
                }
            }
        }

        if (inline is not null)
        {
            foreach ((string name, string value, bool important) in inline)
            {
                if (important)
                {
                    e.Props[name] = value;
                }
            }
        }
    }

    private static SvgElement Create(XmlElement x, SvgElement? parent, Dictionary<string, SvgElement> ids, ref long cssChars)
    {
        var e = new SvgElement(x.LocalName, parent) { Source = x };
        foreach (XmlAttribute a in x.Attributes)
        {
            if (a.Namespace is null)
            {
                if (a.LocalName == "style")
                {
                    e.StyleAttribute = a.Value;
                    cssChars += a.Value.Length;
                }
                else if (PresentationAttributes.Contains(a.LocalName))
                {
                    e.Props[a.LocalName] = a.Value.Trim();
                }
                else
                {
                    e.Attrs[a.LocalName] = a.Value;
                }
            }
            else if (a.Namespace == XlinkNamespace && a.LocalName == "href")
            {
                // SVG 2: a plain href wins over xlink:href when both are present.
                e.Attrs.TryAdd("href", a.Value);
            }
            else if (a.Namespace == XmlParser.XmlNamespace && a.LocalName is "space" or "lang")
            {
                e.Attrs["xml:" + a.LocalName] = a.Value;
            }
        }

        if (e.Attrs.TryGetValue("id", out string? id) && id.Length > 0)
        {
            e.Id = id;
            ids.TryAdd(id, e);
        }

        if (e.Attrs.TryGetValue("class", out string? cls))
        {
            string[] tokens = cls.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0)
            {
                e.Classes = tokens;
            }
        }

        return e;
    }
}
