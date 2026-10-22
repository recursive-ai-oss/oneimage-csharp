using System.Globalization;

namespace OneImage.Svg.Dom;

/// <summary>Parses CSS colors: keywords, hex, <c>rgb()</c>/<c>rgba()</c>, <c>hsl()</c>/<c>hsla()</c> and <c>hwb()</c> in both the comma and the space syntax.</summary>
internal static class CssColor
{
    private const string Names =
        "aliceblue f0f8ff,antiquewhite faebd7,aqua 00ffff,aquamarine 7fffd4,azure f0ffff,beige f5f5dc,bisque ffe4c4,black 000000,blanchedalmond ffebcd,blue 0000ff," +
        "blueviolet 8a2be2,brown a52a2a,burlywood deb887,cadetblue 5f9ea0,chartreuse 7fff00,chocolate d2691e,coral ff7f50,cornflowerblue 6495ed,cornsilk fff8dc,crimson dc143c," +
        "cyan 00ffff,darkblue 00008b,darkcyan 008b8b,darkgoldenrod b8860b,darkgray a9a9a9,darkgreen 006400,darkgrey a9a9a9,darkkhaki bdb76b,darkmagenta 8b008b,darkolivegreen 556b2f," +
        "darkorange ff8c00,darkorchid 9932cc,darkred 8b0000,darksalmon e9967a,darkseagreen 8fbc8f,darkslateblue 483d8b,darkslategray 2f4f4f,darkslategrey 2f4f4f,darkturquoise 00ced1," +
        "darkviolet 9400d3,deeppink ff1493,deepskyblue 00bfff,dimgray 696969,dimgrey 696969,dodgerblue 1e90ff,firebrick b22222,floralwhite fffaf0,forestgreen 228b22,fuchsia ff00ff," +
        "gainsboro dcdcdc,ghostwhite f8f8ff,gold ffd700,goldenrod daa520,gray 808080,grey 808080,green 008000,greenyellow adff2f,honeydew f0fff0,hotpink ff69b4,indianred cd5c5c," +
        "indigo 4b0082,ivory fffff0,khaki f0e68c,lavender e6e6fa,lavenderblush fff0f5,lawngreen 7cfc00,lemonchiffon fffacd,lightblue add8e6,lightcoral f08080,lightcyan e0ffff," +
        "lightgoldenrodyellow fafad2,lightgray d3d3d3,lightgreen 90ee90,lightgrey d3d3d3,lightpink ffb6c1,lightsalmon ffa07a,lightseagreen 20b2aa,lightskyblue 87cefa," +
        "lightslategray 778899,lightslategrey 778899,lightsteelblue b0c4de,lightyellow ffffe0,lime 00ff00,limegreen 32cd32,linen faf0e6,magenta ff00ff,maroon 800000," +
        "mediumaquamarine 66cdaa,mediumblue 0000cd,mediumorchid ba55d3,mediumpurple 9370db,mediumseagreen 3cb371,mediumslateblue 7b68ee,mediumspringgreen 00fa9a," +
        "mediumturquoise 48d1cc,mediumvioletred c71585,midnightblue 191970,mintcream f5fffa,mistyrose ffe4e1,moccasin ffe4b5,navajowhite ffdead,navy 000080,oldlace fdf5e6,olive 808000," +
        "olivedrab 6b8e23,orange ffa500,orangered ff4500,orchid da70d6,palegoldenrod eee8aa,palegreen 98fb98,paleturquoise afeeee,palevioletred db7093,papayawhip ffefd5," +
        "peachpuff ffdab9,peru cd853f,pink ffc0cb,plum dda0dd,powderblue b0e0e6,purple 800080,rebeccapurple 663399,red ff0000,rosybrown bc8f8f,royalblue 4169e1,saddlebrown 8b4513," +
        "salmon fa8072,sandybrown f4a460,seagreen 2e8b57,seashell fff5ee,sienna a0522d,silver c0c0c0,skyblue 87ceeb,slateblue 6a5acd,slategray 708090,slategrey 708090,snow fffafa," +
        "springgreen 00ff7f,steelblue 4682b4,tan d2b48c,teal 008080,thistle d8bfd8,tomato ff6347,turquoise 40e0d0,violet ee82ee,wheat f5deb3,white ffffff,whitesmoke f5f5f5," +
        "yellow ffff00,yellowgreen 9acd32";

    private static readonly Lazy<Dictionary<string, uint>> Table = new(() =>
    {
        var d = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (string entry in Names.Split(','))
        {
            int sp = entry.IndexOf(' ', StringComparison.Ordinal);
            d[entry[..sp]] = uint.Parse(entry[(sp + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return d;
    });

    /// <summary>
    /// Parses a color. <c>transparent</c> is accepted; <c>currentColor</c> is not (the caller handles it, as it needs the element's
    /// <c>color</c>). The whole text must be one color (surrounding white space is allowed).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out Rgba32 color)
    {
        color = default;
        ReadOnlySpan<char> s = text.Trim(" \t\n\r\f");
        if (s.IsEmpty)
        {
            return false;
        }

        if (s[0] == '#')
        {
            return TryHex(s[1..], out color);
        }

        int paren = s.IndexOf('(');
        if (paren > 0)
        {
            if (s[^1] != ')')
            {
                return false;
            }

            Span<char> name = stackalloc char[8];
            ReadOnlySpan<char> fn = s[..paren].Trim();
            if (fn.Length > name.Length || fn.ToLowerInvariant(name) != fn.Length)
            {
                return false;
            }

            return TryFunction(name[..fn.Length], s[(paren + 1)..^1], out color);
        }

        Span<char> lower = stackalloc char[24];
        if (s.Length > lower.Length)
        {
            return false;
        }

        s.ToLowerInvariant(lower);
        ReadOnlySpan<char> key = lower[..s.Length];
        if (key is "transparent")
        {
            return true;
        }

        if (Table.Value.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out uint rgb))
        {
            color = new Rgba32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
            return true;
        }

        return false;
    }

    private static bool TryHex(ReadOnlySpan<char> h, out Rgba32 c)
    {
        c = default;
        Span<int> v = stackalloc int[8];
        if (h.Length is not (3 or 4 or 6 or 8))
        {
            return false;
        }

        for (int i = 0; i < h.Length; i++)
        {
            int d = HexValue(h[i]);
            if (d < 0)
            {
                return false;
            }

            v[i] = d;
        }

        if (h.Length <= 4)
        {
            c = new Rgba32((byte)(v[0] * 17), (byte)(v[1] * 17), (byte)(v[2] * 17), h.Length == 4 ? (byte)(v[3] * 17) : (byte)255);
        }
        else
        {
            c = new Rgba32((byte)((v[0] << 4) | v[1]), (byte)((v[2] << 4) | v[3]), (byte)((v[4] << 4) | v[5]), h.Length == 8 ? (byte)((v[6] << 4) | v[7]) : (byte)255);
        }

        return true;
    }

    private static int HexValue(char ch) =>
        ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;

    // One component: a number, optionally followed by % or (for hue) deg/grad/rad/turn.
    private readonly record struct Comp(double Value, bool Percent, string Unit);

    private static bool TryFunction(ReadOnlySpan<char> fn, ReadOnlySpan<char> args, out Rgba32 c)
    {
        c = default;
        Comp[] comp = new Comp[4];
        int n = 0;
        bool commas = args.Contains(',');
        ReadOnlySpan<char> rest = args.Trim(" \t\n\r\f");
        while (!rest.IsEmpty)
        {
            if (n == 4)
            {
                return false;
            }

            int len = 0;
            while (len < rest.Length && rest[len] is not (' ' or '\t' or '\n' or '\r' or '\f' or ',' or '/'))
            {
                len++;
            }

            if (len == 0 || !ParseComp(rest[..len], out comp[n]))
            {
                return false;
            }

            n++;
            rest = rest[len..].TrimStart(" \t\n\r\f");
            if (rest.IsEmpty)
            {
                break;
            }

            if (rest[0] is ',' or '/')
            {
                // Legacy syntax uses commas throughout; the modern one a slash only before alpha.
                if (rest[0] == '/' && (commas || n != 3))
                {
                    return false;
                }

                if (rest[0] == ',' && (!commas || n >= 4))
                {
                    return false;
                }

                rest = rest[1..].TrimStart(" \t\n\r\f");
                if (rest.IsEmpty)
                {
                    return false;
                }
            }
            else if (commas)
            {
                return false;
            }
        }

        if (n is not (3 or 4))
        {
            return false;
        }

        double alpha = 1;
        if (n == 4)
        {
            alpha = Math.Clamp(comp[3].Percent ? comp[3].Value / 100 : comp[3].Value, 0, 1);
            if (comp[3].Unit.Length > 0)
            {
                return false;
            }
        }

        switch (fn)
        {
            case "rgb" or "rgba":
            {
                // Integers and percentages may not be mixed among the three channels.
                if (comp[0].Percent != comp[1].Percent || comp[1].Percent != comp[2].Percent)
                {
                    return false;
                }

                double[] ch = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (comp[i].Unit.Length > 0)
                    {
                        return false;
                    }

                    ch[i] = comp[i].Percent ? comp[i].Value * 255 / 100 : comp[i].Value;
                }

                c = new Rgba32(ToByte(ch[0]), ToByte(ch[1]), ToByte(ch[2]), ToByte(alpha * 255));
                return true;
            }

            case "hsl" or "hsla":
            {
                if (!Hue(comp[0], out double h))
                {
                    return false;
                }

                double sat = Math.Clamp(comp[1].Value, 0, 100) / 100;
                double light = Math.Clamp(comp[2].Value, 0, 100) / 100;
                (double r, double g, double b) = FromHsl(h, sat, light);
                c = new Rgba32(ToByte(r * 255), ToByte(g * 255), ToByte(b * 255), ToByte(alpha * 255));
                return true;
            }

            case "hwb":
            {
                if (commas || !Hue(comp[0], out double h))
                {
                    return false;
                }

                double w = Math.Clamp(comp[1].Value, 0, 100) / 100;
                double bl = Math.Clamp(comp[2].Value, 0, 100) / 100;
                if (w + bl >= 1)
                {
                    double gray = w / (w + bl);
                    c = new Rgba32(ToByte(gray * 255), ToByte(gray * 255), ToByte(gray * 255), ToByte(alpha * 255));
                    return true;
                }

                (double r, double g, double b) = FromHsl(h, 1, 0.5);
                double k = 1 - w - bl;
                c = new Rgba32(ToByte(((r * k) + w) * 255), ToByte(((g * k) + w) * 255), ToByte(((b * k) + w) * 255), ToByte(alpha * 255));
                return true;
            }

            default:
                return false;
        }
    }

    private static bool ParseComp(ReadOnlySpan<char> t, out Comp comp)
    {
        comp = default;
        if (t is "none")
        {
            comp = new Comp(0, false, string.Empty);
            return true;
        }

        int len = SvgSize.NumberLength(t);
        if (len == 0 || !double.TryParse(t[..len], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            return false;
        }

        ReadOnlySpan<char> unit = t[len..];
        if (unit.Length == 0)
        {
            comp = new Comp(v, false, string.Empty);
        }
        else if (unit is "%")
        {
            comp = new Comp(v, true, string.Empty);
        }
        else if (unit is "deg" or "grad" or "rad" or "turn")
        {
            comp = new Comp(v, false, unit.ToString());
        }
        else
        {
            return false;
        }

        return true;
    }

    private static bool Hue(Comp c, out double degrees)
    {
        degrees = c.Unit switch
        {
            "" => c.Value,
            "deg" => c.Value,
            "grad" => c.Value * 0.9,
            "rad" => c.Value * 180 / Math.PI,
            "turn" => c.Value * 360,
            _ => double.NaN,
        };
        if (c.Percent || double.IsNaN(degrees))
        {
            return false;
        }

        degrees = ((degrees % 360) + 360) % 360;
        return true;
    }

    private static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        double p = (2 * l) - q;
        double hk = h / 360;
        return (Channel(p, q, hk + (1.0 / 3)), Channel(p, q, hk), Channel(p, q, hk - (1.0 / 3)));
    }

    private static double Channel(double p, double q, double t)
    {
        t -= Math.Floor(t);
        return t < 1.0 / 6 ? p + ((q - p) * 6 * t) : t < 0.5 ? q : t < 2.0 / 3 ? p + ((q - p) * ((2.0 / 3) - t) * 6) : p;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);
}
