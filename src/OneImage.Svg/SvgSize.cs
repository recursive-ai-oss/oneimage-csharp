using System.Globalization;
using OneImage.Svg.Xml;

namespace OneImage.Svg;

/// <summary>The natural size of a document, from the root element's <c>width</c>, <c>height</c> and <c>viewBox</c>.</summary>
internal static class SvgSize
{
    internal const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>
    /// Computes the natural size in CSS pixels (96 per inch). Absolute lengths and <c>em</c>/<c>ex</c> (16 px and 8 px, ignoring
    /// font settings on the root) are accepted; percentages count as unset. A missing side is derived from the other through the
    /// <c>viewBox</c> aspect ratio; with neither, the <c>viewBox</c> size is used.
    /// </summary>
    /// <exception cref="ImageFormatException">The root is not an SVG element, a size is not positive, or no size can be derived.</exception>
    public static (double Width, double Height) Natural(XmlElement root)
    {
        if (root.LocalName != "svg" || root.Namespace != SvgNamespace)
        {
            throw new ImageFormatException("The root element is not an SVG element.");
        }

        (double? w, double? h) = Partial(root.GetAttribute(null, "width"), root.GetAttribute(null, "height"), root.GetAttribute(null, "viewBox"));
        if (w is null && h is null)
        {
            throw new ImageFormatException("The SVG has neither a usable width and height nor a viewBox.");
        }

        if (w is null || h is null)
        {
            throw new ImageFormatException("The SVG gives only one of width and height and has no viewBox.");
        }

        return (w.Value, h.Value);
    }

    /// <summary>
    /// The size the root attributes give, with a missing side derived through the <c>viewBox</c> aspect ratio when there is one.
    /// A side that nothing determines is <see langword="null"/> (the caller then measures the content).
    /// </summary>
    /// <exception cref="ImageFormatException">A size is malformed, zero or negative.</exception>
    public static (double? Width, double? Height) Partial(string? width, string? height, string? viewBox)
    {
        double? w = Dimension(width);
        double? h = Dimension(height);
        (double Width, double Height)? vb = ViewBox(viewBox);
        if (w is null && h is null)
        {
            return vb is { } v ? (v.Width, v.Height) : (null, null);
        }

        if (w is not null && h is not null)
        {
            return (w, h);
        }

        if (vb is null)
        {
            return (w, h);
        }

        return w is not null ? (w, w.Value * vb.Value.Height / vb.Value.Width) : (h!.Value * vb.Value.Width / vb.Value.Height, h);
    }

    /// <summary>Converts a size to whole pixels, rounding up and never below 1.</summary>
    public static (int Width, int Height) ToPixels(double width, double height, ImageLimits limits)
    {
        double cw = Math.Ceiling(width - 1e-9);
        double ch = Math.Ceiling(height - 1e-9);
        if (!(cw <= ImageLimits.EngineMaxDimension) || !(ch <= ImageLimits.EngineMaxDimension))
        {
            throw new ImageLimitException("The SVG's natural size is too large.");
        }

        int pw = (int)Math.Max(1, cw);
        int ph = (int)Math.Max(1, ch);
        limits.CheckFrame(pw, ph);
        return (pw, ph);
    }

    // null: attribute missing or relative (percent). Throws for zero, negative or unparsable values.
    private static double? Dimension(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!TryParseLength(value, out double v, out bool percent))
        {
            throw new ImageFormatException($"Invalid length '{value}'.");
        }

        if (percent)
        {
            return null;
        }

        if (!double.IsFinite(v))
        {
            throw new ImageFormatException("The SVG size is not a finite number.");
        }

        // A zero or negative size cannot be drawn; like a missing one it leaves the size to the view box or the content.
        return v > 0 ? v : null;
    }

    private static (double, double)? ViewBox(string? value)
    {
        if (value is null)
        {
            return null;
        }

        Span<double> n = stackalloc double[4];
        ReadOnlySpan<char> s = value;
        for (int i = 0; i < 4; i++)
        {
            s = s.TrimStart(" \t\n\r,");
            int len = NumberLength(s);
            if (len == 0 || !double.TryParse(s[..len], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]))
            {
                return null;
            }

            s = s[len..];
        }

        if (!s.TrimStart(" \t\n\r,").IsEmpty || !(n[2] > 0) || !(n[3] > 0) || !double.IsFinite(n[2]) || !double.IsFinite(n[3]))
        {
            return null;
        }

        return (n[2], n[3]);
    }

    /// <summary>Parses an SVG length (number plus optional unit) into CSS pixels. <paramref name="percent"/> is set for <c>%</c>.</summary>
    internal static bool TryParseLength(ReadOnlySpan<char> text, out double pixels, out bool percent)
    {
        pixels = 0;
        percent = false;
        ReadOnlySpan<char> s = text.Trim(" \t\n\r");
        int len = NumberLength(s);
        if (len == 0 || !double.TryParse(s[..len], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            return false;
        }

        ReadOnlySpan<char> unit = s[len..];
        double scale;
        switch (unit)
        {
            case "" or "px": scale = 1; break;
            case "pt": scale = 4.0 / 3.0; break;
            case "pc": scale = 16; break;
            case "mm": scale = 96 / 25.4; break;
            case "cm": scale = 96 / 2.54; break;
            case "in": scale = 96; break;
            case "em": scale = 16; break;
            case "ex": scale = 8; break;
            case "%": percent = true; scale = 1; break;
            default: return false;
        }

        pixels = v * scale;
        return true;
    }

    /// <summary>Parses a length; absolute units become pixels, <c>em</c>, <c>ex</c> and <c>%</c> keep their unit.</summary>
    internal static bool TryParseLengthUnits(ReadOnlySpan<char> text, out double value, out Dom.LengthUnit unit)
    {
        value = 0;
        unit = Dom.LengthUnit.Px;
        ReadOnlySpan<char> s = text.Trim(" \t\n\r");
        int len = NumberLength(s);
        if (len == 0 || !double.TryParse(s[..len], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            return false;
        }

        double scale = 1;
        switch (s[len..])
        {
            case "" or "px": break;
            case "pt": scale = 4.0 / 3.0; break;
            case "pc": scale = 16; break;
            case "mm": scale = 96 / 25.4; break;
            case "cm": scale = 96 / 2.54; break;
            case "in": scale = 96; break;
            case "em": unit = Dom.LengthUnit.Em; break;
            case "rem": unit = Dom.LengthUnit.Rem; break;
            case "ex": unit = Dom.LengthUnit.Ex; break;
            case "%": unit = Dom.LengthUnit.Percent; break;
            default: return false;
        }

        value = v * scale;
        return true;
    }

    // Length of the leading SVG number: [+-] digits [. digits] [e [+-] digits], or [+-] . digits ...
    internal static int NumberLength(ReadOnlySpan<char> s)
    {
        int i = 0;
        if (i < s.Length && s[i] is '+' or '-')
        {
            i++;
        }

        int digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            i++;
            digits++;
        }

        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return 0;
        }

        // An exponent only counts when digits follow; "1em" must not read "1e" as the start of one.
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            int j = i + 1;
            if (j < s.Length && s[j] is '+' or '-')
            {
                j++;
            }

            if (j < s.Length && char.IsAsciiDigit(s[j]))
            {
                while (j < s.Length && char.IsAsciiDigit(s[j]))
                {
                    j++;
                }

                i = j;
            }
        }

        return i;
    }
}
