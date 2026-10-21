using System.Diagnostics;

namespace OneImage.Tests.Jpeg;

public class JpegRobustnessTests
{
    private static readonly string[] s_samples =
    [
        "base-420", "base-444-restart-blocks", "prog-420", "arith-420", "arith-prog-420", "p12-420", "cmyk", "ycck", "gray", "prog-gray",
    ];

    public static TheoryData<string> Samples()
    {
        var d = new TheoryData<string>();
        foreach (string s in s_samples)
        {
            d.Add(s);
        }

        return d;
    }

    /// <summary>Decodes everything and reports whether it succeeded; anything but the library's own exceptions fails the test.</summary>
    private static bool TryDecodeAll(byte[] data, ImageLimits? limits = null)
    {
        try
        {
            JpegTestSupport.Decode(data, 1, null, limits);
            return true;
        }
        catch (ImageFormatException)
        {
            return false;
        }
        catch (ImageLimitException)
        {
            return false;
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void TruncatedFilesFailCleanly(string name)
    {
        byte[] full = JpegTestSupport.Read(name);
        int succeeded = 0;
        var sw = Stopwatch.StartNew();
        for (int len = 0; len < full.Length; len += Math.Max(1, full.Length / 97))
        {
            if (TryDecodeAll(full.AsSpan(0, len).ToArray()))
            {
                succeeded++;
            }
        }

        // Cutting a progressive file between scans is still a decodable (lower quality) image, but never most cuts.
        Assert.True(succeeded < 40, $"{name}: {succeeded} truncations decoded");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData("base-420")]
    [InlineData("base-444-restart-blocks")]
    [InlineData("gray")]
    [InlineData("arith-420")]
    public void SequentialFileTruncatedInsideScanIsRejected(string name)
    {
        byte[] full = JpegTestSupport.Read(name);
        int sos = full.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA });
        // The arithmetic decoder may legitimately run a few bytes past the end of its data.
        int slack = name.StartsWith("arith", StringComparison.Ordinal) ? 40 : 8;
        for (int len = sos + 20; len < full.Length - slack; len += 7)
        {
            Assert.False(TryDecodeAll(full.AsSpan(0, len).ToArray()), $"{name} cut at {len} of {full.Length} decoded");
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void FixedSeedMutationsNeverEscape(string name)
    {
        byte[] original = JpegTestSupport.Read(name);
        var rnd = new Random(20261021 ^ name.GetHashCode(StringComparison.Ordinal));
        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < 300; iter++)
        {
            byte[] m = (byte[])original.Clone();
            int edits = 1 + rnd.Next(4);
            for (int e = 0; e < edits; e++)
            {
                int pos = rnd.Next(m.Length);
                switch (rnd.Next(4))
                {
                    case 0:
                        m[pos] = (byte)rnd.Next(256);
                        break;
                    case 1:
                        m[pos] ^= (byte)(1 << rnd.Next(8));
                        break;
                    case 2:
                        m[pos] = 0xFF;
                        break;
                    default:
                        // Aim at the headers, where sizes and table definitions live.
                        m[rnd.Next(Math.Min(m.Length, 700))] = (byte)rnd.Next(256);
                        break;
                }
            }

            if (rnd.Next(5) == 0)
            {
                m = m.AsSpan(0, rnd.Next(m.Length)).ToArray();
            }

            _ = TryDecodeAll(m, new ImageLimits { MaxPixelsPerFrame = 4_000_000 });
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"{name}: mutation run took {sw.Elapsed}");
    }

    private static byte[] Craft(Action<MemoryStream> body)
    {
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        body(ms);
        return ms.ToArray();
    }

    private static void Segment(MemoryStream ms, byte marker, params byte[] payload)
    {
        ms.WriteByte(0xFF);
        ms.WriteByte(marker);
        ms.WriteByte((byte)((payload.Length + 2) >> 8));
        ms.WriteByte((byte)(payload.Length + 2));
        ms.Write(payload);
    }

    private static byte[] Sof(int width, int height, byte marker = 0xC0, params byte[] comps)
    {
        byte[] c = comps.Length > 0 ? comps : [1, 0x11, 0];
        var p = new List<byte> { 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)(c.Length / 3) };
        p.AddRange(c);
        return Craft(ms => Segment(ms, marker, [.. p]));
    }

    [Fact]
    public void AbsurdDimensionsAreRefusedBeforeAllocating()
    {
        byte[] data = Sof(65535, 65535);
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var ms = new MemoryStream(data);
        Assert.Throws<ImageLimitException>(() => JpegFormat.Instance.ReadInfo(ms, ImageLimits.Default));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
    }

    [Fact]
    public void ZeroSizeAndDeferredHeightAreRejected()
    {
        foreach ((int w, int h) in new[] { (0, 10), (10, 0) })
        {
            using var ms = new MemoryStream(Sof(w, h));
            Assert.Throws<ImageFormatException>(() => JpegFormat.Instance.ReadInfo(ms, ImageLimits.Default));
        }
    }

    [Fact]
    public void LargeProgressiveFrameWithoutDataDoesNotAllocateTheCoefficients()
    {
        // 16000 x 16000 passes the pixel limit, so the header is accepted; the data is absent.
        byte[] sof = Sof(16000, 16000, 0xC2, 1, 0x22, 0, 2, 0x11, 0, 3, 0x11, 0);
        byte[] data = Craft(ms =>
        {
            ms.Write(sof.AsSpan(2));
            Segment(ms, 0xDB, [.. new byte[] { 0 }, .. Enumerable.Repeat((byte)1, 64)]);
            Segment(ms, 0xC4, [0x00, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            Segment(ms, 0xDA, 3, 1, 0, 2, 0, 3, 0, 0, 0, 0);
            ms.Write([0x00, 0x00, 0x00]);
        });
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<Exception>(() => JpegTestSupport.Decode(data, 1, new ImageRect(0, 0, 64, 64), new ImageLimits { MaxPixelsPerFrame = 1L << 29 }));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64_000_000, "allocated far more than the data justified");
    }

    [Fact]
    public void MalformedHeadersAreFormatErrors()
    {
        var cases = new List<byte[]>
        {
            // SOS before any frame header
            Craft(ms => Segment(ms, 0xDA, 1, 1, 0, 0, 63, 0)),
            // two frame headers
            Craft(ms =>
            {
                ms.Write(Sof(8, 8).AsSpan(2));
                ms.Write(Sof(8, 8).AsSpan(2));
            }),
            // oversubscribed Huffman table
            Craft(ms =>
            {
                ms.Write(Sof(8, 8).AsSpan(2));
                Segment(ms, 0xC4, [0x00, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3]);
            }),
            // Huffman table claims more symbols than the segment holds
            Craft(ms =>
            {
                ms.Write(Sof(8, 8).AsSpan(2));
                Segment(ms, 0xC4, [0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 200, 1]);
            }),
            // sampling factor zero
            Sof(8, 8, 0xC0, 1, 0x01, 0),
            // five components
            Sof(8, 8, 0xC0, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 4, 0x11, 0, 5, 0x11, 0),
            // segment length that runs past the end
            Craft(ms => ms.Write([0xFF, 0xE0, 0xFF, 0xFF, 1, 2, 3])),
            // lossless process
            Sof(8, 8, 0xC3),
            // restart interval segment with wrong size
            Craft(ms => Segment(ms, 0xDD, 1, 2, 3)),
            // quantization table with bad precision nibble
            Craft(ms => Segment(ms, 0xDB, [0x20, .. new byte[64]])),
            // scan refers to an unknown component
            Craft(ms =>
            {
                ms.Write(Sof(8, 8).AsSpan(2));
                Segment(ms, 0xDA, 1, 9, 0, 0, 63, 0);
            }),
            // scan uses a quantization table that was never defined
            Craft(ms =>
            {
                ms.Write(Sof(8, 8).AsSpan(2));
                Segment(ms, 0xC4, [0x00, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
                Segment(ms, 0xC4, [0x10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
                Segment(ms, 0xDA, 1, 1, 0, 0, 63, 0);
                ms.Write([0, 0, 0, 0]);
            }),
            // empty input after SOI
            Craft(_ => { }),
        };
        foreach (byte[] c in cases)
        {
            Assert.False(TryDecodeAll(c));
        }
    }

    [Fact]
    public void HugeRestartIntervalAndTinyIntervalBothFinish()
    {
        byte[] data = JpegTestSupport.Read("base-444-restart-blocks");
        int dri = data.AsSpan().IndexOf(new byte[] { 0xFF, 0xDD });
        Assert.True(dri > 0);
        foreach (ushort ri in new ushort[] { 0xFFFF, 1, 2 })
        {
            byte[] m = (byte[])data.Clone();
            m[dri + 4] = (byte)(ri >> 8);
            m[dri + 5] = (byte)ri;
            _ = TryDecodeAll(m);
        }
    }

    [Fact]
    public void MetadataLimitIsEnforced()
    {
        byte[] data = Craft(ms =>
        {
            for (int i = 0; i < 5; i++)
            {
                Segment(ms, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. new byte[60000]]);
            }

            ms.Write(Sof(8, 8).AsSpan(2));
        });
        using var s = new MemoryStream(data);
        Assert.Throws<ImageLimitException>(() => JpegFormat.Instance.ReadInfo(s, new ImageLimits { MaxMetadataBytes = 100_000 }));
    }
}
