using System.Text;

namespace OneImage.Svg;

/// <summary>Recognizes SVG from the first bytes: XML (UTF-8 or UTF-16) whose root element is <c>svg</c>, or gzip data that starts like that.</summary>
internal static class SvgSniffer
{
    /// <summary>How many decompressed bytes are examined for gzip data.</summary>
    private const int GzipProbe = 2048;

    public static bool Matches(ReadOnlySpan<byte> header)
    {
        if (SvgSource.IsGzip(header))
        {
            try
            {
                byte[] head = SvgSource.Gunzip(header, GzipProbe, 1100, strict: false, out _);
                return MatchesPlain(head);
            }
            catch (ImageException)
            {
                // Not a usable gzip stream, so not an SVG.
                return false;
            }
        }

        return MatchesPlain(header);
    }

    private static bool MatchesPlain(ReadOnlySpan<byte> data)
    {
        string text;
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(data[2..]);
        }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(data[2..]);
        }
        else
        {
            text = Encoding.UTF8.GetString(data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? data[3..] : data);
        }

        ReadOnlySpan<char> s = text;
        int p = 0;
        while (true)
        {
            while (p < s.Length && s[p] is ' ' or '\t' or '\n' or '\r' or '\uFEFF')
            {
                p++;
            }

            ReadOnlySpan<char> rest = s[p..];
            if (rest.StartsWith("<?", StringComparison.Ordinal))
            {
                int end = rest.IndexOf("?>", StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                p += end + 2;
            }
            else if (rest.StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = rest.IndexOf("-->", StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                p += end + 3;
            }
            else if (rest.StartsWith("<!DOCTYPE", StringComparison.Ordinal))
            {
                int end = DoctypeEnd(rest);
                if (end < 0)
                {
                    return false;
                }

                p += end;
            }
            else
            {
                return IsSvgStartTag(rest);
            }
        }
    }

    // Skips a DOCTYPE including an internal subset in brackets; returns the length consumed or -1.
    private static int DoctypeEnd(ReadOnlySpan<char> s)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 9; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (c == '>' && depth <= 0)
            {
                return i + 1;
            }
        }

        return -1;
    }

    private static bool IsSvgStartTag(ReadOnlySpan<char> s)
    {
        if (s.Length < 5 || s[0] != '<')
        {
            return false;
        }

        int i = 1;
        int nameStart = 1;
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '_' or '-' or '.' or ':'))
        {
            if (s[i] == ':')
            {
                nameStart = i + 1;
            }

            i++;
        }

        if (i >= s.Length || s[i] is not (' ' or '\t' or '\n' or '\r' or '>' or '/'))
        {
            return false;
        }

        return s[nameStart..i].SequenceEqual("svg");
    }
}
