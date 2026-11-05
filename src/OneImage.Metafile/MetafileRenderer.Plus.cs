using System.Numerics;
using OneImage.Drawing;
using OneImage.Text;
using CorePath = OneImage.Drawing.Path;
using TextFont = OneImage.Text.Font;

namespace OneImage.Metafile;

// The renderer's part for GDI+ (EMF+) commands.
internal sealed partial class MetafileRenderer
{
    private readonly Stack<(bool Aa, bool SourceCopy, ImageInterpolation Interp)> _hintStack = new();
    private bool _srcCopy;
    private ImageInterpolation _interp = ImageInterpolation.Bilinear;

    private AlphaComposition Composition => _srcCopy ? AlphaComposition.Src : AlphaComposition.SrcOver;

    private void PushHints() => _hintStack.Push((_aa, _srcCopy, _interp));

    private void PopHints()
    {
        if (_hintStack.Count > 0)
        {
            (_aa, _srcCopy, _interp) = _hintStack.Pop();
        }
    }

    private static float DashOffset(PenSpec pen) => pen.Plus is { } p && double.IsFinite(p.DashOffset) ? (float)Math.Clamp(p.DashOffset, -1e7, 1e7) : 0f;

    public override void VisitRenderingHints(RenderingHintsCommand command)
    {
        _aa = _aaOption && command.Smoothing;
        _srcCopy = command.SourceCopy;
        _interp = command.Interpolation;
    }

    public override void VisitClear(ClearCommand command)
    {
        _ct.ThrowIfCancellationRequested();
        _canvas.Fill(command.Color, CorePath.Rectangle(0, 0, _w, _h), false, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.Src, false, null, _clip, _clipRule);
    }

    // ---- brushes -------------------------------------------------------------------------------------------------------

    private Brush? PlusBrush(BrushSpec spec)
    {
        switch (spec)
        {
            case LinearGradientBrushSpec l:
                {
                    // File values: a brush the drawing layer would refuse draws nothing.
                    if (l.Stops.Count is 0 or > 1 << 16 || !IsInvertible(l.Transform * Xf) || !IsFinite(l.Start) || !IsFinite(l.End) || (l.End - l.Start).LengthSquared() < 1e-9f
                        || l.Stops.Any(x => !float.IsFinite(x.Position)))
                    {
                        return null;
                    }

                    var stops = new ColorStop[l.Stops.Count];
                    for (int i = 0; i < stops.Length; i++)
                    {
                        stops[i] = new ColorStop(l.Stops[i].Position, l.Stops[i].Color);
                    }

                    GradientRepetition rep = l.Wrap switch
                    {
                        EdgeWrap.Clamp => GradientRepetition.None,
                        EdgeWrap.TileFlipX or EdgeWrap.TileFlipXY => GradientRepetition.Reflect,
                        _ => GradientRepetition.Repeat,
                    };
                    return Brush.LinearGradient(l.Start, l.End, stops, l.Transform * Xf, rep, false);
                }

            case PathGradientBrushSpec g:
                {
                    Matrix3x2 m = g.Transform * Xf;
                    if (g.Boundary.Count is < 3 or > 1 << 16 || g.BoundaryColors.Count != g.Boundary.Count || !IsFinite(Vector2.Transform(g.Center, m)) || g.Boundary.Any(b => !IsFinite(Vector2.Transform(b, m))))
                    {
                        return null;
                    }

                    var pts = new Vector2[g.Boundary.Count];
                    for (int i = 0; i < pts.Length; i++)
                    {
                        pts[i] = Vector2.Transform(g.Boundary[i], m);
                    }

                    return Brush.PathGradient(pts, [.. g.BoundaryColors], g.CenterColor, Vector2.Transform(g.Center, m));
                }

            case TextureBrushSpec t:
                return TextureBrush(t);
            case StyleHatchBrushSpec h:
                return Brush.Pattern(h.Foreground, h.Background, PlusHatch.Cells(h.Style), Matrix3x2.Identity);
            default:
                return null;
        }
    }

    private static bool IsFinite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    private Brush? TextureBrush(TextureBrushSpec t)
    {
        if (!IsInvertible(t.Transform * Xf))
        {
            return null;
        }

        RasterImage img = t.Image;
        int w = img.Width, h = img.Height;
        byte[] px = img.Rgba;
        int fw = t.Wrap is EdgeWrap.TileFlipX or EdgeWrap.TileFlipXY ? 2 : 1;
        int fh = t.Wrap is EdgeWrap.TileFlipY or EdgeWrap.TileFlipXY ? 2 : 1;
        if (fw * fh > 1)
        {
            // Mirrored copies are laid into one larger tile, which then repeats.
            var big = new byte[w * fw * h * fh * 4];
            for (int y = 0; y < h * fh; y++)
            {
                int sy = y < h ? y : (h * 2) - 1 - y;
                for (int x = 0; x < w * fw; x++)
                {
                    int sx = x < w ? x : (w * 2) - 1 - x;
                    Buffer.BlockCopy(px, ((sy * w) + sx) * 4, big, ((y * w * fw) + x) * 4, 4);
                }
            }

            px = big;
            w *= fw;
            h *= fh;
        }

        ImageSampling sampling = _interp == ImageInterpolation.Nearest ? ImageSampling.Nearest : ImageSampling.Bilinear;
        return Brush.FromImage(ImageOf(px, w, h), sampling, null, 0, 0, t.Transform * Xf, t.Wrap != EdgeWrap.Clamp);
    }

    // ---- images --------------------------------------------------------------------------------------------------------

    public override void VisitDrawImage(DrawImageCommand c)
    {
        _ct.ThrowIfCancellationRequested();
        if (!IsInvertible(Xf))
        {
            // GDI and GDI+ draw nothing through a transform that collapses the plane (or overflows).
            return;
        }

        RasterImage img = c.Image;
        RectD s = c.Source.Normalized;
        if (s.Width <= 0 || s.Height <= 0)
        {
            return;
        }

        int x0 = (int)Math.Floor(s.Left), y0 = (int)Math.Floor(s.Top);
        int cw = (int)Math.Ceiling(s.Right) - x0, ch = (int)Math.Ceiling(s.Bottom) - y0;
        if (cw <= 0 || ch <= 0 || (long)cw * ch > 1L << 26)
        {
            return;
        }

        var crop = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                SampleWrapped(img, c.Wrap, c.ClampColor, x0 + x, y0 + y, crop, ((y * cw) + x) * 4);
            }
        }

        Vector2 u = (c.DestTopRight - c.DestTopLeft) / (float)s.Width, v = (c.DestBottomLeft - c.DestTopLeft) / (float)s.Height;
        Vector2 origin = c.DestTopLeft + ((float)(x0 - s.Left) * u) + ((float)(y0 - s.Top) * v);
        var toLogical = new Matrix3x2(u.X, u.Y, v.X, v.Y, origin.X, origin.Y);
        if (!IsInvertible(toLogical * Xf))
        {
            return;
        }

        ImageSampling sampling = c.Interpolation == ImageInterpolation.Nearest ? ImageSampling.Nearest : ImageSampling.Bilinear;
        Brush brush = Brush.FromImage(ImageOf(crop, cw, ch), sampling, null, 0, 0, toLogical * Xf, false);
        Vector2 br = c.DestTopRight + c.DestBottomLeft - c.DestTopLeft;
        CorePath quad = new PathBuilder().MoveTo(c.DestTopLeft).LineTo(c.DestTopRight).LineTo(br).LineTo(c.DestBottomLeft).Close().Build();
        bool axis = c.DestTopLeft.Y == c.DestTopRight.Y && c.DestTopLeft.X == c.DestBottomLeft.X;
        _canvas.Fill(brush, quad, _aa && !axis, IntersectionRule.NonZero, (float)c.Opacity, BlendMode.Normal, Composition, false, Xf, _clip, _clipRule);
    }

    private static void SampleWrapped(RasterImage img, EdgeWrap wrap, Rgba32 clamp, int x, int y, byte[] dst, int o)
    {
        int w = img.Width, h = img.Height;
        bool inside = x >= 0 && y >= 0 && x < w && y < h;
        if (!inside)
        {
            switch (wrap)
            {
                case EdgeWrap.Clamp:
                    (dst[o], dst[o + 1], dst[o + 2], dst[o + 3]) = (clamp.R, clamp.G, clamp.B, clamp.A);
                    return;
                default:
                    x = Fold(x, w, wrap is EdgeWrap.TileFlipX or EdgeWrap.TileFlipXY);
                    y = Fold(y, h, wrap is EdgeWrap.TileFlipY or EdgeWrap.TileFlipXY);
                    break;
            }
        }

        Buffer.BlockCopy(img.Rgba, ((y * w) + x) * 4, dst, o, 4);
    }

    private static int Fold(int v, int n, bool mirror)
    {
        if (!mirror)
        {
            return ((v % n) + n) % n;
        }

        int m = ((v % (2 * n)) + (2 * n)) % (2 * n);
        return m < n ? m : (2 * n) - 1 - m;
    }

    // ---- text ----------------------------------------------------------------------------------------------------------

    private TextFont? PlusFontFor(FontSpec fs, out double em)
    {
        em = _fonts.EmSize(fs);
        return _fonts.CreateFont(fs, em);
    }

    public override void VisitDrawString(DrawStringCommand c)
    {
        _ct.ThrowIfCancellationRequested();
        if (!IsInvertible(Xf))
        {
            // GDI and GDI+ draw nothing through a transform that collapses the plane (or overflows).
            return;
        }

        TextFont? font = PlusFontFor(c.Font, out double em);
        Brush? brush = ToBrush(c.Brush);
        if (font is null || brush is null || c.Text.Length == 0)
        {
            return;
        }

        FontMetrics fm = font.Metrics;
        double k = em / fm.UnitsPerEm;
        double asc = fm.Ascender * k, desc = Math.Abs(fm.Descender) * k, lineH = asc + desc + (fm.LineGap * k);
        double mL = c.Format.LeadingMargin * em, mR = c.Format.TrailingMargin * em;
        RectD lay = c.Layout;
        Matrix3x2 lm = Matrix3x2.Identity;
        if (c.Format.Vertical)
        {
            // Vertical lines run top to bottom with the glyph tops to the right: lay the text out sideways, then turn it a quarter turn
            // clockwise about the first line's top edge, which is the right edge of the rectangle (or one line height right of its origin).
            double ox = c.Layout.Width > 0 ? c.Layout.Right : c.Layout.Left + lineH;
            lm = new Matrix3x2(0, 1, -1, 0, (float)ox, (float)c.Layout.Top);
            lay = new RectD(0, 0, c.Layout.Height, c.Layout.Width);
        }
        double avail = lay.Width > 0 && !c.Format.NoWrap ? lay.Width - mL - mR : double.PositiveInfinity;

        List<(string Text, double Width)> lines = BreakLines(c.Text, font, avail);
        double blockH = Math.Max(0, (lines.Count * lineH) - (fm.LineGap * k));
        double top = lay.Top;
        if (lay.Height > 0)
        {
            top += c.Format.LineAlignment switch { 1 => (lay.Height - blockH) / 2, 2 => lay.Height - blockH, _ => 0 };
        }

        var b = new PathBuilder();
        double baseline = top + asc;
        foreach ((string text, double width) in lines)
        {
            double x = lay.Left + mL;
            double room = lay.Width - mL - mR;
            if (c.Format.Alignment == 1)
            {
                x = lay.Left + mL + ((room - width) / 2);
            }
            else if (c.Format.Alignment == 2)
            {
                x = lay.Right - mR - width;
            }

            double pen = x;
            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }

                b.AddPath(font.GetGlyphPath(cp).Translate((float)pen, (float)baseline));
                pen += font.GetAdvance(cp);
            }

            if (c.Font.Underline)
            {
                b.AddPath(CorePath.Rectangle((float)x, (float)(baseline - (fm.UnderlinePosition * k)), (float)width, (float)Math.Max(fm.UnderlineThickness * k, 1)));
            }

            if (c.Font.StrikeOut)
            {
                b.AddPath(CorePath.Rectangle((float)x, (float)(baseline - (fm.StrikeoutPosition * k)), (float)width, (float)Math.Max(fm.StrikeoutSize * k, 1)));
            }

            baseline += lineH;
        }

        CorePath? savedClip = _clip;
        IntersectionRule savedRule = _clipRule;
        if (!c.Format.NoClip && lay.Width > 0 && lay.Height > 0)
        {
            CorePath q = CorePath.Rectangle((float)c.Layout.Left, (float)c.Layout.Top, (float)c.Layout.Width, (float)c.Layout.Height).Transform(Xf);
            _clip = _clip is null ? q : _clip.Intersect(q, _clipRule, IntersectionRule.NonZero);
            _clipRule = IntersectionRule.NonZero;
        }

        try
        {
            _canvas.Fill(brush, b.Build(), _aaOption && c.Font.Quality != 3, IntersectionRule.NonZero, 1f, BlendMode.Normal, Composition, false, lm * Xf, _clip, _clipRule);
        }
        finally
        {
            _clip = savedClip;
            _clipRule = savedRule;
        }
    }

    /// <summary>Splits text into lines at newlines and, when the available width is finite, at spaces (and inside a word that does not fit alone).</summary>
    private static List<(string Text, double Width)> BreakLines(string text, TextFont font, double avail)
    {
        var lines = new List<(string, double)>();
        double Adv(string s)
        {
            double w = 0;
            for (int i = 0; i < s.Length; i++)
            {
                int cp = s[i];
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    cp = char.ConvertToUtf32(s[i], s[i + 1]);
                    i++;
                }

                w += font.GetAdvance(cp);
            }

            return w;
        }

        foreach (string para in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (double.IsPositiveInfinity(avail))
            {
                lines.Add((para, Adv(para)));
                continue;
            }

            string line = string.Empty;
            foreach (string word in SplitWords(para))
            {
                string candidate = line + word;
                if (line.Length > 0 && Adv(candidate.TrimEnd(' ')) > avail)
                {
                    lines.Add((line.TrimEnd(' '), Adv(line.TrimEnd(' '))));
                    line = word.TrimStart(' ');
                }
                else
                {
                    line = candidate;
                }

                // A single word wider than the line is broken between characters.
                while (Adv(line) > avail && line.Length > 1)
                {
                    int n = line.Length - 1;
                    while (n > 1 && Adv(line[..n]) > avail)
                    {
                        n--;
                    }

                    lines.Add((line[..n], Adv(line[..n])));
                    line = line[n..];
                }
            }

            lines.Add((line.TrimEnd(' '), Adv(line.TrimEnd(' '))));
        }

        return lines;
    }

    private static IEnumerable<string> SplitWords(string s)
    {
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == ' ')
            {
                yield return s[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < s.Length)
        {
            yield return s[start..];
        }
    }

    public override void VisitDrawGlyphs(DrawGlyphsCommand c)
    {
        _ct.ThrowIfCancellationRequested();
        if (!IsInvertible(Xf))
        {
            // GDI and GDI+ draw nothing through a transform that collapses the plane (or overflows).
            return;
        }

        TextFont? font = PlusFontFor(c.Font, out _);
        Brush? brush = ToBrush(c.Brush);
        if (font is null || brush is null || c.Text.Length == 0)
        {
            return;
        }

        var b = new PathBuilder();
        Vector2 pen = c.Origin;
        for (int i = 0; i < c.Text.Length; i++)
        {
            int cp = c.Text[i];
            Vector2 at = c.Positions is { } p && i < p.Count ? p[i] : pen;
            b.AddPath(font.GetGlyphPath(cp).Translate(at.X, at.Y));
            float adv = font.GetAdvance(cp);
            pen = c.Vertical ? at + new Vector2(0, adv) : at + new Vector2(adv, 0);
        }

        Matrix3x2 m = (c.Transform ?? Matrix3x2.Identity) * Xf;
        _canvas.Fill(brush, b.Build(), _aaOption && c.Font.Quality != 3, IntersectionRule.NonZero, 1f, BlendMode.Normal, Composition, false, m, _clip, _clipRule);
    }
}

/// <summary>The 54 GDI+ hatch styles as eight-by-eight cells. Styles that are plain lines or checkers are exact; the dithered and woven ones are approximations.</summary>
internal static class PlusHatch
{
    private static readonly Dictionary<int, bool[,]> Cache = [];

    private static readonly int[] Bayer =
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    public static bool[,] Cells(int style)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(style, out bool[,]? c))
            {
                return c;
            }

            c = new bool[8, 8];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    c[y, x] = Cell(style, x, y);
                }
            }

            return Cache[style] = c;
        }
    }

    private static bool Dither(int x, int y, int count) => Bayer[((y & 7) * 8) + (x & 7)] < count;

    private static bool Cell(int s, int x, int y)
    {
        int d = (x - y) & 7, e = (x + y) & 7;
        switch (s)
        {
            case 0: return y == 0;
            case 1: return x == 0;
            case 2: return d == 0;
            case 3: return e == 7;
            case 4: return x == 0 || y == 0;
            case 5: return d == 0 || e == 7;
            case 6: return Dither(x, y, 3);
            case 7: return Dither(x, y, 6);
            case 8: return Dither(x, y, 13);
            case 9: return Dither(x, y, 16);
            case 10: return Dither(x, y, 19);
            case 11: return Dither(x, y, 26);
            case 12: return ((x + y) & 1) == 0;
            case 13: return Dither(x, y, 38);
            case 14: return Dither(x, y, 45);
            case 15: return Dither(x, y, 48);
            case 16: return Dither(x, y, 51);
            case 17: return Dither(x, y, 58);
            case 18: return (d & 3) == 0;
            case 19: return ((e + 1) & 3) == 0;
            case 20: return (d & 3) < 2;
            case 21: return ((e + 1) & 3) < 2;
            case 22: return d < 3;
            case 23: return ((e + 5) & 7) < 3;
            case 24: return (x & 3) == 0;
            case 25: return (y & 3) == 0;
            case 26: return (x & 1) == 0;
            case 27: return (y & 1) == 0;
            case 28: return (x & 3) < 2;
            case 29: return (y & 3) < 2;
            case 30: return (d & 3) == 0 && (x & 7) < 4;
            case 31: return ((e + 1) & 3) == 0 && (x & 7) < 4;
            case 32: return (y & 3) == 0 && (((x + ((y & 4) != 0 ? 4 : 0)) & 7) < 4);
            case 33: return (x & 3) == 0 && (((y + ((x & 4) != 0 ? 4 : 0)) & 7) < 4);
            case 34: return Dither(x, y, 12);
            case 35: return Dither(x >> 1, y >> 1, 24);
            case 36: return (y & 3) == ((x & 7) < 4 ? x & 3 : 3 - (x & 3));
            case 37: return y == new[] { 3, 2, 2, 3, 4, 5, 5, 4 }[x & 7];
            case 38: return d == 0 || y == 0 || y == 4;
            case 39: return y == 0 || y == 4 || (x == 0 && y is > 0 and < 4) || (x == 4 && y > 4);
            case 40: return (((x >> 2) ^ (y >> 2)) & 1) == 0 ? ((x + y) & 3) == 0 : ((x - y) & 3) == 0;
            case 41: return ((y & 7) < 4 && (x & 1) == 0) || ((x & 7) >= 4 && (y & 1) == 0);
            case 42: return Dither(x, y, 10);
            case 43: return ((y & 3) == 0 && (x & 1) == 0) || ((x & 3) == 0 && (y & 1) == 0);
            case 44: return (((x + y) & 3) == 0 || ((x - y) & 3) == 0) && ((x + y) & 1) == 0;
            case 45: return y < 4 ? e == 0 : ((x + y + 2) & 7) == 0;
            case 46: return (y & 3) == 0 || ((x & 3) == 0 && ((x + y) & 1) == 0);
            case 47: return Dither(x, y, 40);
            case 48: return (x & 3) == 0 || (y & 3) == 0;
            case 49: return ((x ^ y) & 1) == 0;
            case 50: return (((x >> 2) ^ (y >> 2)) & 1) == 0;
            case 51: return x + y == 3 || x + y == 11 || x - y == 4 || y - x == 4;
            case 52: return x + y >= 3 && x + y <= 11 && Math.Abs(x - y) <= 4;
            default: return Dither(x, y, 32);
        }
    }
}
