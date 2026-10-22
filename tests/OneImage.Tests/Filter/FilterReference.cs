using Xunit;

namespace OneImage.Tests.Filter;

/// <summary>A double-precision whole-image reference for the neighbourhood filters, written independently of the tiled implementation.</summary>
internal static class FilterReference
{
    public static double Lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    public static double Enc(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;

    /// <summary>Stored bytes to premultiplied working doubles (linear light when asked), channels interleaved.</summary>
    public static double[] Decode(byte[] raw, int channels, bool alpha, bool linear)
    {
        int n = raw.Length / channels;
        double[] w = new double[raw.Length];
        for (int i = 0; i < n; i++)
        {
            double a = alpha ? raw[(i * channels) + channels - 1] / 255.0 : 1.0;
            int colors = alpha ? channels - 1 : channels;
            for (int k = 0; k < colors; k++)
            {
                double v = raw[(i * channels) + k] / 255.0;
                w[(i * channels) + k] = (linear ? Lin(v) : v) * a;
            }

            if (alpha)
            {
                w[(i * channels) + colors] = a;
            }
        }

        return w;
    }

    public static byte[] Encode(double[] w, int channels, bool alpha, bool linear)
    {
        int n = w.Length / channels;
        byte[] raw = new byte[w.Length];
        for (int i = 0; i < n; i++)
        {
            double a = alpha ? w[(i * channels) + channels - 1] : 1.0;
            int colors = alpha ? channels - 1 : channels;
            for (int k = 0; k < colors; k++)
            {
                double v = a > 0 ? w[(i * channels) + k] / a : 0;
                v = Math.Clamp(v, 0, 1);
                raw[(i * channels) + k] = (byte)Math.Clamp(Math.Floor(((linear ? Enc(v) : v) * 255) + 0.5), 0, 255);
            }

            if (alpha)
            {
                raw[(i * channels) + colors] = (byte)Math.Clamp(Math.Floor((a * 255) + 0.5), 0, 255);
            }
        }

        return raw;
    }

    public static int Map(int i, int n, EdgeMode mode)
    {
        if (i >= 0 && i < n)
        {
            return i;
        }

        if (mode == EdgeMode.Clamp)
        {
            return Math.Clamp(i, 0, n - 1);
        }

        if (mode == EdgeMode.Mirror)
        {
            int m = ((i % (2 * n)) + (2 * n)) % (2 * n);
            return m >= n ? (2 * n) - 1 - m : m;
        }

        return -1;
    }

    /// <summary>Direct 2-D correlation, kernel[ky, kx] weighting the pixel at (x + kx - cx, y + ky - cy); offset is added to color lanes only.</summary>
    public static double[] Correlate(double[] w, int width, int height, int channels, bool alpha, double[,] kernel, double offset, EdgeMode edge)
    {
        int kh = kernel.GetLength(0);
        int kw = kernel.GetLength(1);
        double[] r = new double[w.Length];
        int colors = alpha ? channels - 1 : channels;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int k = 0; k < channels; k++)
                {
                    double s = 0;
                    for (int j = 0; j < kh; j++)
                    {
                        int yy = Map(y + j - (kh / 2), height, edge);
                        for (int i = 0; i < kw; i++)
                        {
                            int xx = Map(x + i - (kw / 2), width, edge);
                            if (yy >= 0 && xx >= 0)
                            {
                                s += kernel[j, i] * w[((((yy * width) + xx) * channels)) + k];
                            }
                        }
                    }

                    r[((((y * width) + x) * channels)) + k] = s + (k < colors ? offset : 0);
                }
            }
        }

        return r;
    }

    /// <summary>Separable correlation with 1-D kernels (cheap for wide kernels).</summary>
    public static double[] Separable(double[] w, int width, int height, int channels, double[] horizontal, double[] vertical, EdgeMode edge)
    {
        double[] t = new double[w.Length];
        int rx = horizontal.Length / 2;
        int ry = vertical.Length / 2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int k = 0; k < channels; k++)
                {
                    double s = 0;
                    for (int i = 0; i < horizontal.Length; i++)
                    {
                        int xx = Map(x + i - rx, width, edge);
                        if (xx >= 0)
                        {
                            s += horizontal[i] * w[((((y * width) + xx) * channels)) + k];
                        }
                    }

                    t[((((y * width) + x) * channels)) + k] = s;
                }
            }
        }

        double[] r = new double[w.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int k = 0; k < channels; k++)
                {
                    double s = 0;
                    for (int j = 0; j < vertical.Length; j++)
                    {
                        int yy = Map(y + j - ry, height, edge);
                        if (yy >= 0)
                        {
                            s += vertical[j] * t[((((yy * width) + x) * channels)) + k];
                        }
                    }

                    r[((((y * width) + x) * channels)) + k] = s;
                }
            }
        }

        return r;
    }

    public static double[] Gaussian(double sigma)
    {
        if (sigma <= 0)
        {
            return [1];
        }

        int r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        double[] k = new double[(2 * r) + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++)
        {
            k[i + r] = Math.Exp(-(double)i * i / (2 * sigma * sigma));
            sum += k[i + r];
        }

        for (int i = 0; i < k.Length; i++)
        {
            k[i] /= sum;
        }

        return k;
    }

    public static (int Max, double Mean) Diff(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0;
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            max = Math.Max(max, d);
            sum += d;
        }

        return (max, (double)sum / a.Length);
    }

    /// <summary>A deterministic test picture: smooth gradients, hard edges, noise and (optionally) varying alpha including fully transparent areas.</summary>
    public static byte[] Picture(int w, int h, int channels, bool alpha, int seed = 5)
    {
        var rnd = new Random(seed);
        byte[] raw = new byte[w * h * channels];
        int colors = alpha ? channels - 1 : channels;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * channels;
                bool box = x > w / 3 && x < w / 2 && y > h / 4 && y < h / 2;
                for (int k = 0; k < colors; k++)
                {
                    double v = 128 + (90 * Math.Sin((x + (k * 7)) / 13.0) * Math.Cos(y / 9.0)) + rnd.Next(-12, 13);
                    raw[i + k] = (byte)(box ? 250 - (k * 40) : Math.Clamp(v, 0, 255));
                }

                if (alpha)
                {
                    raw[i + colors] = (byte)(x < w / 5 ? 0 : (box ? 255 : 40 + ((x * 215) / w)));
                }
            }
        }

        return raw;
    }

    public static PixelFormat Fmt(int channels, bool alpha) => channels switch
    {
        1 => PixelFormat.Gray8,
        2 => PixelFormat.GrayAlpha8,
        3 => PixelFormat.Rgb8,
        _ => alpha ? PixelFormat.Rgba8 : PixelFormat.Cmyk8,
    };
}
