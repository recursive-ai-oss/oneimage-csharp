using System.Numerics;
using OneImage.Drawing;
using OnePdf;

namespace OneImage.Pdf.Render;

/// <summary>A shading's color along its parameter, tabulated once: 1025 straight sRGB triples between the ends of the parameter range.</summary>
internal sealed class ShadingLut
{
    public const int Size = 1024;

    private readonly float[] _rgb = new float[(Size + 1) * 3];

    public ShadingLut(PdfShading shading, double t0, double t1)
    {
        T0 = t0;
        T1 = t1;
        int n = shading.ColorComponentCount;
        Span<double> color = stackalloc double[32];
        Span<double> p = stackalloc double[1];
        PdfColorSpace space = shading.FinalSpace;
        for (int i = 0; i <= Size; i++)
        {
            p[0] = t0 + ((t1 - t0) * i / Size);
            shading.Evaluate(p, color);
            Rgba32 c = PdfColors.ToRgba(space, color[..n]);
            _rgb[i * 3] = c.R * (1f / 255f);
            _rgb[(i * 3) + 1] = c.G * (1f / 255f);
            _rgb[(i * 3) + 2] = c.B * (1f / 255f);
        }
    }

    public double T0 { get; }

    public double T1 { get; }

    /// <summary>Writes the interpolated color for parameter <paramref name="t"/>.</summary>
    public void Lookup(double t, out float r, out float g, out float b)
    {
        double s = T1 == T0 ? 0 : (t - T0) / (T1 - T0);
        s = Math.Clamp(s, 0, 1) * Size;
        int i = Math.Min((int)s, Size - 1);
        float f = (float)(s - i);
        int o = i * 3;
        r = _rgb[o] + ((_rgb[o + 3] - _rgb[o]) * f);
        g = _rgb[o + 1] + ((_rgb[o + 4] - _rgb[o + 1]) * f);
        b = _rgb[o + 2] + ((_rgb[o + 5] - _rgb[o + 2]) * f);
    }
}

/// <summary>
/// Paints a PDF shading (types 1 to 7) in a cell. Function-based, axial and radial shadings are evaluated per pixel center; mesh shadings
/// are rasterized once into a buffer of the cell's size. Pixels the shading does not cover are the background color when there is one
/// (shading patterns), otherwise transparent.
/// </summary>
internal sealed class ShadingBrush : Brush
{
    private readonly PdfShading _shading;
    private readonly PdfMatrix _inverse;
    private readonly bool _invertible;
    private readonly ShadingLut? _lut;
    private readonly float[]? _mesh;
    private readonly int _meshWidth;
    private readonly Rgba32? _background;

    /// <param name="shading">The shading.</param>
    /// <param name="toDevice">Maps the shading's target space to cell pixels.</param>
    /// <param name="lut">The tabulated colors for axial and radial shadings.</param>
    /// <param name="background">The color where the shading paints nothing, or null for transparent.</param>
    /// <param name="cellWidth">Width of the cell, for meshes.</param>
    /// <param name="cellHeight">Height of the cell, for meshes.</param>
    public ShadingBrush(PdfShading shading, in PdfMatrix toDevice, ShadingLut? lut, Rgba32? background, int cellWidth, int cellHeight)
    {
        _shading = shading;
        _lut = lut;
        _background = background;
        double det = toDevice.Determinant;
        _invertible = det != 0 && double.IsFinite(det);
        if (_invertible)
        {
            double a = toDevice.D / det, b = -toDevice.B / det, c = -toDevice.C / det, d = toDevice.A / det;
            _inverse = new PdfMatrix(a, b, c, d, -((toDevice.E * a) + (toDevice.F * c)), -((toDevice.E * b) + (toDevice.F * d)));
        }

        if (shading.ShadingType >= 4 && shading.Mesh is { } mesh)
        {
            _meshWidth = cellWidth;
            _mesh = MeshRaster.Render(shading, mesh, toDevice, cellWidth, cellHeight);
        }
    }

    public override void FillRow(int x, int y, Span<float> rgba)
    {
        int n = rgba.Length / 4;
        if (_mesh is not null)
        {
            for (int i = 0; i < n; i++)
            {
                int px = x + i;
                int o = i * 4;
                if ((uint)px < (uint)_meshWidth && (uint)y < (uint)(_mesh.Length / (_meshWidth * 4)))
                {
                    int s = ((y * _meshWidth) + px) * 4;
                    if (_mesh[s + 3] > 0)
                    {
                        rgba[o] = _mesh[s];
                        rgba[o + 1] = _mesh[s + 1];
                        rgba[o + 2] = _mesh[s + 2];
                        rgba[o + 3] = 1;
                        continue;
                    }
                }

                Background(rgba, o);
            }

            return;
        }

        if (!_invertible)
        {
            for (int i = 0; i < n; i++)
            {
                Background(rgba, i * 4);
            }

            return;
        }

        PdfBounds? box = _shading.BBox;
        int comps = _shading.ColorComponentCount;
        Span<double> color = stackalloc double[32];
        double py = y + 0.5;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            double px = x + i + 0.5;
            double sx = (px * _inverse.A) + (py * _inverse.C) + _inverse.E;
            double sy = (px * _inverse.B) + (py * _inverse.D) + _inverse.F;
            if (box is { } b && !(sx >= b.Left && sx <= b.Right && sy >= b.Bottom && sy <= b.Top))
            {
                Background(rgba, o);
                continue;
            }

            if (_lut is not null)
            {
                if (_shading.TryGetParameter(sx, sy, out double t))
                {
                    _lut.Lookup(t, out rgba[o], out rgba[o + 1], out rgba[o + 2]);
                    rgba[o + 3] = 1;
                }
                else
                {
                    Background(rgba, o);
                }

                continue;
            }

            if (_shading.EvaluateAt(sx, sy, color))
            {
                Rgba32 c = PdfColors.ToRgba(_shading.FinalSpace, color[..comps]);
                rgba[o] = c.R * (1f / 255f);
                rgba[o + 1] = c.G * (1f / 255f);
                rgba[o + 2] = c.B * (1f / 255f);
                rgba[o + 3] = 1;
            }
            else
            {
                Background(rgba, o);
            }
        }
    }

    private void Background(Span<float> rgba, int o)
    {
        if (_background is { } bg)
        {
            rgba[o] = bg.R * (1f / 255f);
            rgba[o + 1] = bg.G * (1f / 255f);
            rgba[o + 2] = bg.B * (1f / 255f);
            rgba[o + 3] = 1;
        }
        else
        {
            rgba[o] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
        }
    }
}

/// <summary>Rasterizes the triangles of a mesh shading (types 4 to 7) at pixel centers with Gouraud interpolation.</summary>
internal static class MeshRaster
{
    public static float[] Render(PdfShading shading, PdfShadingMesh mesh, in PdfMatrix toDevice, int width, int height)
    {
        var buffer = new float[width * height * 4];
        ReadOnlySpan<double> verts = mesh.Vertices.Span;
        ReadOnlySpan<int> tris = mesh.Triangles.Span;
        int stride = mesh.VertexStride;
        int pc = stride - 2;
        if (pc < 1 || stride <= 0)
        {
            return buffer;
        }

        int vc = Math.Min(mesh.VertexCount, verts.Length / stride);
        bool useFunction = shading.HasFunction;
        ShadingLut? lut = null;
        float[]? vertexRgb = null;
        if (useFunction)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int i = 0; i < vc; i++)
            {
                double t = verts[(i * stride) + 2];
                lo = Math.Min(lo, t);
                hi = Math.Max(hi, t);
            }

            if (vc == 0)
            {
                return buffer;
            }

            lut = new ShadingLut(shading, lo, hi);
        }
        else
        {
            // Vertices carry color components: convert each vertex once and interpolate in RGB.
            vertexRgb = new float[vc * 3];
            Span<double> comps = stackalloc double[32];
            Span<double> fin = stackalloc double[32];
            for (int i = 0; i < vc; i++)
            {
                ReadOnlySpan<double> p = verts.Slice((i * stride) + 2, pc);
                shading.Evaluate(p, fin);
                Rgba32 c = PdfColors.ToRgba(shading.FinalSpace, fin[..shading.ColorComponentCount]);
                vertexRgb[i * 3] = c.R * (1f / 255f);
                vertexRgb[(i * 3) + 1] = c.G * (1f / 255f);
                vertexRgb[(i * 3) + 2] = c.B * (1f / 255f);
            }

            _ = comps;
        }

        int triCount = Math.Min(mesh.TriangleCount, tris.Length / 3);
        for (int t = 0; t < triCount; t++)
        {
            int i0 = tris[t * 3], i1 = tris[(t * 3) + 1], i2 = tris[(t * 3) + 2];
            if ((uint)i0 >= (uint)vc || (uint)i1 >= (uint)vc || (uint)i2 >= (uint)vc)
            {
                continue;
            }

            (double x0, double y0) = toDevice.Transform(verts[i0 * stride], verts[(i0 * stride) + 1]);
            (double x1, double y1) = toDevice.Transform(verts[i1 * stride], verts[(i1 * stride) + 1]);
            (double x2, double y2) = toDevice.Transform(verts[i2 * stride], verts[(i2 * stride) + 1]);
            double minX = Math.Min(x0, Math.Min(x1, x2)), maxX = Math.Max(x0, Math.Max(x1, x2));
            double minY = Math.Min(y0, Math.Min(y1, y2)), maxY = Math.Max(y0, Math.Max(y1, y2));
            if (!double.IsFinite(minX + maxX + minY + maxY) || maxX < 0 || maxY < 0 || minX > width || minY > height)
            {
                continue;
            }

            double area = ((x1 - x0) * (y2 - y0)) - ((x2 - x0) * (y1 - y0));
            if (Math.Abs(area) < 1e-12)
            {
                continue;
            }

            int px0 = Math.Max(0, (int)Math.Floor(minX - 0.5));
            int px1 = Math.Min(width - 1, (int)Math.Ceiling(maxX - 0.5));
            int py0 = Math.Max(0, (int)Math.Floor(minY - 0.5));
            int py1 = Math.Min(height - 1, (int)Math.Ceiling(maxY - 0.5));
            double inv = 1 / area;
            for (int py = py0; py <= py1; py++)
            {
                double cy = py + 0.5;
                for (int px = px0; px <= px1; px++)
                {
                    double cx = px + 0.5;
                    double w1 = (((cx - x0) * (y2 - y0)) - ((x2 - x0) * (cy - y0))) * inv;
                    double w2 = (((x1 - x0) * (cy - y0)) - ((cx - x0) * (y1 - y0))) * inv;
                    double w0 = 1 - w1 - w2;
                    const double Eps = -1e-7;
                    if (w0 < Eps || w1 < Eps || w2 < Eps)
                    {
                        continue;
                    }

                    int o = ((py * width) + px) * 4;
                    if (lut is not null)
                    {
                        double tp = (w0 * verts[(i0 * stride) + 2]) + (w1 * verts[(i1 * stride) + 2]) + (w2 * verts[(i2 * stride) + 2]);
                        lut.Lookup(tp, out buffer[o], out buffer[o + 1], out buffer[o + 2]);
                    }
                    else
                    {
                        float f0 = (float)w0, f1 = (float)w1, f2 = (float)w2;
                        buffer[o] = (vertexRgb![i0 * 3] * f0) + (vertexRgb[i1 * 3] * f1) + (vertexRgb[i2 * 3] * f2);
                        buffer[o + 1] = (vertexRgb[(i0 * 3) + 1] * f0) + (vertexRgb[(i1 * 3) + 1] * f1) + (vertexRgb[(i2 * 3) + 1] * f2);
                        buffer[o + 2] = (vertexRgb[(i0 * 3) + 2] * f0) + (vertexRgb[(i1 * 3) + 2] * f1) + (vertexRgb[(i2 * 3) + 2] * f2);
                    }

                    buffer[o + 3] = 1;
                }
            }
        }

        return buffer;
    }
}
