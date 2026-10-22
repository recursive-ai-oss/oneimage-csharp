using System.Globalization;
using System.Numerics;

namespace OneImage.Drawing;

/// <summary>
/// Reads SVG path data (SVG 1.1 section 8.3) into a <see cref="Path"/>. This parses untrusted text, so it is bounded: input
/// length and command count are capped, every number is checked for being finite, and nothing recurses. The first
/// malformed token fails the whole parse.
/// </summary>
internal static class SvgPathParser
{
    private const int MaxChars = 1 << 26;

    public static bool TryParse(ReadOnlySpan<char> data, out Path? path)
    {
        path = null;
        if (data.Length > MaxChars)
        {
            return false;
        }

        var b = new PathBuilder();
        int i = 0;
        char cmd = '\0';
        bool first = true;
        double cx = 0, cy = 0, sx = 0, sy = 0;
        double lastCx = 0, lastCy = 0; // the previous curve's last control point, for S and T
        char prev = '\0';
        SkipSpace(data, ref i);
        try
        {
            while (i < data.Length)
            {
                char c = data[i];
                if (IsCommand(c))
                {
                    cmd = c;
                    i++;
                }
                else if (cmd == '\0' || cmd is 'Z' or 'z')
                {
                    return false;
                }

                if (first && cmd is not ('M' or 'm'))
                {
                    return false;
                }

                first = false;
                bool rel = char.IsAsciiLetterLower(cmd);
                double ox = rel ? cx : 0, oy = rel ? cy : 0;
                char up = char.ToUpperInvariant(cmd);
                double x, y, x1, y1, x2, y2;
                switch (up)
                {
                    case 'Z':
                        b.Close();
                        cx = sx;
                        cy = sy;
                        break;
                    case 'M':
                        if (!Num(data, ref i, out x) || !Num(data, ref i, out y) || !Check(x + ox, y + oy))
                        {
                            return false;
                        }

                        cx = sx = x + ox;
                        cy = sy = y + oy;
                        b.MoveTo((float)cx, (float)cy);
                        cmd = rel ? 'l' : 'L'; // further pairs are implicit line-tos
                        break;
                    case 'L':
                        if (!Num(data, ref i, out x) || !Num(data, ref i, out y) || !Check(x + ox, y + oy))
                        {
                            return false;
                        }

                        cx = x + ox;
                        cy = y + oy;
                        b.LineTo((float)cx, (float)cy);
                        break;
                    case 'H':
                        if (!Num(data, ref i, out x) || !Check(x + ox, cy))
                        {
                            return false;
                        }

                        cx = x + ox;
                        b.LineTo((float)cx, (float)cy);
                        break;
                    case 'V':
                        if (!Num(data, ref i, out y) || !Check(cx, y + oy))
                        {
                            return false;
                        }

                        cy = y + oy;
                        b.LineTo((float)cx, (float)cy);
                        break;
                    case 'C':
                    case 'S':
                        if (up == 'C')
                        {
                            if (!Num(data, ref i, out x1) || !Num(data, ref i, out y1))
                            {
                                return false;
                            }

                            x1 += ox;
                            y1 += oy;
                        }
                        else
                        {
                            bool smooth = prev is 'C' or 'S';
                            x1 = smooth ? (2 * cx) - lastCx : cx;
                            y1 = smooth ? (2 * cy) - lastCy : cy;
                        }

                        if (!Num(data, ref i, out x2) || !Num(data, ref i, out y2) || !Num(data, ref i, out x) || !Num(data, ref i, out y))
                        {
                            return false;
                        }

                        x2 += ox;
                        y2 += oy;
                        x += ox;
                        y += oy;
                        if (!Check(x1, y1) || !Check(x2, y2) || !Check(x, y))
                        {
                            return false;
                        }

                        b.CubicBezierTo((float)x1, (float)y1, (float)x2, (float)y2, (float)x, (float)y);
                        lastCx = x2;
                        lastCy = y2;
                        cx = x;
                        cy = y;
                        break;
                    case 'Q':
                    case 'T':
                        if (up == 'Q')
                        {
                            if (!Num(data, ref i, out x1) || !Num(data, ref i, out y1))
                            {
                                return false;
                            }

                            x1 += ox;
                            y1 += oy;
                        }
                        else
                        {
                            bool smooth = prev is 'Q' or 'T';
                            x1 = smooth ? (2 * cx) - lastCx : cx;
                            y1 = smooth ? (2 * cy) - lastCy : cy;
                        }

                        if (!Num(data, ref i, out x) || !Num(data, ref i, out y))
                        {
                            return false;
                        }

                        x += ox;
                        y += oy;
                        if (!Check(x1, y1) || !Check(x, y))
                        {
                            return false;
                        }

                        b.QuadraticBezierTo((float)x1, (float)y1, (float)x, (float)y);
                        lastCx = x1;
                        lastCy = y1;
                        cx = x;
                        cy = y;
                        break;
                    default: // 'A'
                        if (!Num(data, ref i, out x1) || !Num(data, ref i, out y1) || !Num(data, ref i, out x2)
                            || !Flag(data, ref i, out bool large) || !Flag(data, ref i, out bool sweep)
                            || !Num(data, ref i, out x) || !Num(data, ref i, out y))
                        {
                            return false;
                        }

                        x += ox;
                        y += oy;
                        if (!Check(x1, y1) || !Check(x2, 0) || !Check(x, y))
                        {
                            return false;
                        }

                        b.ArcTo((float)x1, (float)y1, (float)x2, large, sweep, (float)x, (float)y);
                        cx = x;
                        cy = y;
                        break;
                }

                prev = up;
                SkipSpace(data, ref i);
            }
        }
        catch (ImageLimitException)
        {
            // Too many commands: the data is refused, not truncated.
            return false;
        }

        path = b.Build();
        return true;
    }

    private static bool IsCommand(char c) => c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z';

    // A coordinate must survive the cast to float, which is what paths store.
    private static bool Check(double x, double y) => float.IsFinite((float)x) && float.IsFinite((float)y);

    private static void SkipSpace(ReadOnlySpan<char> d, ref int i)
    {
        while (i < d.Length && d[i] is ' ' or '\t' or '\n' or '\r' or '\f')
        {
            i++;
        }
    }

    // Whitespace, then at most one comma, then whitespace: the separator before an argument.
    private static void SkipSeparator(ReadOnlySpan<char> d, ref int i)
    {
        SkipSpace(d, ref i);
        if (i < d.Length && d[i] == ',')
        {
            i++;
            SkipSpace(d, ref i);
        }
    }

    private static bool Flag(ReadOnlySpan<char> d, ref int i, out bool value)
    {
        SkipSeparator(d, ref i);
        value = false;
        if (i < d.Length && d[i] is '0' or '1')
        {
            value = d[i] == '1';
            i++;
            return true;
        }

        return false;
    }

    private static bool Num(ReadOnlySpan<char> d, ref int i, out double value)
    {
        SkipSeparator(d, ref i);
        value = 0;
        int s = i;
        int p = i;
        if (p < d.Length && d[p] is '+' or '-')
        {
            p++;
        }

        int digits = 0;
        while (p < d.Length && char.IsAsciiDigit(d[p]))
        {
            p++;
            digits++;
        }

        if (p < d.Length && d[p] == '.')
        {
            p++;
            while (p < d.Length && char.IsAsciiDigit(d[p]))
            {
                p++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        if (p < d.Length && d[p] is 'e' or 'E')
        {
            int q = p + 1;
            if (q < d.Length && d[q] is '+' or '-')
            {
                q++;
            }

            if (q < d.Length && char.IsAsciiDigit(d[q]))
            {
                while (q < d.Length && char.IsAsciiDigit(d[q]))
                {
                    q++;
                }

                p = q;
            }
        }

        if (!double.TryParse(d[s..p], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            return false;
        }

        i = p;
        return true;
    }
}
