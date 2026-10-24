using System.Buffers;
using OneImage.Kernels;

namespace OneImage;

/// <summary>Per-channel counts of how many samples fall into each of a fixed number of equal-width bins. Made by <see cref="ImageFrame.Histogram"/>.</summary>
public sealed class ImageHistogram
{
    private readonly long[] _counts;

    internal ImageHistogram(int channels, int bins, long pixels, long[] counts)
    {
        ChannelCount = channels;
        Bins = bins;
        PixelCount = pixels;
        _counts = counts;
    }

    /// <summary>Gets the number of channels: the color channels in stored order (gray; R, G, B; C, M, Y, K) followed by alpha when the format has it.</summary>
    public int ChannelCount { get; }

    /// <summary>Gets the number of bins per channel.</summary>
    public int Bins { get; }

    /// <summary>Gets the number of pixels that were counted (all pixels, or those with non-zero alpha when transparent pixels were skipped).</summary>
    public long PixelCount { get; }

    /// <summary>Gets one count.</summary>
    /// <param name="channel">The channel, 0 to <see cref="ChannelCount"/> - 1.</param>
    /// <param name="bin">The bin, 0 to <see cref="Bins"/> - 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">An index is out of range.</exception>
    public long this[int channel, int bin] => Channel(channel)[bin];

    /// <summary>Gets the counts of one channel.</summary>
    /// <param name="channel">The channel, 0 to <see cref="ChannelCount"/> - 1.</param>
    /// <returns>A read-only view of <see cref="Bins"/> counts.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The channel is out of range.</exception>
    public ReadOnlySpan<long> Channel(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, ChannelCount);
        return _counts.AsSpan(channel * Bins, Bins);
    }
}

/// <summary>Statistics of one channel, in the channel's stored units (see <see cref="ImageStatistics.FullScale"/>).</summary>
/// <param name="Count">Number of samples that took part (non-finite float samples and skipped transparent pixels do not).</param>
/// <param name="Min">Smallest sample, or 0 when <paramref name="Count"/> is 0.</param>
/// <param name="Max">Largest sample, or 0 when <paramref name="Count"/> is 0.</param>
/// <param name="Mean">Arithmetic mean, or 0 when <paramref name="Count"/> is 0.</param>
/// <param name="StdDev">Population standard deviation (divides by <paramref name="Count"/>), or 0 when <paramref name="Count"/> is 0.</param>
public readonly record struct ChannelStatistics(long Count, double Min, double Max, double Mean, double StdDev);

/// <summary>Per-channel statistics of a frame. Made by <see cref="ImageFrame.Statistics"/>.</summary>
public sealed class ImageStatistics
{
    internal ImageStatistics(ChannelStatistics[] channels, double fullScale, long pixels)
    {
        Channels = channels;
        FullScale = fullScale;
        PixelCount = pixels;
    }

    /// <summary>Gets the channels in stored order (color channels, then alpha when present).</summary>
    public IReadOnlyList<ChannelStatistics> Channels { get; }

    /// <summary>Gets the value that means "full" in the stored units: 255 for 8-bit, 65535 for 16-bit, 1 for floating point. Divide by it to normalize.</summary>
    public double FullScale { get; }

    /// <summary>Gets the number of pixels that were counted (all pixels, or those with non-zero alpha when transparent pixels were skipped).</summary>
    public long PixelCount { get; }
}

public sealed partial class ImageFrame
{
    /// <summary>
    /// Counts the samples of this frame into <paramref name="bins"/> equal-width bins per channel (computes the whole frame, in parallel).
    /// <para>
    /// Samples are the stored values as they are: no color conversion, no un-premultiplying. The range 0..full scale (255, 65535 or
    /// 1.0) is split into equal bins: 8-bit value v goes to bin floor(v × bins / 256) (so 256 bins is one bin per value), 16-bit
    /// to floor(v × bins / 65536) (exact integer arithmetic), floating-point values to floor(clamp(v, 0, 1) × bins) limited
    /// to the last bin; NaN samples are not counted. Channels are in stored order with alpha last. Lab images are refused.
    /// </para>
    /// <para>
    /// The counts are exact and identical for any thread count or tile order. Memory use is bounded for streaming sources.
    /// </para>
    /// </summary>
    /// <param name="bins">Bins per channel, 1 to 65536. Defaults to 256.</param>
    /// <param name="skipTransparent">Ignore pixels whose alpha is 0 (in every channel). Defaults to <see langword="false"/>.</param>
    /// <param name="maxDegree">Most threads to use; 0 means the default.</param>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <returns>The histogram.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bins"/> is out of range.</exception>
    /// <exception cref="ImageOperationException">The frame is a Lab image.</exception>
    /// <exception cref="ImageFormatException">The underlying data is corrupt.</exception>
    /// <exception cref="ImageSourceChangedException">The source file changed since load.</exception>
    public ImageHistogram Histogram(int bins = 256, bool skipTransparent = false, int maxDegree = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bins, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bins, 65536);
        PixelFormat fmt = Format;
        if (fmt.Model == ColorModel.Lab)
        {
            throw new ImageOperationException("Histograms are not defined for Lab pixels; convert to RGB first.");
        }

        int c = fmt.ChannelCount;
        long[] total = new long[c * bins];
        long pixels = 0;
        TileReducer.Run<(uint[] Counts, long Pixels)>(
            Node,
            RenderContext.Create(maxDegree, cancellationToken),
            (rect, tile) =>
            {
                uint[] counts = Pools<uint>.Pool.Rent(c * bins);
                counts.AsSpan(0, c * bins).Clear();
                long n = AnalysisKernel.HistogramTile(tile, rect.Width, rect.Height, fmt, bins, skipTransparent, counts);
                return (counts, n);
            },
            part =>
            {
                for (int i = 0; i < total.Length; i++)
                {
                    total[i] += part.Counts[i];
                }

                pixels += part.Pixels;
                Pools<uint>.Pool.Return(part.Counts);
            });
        return new ImageHistogram(c, bins, pixels, total);
    }

    /// <summary>
    /// Computes per-channel count, minimum, maximum, mean and population standard deviation of the stored samples (computes the
    /// whole frame, in parallel). Samples are used as stored (no color conversion, no un-premultiplying), each channel on its
    /// own, alpha last. Statistics are in stored units (0..255, 0..65535, or the float value); see <see cref="ImageStatistics.FullScale"/>.
    /// <para>
    /// Integer images are summed exactly (sum and sum of squares in 64-bit integers, variance in 128-bit integers), so the results are
    /// exact on every machine. Floating-point images skip NaN and infinite samples and are accumulated per tile in double
    /// precision and merged in tile order. Either way the result is identical for any thread count or tile order.
    /// </para>
    /// </summary>
    /// <param name="skipTransparent">Ignore pixels whose alpha is 0 (in every channel). Defaults to <see langword="false"/>.</param>
    /// <param name="maxDegree">Most threads to use; 0 means the default.</param>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <returns>The statistics.</returns>
    /// <exception cref="ImageFormatException">The underlying data is corrupt.</exception>
    /// <exception cref="ImageSourceChangedException">The source file changed since load.</exception>
    public ImageStatistics Statistics(bool skipTransparent = false, int maxDegree = 0, CancellationToken cancellationToken = default)
    {
        PixelFormat fmt = Format;
        int c = fmt.ChannelCount;
        bool isFloat = fmt.Sample is SampleType.F16 or SampleType.F32;
        var total = new ChannelSums[c];
        for (int k = 0; k < c; k++)
        {
            total[k] = ChannelSums.Empty;
        }

        long pixels = 0;
        TileReducer.Run<(ChannelSums[] Sums, long Pixels)>(
            Node,
            RenderContext.Create(maxDegree, cancellationToken),
            (rect, tile) =>
            {
                var sums = new ChannelSums[c];
                for (int k = 0; k < c; k++)
                {
                    sums[k] = ChannelSums.Empty;
                }

                long n = AnalysisKernel.StatsTile(tile, rect.Width, rect.Height, fmt, skipTransparent, sums);
                return (sums, n);
            },
            part =>
            {
                for (int k = 0; k < c; k++)
                {
                    total[k].Merge(part.Sums[k], isFloat);
                }

                pixels += part.Pixels;
            });
        double full = fmt.Sample switch { SampleType.U8 => 255.0, SampleType.U16 => 65535.0, _ => 1.0 };
        var result = new ChannelStatistics[c];
        for (int k = 0; k < c; k++)
        {
            ChannelSums s = total[k];
            if (s.Count == 0)
            {
                result[k] = new ChannelStatistics(0, 0, 0, 0, 0);
            }
            else if (isFloat)
            {
                result[k] = new ChannelStatistics(s.Count, s.MinFloat, s.MaxFloat, s.Mean, Math.Sqrt(s.M2 / s.Count));
            }
            else
            {
                // variance = (n * sumsq - sum^2) / n^2, exact in 128 bits, then one rounding to double.
                Int128 n = s.Count;
                Int128 numerator = (n * (Int128)s.SumSquares) - ((Int128)s.Sum * (Int128)s.Sum);
                double mean = (double)s.Sum / s.Count;
                result[k] = new ChannelStatistics(s.Count, s.MinInt, s.MaxInt, mean, Math.Sqrt((double)numerator) / s.Count);
            }
        }

        return new ImageStatistics(result, full, pixels);
    }
}

public sealed partial class Image
{
    /// <summary>Computes the histogram of frame 0; see <see cref="ImageFrame.Histogram"/>.</summary>
    /// <param name="bins">Bins per channel, 1 to 65536. Defaults to 256.</param>
    /// <param name="skipTransparent">Ignore pixels whose alpha is 0. Defaults to <see langword="false"/>.</param>
    /// <param name="maxDegree">Most threads to use; 0 means the default.</param>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <returns>The histogram.</returns>
    public ImageHistogram Histogram(int bins = 256, bool skipTransparent = false, int maxDegree = 0, CancellationToken cancellationToken = default) =>
        _frames[0].Histogram(bins, skipTransparent, maxDegree, cancellationToken);

    /// <summary>Computes the channel statistics of frame 0; see <see cref="ImageFrame.Statistics"/>.</summary>
    /// <param name="skipTransparent">Ignore pixels whose alpha is 0. Defaults to <see langword="false"/>.</param>
    /// <param name="maxDegree">Most threads to use; 0 means the default.</param>
    /// <param name="cancellationToken">Cancels the computation.</param>
    /// <returns>The statistics.</returns>
    public ImageStatistics Statistics(bool skipTransparent = false, int maxDegree = 0, CancellationToken cancellationToken = default) =>
        _frames[0].Statistics(skipTransparent, maxDegree, cancellationToken);
}
