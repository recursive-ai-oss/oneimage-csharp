using OneImage.Drawing;
using OnePdf;

namespace OneImage.Pdf.Render;

/// <summary>What identifies a rendered pattern tile: the pattern, how it maps to the page, its tint and how the tile is laid out.</summary>
internal readonly record struct TileKey(PdfTilingPattern Pattern, long A, long B, long C, long D, int FracX, int FracY, uint Tint, bool Aligned);

/// <summary>
/// One period of a tiling pattern drawn to pixels. In the aligned layout (the pattern maps to the page without rotation or skew and its
/// period is a whole number of pixels) the tile is exactly the pixels of the page under one period, picked by position modulo the size, so
/// the pattern looks as if its cell had been drawn at every position. Otherwise the tile is the period in pattern space at the page's
/// scale and is sampled bilinearly through the inverse mapping.
/// </summary>
internal sealed class PatternTile(int width, int height, Rgba32[] pixels, bool aligned, double xStep, double yStep)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public Rgba32[] Pixels { get; } = pixels;

    public bool Aligned { get; } = aligned;

    public double XStep { get; } = xStep;

    public double YStep { get; } = yStep;

    public long PixelCount => (long)Width * Height;
}

/// <summary>Paints with a <see cref="PatternTile"/> repeated over the page.</summary>
internal sealed class TileBrush : Brush
{
    private readonly PatternTile _tile;
    private readonly int _originX, _originY;
    private readonly double _a, _b, _c, _d, _e, _f; // inverse of pattern -> device

    public TileBrush(PatternTile tile, in PdfMatrix patternToDevice)
    {
        _tile = tile;
        _originX = (int)Math.Floor(patternToDevice.E);
        _originY = (int)Math.Floor(patternToDevice.F);
        double det = patternToDevice.Determinant;
        if (!(Math.Abs(det) > 1e-18) || !double.IsFinite(det))
        {
            _a = _d = 1;
            return;
        }

        double ia = patternToDevice.D / det, ib = -patternToDevice.B / det, ic = -patternToDevice.C / det, id = patternToDevice.A / det;
        _a = ia;
        _b = ib;
        _c = ic;
        _d = id;
        _e = -((patternToDevice.E * ia) + (patternToDevice.F * ic));
        _f = -((patternToDevice.E * ib) + (patternToDevice.F * id));
    }

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        int n = rgba.Length / 4;
        int w = _tile.Width, h = _tile.Height;
        Rgba32[] data = _tile.Pixels;
        const float k = 1f / 255f;
        if (_tile.Aligned)
        {
            int ty = Mod(y - _originY, h) * w;
            for (int i = 0; i < n; i++)
            {
                Rgba32 p = data[ty + Mod(x + i - _originX, w)];
                int o = i * 4;
                rgba[o] = p.R * k;
                rgba[o + 1] = p.G * k;
                rgba[o + 2] = p.B * k;
                rgba[o + 3] = p.A * k;
            }

            return;
        }

        double py = y + 0.5;
        double xs = _tile.XStep, ys = _tile.YStep;
        for (int i = 0; i < n; i++)
        {
            double px = x + i + 0.5;
            double u = (px * _a) + (py * _c) + _e;
            double v = (px * _b) + (py * _d) + _f;
            int o = i * 4;
            if (!(Math.Abs(u) < 1e12 && Math.Abs(v) < 1e12))
            {
                rgba.Slice(o, 4).Clear();
                continue;
            }

            // Period position in tile pixels, sampled bilinearly with wrap-around so the repeats join without a seam.
            double tu = ((u - (Math.Floor(u / xs) * xs)) / xs * w) - 0.5;
            double tv = ((v - (Math.Floor(v / ys) * ys)) / ys * h) - 0.5;
            double fu0 = Math.Floor(tu), fv0 = Math.Floor(tv);
            float fx = (float)(tu - fu0), fy = (float)(tv - fv0);
            int x0 = Mod((int)fu0, w), x1 = Mod((int)fu0 + 1, w), y0 = Mod((int)fv0, h), y1 = Mod((int)fv0 + 1, h);
            Rgba32 pa = data[(y0 * w) + x0], pb = data[(y0 * w) + x1], pc = data[(y1 * w) + x0], pd = data[(y1 * w) + x1];
            float wa = (1 - fx) * (1 - fy), wb = fx * (1 - fy), wc = (1 - fx) * fy, wd = fx * fy;
            float aa = pa.A * wa, ab = pb.A * wb, ac = pc.A * wc, ad = pd.A * wd;
            float alpha = aa + ab + ac + ad;
            if (alpha <= 0)
            {
                rgba.Slice(o, 4).Clear();
                continue;
            }

            float inv = k / alpha;
            rgba[o] = ((pa.R * aa) + (pb.R * ab) + (pc.R * ac) + (pd.R * ad)) * inv;
            rgba[o + 1] = ((pa.G * aa) + (pb.G * ab) + (pc.G * ac) + (pd.G * ad)) * inv;
            rgba[o + 2] = ((pa.B * aa) + (pb.B * ab) + (pc.B * ac) + (pd.B * ad)) * inv;
            rgba[o + 3] = alpha * k;
        }
    }

    private static int Mod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
    }
}

internal sealed partial class CanvasDevice
{
    private TileBrush? TilingPatternBrush(PdfTilingPattern pattern, in PdfColor color)
    {
        if (pattern.Cell is null)
        {
            return null;
        }

        PdfMatrix t = pattern.Matrix.Then(_view);
        if (!t.IsFinite)
        {
            return null;
        }

        Rgba32? tint = null;
        if (!pattern.IsColored)
        {
            PdfColorSpace? under = color.Space.PatternBase;
            tint = under is null ? new Rgba32(0, 0, 0, 255) : PdfColors.ToRgba(under, color.C0, color.C1, color.C2, color.C3);
        }

        double xs = Math.Abs(pattern.XStep), ys = Math.Abs(pattern.YStep);
        double sx = Math.Sqrt((t.A * t.A) + (t.B * t.B)), sy = Math.Sqrt((t.C * t.C) + (t.D * t.D));
        if (!(xs > 1e-9 && ys > 1e-9 && sx > 1e-9 && sy > 1e-9) || !double.IsFinite(xs + ys + sx + sy))
        {
            return null;
        }

        int max = Limits.MaxPatternTileSide;
        double pw = xs * sx, ph = ys * sy;
        bool axisAligned = Math.Abs(t.B) < 1e-6 * sx && Math.Abs(t.C) < 1e-6 * sy;
        bool aligned = axisAligned && NearInt(pw) && NearInt(ph) && pw <= max && ph <= max && pw >= 0.5 && ph >= 0.5;

        // The tile depends on the pattern's fractional offset on the page only in the aligned layout.
        int fx = 0, fy = 0;
        if (aligned)
        {
            fx = (int)Math.Round((t.E - Math.Floor(t.E)) * 32) & 31;
            fy = (int)Math.Round((t.F - Math.Floor(t.F)) * 32) & 31;
            if (Math.Round((t.E - Math.Floor(t.E)) * 32) >= 32)
            {
                fx = 0;
            }

            if (Math.Round((t.F - Math.Floor(t.F)) * 32) >= 32)
            {
                fy = 0;
            }
        }

        uint tintKey = tint is { } tc ? (uint)((tc.A << 24) | (tc.R << 16) | (tc.G << 8) | tc.B) : 0;
        var key = new TileKey(pattern, Q(t.A), Q(t.B), Q(t.C), Q(t.D), fx, fy, tintKey, aligned);
        PatternTile? tile = _page.GetTile(key, () => BuildTile(pattern, t, tint, xs, ys, aligned, fx, fy, pw, ph, sx, sy));
        return tile is null ? null : new TileBrush(tile, t);
    }

    private static bool NearInt(double v) => Math.Abs(v - Math.Round(v)) < 1e-3 * Math.Max(1, v);

    private static long Q(double v) => (long)Math.Round(v * 1000);

    private PatternTile? BuildTile(PdfTilingPattern pattern, PdfMatrix t, Rgba32? tint, double xs, double ys, bool aligned, int fx, int fy, double pw, double ph, double sx, double sy)
    {
        CheckNesting();
        int max = Limits.MaxPatternTileSide;
        int tw, th;
        PdfMatrix view; // pattern space to tile pixels
        if (aligned)
        {
            tw = (int)Math.Round(pw);
            th = (int)Math.Round(ph);
            view = new PdfMatrix(t.A, t.B, t.C, t.D, fx / 32.0, fy / 32.0);
        }
        else
        {
            tw = (int)Math.Clamp(Math.Ceiling(pw), 1, max);
            th = (int)Math.Clamp(Math.Ceiling(ph), 1, max);
            view = new PdfMatrix(tw / xs, 0, 0, th / ys, 0, 0);
        }

        if (tw < 1 || th < 1 || (long)tw * th > (long)max * max)
        {
            return null;
        }

        // Copies of the cell whose box overlaps the part of pattern space the tile shows.
        if (!Invert(view, out PdfMatrix viewInverse))
        {
            return null;
        }

        PdfBounds shown = viewInverse.TransformBounds(new PdfBounds(0, 0, tw, th));
        PdfBounds bb = pattern.BBox;
        int i0 = (int)Math.Ceiling((shown.Left - bb.Right) / xs - 1e-9), i1 = (int)Math.Floor((shown.Right - bb.Left) / xs + 1e-9);
        int j0 = (int)Math.Ceiling((shown.Bottom - bb.Top) / ys - 1e-9), j1 = (int)Math.Floor((shown.Top - bb.Bottom) / ys + 1e-9);
        if (!(i1 >= i0 && j1 >= j0))
        {
            return new PatternTile(tw, th, new Rgba32[tw * th], aligned, xs, ys);
        }

        int maxCopies = Limits.MaxPatternCopies;
        while ((long)(i1 - i0 + 1) * (j1 - j0 + 1) > maxCopies)
        {
            if (i1 - i0 >= j1 - j0)
            {
                if (i1 > 0) i1--; else i0++;
            }
            else if (j1 > 0)
            {
                j1--;
            }
            else
            {
                j0++;
            }
        }

        using var canvas = new Canvas(tw, th, PixelFormat.Rgba8);
        for (int j = j0; j <= j1; j++)
        {
            for (int i = i0; i <= i1; i++)
            {
                _ct.ThrowIfCancellationRequested();
                PdfMatrix copyView = PdfMatrix.Translation(i * xs, j * ys).Then(view);
                if (!Invert(copyView, out PdfMatrix inv))
                {
                    return null;
                }

                PdfBounds cull = inv.TransformBounds(new PdfBounds(-1.5, -1.5, tw + 1.5, th + 1.5));
                var device = new CanvasDevice(_page, copyView, canvas, _depth + _frames.Count + 1, tint, _ct);
                try
                {
                    pattern.Cell!.Replay(device, cull);
                }
                finally
                {
                    device.Dispose();
                }
            }
        }

        var pixels = new Rgba32[tw * th];
        canvas.CopyTo<Rgba32>(pixels, new ImageRect(0, 0, tw, th));
        return new PatternTile(tw, th, pixels, aligned, xs, ys);
    }

    internal static bool Invert(in PdfMatrix m, out PdfMatrix inv)
    {
        double det = m.Determinant;
        if (!(Math.Abs(det) > 1e-18) || !double.IsFinite(det))
        {
            inv = default;
            return false;
        }

        double a = m.D / det, b = -m.B / det, c = -m.C / det, d = m.A / det;
        inv = new PdfMatrix(a, b, c, d, -((m.E * a) + (m.F * c)), -((m.E * b) + (m.F * d)));
        return true;
    }
}
