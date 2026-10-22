namespace OneImage.Svg.Xml;

/// <summary>A node of the parsed document: an element or character data.</summary>
internal abstract class XmlNode
{
    /// <summary>Gets or sets the containing element; <see langword="null"/> for the root.</summary>
    public XmlElement? Parent { get; set; }
}

/// <summary>Character data with references expanded and line ends normalized to <c>\n</c>.</summary>
internal sealed class XmlText : XmlNode
{
    public XmlText(string value) => Value = value;

    public string Value { get; }
}

/// <summary>An attribute with its namespace resolved. Unprefixed attributes have no namespace.</summary>
internal readonly record struct XmlAttribute(string? Namespace, string LocalName, string Value);

/// <summary>An element with its namespace resolved. Namespace declarations are consumed by the parser and are not attributes.</summary>
internal sealed class XmlElement : XmlNode
{
    public XmlElement(string? ns, string localName, XmlAttribute[] attributes)
    {
        Namespace = ns;
        LocalName = localName;
        Attributes = attributes;
    }

    public string? Namespace { get; }

    public string LocalName { get; }

    public XmlAttribute[] Attributes { get; }

    public List<XmlNode> Children { get; } = [];

    /// <summary>Returns the value of the attribute with this namespace and local name, or <see langword="null"/>.</summary>
    public string? GetAttribute(string? ns, string localName)
    {
        foreach (XmlAttribute a in Attributes)
        {
            if (a.LocalName == localName && a.Namespace == ns)
            {
                return a.Value;
            }
        }

        return null;
    }
}

/// <summary>A parsed document.</summary>
internal sealed class XmlDocument
{
    public XmlDocument(XmlElement root, int elementCount)
    {
        Root = root;
        ElementCount = elementCount;
    }

    public XmlElement Root { get; }

    public int ElementCount { get; }
}
