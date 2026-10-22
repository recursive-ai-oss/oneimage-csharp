using System.Numerics;

namespace OneImage.Kernels;

/// <summary>Rank and edge kernels: the 8-bit sliding-histogram median, the generic median, and the Sobel magnitude row.</summary>
internal static class FilterRankKernel
{
    /// <summary>
    /// Median of every (2r+1) x (2r+1) window of interleaved 8-bit samples, channel by channel (Huang's sliding histogram).
    /// <paramref name="src"/> is the padded input: <c>height + 2r</c> rows of <c>width + 2r</c> pixels with row stride
    /// <paramref name="srcStride"/> bytes. For each output row a 256-bin histogram of the first window is built, then the window
    /// slides right by removing one column and adding one, while the median bin moves only as far as the counts require.
    /// The window holds an odd number of samples, so the median is the exact middle value and every correct algorithm agrees.
    /// </summary>
    public static void MedianU8(ReadOnlySpan<byte> src, int srcStride, int channels, int radius, Span<byte> dst, int dstStride, int width, int height)
    {
        int win = (2 * radius) + 1;
        if (src.Length < (long)(height + (2 * radius) - 1) * srcStride + ((long)(width + (2 * radius)) * channels)
            || srcStride < (width + (2 * radius)) * channels
            || dst.Length < ((long)(height - 1) * dstStride) + ((long)width * channels)
            || dstStride < width * channels)
        {
            throw new ArgumentException("Median buffers are too small.");
        }

        int rank = (win * win) / 2;
        Span<int> hist = stackalloc int[256];
        for (int c = 0; c < channels; c++)
        {
            for (int y = 0; y < height; y++)
            {
                hist.Clear();
                for (int wy = 0; wy < win; wy++)
                {
                    int row = ((y + wy) * srcStride) + c;
                    for (int wx = 0; wx < win; wx++)
                    {
                        hist[src[row + (wx * channels)]]++;
                    }
                }

                int m = 0;
                int below = 0;
                while (below + hist[m] <= rank)
                {
                    below += hist[m];
                    m++;
                }

                int outIndex = (y * dstStride) + c;
                dst[outIndex] = (byte)m;
                for (int x = 1; x < width; x++)
                {
                    int colOut = ((x - 1) * channels) + c;
                    int colIn = ((x + win - 1) * channels) + c;
                    for (int wy = 0; wy < win; wy++)
                    {
                        int row = (y + wy) * srcStride;
                        int v = src[row + colOut];
                        hist[v]--;
                        if (v < m)
                        {
                            below--;
                        }

                        v = src[row + colIn];
                        hist[v]++;
                        if (v < m)
                        {
                            below++;
                        }
                    }

                    while (below > rank)
                    {
                        m--;
                        below -= hist[m];
                    }

                    while (below + hist[m] <= rank)
                    {
                        below += hist[m];
                        m++;
                    }

                    dst[outIndex + (x * channels)] = (byte)m;
                }
            }
        }
    }

    /// <summary>
    /// Median of every window of float samples (used for 16-bit and floating-point images), by sorting the window. Same buffer
    /// layout as <see cref="MedianU8"/> with the strides counted in samples. NaN sorts before every number.
    /// </summary>
    public static void MedianFloat(ReadOnlySpan<float> src, int srcStride, int channels, int radius, Span<float> dst, int dstStride, int width, int height, Span<float> window)
    {
        int win = (2 * radius) + 1;
        if (window.Length < win * win
            || src.Length < (long)(height + (2 * radius) - 1) * srcStride + ((long)(width + (2 * radius)) * channels)
            || srcStride < (width + (2 * radius)) * channels
            || dst.Length < ((long)(height - 1) * dstStride) + ((long)width * channels)
            || dstStride < width * channels)
        {
            throw new ArgumentException("Median buffers are too small.");
        }

        Span<float> w = window[..(win * win)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int n = 0;
                    for (int wy = 0; wy < win; wy++)
                    {
                        int row = ((y + wy) * srcStride) + ((x * channels) + c);
                        for (int wx = 0; wx < win; wx++)
                        {
                            w[n++] = src[row + (wx * channels)];
                        }
                    }

                    w.Sort();
                    dst[(y * dstStride) + (x * channels) + c] = w[n / 2];
                }
            }
        }
    }

    /// <summary>
    /// One row of Sobel magnitudes, <c>sqrt(gx^2 + gy^2) * factor</c>, from three rows of a gray plane that carry one extra
    /// sample on each side (so each is <c>dst.Length + 2</c> long). gx and gy are the usual 3x3 Sobel sums
    /// ([1 2 1] smoothing across, [-1 0 1] difference along).
    /// </summary>
    public static void SobelRow(ReadOnlySpan<float> above, ReadOnlySpan<float> middle, ReadOnlySpan<float> below, float factor, Span<float> dst)
    {
        int n = dst.Length;
        if (above.Length < n + 2 || middle.Length < n + 2 || below.Length < n + 2)
        {
            throw new ArgumentException("Sobel rows are too short.");
        }

        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vc = Vector<float>.Count;
            var two = new Vector<float>(2f);
            var f = new Vector<float>(factor);
            for (; x + vc <= n; x += vc)
            {
                var a0 = new Vector<float>(above.Slice(x, vc));
                var a1 = new Vector<float>(above.Slice(x + 1, vc));
                var a2 = new Vector<float>(above.Slice(x + 2, vc));
                var m0 = new Vector<float>(middle.Slice(x, vc));
                var m2 = new Vector<float>(middle.Slice(x + 2, vc));
                var b0 = new Vector<float>(below.Slice(x, vc));
                var b1 = new Vector<float>(below.Slice(x + 1, vc));
                var b2 = new Vector<float>(below.Slice(x + 2, vc));
                Vector<float> gx = (a2 - a0) + ((m2 - m0) * two) + (b2 - b0);
                Vector<float> gy = (b0 + (b1 * two) + b2) - (a0 + (a1 * two) + a2);
                (Vector.SquareRoot((gx * gx) + (gy * gy)) * f).CopyTo(dst.Slice(x, vc));
            }
        }

        for (; x < n; x++)
        {
            float gx = (above[x + 2] - above[x]) + ((middle[x + 2] - middle[x]) * 2f) + (below[x + 2] - below[x]);
            float gy = (below[x] + (below[x + 1] * 2f) + below[x + 2]) - (above[x] + (above[x + 1] * 2f) + above[x + 2]);
            dst[x] = MathF.Sqrt((gx * gx) + (gy * gy)) * factor;
        }
    }
}
