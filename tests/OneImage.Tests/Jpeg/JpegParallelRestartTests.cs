using OneImage.Codecs.Jpeg;
using System.Security.Cryptography;

namespace OneImage.Tests.Jpeg;

/// <summary>
/// Decoding the restart intervals of a baseline scan on several threads must give the sequential decoder's bytes, and
/// fail the way it fails: same rows delivered before an error, same exception.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public class JpegParallelRestartTests
{
    private readonly record struct Outcome(string Kind, string Message, int Rows, string Hash);

    private static Rgb24[] Noise(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var px = new Rgb24[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // A gradient with noise: plenty of coefficients, and long runs in places.
                int n = (x / 40 + y / 40) % 3 == 0 ? 0 : 60;
                px[(y * w) + x] = new Rgb24(
                    (byte)Math.Clamp((x * 255 / w) + rnd.Next(-n, n + 1), 0, 255),
                    (byte)Math.Clamp((y * 255 / h) + rnd.Next(-n, n + 1), 0, 255),
                    (byte)Math.Clamp(((x + y) * 255 / (w + h)) + rnd.Next(-n, n + 1), 0, 255));
            }
        }

        return px;
    }

    private static byte[] Make(int w, int h, JpegSubsampling sub, int restart, int seed = 1)
    {
        var o = new JpegEncoderOptions { Quality = 85, Subsampling = sub, RestartInterval = restart };
        return JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(Noise(w, h, seed), w, h), o);
    }

    private static Outcome Run(byte[] jpeg, int scale, ImageRect? region, int stages, int parallel, int chunk)
    {
        int oldStages = JpegDecoderCore.StageOverride;
        int oldParallel = JpegScanDecoder.ParallelOverride;
        int oldChunk = JpegScanDecoder.ParallelChunkOverride;
        JpegDecoderCore.StageOverride = stages;
        JpegScanDecoder.ParallelOverride = parallel;
        JpegScanDecoder.ParallelChunkOverride = chunk;
        ArraySink? sink = null;
        try
        {
            using var ms = new MemoryStream(jpeg);
            ImageInfo info = JpegFormat.Instance.ReadInfo(ms, ImageLimits.Default);
            FrameInfo fi = info.Frames[0];
            int ow = (fi.Width + scale - 1) / scale;
            int oh = (fi.Height + scale - 1) / scale;
            ImageRect r = region ?? new ImageRect(0, 0, ow, oh);
            sink = new ArraySink(r.Width, r.Height, fi.Format);
            JpegFormat.Instance.DecodeFrame(ms, info, 0, new DecodeRequest(r, scale), sink, ImageLimits.Default, CancellationToken.None);
            return new Outcome("ok", "", sink.RowsWritten, Convert.ToHexString(SHA256.HashData(sink.Data)));
        }
        catch (ImageFormatException e)
        {
            return new Outcome("format", e.Message, sink?.RowsWritten ?? -1, Partial(sink));
        }
        catch (ImageLimitException e)
        {
            return new Outcome("limit", e.Message, sink?.RowsWritten ?? -1, Partial(sink));
        }
        finally
        {
            JpegDecoderCore.StageOverride = oldStages;
            JpegScanDecoder.ParallelOverride = oldParallel;
            JpegScanDecoder.ParallelChunkOverride = oldChunk;
        }
    }

    private static string Partial(ArraySink? sink) => sink is null ? "" : Convert.ToHexString(SHA256.HashData(sink.Data));

    public static TheoryData<int, int, int> Layouts()
    {
        var d = new TheoryData<int, int, int>();

        // width, height, subsampling (0 = 4:2:0, 1 = 4:4:4, 2 = 4:2:2, 3 = 4:4:0)
        foreach ((int w, int h, int s) in new[] { (200, 136, 0), (203, 141, 1), (131, 90, 2), (67, 250, 3), (40, 24, 0), (500, 40, 0) })
        {
            d.Add(w, h, s);
        }

        return d;
    }

    private static JpegSubsampling Sub(int s) => s switch { 0 => JpegSubsampling.Yuv420, 1 => JpegSubsampling.Yuv444, 2 => JpegSubsampling.Yuv422, _ => JpegSubsampling.Yuv440 };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ParallelIntervalsGiveTheSequentialBytes(int w, int h, int s)
    {
        foreach (int restart in new[] { 1, 2, 5, 13, 26, 40, 60000 })
        {
            byte[] jpeg = Make(w, h, Sub(s), restart);
            int mcus = ((w + (s is 0 or 2 ? 15 : 7)) / (s is 0 or 2 ? 16 : 8)) * ((h + (s is 0 or 3 ? 15 : 7)) / (s is 0 or 3 ? 16 : 8));
            foreach (int scale in new[] { 1, 2, 4, 8 })
            {
                Outcome expected = Run(jpeg, scale, null, 0, 0, 0);
                Assert.Equal("ok", expected.Kind);
                int before = JpegScanDecoder.ParallelScans;
                foreach (int degree in new[] { 2, 4, 16 })
                {
                    foreach (int chunk in new[] { 0, 1, 3, 40 })
                    {
                        foreach (int stages in new[] { 0, 1, 2 })
                        {
                            Assert.Equal(expected, Run(jpeg, scale, null, stages, degree, chunk));
                        }
                    }
                }

                Assert.True(restart * 2 > mcus || JpegScanDecoder.ParallelScans > before, $"the parallel path did not run (restart {restart})");
            }
        }
    }

    [Fact]
    public void RegionsMatch()
    {
        byte[] jpeg = Make(300, 220, JpegSubsampling.Yuv420, 7);
        var rnd = new Random(5);
        for (int k = 0; k < 12; k++)
        {
            int scale = 1 << rnd.Next(4);
            int ow = (300 + scale - 1) / scale;
            int oh = (220 + scale - 1) / scale;
            int w = 1 + rnd.Next(ow);
            int h = 1 + rnd.Next(oh);
            var r = new ImageRect(rnd.Next(ow - w + 1), rnd.Next(oh - h + 1), w, h);
            Outcome expected = Run(jpeg, scale, r, 0, 0, 0);
            foreach (int stages in new[] { 0, 2 })
            {
                Assert.Equal(expected, Run(jpeg, scale, r, stages, 4, 2));
            }
        }
    }

    [Fact]
    public void CorpusFilesWithRestartsMatch()
    {
        foreach (string name in new[] { "base-420-restart-rows", "base-422-restart-blocks", "base-444-restart-blocks", "gray-restart", "arith-restart", "prog-422-restart", "restart-many-420-r1", "restart-many-420-r4", "restart-many-444-r5", "restart-many-422-r2" })
        {
            byte[] jpeg = JpegTestSupport.Read(name);
            foreach (int scale in new[] { 1, 2, 8 })
            {
                Outcome expected = Run(jpeg, scale, null, 0, 0, 0);
                Assert.Equal(expected, Run(jpeg, scale, null, 0, 3, 1));
                Assert.Equal(expected, Run(jpeg, scale, null, 2, 3, 2));
            }
        }
    }

    private static IEnumerable<(string Name, byte[] Data)> Damaged(byte[] jpeg, int seed)
    {
        int sos = FindSos(jpeg);
        var rnd = new Random(seed);
        var rst = new List<int>();
        for (int i = sos; i < jpeg.Length - 1; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] is >= 0xD0 and <= 0xD7)
            {
                rst.Add(i);
            }
        }

        Assert.True(rst.Count > 4);

        for (int k = 0; k < 24; k++)
        {
            yield return ($"truncate {k}", jpeg.AsSpan(0, sos + rnd.Next(jpeg.Length - sos)).ToArray());
        }

        for (int k = 0; k < 40; k++)
        {
            byte[] c = (byte[])jpeg.Clone();
            for (int j = 0; j < 1 + rnd.Next(4); j++)
            {
                c[sos + rnd.Next(jpeg.Length - sos)] ^= (byte)(1 << rnd.Next(8));
            }

            yield return ($"flip {k}", c);
        }

        for (int k = 0; k < 24; k++)
        {
            int at = rst[rnd.Next(rst.Count)];
            byte[] c = (byte[])jpeg.Clone();
            switch (k % 8)
            {
                case 0: // a marker that is not a restart
                    c[at + 1] = 0xD9;
                    break;
                case 1:
                    c[at + 1] = 0xC4;
                    break;
                case 2: // the marker is gone
                    yield return ($"remove {k}", Concat(Part(jpeg, 0, at), Part(jpeg, at + 2, jpeg.Length)));
                    continue;
                case 3: // an extra one
                    yield return ($"double {k}", Concat(Part(jpeg, 0, at + 2), Part(jpeg, at, jpeg.Length)));
                    continue;
                case 4: // data the interval does not need
                    yield return ($"garbage {k}", Concat(Part(jpeg, 0, at), [0x12, 0x34, 0x56, 0x78, 0x9A], Part(jpeg, at, jpeg.Length)));
                    continue;
                case 5: // fill bytes before the marker
                    yield return ($"fill {k}", Concat(Part(jpeg, 0, at), [0xFF, 0xFF], Part(jpeg, at, jpeg.Length)));
                    continue;
                case 6: // the data ends right at the marker
                    yield return ($"cut {k}", jpeg.AsSpan(0, at + 1 + rnd.Next(2)).ToArray());
                    continue;
                default: // a stuffed zero turned into a marker
                    c[at] = 0xFF;
                    c[at + 1] = 0x00;
                    break;
            }

            yield return ($"marker {k}", c);
        }
    }

    private static byte[] Part(byte[] data, int from, int to) => data[from..to];

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static int FindSos(byte[] j)
    {
        for (int i = 2; i < j.Length - 4; i++)
        {
            if (j[i] == 0xFF && j[i + 1] == 0xDA)
            {
                return i + 2 + ((j[i + 2] << 8) | j[i + 3]);
            }
        }

        throw new InvalidOperationException("no SOS");
    }

    [Theory]
    [InlineData(0, 3, 0, 1)]
    [InlineData(1, 4, 2, 1)]
    [InlineData(0, 7, 1, 2)]
    [InlineData(3, 16, 3, 0)]
    public void DamagedFilesBehaveTheSame(int sub, int restart, int seed, int scaleShift)
    {
        byte[] jpeg = Make(180, 120, Sub(sub), restart, seed);
        int scale = 1 << scaleShift;
        int differing = 0;
        int failing = 0;
        foreach ((string name, byte[] data) in Damaged(jpeg, 100 + seed))
        {
            // One thread: the rows delivered before a failure are deterministic, so they compare too.
            Outcome expected = Run(data, scale, null, 0, 0, 0);
            if (expected.Kind != "ok")
            {
                failing++;
            }

            foreach ((int degree, int chunk) in new[] { (2, 0), (4, 1), (16, 2) })
            {
                Outcome got = Run(data, scale, null, 0, degree, chunk);
                if (got != expected)
                {
                    differing++;
                }

                Assert.True(got == expected, $"{name} degree {degree} chunk {chunk}: {expected} / {got}");
            }

            // With helper threads the number of rows delivered at the time of a failure varies; the outcome must not.
            Outcome staged = Run(data, scale, null, 2, 4, 1);
            Outcome serialStaged = Run(data, scale, null, 2, 0, 0);
            Assert.True(staged.Kind == serialStaged.Kind && (staged.Kind != "ok" || staged == serialStaged), $"{name} staged: {serialStaged} / {staged}");
        }

        Assert.Equal(0, differing);
        Assert.True(failing > 10, $"only {failing} damaged files failed; the set is too gentle");
    }

    [Fact]
    public void RegionThatEndsBeforeTheDamageStillSucceeds()
    {
        byte[] jpeg = Make(160, 160, JpegSubsampling.Yuv420, 5);
        byte[] cut = jpeg.AsSpan(0, jpeg.Length * 3 / 4).ToArray();
        var r = new ImageRect(0, 0, 160, 40);
        Outcome expected = Run(cut, 1, r, 0, 0, 0);
        Assert.Equal("ok", expected.Kind);
        Assert.Equal(expected, Run(cut, 1, r, 0, 4, 1));
        Assert.Equal(expected, Run(cut, 1, r, 2, 4, 1));
        Assert.Equal("format", Run(cut, 1, null, 0, 4, 1).Kind);
    }

    [Fact]
    public void CancellationStopsAParallelDecode()
    {
        byte[] jpeg = Make(400, 400, JpegSubsampling.Yuv420, 3);
        JpegScanDecoder.ParallelOverride = 4;
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var ms = new MemoryStream(jpeg);
            ImageInfo info = JpegFormat.Instance.ReadInfo(ms, ImageLimits.Default);
            var sink = new ArraySink(400, 400, info.Frames[0].Format);
            Assert.ThrowsAny<OperationCanceledException>(() => JpegFormat.Instance.DecodeFrame(ms, info, 0, new DecodeRequest(new ImageRect(0, 0, 400, 400)), sink, ImageLimits.Default, cts.Token));
        }
        finally
        {
            JpegScanDecoder.ParallelOverride = -1;
        }
    }
}
