namespace OneImage.Svg.Dom;

/// <summary>Parses the declarations of a <c>style</c> attribute (<c>name: value; name: value</c>).</summary>
internal static class StyleDeclarations
{
    /// <summary>
    /// Returns the declarations in order. Names are lower-cased; values are trimmed and lose a trailing <c>!important</c>.
    /// Comments are removed, semicolons inside quotes or parentheses do not split, and a declaration without a colon is skipped.
    /// </summary>
    public static List<(string Name, string Value)> Parse(string style)
    {
        var full = ParseFull(style);
        var result = new List<(string, string)>(full.Count);
        foreach ((string name, string value, bool _) in full)
        {
            result.Add((name, value));
        }

        return result;
    }

    /// <summary>As <see cref="Parse"/>, and says which declarations carry <c>!important</c>.</summary>
    public static List<(string Name, string Value, bool Important)> ParseFull(string style)
    {
        var result = new List<(string, string, bool)>();
        if (style.Contains("/*", StringComparison.Ordinal))
        {
            style = StripComments(style);
        }

        int start = 0;
        int paren = 0;
        char quote = '\0';
        for (int i = 0; i <= style.Length; i++)
        {
            char c = i < style.Length ? style[i] : ';';
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
                case '(':
                    paren++;
                    break;
                case ')':
                    if (paren > 0)
                    {
                        paren--;
                    }

                    break;
                case ';' when paren == 0:
                    Add(result, style.AsSpan(start, i - start));
                    start = i + 1;
                    break;
            }
        }

        return result;
    }

    internal static string StripComments(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    break;
                }

                i = end + 2;
            }
            else
            {
                sb.Append(s[i++]);
            }
        }

        return sb.ToString();
    }

    private static void Add(List<(string, string, bool)> result, ReadOnlySpan<char> decl)
    {
        int colon = decl.IndexOf(':');
        if (colon <= 0)
        {
            return;
        }

        string name = decl[..colon].Trim().ToString().ToLowerInvariant();
        ReadOnlySpan<char> value = decl[(colon + 1)..].Trim();
        int bang = value.LastIndexOf('!');
        bool important = false;
        if (bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..bang].TrimEnd();
            important = true;
        }

        if (name.Length > 0 && value.Length > 0)
        {
            result.Add((name, value.ToString(), important));
        }
    }
}
