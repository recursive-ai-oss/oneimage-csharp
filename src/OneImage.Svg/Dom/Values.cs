using System.Globalization;

namespace OneImage.Svg.Dom;

internal enum LengthUnit
{
    /// <summary>Pixels (absolute units are converted when parsed).</summary>
    Px,

    /// <summary>A multiple of the font size.</summary>
    Em,

    /// <summary>Half the font size.</summary>
    Ex,

    /// <summary>A percentage of the reference viewport size.</summary>
    Percent,

    /// <summary>A multiple of the root element's font size.</summary>
    Rem,
}

/// <summary>An SVG length. Absolute units are folded into pixels at 96 per inch.</summary>
internal readonly record struct Length(double Value, LengthUnit Unit)
{
    public static Length Zero { get; } = new(0, LengthUnit.Px);

    public static bool TryParse(ReadOnlySpan<char> text, out Length length)
    {
        length = default;
        if (!SvgSize.TryParseLengthUnits(text, out double v, out LengthUnit unit) || !double.IsFinite(v))
        {
            return false;
        }

        length = new Length(v, unit);
        return true;
    }
}

/// <summary>Where a <c>viewBox</c> lands inside its viewport: alignment and meet or slice.</summary>
internal readonly record struct AspectRatio(int AlignX, int AlignY, bool Slice, bool None)
{
    /// <summary>The default, <c>xMidYMid meet</c>.</summary>
    public static AspectRatio Default { get; } = new(1, 1, false, false);

    /// <summary>Parses the attribute; returns the default for a missing or malformed value.</summary>
    public static AspectRatio Parse(string? value)
    {
        if (value is null)
        {
            return Default;
        }

        string[] parts = value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        if (parts.Length > 0 && parts[0] == "defer")
        {
            i = 1;
        }

        if (i >= parts.Length || parts.Length > i + 2)
        {
            return Default;
        }

        bool slice = false;
        if (parts.Length == i + 2)
        {
            if (parts[i + 1] == "slice")
            {
                slice = true;
            }
            else if (parts[i + 1] != "meet")
            {
                return Default;
            }
        }

        string a = parts[i];
        if (a == "none")
        {
            return new AspectRatio(0, 0, slice, true);
        }

        if (a.Length != 8 || a[0] != 'x' || a[4] != 'Y')
        {
            return Default;
        }

        int ax = AlignIndex(a.AsSpan(1, 3));
        int ay = AlignIndex(a.AsSpan(5, 3));
        return ax < 0 || ay < 0 ? Default : new AspectRatio(ax, ay, slice, false);
    }

    private static int AlignIndex(ReadOnlySpan<char> s) => s switch { "Min" => 0, "Mid" => 1, "Max" => 2, _ => -1 };

    /// <summary>
    /// The transform that places a view box in a viewport of width x height user units
    /// (SVG 1.1 section 7.8): a uniform scale chosen by meet or slice, then aligned; or a non-uniform stretch for <c>none</c>.
    /// </summary>
    public Affine Fit(double vbX, double vbY, double vbW, double vbH, double width, double height)
    {
        double sx = width / vbW;
        double sy = height / vbH;
        if (None)
        {
            return new Affine(sx, 0, 0, sy, -vbX * sx, -vbY * sy);
        }

        double s = Slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
        double tx = -vbX * s;
        double ty = -vbY * s;
        tx += AlignX * (width - (vbW * s)) / 2;
        ty += AlignY * (height - (vbH * s)) / 2;
        return new Affine(s, 0, 0, s, tx, ty);
    }
}

/// <summary>Parsers for the small SVG micro-syntaxes: number lists, transforms, view boxes.</summary>
internal static class SvgValues
{
    private const string Sep = " \t\n\r\f";

    /// <summary>Reads one number and skips following separators (white space and at most one comma).</summary>
    public static bool NextNumber(ref ReadOnlySpan<char> s, out double value)
    {
        value = 0;
        s = s.TrimStart(Sep);
        int len = SvgSize.NumberLength(s);
        if (len == 0 || !double.TryParse(s[..len], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            return false;
        }

        s = s[len..].TrimStart(Sep);
        if (!s.IsEmpty && s[0] == ',')
        {
            s = s[1..];
        }

        return true;
    }

    /// <summary>Parses a list of numbers; a malformed tail is dropped (the numbers before it are kept) and reported by <paramref name="clean"/>.</summary>
    public static List<double> NumberList(ReadOnlySpan<char> s, out bool clean)
    {
        var list = new List<double>();
        clean = true;
        s = s.Trim(Sep);
        while (!s.IsEmpty)
        {
            if (!NextNumber(ref s, out double v))
            {
                clean = false;
                break;
            }

            list.Add(v);
        }

        return list;
    }

    public static bool TryViewBox(string? text, out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> s = text;
        s = s.Trim(Sep);
        if (!NextNumber(ref s, out x) || !NextNumber(ref s, out y) || !NextNumber(ref s, out w) || !NextNumber(ref s, out h) || !s.IsEmpty)
        {
            return false;
        }

        return w > 0 && h > 0;
    }

    /// <summary>
    /// Parses a transform list (SVG 1.1 section 7.4). Any error makes the whole attribute invalid.
    /// An empty list is the identity.
    /// </summary>
    public static bool TryTransform(ReadOnlySpan<char> text, out Affine result)
    {
        result = Affine.Identity;
        ReadOnlySpan<char> s = text.Trim(Sep);
        Span<double> a = stackalloc double[6];
        while (!s.IsEmpty)
        {
            int open = s.IndexOf('(');
            if (open <= 0)
            {
                return false;
            }

            ReadOnlySpan<char> name = s[..open].Trim(Sep);
            int close = s.IndexOf(')');
            if (close < open)
            {
                return false;
            }

            ReadOnlySpan<char> args = s[(open + 1)..close];
            int n = 0;
            args = args.Trim(Sep);
            while (!args.IsEmpty)
            {
                if (n == 6 || !NextNumber(ref args, out double v))
                {
                    return false;
                }

                a[n++] = v;
            }

            Affine m;
            switch (name)
            {
                case "matrix" when n == 6:
                    m = new Affine(a[0], a[1], a[2], a[3], a[4], a[5]);
                    break;
                case "translate" when n is 1 or 2:
                    m = Affine.Translate(a[0], n == 2 ? a[1] : 0);
                    break;
                case "scale" when n is 1 or 2:
                    m = Affine.Scale(a[0], n == 2 ? a[1] : a[0]);
                    break;
                case "rotate" when n is 1 or 3:
                {
                    double rad = a[0] * Math.PI / 180;
                    double cos = Math.Cos(rad);
                    double sin = Math.Sin(rad);
                    var r = new Affine(cos, sin, -sin, cos, 0, 0);
                    m = n == 3 ? Affine.Translate(a[1], a[2]).Multiply(r).Multiply(Affine.Translate(-a[1], -a[2])) : r;
                    break;
                }

                case "skewX" when n == 1:
                    m = new Affine(1, 0, Math.Tan(a[0] * Math.PI / 180), 1, 0, 0);
                    break;
                case "skewY" when n == 1:
                    m = new Affine(1, Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0);
                    break;
                default:
                    return false;
            }

            result = result.Multiply(m);
            s = s[(close + 1)..].TrimStart(Sep);
            if (!s.IsEmpty && s[0] == ',')
            {
                s = s[1..].TrimStart(Sep);
            }
        }

        return true;
    }
}
