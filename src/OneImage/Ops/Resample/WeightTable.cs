namespace OneImage;

/// <summary>The standard definitions of the reconstruction kernels.</summary>
internal static class FilterFunctions
{
    /// <summary>Half-width of the filter's support in output-scale units.</summary>
    public static double Support(ResamplingFilter filter) => filter switch
    {
        ResamplingFilter.Nearest or ResamplingFilter.Box => 0.5,
        ResamplingFilter.Bilinear => 1,
        ResamplingFilter.Lanczos5 => 5,
        ResamplingFilter.Lanczos8 => 8,
        ResamplingFilter.Hermite => 1,
        ResamplingFilter.Lanczos3 or ResamplingFilter.Welch => 3,
        _ => 2,
    };

    /// <summary>Language-neutral name of a filter, used in op descriptors.</summary>
    public static string Name(ResamplingFilter f) => f switch
    {
        ResamplingFilter.Nearest => "nearest",
        ResamplingFilter.Box => "box",
        ResamplingFilter.Bilinear => "bilinear",
        ResamplingFilter.CatmullRom => "catmull-rom",
        ResamplingFilter.Mitchell => "mitchell",
        ResamplingFilter.Lanczos2 => "lanczos2",
        ResamplingFilter.Lanczos3 => "lanczos3",
        ResamplingFilter.Hermite => "hermite",
        ResamplingFilter.Lanczos5 => "lanczos5",
        ResamplingFilter.Lanczos8 => "lanczos8",
        ResamplingFilter.Robidoux => "robidoux",
        ResamplingFilter.RobidouxSharp => "robidoux-sharp",
        ResamplingFilter.Spline => "spline",
        ResamplingFilter.Welch => "welch",
        _ => "auto",
    };

    /// <summary>The (B, C) pair of the Mitchell–Netravali cubic a filter is, or null when it is not one.</summary>
    public static (double B, double C)? CubicParameters(ResamplingFilter f) => f switch
    {
        ResamplingFilter.CatmullRom => (0, 0.5),
        ResamplingFilter.Mitchell => (1.0 / 3, 1.0 / 3),
        ResamplingFilter.Hermite => (0, 0),
        ResamplingFilter.Spline => (1, 0),
        // N. Robidoux's optimal cubics, as published with ImageMagick's resize filters: B = 12 / (19 + 9 sqrt 2), C = 113 / (58 + 216 sqrt 2).
        ResamplingFilter.Robidoux => (12 / (19 + (9 * Math.Sqrt(2))), 113 / (58 + (216 * Math.Sqrt(2)))),
        // Sharp variant: B = 6 / (13 + 7 sqrt 2), C = 7 / (2 + 12 sqrt 2).
        ResamplingFilter.RobidouxSharp => (6 / (13 + (7 * Math.Sqrt(2))), 7 / (2 + (12 * Math.Sqrt(2)))),
        _ => null,
    };

    /// <summary>Evaluates the kernel at distance <paramref name="x"/> from the sample center. Box and nearest are 1 on [-0.5, 0.5).</summary>
    public static double Evaluate(ResamplingFilter filter, double x)
    {
        double ax = Math.Abs(x);
        switch (filter)
        {
            case ResamplingFilter.Nearest:
            case ResamplingFilter.Box:
                return x >= -0.5 && x < 0.5 ? 1 : 0;
            case ResamplingFilter.Bilinear:
                return ax < 1 ? 1 - ax : 0;
            case ResamplingFilter.Lanczos2:
                return Lanczos(ax, 2);
            case ResamplingFilter.Lanczos3:
                return Lanczos(ax, 3);
            case ResamplingFilter.Lanczos5:
                return Lanczos(ax, 5);
            case ResamplingFilter.Lanczos8:
                return Lanczos(ax, 8);
            case ResamplingFilter.Welch:
                // Sinc windowed by a Welch parabola over its three lobes: sinc(x) * (1 - (x/3)^2).
                return ax >= 3 ? 0 : Sinc(ax) * (1 - (ax * ax / 9));
            default:
                if (CubicParameters(filter) is var (b, c))
                {
                    return Cubic(ax, b, c);
                }

                throw new ArgumentOutOfRangeException(nameof(filter));
        }
    }

    // Mitchell & Netravali, "Reconstruction filters in computer graphics" (1988), eq. (8).
    private static double Cubic(double ax, double b, double c)
    {
        if (ax < 1)
        {
            return (((12 - (9 * b) - (6 * c)) * ax * ax * ax) + ((-18 + (12 * b) + (6 * c)) * ax * ax) + (6 - (2 * b))) / 6;
        }

        if (ax < 2)
        {
            return (((-b - (6 * c)) * ax * ax * ax) + (((6 * b) + (30 * c)) * ax * ax) + (((-12 * b) - (48 * c)) * ax) + ((8 * b) + (24 * c))) / 6;
        }

        return 0;
    }

    private static double Sinc(double ax)
    {
        if (ax == 0)
        {
            return 1;
        }

        double px = Math.PI * ax;
        return Math.Sin(px) / px;
    }

    private static double Lanczos(double ax, int lobes)
    {
        if (ax == 0)
        {
            return 1;
        }

        if (ax >= lobes)
        {
            return 0;
        }

        double px = Math.PI * ax;
        return lobes * Math.Sin(px) * Math.Sin(px / lobes) / (px * px);
    }
}

/// <summary>
/// For every output index along one axis: the first source index, the number of taps and the normalized weights.
/// Source positions follow the pixel-center convention: output pixel <c>o</c> samples at <c>(o + 0.5) * scale - 0.5</c> in
/// source index space. When shrinking (scale above 1) the kernel is stretched by the scale so it band-limits the signal.
/// Taps that fall outside the source are folded onto the nearest edge pixel (edge replication), so every table entry reads
/// only valid indices and sums to 1. An axis whose size does not change is an exact copy (one tap of weight 1).
/// </summary>
internal sealed class WeightTable
{
    private WeightTable(int[] start, int[] count, int[] offset, float[] weights, int sourceSize)
    {
        Start = start;
        Count = count;
        Offset = offset;
        Weights = weights;
        SourceSize = sourceSize;
    }

    public int[] Start { get; }

    public int[] Count { get; }

    public int[] Offset { get; }

    public float[] Weights { get; }

    public int SourceSize { get; }

    public int OutputSize => Start.Length;

    /// <summary>Builds the table.</summary>
    /// <param name="sourceSize">Samples on the source grid.</param>
    /// <param name="outputSize">Output samples.</param>
    /// <param name="scale">Source samples per output sample (may differ from the size ratio when the source grid is itself a reduced grid).</param>
    /// <param name="filter">A concrete filter (not <see cref="ResamplingFilter.Auto"/>).</param>
    /// <returns>The table.</returns>
    public static WeightTable Build(int sourceSize, int outputSize, double scale, ResamplingFilter filter)
    {
        // Tables are immutable and depend only on these four values; a service making many thumbnails of the same few sizes
        // would otherwise rebuild (and throw away) the same tables for every image.
        var key = (sourceSize, outputSize, BitConverter.DoubleToInt64Bits(scale), filter);
        if (s_cache.TryGetValue(key, out WeightTable? cached))
        {
            return cached;
        }

        WeightTable table = BuildCore(sourceSize, outputSize, scale, filter);
        if (s_cache.Count >= MaxCached)
        {
            s_cache.Clear();
        }

        s_cache[key] = table;
        return table;
    }

    private const int MaxCached = 64;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, long, ResamplingFilter), WeightTable> s_cache = new();

    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static WeightTable BuildCore(int sourceSize, int outputSize, double scale, ResamplingFilter filter)
    {
        var start = new int[outputSize];
        var count = new int[outputSize];
        var offset = new int[outputSize];
        bool identity = sourceSize == outputSize && Math.Abs(scale - 1) < 1e-12;
        // Sized for the expected taps per output so the list does not grow by doubling (each doubling leaves a dead array behind).
        double tapsPerOutput = identity || filter == ResamplingFilter.Nearest ? 1 : filter == ResamplingFilter.Box ? Math.Ceiling(scale) + 1 : (2 * FilterFunctions.Support(filter) * Math.Max(1, scale)) + 2;
        var weights = new List<float>((int)Math.Min(1 << 24, outputSize * tapsPerOutput));
        double[] tmp = new double[8];
        double[] folded = [];
        for (int o = 0; o < outputSize; o++)
        {
            offset[o] = weights.Count;
            if (identity)
            {
                start[o] = o;
                count[o] = 1;
                weights.Add(1f);
                continue;
            }

            if (filter == ResamplingFilter.Nearest)
            {
                start[o] = Math.Clamp((int)Math.Floor((o + 0.5) * scale), 0, sourceSize - 1);
                count[o] = 1;
                weights.Add(1f);
                continue;
            }

            int k0;
            int k1;
            if (filter == ResamplingFilter.Box)
            {
                k0 = (int)Math.Floor(o * scale);
                k1 = (int)Math.Ceiling((o + 1) * scale) - 1;
            }
            else
            {
                double center = ((o + 0.5) * scale) - 0.5;
                double radius = FilterFunctions.Support(filter) * Math.Max(1, scale);
                k0 = (int)Math.Floor(center - radius);
                k1 = (int)Math.Ceiling(center + radius);
            }

            int n = k1 - k0 + 1;
            if (tmp.Length < n)
            {
                tmp = new double[n];
            }

            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                int k = k0 + i;
                double w;
                if (filter == ResamplingFilter.Box)
                {
                    // Overlap of source pixel [k, k+1) with the output window [o*scale, (o+1)*scale).
                    w = Math.Max(0, Math.Min(k + 1, (o + 1) * scale) - Math.Max(k, o * scale));
                }
                else
                {
                    double x = (k - (((o + 0.5) * scale) - 0.5)) / Math.Max(1, scale);
                    w = FilterFunctions.Evaluate(filter, x);
                }

                tmp[i] = w;
                sum += w;
            }

            // Fold taps outside [0, sourceSize) onto the edge pixels.
            int lo = Math.Clamp(k0, 0, sourceSize - 1);
            int hi = Math.Clamp(k1, 0, sourceSize - 1);
            int m = hi - lo + 1;
            if (folded.Length < m)
            {
                folded = new double[m];
            }

            Array.Clear(folded, 0, m);
            for (int i = 0; i < n; i++)
            {
                folded[Math.Clamp(k0 + i, 0, sourceSize - 1) - lo] += tmp[i];
            }

            // Trim zero weights at both ends (kernel zero crossings) so no tap is wasted.
            int a = 0;
            int b = m - 1;
            while (a < b && folded[a] == 0)
            {
                a++;
            }

            while (b > a && folded[b] == 0)
            {
                b--;
            }

            if (sum <= 0)
            {
                // Only possible for degenerate scales; fall back to the nearest pixel.
                start[o] = Math.Clamp((int)Math.Floor((o + 0.5) * scale), 0, sourceSize - 1);
                count[o] = 1;
                weights.Add(1f);
                continue;
            }

            start[o] = lo + a;
            count[o] = b - a + 1;
            for (int i = a; i <= b; i++)
            {
                weights.Add((float)(folded[i] / sum));
            }
        }

        return new WeightTable(start, count, offset, weights.ToArray(), sourceSize);
    }

    /// <summary>The source index range [first, end) read by output indices [<paramref name="outFirst"/>, <paramref name="outEnd"/>).</summary>
    public (int First, int End) SourceRange(int outFirst, int outEnd)
    {
        int first = int.MaxValue;
        int end = 0;
        for (int o = outFirst; o < outEnd; o++)
        {
            first = Math.Min(first, Start[o]);
            end = Math.Max(end, Start[o] + Count[o]);
        }

        return (first, end);
    }
}
