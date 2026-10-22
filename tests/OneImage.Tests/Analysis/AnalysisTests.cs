using Xunit;

namespace OneImage.Tests.Analysis;

public class AnalysisTests
{
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Rgba32[] Pixels(int w, int h) =>
        Enumerable.Range(0, w * h).Select(i => new Rgba32((byte)(i % 251), (byte)((i / 7) % 256), (byte)(i % w < 300 ? 9 : 200), (byte)(i % 5 == 0 ? 0 : 255))).ToArray();

    [Fact]
    public void HistogramIsExactAndIndependentOfThreadCount()
    {
        const int w = 700;
        const int h = 520;
        Rgba32[] px = Pixels(w, h);
        Image img = Image.FromPixels<Rgba32>(px, w, h);
        foreach (int bins in new[] { 256, 16, 1, 1000 })
        {
            long[][] want = new long[4][];
            for (int k = 0; k < 4; k++)
            {
                want[k] = new long[bins];
            }

            foreach (Rgba32 p in px)
            {
                want[0][(p.R * bins) >> 8]++;
                want[1][(p.G * bins) >> 8]++;
                want[2][(p.B * bins) >> 8]++;
                want[3][(p.A * bins) >> 8]++;
            }

            foreach (int degree in new[] { 1, 2, 8 })
            {
                ImageHistogram hist = img.Histogram(bins, maxDegree: degree);
                Assert.Equal((4, bins, (long)w * h), (hist.ChannelCount, hist.Bins, hist.PixelCount));
                for (int k = 0; k < 4; k++)
                {
                    Assert.True(hist.Channel(k).SequenceEqual(want[k]), $"bins {bins} channel {k} degree {degree}");
                }
            }
        }

        ImageHistogram visible = img.Histogram(skipTransparent: true);
        Assert.Equal(px.Count(p => p.A != 0), visible.PixelCount);
        Assert.Equal(px.Count(p => p.A != 0 && p.R == 7), visible[0, 7]);
    }

    [Fact]
    public void StatisticsAreExactAndIndependentOfThreadCount()
    {
        const int w = 650;
        const int h = 410;
        Rgba32[] px = Pixels(w, h);
        Image img = Image.FromPixels<Rgba32>(px, w, h);
        foreach (int degree in new[] { 1, 3, 8 })
        {
            ImageStatistics s = img.Statistics(maxDegree: degree);
            Assert.Equal(255.0, s.FullScale);
            Func<Rgba32, int>[] get = [p => p.R, p => p.G, p => p.B, p => p.A];
            for (int k = 0; k < 4; k++)
            {
                long n = px.Length;
                long sum = px.Sum(p => (long)get[k](p));
                long sq = px.Sum(p => (long)get[k](p) * get[k](p));
                ChannelStatistics c = s.Channels[k];
                Assert.Equal(n, c.Count);
                Assert.Equal(px.Min(p => get[k](p)), c.Min);
                Assert.Equal(px.Max(p => get[k](p)), c.Max);
                Assert.Equal((double)sum / n, c.Mean);
                Assert.Equal(Math.Sqrt(((double)n * sq) - ((double)sum * sum)) / n, c.StdDev, 9);
            }
        }

        ImageStatistics flat = Image.FromPixels<Gray8>(Enumerable.Repeat(new Gray8(77), 300 * 300).ToArray(), 300, 300).Statistics();
        Assert.Equal(new ChannelStatistics(90000, 77, 77, 77, 0), flat.Channels[0]);
    }

    [Fact]
    public void FloatStatisticsAreBitIdenticalAcrossThreadCounts()
    {
        const int w = 500;
        const int h = 400;
        var rnd = new Random(2);
        GrayFloat[] px = Enumerable.Range(0, w * h).Select(_ => new GrayFloat((float)rnd.NextDouble())).ToArray();
        px[10] = new GrayFloat(float.NaN);
        px[11] = new GrayFloat(float.PositiveInfinity);
        Image img = Image.FromPixels<GrayFloat>(px, w, h);
        ChannelStatistics one = img.Statistics(maxDegree: 1).Channels[0];
        foreach (int degree in new[] { 2, 5, 8 })
        {
            Assert.Equal(one, img.Statistics(maxDegree: degree).Channels[0]);
        }

        Assert.Equal((long)(w * h) - 2, one.Count);
        Assert.Equal(px.Where(p => float.IsFinite(p.V)).Average(p => (double)p.V), one.Mean, 9);
        ImageHistogram hist = img.Histogram(10);
        Assert.Equal((long)(w * h) - 1, hist.Channel(0).ToArray().Sum());
    }

    [Fact]
    public void Gray16HistogramUsesExactIntegerBins()
    {
        Gray16[] px = [new(0), new(65535), new(32768), new(32767), new(16384)];
        ImageHistogram h = Image.FromPixels<Gray16>(px, 5, 1).Histogram(4);
        Assert.Equal([1, 1, 1, 2], h.Channel(0).ToArray());
    }

    [Fact]
    public void IdentifyDecodesNoPixels()
    {
        var raw = new RawFormat();
        byte[] file = new byte[21 + 4 + (4000 * 3000 * 4)];
        "RAW1"u8.CopyTo(file);
        file[4] = 1;
        BitConverter.TryWriteBytes(file.AsSpan(5), 4000);
        BitConverter.TryWriteBytes(file.AsSpan(9), 3000);
        BitConverter.TryWriteBytes(file.AsSpan(13), 1);
        ImageInfo info = Image.Identify(file, new LoadOptions { Configuration = raw.Configuration() });
        Assert.Equal((4000, 3000), (info.Width, info.Height));
        Assert.Equal(0, raw.DecodeCalls);

        // A 24 MP TIFF: Identify touches only the header and directory.
        using var ms = new MemoryStream();
        Image.FromPixels<Gray8>(new Gray8[6000 * 4000], 6000, 4000).Save(ms, TiffFormat.Instance, new SaveOptions { Configuration = new ImageConfiguration().Add(TiffFormat.Instance) });
        var counting = new CountingStream(new MemoryStream(ms.ToArray()));
        ImageInfo tiff = Image.Identify(counting, new LoadOptions { Configuration = new ImageConfiguration().Add(TiffFormat.Instance) });
        Assert.Equal((6000, 4000), (tiff.Width, tiff.Height));
        Assert.True(counting.BytesRead < 64 * 1024, $"Identify read {counting.BytesRead} of {counting.Length} bytes");
    }
}
