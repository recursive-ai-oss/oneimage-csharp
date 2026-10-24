using System.Buffers;

namespace OneImage;

/// <summary>
/// Builds the per-output tap tables of <see cref="WeightTable"/> into arrays that are kept between builds, so a caller that
/// resizes many images does not allocate tables for each. This is the single implementation of the weight rules (positions,
/// kernel stretching, edge folding, zero trimming, normalization); <see cref="WeightTable"/> copies its result out.
/// Arrays come from <see cref="ArrayPool{T}"/> only when they must grow, and are handed back by <see cref="Dispose"/>.
/// </summary>
internal sealed class WeightBuffers : IDisposable
{
    private double[] _tmp = [];
    private double[] _folded = [];

    public int[] Start { get; private set; } = [];

    public int[] Count { get; private set; } = [];

    public int[] Offset { get; private set; } = [];

    public float[] Weights { get; private set; } = [];

    /// <summary>Number of weights in use.</summary>
    public int WeightCount { get; private set; }

    /// <summary>Output entries of the last <see cref="Fill"/>.</summary>
    public int OutputSize { get; private set; }

    /// <summary>Bytes held by the arrays (their capacity, not the size of the last table).</summary>
    public long CapacityBytes =>
        (((long)Start.Length + Count.Length + Offset.Length + Weights.Length) * 4) + (((long)_tmp.Length + _folded.Length) * 8);

    /// <summary>An upper bound of the bytes <see cref="Fill"/> needs for these arguments (before pool rounding).</summary>
    public static long EstimateBytes(int outputSize, double scale, ResamplingFilter filter)
    {
        double taps = filter == ResamplingFilter.Nearest ? 1 : filter == ResamplingFilter.Box ? Math.Ceiling(scale) + 2 : (2 * FilterFunctions.Support(filter) * Math.Max(1, scale)) + 3;
        return (long)(outputSize * (12 + (taps * 4))) + (long)(taps * 16);
    }

    public void Dispose()
    {
        Release(ref _tmp);
        Release(ref _folded);
        Release(Start);
        Release(Count);
        Release(Offset);
        Release(Weights);
        Start = [];
        Count = [];
        Offset = [];
        Weights = [];
        WeightCount = 0;
    }

    private static void Release<T>(ref T[] array)
    {
        Release(array);
        array = [];
    }

    private static void Release<T>(T[] array)
    {
        if (array.Length > 0)
        {
            Pools<T>.Pool.Return(array);
        }
    }

    private static void Ensure<T>(ref T[] array, int needed, int keep)
    {
        if (array.Length >= needed)
        {
            return;
        }

        T[] bigger = Pools<T>.Pool.Rent(needed);
        array.AsSpan(0, keep).CopyTo(bigger);
        Release(array);
        array = bigger;
    }

    private void Add(float weight)
    {
        if (WeightCount == Weights.Length)
        {
            float[] w = Weights;
            Ensure(ref w, Math.Max(64, WeightCount * 2), WeightCount);
            Weights = w;
        }

        Weights[WeightCount++] = weight;
    }

    /// <summary>
    /// Fills the tables for one axis; see <see cref="WeightTable.Build"/> for the arguments. Entries beyond
    /// <paramref name="outputSize"/> are unspecified.
    /// </summary>
    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public void Fill(int sourceSize, int outputSize, double scale, ResamplingFilter filter)
    {
        int[] start = Start;
        int[] count = Count;
        int[] offset = Offset;
        Ensure(ref start, outputSize, 0);
        Ensure(ref count, outputSize, 0);
        Ensure(ref offset, outputSize, 0);
        Start = start;
        Count = count;
        Offset = offset;
        OutputSize = outputSize;
        WeightCount = 0;
        bool identity = sourceSize == outputSize && Math.Abs(scale - 1) < 1e-12;
        Ensure(ref _tmp, 8, 0);
        for (int o = 0; o < outputSize; o++)
        {
            Offset[o] = WeightCount;
            if (identity)
            {
                Start[o] = o;
                Count[o] = 1;
                Add(1f);
                continue;
            }

            if (filter == ResamplingFilter.Nearest)
            {
                Start[o] = Math.Clamp((int)Math.Floor((o + 0.5) * scale), 0, sourceSize - 1);
                Count[o] = 1;
                Add(1f);
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
            Ensure(ref _tmp, n, 0);
            double[] tmp = _tmp;

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
            Ensure(ref _folded, m, 0);
            double[] folded = _folded;
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
                Start[o] = Math.Clamp((int)Math.Floor((o + 0.5) * scale), 0, sourceSize - 1);
                Count[o] = 1;
                Add(1f);
                continue;
            }

            Start[o] = lo + a;
            Count[o] = b - a + 1;
            for (int i = a; i <= b; i++)
            {
                Add((float)(folded[i] / sum));
            }
        }
    }
}
