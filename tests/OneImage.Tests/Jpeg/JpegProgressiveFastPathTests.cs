using OneImage.Codecs.Jpeg;
using System.Security.Cryptography;

namespace OneImage.Tests.Jpeg;

/// <summary>
/// Progressive decoding has two fast paths: refinement scans that read their correction bits in one go, and scans that decode
/// side by side. Both must give the pixels of the plain sequential, coefficient-by-coefficient decoder, and fail the way it
/// fails on damaged files: the same exception (type and message), and the same pixels wherever a request completes. A failing file
/// may hand over some rows before the exception now (rows are passed on while the last scans still run), so for failures only the
/// exception is compared.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public class JpegProgressiveFastPathTests
{
    private readonly record struct Outcome(string Kind, string Message, int Rows, string Hash);

    private static Rgb24[] Content(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var px = new Rgb24[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int n = ((x / 24) + (y / 24)) % 3 == 0 ? 2 : 40;
                px[(y * w) + x] = new Rgb24(
                    (byte)Math.Clamp((x * 255 / w) + rnd.Next(-n, n + 1), 0, 255),
                    (byte)Math.Clamp((y * 255 / h) + rnd.Next(-n, n + 1), 0, 255),
                    (byte)Math.Clamp(((x + y) * 255 / (w + h)) + rnd.Next(-n, n + 1), 0, 255));
            }
        }

        return px;
    }

    private static byte[] Make(int w, int h, JpegSubsampling sub, int restart, bool spectralOnly, int quality = 90)
    {
        var o = new JpegEncoderOptions { Quality = quality, Subsampling = sub, Progressive = true, RestartInterval = restart };
        JpegEncodeSession.SpectralOnlyScript = spectralOnly;
        try
        {
            return JpegEncodeTests.Encode(Image.FromPixels<Rgb24>(Content(w, h, 5), w, h), o);
        }
        finally
        {
            JpegEncodeSession.SpectralOnlyScript = false;
        }
    }

    private static Outcome Run(byte[] jpeg, int scale, bool maskRefine, int pipeline, bool region = false)
    {
        bool oldMask = JpegReader.UseMaskRefine;
        int oldPipeline = JpegScanPipeline.Override;
        JpegReader.UseMaskRefine = maskRefine;
        JpegScanPipeline.Override = pipeline;
        ArraySink? sink = null;
        try
        {
            using var ms = new MemoryStream(jpeg);
            ImageInfo info = JpegFormat.Instance.ReadInfo(ms, ImageLimits.Default);
            FrameInfo fi = info.Frames[0];
            int ow = (fi.Width + scale - 1) / scale;
            int oh = (fi.Height + scale - 1) / scale;
            var r = region ? new ImageRect(ow / 5, oh / 7, Math.Max(1, ow / 2), Math.Max(1, oh / 3)) : new ImageRect(0, 0, ow, oh);
            sink = new ArraySink(r.Width, r.Height, fi.Format);
            JpegFormat.Instance.DecodeFrame(ms, info, 0, new DecodeRequest(r, scale), sink, ImageLimits.Default, CancellationToken.None);
            return new Outcome("ok", "", sink.RowsWritten, Convert.ToHexString(SHA256.HashData(sink.Data)));
        }
        catch (ImageFormatException e)
        {
            return new Outcome("format", e.Message, 0, "");
        }
        catch (ImageLimitException e)
        {
            return new Outcome("limit", e.Message, 0, "");
        }
        finally
        {
            JpegReader.UseMaskRefine = oldMask;
            JpegScanPipeline.Override = oldPipeline;
        }
    }

    // The decoder as it was before the fast paths: one scan after the other, one coefficient at a time.
    private static Outcome Reference(byte[] jpeg, int scale) => Run(jpeg, scale, maskRefine: false, pipeline: 0);

    public static TheoryData<int, int, int, int, bool> Layouts()
    {
        var d = new TheoryData<int, int, int, int, bool>();

        // width, height, subsampling (0 = 4:2:0, 1 = 4:4:4, 2 = 4:2:2, 3 = 4:4:0), restart interval, spectral selection only
        foreach ((int w, int h, int s, int r) in new[] { (200, 136, 0, 0), (203, 141, 1, 0), (131, 90, 2, 0), (67, 250, 3, 0), (40, 24, 0, 0), (500, 40, 0, 7), (96, 64, 1, 5), (257, 129, 0, 11) })
        {
            d.Add(w, h, s, r, false);
            d.Add(w, h, s, r, true);
        }

        return d;
    }

    private static JpegSubsampling Sub(int s) => s switch { 0 => JpegSubsampling.Yuv420, 1 => JpegSubsampling.Yuv444, 2 => JpegSubsampling.Yuv422, _ => JpegSubsampling.Yuv440 };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void MatchesSequentialDecoder(int w, int h, int sub, int restart, bool spectralOnly)
    {
        byte[] jpeg = Make(w, h, Sub(sub), restart, spectralOnly);
        foreach (int scale in new[] { 1, 2, 4, 8 })
        {
            Outcome reference = Reference(jpeg, scale);
            Assert.Equal("ok", reference.Kind);
            Assert.Equal(reference, Run(jpeg, scale, maskRefine: true, pipeline: 0));
            int before = JpegScanPipeline.PipelinedScans;
            for (int rep = 0; rep < 6; rep++)
            {
                // Repeated: a missed wait between scans shows up only on some interleavings.
                Assert.Equal(reference, Run(jpeg, scale, maskRefine: true, pipeline: 2 + (rep % 3)));
            }

            Assert.Equal(reference, Run(jpeg, scale, maskRefine: false, pipeline: 3));
            Assert.Equal(Run(jpeg, scale, maskRefine: false, pipeline: 0, region: true), Run(jpeg, scale, maskRefine: true, pipeline: 3, region: true));
            Assert.True(JpegScanPipeline.PipelinedScans > before, "the pipeline did not run");
        }
    }

    [Fact]
    public void MatchesSequentialDecoderOnTheCorpus()
    {
        foreach (string file in Directory.GetFiles(JpegTestSupport.Dir, "*prog*.jpg"))
        {
            byte[] jpeg = File.ReadAllBytes(file);
            foreach (int scale in new[] { 1, 2, 4, 8 })
            {
                Outcome reference = Reference(jpeg, scale);
                Assert.Equal(reference, Run(jpeg, scale, maskRefine: true, pipeline: 0));

                // Several times: files with a first scan that runs past its band (a damaged run length) once raced with the scan of the next band.
                for (int rep = 0; rep < 12; rep++)
                {
                    Assert.Equal(reference, Run(jpeg, scale, maskRefine: true, pipeline: 2 + (rep % 4)));
                }
            }
        }
    }

    public static TheoryData<int, int, bool> DamageCases()
    {
        var d = new TheoryData<int, int, bool>();
        foreach ((int sub, int restart) in new[] { (0, 0), (1, 0), (0, 6), (2, 0) })
        {
            d.Add(sub, restart, false);
            d.Add(sub, restart, true);
        }

        return d;
    }

    /// <summary>Truncated files, flipped bytes, stray markers and altered scan headers fail (or succeed) exactly as they do sequentially.</summary>
    [Theory]
    [MemberData(nameof(DamageCases))]
    public void DamagedFilesBehaveAsBefore(int sub, int restart, bool spectralOnly)
    {
        byte[] jpeg = Make(120, 88, Sub(sub), restart, spectralOnly, quality: 85);
        var rnd = new Random(1234 + (sub * 10) + restart);
        var variants = new List<byte[]>();

        // Truncations: at scan boundaries' neighbourhoods and evenly through the file.
        for (int i = 1; i <= 60; i++)
        {
            variants.Add(jpeg.AsSpan(0, jpeg.Length * i / 61).ToArray());
        }

        // Single and multiple corrupted bytes.
        for (int i = 0; i < 160; i++)
        {
            byte[] c = (byte[])jpeg.Clone();
            int n = 1 + rnd.Next(3);
            for (int j = 0; j < n; j++)
            {
                c[rnd.Next(2, c.Length)] = (byte)rnd.Next(256);
            }

            variants.Add(c);
        }

        // Scan headers: change the spectral band and the approximation bits of one scan.
        var sos = new List<int>();
        for (int i = 0; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xDA)
            {
                sos.Add(i);
            }
        }

        foreach (int at in sos)
        {
            int ns = jpeg[at + 4];
            int tail = at + 5 + (2 * ns);
            for (int t = 0; t < 10; t++)
            {
                byte[] c = (byte[])jpeg.Clone();
                c[tail] = (byte)(t % 3 == 0 ? rnd.Next(0, 64) : c[tail]);
                c[tail + 1] = (byte)(t % 3 == 1 ? rnd.Next(0, 64) : c[tail + 1]);
                c[tail + 2] = (byte)rnd.Next(0, 256);
                variants.Add(c);
            }

            // A scan cut out, a scan duplicated, an end-of-image in the middle of its data.
            int next = sos.FirstOrDefault(s => s > at);
            int end = next == 0 ? jpeg.Length - 2 : next;
            var cut = new List<byte>(jpeg.AsSpan(0, at).ToArray());
            cut.AddRange(jpeg.AsSpan(end).ToArray());
            variants.Add([.. cut]);
            var dup = new List<byte>(jpeg.AsSpan(0, end).ToArray());
            dup.AddRange(jpeg.AsSpan(at, end - at).ToArray());
            dup.AddRange(jpeg.AsSpan(end).ToArray());
            variants.Add([.. dup]);
            byte[] early = (byte[])jpeg.Clone();
            int mid = at + ((end - at) / 2);
            early[mid] = 0xFF;
            early[mid + 1] = 0xD9;
            variants.Add(early);
        }

        // Two scans swapped.
        if (sos.Count >= 4)
        {
            int a = sos[1];
            int b = sos[2];
            int c2 = sos[3];
            var swapped = new List<byte>(jpeg.AsSpan(0, a).ToArray());
            swapped.AddRange(jpeg.AsSpan(b, c2 - b).ToArray());
            swapped.AddRange(jpeg.AsSpan(a, b - a).ToArray());
            swapped.AddRange(jpeg.AsSpan(c2).ToArray());
            variants.Add([.. swapped]);
        }

        int failures = 0;
        foreach (byte[] v in variants)
        {
            int scale = rnd.Next(4) switch { 0 => 1, 1 => 2, 2 => 4, _ => 8 };
            Outcome reference = Reference(v, scale);
            if (reference.Kind != "ok")
            {
                failures++;
            }

            Assert.Equal(reference, Run(v, scale, maskRefine: true, pipeline: 0));
            for (int rep = 0; rep < 4; rep++)
            {
                // Repeated: scans that interfere show up only on some interleavings.
                Assert.Equal(reference, Run(v, scale, maskRefine: true, pipeline: 2 + (rep % 3)));
            }
        }

        // The variants must actually exercise failures, or the comparison proves little.
        Assert.True(failures > variants.Count / 4, $"only {failures} of {variants.Count} variants failed");
    }

    /// <summary>The refinement reader, block by block, against the coefficient-by-coefficient one on random blocks and bit streams.</summary>
    [Fact]
    public void MaskedRefinementMatchesReferenceOnRandomData()
    {
        var rnd = new Random(77);
        for (int round = 0; round < 400; round++)
        {
            int w = 8 * (1 + rnd.Next(6));
            int h = 8 * (1 + rnd.Next(6));
            byte[] jpeg = Make(w, h, JpegSubsampling.Yuv444, 0, false, quality: 30 + rnd.Next(70));
            // Garble the tail half of the file, where the refinement scans are.
            for (int i = 0; i < 1 + rnd.Next(4); i++)
            {
                jpeg[(jpeg.Length / 2) + rnd.Next(jpeg.Length / 2 - 2)] = (byte)rnd.Next(256);
            }

            Outcome reference = Reference(jpeg, 1);
            Assert.Equal(reference, Run(jpeg, 1, maskRefine: true, pipeline: 0));
        }
    }
}
