using System.Globalization;
using OneImage.Drawing;
using Path = OneImage.Drawing.Path;

namespace OneImage.Svg.Dom;

/// <summary>
/// Parses path data (<c>d</c>, SVG 1.1 section 8.3 and its error handling): the path is rendered up to the first error, so a malformed
/// tail leaves the segments before it. Data that does not start with a move yields an empty path.
/// </summary>
internal static class PathData
{
    /// <summary>Parses <paramref name="data"/>; never throws on malformed data. Returns whether the data was well formed to the end.</summary>
    public static bool Parse(ReadOnlySpan<char> data, out Path path) => Parse(data, out path, out _);

    /// <summary>
    /// Parses <paramref name="data"/>. <paramref name="dotted"/> is the same path with every open subpath of zero length (all its points
    /// equal) closed, or <see langword="null"/> when there is none: SVG draws such a subpath as a dot under round and square caps,
    /// which the stroker does for a closed figure.
    /// </summary>
    public static bool Parse(ReadOnlySpan<char> data, out Path path, out Path? dotted)
    {
        var b = new Dual();
        int i = 0;
        char cmd = '\0';
        bool first = true;
        double cx = 0, cy = 0, sx = 0, sy = 0;
        double lastCx = 0, lastCy = 0;
        char prev = '\0';
        bool ok = true;
        Skip(data, ref i);
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
                ok = false;
                break;
            }

            if (first && cmd is not ('M' or 'm'))
            {
                ok = false;
                break;
            }

            first = false;
            bool rel = char.IsAsciiLetterLower(cmd);
            double ox = rel ? cx : 0;
            double oy = rel ? cy : 0;
            char up = char.ToUpperInvariant(cmd);
            double x = 0, y = 0, x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            bool large = false, sweep = false;
            bool good = true;
            switch (up)
            {
                case 'Z':
                    b.Close();
                    cx = sx;
                    cy = sy;
                    break;
                case 'M':
                    good = Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        cx = sx = x + ox;
                        cy = sy = y + oy;
                        b.MoveTo(Coord.F(cx), Coord.F(cy));
                        cmd = rel ? 'l' : 'L';
                    }

                    break;
                case 'L':
                    good = Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        cx = x + ox;
                        cy = y + oy;
                        b.LineTo(Coord.F(cx), Coord.F(cy));
                    }

                    break;
                case 'H':
                    good = Num(data, ref i, out x);
                    if (good)
                    {
                        cx = x + ox;
                        b.LineTo(Coord.F(cx), Coord.F(cy));
                    }

                    break;
                case 'V':
                    good = Num(data, ref i, out y);
                    if (good)
                    {
                        cy = y + oy;
                        b.LineTo(Coord.F(cx), Coord.F(cy));
                    }

                    break;
                case 'C':
                case 'S':
                {
                    x1 = y1 = 0;
                    if (up == 'C')
                    {
                        good = Num(data, ref i, out x1) && Num(data, ref i, out y1);
                        x1 += ox;
                        y1 += oy;
                    }
                    else
                    {
                        bool smooth = prev is 'C' or 'S';
                        x1 = smooth ? (2 * cx) - lastCx : cx;
                        y1 = smooth ? (2 * cy) - lastCy : cy;
                    }

                    good = good && Num(data, ref i, out x2) && Num(data, ref i, out y2) && Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        x2 += ox;
                        y2 += oy;
                        x += ox;
                        y += oy;
                        b.CubicBezierTo(Coord.F(x1), Coord.F(y1), Coord.F(x2), Coord.F(y2), Coord.F(x), Coord.F(y));
                        lastCx = x2;
                        lastCy = y2;
                        cx = x;
                        cy = y;
                    }

                    break;
                }

                case 'Q':
                case 'T':
                {
                    x1 = y1 = 0;
                    if (up == 'Q')
                    {
                        good = Num(data, ref i, out x1) && Num(data, ref i, out y1);
                        x1 += ox;
                        y1 += oy;
                    }
                    else
                    {
                        bool smooth = prev is 'Q' or 'T';
                        x1 = smooth ? (2 * cx) - lastCx : cx;
                        y1 = smooth ? (2 * cy) - lastCy : cy;
                    }

                    good = good && Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        x += ox;
                        y += oy;
                        b.QuadraticBezierTo(Coord.F(x1), Coord.F(y1), Coord.F(x), Coord.F(y));
                        lastCx = x1;
                        lastCy = y1;
                        cx = x;
                        cy = y;
                    }

                    break;
                }

                case 'A':
                {
                    good = Num(data, ref i, out x1) && Num(data, ref i, out y1) && Num(data, ref i, out x2)
                        && Flag(data, ref i, out large) && Flag(data, ref i, out sweep) && Num(data, ref i, out x) && Num(data, ref i, out y);
                    if (good)
                    {
                        x += ox;
                        y += oy;
                        b.ArcTo(Coord.F(x1), Coord.F(y1), Coord.F(x2), large, sweep, Coord.F(x), Coord.F(y));
                        cx = x;
                        cy = y;
                    }

                    break;
                }

                default:
                    good = false;
                    break;
            }

            if (!good)
            {
                ok = false;
                break;
            }

            prev = up;
            Skip(data, ref i);
        }

        path = b.Build(out dotted);
        return ok;
    }

    // Builds the path and, beside it, the variant in which zero-length open subpaths are closed.
    private sealed class Dual
    {
        private readonly PathBuilder _main = new();
        private readonly PathBuilder _dotted = new();
        private bool _segments;
        private bool _moved;
        private bool _any;
        private float _sx;
        private float _sy;

        public void MoveTo(float x, float y)
        {
            Flush();
            _main.MoveTo(x, y);
            _dotted.MoveTo(x, y);
            _sx = x;
            _sy = y;
            _segments = false;
            _moved = false;
        }

        public void LineTo(float x, float y)
        {
            _main.LineTo(x, y);
            _dotted.LineTo(x, y);
            Note(x, y);
        }

        public void QuadraticBezierTo(float cx, float cy, float x, float y)
        {
            _main.QuadraticBezierTo(cx, cy, x, y);
            _dotted.QuadraticBezierTo(cx, cy, x, y);
            Note(cx, cy);
            Note(x, y);
        }

        public void CubicBezierTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            _main.CubicBezierTo(c1x, c1y, c2x, c2y, x, y);
            _dotted.CubicBezierTo(c1x, c1y, c2x, c2y, x, y);
            Note(c1x, c1y);
            Note(c2x, c2y);
            Note(x, y);
        }

        public void ArcTo(float rx, float ry, float rotation, bool large, bool sweep, float x, float y)
        {
            _main.ArcTo(rx, ry, rotation, large, sweep, x, y);
            _dotted.ArcTo(rx, ry, rotation, large, sweep, x, y);
            Note(x, y);
        }

        public void Close()
        {
            _main.Close();
            _dotted.Close();
            _segments = false;
        }

        public Path Build(out Path? dotted)
        {
            Flush();
            dotted = _any ? _dotted.Build() : null;
            return _main.Build();
        }

        private void Note(float x, float y)
        {
            _segments = true;
            _moved |= x != _sx || y != _sy;
        }

        private void Flush()
        {
            if (_segments && !_moved)
            {
                _dotted.Close();
                _any = true;
            }

            _segments = false;
        }
    }

    private static bool IsCommand(char c) => c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z';

    private static void Skip(ReadOnlySpan<char> d, ref int i)
    {
        while (i < d.Length && d[i] is ' ' or '\t' or '\n' or '\r' or '\f')
        {
            i++;
        }
    }

    // Separators between arguments: white space with at most one comma.
    private static void Sep(ReadOnlySpan<char> d, ref int i)
    {
        Skip(d, ref i);
        if (i < d.Length && d[i] == ',')
        {
            i++;
            Skip(d, ref i);
        }
    }

    private static bool Num(ReadOnlySpan<char> d, ref int i, out double v)
    {
        v = 0;
        Skip(d, ref i);
        int len = SvgSize.NumberLength(d[i..]);
        if (len == 0 || !double.TryParse(d.Slice(i, len), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || !double.IsFinite(v) || Math.Abs(v) > 3e38)
        {
            return false;
        }

        i += len;
        Sep(d, ref i);
        return true;
    }

    private static bool Flag(ReadOnlySpan<char> d, ref int i, out bool v)
    {
        v = false;
        Skip(d, ref i);
        if (i >= d.Length || d[i] is not ('0' or '1'))
        {
            return false;
        }

        v = d[i] == '1';
        i++;
        Sep(d, ref i);
        return true;
    }
}
