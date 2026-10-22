using System.Numerics;
using System.Runtime.InteropServices;

namespace OneImage.Kernels;

/// <summary>Running totals of one channel over some pixels. Integer samples use exact sums; float samples use mean and M2 (Welford / Chan merge).</summary>
internal struct ChannelSums
{
    public long Count;
    public ulong Sum;
    public ulong SumSquares;
    public ulong MinInt;
    public ulong MaxInt;
    public double MinFloat;
    public double MaxFloat;
    public double Mean;
    public double M2;

    public static ChannelSums Empty => new() { MinInt = ulong.MaxValue, MinFloat = double.PositiveInfinity, MaxFloat = double.NegativeInfinity };

    /// <summary>Adds <paramref name="other"/> into this total. Integer parts commute exactly; float parts must be merged in a fixed order.</summary>
    public void Merge(in ChannelSums other, bool floatSamples)
    {
        if (other.Count == 0)
        {
            return;
        }

        if (!floatSamples)
        {
            Count += other.Count;
            Sum += other.Sum;
            SumSquares += other.SumSquares;
            MinInt = Math.Min(MinInt, other.MinInt);
            MaxInt = Math.Max(MaxInt, other.MaxInt);
            return;
        }

        long n = Count + other.Count;
        double delta = other.Mean - Mean;
        M2 = M2 + other.M2 + (delta * delta * Count * other.Count / n);
        Mean += delta * other.Count / n;
        Count = n;
        MinFloat = Math.Min(MinFloat, other.MinFloat);
        MaxFloat = Math.Max(MaxFloat, other.MaxFloat);
    }
}

/// <summary>Per-tile histogram and statistics loops over stored samples. Callers pass whole tile buffers and the valid width and height.</summary>
internal static class AnalysisKernel
{
    /// <summary>Counts the samples of the valid part of one tile into <paramref name="counts"/> (channels × bins) and returns how many pixels were counted.</summary>
    public static long HistogramTile(ReadOnlySpan<byte> tile, int width, int height, PixelFormat format, int bins, bool skipTransparent, Span<uint> counts)
    {
        int c = format.ChannelCount;
        if (counts.Length < c * bins || tile.Length < Tile.ByteSize(format) || (uint)width > Tile.Size || (uint)height > Tile.Size)
        {
            throw new ArgumentException("Histogram buffers are too small.");
        }

        bool alpha = skipTransparent && format.HasAlpha;
        switch (format.Sample)
        {
            case SampleType.U8:
                return Histogram8(tile, width, height, c, bins, alpha, counts);
            case SampleType.U16:
                return Histogram16(MemoryMarshal.Cast<byte, ushort>(tile), width, height, c, bins, alpha, counts);
            case SampleType.F16:
                return HistogramFloat(MemoryMarshal.Cast<byte, Half>(tile), width, height, c, bins, alpha, counts);
            default:
                return HistogramFloat(MemoryMarshal.Cast<byte, float>(tile), width, height, c, bins, alpha, counts);
        }
    }

    private static long Histogram8(ReadOnlySpan<byte> tile, int width, int height, int c, int bins, bool skipAlpha, Span<uint> counts)
    {
        Span<int> lut = stackalloc int[256];
        for (int v = 0; v < 256; v++)
        {
            lut[v] = (v * bins) >> 8;
        }

        long pixels = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = tile.Slice(y * Tile.Size * c, width * c);
            if (!skipAlpha)
            {
                pixels += width;
                for (int k = 0; k < c; k++)
                {
                    Span<uint> h = counts.Slice(k * bins, bins);
                    for (int i = k; i < row.Length; i += c)
                    {
                        h[lut[row[i]]]++;
                    }
                }

                continue;
            }

            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<byte> px = row.Slice(x * c, c);
                if (px[c - 1] == 0)
                {
                    continue;
                }

                pixels++;
                for (int k = 0; k < c; k++)
                {
                    counts[(k * bins) + lut[px[k]]]++;
                }
            }
        }

        return pixels;
    }

    private static long Histogram16(ReadOnlySpan<ushort> tile, int width, int height, int c, int bins, bool skipAlpha, Span<uint> counts)
    {
        long pixels = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ushort> row = tile.Slice(y * Tile.Size * c, width * c);
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<ushort> px = row.Slice(x * c, c);
                if (skipAlpha && px[c - 1] == 0)
                {
                    continue;
                }

                pixels++;
                for (int k = 0; k < c; k++)
                {
                    // Exact integer scaling: floor(v * bins / 65536).
                    counts[(k * bins) + (int)(((uint)px[k] * (uint)bins) >> 16)]++;
                }
            }
        }

        return pixels;
    }

    private static long HistogramFloat<T>(ReadOnlySpan<T> tile, int width, int height, int c, int bins, bool skipAlpha, Span<uint> counts)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        long pixels = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<T> row = tile.Slice(y * Tile.Size * c, width * c);
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<T> px = row.Slice(x * c, c);
                if (skipAlpha && T.IsZero(px[c - 1]))
                {
                    continue;
                }

                pixels++;
                for (int k = 0; k < c; k++)
                {
                    double v = double.CreateTruncating(px[k]);
                    if (double.IsNaN(v))
                    {
                        continue;
                    }

                    int b = (int)(Math.Clamp(v, 0.0, 1.0) * bins);
                    counts[(k * bins) + Math.Min(b, bins - 1)]++;
                }
            }
        }

        return pixels;
    }

    /// <summary>Accumulates the samples of the valid part of one tile into <paramref name="sums"/> (one entry per channel).</summary>
    public static long StatsTile(ReadOnlySpan<byte> tile, int width, int height, PixelFormat format, bool skipTransparent, Span<ChannelSums> sums)
    {
        int c = format.ChannelCount;
        if (sums.Length < c || tile.Length < Tile.ByteSize(format) || (uint)width > Tile.Size || (uint)height > Tile.Size)
        {
            throw new ArgumentException("Statistics buffers are too small.");
        }

        bool alpha = skipTransparent && format.HasAlpha;
        switch (format.Sample)
        {
            case SampleType.U8:
                return StatsInt(tile, width, height, c, alpha, sums);
            case SampleType.U16:
                return StatsInt(MemoryMarshal.Cast<byte, ushort>(tile), width, height, c, alpha, sums);
            case SampleType.F16:
                return StatsFloat(MemoryMarshal.Cast<byte, Half>(tile), width, height, c, alpha, sums);
            default:
                return StatsFloat(MemoryMarshal.Cast<byte, float>(tile), width, height, c, alpha, sums);
        }
    }

    private static long StatsInt<T>(ReadOnlySpan<T> tile, int width, int height, int c, bool skipAlpha, Span<ChannelSums> sums)
        where T : unmanaged, IBinaryInteger<T>
    {
        long pixels = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<T> row = tile.Slice(y * Tile.Size * c, width * c);
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<T> px = row.Slice(x * c, c);
                if (skipAlpha && T.IsZero(px[c - 1]))
                {
                    continue;
                }

                pixels++;
                for (int k = 0; k < c; k++)
                {
                    ulong v = ulong.CreateTruncating(px[k]);
                    ref ChannelSums s = ref sums[k];
                    s.Count++;
                    s.Sum += v;
                    s.SumSquares += v * v;
                    if (v < s.MinInt)
                    {
                        s.MinInt = v;
                    }

                    if (v > s.MaxInt)
                    {
                        s.MaxInt = v;
                    }
                }
            }
        }

        return pixels;
    }

    private static long StatsFloat<T>(ReadOnlySpan<T> tile, int width, int height, int c, bool skipAlpha, Span<ChannelSums> sums)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        long pixels = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<T> row = tile.Slice(y * Tile.Size * c, width * c);
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<T> px = row.Slice(x * c, c);
                if (skipAlpha && T.IsZero(px[c - 1]))
                {
                    continue;
                }

                pixels++;
                for (int k = 0; k < c; k++)
                {
                    double v = double.CreateTruncating(px[k]);
                    if (!double.IsFinite(v))
                    {
                        continue;
                    }

                    ref ChannelSums s = ref sums[k];
                    s.Count++;
                    double d = v - s.Mean;
                    s.Mean += d / s.Count;
                    s.M2 += d * (v - s.Mean);
                    if (v < s.MinFloat)
                    {
                        s.MinFloat = v;
                    }

                    if (v > s.MaxFloat)
                    {
                        s.MaxFloat = v;
                    }
                }
            }
        }

        return pixels;
    }
}
