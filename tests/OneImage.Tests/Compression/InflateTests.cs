using System.Diagnostics;
using System.IO.Compression;
using OneImage.Compression;

namespace OneImage.Tests.Compression;

public class InflateTests
{
    private static readonly int[] TwoDist = { 1, 1 };

    public static IEnumerable<object[]> CorpusNames() => CompressionTestUtil.Corpus().Select(c => new object[] { c.Name });

    private static byte[] Get(string name) => CompressionTestUtil.Corpus().First(c => c.Name == name).Data;

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void DecodesSystemOutputAtEveryLevel(string name)
    {
        byte[] data = Get(name);
        foreach (CompressionLevel level in new[] { CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        {
            byte[] z = CompressionTestUtil.SystemCompress(data, level, true);
            Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(z, true)), $"{name} zlib {level}");
            byte[] raw = CompressionTestUtil.SystemCompress(data, level, false);
            Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(raw, false)), $"{name} raw {level}");
        }
    }

    [Theory]
    [InlineData("image640x200")]
    [InlineData("mixed")]
    [InlineData("one")]
    [InlineData("empty")]
    public void OneByteChunksMatchWholeBuffer(string name)
    {
        byte[] data = Get(name);
        byte[] z = CompressionTestUtil.SystemCompress(data, CompressionLevel.Optimal, true);
        Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(z, true, inChunk: 1, outChunk: 1)));
        Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(z, true, inChunk: 1, outChunk: 1 << 16)));
        Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(z, true, inChunk: 7, outChunk: 13)));
        Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(z, true, inChunk: 1 << 20, outChunk: 1)));
    }

    [Fact]
    public void StoredBlockOf65535()
    {
        byte[] data = CompressionTestUtil.Random(65535, 11);
        byte[] raw = new DeflateBuilder().Stored(data, true).ToArray();
        Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(raw, false)));
        Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(raw, false, inChunk: 1, outChunk: 100)));
        Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.SystemDecompress(raw, false)));
    }

    [Fact]
    public void EmptyStoredAndMultipleBlocks()
    {
        byte[] a = CompressionTestUtil.Random(1000, 12);
        byte[] b = CompressionTestUtil.Random(300, 13);
        var bld = new DeflateBuilder();
        bld.Stored(a, false);
        bld.Stored(ReadOnlySpan<byte>.Empty, false);
        bld.FixedHeader(false);
        bld.FixedLit('x');
        bld.FixedLit(256);
        bld.Stored(b, true);
        byte[] expected = a.Concat(new[] { (byte)'x' }).Concat(b).ToArray();
        byte[] raw = bld.ToArray();
        Assert.True(expected.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(raw, false)));
        Assert.True(expected.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(raw, false, 1, 1)));
        Assert.True(expected.AsSpan().SequenceEqual(CompressionTestUtil.SystemDecompress(raw, false)));
    }

    [Fact]
    public void FixedHuffmanWithMaxDistanceAndMaxLength()
    {
        byte[] history = CompressionTestUtil.Random(32768, 14);
        var bld = new DeflateBuilder();
        bld.Stored(history, false);
        bld.FixedHeader(true);
        bld.FixedMatch(258, 32768);
        bld.FixedMatch(258, 1);
        bld.FixedMatch(3, 32768);
        bld.FixedLit(256);
        var expected = new List<byte>(history);
        for (int i = 0; i < 258; i++)
        {
            expected.Add(expected[expected.Count - 32768]);
        }

        for (int i = 0; i < 258; i++)
        {
            expected.Add(expected[expected.Count - 1]);
        }

        for (int i = 0; i < 3; i++)
        {
            expected.Add(expected[expected.Count - 32768]);
        }

        byte[] raw = bld.ToArray();
        Assert.Equal(expected.ToArray(), CompressionTestUtil.Inflate(raw, false));
        Assert.Equal(expected.ToArray(), CompressionTestUtil.Inflate(raw, false, 1, 3));
        Assert.Equal(expected.ToArray(), CompressionTestUtil.SystemDecompress(raw, false));
    }

    [Fact]
    public void DynamicBlockWithMaximumCodeLengths()
    {
        // Lengths 1..15 on one symbol each plus a second 15-bit symbol: a complete tree whose deepest codes use 15 bits.
        int[] lit = new int[286];
        int[] syms = { 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110 };
        for (int i = 0; i < syms.Length; i++)
        {
            lit[syms[i]] = i + 1;
        }

        lit[256] = 15;
        lit[111] = 15;
        var bld = new DeflateBuilder();
        (uint[] lc, _) = bld.DynamicHeader(true, lit, TwoDist);
        var expected = new List<byte>();
        foreach (int s in new[] { 111, 97, 110, 98, 111, 111, 105, 97 })
        {
            bld.Code(lc[s], lit[s]);
            expected.Add((byte)s);
        }

        bld.Code(lc[256], 15);
        byte[] raw = bld.ToArray();
        Assert.Equal(expected.ToArray(), CompressionTestUtil.Inflate(raw, false));
        Assert.Equal(expected.ToArray(), CompressionTestUtil.Inflate(raw, false, 1, 1));
        Assert.Equal(expected.ToArray(), CompressionTestUtil.SystemDecompress(raw, false));
    }

    [Fact]
    public void DynamicBlockWithMatchesAndTwoDistanceCodes()
    {
        int[] lit = new int[258];
        lit[97] = 2;
        lit[98] = 2;
        lit[256] = 2;
        lit[257] = 2; // length 3
        var bld = new DeflateBuilder();
        (uint[] lc, uint[] dc) = bld.DynamicHeader(true, lit, TwoDist);
        bld.Code(lc[97], 2);
        bld.Code(lc[98], 2);
        bld.Code(lc[257], 2);
        bld.Code(dc[1], 1); // distance 2
        bld.Code(lc[257], 2);
        bld.Code(dc[0], 1); // distance 1
        bld.Code(lc[256], 2);
        byte[] expected = System.Text.Encoding.ASCII.GetBytes("ababaaaa");
        byte[] raw = bld.ToArray();
        Assert.Equal(expected, CompressionTestUtil.Inflate(raw, false));
        Assert.Equal(expected, CompressionTestUtil.SystemDecompress(raw, false));
    }

    [Fact]
    public void ZlibWrapperWithHandBuiltStream()
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes("hello");
        byte[] z = new DeflateBuilder().Stored(data, true).ToArray(true, data);
        Assert.Equal(data, CompressionTestUtil.Inflate(z, true, 1, 1));
    }

    [Fact]
    public void UnconsumedTailIsReportedAfterDone()
    {
        byte[] data = CompressionTestUtil.Text(5000, 21);
        byte[] z = CompressionTestUtil.SystemCompress(data, CompressionLevel.Optimal, true);
        byte[] tail = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        byte[] all = z.Concat(tail).ToArray();
        foreach (int chunk in new[] { int.MaxValue, 1000, 64, 1 })
        {
            using var inf = new Inflater(true);
            byte[] output = new byte[data.Length + 10];
            int pos = 0;
            int outPos = 0;
            while (true)
            {
                int n = Math.Min(chunk, all.Length - pos);
                InflateStatus st = inf.Inflate(all.AsSpan(pos, n), output.AsSpan(outPos), out int c, out int w);
                pos += c;
                outPos += w;
                if (st == InflateStatus.Done)
                {
                    break;
                }

                Assert.NotEqual(InflateStatus.NeedOutput, st);
            }

            Assert.Equal(z.Length, pos);
            Assert.Equal(data.Length, outPos);
        }
    }

    [Fact]
    public void ReuseAfterReset()
    {
        byte[] a = CompressionTestUtil.Text(10000, 31);
        byte[] z = CompressionTestUtil.SystemCompress(a, CompressionLevel.Optimal, true);
        using var inf = new Inflater(true);
        for (int i = 0; i < 3; i++)
        {
            inf.Reset(true);
            byte[] o = new byte[a.Length];
            Assert.Equal(InflateStatus.Done, inf.Inflate(z, o, out _, out int w));
            Assert.Equal(a.Length, w);
            Assert.Equal(a, o);
        }
    }

    // ---- malformed input --------------------------------------------------------------------------

    [Fact]
    public void TruncatedStreamsFail()
    {
        byte[] data = CompressionTestUtil.ImageLike(200, 50, 41);
        byte[] z = CompressionTestUtil.SystemCompress(data, CompressionLevel.Optimal, true);
        for (int cut = 0; cut < z.Length; cut += Math.Max(1, z.Length / 97))
        {
            byte[] t = z.AsSpan(0, cut).ToArray();
            Assert.Throws<ImageFormatException>(() => CompressionTestUtil.Inflate(t, true));
            Assert.Throws<ImageFormatException>(() => CompressionTestUtil.Inflate(t, true, 1, 100));
        }
    }

    public static IEnumerable<object[]> BadStreams()
    {
        yield return new object[] { "blocktype3", false, new byte[] { 0x07 } };
        yield return new object[] { "storedlen", false, new byte[] { 0x01, 0x05, 0x00, 0x00, 0x00, 1, 2, 3, 4, 5 } };
        yield return new object[] { "zlibheader", true, new byte[] { 0x78, 0x00, 0x03, 0x00 } };
        yield return new object[] { "zlibmethod", true, new byte[] { 0x79, 0x9C, 0x03, 0x00 } };
        yield return new object[] { "zlibdict", true, new byte[] { 0x78, 0xBB, 0x03, 0x00 } };

        // Fixed block: 'a' then distance 2 reaches before the start.
        var b = new DeflateBuilder().FixedHeader(true);
        b.FixedLit('a');
        b.FixedMatch(3, 2);
        b.FixedLit(256);
        yield return new object[] { "distance-too-far", false, b.ToArray() };

        // Invalid distance symbol 30 in a fixed block.
        b = new DeflateBuilder().FixedHeader(true);
        b.FixedLit('a');
        b.FixedLit(257);
        b.Code(30, 5);
        b.FixedLit(256);
        yield return new object[] { "distance-symbol-30", false, b.ToArray() };

        // Invalid literal/length symbol 286.
        b = new DeflateBuilder().FixedHeader(true);
        b.FixedLit(286);
        yield return new object[] { "length-symbol-286", false, b.ToArray() };

        // Oversubscribed literal tree: three 1-bit codes.
        int[] lit = new int[257];
        lit[0] = 1;
        lit[1] = 1;
        lit[256] = 1;
        b = new DeflateBuilder();
        b.DynamicHeader(true, lit, TwoDist);
        yield return new object[] { "oversubscribed", false, b.ToArray() };

        // Incomplete literal tree (three 2-bit codes).
        lit = new int[257];
        lit[0] = 2;
        lit[1] = 2;
        lit[256] = 2;
        b = new DeflateBuilder();
        b.DynamicHeader(true, lit, TwoDist);
        b.Bits(0, 16);
        yield return new object[] { "incomplete", false, b.ToArray() };

        // Missing end-of-block code.
        lit = new int[257];
        lit[0] = 1;
        lit[1] = 1;
        b = new DeflateBuilder();
        b.DynamicHeader(true, lit, TwoDist);
        b.Bits(0, 16);
        yield return new object[] { "no-eob", false, b.ToArray() };

        // Too many length codes (HLIT = 288).
        b = new DeflateBuilder();
        b.Bits(1, 1);
        b.Bits(2, 2);
        b.Bits(31, 5);
        b.Bits(0, 5);
        b.Bits(0, 4);
        b.Bits(0, 24);
        yield return new object[] { "hlit-288", false, b.ToArray() };

        // Bad Adler-32.
        byte[] data = System.Text.Encoding.ASCII.GetBytes("adler");
        byte[] z = new DeflateBuilder().Stored(data, true).ToArray(true, data);
        z[^1] ^= 0x55;
        yield return new object[] { "adler", true, z };
    }

    [Theory]
    [MemberData(nameof(BadStreams))]
    public void MalformedStreamsThrowFormatException(string name, bool zlib, byte[] stream)
    {
        _ = name;
        Assert.Throws<ImageFormatException>(() => CompressionTestUtil.Inflate(stream, zlib));
        Assert.Throws<ImageFormatException>(() => CompressionTestUtil.Inflate(stream, zlib, 1, 1));
    }

    [Fact]
    public void RandomMutationsNeverHangOrCrash()
    {
        var rnd = new Random(12345);
        var seeds = new List<(byte[] Data, bool Zlib)>();
        foreach (string name in new[] { "image640x200", "text300k", "mixed", "repeat" })
        {
            byte[] d = Get(name);
            seeds.Add((CompressionTestUtil.SystemCompress(d.AsSpan(0, Math.Min(d.Length, 40000)).ToArray(), CompressionLevel.Optimal, true), true));
            seeds.Add((CompressionTestUtil.SystemCompress(d.AsSpan(0, Math.Min(d.Length, 40000)).ToArray(), CompressionLevel.Fastest, false), false));
        }

        var sw = Stopwatch.StartNew();
        int ok = 0;
        int bad = 0;
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("ONEIMAGE_MUTATION_ITERS"), out int env) ? env : 3000;
        for (int iter = 0; iter < iterations; iter++)
        {
            (byte[] orig, bool zlib) = seeds[rnd.Next(seeds.Count)];
            byte[] m = (byte[])orig.Clone();
            int flips = 1 + rnd.Next(4);
            for (int f = 0; f < flips; f++)
            {
                m[rnd.Next(m.Length)] ^= (byte)(1 << rnd.Next(8));
            }

            if (rnd.Next(4) == 0)
            {
                m = m.AsSpan(0, rnd.Next(m.Length)).ToArray();
            }

            try
            {
                CompressionTestUtil.Inflate(m, zlib, rnd.Next(2) == 0 ? int.MaxValue : 1 + rnd.Next(50), 1 + rnd.Next(5000), 8_000_000, 0, false);
                ok++;
            }
            catch (ImageException)
            {
                bad++;
            }
        }

        Assert.True(bad > 100, $"mutations were not exercising errors (ok={ok}, bad={bad})");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60) || iterations > 3000);
    }

    // ---- expansion guard --------------------------------------------------------------------------

    /// <summary>Builds a stream that expands to about <paramref name="targetBytes"/> of zeros at nearly the DEFLATE maximum ratio.</summary>
    private static byte[] Bomb(long targetBytes)
    {
        var bld = new DeflateBuilder();
        bld.Stored(new byte[] { 0 }, false);
        int[] lit = new int[286];
        lit[256] = 1;
        lit[285] = 1;
        (uint[] lc, uint[] dc) = bld.DynamicHeader(true, lit, TwoDist);
        long matches = targetBytes / 258;
        for (long i = 0; i < matches; i++)
        {
            bld.Code(lc[285], 1);
            bld.Code(dc[0], 1);
        }

        bld.Code(lc[256], 1);
        return bld.ToArray();
    }

    [Fact]
    public void ZipBombStopsAtOutputCapWithSmallMemory()
    {
        byte[] bomb = Bomb(1L << 30);
        Assert.True(bomb.Length < 2_000_000);
        long cap = 10_000_000;
        long allocBefore = GC.GetTotalAllocatedBytes(true);
        using var inf = new Inflater(false, cap);
        byte[] buf = new byte[65536];
        long delivered = 0;
        int pos = 0;
        Assert.Throws<ImageLimitException>(() =>
        {
            while (true)
            {
                InflateStatus st = inf.Inflate(bomb.AsSpan(pos), buf, out int c, out int w);
                pos += c;
                delivered += w;
                if (st == InflateStatus.Done)
                {
                    break;
                }
            }
        });
        Assert.True(delivered <= cap, $"delivered {delivered}");
        Assert.True(GC.GetTotalAllocatedBytes(true) - allocBefore < 4_000_000);
    }

    [Fact]
    public void ZipBombStopsAtExpansionRatio()
    {
        byte[] bomb = Bomb(200_000_000);
        using var inf = new Inflater(false, long.MaxValue, 100);
        byte[] buf = new byte[65536];
        long delivered = 0;
        Assert.Throws<ImageLimitException>(() =>
        {
            int pos = 0;
            while (true)
            {
                InflateStatus st = inf.Inflate(bomb.AsSpan(pos), buf, out int c, out int w);
                pos += c;
                delivered += w;
                if (st == InflateStatus.Done)
                {
                    break;
                }
            }
        });
        Assert.True(delivered < 100L * bomb.Length + 300_000, $"delivered {delivered}");
    }

    [Fact]
    public void DefaultRatioAllowsLegitimateHighlyCompressibleData()
    {
        byte[] zeros = new byte[20_000_000];
        byte[] z = CompressionTestUtil.SystemCompress(zeros, CompressionLevel.Optimal, true);
        byte[] back = CompressionTestUtil.Inflate(z, true, maxOut: 20_000_000, ratio: 1100);
        Assert.Equal(zeros.Length, back.Length);
    }

    [Fact]
    public void BombWithinCapDecodes()
    {
        byte[] bomb = Bomb(50_000_000);
        byte[] o = CompressionTestUtil.Inflate(bomb, false, maxOut: 60_000_000);
        Assert.Equal((50_000_000 / 258 * 258) + 1, o.Length);
    }

    [Fact]
    public void CorpusFilesBehaveAsNamed()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "compression");
        Assert.True(Directory.Exists(dir), dir);
        int n = 0;
        foreach (string f in Directory.GetFiles(dir))
        {
            string file = Path.GetFileName(f);
            bool zlib = f.EndsWith(".zlib", StringComparison.Ordinal);
            byte[] bytes = File.ReadAllBytes(f);
            if (file.StartsWith("valid-", StringComparison.Ordinal))
            {
                byte[] got = CompressionTestUtil.Inflate(bytes, zlib);
                Assert.True(got.AsSpan().SequenceEqual(CompressionTestUtil.SystemDecompress(bytes, zlib)), file);
            }
            else if (file.StartsWith("bad-", StringComparison.Ordinal))
            {
                Assert.Throws<ImageFormatException>(() => CompressionTestUtil.Inflate(bytes, zlib));
            }

            n++;
        }

        Assert.True(n > 0);
    }
}
