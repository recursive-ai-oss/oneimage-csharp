using System.Buffers;
using System.IO.Compression;
using OneImage.Compression;

namespace OneImage.Tests.Compression;

/// <summary>Helpers shared by the compression tests: corpus generators, drivers and a stream builder.</summary>
internal static class CompressionTestUtil
{
    public static byte[] Random(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Scanlines that look like filtered image rows: smooth gradients plus a little noise.</summary>
    public static byte[] ImageLike(int width, int height, int seed)
    {
        var rnd = new Random(seed);
        int stride = width * 3 + 1;
        var b = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            b[y * stride] = (byte)(y % 5);
            for (int x = 0; x < width * 3; x++)
            {
                int v = ((x / 3) + y + (x % 3 * 40)) & 0xFF;
                if (rnd.Next(8) == 0)
                {
                    v += rnd.Next(-3, 4);
                }

                b[(y * stride) + 1 + x] = (byte)v;
            }
        }

        return b;
    }

    public static byte[] Text(int n, int seed)
    {
        string[] words = { "image", "tile", "the", "of", "pixel", "stream", "decode", "and", "buffer", "kernel", "a", "color", "profile", "resize" };
        var rnd = new Random(seed);
        var sb = new System.Text.StringBuilder();
        while (sb.Length < n)
        {
            sb.Append(words[rnd.Next(words.Length)]).Append(rnd.Next(10) == 0 ? ".\n" : " ");
        }

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, n));
    }

    public static IEnumerable<(string Name, byte[] Data)> Corpus()
    {
        yield return ("empty", Array.Empty<byte>());
        yield return ("one", new byte[] { 42 });
        yield return ("zeros64k", new byte[65536]);
        yield return ("repeat", Enumerable.Range(0, 100000).Select(i => (byte)("abcabcabd"[i % 9])).ToArray());
        yield return ("random100k", Random(100_000, 1));
        yield return ("image640x200", ImageLike(640, 200, 2));
        yield return ("text300k", Text(300_000, 3));
        yield return ("image1500x700", ImageLike(1500, 700, 4));
        yield return ("mixed", Random(70000, 5).Concat(new byte[50000]).Concat(Text(60000, 6)).Concat(Random(5000, 7)).ToArray());
    }

    /// <summary>Runs the inflater with the given chunk sizes and returns all output.</summary>
    public static byte[] Inflate(byte[] comp, bool zlib, int inChunk = int.MaxValue, int outChunk = 1 << 16, long maxOut = long.MaxValue, double ratio = 0, bool requireAllConsumed = true)
    {
        using var inf = new Inflater(zlib, maxOut, ratio);
        var result = new MemoryStream();
        byte[] buf = new byte[outChunk];
        int pos = 0;
        long guard = 0;
        while (true)
        {
            int n = Math.Min(inChunk, comp.Length - pos);
            InflateStatus st = inf.Inflate(comp.AsSpan(pos, n), buf, out int c, out int w);
            result.Write(buf, 0, w);
            pos += c;
            if (st == InflateStatus.Done)
            {
                if (requireAllConsumed)
                {
                    Assert.Equal(comp.Length, pos);
                }

                return result.ToArray();
            }

            if (st == InflateStatus.NeedInput && pos >= comp.Length)
            {
                throw new ImageFormatException("truncated");
            }

            if (++guard > 50_000_000)
            {
                throw new InvalidOperationException("no progress");
            }
        }
    }

    public static byte[] SystemCompress(byte[] data, CompressionLevel level, bool zlib)
    {
        if (data.Length == 0)
        {
            // The BCL writes nothing for an empty stream; use the canonical empty encoding.
            return zlib ? new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 } : new byte[] { 0x03, 0x00 };
        }

        var ms = new MemoryStream();
        using (Stream z = zlib ? new ZLibStream(ms, level, true) : new DeflateStream(ms, level, true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    public static byte[] SystemDecompress(byte[] comp, bool zlib)
    {
        using Stream z = zlib ? new ZLibStream(new MemoryStream(comp), CompressionMode.Decompress) : new DeflateStream(new MemoryStream(comp), CompressionMode.Decompress);
        var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }
}

/// <summary>
/// Independent, deliberately simple DEFLATE stream writer used to build edge-case streams by hand.
/// It writes whatever code it is told to, so tests can create valid and invalid streams.
/// </summary>
internal sealed class DeflateBuilder
{
    private readonly List<byte> _bytes = new();
    private ulong _acc;
    private int _n;

    public void Bits(uint value, int count)
    {
        _acc |= (ulong)value << _n;
        _n += count;
        while (_n >= 8)
        {
            _bytes.Add((byte)_acc);
            _acc >>= 8;
            _n -= 8;
        }
    }

    /// <summary>Writes a Huffman code, most significant bit first.</summary>
    public void Code(uint code, int len)
    {
        for (int i = len - 1; i >= 0; i--)
        {
            Bits((code >> i) & 1, 1);
        }
    }

    public void Align()
    {
        if (_n > 0)
        {
            Bits(0, 8 - _n);
        }
    }

    public DeflateBuilder Stored(ReadOnlySpan<byte> data, bool last)
    {
        Bits(last ? 1u : 0u, 1);
        Bits(0, 2);
        Align();
        Bits((uint)data.Length, 16);
        Bits((uint)(~data.Length & 0xFFFF), 16);
        foreach (byte b in data)
        {
            Bits(b, 8);
        }

        return this;
    }

    public void FixedLit(int sym)
    {
        if (sym < 144)
        {
            Code((uint)(0x30 + sym), 8);
        }
        else if (sym < 256)
        {
            Code((uint)(0x190 + sym - 144), 9);
        }
        else if (sym < 280)
        {
            Code((uint)(sym - 256), 7);
        }
        else
        {
            Code((uint)(0xC0 + sym - 280), 8);
        }
    }

    public DeflateBuilder FixedHeader(bool last)
    {
        Bits(last ? 1u : 0u, 1);
        Bits(1, 2);
        return this;
    }

    /// <summary>Writes a length/distance pair with fixed codes.</summary>
    public void FixedMatch(int length, int dist)
    {
        int ls = DeflateTables.LengthSymbol[length - 3];
        FixedLit(257 + ls);
        Bits((uint)(length - DeflateTables.LengthBase[ls]), DeflateTables.LengthExtra[ls]);
        int ds = DeflateTables.DistanceSymbol(dist);
        Code((uint)ds, 5);
        Bits((uint)(dist - DeflateTables.DistBase[ds]), DeflateTables.DistExtra[ds]);
    }

    /// <summary>MSB-first canonical codes for a length table.</summary>
    public static uint[] Canonical(int[] lens)
    {
        var codes = new uint[lens.Length];
        uint code = 0;
        for (int l = 1; l <= 15; l++)
        {
            for (int s = 0; s < lens.Length; s++)
            {
                if (lens[s] == l)
                {
                    codes[s] = code++;
                }
            }

            code <<= 1;
        }

        return codes;
    }

    /// <summary>
    /// Starts a dynamic block with the given literal/length and distance code lengths. Code lengths are
    /// sent one by one (no run-length codes), using a fixed 4/5-bit code-length alphabet.
    /// </summary>
    public (uint[] Lit, uint[] Dist) DynamicHeader(bool last, int[] litLens, int[] distLens)
    {
        int hlit = litLens.Length;
        int hdist = distLens.Length;
        Bits(last ? 1u : 0u, 1);
        Bits(2, 2);
        Bits((uint)(hlit - 257), 5);
        Bits((uint)(hdist - 1), 5);
        Bits(15, 4); // all 19 code length code lengths
        int[] clens = new int[19];
        for (int i = 0; i < 19; i++)
        {
            clens[i] = i < 13 ? 4 : 5;
        }

        foreach (byte o in DeflateTables.ClenOrder)
        {
            Bits((uint)clens[o], 3);
        }

        uint[] cc = Canonical(clens);
        foreach (int l in litLens.Concat(distLens))
        {
            Code(cc[l], clens[l]);
        }

        return (Canonical(litLens), Canonical(distLens));
    }

    public byte[] ToArray(bool zlibWrap = false, byte[]? uncompressedForAdler = null)
    {
        Align();
        byte[] body = _bytes.ToArray();
        if (!zlibWrap)
        {
            return body;
        }

        uint adler = Adler32.Update(Adler32.Initial, uncompressedForAdler ?? Array.Empty<byte>());
        return new byte[] { 0x78, 0x9C }.Concat(body).Concat(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler }).ToArray();
    }
}
