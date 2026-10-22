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

    public Dictionary<string, string> Attrs { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Props { get; } = new(StringComparer.Ordinal);

    public List<SvgElement> Children { get; } = [];

    public string? Attr(string name) => Attrs.TryGetValue(name, out string? v) ? v : null;

    public string? Prop(string name) => Props.TryGetValue(name, out string? v) ? v : null;
}

/// <summary>The typed document: SVG-namespace elements with their attributes and declared properties, and an id index.</summary>
internal sealed class SvgDocument
{
    private const string XlinkNamespace = "http://www.w3.org/1999/xlink";

    private static readonly HashSet<string> PresentationAttributes = new(StringComparer.Ordinal)
    {
        "alignment-baseline", "baseline-shift", "clip", "clip-path", "clip-rule", "color", "color-interpolation", "color-interpolation-filters",
        "color-rendering", "cursor", "direction", "display", "dominant-baseline", "fill", "fill-opacity", "fill-rule", "filter", "flood-color",
        "flood-opacity", "font-family", "font-size", "font-size-adjust", "font-stretch", "font-style", "font-variant", "font-weight",
        "glyph-orientation-horizontal", "glyph-orientation-vertical", "image-rendering", "isolation", "letter-spacing", "lighting-color",
        "marker-end", "marker-mid", "marker-start", "mask", "mask-type", "mix-blend-mode", "opacity", "overflow", "paint-order", "pointer-events",
        "shape-rendering", "stop-color", "stop-opacity", "stroke", "stroke-dasharray", "stroke-dashoffset", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "stroke-opacity", "stroke-width", "text-anchor", "text-decoration", "text-rendering", "transform-box",
        "transform-origin", "unicode-bidi", "vector-effect", "visibility", "word-spacing", "writing-mode",
    };

    private SvgDocument(SvgElement root, Dictionary<string, SvgElement> ids, int count)
    {
        Root = root;
        Ids = ids;
        ElementCount = count;
    }

    public SvgElement Root { get; }

    public Dictionary<string, SvgElement> Ids { get; }

    public int ElementCount { get; }

    /// <summary>Builds the typed document from the parsed XML tree (iteratively; nesting depth was bounded by the parser).</summary>
    /// <exception cref="ImageFormatException">The root element is not an SVG <c>svg</c> element.</exception>
    public static SvgDocument Build(XmlDocument xml)
    {
        XmlElement xr = xml.Root;
        if (xr.LocalName != "svg" || xr.Namespace != SvgSize.SvgNamespace)
        {
            throw new ImageFormatException("The root element is not an SVG element.");
        }

        var ids = new Dictionary<string, SvgElement>(StringComparer.Ordinal);
        var root = Create(xr, null, ids);
        int count = 1;
        var stack = new Stack<(XmlElement Xml, SvgElement Svg)>();
        stack.Push((xr, root));
        while (stack.Count > 0)
        {
            (XmlElement x, SvgElement e) = stack.Pop();
            foreach (XmlNode node in x.Children)
            {
                if (node is XmlElement child && child.Namespace == SvgSize.SvgNamespace)
                {
                    SvgElement c = Create(child, e, ids);
                    e.Children.Add(c);
                    count++;
                    stack.Push((child, c));
                }
            }
        }

        return new SvgDocument(root, ids, count);
    }

    private static SvgElement Create(XmlElement x, SvgElement? parent, Dictionary<string, SvgElement> ids)
    {
        var e = new SvgElement(x.LocalName, parent);
        string? style = null;
        foreach (XmlAttribute a in x.Attributes)
        {
            if (a.Namespace is null)
            {
                if (a.LocalName == "style")
                {
                    style = a.Value;
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
            else if (a.Namespace == XmlParser.XmlNamespace && a.LocalName == "space")
            {
                e.Attrs["xml:space"] = a.Value;
            }
        }

        if (style is not null)
        {
            foreach ((string name, string value) in StyleDeclarations.Parse(style))
            {
                e.Props[name] = value;
            }
        }

        if (e.Attrs.TryGetValue("id", out string? id) && id.Length > 0)
        {
            e.Id = id;
            ids.TryAdd(id, e);
        }

        return e;
    }
}
