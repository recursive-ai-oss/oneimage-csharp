using System.Globalization;

namespace OneImage.Svg.Dom;

/// <summary>How a compound selector relates to the one on its left.</summary>
internal enum CssCombinator : byte
{
    Descendant,
    Child,
    NextSibling,
    LaterSibling,
}

internal enum CssTestKind : byte
{
    Id,
    Class,
    AttrExists,
    AttrEquals,
    AttrIncludes,
    AttrDash,
    AttrPrefix,
    AttrSuffix,
    AttrSubstring,
    FirstChild,
    LastChild,
    OnlyChild,
    FirstOfType,
    LastOfType,
    OnlyOfType,
    NthChild,
    NthLastChild,
    NthOfType,
    NthLastOfType,
    Empty,
    Root,
    Never,
    Not,
    Is,
}

/// <summary>One simple selector inside a compound: an id, class, attribute test or pseudo-class.</summary>
internal sealed class CssTest
{
    public CssTestKind Kind;
    public string Name = string.Empty;
    public string Value = string.Empty;
    public bool IgnoreCase;
    public int A;
    public int B;
    public CssCompound[]? Args;
}

/// <summary>A type (or universal) selector followed by simple selectors, all of which must match one element.</summary>
internal sealed class CssCompound
{
    public string? Type;
    public CssTest[] Tests = [];
}

/// <summary>A complex selector: compounds joined by combinators, with the specificity of CSS Selectors level 3/4.</summary>
internal sealed class CssSelector
{
    public const int MaxCompounds = 32;

    public required CssCompound[] Parts { get; init; }

    /// <summary>Gets the combinator between <c>Parts[i - 1]</c> and <c>Parts[i]</c> (entry 0 is unused).</summary>
    public required CssCombinator[] Rels { get; init; }

    /// <summary>Gets ids * 2^20 + classes, attributes and pseudo-classes * 2^10 + types, each part capped at 1023.</summary>
    public required int Specificity { get; init; }

    public CssCompound Last => Parts[^1];
}

/// <summary>Parses selector text. Anything the engine does not understand makes the whole selector invalid, which drops its rule, as CSS prescribes.</summary>
internal static class CssSelectorParser
{
    /// <summary>Parses a comma separated selector list; returns <see langword="null"/> when any selector in it is invalid.</summary>
    public static List<CssSelector>? ParseList(ReadOnlySpan<char> text)
    {
        var result = new List<CssSelector>();
        int start = 0;
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : ',';
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '\\':
                    i++;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    CssSelector? s = ParseComplex(text[start..i]);
                    if (s is null)
                    {
                        return null;
                    }

                    result.Add(s);
                    start = i + 1;
                    break;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static CssSelector? ParseComplex(ReadOnlySpan<char> text)
    {
        var parts = new List<CssCompound>();
        var rels = new List<CssCombinator> { CssCombinator.Descendant };
        int pos = 0;
        int spec = 0;
        SkipSpace(text, ref pos);
        while (true)
        {
            CssCompound? c = ParseCompound(text, ref pos, out int cs, nested: false);
            if (c is null || parts.Count >= CssSelector.MaxCompounds)
            {
                return null;
            }

            spec = AddSpecificity(spec, cs);
            parts.Add(c);
            int before = pos;
            SkipSpace(text, ref pos);
            if (pos >= text.Length)
            {
                break;
            }

            CssCombinator rel;
            switch (text[pos])
            {
                case '>':
                    rel = CssCombinator.Child;
                    pos++;
                    break;
                case '+':
                    rel = CssCombinator.NextSibling;
                    pos++;
                    break;
                case '~':
                    rel = CssCombinator.LaterSibling;
                    pos++;
                    break;
                default:
                    if (pos == before)
                    {
                        return null;
                    }

                    rel = CssCombinator.Descendant;
                    break;
            }

            SkipSpace(text, ref pos);
            if (pos >= text.Length)
            {
                return null;
            }

            rels.Add(rel);
        }

        return new CssSelector { Parts = [.. parts], Rels = [.. rels], Specificity = spec };
    }

    private static int AddSpecificity(int a, int b)
    {
        int ids = Math.Min(1023, (a >> 20) + (b >> 20));
        int mid = Math.Min(1023, ((a >> 10) & 1023) + ((b >> 10) & 1023));
        int low = Math.Min(1023, (a & 1023) + (b & 1023));
        return (ids << 20) | (mid << 10) | low;
    }

    private static void SkipSpace(ReadOnlySpan<char> t, ref int pos)
    {
        while (pos < t.Length && (t[pos] is ' ' or '\t' or '\n' or '\r' or '\f'))
        {
            pos++;
        }
    }

    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c >= 0x80 || c == '\\';

    private static string? ReadIdent(ReadOnlySpan<char> t, ref int pos)
    {
        int start = pos;
        System.Text.StringBuilder? sb = null;
        while (pos < t.Length && IsIdentChar(t[pos]))
        {
            if (t[pos] == '\\')
            {
                // CSS escape: up to six hex digits (and one white space), or any other single character, stands for that character.
                sb ??= new System.Text.StringBuilder().Append(t[start..pos]);
                pos++;
                if (pos >= t.Length)
                {
                    return null;
                }

                int hexStart = pos;
                while (pos < t.Length && pos - hexStart < 6 && char.IsAsciiHexDigit(t[pos]))
                {
                    pos++;
                }

                if (pos > hexStart)
                {
                    int cp = int.Parse(t[hexStart..pos], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    sb.Append(cp is > 0 and <= 0x10FFFF && !(cp is >= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(cp) : "\uFFFD");
                    if (pos < t.Length && t[pos] is ' ' or '\t' or '\n')
                    {
                        pos++;
                    }
                }
                else
                {
                    sb.Append(t[pos++]);
                }

                continue;
            }

            sb?.Append(t[pos]);
            pos++;
        }

        if (pos == start)
        {
            return null;
        }

        return sb?.ToString() ?? t[start..pos].ToString();
    }

    private static CssCompound? ParseCompound(ReadOnlySpan<char> t, ref int pos, out int spec, bool nested)
    {
        spec = 0;
        string? type = null;
        var tests = new List<CssTest>();
        bool any = false;
        if (pos < t.Length && t[pos] == '*')
        {
            pos++;
            any = true;
        }
        else if (pos < t.Length && IsIdentChar(t[pos]) && t[pos] != '-' || pos < t.Length && t[pos] == '-' && pos + 1 < t.Length && IsIdentChar(t[pos + 1]))
        {
            type = ReadIdent(t, ref pos);
            if (type is null)
            {
                return null;
            }

            spec = AddSpecificity(spec, 1);
            any = true;
        }

        if (pos < t.Length && t[pos] == '|')
        {
            // Namespace prefixes in selectors are not supported.
            return null;
        }

        while (pos < t.Length)
        {
            char c = t[pos];
            if (c == '#')
            {
                pos++;
                string? id = ReadIdent(t, ref pos);
                if (id is null)
                {
                    return null;
                }

                tests.Add(new CssTest { Kind = CssTestKind.Id, Value = id });
                spec = AddSpecificity(spec, 1 << 20);
            }
            else if (c == '.')
            {
                pos++;
                string? cls = ReadIdent(t, ref pos);
                if (cls is null)
                {
                    return null;
                }

                tests.Add(new CssTest { Kind = CssTestKind.Class, Value = cls });
                spec = AddSpecificity(spec, 1 << 10);
            }
            else if (c == '[')
            {
                CssTest? a = ParseAttribute(t, ref pos);
                if (a is null)
                {
                    return null;
                }

                tests.Add(a);
                spec = AddSpecificity(spec, 1 << 10);
            }
            else if (c == ':')
            {
                CssTest? p = ParsePseudo(t, ref pos, out int ps, nested);
                if (p is null)
                {
                    return null;
                }

                tests.Add(p);
                spec = AddSpecificity(spec, ps);
            }
            else
            {
                break;
            }

            any = true;
        }

        return any ? new CssCompound { Type = type, Tests = [.. tests] } : null;
    }

    private static CssTest? ParseAttribute(ReadOnlySpan<char> t, ref int pos)
    {
        pos++; // [
        SkipSpace(t, ref pos);
        string? name = ReadIdent(t, ref pos);
        if (name is null)
        {
            return null;
        }

        SkipSpace(t, ref pos);
        if (pos < t.Length && t[pos] == '|' && !(pos + 1 < t.Length && t[pos + 1] == '='))
        {
            return null;
        }

        if (pos >= t.Length)
        {
            return null;
        }

        if (t[pos] == ']')
        {
            pos++;
            return new CssTest { Kind = CssTestKind.AttrExists, Name = name };
        }

        CssTestKind kind;
        switch (t[pos])
        {
            case '=':
                kind = CssTestKind.AttrEquals;
                pos++;
                break;
            case '~' or '|' or '^' or '$' or '*' when pos + 1 < t.Length && t[pos + 1] == '=':
                kind = t[pos] switch
                {
                    '~' => CssTestKind.AttrIncludes,
                    '|' => CssTestKind.AttrDash,
                    '^' => CssTestKind.AttrPrefix,
                    '$' => CssTestKind.AttrSuffix,
                    _ => CssTestKind.AttrSubstring,
                };
                pos += 2;
                break;
            default:
                return null;
        }

        SkipSpace(t, ref pos);
        string? value;
        if (pos < t.Length && t[pos] is '"' or '\'')
        {
            char q = t[pos++];
            var sb = new System.Text.StringBuilder();
            while (pos < t.Length && t[pos] != q)
            {
                if (t[pos] == '\\' && pos + 1 < t.Length)
                {
                    pos++;
                }

                sb.Append(t[pos++]);
            }

            if (pos >= t.Length)
            {
                return null;
            }

            pos++;
            value = sb.ToString();
        }
        else
        {
            value = ReadIdent(t, ref pos);
        }

        if (value is null)
        {
            return null;
        }

        SkipSpace(t, ref pos);
        bool ic = false;
        if (pos < t.Length && t[pos] is 'i' or 'I')
        {
            ic = true;
            pos++;
            SkipSpace(t, ref pos);
        }

        if (pos >= t.Length || t[pos] != ']')
        {
            return null;
        }

        pos++;
        return new CssTest { Kind = kind, Name = name, Value = value, IgnoreCase = ic };
    }

    private static CssTest? ParsePseudo(ReadOnlySpan<char> t, ref int pos, out int spec, bool nested)
    {
        spec = 1 << 10;
        pos++; // :
        if (pos < t.Length && t[pos] == ':')
        {
            return null; // pseudo-elements never match an element
        }

        string? name = ReadIdent(t, ref pos);
        if (name is null)
        {
            return null;
        }

        name = name.ToLowerInvariant();
        if (pos < t.Length && t[pos] == '(')
        {
            int close = FindClose(t, pos);
            if (close < 0)
            {
                return null;
            }

            ReadOnlySpan<char> arg = t[(pos + 1)..close];
            pos = close + 1;
            switch (name)
            {
                case "not" or "is" or "where" or "matches" or "-webkit-any" or "-moz-any":
                    if (nested)
                    {
                        return null;
                    }

                    CssCompound[]? args = ParseCompoundList(arg, out int max);
                    if (args is null)
                    {
                        return null;
                    }

                    spec = name == "where" ? 0 : max;
                    return new CssTest { Kind = name == "not" ? CssTestKind.Not : CssTestKind.Is, Args = args };
                case "nth-child" or "nth-last-child" or "nth-of-type" or "nth-last-of-type":
                    if (!TryParseNth(arg, out int a, out int b))
                    {
                        return null;
                    }

                    CssTestKind k = name switch
                    {
                        "nth-child" => CssTestKind.NthChild,
                        "nth-last-child" => CssTestKind.NthLastChild,
                        "nth-of-type" => CssTestKind.NthOfType,
                        _ => CssTestKind.NthLastOfType,
                    };
                    return new CssTest { Kind = k, A = a, B = b };
                default:
                    return null;
            }
        }

        switch (name)
        {
            case "first-child": return new CssTest { Kind = CssTestKind.FirstChild };
            case "last-child": return new CssTest { Kind = CssTestKind.LastChild };
            case "only-child": return new CssTest { Kind = CssTestKind.OnlyChild };
            case "first-of-type": return new CssTest { Kind = CssTestKind.FirstOfType };
            case "last-of-type": return new CssTest { Kind = CssTestKind.LastOfType };
            case "only-of-type": return new CssTest { Kind = CssTestKind.OnlyOfType };
            case "empty": return new CssTest { Kind = CssTestKind.Empty };
            case "root": return new CssTest { Kind = CssTestKind.Root };

            // Interaction and document state never apply to a static rendering.
            case "link" or "visited" or "any-link" or "hover" or "active" or "focus" or "focus-visible" or "focus-within" or "target" or "checked" or "disabled" or "enabled" or "scope":
                return new CssTest { Kind = CssTestKind.Never };
            default:
                return null;
        }
    }

    private static int FindClose(ReadOnlySpan<char> t, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < t.Length; i++)
        {
            char c = t[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static CssCompound[]? ParseCompoundList(ReadOnlySpan<char> text, out int maxSpec)
    {
        maxSpec = 0;
        var list = new List<CssCompound>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != ',')
            {
                continue;
            }

            ReadOnlySpan<char> part = text[start..i];
            start = i + 1;
            int pos = 0;
            SkipSpace(part, ref pos);
            CssCompound? c = ParseCompound(part, ref pos, out int spec, nested: true);
            SkipSpace(part, ref pos);
            if (c is null || pos != part.Length)
            {
                return null;
            }

            maxSpec = Math.Max(maxSpec, spec);
            list.Add(c);
        }

        return list.Count == 0 ? null : [.. list];
    }

    /// <summary>Parses the <c>An+B</c> micro-syntax of the nth- pseudo-classes, including <c>odd</c> and <c>even</c>.</summary>
    private static bool TryParseNth(ReadOnlySpan<char> arg, out int a, out int b)
    {
        a = 0;
        b = 0;
        string s = arg.ToString().Trim().ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (s == "odd")
        {
            a = 2;
            b = 1;
            return true;
        }

        if (s == "even")
        {
            a = 2;
            return true;
        }

        int n = s.IndexOf('n', StringComparison.Ordinal);
        if (n < 0)
        {
            return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out b) && Math.Abs(b) < 1_000_000;
        }

        string left = s[..n];
        string right = s[(n + 1)..];
        if (left is "" or "+")
        {
            a = 1;
        }
        else if (left == "-")
        {
            a = -1;
        }
        else if (!int.TryParse(left, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out a))
        {
            return false;
        }

        if (right.Length > 0 && !int.TryParse(right, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out b))
        {
            return false;
        }

        return Math.Abs(a) < 1_000_000 && Math.Abs(b) < 1_000_000;
    }
}
