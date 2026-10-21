using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneImage.Kernels;

/// <summary>Filter weight lookup for the affine sampler: the kernels of <see cref="FilterFunctions"/> tabulated and linearly interpolated.</summary>
internal static class AffineFilterTable
{
    /// <summary>Table entries per unit of distance. The interpolation error stays below 1e-5, far under half a code at 16 bits.</summary>
    public const int Resolution = 2048;

    private static readonly float[][] s_tables = new float[8][];

    /// <summary>Taps on each side of the sample position: 1 for bilinear, 2 for cubic and Lanczos2, 3 for Lanczos3, 0 for nearest.</summary>
    public static int Support(ResamplingFilter filter) => filter switch
    {
        ResamplingFilter.Nearest or ResamplingFilter.Box => 0,
        ResamplingFilter.Bilinear => 1,
        ResamplingFilter.Lanczos3 => 3,
        _ => 2,
    };

    public static float[] Get(ResamplingFilter filter)
    {
        int index = (int)filter;
        float[]? t = Volatile.Read(ref s_tables[index]);
        if (t != null)
        {
            return t;
        }

        int support = Support(filter);
        t = new float[(support * Resolution) + 2];
        for (int i = 0; i < t.Length; i++)
        {
            double d = (double)i / Resolution;
            t[i] = support == 0 || d >= support ? 0f : (float)FilterFunctions.Evaluate(filter, d);
        }

        Volatile.Write(ref s_tables[index], t);
        return t;
    }

    /// <summary>
    /// Fills the 2S tap weights for a sample at fractional offset <paramref name="frac"/> past its base pixel (taps at base-S+1 .. base+S).
    /// Bilinear and the cubics are evaluated from their polynomials; the sinc kernels come from the table.
    /// </summary>
    public static void Fill(ResamplingFilter filter, ReadOnlySpan<float> table, int support, float frac, Span<float> w)
    {
        switch (filter)
        {
            case ResamplingFilter.Bilinear:
                w[0] = 1f - frac;
                w[1] = frac;
                return;
            case ResamplingFilter.CatmullRom:
                Cubic(0f, 0.5f, frac, w);
                return;
            case ResamplingFilter.Mitchell:
                Cubic(1f / 3f, 1f / 3f, frac, w);
                return;
            default:
                for (int t = 0; t < 2 * support; t++)
                {
                    w[t] = Weight(table, (t - support + 1) - frac);
                }

                return;
        }
    }

    // Mitchell-Netravali (B, C) cubic at the four tap distances 1+t, t, 1-t, 2-t.
    private static void Cubic(float b, float c, float t, Span<float> w)
    {
        float a3 = (12f - (9f * b) - (6f * c)) / 6f;
        float a2 = (-18f + (12f * b) + (6f * c)) / 6f;
        float a0 = (6f - (2f * b)) / 6f;
        float b3 = (-b - (6f * c)) / 6f;
        float b2 = ((6f * b) + (30f * c)) / 6f;
        float b1 = ((-12f * b) - (48f * c)) / 6f;
        float b0 = ((8f * b) + (24f * c)) / 6f;
        float u = 1f - t;
        float p = 1f + t;
        float q = 2f - t;
        w[0] = (((((b3 * p) + b2) * p) + b1) * p) + b0;
        w[1] = ((((a3 * t) + a2) * t) * t) + a0;
        w[2] = ((((a3 * u) + a2) * u) * u) + a0;
        w[3] = (((((b3 * q) + b2) * q) + b1) * q) + b0;
    }

    public static float Weight(ReadOnlySpan<float> table, float distance)
    {
        float pos = Math.Abs(distance) * Resolution;
        int i = (int)pos;
        if (i >= table.Length - 1)
        {
            return 0f;
        }

        float f = pos - i;
        return table[i] + ((table[i + 1] - table[i]) * f);
    }
}

/// <summary>
/// The inverse-mapping sampler behind rotate and affine. For each output pixel it computes the source position in double
/// precision from the absolute output coordinates (so any tile split gives the same position), takes 2S x 2S taps with
/// normalized weights, resolves taps outside the source by the edge mode, and sums in a fixed order without fused
/// multiply-add, so the vector and scalar paths produce identical floats.
/// </summary>
internal static class AffineKernel
{
    public const int MaxTaps = 6;

    /// <summary>Maps a possibly out-of-range index into [0, n), or returns -1 for <see cref="EdgeMode.Constant"/> outside the source.</summary>
    public static int MapIndex(int i, int n, EdgeMode mode)
    {
        if ((uint)i < (uint)n)
        {
            return i;
        }

        switch (mode)
        {
            case EdgeMode.Clamp:
                return i < 0 ? 0 : n - 1;
            case EdgeMode.Mirror:
                // Mirror repeats the edge pixel: ... c b a | a b c | c b a ...
                int m = (int)(((long)i % (2L * n)) + (2L * n)) % (2 * n);
                return m >= n ? (2 * n) - 1 - m : m;
            case EdgeMode.Wrap:
                return (int)((((long)i % n) + n) % n);
            default:
                return -1;
        }
    }

    /// <summary>
    /// Computes <paramref name="count"/> output pixels of one row starting at absolute output column <paramref name="x0"/>.
    /// The source position of column x is (rowX + x * stepX, rowY + x * stepY) in pixel-index space. The region holds source
    /// pixels [regionX, regionX + regionWidth) x [regionY, regionY + regionHeight) as float pixels of <paramref name="stride"/>
    /// floats.
    /// </summary>
    public static void SampleRow(
        ReadOnlySpan<float> region,
        int regionWidth,
        int regionHeight,
        int regionX,
        int regionY,
        int sourceWidth,
        int sourceHeight,
        int stride,
        EdgeMode edge,
        ReadOnlySpan<float> background,
        ResamplingFilter filter,
        double rowX,
        double rowY,
        double stepX,
        double stepY,
        int x0,
        int count,
        Span<float> output)
    {
        if (stride is not (1 or 2 or 4) || background.Length < stride || output.Length < (long)count * stride || region.Length < (long)regionWidth * regionHeight * stride)
        {
            throw new ArgumentException("Sampler buffers do not match the layout.");
        }

        if (regionWidth == 0 || regionHeight == 0)
        {
            // No source pixel is reachable from this row (constant edge): every output pixel is the background.
            for (int x = 0; x < count; x++)
            {
                background[..stride].CopyTo(output.Slice(x * stride, stride));
            }

            return;
        }

        int support = AffineFilterTable.Support(filter);
        ReadOnlySpan<float> table = AffineFilterTable.Get(filter);
        Span<float> wx = stackalloc float[MaxTaps];
        Span<float> wy = stackalloc float[MaxTaps];
        Span<int> ix = stackalloc int[MaxTaps];
        Span<int> iy = stackalloc int[MaxTaps];
        int taps = support == 0 ? 1 : 2 * support;
        Vector128<float> bg4 = stride == 4 ? Vector128.Create(background[0], background[1], background[2], background[3]) : default;
        for (int x = 0; x < count; x++)
        {
            double fx = x0 + x;
            double px = rowX + (fx * stepX);
            double py = rowY + (fx * stepY);
            if (support == 0)
            {
                wx[0] = 1f;
                wy[0] = 1f;
                ix[0] = Resolve((int)Math.Floor(px + 0.5), sourceWidth, regionX, regionWidth, edge);
                iy[0] = Resolve((int)Math.Floor(py + 0.5), sourceHeight, regionY, regionHeight, edge);
            }
            else
            {
                int bx = (int)Math.Floor(px);
                int by = (int)Math.Floor(py);
                float ax = (float)(px - bx);
                float ay = (float)(py - by);
                AffineFilterTable.Fill(filter, table, support, ax, wx);
                AffineFilterTable.Fill(filter, table, support, ay, wy);
                float sx = 0f;
                float sy = 0f;
                for (int t = 0; t < taps; t++)
                {
                    sx += wx[t];
                    sy += wy[t];
                }

                float rx = 1f / sx;
                float ry = 1f / sy;
                for (int t = 0; t < taps; t++)
                {
                    wx[t] *= rx;
                    wy[t] *= ry;
                }

                if (stride == 4 && bx - support + 1 >= 0 && bx + support < sourceWidth && by - support + 1 >= 0 && by + support < sourceHeight)
                {
                    // Every tap is inside the source: no edge handling, one bounds check for the whole 2S x 2S block.
                    int cx = bx - support + 1 - regionX;
                    int cy = by - support + 1 - regionY;
                    if (cx < 0 || cy < 0 || cx + taps > regionWidth || cy + taps > regionHeight)
                    {
                        throw new ImageOperationException("Sampler taps fell outside the prepared source region.");
                    }

                    ref float r0 = ref MemoryMarshal.GetReference(region);
                    Vector128<float> inner = Vector128<float>.Zero;
                    for (int j = 0; j < taps; j++)
                    {
                        nuint at = (nuint)(((((cy + j) * regionWidth) + cx)) * 4);
                        Vector128<float> h = Vector128.LoadUnsafe(ref r0, at) * Vector128.Create(wx[0]);
                        for (int i = 1; i < taps; i++)
                        {
                            h += Vector128.LoadUnsafe(ref r0, at + (nuint)(i * 4)) * Vector128.Create(wx[i]);
                        }

                        inner += h * Vector128.Create(wy[j]);
                    }

                    inner.StoreUnsafe(ref MemoryMarshal.GetReference(output), (nuint)(x * 4));
                    continue;
                }

                for (int t = 0; t < taps; t++)
                {
                    int off = t - support + 1;
                    ix[t] = Resolve(bx + off, sourceWidth, regionX, regionWidth, edge);
                    iy[t] = Resolve(by + off, sourceHeight, regionY, regionHeight, edge);
                }
            }

            ref float outRef = ref MemoryMarshal.GetReference(output.Slice(x * stride, stride));
            if (stride == 4)
            {
                Vector128<float> acc = Vector128<float>.Zero;
                for (int j = 0; j < taps; j++)
                {
                    Vector128<float> h;
                    if (iy[j] < 0)
                    {
                        h = bg4;
                    }
                    else
                    {
                        h = Vector128<float>.Zero;
                        int rowBase = iy[j] * regionWidth;
                        for (int i = 0; i < taps; i++)
                        {
                            Vector128<float> v = ix[i] < 0 ? bg4 : Vector128.Create(region.Slice((rowBase + ix[i]) * 4, 4));
                            h += v * Vector128.Create(wx[i]);
                        }
                    }

                    acc += h * Vector128.Create(wy[j]);
                }

                acc.StoreUnsafe(ref outRef);
            }
            else
            {
                for (int k = 0; k < stride; k++)
                {
                    float acc = 0f;
                    for (int j = 0; j < taps; j++)
                    {
                        float h;
                        if (iy[j] < 0)
                        {
                            h = background[k];
                        }
                        else
                        {
                            h = 0f;
                            int rowBase = iy[j] * regionWidth;
                            for (int i = 0; i < taps; i++)
                            {
                                float v = ix[i] < 0 ? background[k] : region[(((rowBase + ix[i]) * stride) + k)];
                                h += v * wx[i];
                            }
                        }

                        acc += h * wy[j];
                    }

                    Unsafe.Add(ref outRef, k) = acc;
                }
            }
        }
    }

    // Maps a source index through the edge mode and makes it relative to the region, or -1 for the background.
    private static int Resolve(int i, int n, int regionStart, int regionLength, EdgeMode edge)
    {
        int m = MapIndex(i, n, edge);
        if (m < 0)
        {
            return -1;
        }

        int r = m - regionStart;
        if ((uint)r >= (uint)regionLength)
        {
            throw new ImageOperationException("Sampler tap fell outside the prepared source region.");
        }

        return r;
    }
}
