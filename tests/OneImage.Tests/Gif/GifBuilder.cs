using System.Buffers.Binary;
using OneImage.Compression;

namespace OneImage.Tests.Gif;

/// <summary>Hand-assembles GIF files, including broken ones, so decoder policies can be tested without another library.</summary>
internal static class GifBuilder
{
    internal sealed record Frame(int X, int Y, int W, int H, byte[] Indices, byte[]? LocalRgb = null, int Disposal = 0, int Transparent = -1, int Delay = 0, bool Interlaced = false, bool EndCode = true, int MinCode = 0);

    public static byte[] Build(int sw, int sh, byte[]? globalRgb, IEnumerable<Frame> frames, int? loop = null, bool trailer = true, byte[]? xmp = null)
    {
        var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        Span<byte> w = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)sw);
        BinaryPrimitives.WriteUInt16LittleEndian(w[2..], (ushort)sh);
        ms.Write(w);
        int gBits = globalRgb == null ? 0 : Bits(globalRgb.Length / 3);
        ms.WriteByte((byte)(globalRgb == null ? 0x70 : 0xF0 | (gBits - 1)));
        ms.WriteByte(0);
        ms.WriteByte(0);
        if (globalRgb != null)
        {
            Table(ms, globalRgb, gBits);
        }

        if (loop is { } l)
        {
            ms.Write(new byte[] { 0x21, 0xFF, 11 });
            ms.Write("NETSCAPE2.0"u8);
            ms.Write(new byte[] { 3, 1, (byte)l, (byte)(l >> 8), 0 });
        }

        if (xmp != null)
        {
            ms.Write(new byte[] { 0x21, 0xFF, 11 });
            ms.Write("XMP DataXMP"u8);
            ms.Write(xmp);
            ms.WriteByte(1);
            for (int i = 255; i >= 1; i--)
            {
                ms.WriteByte((byte)i);
            }

            ms.WriteByte(0);
            ms.WriteByte(0);
        }

        foreach (Frame f in frames)
        {
            ms.Write(new byte[] { 0x21, 0xF9, 4, (byte)((f.Disposal << 2) | (f.Transparent >= 0 ? 1 : 0)), (byte)f.Delay, (byte)(f.Delay >> 8), (byte)Math.Max(f.Transparent, 0), 0 });
            ms.WriteByte(0x2C);
            Span<byte> d = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)f.X);
            BinaryPrimitives.WriteUInt16LittleEndian(d[2..], (ushort)f.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(d[4..], (ushort)f.W);
            BinaryPrimitives.WriteUInt16LittleEndian(d[6..], (ushort)f.H);
            ms.Write(d);
            int lBits = f.LocalRgb == null ? 0 : Bits(f.LocalRgb.Length / 3);
            ms.WriteByte((byte)((f.LocalRgb != null ? 0x80 | (lBits - 1) : 0) | (f.Interlaced ? 0x40 : 0)));
            if (f.LocalRgb != null)
            {
                Table(ms, f.LocalRgb, lBits);
            }

            int bits = f.LocalRgb != null ? lBits : gBits;
            int min = f.MinCode != 0 ? f.MinCode : Math.Max(2, bits);
            byte[] idx = f.Interlaced ? Interlace(f.Indices, f.W, f.H) : f.Indices;
            byte[] data = f.EndCode ? LzwEncoder.Compress(idx, LzwVariant.Gif, min) : PackLiteral(idx, min);
            ms.WriteByte((byte)min);
            for (int off = 0; off < data.Length; off += 255)
            {
                int n = Math.Min(255, data.Length - off);
                ms.WriteByte((byte)n);
                ms.Write(data, off, n);
            }

            ms.WriteByte(0);
        }

        if (trailer)
        {
            ms.WriteByte(0x3B);
        }

        return ms.ToArray();
    }

    private static int Bits(int count)
    {
        int b = 1;
        while ((1 << b) < count)
        {
            b++;
        }

        return b;
    }

    private static void Table(Stream s, byte[] rgb, int bits)
    {
        var t = new byte[(1 << bits) * 3];
        rgb.CopyTo(t, 0);
        s.Write(t);
    }

    /// <summary>Stores rows in GIF's four-pass order.</summary>
    public static byte[] Interlace(byte[] indices, int w, int h)
    {
        var o = new byte[indices.Length];
        int n = 0;
        foreach ((int start, int step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
        {
            for (int y = start; y < h; y += step)
            {
                Array.Copy(indices, y * w, o, n++ * w, w);
            }
        }

        return o;
    }

    /// <summary>LZW stream of literal codes only, with periodic clear codes, and no end code.</summary>
    private static byte[] PackLiteral(byte[] idx, int min)
    {
        var bytes = new List<byte>();
        ulong acc = 0;
        int nbits = 0;
        int width = min + 1;
        void Put(int code)
        {
            acc |= (ulong)code << nbits;
            nbits += width;
            while (nbits >= 8)
            {
                bytes.Add((byte)acc);
                acc >>= 8;
                nbits -= 8;
            }
        }

        int clear = 1 << min;
        int run = 0;
        Put(clear);
        foreach (byte b in idx)
        {
            if (run == clear - 3)
            {
                Put(clear);
                run = 0;
            }

            Put(b);
            run++;
        }

        if (nbits > 0)
        {
            bytes.Add((byte)acc);
        }

        return bytes.ToArray();
    }
}
