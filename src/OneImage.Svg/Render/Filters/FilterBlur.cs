using System.Buffers;
using OneImage.Kernels;

namespace OneImage.Svg.Render;

/// <summary>
/// Gaussian blur of premultiplied float RGBA, the way the filter specification defines <c>feGaussianBlur</c>: for a standard deviation of
/// 2 pixels or more, three successive box blurs of a size derived from it (with the two-box-plus-one arrangement for even sizes); below
/// that, a true Gaussian kernel. Everything outside the buffer is transparent. Each axis is handled alone, and a deviation of zero or less
/// leaves that axis as it is.
/// </summary>
/// <remarks>
/// The sums run in double precision with the same operations in the same order as <see cref="FilterBlurReference"/> (the scalar reference), and
/// every pass rounds to float, so the result is bit for bit the reference's. What differs is the layout: lines (rows, or columns read in place with
/// a stride) go through all three boxes in line-sized scratch, so a full-size buffer is read and written once per axis instead of once per box;
/// four lines run together; lines that are entirely zero are skipped (their blur is zero); large buffers are cut into bands that run in parallel.
/// The blur is in place, so no second full-size buffer is needed. Without AVX the scalar reference runs.
/// </remarks>
internal static class FilterBlur
{
    /// <summary>Pixels (width x height) from which the bands of a pass run in parallel.</summary>
    private const long ParallelPixels = 1L << 17;

    /// <summary>Largest number of threads one blur uses (the engine's default per-call degree).</summary>
    private static readonly int Degree = Math.Max(1, Math.Min(4, Environment.ProcessorCount));

    /// <summary>Tests set this to run the scalar reference in place of the vector code.</summary>
    internal static bool ForceScalar { get; set; }

    /// <summary>The box size for a deviation (filter effects specification, <c>feGaussianBlur</c>): floor(σ · 3 · √(2π) / 4 + 0.5).</summary>
    internal static int BoxSize(double sigma) => FilterBlurReference.BoxSize(sigma);

    /// <summary>How many pixels beyond a pixel the blur of that deviation (in pixels) reads.</summary>
    public static int ReachPx(double sigma) => FilterBlurReference.ReachPx(sigma);

    /// <summary>Blurs <paramref name="data"/> (<paramref name="w"/> x <paramref name="h"/> RGBA floats) in place with the given deviations in pixels.</summary>
    public static void Apply(float[] data, int w, int h, double sigmaX, double sigmaY)
    {
        if (w <= 0 || h <= 0 || (!(sigmaX > 0) && !(sigmaY > 0)))
        {
            return;
        }

        // Scratch for four lines is 16 floats per pixel of the longer side; sizes beyond int range use the reference.
        if (ForceScalar || !BlurKernel.IsSupported || (long)Math.Max(w, h) * 16 > int.MaxValue)
        {
            ApplyReference(data, w, h, sigmaX, sigmaY);
            return;
        }

        if (sigmaX > 0)
        {
            Horizontal(data, w, h, sigmaX);
        }

        if (sigmaY > 0)
        {
            Vertical(data, w, h, sigmaY);
        }
    }

    private static void ApplyReference(float[] data, int w, int h, double sigmaX, double sigmaY)
    {
        int n = w * h * 4;
        float[] scratch = Pools<float>.Pool.Rent(n);
        try
        {
            float[] res = FilterBlurReference.Apply(data, scratch, w, h, sigmaX, sigmaY);
            if (!ReferenceEquals(res, data))
            {
                Array.Copy(res, data, n);
            }
        }
        finally
        {
            Pools<float>.Pool.Return(scratch);
        }
    }

    // The boxes of one axis: (size, cells to the left of the pixel) for the three passes; null boxes and a kernel when the deviation is below 2.
    private static void Plan(double sigma, out double[]? kernel, out (int Size, int Left) b0, out (int Size, int Left) b1, out (int Size, int Left) b2)
    {
        if (sigma < 2)
        {
            kernel = FilterBlurReference.Kernel(sigma);
            b0 = b1 = b2 = default;
            return;
        }

        kernel = null;
        int d = Math.Max(FilterBlurReference.BoxSize(sigma), 1);
        if ((d & 1) == 1)
        {
            b0 = b1 = b2 = (d, d / 2);
        }
        else
        {
            // Even size: one box centered between the pixel and its left neighbour, one between it and its right neighbour, then an odd box of d + 1.
            b0 = (d, d / 2);
            b1 = (d, (d / 2) - 1);
            b2 = (d + 1, d / 2);
        }
    }

    private static void ForBands(int count, bool parallel, Action<int> body)
    {
        if (!parallel || count < 2)
        {
            for (int i = 0; i < count; i++)
            {
                body(i);
            }

            return;
        }

        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Degree }, body);
    }

    private static void Horizontal(float[] data, int w, int h, double sigma)
    {
        Plan(sigma, out double[]? kernel, out var b0, out var b1, out var b2);
        int rowLen = w * 4;
        bool parallel = (long)w * h >= ParallelPixels;

        // Bands of rows, a multiple of four so the kernels get full groups of four independent rows.
        int bands = parallel ? Math.Min((h + 3) / 4, Degree * 4) : 1;
        int groupsPerBand = (((h + 3) / 4) + bands - 1) / bands;
        ForBands(bands, parallel, band =>
        {
            int y0 = band * groupsPerBand * 4;
            int y1 = Math.Min(h, y0 + (groupsPerBand * 4));
            float[] ra = Pools<float>.Pool.Rent(4 * rowLen);
            float[] rb = Pools<float>.Pool.Rent(4 * rowLen);
            try
            {
                for (int y = y0; y < y1; y += 4)
                {
                    int lines = Math.Min(4, y1 - y);
                    int o = y * rowLen;
                    if (!BlurKernel.AnyNonZero(data, o, rowLen, 4, lines, w))
                    {
                        continue;
                    }

                    if (kernel is not null)
                    {
                        BlurKernel.Gauss(data, o, rowLen, 4, rb, 0, rowLen, 4, lines, w, kernel);
                        BlurKernel.Copy(rb, 0, rowLen, 4, data, o, rowLen, 4, lines, w);
                        continue;
                    }

                    BlurKernel.Box(data, o, rowLen, 4, rb, 0, rowLen, 4, lines, w, b0.Size, b0.Left);
                    BlurKernel.Box(rb, 0, rowLen, 4, ra, 0, rowLen, 4, lines, w, b1.Size, b1.Left);
                    BlurKernel.Box(ra, 0, rowLen, 4, data, o, rowLen, 4, lines, w, b2.Size, b2.Left);
                }
            }
            finally
            {
                Pools<float>.Pool.Return(ra);
                Pools<float>.Pool.Return(rb);
            }
        });
    }

    private static void Vertical(float[] data, int w, int h, double sigma)
    {
        Plan(sigma, out double[]? kernel, out var b0, out var b1, out var b2);
        int stride = w * 4;
        int lineLen = h * 4;

        // Four neighbouring pixel columns at a time (one cache line per row) go through all three boxes in scratch of 4 x h pixels, read
        // from and written back to the buffer in place; a band of 16 columns is one unit of parallel work.
        const int BandPixels = 16;
        int bandCount = (w + BandPixels - 1) / BandPixels;
        bool parallel = (long)w * h >= ParallelPixels;
        ForBands(bandCount, parallel, band =>
        {
            float[] ra = Pools<float>.Pool.Rent(4 * lineLen);
            float[] rb = Pools<float>.Pool.Rent(4 * lineLen);
            try
            {
                int xEnd = Math.Min(w, (band + 1) * BandPixels);
                for (int x = band * BandPixels; x < xEnd; x += 4)
                {
                    int lines = Math.Min(4, xEnd - x);
                    int o = x * 4;
                    if (!BlurKernel.AnyNonZero(data, o, 4, stride, lines, h))
                    {
                        continue;
                    }

                    if (kernel is not null)
                    {
                        BlurKernel.Gauss(data, o, 4, stride, rb, 0, lineLen, 4, lines, h, kernel);
                        BlurKernel.Copy(rb, 0, lineLen, 4, data, o, 4, stride, lines, h);
                        continue;
                    }

                    BlurKernel.Box(data, o, 4, stride, rb, 0, lineLen, 4, lines, h, b0.Size, b0.Left);
                    BlurKernel.Box(rb, 0, lineLen, 4, ra, 0, lineLen, 4, lines, h, b1.Size, b1.Left);
                    BlurKernel.Box(ra, 0, lineLen, 4, data, o, 4, stride, lines, h, b2.Size, b2.Left);
                }
            }
            finally
            {
                Pools<float>.Pool.Return(ra);
                Pools<float>.Pool.Return(rb);
            }
        });
    }
}
