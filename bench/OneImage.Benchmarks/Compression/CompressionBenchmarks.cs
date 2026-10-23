using System.IO;
using System.Buffers;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using OneImage.Compression;

namespace OneImage.Benchmarks.Compression;

/// <summary>Shared ~10 MB of image-like scanlines (PNG-filtered style rows with some noise).</summary>
internal static class CompressionData
{
    public static byte[] ImageLike(int width, int height)
    {
        var rnd = new Random(2);
        int stride = (width * 3) + 1;
        var b = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            b[y * stride] = (byte)(y % 5);
            for (int x = 0; x < width * 3; x++)
            {
                int v = ((x / 3) + y + ((x % 3) * 40)) & 0xFF;
                if (rnd.Next(8) == 0)
                {
                    v += rnd.Next(-3, 4);
                }

                b[(y * stride) + 1 + x] = (byte)v;
            }
        }

        return b;
    }

    public static byte[] Compress(byte[] data, CompressionLevel level)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }
}

[MemoryDiagnoser]
public class InflateBenchmarks : IDisposable
{
    private byte[] _data = null!;
    private byte[] _compressed = null!;
    private byte[] _buffer = null!;
    private Inflater _inflater = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = CompressionData.ImageLike(1800, 1850);
        _compressed = CompressionData.Compress(_data, CompressionLevel.Optimal);
        _buffer = new byte[64 * 1024];
        _inflater = new Inflater(true);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _inflater.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true, Description = "System.IO.Compression ZLibStream inflate, 10 MB")]
    public long SystemInflate()
    {
        long total = 0;
        using var z = new ZLibStream(new MemoryStream(_compressed), CompressionMode.Decompress);
        int n;
        while ((n = z.Read(_buffer)) > 0)
        {
            total += n;
        }

        return total;
    }

    [Benchmark(Description = "Inflater (64 KiB output pulls), 10 MB")]
    public long Ours()
    {
        _inflater.Reset(true);
        long total = 0;
        int pos = 0;
        while (true)
        {
            InflateStatus st = _inflater.Inflate(_compressed.AsSpan(pos), _buffer, out int c, out int w);
            pos += c;
            total += w;
            if (st == InflateStatus.Done)
            {
                return total;
            }
        }
    }
}

[MemoryDiagnoser]
public class DeflateBenchmarks : IDisposable
{
    private byte[] _data = null!;
    private ArrayBufferWriter<byte> _sink = null!;
    private MemoryStream _ms = null!;
    private Deflater _deflater = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = CompressionData.ImageLike(1800, 1850);
        _sink = new ArrayBufferWriter<byte>(8 << 20);
        _ms = new MemoryStream(8 << 20);
        _deflater = new Deflater(_sink, 6);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _deflater.Dispose();
        _ms.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true, Description = "ZLibStream Optimal (level 6), 10 MB")]
    public long System6()
    {
        _ms.SetLength(0);
        using (var z = new ZLibStream(_ms, CompressionLevel.Optimal, true))
        {
            z.Write(_data);
        }

        return _ms.Length;
    }

    [Benchmark(Description = "Deflater level 6, 10 MB")]
    public long Ours6() => Ours(6);

    [Benchmark(Description = "ZLibStream Fastest, 10 MB")]
    public long SystemFast()
    {
        _ms.SetLength(0);
        using (var z = new ZLibStream(_ms, CompressionLevel.Fastest, true))
        {
            z.Write(_data);
        }

        return _ms.Length;
    }

    [Benchmark(Description = "Deflater level 1, 10 MB")]
    public long Ours1() => Ours(1);

    [Benchmark(Description = "Deflater level 9, 10 MB")]
    public long Ours9() => Ours(9);

    private long Ours(int level)
    {
        _sink.ResetWrittenCount();
        _deflater.Reset(level, true);
        _deflater.Write(_data);
        _deflater.Finish();
        return _sink.WrittenCount;
    }
}

[MemoryDiagnoser]
public class LzwBenchmarks
{
    private byte[] _data = null!;
    private byte[] _gif = null!;
    private byte[] _tiff = null!;
    private byte[] _buffer = null!;
    private byte[] _whole = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = CompressionData.ImageLike(1800, 1850);
        _gif = LzwEncoder.Compress(_data, LzwVariant.Gif, 8);
        _tiff = LzwEncoder.Compress(_data, LzwVariant.Tiff);
        _buffer = new byte[64 * 1024];
        _whole = new byte[_data.Length];
    }

    [Benchmark(Description = "LZW decode TIFF, 10 MB")]
    public long DecodeTiff() => Decode(_tiff, LzwVariant.Tiff);

    [Benchmark(Description = "LZW decode TIFF whole block, 10 MB")]
    public long DecodeTiffBlock()
    {
        var dec = new LzwDecoder(LzwVariant.Tiff, 8);
        return dec.DecodeBlock(_tiff, _whole);
    }

    [Benchmark(Description = "LZW decode GIF, 10 MB")]
    public long DecodeGif() => Decode(_gif, LzwVariant.Gif);

    [Benchmark(Description = "LZW encode TIFF, 10 MB")]
    public long EncodeTiff() => LzwEncoder.Compress(_data, LzwVariant.Tiff).Length;

    private long Decode(byte[] comp, LzwVariant v)
    {
        var dec = new LzwDecoder(v, 8);
        long total = 0;
        int pos = 0;
        while (true)
        {
            InflateStatus st = dec.Decode(comp.AsSpan(pos), _buffer, out int c, out int w);
            pos += c;
            total += w;
            if (st == InflateStatus.Done)
            {
                return total;
            }
        }
    }
}

[MemoryDiagnoser]
public class ChecksumBenchmarks
{
    private byte[] _data = null!;

    [GlobalSetup]
    public void Setup() => _data = CompressionData.ImageLike(1800, 1850);

    [Benchmark(Baseline = true, Description = "System.IO.Hashing Crc32, 10 MB")]
    public uint SystemCrc() => System.IO.Hashing.Crc32.HashToUInt32(_data);

    [Benchmark(Description = "Crc32 (ours), 10 MB")]
    public uint OursCrc() => Crc32.Compute(_data);

    [Benchmark(Description = "Crc32 slice-by-8 table only, 10 MB")]
    public uint TableCrc() => ~Crc32.UpdateTable(0xFFFFFFFF, _data);

    [Benchmark(Description = "Adler32 scalar, 10 MB")]
    public uint AdlerScalar() => Adler32.UpdateScalar(1, _data);

    [Benchmark(Description = "Adler32 vector, 10 MB")]
    public uint AdlerVector() => Adler32.Update(1, _data);
}
