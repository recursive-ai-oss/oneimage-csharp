using OneImage.Compression;

namespace OneImage.Tests.Compression;

public class ChecksumTests
{
    private readonly ITestOutputHelper _output;

    public ChecksumTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReportHardwarePaths() =>
        _output.WriteLine($"HW: Vector128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated} Vector256={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} Vector512={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated} Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported} Pclmulqdq={System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported}");
    [Fact]
    public void Crc32PublishedVectors()
    {
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        Assert.Equal(0x414FA339u, Crc32.Compute("The quick brown fox jumps over the lazy dog"u8));
        // PNG: CRC of the IEND chunk type.
        Assert.Equal(0xAE426082u, Crc32.Compute("IEND"u8));
    }

    [Fact]
    public void Crc32MatchesReferenceOnAllSizesAndAlignments()
    {
        byte[] data = CompressionTestUtil.Random(5000, 1);
        for (int len = 0; len <= 300; len++)
        {
            for (int off = 0; off < 3; off++)
            {
                ReadOnlySpan<byte> s = data.AsSpan(off, len);
                Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(s), Crc32.Compute(s));
            }
        }

        byte[] big = CompressionTestUtil.Random(3_000_001, 2);
        Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(big), Crc32.Compute(big));
    }

    [Fact]
    public void Crc32IsIncremental()
    {
        byte[] data = CompressionTestUtil.Random(100_000, 3);
        uint whole = Crc32.Compute(data);
        foreach (int chunk in new[] { 1, 7, 63, 64, 65, 1000, 4096 })
        {
            uint c = 0;
            for (int i = 0; i < data.Length; i += chunk)
            {
                c = Crc32.Update(c, data.AsSpan(i, Math.Min(chunk, data.Length - i)));
            }

            Assert.Equal(whole, c);
        }
    }

    [Fact]
    public void Crc32TableAndAcceleratedPathsAgree()
    {
        byte[] data = CompressionTestUtil.Random(10_000, 4);
        for (int len = 0; len <= 200; len++)
        {
            Assert.Equal(~Crc32.UpdateTable(0xFFFFFFFF, data.AsSpan(1, len)), Crc32.Compute(data.AsSpan(1, len)));
        }
    }

    [Fact]
    public void Adler32PublishedVectors()
    {
        Assert.Equal(1u, Adler32.Update(Adler32.Initial, ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x11E60398u, Adler32.Update(Adler32.Initial, "Wikipedia"u8));
        Assert.Equal(0x024D0127u, Adler32.Update(Adler32.Initial, "abc"u8));
    }

    [Fact]
    public void Adler32VectorMatchesScalar()
    {
        var rnd = new Random(7);
        byte[] data = CompressionTestUtil.Random(40_000, 5);
        for (int i = 0; i < 400; i++)
        {
            int len = i < 80 ? i : rnd.Next(0, 20_000);
            int off = rnd.Next(0, 5);
            uint start = i % 3 == 0 ? 1u : (uint)rnd.Next();
            start = ((start >> 16) % 65521 << 16) | ((start & 0xFFFF) % 65521);
            Assert.Equal(Adler32.UpdateScalar(start, data.AsSpan(off, len)), Adler32.Update(start, data.AsSpan(off, len)));
        }

        // Worst case for overflow: all 0xFF.
        byte[] ff = new byte[200_000];
        Array.Fill(ff, (byte)0xFF);
        Assert.Equal(Adler32.UpdateScalar(1, ff), Adler32.Update(1, ff));
        byte[] big = CompressionTestUtil.Random(3_000_007, 6);
        Assert.Equal(Adler32.UpdateScalar(1, big), Adler32.Update(1, big));
    }

    [Fact]
    public void Adler32IsIncremental()
    {
        byte[] data = CompressionTestUtil.Random(30_000, 8);
        uint whole = Adler32.Update(Adler32.Initial, data);
        uint a = Adler32.Initial;
        for (int i = 0; i < data.Length; i += 777)
        {
            a = Adler32.Update(a, data.AsSpan(i, Math.Min(777, data.Length - i)));
        }

        Assert.Equal(whole, a);
    }
}
