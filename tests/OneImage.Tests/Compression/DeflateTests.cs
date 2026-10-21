using System.IO.Compression;
using OneImage.Compression;

namespace OneImage.Tests.Compression;

public class DeflateTests
{
    private readonly ITestOutputHelper _output;

    public DeflateTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Levels() => Enumerable.Range(0, 10).Select(l => new object[] { l });

    [Theory]
    [MemberData(nameof(Levels))]
    public void RoundTripsThroughOursAndSystem(int level)
    {
        foreach ((string name, byte[] data) in CompressionTestUtil.Corpus())
        {
            if (level >= 8 && data.Length > 1_000_000)
            {
                continue;
            }

            foreach (bool zlib in new[] { true, false })
            {
                byte[] z = Deflater.Compress(data, level, zlib);
                Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.Inflate(z, zlib)), $"ours {name} L{level} zlib={zlib}");
                Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.SystemDecompress(z, zlib)), $"system {name} L{level} zlib={zlib}");
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void IdenticalBytesAcrossRunsAndChunkings(int level)
    {
        byte[] data = CompressionTestUtil.Corpus().First(c => c.Name == "mixed").Data;
        byte[] a = Deflater.Compress(data, level);
        byte[] b = Deflater.Compress(data, level);
        Assert.Equal(a, b);

        foreach (int chunk in new[] { 1, 1000, 40000 })
        {
            var sink = new System.Buffers.ArrayBufferWriter<byte>();
            using var d = new Deflater(sink, level);
            for (int i = 0; i < data.Length; i += chunk)
            {
                d.Write(data.AsSpan(i, Math.Min(chunk, data.Length - i)));
            }

            d.Finish();
            Assert.Equal(a, sink.WrittenSpan.ToArray());
        }
    }

    [Fact]
    public void ReusedInstanceMatchesFreshOne()
    {
        byte[] d1 = CompressionTestUtil.Text(200_000, 1);
        byte[] d2 = CompressionTestUtil.ImageLike(300, 300, 2);
        var sink = new System.Buffers.ArrayBufferWriter<byte>();
        using var d = new Deflater(sink, 6);
        d.Write(d1);
        d.Finish();
        sink.Clear();
        d.Reset(6, true);
        d.Write(d2);
        d.Finish();
        Assert.Equal(Deflater.Compress(d2, 6), sink.WrittenSpan.ToArray());
    }

    [Fact]
    public void SizeAtLevel6IsCloseToSystemOptimal()
    {
        long ours = 0;
        long sys = 0;
        foreach ((string name, byte[] data) in CompressionTestUtil.Corpus())
        {
            int o = Deflater.Compress(data, 6).Length;
            int s = CompressionTestUtil.SystemCompress(data, CompressionLevel.Optimal, true).Length;
            int o9 = Deflater.Compress(data, 9).Length;
            int s9 = CompressionTestUtil.SystemCompress(data, CompressionLevel.SmallestSize, true).Length;
            _output.WriteLine($"{name,-14} in={data.Length,9} L6 ours={o,8} sys={s,8} ({(double)o / Math.Max(1, s):F3})  L9 ours={o9,8} sys={s9,8} ({(double)o9 / Math.Max(1, s9):F3})");
            Assert.True(o <= (s * 1.05) + 16, $"{name}: {o} vs {s}");
            ours += o;
            sys += s;
        }

        Assert.True(ours <= sys * 1.05, $"total {ours} vs {sys}");
    }

    [Fact]
    public void IncompressibleDataStaysNearInputSize()
    {
        byte[] data = CompressionTestUtil.Random(1_000_000, 99);
        byte[] z = Deflater.Compress(data, 6);
        Assert.True(z.Length < data.Length + (data.Length / 1000) + 64, $"{z.Length}");
        Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.SystemDecompress(z, true)));
    }

    [Fact]
    public void EmptyInputProducesValidStreams()
    {
        for (int level = 0; level <= 9; level++)
        {
            byte[] z = Deflater.Compress(ReadOnlySpan<byte>.Empty, level);
            Assert.Empty(CompressionTestUtil.SystemDecompress(z, true));
            Assert.Empty(CompressionTestUtil.Inflate(z, true));
        }
    }

    [Fact]
    public void ZlibHeaderMatchesLevelHint()
    {
        Assert.Equal(new byte[] { 0x78, 0x9C }, Deflater.Compress(new byte[] { 1 }, 6).AsSpan(0, 2).ToArray());
        Assert.Equal(new byte[] { 0x78, 0x01 }, Deflater.Compress(new byte[] { 1 }, 1).AsSpan(0, 2).ToArray());
        Assert.Equal(new byte[] { 0x78, 0xDA }, Deflater.Compress(new byte[] { 1 }, 9).AsSpan(0, 2).ToArray());
    }

    [Fact]
    public void SkewedAlphabetsUseLengthLimitedCodes()
    {
        // Fibonacci-like frequencies force very deep optimal Huffman trees, exercising the 15-bit limit.
        var data = new List<byte>();
        long a = 1;
        long b = 1;
        for (int s = 0; s < 26; s++)
        {
            for (long i = 0; i < Math.Min(a, 200_000); i++)
            {
                data.Add((byte)(s * 3));
            }

            long t = a + b;
            a = b;
            b = t;
        }

        byte[] arr = data.OrderBy(x => (x * 2654435761L) % 1013).ToArray();
        byte[] z = Deflater.Compress(arr, 6);
        Assert.True(arr.AsSpan().SequenceEqual(CompressionTestUtil.SystemDecompress(z, true)));
    }

    [Fact]
    public void LongRunsAndMaxDistanceMatches()
    {
        byte[] block = CompressionTestUtil.Random(32000, 5);
        byte[] data = block.Concat(block).Concat(new byte[100]).Concat(block).ToArray();
        for (int level = 1; level <= 9; level++)
        {
            byte[] z = Deflater.Compress(data, level);
            Assert.True(data.AsSpan().SequenceEqual(CompressionTestUtil.SystemDecompress(z, true)), $"L{level}");
            // Levels 1-3 do not hash the interior of long matches, so they miss the third copy.
            Assert.True(z.Length < (level >= 4 ? data.Length / 2 : data.Length * 3 / 4), $"L{level} {z.Length}");
        }
    }
}
