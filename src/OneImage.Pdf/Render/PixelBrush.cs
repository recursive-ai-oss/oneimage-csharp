using OneImage.Drawing;
using OnePdf;

namespace OneImage.Pdf.Render;

/// <summary>
/// Paints a raster picture through a placement matrix. Sampling is bilinear on alpha-weighted color or the nearest pixel, with the border
/// pixels extended, so the fill's own anti-aliased edge gives the picture its soft border. A stencil raster takes its color from
/// <c>stencil</c> and its alpha from the coverage.
/// </summary>
internal sealed class PixelBrush : Brush
{
    private readonly Raster _raster;
    private readonly double _a, _b, _c, _d, _e, _f; // inverse: device pixel -> picture pixel
    private readonly bool _nearest;
    private readonly Rgba32 _stencil;

    /// <param name="raster">The picture.</param>
    /// <param name="placement">Maps picture pixel coordinates to device pixel coordinates.</param>
    /// <param name="nearest">Take the nearest pixel instead of interpolating.</param>
    /// <param name="stencil">The color of a stencil raster.</param>
    public PixelBrush(Raster raster, in PdfMatrix placement, bool nearest, Rgba32 stencil)
    {
        _raster = raster;
        _nearest = nearest;
        _stencil = stencil;
        double det = placement.Determinant;
        if (!(Math.Abs(det) > 1e-18) || !double.IsFinite(det))
        {
            _a = _d = 1;
            _e = _f = 1e12;
            return;
        }

        double ia = placement.D / det, ib = -placement.B / det, ic = -placement.C / det, id = placement.A / det;
        _a = ia;
        _b = ib;
        _c = ic;
        _d = id;
        _e = -((placement.E * ia) + (placement.F * ic));
        _f = -((placement.E * ib) + (placement.F * id));
    }

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        Rgba32[] data = _raster.Pixels;
        int w = _raster.Width, h = _raster.Height;
        bool stencil = _raster.Stencil;
        double py = y + 0.5;
        int n = rgba.Length / 4;
        const float k = 1f / 255f;
        for (int i = 0; i < n; i++)
        {
            double px = x + i + 0.5;
            double u = (px * _a) + (py * _c) + _e;
            double v = (px * _b) + (py * _d) + _f;
            int o = i * 4;
            if (!(Math.Abs(u) < 1e9 && Math.Abs(v) < 1e9))
            {
                rgba.Slice(o, 4).Clear();
                continue;
            }

            if (_nearest)
            {
                Rgba32 p = data[(Math.Clamp((int)Math.Floor(v), 0, h - 1) * w) + Math.Clamp((int)Math.Floor(u), 0, w - 1)];
                if (stencil)
                {
                    Put(rgba, o, _stencil.R * k, _stencil.G * k, _stencil.B * k, p.A * k);
                }
                else
                {
                    Put(rgba, o, p.R * k, p.G * k, p.B * k, p.A * k);
                }

                continue;
            }

            double su = u - 0.5, sv = v - 0.5;
            double fu0 = Math.Floor(su), fv0 = Math.Floor(sv);
            float fx = (float)(su - fu0), fy = (float)(sv - fv0);
            int x0 = Math.Clamp((int)fu0, 0, w - 1), x1 = Math.Clamp((int)fu0 + 1, 0, w - 1);
            int y0 = Math.Clamp((int)fv0, 0, h - 1), y1 = Math.Clamp((int)fv0 + 1, 0, h - 1);
            Rgba32 pa = data[(y0 * w) + x0], pb = data[(y0 * w) + x1], pc = data[(y1 * w) + x0], pd = data[(y1 * w) + x1];
            float wa = (1 - fx) * (1 - fy), wb = fx * (1 - fy), wc = (1 - fx) * fy, wd = fx * fy;
            float aa = pa.A * wa, ab = pb.A * wb, ac = pc.A * wc, ad = pd.A * wd;
            float alpha = aa + ab + ac + ad;
            if (alpha <= 0)
            {
                rgba.Slice(o, 4).Clear();
                continue;
            }

            if (stencil)
            {
                Put(rgba, o, _stencil.R * k, _stencil.G * k, _stencil.B * k, alpha * k);
                continue;
            }

            // Interpolate premultiplied so a transparent pixel's color does not bleed into its neighbours.
            float inv = k / alpha;
            Put(
                rgba,
                o,
                ((pa.R * aa) + (pb.R * ab) + (pc.R * ac) + (pd.R * ad)) * inv,
                ((pa.G * aa) + (pb.G * ab) + (pc.G * ac) + (pd.G * ad)) * inv,
                ((pa.B * aa) + (pb.B * ab) + (pc.B * ac) + (pd.B * ad)) * inv,
                alpha * k);
        }
    }

    private static void Put(Span<float> rgba, int o, float r, float g, float b, float a)
    {
        rgba[o] = r;
        rgba[o + 1] = g;
        rgba[o + 2] = b;
        rgba[o + 3] = a;
    }
}
