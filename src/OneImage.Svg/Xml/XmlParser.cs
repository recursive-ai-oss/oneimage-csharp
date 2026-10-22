using System.Globalization;
using System.Text;

namespace OneImage.Svg.Xml;

/// <summary>
/// A small, strict XML 1.0 + Namespaces parser for untrusted documents. It refuses what makes XML dangerous:
/// any <c>&lt;!DOCTYPE</c> (so no DTDs, no external subsets, no entity declarations) and every entity reference except the
/// five predefined ones and numeric character references, so a document can never grow while it is read.
/// Nesting is handled with an explicit stack, and element count, depth, attributes per element, name length and
/// namespace declarations in scope are bounded by <see cref="SvgLimits"/>.
/// </summary>
internal static class XmlParser
{
    /// <summary>The namespace bound to the <c>xml</c> prefix.</summary>
    public const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Parses a whole document.</summary>
    /// <exception cref="ImageFormatException">The data is not well-formed XML, uses a DTD or an entity, or has an unsupported encoding.</exception>
    /// <exception cref="ImageLimitException">A limit in <paramref name="limits"/> was exceeded.</exception>
    public static XmlDocument Parse(ReadOnlySpan<byte> data, SvgLimits limits) => ParseText(DecodeText(data), limits);

    /// <summary>Parses a whole document from text.</summary>
    public static XmlDocument ParseText(string text, SvgLimits limits) => new Parser(text, limits).ParseDocument();

    /// <summary>Parses only up to the end of the root element's start tag and returns that element (without children).</summary>
    public static XmlElement ParseRootStart(ReadOnlySpan<byte> data, SvgLimits limits) => new Parser(DecodeText(data), limits).ParseRootStartOnly();

    /// <summary>
    /// Decodes the document bytes: UTF-8 (with or without BOM), UTF-16 with BOM, and ASCII/ISO-8859-1 when the XML declaration says so.
    /// Any other declared encoding is refused rather than guessed.
    /// </summary>
    internal static string DecodeText(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                return StrictUtf8.GetString(data[3..]);
            }

            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            {
                return new UnicodeEncoding(false, false, true).GetString(data[2..]);
            }

            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            {
                return new UnicodeEncoding(true, false, true).GetString(data[2..]);
            }

            string? declared = DeclaredEncoding(data);
            if (declared is not null)
            {
                switch (declared.ToLowerInvariant())
                {
                    case "utf-8" or "utf8" or "us-ascii" or "ascii":
                        break;
                    case "iso-8859-1" or "latin1" or "latin-1" or "iso_8859-1":
                        return Encoding.Latin1.GetString(data);
                    default:
                        throw new ImageFormatException($"The XML encoding '{declared}' is not supported.");
                }
            }

            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new ImageFormatException("The document is not valid in its text encoding.");
        }
    }

    private static string? DeclaredEncoding(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> head = data[..Math.Min(data.Length, 200)];
        if (!head.StartsWith("<?xml"u8) || head.Length < 6 || !IsWs((char)head[5]))
        {
            return null;
        }

        int end = head.IndexOf("?>"u8);
        if (end < 0)
        {
            return null;
        }

        Span<char> chars = stackalloc char[end];
        for (int i = 0; i < end; i++)
        {
            chars[i] = (char)(head[i] & 0x7F);
        }

        int k = chars.IndexOf("encoding", StringComparison.Ordinal);
        if (k < 0)
        {
            return null;
        }

        k += 8;
        while (k < chars.Length && IsWs(chars[k]))
        {
            k++;
        }

        if (k >= chars.Length || chars[k] != '=')
        {
            return null;
        }

        k++;
        while (k < chars.Length && IsWs(chars[k]))
        {
            k++;
        }

        if (k >= chars.Length || chars[k] is not ('"' or '\''))
        {
            return null;
        }

        char quote = chars[k++];
        int close = chars[k..].IndexOf(quote);
        return close < 0 ? null : new string(chars.Slice(k, close));
    }

    internal static bool IsWs(char c) => c is ' ' or '\t' or '\n' or '\r';

    private static bool IsNameStart(char c) =>
        c < 0x80 ? char.IsAsciiLetter(c) || c is '_' or ':' : char.IsLetter(c) || char.IsSurrogate(c);

    private static bool IsNameChar(char c)
    {
        if (c < 0x80)
        {
            return char.IsAsciiLetterOrDigit(c) || c is '_' or ':' or '-' or '.';
        }

        if (char.IsLetter(c) || char.IsSurrogate(c) || c == '\u00B7')
        {
            return true;
        }

        UnicodeCategory cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.DecimalDigitNumber;
    }

    private sealed class Parser
    {
        private const int InternLimit = 4096;

        private readonly string _s;
        private readonly SvgLimits _limits;
        private readonly Dictionary<string, string> _intern = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _internLookup;
        private readonly List<(string Prefix, string Uri)> _bindings = [];
        private readonly List<(string Name, string Value)> _raw = [];
        private readonly HashSet<(string?, string)> _seen = [];
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _attr = new();
        private int _p;
        private int _elements;

        public Parser(string s, SvgLimits limits)
        {
            _s = s;
            _limits = limits;
            _internLookup = _intern.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        private readonly record struct Frame(XmlElement Element, string QName, int BindingMark);

        public XmlDocument ParseDocument()
        {
            SkipProlog();
            XmlElement root = ParseStartTag(out bool selfClosing, out string qname, out int mark);
            if (!selfClosing)
            {
                ParseContent(root, qname, mark);
            }

            SkipMisc();
            if (_p < _s.Length)
            {
                throw Bad("Content after the root element.");
            }

            return new XmlDocument(root, _elements);
        }

        public XmlElement ParseRootStartOnly()
        {
            SkipProlog();
            return ParseStartTag(out _, out _, out _);
        }

        private static ImageFormatException Bad(string message) => new("Malformed XML: " + message);

        private void SkipProlog()
        {
            if (_p < _s.Length && _s[_p] == '\uFEFF')
            {
                _p++;
            }

            if (At("<?xml") && _p + 5 < _s.Length && IsWs(_s[_p + 5]))
            {
                SkipPi();
            }

            SkipMisc();
            if (_p >= _s.Length || _s[_p] != '<')
            {
                throw Bad("No root element.");
            }
        }

        private bool At(string lit) => string.CompareOrdinal(_s, _p, lit, 0, lit.Length) == 0;

        // Whitespace, comments and processing instructions between top-level items. Anything starting with "<!" that is not
        // a comment is a DOCTYPE (or garbage) and is refused.
        private void SkipMisc()
        {
            while (true)
            {
                while (_p < _s.Length && IsWs(_s[_p]))
                {
                    _p++;
                }

                if (At("<?"))
                {
                    SkipPi();
                }
                else if (At("<!--"))
                {
                    SkipComment();
                }
                else if (At("<!"))
                {
                    throw new ImageFormatException("DOCTYPE declarations are not allowed.");
                }
                else
                {
                    return;
                }
            }
        }

        private void SkipPi()
        {
            int end = _s.IndexOf("?>", _p + 2, StringComparison.Ordinal);
            _p = end < 0 ? throw Bad("Unterminated processing instruction.") : end + 2;
        }

        private void SkipComment()
        {
            int end = _s.IndexOf("-->", _p + 4, StringComparison.Ordinal);
            _p = end < 0 ? throw Bad("Unterminated comment.") : end + 3;
        }

        private void ParseContent(XmlElement root, string rootQName, int rootMark)
        {
            List<Frame> stack = new(16) { new Frame(root, rootQName, rootMark) };
            while (stack.Count > 0)
            {
                if (_p >= _s.Length)
                {
                    throw Bad("Unexpected end of document inside an element.");
                }

                if (_s[_p] != '<')
                {
                    ReadCharData();
                    continue;
                }

                if (At("</"))
                {
                    Flush(stack[^1].Element);
                    ReadEndTag(stack[^1]);
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (At("<!--"))
                {
                    SkipComment();
                }
                else if (At("<![CDATA["))
                {
                    int end = _s.IndexOf("]]>", _p + 9, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw Bad("Unterminated CDATA section.");
                    }

                    AppendRaw(_p + 9, end);
                    _p = end + 3;
                }
                else if (At("<?"))
                {
                    SkipPi();
                }
                else if (At("<!"))
                {
                    throw new ImageFormatException("DOCTYPE and other declarations are not allowed.");
                }
                else
                {
                    XmlElement parent = stack[^1].Element;
                    Flush(parent);
                    if (stack.Count >= _limits.MaxDepth)
                    {
                        throw new ImageLimitException($"The document nests elements deeper than {_limits.MaxDepth}.");
                    }

                    XmlElement el = ParseStartTag(out bool selfClosing, out string qname, out int mark);
                    el.Parent = parent;
                    parent.Children.Add(el);
                    if (!selfClosing)
                    {
                        stack.Add(new Frame(el, qname, mark));
                    }
                }
            }
        }

        private void Flush(XmlElement into)
        {
            if (_text.Length > 0)
            {
                into.Children.Add(new XmlText(_text.ToString()) { Parent = into });
                _text.Clear();
            }
        }

        private void ReadEndTag(Frame top)
        {
            _p += 2;
            ReadOnlySpan<char> name = ReadName();
            if (!name.SequenceEqual(top.QName))
            {
                throw Bad($"End tag does not match the start tag <{top.QName}>.");
            }

            SkipWs();
            Expect('>');
            _bindings.RemoveRange(top.BindingMark, _bindings.Count - top.BindingMark);
        }

        private void SkipWs()
        {
            while (_p < _s.Length && IsWs(_s[_p]))
            {
                _p++;
            }
        }

        private void Expect(char c)
        {
            if (_p >= _s.Length || _s[_p] != c)
            {
                throw Bad($"Expected '{c}'.");
            }

            _p++;
        }

        private ReadOnlySpan<char> ReadName()
        {
            int start = _p;
            if (_p >= _s.Length || !IsNameStart(_s[_p]))
            {
                throw Bad("Invalid name.");
            }

            _p++;
            while (_p < _s.Length && IsNameChar(_s[_p]))
            {
                _p++;
            }

            if (_p - start > _limits.MaxNameLength)
            {
                throw new ImageLimitException($"A name is longer than {_limits.MaxNameLength} characters.");
            }

            return _s.AsSpan(start, _p - start);
        }

        private string Intern(ReadOnlySpan<char> value)
        {
            if (_internLookup.TryGetValue(value, out string? existing))
            {
                return existing;
            }

            string made = new(value);
            if (_intern.Count < InternLimit && value.Length <= 128)
            {
                _intern[made] = made;
            }

            return made;
        }

        private XmlElement ParseStartTag(out bool selfClosing, out string qname, out int bindingMark)
        {
            if (++_elements > _limits.MaxElements)
            {
                throw new ImageLimitException($"The document has more than {_limits.MaxElements} elements.");
            }

            _p++; // '<'
            qname = Intern(ReadName());
            _raw.Clear();
            while (true)
            {
                int before = _p;
                SkipWs();
                if (_p >= _s.Length)
                {
                    throw Bad("Unexpected end of document inside a tag.");
                }

                char c = _s[_p];
                if (c == '>')
                {
                    _p++;
                    selfClosing = false;
                    break;
                }

                if (c == '/')
                {
                    _p++;
                    Expect('>');
                    selfClosing = true;
                    break;
                }

                if (_p == before)
                {
                    throw Bad("Attributes must be separated by white space.");
                }

                if (_raw.Count >= _limits.MaxAttributesPerElement)
                {
                    throw new ImageLimitException($"An element has more than {_limits.MaxAttributesPerElement} attributes.");
                }

                string name = Intern(ReadName());
                SkipWs();
                Expect('=');
                SkipWs();
                if (_p >= _s.Length || _s[_p] is not ('"' or '\''))
                {
                    throw Bad("Attribute values must be quoted.");
                }

                _raw.Add((name, ReadAttributeValue(_s[_p++])));
            }

            bindingMark = _bindings.Count;
            foreach ((string aname, string avalue) in _raw)
            {
                if (aname == "xmlns")
                {
                    Bind(string.Empty, avalue);
                }
                else if (aname.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    string prefix = aname[6..];
                    if (prefix is "xmlns" || (prefix == "xml" && avalue != XmlNamespace) || avalue.Length == 0)
                    {
                        throw Bad("Invalid namespace declaration.");
                    }

                    Bind(Intern(prefix), avalue);
                }
            }

            SplitName(qname, out string prefixPart, out string local);
            string? ns = prefixPart.Length == 0 ? NullIfEmpty(Lookup(string.Empty)) : Lookup(prefixPart) ?? throw Bad($"Unbound namespace prefix '{prefixPart}'.");

            var attrs = new XmlAttribute[_raw.Count];
            int count = 0;
            _seen.Clear();
            foreach ((string aname, string avalue) in _raw)
            {
                if (aname == "xmlns" || aname.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    continue;
                }

                SplitName(aname, out string ap, out string al);
                string? ans = ap.Length == 0 ? null : Lookup(ap) ?? throw Bad($"Unbound namespace prefix '{ap}'.");
                // Few attributes: compare directly. From the ninth on, switch to a hash set seeded with the first eight.
                bool duplicate;
                if (count < 8)
                {
                    duplicate = DuplicateOf(attrs, count, ans, al);
                }
                else
                {
                    if (count == 8)
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            _seen.Add((attrs[i].Namespace, attrs[i].LocalName));
                        }
                    }

                    duplicate = !_seen.Add((ans, al));
                }

                if (duplicate)
                {
                    throw Bad($"Duplicate attribute '{aname}'.");
                }

                attrs[count++] = new XmlAttribute(ans, al, avalue);
            }

            var el = new XmlElement(ns, local, count == 0 ? [] : count == attrs.Length ? attrs : attrs[..count]);
            if (selfClosing)
            {
                _bindings.RemoveRange(bindingMark, _bindings.Count - bindingMark);
            }

            return el;
        }

        private static bool DuplicateOf(XmlAttribute[] attrs, int count, string? ns, string local)
        {
            for (int i = 0; i < count; i++)
            {
                if (attrs[i].LocalName == local && attrs[i].Namespace == ns)
                {
                    return true;
                }
            }

            return false;
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

        private static void SplitName(string qname, out string prefix, out string local)
        {
            int colon = qname.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                prefix = string.Empty;
                local = qname;
                return;
            }

            if (colon == 0 || colon == qname.Length - 1 || qname.IndexOf(':', colon + 1) >= 0)
            {
                throw Bad($"Invalid qualified name '{qname}'.");
            }

            prefix = qname[..colon];
            local = qname[(colon + 1)..];
        }

        private void Bind(string prefix, string uri)
        {
            if (_bindings.Count >= _limits.MaxNamespaceBindings)
            {
                throw new ImageLimitException($"More than {_limits.MaxNamespaceBindings} namespace declarations are in scope.");
            }

            _bindings.Add((prefix, uri));
        }

        private string? Lookup(string prefix)
        {
            if (prefix == "xml")
            {
                return XmlNamespace;
            }

            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                if (_bindings[i].Prefix == prefix)
                {
                    return _bindings[i].Uri;
                }
            }

            return null;
        }

        private string ReadAttributeValue(char quote)
        {
            // Fast path: no references, no white space to normalize, no illegal characters before the closing quote.
            int start = _p;
            int q = _p;
            while (q < _s.Length)
            {
                char ch = _s[q];
                if (ch == quote)
                {
                    _p = q + 1;
                    return _s.Substring(start, q - start);
                }

                if (ch is '<' or '&' or '\r' or '\t' or '\n' || ch < 0x20 || ch is '\uFFFE' or '\uFFFF')
                {
                    break;
                }

                q++;
            }

            StringBuilder sb = _attr;
            sb.Clear();
            while (true)
            {
                if (_p >= _s.Length)
                {
                    throw Bad("Unterminated attribute value.");
                }

                char c = _s[_p];
                if (c == quote)
                {
                    _p++;
                    return sb.ToString();
                }

                switch (c)
                {
                    case '<':
                        throw Bad("'<' in an attribute value.");
                    case '&':
                        ReadReference(sb);
                        break;
                    case '\r':
                        sb.Append(' ');
                        _p++;
                        if (_p < _s.Length && _s[_p] == '\n')
                        {
                            _p++;
                        }

                        break;
                    case '\t' or '\n':
                        sb.Append(' ');
                        _p++;
                        break;
                    default:
                        CheckChar(c);
                        sb.Append(c);
                        _p++;
                        break;
                }
            }
        }

        private static void CheckChar(char c)
        {
            if ((c < 0x20 && c is not ('\t' or '\n' or '\r')) || c is '\uFFFE' or '\uFFFF')
            {
                throw Bad($"Character U+{(int)c:X4} is not allowed in XML.");
            }
        }

        // Character data up to the next '<'. References are expanded, \r\n and \r become \n.
        private void ReadCharData()
        {
            while (_p < _s.Length && _s[_p] != '<')
            {
                if (_s[_p] == '&')
                {
                    ReadReference(_text);
                    continue;
                }

                int end = _s.AsSpan(_p).IndexOfAny('<', '&');
                end = end < 0 ? _s.Length : _p + end;
                AppendRaw(_p, end);
                _p = end;
            }
        }

        private void AppendRaw(int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                char c = _s[i];
                if (c == '\r')
                {
                    _text.Append('\n');
                    if (i + 1 < end && _s[i + 1] == '\n')
                    {
                        i++;
                    }
                }
                else
                {
                    CheckChar(c);
                    _text.Append(c);
                }
            }
        }

        // Only the five predefined entities and numeric references. Everything else is refused: there are no declarations to
        // resolve a name against, and honoring none keeps expansion strictly linear.
        private void ReadReference(StringBuilder into)
        {
            int semi = _s.IndexOf(';', _p + 1, Math.Min(12, _s.Length - _p - 1));
            if (semi < 0)
            {
                throw Bad("Unterminated reference.");
            }

            ReadOnlySpan<char> name = _s.AsSpan(_p + 1, semi - _p - 1);
            if (name.Length > 1 && name[0] == '#')
            {
                bool hex = name[1] is 'x';
                ReadOnlySpan<char> digits = name[(hex ? 2 : 1)..];
                if (digits.Length is 0 or > 8 || !int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int cp)
                    || cp is > 0x10FFFF or (>= 0xD800 and <= 0xDFFF) or 0xFFFE or 0xFFFF || (cp < 0x20 && cp is not (9 or 10 or 13)) || cp == 0)
                {
                    throw Bad("Invalid character reference.");
                }

                into.Append(char.ConvertFromUtf32(cp));
            }
            else
            {
                switch (name)
                {
                    case "lt": into.Append('<'); break;
                    case "gt": into.Append('>'); break;
                    case "amp": into.Append('&'); break;
                    case "quot": into.Append('"'); break;
                    case "apos": into.Append('\''); break;
                    default:
                        throw new ImageFormatException("Entity references other than the five predefined ones are not supported.");
                }
            }

            _p = semi + 1;
        }
    }
}
