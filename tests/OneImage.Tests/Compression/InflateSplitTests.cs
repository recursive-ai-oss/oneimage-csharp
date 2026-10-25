using System.Buffers.Binary;
using System.IO.Compression;
using OneImage.Compression;
using OneImage.Tests.Png;

namespace OneImage.Tests.Compression;

/// <summary>
/// Inflater regression tests built from generated streams: a dynamic block whose codes are chosen so that a long literal is
/// followed by a long length/distance pair (the fast loop once decoded that pair from bits it had not counted), stored and
/// fixed blocks, and every way of cutting the input and output into pieces. <see cref="ZLibStream"/> is the reference.
/// </summary>
public class InflateSplitTests
{
    // --- stream generators --------------------------------------------------------------------------------------------------

    /// <summary>Everything a generated stream needs: the zlib bytes and what they must decode to.</summary>
    private sealed record Gen(string Name, byte[] Zlib, byte[] Expected);

    private static byte[] Wrap(DeflateBuilder b, byte[] expected) => b.ToArray(zlibWrap: true, uncompressedForAdler: expected);

    /// <summary>
    /// One dynamic block. Literal/length code: 'a' has 1 bit, twelve more literals and the end-of-block code have 2..14 bits, and
    /// 'x' and length symbol 284 (five extra bits) have 15. Distance code: symbols 0..13 have 1..14 bits, 28 and 29 (13 extra bits
    /// each) have 15. A unit "x + pair" is then 15 + 15 + 5 + 15 + 13 = 63 bits, the worst case for the fast loop's refill.
    /// </summary>
    private static Gen LongCodes(string name, int fillerLiterals, int units, int seed)
    {
        int[] lit = new int[286];
        lit[97] = 1;
        int[] mid = [98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 256];
        for (int i = 0; i < mid.Length; i++)
        {
            lit[mid[i]] = 2 + i;
        }

        lit['x'] = 15;
        lit[284] = 15;
        int[] dist = new int[30];
        for (int i = 0; i < 14; i++)
        {
            dist[i] = 1 + i;
        }

        dist[28] = 15;
        dist[29] = 15;

        var b = new DeflateBuilder();
        (uint[] lc, uint[] dc) = b.DynamicHeader(true, lit, dist);
        var exp = new List<byte>();
        var rnd = new Random(seed);

        void Lit(int s)
        {
            b.Code(lc[s], lit[s]);
            if (s < 256)
            {
                exp.Add((byte)s);
            }
        }

        for (int i = 0; i < fillerLiterals; i++)
        {
            Lit(rnd.Next(4) == 0 ? mid[rnd.Next(12)] : 'a');
        }

        for (int u = 0; u < units; u++)
        {
            int m = rnd.Next(6) switch { 0 => 0, 1 => 0, 2 => 1, 3 => 2, 4 => 3, _ => rnd.Next(12) };
            for (int i = 0; i < m; i++)
            {
                Lit(rnd.Next(3) == 0 ? mid[rnd.Next(12)] : 'a');
            }

            Lit('x');
            int length = 227 + rnd.Next(31);
            b.Code(lc[284], 15);
            b.Bits((uint)(length - 227), 5);
            int ds = rnd.Next(2) == 0 ? 29 : 28;
            int baseDist = DeflateTables.DistBase[ds];
            int d;
            if (exp.Count >= baseDist)
            {
                d = baseDist + rnd.Next(Math.Min(8191, exp.Count - baseDist) + 1);
            }
            else
            {
                ds = 13;
                d = DeflateTables.DistBase[ds] + rnd.Next(Math.Min((1 << DeflateTables.DistExtra[ds]) - 1, exp.Count - DeflateTables.DistBase[ds]) + 1);
            }

            b.Code(dc[ds], dist[ds]);
            b.Bits((uint)(d - DeflateTables.DistBase[ds]), DeflateTables.DistExtra[ds]);
            for (int i = 0; i < length; i++)
            {
                exp.Add(exp[exp.Count - d]);
            }
        }

        b.Code(lc[256], lit[256]);
        byte[] expected = [.. exp];
        return new Gen(name, Wrap(b, expected), expected);
    }

    private static Gen Fixed(string name, int symbols, int seed)
    {
        var b = new DeflateBuilder();
        b.FixedHeader(true);
        var exp = new List<byte>();
        var rnd = new Random(seed);
        for (int i = 0; i < symbols; i++)
        {
            if (exp.Count < 3 || rnd.Next(3) != 0)
            {
                int v = rnd.Next(4) == 0 ? rnd.Next(256) : 'a' + rnd.Next(3);
                b.FixedLit(v);
                exp.Add((byte)v);
                continue;
            }

            int length = rnd.Next(4) == 0 ? 258 : 3 + rnd.Next(60);
            int d = rnd.Next(3) switch { 0 => 1 + rnd.Next(4), 1 => 1 + rnd.Next(Math.Min(exp.Count, 300)), _ => 1 + rnd.Next(Math.Min(exp.Count, 32768)) };
            b.FixedMatch(length, d);
            for (int k = 0; k < length; k++)
            {
                exp.Add(exp[exp.Count - d]);
            }
        }

        b.FixedLit(256);
        byte[] expected = [.. exp];
        return new Gen(name, Wrap(b, expected), expected);
    }

    private static Gen Stored(string name, int[] sizes, int seed)
    {
        var b = new DeflateBuilder();
        var exp = new List<byte>();
        for (int i = 0; i < sizes.Length; i++)
        {
            byte[] data = CompressionTestUtil.Random(sizes[i], seed + i);
            b.Stored(data, i == sizes.Length - 1);
            exp.AddRange(data);
        }

        byte[] expected = [.. exp];
        return new Gen(name, Wrap(b, expected), expected);
    }

    private static Gen Library(string name, byte[] data, CompressionLevel level) =>
        new(name, CompressionTestUtil.SystemCompress(data, level, true), data);

    private static Gen[] Small() =>
    [
        LongCodes("long-codes-small", 17000, 4, 1),
        Fixed("fixed-small", 600, 2),
        Stored("stored-small", [0, 1, 300], 3),
        Library("system-small", CompressionTestUtil.Text(500, 4), CompressionLevel.Optimal),
    ];

    private static Gen[] Large() =>
    [
        LongCodes("long-codes", 26000, 3000, 11),
        LongCodes("long-codes-b", 40000, 3000, 12),
        Fixed("fixed", 40000, 13),
        Stored("stored", [65535, 1, 0, 70, 65535, 12345], 14),
        Library("system-image", CompressionTestUtil.ImageLike(640, 200, 15), CompressionLevel.Optimal),
    ];

    // --- driving --------------------------------------------------------------------------------------------------------

    /// <summary>Feeds <paramref name="z"/> in pieces chosen by <paramref name="nextIn"/> and collects output in pieces chosen by <paramref name="nextOut"/>.</summary>
    private static byte[] Drive(byte[] z, Func<int> nextIn, Func<int> nextOut)
    {
        using var inf = new Inflater(true);
        var result = new MemoryStream();
        int pos = 0;
        for (long calls = 0; calls < 20_000_000; calls++)
        {
            int take = Math.Min(nextIn(), z.Length - pos);
            byte[] buf = new byte[nextOut()];
            InflateStatus st = inf.Inflate(z.AsSpan(pos, take), buf, out int c, out int w);
            Assert.InRange(c, 0, take);
            result.Write(buf, 0, w);
            pos += c;
            if (st == InflateStatus.Done)
            {
                Assert.Equal(z.Length, pos);
                return result.ToArray();
            }

            if (st == InflateStatus.NeedInput && pos >= z.Length)
            {
                throw new ImageFormatException("truncated");
            }
        }

        throw new InvalidOperationException("no progress");
    }

    private static void AssertSame(byte[] expected, byte[] actual, string what)
    {
        Assert.True(expected.Length == actual.Length, $"{what}: length {actual.Length}, expected {expected.Length}");
        int bad = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(bad == expected.Length, $"{what}: first difference at byte {bad}");
    }

    // --- tests ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void GeneratedStreamsMatchTheSystemDecoder()
    {
        foreach (Gen g in Small().Concat(Large()))
        {
            AssertSame(g.Expected, CompressionTestUtil.SystemDecompress(g.Zlib, true), g.Name + " (generator vs ZLibStream)");
        }
    }

    [Fact]
    public void LongLiteralThenLongPairDecodesInOneCall()
    {
        foreach (Gen g in Large().Where(g => g.Name.StartsWith("long-codes", StringComparison.Ordinal)))
        {
            AssertSame(g.Expected, Drive(g.Zlib, () => int.MaxValue, () => g.Expected.Length + 8), g.Name);
        }
    }

    [Fact]
    public void EverySplitPointOfSmallStreams()
    {
        foreach (Gen g in Small())
        {
            for (int s = 0; s <= g.Zlib.Length; s++)
            {
                foreach (int outChunk in new[] { 1 << 20, 4099, 3 })
                {
                    int call = 0;
                    byte[] got = Drive(g.Zlib, () => call++ == 0 ? s : int.MaxValue, () => outChunk);
                    AssertSame(g.Expected, got, $"{g.Name} split at {s}, output pieces of {outChunk}");
                }
            }
        }
    }

    [Fact]
    public void EverySplitPointWithOneByteOutput()
    {
        // Slow, so only the streams that are short once decoded.
        foreach (Gen g in Small().Where(g => g.Expected.Length < 3000))
        {
            for (int s = 0; s <= g.Zlib.Length; s++)
            {
                int call = 0;
                AssertSame(g.Expected, Drive(g.Zlib, () => call++ == 0 ? s : int.MaxValue, () => 1), $"{g.Name} split at {s}, one-byte output");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RandomSchedulesOfLargeStreams(int seed)
    {
        foreach (Gen g in Large())
        {
            var rnd = new Random(seed * 7919);

            // One-byte input feeds with output pieces of mixed sizes (decoding never sees more than a byte at a time).
            AssertSame(g.Expected, Drive(g.Zlib, () => 1, () => 1 + rnd.Next(2000)), $"{g.Name} one-byte input, seed {seed}");

            // Mixed piece sizes on both sides: single bytes, tiny, and large.
            for (int round = 0; round < 6; round++)
            {
                int inMax = new[] { 7, 64, 700, 5000, 70000, 1 }[rnd.Next(6)];
                int outMax = new[] { 1, 5, 100, 4000, 40000, 300000 }[rnd.Next(6)];
                AssertSame(g.Expected, Drive(g.Zlib, () => 1 + rnd.Next(inMax), () => 1 + rnd.Next(outMax)), $"{g.Name} schedule {round}, seed {seed}, in<={inMax}, out<={outMax}");
            }
        }
    }

    [Fact]
    public void SplitOutputBufferExactlyAtTheEndOfTheData()
    {
        // The output fills exactly when the last byte is produced; the trailer must still be read in a later call.
        foreach (Gen g in Small())
        {
            using var inf = new Inflater(true);
            byte[] output = new byte[g.Expected.Length];
            InflateStatus st = inf.Inflate(g.Zlib, output, out int used, out int written);
            Assert.Equal(g.Expected.Length, written);
            AssertSame(g.Expected, output, g.Name);
            if (st != InflateStatus.Done)
            {
                Assert.False(inf.IsDone);
                Assert.Equal(InflateStatus.Done, inf.Drain(g.Zlib.AsSpan(used), out int more));
                Assert.Equal(g.Zlib.Length, used + more);
            }
        }
    }

    [Fact]
    public void DrainReportsABadChecksumAndATruncatedTrailer()
    {
        Gen g = Small()[0];
        for (int cut = 1; cut <= 4; cut++)
        {
            using var inf = new Inflater(true);
            byte[] output = new byte[g.Expected.Length];
            byte[] z = g.Zlib[..^cut];
            InflateStatus st = inf.Inflate(z, output, out int used, out _);
            Assert.NotEqual(InflateStatus.Done, st);
            Assert.Equal(InflateStatus.NeedInput, inf.Drain(z.AsSpan(used), out _));
        }

        byte[] bad = (byte[])g.Zlib.Clone();
        bad[^1] ^= 0x40;
        // Output sized exactly: the trailer is then read by Drain, not by the call that produced the last byte.
        Assert.Throws<ImageFormatException>(() =>
        {
            using var inf2 = new Inflater(true);
            byte[] output2 = new byte[g.Expected.Length];
            InflateStatus st2 = inf2.Inflate(bad, output2, out int used2, out _);
            if (st2 != InflateStatus.Done)
            {
                inf2.Drain(bad.AsSpan(used2), out _);
            }
        });
    }

    // --- PNG level ------------------------------------------------------------------------------------------------------

    internal static byte[] Chunk(string type, ReadOnlySpan<byte> data)
    {
        byte[] r = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, r.AsSpan(4));
        data.CopyTo(r.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(8 + data.Length), Crc32.Compute(r.AsSpan(4, 4 + data.Length)));
        return r;
    }

    /// <summary>Rebuilds a PNG with the same header and the given zlib stream as its only IDAT.</summary>
    internal static byte[] WithIdat(byte[] png, byte[] zlib, int idatChunk = 1 << 20)
    {
        using var ms = new MemoryStream();
        ms.Write(png, 0, 8);
        foreach ((string name, int off, int len, int data) in PngGen.Chunks(png))
        {
            if (name == "IDAT")
            {
                continue;
            }

            if (name == "IEND")
            {
                for (int p = 0; p < zlib.Length; p += idatChunk)
                {
                    ms.Write(Chunk("IDAT", zlib.AsSpan(p, Math.Min(idatChunk, zlib.Length - p))));
                }
            }

            ms.Write(png, off, 12 + len);
        }

        return ms.ToArray();
    }

    internal static byte[] IdatPayload(byte[] png)
    {
        using var ms = new MemoryStream();
        foreach (var c in PngGen.Chunks(png).Where(c => c.Name == "IDAT"))
        {
            ms.Write(png, c.Data, c.Length);
        }

        return ms.ToArray();
    }

    /// <summary>The same image with its pixel data stored uncompressed, which no Huffman fast loop can touch.</summary>
    internal static byte[] Restored(byte[] png)
    {
        byte[] raw = CompressionTestUtil.SystemDecompress(IdatPayload(png), true);
        var b = new DeflateBuilder();
        int pos = 0;
        do
        {
            int n = Math.Min(65535, raw.Length - pos);
            b.Stored(raw.AsSpan(pos, n), pos + n == raw.Length);
            pos += n;
        }
        while (pos < raw.Length);
        return WithIdat(png, b.ToArray(zlibWrap: true, uncompressedForAdler: raw));
    }

    public static IEnumerable<object[]> PageFiles() => [["page-a.png"], ["page-b.png"]];

    private static byte[] Page(string name) => File.ReadAllBytes(PngTestUtil.DataDir("png", "pages", name));

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void PageImagesInflateToTheSystemDecoderOutput(string name)
    {
        byte[] png = Page(name);
        byte[] z = IdatPayload(png);
        byte[] expected = CompressionTestUtil.SystemDecompress(z, true);
        AssertSame(expected, Drive(z, () => int.MaxValue, () => expected.Length + 1), name + " whole");
        var rnd = new Random(5);
        AssertSame(expected, Drive(z, () => 1 + rnd.Next(100), () => 1 + rnd.Next(70000)), name + " pieces");
        AssertSame(expected, Drive(z, () => 1 + rnd.Next(30000), () => 1 + rnd.Next(20)), name + " tiny output");
    }

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void PageImagesDecodeLikeTheirStoredCopies(string name)
    {
        byte[] png = Page(name);
        Decoded stored = PngTestUtil.Decode(Restored(png));
        Decoded real = PngTestUtil.Decode(png);
        Assert.Equal(stored.Width, real.Width);
        Assert.Equal(stored.Height, real.Height);
        AssertSame(stored.Data, real.Data, name + " whole decode");

        // The same file with its data cut into many small chunks (the trailer lands in a different place).
        AssertSame(stored.Data, PngTestUtil.Decode(WithIdat(png, IdatPayload(png), 4099)).Data, name + " in 4099-byte chunks");
    }

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void PageImagesDecodeAsBandsAndFromAStream(string name)
    {
        byte[] png = Page(name);
        Decoded whole = PngTestUtil.Decode(Restored(png));
        int rowBytes = whole.Width * whole.Format.BytesPerPixel;
        using var ms = new MemoryStream(png);
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(ms, ImageLimits.Default);
        FrameInfo fi = info.Frames[0];
        foreach (int rows in new[] { 1, 37, fi.Height / 2 })
        {
            var sink = new CollectSink(fi.Width, rows, fi.Format);
            ms.Position = 0;
            PngFormat.Instance.Decoder.DecodeFrame(ms, info, 0, new DecodeRequest(new ImageRect(0, 0, fi.Width, rows)), sink, ImageLimits.Default, CancellationToken.None);
            AssertSame(whole.Data.AsSpan(0, rows * rowBytes).ToArray(), sink.Data, $"{name} top {rows} rows");
        }
    }

    [Fact]
    public void WholeReadChecksTheZlibTrailer()
    {
        byte[] png = Page("page-a.png");
        byte[] z = IdatPayload(png);

        // A wrong Adler-32 with every pixel intact: a whole read has to refuse it, whatever chunking puts the trailer where.
        byte[] bad = (byte[])z.Clone();
        bad[^1] ^= 0x01;
        foreach (int chunk in new[] { 1 << 20, 4099, 32768 - 4, 32768 - 3, 32768 - 1, 32768, 32769, 65536 })
        {
            Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode(WithIdat(png, bad, chunk)));
        }

        // A stream cut before its trailer is incomplete too.
        Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode(WithIdat(png, z[..^4])));
    }

    [Fact]
    public void TrailerInALaterRefillIsStillChecked()
    {
        // Two IDAT chunks cut at every offset around the 4-byte trailer, so the trailer is read by a later refill of the input buffer
        // (the case where the last row is complete before the trailer has been seen).
        byte[] png = Page("page-b.png");
        byte[] z = IdatPayload(png);
        byte[] expected = PngTestUtil.Decode(Restored(png)).Data;
        for (int fromEnd = 1; fromEnd <= 6; fromEnd++)
        {
            byte[] two = WithTwoChunks(png, z, z.Length - fromEnd);
            AssertSame(expected, PngTestUtil.Decode(two).Data, $"cut {fromEnd} bytes before the end");
            byte[] bad = (byte[])z.Clone();
            bad[^3] ^= 0x10;
            Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode(WithTwoChunks(png, bad, z.Length - fromEnd)));
        }
    }

    internal static byte[] WithTwoChunks(byte[] png, byte[] zlib, int firstLength)
    {
        using var ms = new MemoryStream();
        ms.Write(png, 0, 8);
        foreach ((string name, int off, int len, int _) in PngGen.Chunks(png))
        {
            if (name == "IDAT")
            {
                continue;
            }

            if (name == "IEND")
            {
                ms.Write(Chunk("IDAT", zlib.AsSpan(0, firstLength)));
                ms.Write(Chunk("IDAT", zlib.AsSpan(firstLength)));
            }

            ms.Write(png, off, 12 + len);
        }

        return ms.ToArray();
    }
}
