using System.Numerics;
using OneImage.Drawing;

namespace OneImage.Svg.Render;

/// <summary>
/// Paints a raster picture through a placement matrix, sampling bilinearly (or the nearest pixel) with the border pixels extended, so that
/// the edge of a fill that only partly covers a border pixel still gets picture color and the fill's anti-aliasing gives the soft edge.
/// </summary>
internal sealed class SampledBrush : Brush
{
    private readonly SampledPixels _pixels;
    private readonly Matrix3x2 _inverse;
    private readonly bool _nearest;

    /// <param name="pixels">The picture.</param>
    /// <param name="placement">Maps picture pixel coordinates to canvas pixel coordinates (row vectors).</param>
    /// <param name="nearest">Take the nearest pixel instead of interpolating.</param>
    public SampledBrush(SampledPixels pixels, Matrix3x2 placement, bool nearest)
    {
        _pixels = pixels;
        _nearest = nearest;
        if (!Matrix3x2.Invert(placement, out _inverse))
        {
            _inverse = Matrix3x2.Identity;
        }
    }

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        Rgba32[] data = _pixels.Pixels;
        int w = _pixels.Width;
        int h = _pixels.Height;
        Matrix3x2 m = _inverse;
        double py = y + 0.5;
        int n = rgba.Length / 4;
        const float k = 1f / 255f;
        for (int i = 0; i < n; i++)
        {
            double px = x + i + 0.5;
            double u = (px * m.M11) + (py * m.M21) + m.M31;
            double v = (px * m.M12) + (py * m.M22) + m.M32;
            int o = i * 4;
            if (!(Math.Abs(u) < 1e9 && Math.Abs(v) < 1e9))
            {
                rgba.Slice(o, 4).Clear();
                continue;
            }

            if (_nearest)
            {
                Rgba32 p = data[(Math.Clamp((int)Math.Floor(v), 0, h - 1) * w) + Math.Clamp((int)Math.Floor(u), 0, w - 1)];
                rgba[o] = p.R * k;
                rgba[o + 1] = p.G * k;
                rgba[o + 2] = p.B * k;
                rgba[o + 3] = p.A * k;
                continue;
            }

            // Sample positions are pixel centers, so the four neighbors of (u, v) start half a pixel up and left.
            double su = u - 0.5;
            double sv = v - 0.5;
            double fu0 = Math.Floor(su);
            double fv0 = Math.Floor(sv);
            float fx = (float)(su - fu0);
            float fy = (float)(sv - fv0);
            int x0 = Math.Clamp((int)fu0, 0, w - 1);
            int x1 = Math.Clamp((int)fu0 + 1, 0, w - 1);
            int y0 = Math.Clamp((int)fv0, 0, h - 1);
            int y1 = Math.Clamp((int)fv0 + 1, 0, h - 1);
            Rgba32 a = data[(y0 * w) + x0];
            Rgba32 b = data[(y0 * w) + x1];
            Rgba32 c = data[(y1 * w) + x0];
            Rgba32 d = data[(y1 * w) + x1];
            float wa = (1 - fx) * (1 - fy);
            float wb = fx * (1 - fy);
            float wc = (1 - fx) * fy;
            float wd = fx * fy;

            // Interpolate premultiplied so a transparent pixel's color does not bleed into its neighbors.
            float aa = a.A * wa, ab = b.A * wb, ac = c.A * wc, ad = d.A * wd;
            float alpha = aa + ab + ac + ad;
            if (alpha <= 0)
            {
                rgba.Slice(o, 4).Clear();
                continue;
            }

            float inv = 1f / alpha;
            rgba[o] = ((a.R * aa) + (b.R * ab) + (c.R * ac) + (d.R * ad)) * inv * k;
            rgba[o + 1] = ((a.G * aa) + (b.G * ab) + (c.G * ac) + (d.G * ad)) * inv * k;
            rgba[o + 2] = ((a.B * aa) + (b.B * ab) + (c.B * ac) + (d.B * ad)) * inv * k;
            rgba[o + 3] = alpha * k;
        }
    }
}
