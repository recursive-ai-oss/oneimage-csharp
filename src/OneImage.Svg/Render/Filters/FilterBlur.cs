using System.Buffers;

namespace OneImage.Svg.Render;

/// <summary>
/// Gaussian blur of premultiplied float RGBA rows, the way the filter specification defines <c>feGaussianBlur</c>: for a standard deviation of
/// 2 pixels or more, three successive box blurs of a size derived from it (with the two-box-plus-one arrangement for even sizes); below
/// that, a true Gaussian kernel. Everything outside the buffer is transparent. Each axis is handled alone, and a deviation of zero or less
/// leaves that axis as it is.
/// </summary>
internal static class FilterBlur
{
    // Larger deviations are clamped; the effect is already a flat wash by then and the sizes stay inside int range.
    private const double MaxSigma = 1e6;

    /// <summary>The box size for a deviation (filter effects specification, <c>feGaussianBlur</c>): floor(σ · 3 · √(2π) / 4 + 0.5).</summary>
    internal static int BoxSize(double sigma) => (int)Math.Floor((Math.Min(sigma, MaxSigma) * 3 * Math.Sqrt(2 * Math.PI) / 4) + 0.5);

    /// <summary>How many pixels beyond a pixel the blur of that deviation (in pixels) reads.</summary>
    public static int ReachPx(double sigma)
    {
        if (!(sigma > 0))
        {
            return 0;
        }

        if (sigma < 2)
        {
            return (int)Math.Ceiling(sigma * 3);
        }

        int d = BoxSize(sigma);
        return 3 * ((d / 2) + 1);
    }

    /// <summary>
    /// Blurs <paramref name="a"/> (<paramref name="w"/> x <paramref name="h"/> RGBA floats) with the given deviations in pixels.
    /// <paramref name="b"/> is scratch of the same size. Returns the array that holds the result (<paramref name="a"/> or <paramref name="b"/>).
    /// </summary>
    public static float[] Apply(float[] a, float[] b, int w, int h, double sigmaX, double sigmaY)
    {
        float[] src = a;
        float[] dst = b;
        if (sigmaX > 0)
        {
            Axis(ref src, ref dst, w, h, sigmaX, horizontal: true);
        }

        if (sigmaY > 0)
        {
            Axis(ref src, ref dst, w, h, sigmaY, horizontal: false);
        }

        return src;
    }

    private static void Axis(ref float[] src, ref float[] dst, int w, int h, double sigma, bool horizontal)
    {
        if (sigma < 2)
        {
            double[] kernel = Kernel(sigma);
            if (horizontal)
            {
                GaussH(src, dst, w, h, kernel);
            }
            else
            {
                GaussV(src, dst, w, h, kernel);
            }

            (src, dst) = (dst, src);
            return;
        }

        int d = BoxSize(sigma);
        if (d < 1)
        {
            d = 1;
        }

        Span<(int Size, int Left)> boxes = stackalloc (int, int)[3];
        if ((d & 1) == 1)
        {
            boxes[0] = (d, d / 2);
            boxes[1] = (d, d / 2);
            boxes[2] = (d, d / 2);
        }
        else
        {
            // Even size: one box centered between the pixel and its left neighbour, one between it and its right neighbour, then an odd box of d + 1.
            boxes[0] = (d, d / 2);
            boxes[1] = (d, (d / 2) - 1);
            boxes[2] = (d + 1, d / 2);
        }

        foreach ((int size, int left) in boxes)
        {
            if (horizontal)
            {
                BoxH(src, dst, w, h, size, left);
            }
            else
            {
                BoxV(src, dst, w, h, size, left);
            }

            (src, dst) = (dst, src);
        }
    }

    private static double[] Kernel(double sigma)
    {
        int r = Math.Max((int)Math.Ceiling(sigma * 3), 1);
        var k = new double[(2 * r) + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++)
        {
            double v = Math.Exp(-(i * (double)i) / (2 * sigma * sigma));
            k[i + r] = v;
            sum += v;
        }

        for (int i = 0; i < k.Length; i++)
        {
            k[i] /= sum;
        }

        return k;
    }

    private static void GaussH(float[] src, float[] dst, int w, int h, double[] kernel)
    {
        int r = kernel.Length / 2;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                int k0 = Math.Max(-r, -x);
                int k1 = Math.Min(r, w - 1 - x);
                for (int k = k0; k <= k1; k++)
                {
                    double wk = kernel[k + r];
                    int o = row + ((x + k) * 4);
                    s0 += wk * src[o];
                    s1 += wk * src[o + 1];
                    s2 += wk * src[o + 2];
                    s3 += wk * src[o + 3];
                }

                int p = row + (x * 4);
                dst[p] = (float)s0;
                dst[p + 1] = (float)s1;
                dst[p + 2] = (float)s2;
                dst[p + 3] = (float)s3;
            }
        }
    }

    private static void GaussV(float[] src, float[] dst, int w, int h, double[] kernel)
    {
        int r = kernel.Length / 2;
        int stride = w * 4;
        double[] acc = ArrayPool<double>.Shared.Rent(stride);
        try
        {
            for (int y = 0; y < h; y++)
            {
                Array.Clear(acc, 0, stride);
                int k0 = Math.Max(-r, -y);
                int k1 = Math.Min(r, h - 1 - y);
                for (int k = k0; k <= k1; k++)
                {
                    double wk = kernel[k + r];
                    int o = (y + k) * stride;
                    for (int i = 0; i < stride; i++)
                    {
                        acc[i] += wk * src[o + i];
                    }
                }

                int p = y * stride;
                for (int i = 0; i < stride; i++)
                {
                    dst[p + i] = (float)acc[i];
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(acc);
        }
    }

    // out[i] = mean of src[i - left .. i - left + size - 1], zero outside. A running sum in double keeps the result independent of where the
    // buffer starts to well below one level.
    private static void BoxH(float[] src, float[] dst, int w, int h, int size, int left)
    {
        int hi = size - 1 - left;
        double inv = 1.0 / size;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            int init = Math.Min(w - 1, hi);
            for (int k = 0; k <= init; k++)
            {
                int o = row + (k * 4);
                s0 += src[o];
                s1 += src[o + 1];
                s2 += src[o + 2];
                s3 += src[o + 3];
            }

            for (int i = 0; i < w; i++)
            {
                int p = row + (i * 4);
                dst[p] = (float)(s0 * inv);
                dst[p + 1] = (float)(s1 * inv);
                dst[p + 2] = (float)(s2 * inv);
                dst[p + 3] = (float)(s3 * inv);
                int add = i + 1 + hi;
                if (add < w)
                {
                    int o = row + (add * 4);
                    s0 += src[o];
                    s1 += src[o + 1];
                    s2 += src[o + 2];
                    s3 += src[o + 3];
                }

                int rem = i - left;
                if (rem >= 0)
                {
                    int o = row + (rem * 4);
                    s0 -= src[o];
                    s1 -= src[o + 1];
                    s2 -= src[o + 2];
                    s3 -= src[o + 3];
                }
            }
        }
    }

    private static void BoxV(float[] src, float[] dst, int w, int h, int size, int left)
    {
        int hi = size - 1 - left;
        double inv = 1.0 / size;
        int stride = w * 4;
        double[] acc = ArrayPool<double>.Shared.Rent(stride);
        try
        {
            Array.Clear(acc, 0, stride);
            int init = Math.Min(h - 1, hi);
            for (int k = 0; k <= init; k++)
            {
                int o = k * stride;
                for (int i = 0; i < stride; i++)
                {
                    acc[i] += src[o + i];
                }
            }

            for (int y = 0; y < h; y++)
            {
                int p = y * stride;
                for (int i = 0; i < stride; i++)
                {
                    dst[p + i] = (float)(acc[i] * inv);
                }

                int add = y + 1 + hi;
                if (add < h)
                {
                    int o = add * stride;
                    for (int i = 0; i < stride; i++)
                    {
                        acc[i] += src[o + i];
                    }
                }

                int rem = y - left;
                if (rem >= 0)
                {
                    int o = rem * stride;
                    for (int i = 0; i < stride; i++)
                    {
                        acc[i] -= src[o + i];
                    }
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(acc);
        }
    }
}
