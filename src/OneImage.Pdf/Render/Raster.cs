using System.Buffers;
using OnePdf;

namespace OneImage.Pdf.Render;

/// <summary>
/// A decoded PDF image as 8-bit straight RGBA. For a stencil mask the color channels are zero and alpha is the coverage; the fill color is
/// supplied when it is painted.
/// </summary>
internal sealed class Raster
{
    public Raster(int width, int height, Rgba32[] pixels, bool stencil)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Stencil = stencil;
    }

    public int Width { get; }

    public int Height { get; }

    public Rgba32[] Pixels { get; }

    public bool Stencil { get; }

    public long PixelCount => (long)Width * Height;

    /// <summary>
    /// Builds the raster of a decoded image. Returns null when the image has no pixels (not decoded, nothing to show). The size has been
    /// checked against the limit by the caller, so the allocation here is bounded.
    /// </summary>
    public static Raster? FromData(PdfImageData data, CancellationToken ct)
    {
        if (!data.IsDecoded || data.Width < 1 || data.Height < 1)
        {
            return null;
        }

        if (data.IsStencil)
        {
            PdfImageAlpha? coverage = data.Alpha;
            if (coverage is null)
            {
                return null;
            }

            ReadOnlySpan<byte> v = coverage.Values.Span;
            var px = new Rgba32[(long)coverage.Width * coverage.Height];
            for (int i = 0; i < px.Length && i < v.Length; i++)
            {
                px[i] = new Rgba32(0, 0, 0, v[i]);
            }

            return new Raster(coverage.Width, coverage.Height, px, stencil: true);
        }

        PdfColorSpace? space = data.ColorSpace;
        if (space is null)
        {
            return null;
        }

        int w = data.Width, h = data.Height;
        int n = space.ComponentCount;
        ReadOnlySpan<byte> samples = data.Samples.Span;
        if (samples.Length < (long)w * h * n)
        {
            return null;
        }

        var pixels = new Rgba32[(long)w * h];
        for (int y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            PdfColors.ConvertRow(space, samples.Slice(y * w * n, w * n), pixels.AsSpan(y * w, w), w);
        }

        var raster = new Raster(w, h, pixels, stencil: false);
        if (data.Alpha is { } alpha && alpha.Width > 0 && alpha.Height > 0)
        {
            raster = raster.WithAlpha(alpha);
        }

        return raster;
    }

    /// <summary>Combines the color pixels with a soft mask that may have another size: both are brought to the larger of the two sizes.</summary>
    private Raster WithAlpha(PdfImageAlpha alpha)
    {
        ReadOnlySpan<byte> av = alpha.Values.Span;
        if (av.Length < (long)alpha.Width * alpha.Height)
        {
            return this;
        }

        int tw = Math.Max(Width, alpha.Width), th = Math.Max(Height, alpha.Height);
        Rgba32[] color = (tw == Width && th == Height) ? Pixels : Resize.Bilinear(Pixels, Width, Height, tw, th);
        var result = new Rgba32[(long)tw * th];
        for (int y = 0; y < th; y++)
        {
            // The mask is placed over the unit square just like the color: pixel centers map proportionally.
            int ay = Math.Min(alpha.Height - 1, (int)(((long)y * alpha.Height) / th));
            for (int x = 0; x < tw; x++)
            {
                int ax = Math.Min(alpha.Width - 1, (int)(((long)x * alpha.Width) / tw));
                Rgba32 c = color[(y * tw) + x];
                result[(y * tw) + x] = new Rgba32(c.R, c.G, c.B, av[(ay * alpha.Width) + ax]);
            }
        }

        return new Raster(tw, th, result, stencil: false);
    }
}

/// <summary>Resampling of RGBA pictures: area averaging for reduction, bilinear for enlargement. Color is weighted by alpha, so transparent pixels do not tint their neighbours.</summary>
internal static class Resize
{
    /// <summary>Area average to a smaller (or equal) size, streaming over the source rows in memory proportional to the destination row.</summary>
    public static Rgba32[] Area(Rgba32[] src, int sw, int sh, int dw, int dh, bool stencil, CancellationToken ct)
    {
        var dst = new Rgba32[(long)dw * dh];
        float[] rowAcc = ArrayPool<float>.Shared.Rent(dw * 4);
        float[] acc0 = ArrayPool<float>.Shared.Rent(dw * 4);
        float[] acc1 = ArrayPool<float>.Shared.Rent(dw * 4);
        try
        {
            Array.Clear(acc0, 0, dw * 4);
            Array.Clear(acc1, 0, dw * 4);
            double yScale = (double)dh / sh;
            double xScale = (double)dw / sw;
            int curDst = 0;
            for (int y = 0; y < sh; y++)
            {
                if ((y & 63) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                HorizontalRow(src.AsSpan(y * sw, sw), rowAcc, dw, xScale);
                double top = y * yScale, bottom = (y + 1) * yScale;
                int d0 = Math.Min((int)top, dh - 1);
                int d1 = Math.Min((int)(bottom - 1e-9), dh - 1);
                if (d0 != curDst)
                {
                    // Rows above d0 are finished: only the last accumulator can still be open.
                    Flush(acc0, dst, curDst, dw, stencil);
                    (acc0, acc1) = (acc1, acc0);
                    Array.Clear(acc1, 0, dw * 4);
                    curDst = d0;
                }

                double wA = d0 == d1 ? bottom - top : (d0 + 1) - top;
                Accumulate(acc0, rowAcc, dw, (float)wA);
                if (d1 != d0)
                {
                    double wB = bottom - d1;
                    Accumulate(acc1, rowAcc, dw, (float)wB);
                }
            }

            Flush(acc0, dst, curDst, dw, stencil);
            if (curDst + 1 < dh)
            {
                Flush(acc1, dst, curDst + 1, dw, stencil);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rowAcc);
            ArrayPool<float>.Shared.Return(acc0);
            ArrayPool<float>.Shared.Return(acc1);
        }

        return dst;
    }

    private static void HorizontalRow(ReadOnlySpan<Rgba32> row, float[] acc, int dw, double xScale)
    {
        Array.Clear(acc, 0, dw * 4);
        for (int x = 0; x < row.Length; x++)
        {
            double left = x * xScale, right = (x + 1) * xScale;
            int d0 = Math.Min((int)left, dw - 1);
            int d1 = Math.Min((int)(right - 1e-9), dw - 1);
            Rgba32 p = row[x];
            float a = p.A * (1f / 255f);
            float pr = p.R * a, pg = p.G * a, pb = p.B * a;
            double wA = d0 == d1 ? right - left : (d0 + 1) - left;
            float fa = (float)wA;
            int o = d0 * 4;
            acc[o] += pr * fa;
            acc[o + 1] += pg * fa;
            acc[o + 2] += pb * fa;
            acc[o + 3] += a * fa;
            if (d1 != d0)
            {
                float fb = (float)(right - d1);
                o = d1 * 4;
                acc[o] += pr * fb;
                acc[o + 1] += pg * fb;
                acc[o + 2] += pb * fb;
                acc[o + 3] += a * fb;
            }
        }
    }

    private static void Accumulate(float[] acc, float[] row, int dw, float weight)
    {
        for (int i = 0; i < dw * 4; i++)
        {
            acc[i] += row[i] * weight;
        }
    }

    private static void Flush(float[] acc, Rgba32[] dst, int row, int dw, bool stencil)
    {
        int o = row * dw;
        for (int x = 0; x < dw; x++)
        {
            float a = acc[(x * 4) + 3];
            if (a <= 1e-6f)
            {
                dst[o + x] = default;
                continue;
            }

            // The sums hold color in 0..255 times alpha in 0..1, so dividing by alpha and by 255 gives the 0..1 range ToByte expects.
            float inv = 1f / (a * 255f);
            dst[o + x] = stencil
                ? new Rgba32(0, 0, 0, ToByte(a))
                : new Rgba32(ToByte(acc[x * 4] * inv), ToByte(acc[(x * 4) + 1] * inv), ToByte(acc[(x * 4) + 2] * inv), ToByte(a));
        }
    }

    private static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)((v * 255f) + 0.5f);

    /// <summary>Bilinear enlargement with pixel centers aligned (used to bring a smaller picture to the size of its mask).</summary>
    public static Rgba32[] Bilinear(Rgba32[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new Rgba32[(long)dw * dh];
        for (int y = 0; y < dh; y++)
        {
            double sy = (((y + 0.5) * sh) / dh) - 0.5;
            int y0 = (int)Math.Floor(sy);
            float fy = (float)(sy - y0);
            int ya = Math.Clamp(y0, 0, sh - 1), yb = Math.Clamp(y0 + 1, 0, sh - 1);
            for (int x = 0; x < dw; x++)
            {
                double sx = (((x + 0.5) * sw) / dw) - 0.5;
                int x0 = (int)Math.Floor(sx);
                float fx = (float)(sx - x0);
                int xa = Math.Clamp(x0, 0, sw - 1), xb = Math.Clamp(x0 + 1, 0, sw - 1);
                Rgba32 p00 = src[(ya * sw) + xa], p10 = src[(ya * sw) + xb], p01 = src[(yb * sw) + xa], p11 = src[(yb * sw) + xb];
                float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
                dst[(y * dw) + x] = new Rgba32(
                    ToByte(((p00.R * w00) + (p10.R * w10) + (p01.R * w01) + (p11.R * w11)) / 255f),
                    ToByte(((p00.G * w00) + (p10.G * w10) + (p01.G * w01) + (p11.G * w11)) / 255f),
                    ToByte(((p00.B * w00) + (p10.B * w10) + (p01.B * w01) + (p11.B * w11)) / 255f),
                    ToByte(((p00.A * w00) + (p10.A * w10) + (p01.A * w01) + (p11.A * w11)) / 255f));
            }
        }

        return dst;
    }
}
