namespace OneImage.Svg.Dom;

/// <summary>A declaration of a style rule.</summary>
internal readonly record struct CssDeclaration(string Name, string Value, bool Important);

/// <summary>One selector with the declarations of its rule. A rule with a selector list becomes one entry per selector, all sharing the declarations.</summary>
internal sealed class CssRule
{
    public required CssSelector Selector { get; init; }

    public required CssDeclaration[] Declarations { get; init; }

    /// <summary>Gets the position among all rules, in document order across all style sheets.</summary>
    public required int Order { get; init; }
}

/// <summary>
/// Parses style sheet text into rules. The supported subset is the one SVG documents use: style rules with selectors, declarations and
/// <c>!important</c>. At-rules (<c>@import</c>, <c>@media</c>, <c>@font-face</c> ...) are skipped without being read, so nothing is
/// ever fetched; a rule with a selector this engine does not understand is dropped, as CSS says. Parsing is iterative and bounded by
/// <see cref="SvgLimits.MaxCssRules"/>.
/// </summary>
internal static class CssParser
{
    /// <summary>Appends the rules of <paramref name="text"/> to <paramref name="rules"/>.</summary>
    /// <exception cref="ImageLimitException">The sheets define more rules than <see cref="SvgLimits.MaxCssRules"/>.</exception>
    public static void Parse(string text, List<CssRule> rules, SvgLimits limits)
    {
        if (text.Contains("/*", StringComparison.Ordinal))
        {
            text = StyleDeclarations.StripComments(text);
        }

        int n = text.Length;
        int i = 0;
        while (i < n)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == '}')
            {
                i++;
                continue;
            }

            if (c == '<' && string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                i += 4;
                continue;
            }

            if (c == '-' && string.CompareOrdinal(text, i, "-->", 0, 3) == 0)
            {
                i += 3;
                continue;
            }

            int preludeStart = i;
            int brace = FindBlockStart(text, ref i, out bool semicolon);
            if (brace < 0)
            {
                // A statement at-rule such as @import ends at its semicolon; anything else unterminated ends the sheet.
                if (semicolon)
                {
                    continue;
                }

                break;
            }

            int bodyStart = brace + 1;
            int bodyEnd = FindBlockEnd(text, bodyStart);
            string body = text.Substring(bodyStart, bodyEnd - bodyStart);
            i = Math.Min(bodyEnd + 1, n);
            if (text[preludeStart] == '@')
            {
                continue;
            }

            List<CssSelector>? selectors = CssSelectorParser.ParseList(text.AsSpan(preludeStart, brace - preludeStart));
            if (selectors is null)
            {
                continue;
            }

            List<(string Name, string Value, bool Important)> decls = StyleDeclarations.ParseFull(body);
            if (decls.Count == 0)
            {
                continue;
            }

            var array = new CssDeclaration[decls.Count];
            for (int d = 0; d < array.Length; d++)
            {
                array[d] = new CssDeclaration(decls[d].Name, decls[d].Value, decls[d].Important);
            }

            foreach (CssSelector s in selectors)
            {
                if (rules.Count >= limits.MaxCssRules)
                {
                    throw new ImageLimitException($"The style sheets define more than {limits.MaxCssRules} rules.");
                }

                rules.Add(new CssRule { Selector = s, Declarations = array, Order = rules.Count });
            }
        }
    }

    // Moves i to just past the prelude: returns the index of the '{' that opens the block, or -1 (with i at the end, or after a ';' for an at-rule).
    private static int FindBlockStart(string t, ref int i, out bool semicolon)
    {
        semicolon = false;
        bool atRule = t[i] == '@';
        int depth = 0;
        char quote = '\0';
        for (; i < t.Length; i++)
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
                case ';' when atRule && depth <= 0:
                    i++;
                    semicolon = true;
                    return -1;
                case '{' when depth <= 0:
                    return i;
            }
        }

        return -1;
    }

    // Returns the index of the '}' that closes the block whose body starts at 'from' (or the end of the text).
    private static int FindBlockEnd(string t, int from)
    {
        int depth = 1;
        char quote = '\0';
        for (int i = from; i < t.Length; i++)
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

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '\\':
                    i++;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    if (--depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return t.Length;
    }

    /// <summary>Whether a <c>style</c> element's <c>type</c> attribute names CSS (a missing or empty one does).</summary>
    public static bool IsCssType(string? type) =>
        string.IsNullOrWhiteSpace(type) || type.Trim().Equals("text/css", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Matches the rules of the style sheets against elements and hands back the matching declarations in cascade order.
/// Rules are indexed by the id, a class or the type of the rightmost compound of their selector, so an element is only tested against
/// the rules that could apply to it. Every simple-selector test and ancestor or sibling move counts as a step against
/// <see cref="SvgLimits.MaxCssMatchSteps"/>, which stops a hostile sheet (long descendant chains against deep trees) with
/// <see cref="ImageLimitException"/> instead of running for minutes.
/// </summary>
internal sealed class CssMatcher
{
    private readonly Dictionary<string, List<CssRule>> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CssRule>> _byClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CssRule>> _byType = new(StringComparer.Ordinal);
    private readonly List<CssRule> _other = [];
    private readonly long _maxSteps;
    private readonly SvgElement?[] _matched = new SvgElement?[CssSelector.MaxCompounds];
    private readonly SvgElement?[] _cand = new SvgElement?[CssSelector.MaxCompounds];
    private readonly List<CssRule> _hits = [];
    private long _steps;

    public CssMatcher(List<CssRule> rules, SvgLimits limits)
    {
        _maxSteps = limits.MaxCssMatchSteps;
        foreach (CssRule r in rules)
        {
            CssCompound last = r.Selector.Last;
            string? id = null;
            string? cls = null;
            foreach (CssTest t in last.Tests)
            {
                if (t.Kind == CssTestKind.Id)
                {
                    id ??= t.Value;
                }
                else if (t.Kind == CssTestKind.Class)
                {
                    cls ??= t.Value;
                }
            }

            List<CssRule> bucket;
            if (id is not null)
            {
                bucket = Bucket(_byId, id);
            }
            else if (cls is not null)
            {
                bucket = Bucket(_byClass, cls);
            }
            else if (last.Type is not null)
            {
                bucket = Bucket(_byType, last.Type);
            }
            else
            {
                bucket = _other;
            }

            bucket.Add(r);
        }
    }

    /// <summary>Gets the number of matching steps spent so far.</summary>
    public long Steps => _steps;

    private static List<CssRule> Bucket(Dictionary<string, List<CssRule>> d, string key)
    {
        if (!d.TryGetValue(key, out List<CssRule>? list))
        {
            d[key] = list = [];
        }

        return list;
    }

    /// <summary>Collects the rules that match <paramref name="e"/>, ordered from least to most specific (ties by source order).</summary>
    public List<CssRule> Match(SvgElement e)
    {
        _hits.Clear();
        if (e.Id is { } id && _byId.TryGetValue(id, out List<CssRule>? byId))
        {
            Test(byId, e);
        }

        if (e.Classes is { Length: > 0 } classes)
        {
            foreach (string c in classes)
            {
                if (_byClass.TryGetValue(c, out List<CssRule>? byClass))
                {
                    Test(byClass, e);
                }
            }
        }

        if (_byType.TryGetValue(e.Name, out List<CssRule>? byType))
        {
            Test(byType, e);
        }

        Test(_other, e);
        if (_hits.Count > 1)
        {
            _hits.Sort(static (a, b) =>
            {
                int c = a.Selector.Specificity.CompareTo(b.Selector.Specificity);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });
        }

        return _hits;
    }

    private void Test(List<CssRule> rules, SvgElement e)
    {
        foreach (CssRule r in rules)
        {
            if (Matches(r.Selector, e))
            {
                // A class listed twice on an element must not apply a rule twice.
                if (!_hits.Contains(r))
                {
                    _hits.Add(r);
                }
            }
        }
    }

    private void Step()
    {
        if (++_steps > _maxSteps)
        {
            throw new ImageLimitException($"The style sheets need more than {_maxSteps} selector matching steps.");
        }
    }

    // Right-to-left matching with backtracking over descendant and later-sibling combinators, on explicit arrays (no recursion).
    private bool Matches(CssSelector s, SvgElement el)
    {
        CssCompound[] parts = s.Parts;
        int n = parts.Length;
        Step();
        if (!MatchCompound(parts[n - 1], el))
        {
            return false;
        }

        if (n == 1)
        {
            return true;
        }

        int i = n - 1;
        _matched[i] = el;
        _cand[i] = null;
        while (true)
        {
            CssCombinator rel = s.Rels[i];
            SvgElement? next = _cand[i] is null ? First(rel, _matched[i]!) : Next(rel, _cand[i]!);
            bool found = false;
            while (next is not null)
            {
                Step();
                if (MatchCompound(parts[i - 1], next))
                {
                    found = true;
                    break;
                }

                next = Next(rel, next);
            }

            if (found)
            {
                _cand[i] = next;
                _matched[i - 1] = next;
                if (i - 1 == 0)
                {
                    return true;
                }

                i--;
                _cand[i] = null;
            }
            else
            {
                if (i == n - 1)
                {
                    return false;
                }

                i++;
            }
        }
    }

    private static SvgElement? First(CssCombinator rel, SvgElement e) => rel switch
    {
        CssCombinator.Descendant or CssCombinator.Child => e.Parent,
        _ => e.PreviousSibling,
    };

    private static SvgElement? Next(CssCombinator rel, SvgElement cand) => rel switch
    {
        CssCombinator.Descendant => cand.Parent,
        CssCombinator.LaterSibling => cand.PreviousSibling,
        _ => null,
    };

    private bool MatchCompound(CssCompound c, SvgElement e)
    {
        if (c.Type is not null && !string.Equals(c.Type, e.Name, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (CssTest t in c.Tests)
        {
            Step();
            if (!MatchTest(t, e))
            {
                return false;
            }
        }

        return true;
    }

    private bool MatchTest(CssTest t, SvgElement e)
    {
        switch (t.Kind)
        {
            case CssTestKind.Id:
                return string.Equals(e.Id, t.Value, StringComparison.Ordinal);
            case CssTestKind.Class:
                return e.Classes is { } cl && Array.IndexOf(cl, t.Value) >= 0;
            case CssTestKind.AttrExists:
                return e.Source?.GetAttribute(null, t.Name) is not null;
            case CssTestKind.AttrEquals or CssTestKind.AttrIncludes or CssTestKind.AttrDash or CssTestKind.AttrPrefix or CssTestKind.AttrSuffix or CssTestKind.AttrSubstring:
                return e.Source?.GetAttribute(null, t.Name) is { } v && AttrMatches(t, v);
            case CssTestKind.FirstChild:
                return e.Index == 0;
            case CssTestKind.LastChild:
                return e.Parent is null || e.Index == e.Parent.Children.Count - 1;
            case CssTestKind.OnlyChild:
                return e.Parent is null || e.Parent.Children.Count == 1;
            case CssTestKind.NthChild:
                return NthMatches(t, e.Index + 1);
            case CssTestKind.NthLastChild:
                return NthMatches(t, e.Parent is null ? 1 : e.Parent.Children.Count - e.Index);
            case CssTestKind.FirstOfType or CssTestKind.LastOfType or CssTestKind.OnlyOfType or CssTestKind.NthOfType or CssTestKind.NthLastOfType:
                return MatchOfType(t, e);
            case CssTestKind.Empty:
                return e.Children.Count == 0 && (e.Source is null || !HasText(e.Source));
            case CssTestKind.Root:
                return e.Parent is null;
            case CssTestKind.Never:
                return false;
            case CssTestKind.Not:
                foreach (CssCompound c in t.Args!)
                {
                    if (MatchCompound(c, e))
                    {
                        return false;
                    }
                }

                return true;
            case CssTestKind.Is:
                foreach (CssCompound c in t.Args!)
                {
                    if (MatchCompound(c, e))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static bool HasText(Xml.XmlElement x)
    {
        foreach (Xml.XmlNode node in x.Children)
        {
            if (node is Xml.XmlText { Value.Length: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    private static bool AttrMatches(CssTest t, string v)
    {
        StringComparison cmp = t.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string want = t.Value;
        switch (t.Kind)
        {
            case CssTestKind.AttrEquals:
                return string.Equals(v, want, cmp);
            case CssTestKind.AttrIncludes:
                if (want.Length == 0 || want.AsSpan().IndexOfAny(" \t\r\n\f") >= 0)
                {
                    return false;
                }

                foreach (Range r in v.AsSpan().Split(' '))
                {
                    if (v.AsSpan(r).Trim().Equals(want, cmp))
                    {
                        return true;
                    }
                }

                return false;
            case CssTestKind.AttrDash:
                return string.Equals(v, want, cmp) || v.StartsWith(want + "-", cmp);
            case CssTestKind.AttrPrefix:
                return want.Length > 0 && v.StartsWith(want, cmp);
            case CssTestKind.AttrSuffix:
                return want.Length > 0 && v.EndsWith(want, cmp);
            default:
                return want.Length > 0 && v.Contains(want, cmp);
        }
    }

    private static bool NthMatches(CssTest t, int position)
    {
        // position = A*n + B for some n >= 0.
        if (t.A == 0)
        {
            return position == t.B;
        }

        int diff = position - t.B;
        return diff % t.A == 0 && diff / t.A >= 0;
    }

    private static bool MatchOfType(CssTest t, SvgElement e)
    {
        if (e.Parent is null)
        {
            return t.Kind is CssTestKind.FirstOfType or CssTestKind.LastOfType or CssTestKind.OnlyOfType || NthMatches(t, 1);
        }

        // Counts per type are computed once per parent, not once per child.
        e.Parent.EnsureTypeIndex();
        int fromStart = e.TypeIndex;
        int fromEnd = e.TypeCount - e.TypeIndex + 1;
        return t.Kind switch
        {
            CssTestKind.FirstOfType => fromStart == 1,
            CssTestKind.LastOfType => fromEnd == 1,
            CssTestKind.OnlyOfType => e.TypeCount == 1,
            CssTestKind.NthOfType => NthMatches(t, fromStart),
            _ => NthMatches(t, fromEnd),
        };
    }
}
