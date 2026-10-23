using System.Globalization;

namespace OneImage.Svg.Dom;

internal enum TextAnchor
{
    Start,
    Middle,
    End,
}

/// <summary>The baselines <c>dominant-baseline</c> and <c>alignment-baseline</c> can name.</summary>
internal enum BaselineKind
{
    Auto,
    Alphabetic,
    Hanging,
    Ideographic,
    Mathematical,
    Central,
    Middle,
    TextBefore,
    TextAfter,
}

internal enum ShiftKind
{
    None,
    Sub,
    Super,
    Length,
}

/// <summary>The text-specific computed values of one element (the ones <see cref="SvgStyle"/> does not carry).</summary>
internal sealed class TextStyle
{
    public static readonly TextStyle Initial = new();

    // Inherited.
    public string[] Families = ["serif"];
    public int Weight = 400;
    public bool Italic;
    public bool SmallCaps;
    public TextAnchor Anchor;
    public double LetterSpacing;
    public double WordSpacing;
    public bool Rtl;
    public bool Vertical;
    public BaselineKind Dominant;
    public bool Kerning = true;
    public bool PreserveSpace;
    public bool OptimizeSpeed;

    // Not inherited.
    public bool Underline;
    public bool Overline;
    public bool LineThrough;
    public BaselineKind Alignment;
    public ShiftKind Shift;
    public Length ShiftLength;
    public bool BidiEmbed;
    public bool BidiOverride;

    public bool HasDecoration => Underline || Overline || LineThrough;

    /// <summary>
    /// Computes an element's text style from its declared properties and its parent's computed one. An invalid declaration is ignored,
    /// as if it had not been written.
    /// </summary>
    public static TextStyle Compute(SvgElement e, TextStyle parent, in SvgStyle st, double percentBase = 100)
    {
        var t = (TextStyle)parent.MemberwiseClone();
        t.Underline = t.Overline = t.LineThrough = false;
        t.Alignment = BaselineKind.Auto;
        t.Shift = ShiftKind.None;
        t.BidiEmbed = t.BidiOverride = false;

        if (e.Attrs.TryGetValue("xml:space", out string? space))
        {
            if (space == "preserve")
            {
                t.PreserveSpace = true;
            }
            else if (space == "default")
            {
                t.PreserveSpace = false;
            }
        }

        foreach ((string name, string raw) in e.Props)
        {
            string value = raw.Trim();
            bool inherit = value == "inherit";
            bool initial = value == "initial";
            switch (name)
            {
                case "font-family":
                    if (initial)
                    {
                        t.Families = Initial.Families;
                    }
                    else if (!inherit && ParseFamilies(value) is { Length: > 0 } fams)
                    {
                        t.Families = fams;
                    }

                    break;
                case "font-weight":
                    if (initial)
                    {
                        t.Weight = 400;
                    }
                    else if (!inherit && ParseWeight(value, parent.Weight) is { } w)
                    {
                        t.Weight = w;
                    }

                    break;
                case "font-style":
                    if (initial || value == "normal")
                    {
                        t.Italic = false;
                    }
                    else if (value is "italic" or "oblique" || value.StartsWith("oblique ", StringComparison.Ordinal))
                    {
                        t.Italic = true;
                    }

                    break;
                case "font-variant" or "font-variant-caps":
                    if (value == "small-caps")
                    {
                        t.SmallCaps = true;
                    }
                    else if (value == "normal" || initial)
                    {
                        t.SmallCaps = false;
                    }

                    break;
                case "font-kerning":
                    if (value == "none")
                    {
                        t.Kerning = false;
                    }
                    else if (value is "normal" or "auto" || initial)
                    {
                        t.Kerning = true;
                    }

                    break;
                case "kerning":
                    // The SVG 1.1 property: a zero length turns kerning off, `auto` leaves it on.
                    if (value is "auto" || initial)
                    {
                        t.Kerning = true;
                    }
                    else if (Length.TryParse(value, out Length kl) && kl.Value == 0)
                    {
                        t.Kerning = false;
                    }

                    break;
                case "text-rendering":
                    // Only the text element decides how its glyphs are drawn; a tspan's own value is not used.
                    if (e.Name != "text")
                    {
                        break;
                    }

                    if (value is "optimizeSpeed")
                    {
                        t.OptimizeSpeed = true;
                    }
                    else if (value is "optimizeLegibility" or "geometricPrecision" or "auto" || initial)
                    {
                        t.OptimizeSpeed = false;
                    }

                    break;
                case "text-anchor":
                    if (value is "start" or "middle" or "end")
                    {
                        t.Anchor = value == "start" ? TextAnchor.Start : value == "middle" ? TextAnchor.Middle : TextAnchor.End;
                    }
                    else if (initial)
                    {
                        t.Anchor = TextAnchor.Start;
                    }

                    break;
                case "letter-spacing":
                    if (value == "normal" || initial)
                    {
                        t.LetterSpacing = 0;
                    }
                    else if (!inherit && SpacingPx(value, st, percentBase) is { } ls)
                    {
                        t.LetterSpacing = ls;
                    }

                    break;
                case "word-spacing":
                    if (value == "normal" || initial)
                    {
                        t.WordSpacing = 0;
                    }
                    else if (!inherit && SpacingPx(value, st, percentBase) is { } ws)
                    {
                        t.WordSpacing = ws;
                    }

                    break;
                case "direction":
                    if (value is "ltr" or "rtl")
                    {
                        t.Rtl = value == "rtl";
                    }
                    else if (initial)
                    {
                        t.Rtl = false;
                    }

                    break;
                case "unicode-bidi":
                    t.BidiEmbed = value is "embed" or "isolate";
                    t.BidiOverride = value is "bidi-override" or "isolate-override";
                    break;
                case "writing-mode":
                    if (value is "tb" or "tb-rl" or "vertical-rl" or "vertical-lr" or "sideways-rl" or "sideways-lr")
                    {
                        t.Vertical = true;
                    }
                    else if (value is "lr" or "lr-tb" or "rl" or "rl-tb" or "horizontal-tb" || initial)
                    {
                        // The old right-to-left horizontal modes do not turn the text around (direction does).
                        t.Vertical = false;
                    }

                    break;
                case "dominant-baseline":
                    if (Baseline(value) is { } db)
                    {
                        t.Dominant = db;
                    }
                    else if (initial)
                    {
                        t.Dominant = BaselineKind.Auto;
                    }

                    break;
                case "alignment-baseline":
                    if (inherit)
                    {
                        t.Alignment = parent.Alignment;
                    }
                    else if (value == "baseline")
                    {
                        // The element's own dominant baseline, which is what an unset alignment-baseline uses.
                        t.Alignment = BaselineKind.Auto;
                    }
                    else if (Baseline(value) is { } ab)
                    {
                        t.Alignment = ab;
                    }

                    break;
                case "baseline-shift":
                    if (inherit)
                    {
                        t.Shift = parent.Shift;
                        t.ShiftLength = parent.ShiftLength;
                    }
                    else
                    {
                        ParseShift(value, t);
                    }

                    break;
                case "text-decoration" or "text-decoration-line":
                    foreach (string part in value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        switch (part)
                        {
                            case "underline":
                                t.Underline = true;
                                break;
                            case "overline":
                                t.Overline = true;
                                break;
                            case "line-through":
                                t.LineThrough = true;
                                break;
                        }
                    }

                    break;
                case "white-space":
                    if (value is "pre" or "pre-wrap" or "break-spaces")
                    {
                        t.PreserveSpace = true;
                    }
                    else if (value is "normal" or "nowrap" or "pre-line")
                    {
                        t.PreserveSpace = false;
                    }

                    break;
            }
        }

        return t;
    }

    private static double? SpacingPx(string value, in SvgStyle st, double percentBase)
    {
        if (!Length.TryParse(value, out Length l))
        {
            return null;
        }

        return l.Unit switch
        {
            LengthUnit.Px => l.Value,
            LengthUnit.Em => l.Value * st.FontSize,
            LengthUnit.Ex => l.Value * st.FontSize / 2,
            LengthUnit.Rem => l.Value * st.MediumFontSize,
            LengthUnit.Percent => l.Value / 100 * percentBase,
            _ => null,
        };
    }

    private static BaselineKind? Baseline(string value)
    {
        switch (value)
        {
            case "auto" or "use-script":
                return BaselineKind.Auto;
            case "alphabetic" or "baseline":
                return BaselineKind.Alphabetic;
            case "hanging":
                return BaselineKind.Hanging;
            case "mathematical":
                return BaselineKind.Mathematical;
            case "central":
                return BaselineKind.Central;
            case "middle":
                return BaselineKind.Middle;
            case "text-before-edge" or "text-top" or "before-edge":
                return BaselineKind.TextBefore;
            case "text-after-edge" or "text-bottom" or "after-edge":
                return BaselineKind.TextAfter;
            case "ideographic":
                return BaselineKind.Ideographic;
            default:
                return null;
        }
    }

    private static void ParseShift(string value, TextStyle t)
    {
        switch (value)
        {
            case "baseline":
                t.Shift = ShiftKind.None;
                return;
            case "sub":
                t.Shift = ShiftKind.Sub;
                return;
            case "super":
                t.Shift = ShiftKind.Super;
                return;
        }

        if (Length.TryParse(value, out Length l))
        {
            t.Shift = ShiftKind.Length;
            t.ShiftLength = l;
        }
    }

    internal static string[] ParseFamilies(string value)
    {
        var list = new List<string>();
        int i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && (value[i] is ' ' or '\t' or '\n' or '\r' or ','))
            {
                i++;
            }

            if (i >= value.Length)
            {
                break;
            }

            if (value[i] is '\'' or '"')
            {
                char q = value[i++];
                int start = i;
                while (i < value.Length && value[i] != q)
                {
                    i++;
                }

                string quoted = value[start..i].Trim();
                if (quoted.Length > 0)
                {
                    list.Add(quoted);
                }

                i = Math.Min(i + 1, value.Length);
            }
            else
            {
                int start = i;
                while (i < value.Length && value[i] != ',')
                {
                    i++;
                }

                string unquoted = string.Join(' ', value[start..i].Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
                if (unquoted.Length > 0)
                {
                    list.Add(unquoted);
                }
            }
        }

        return [.. list];
    }

    private static int? ParseWeight(string value, int parent)
    {
        switch (value)
        {
            case "normal":
                return 400;
            case "bold":
                return 700;
            case "bolder":
                return parent < 400 ? 400 : parent < 600 ? 700 : 900;
            case "lighter":
                return parent < 600 ? 100 : parent < 800 ? 400 : 700;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 1 && d <= 1000)
        {
            return (int)Math.Round(d);
        }

        return null;
    }
}

/// <summary>The <c>font</c> shorthand: expands to the longhand properties it sets.</summary>
internal static class TextFont
{
    /// <summary>Replaces the <c>font</c> property of <paramref name="e"/> by its longhands (style, variant, weight, stretch, size, family). A value that does not parse is dropped.</summary>
    public static void ExpandShorthand(SvgElement e)
    {
        string value = e.Props["font"].Trim();
        e.Props.Remove("font");
        if (value is "inherit" or "initial" or "unset" or "")
        {
            return;
        }

        // [style || variant || weight || stretch]? size[/line-height]? family
        var tokens = new List<string>();
        int i = 0;
        string family = string.Empty;
        while (i < value.Length)
        {
            while (i < value.Length && value[i] is ' ' or '\t' or '\n' or '\r')
            {
                i++;
            }

            if (i >= value.Length)
            {
                break;
            }

            if (tokens.Count >= 1 && IsSizeToken(tokens[^1]))
            {
                family = value[i..];
                break;
            }

            int start = i;
            while (i < value.Length && value[i] is not (' ' or '\t' or '\n' or '\r'))
            {
                i++;
            }

            tokens.Add(value[start..i]);
        }

        if (family.Length == 0 && tokens.Count > 0)
        {
            return;
        }

        string? style = null, variant = null, weight = null, size = null;
        foreach (string tok in tokens)
        {
            string main = tok;
            int slash = tok.IndexOf('/');
            if (slash > 0)
            {
                main = tok[..slash];
            }

            if (size is null && IsSizeToken(tok))
            {
                size = main;
            }
            else if (size is null)
            {
                if (tok is "italic" or "oblique")
                {
                    style = tok;
                }
                else if (tok == "small-caps")
                {
                    variant = tok;
                }
                else if (tok is "bold" or "bolder" or "lighter" || (tok.Length > 0 && char.IsDigit(tok[0]) && int.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                {
                    weight = tok;
                }
            }
        }

        if (size is null || family.Length == 0)
        {
            return;
        }

        e.Props["font-style"] = style ?? "normal";
        e.Props["font-variant"] = variant ?? "normal";
        e.Props["font-kerning"] = "auto";
        e.Props["font-weight"] = weight ?? "normal";
        e.Props["font-size"] = size;
        e.Props["font-family"] = family;
    }

    private static bool IsSizeToken(string tok)
    {
        int slash = tok.IndexOf('/');
        string main = slash > 0 ? tok[..slash] : tok;
        return main is "xx-small" or "x-small" or "small" or "medium" or "large" or "x-large" or "xx-large" or "larger" or "smaller"
            || (main.Length > 0 && (char.IsDigit(main[0]) || main[0] is '.' or '+' or '-') && Length.TryParse(main, out _));
    }
}
