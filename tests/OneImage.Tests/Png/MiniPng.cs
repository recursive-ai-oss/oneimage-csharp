using System.Buffers.Binary;
using System.IO.Compression;

namespace OneImage.Tests.Png;

/// <summary>Builds PNG files byte by byte (using the platform zlib, independent of the library) for crafted and malformed cases.</summary>
internal static class MiniPng
{
    public static byte[] Chunk(string type, ReadOnlySpan<byte> data, bool badCrc = false)
    {
        byte[] o = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(o, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, o.AsSpan(4));
        data.CopyTo(o.AsSpan(8));
        uint crc = System.IO.Hashing.Crc32.HashToUInt32(o.AsSpan(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(8 + data.Length), badCrc ? ~crc : crc);
        return o;
    }

    public static byte[] Zlib(ReadOnlySpan<byte> raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
        {
            z.Write(raw);
        }

        return ms.ToArray();
    }

    public static byte[] Ihdr(int w, int h, int depth = 8, int colorType = 6, int interlace = 0)
    {
        byte[] d = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), (uint)h);
        d[8] = (byte)depth;
        d[9] = (byte)colorType;
        d[12] = (byte)interlace;
        return d;
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Unfiltered RGBA8 scanlines (filter byte 0 per row) for a w x h region.</summary>
    public static byte[] RawRgba(byte[] rgba, int w, int h)
    {
        byte[] raw = new byte[h * ((w * 4) + 1)];
        for (int y = 0; y < h; y++)
        {
            rgba.AsSpan(y * w * 4, w * 4).CopyTo(raw.AsSpan((y * ((w * 4) + 1)) + 1));
        }

        return raw;
    }

    public static byte[] Rgba(int w, int h, params byte[] pixel)
    {
        byte[] b = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixel.CopyTo(b, i * 4);
        }

        return b;
    }

    public sealed record Frame(int X, int Y, int W, int H, byte[] Rgba, int DelayNum = 1, int DelayDen = 10, int Dispose = 0, int Blend = 0);

    /// <summary>An APNG. With <paramref name="hiddenDefault"/> the IDAT image is not part of the animation (no fcTL before it).</summary>
    public static byte[] Apng(int w, int h, Frame[] frames, int plays = 0, byte[]? hiddenDefault = null)
    {
        var parts = new List<byte[]> { Signature, Chunk("IHDR", Ihdr(w, h)) };
        byte[] actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames.Length);
        BinaryPrimitives.WriteUInt32BigEndian(actl.AsSpan(4), (uint)plays);
        parts.Add(Chunk("acTL", actl));
        int seq = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            Frame f = frames[i];
            byte[] fctl = new byte[26];
            BinaryPrimitives.WriteInt32BigEndian(fctl, seq++);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(4), f.W);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(8), f.H);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(12), f.X);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(16), f.Y);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), (ushort)f.DelayNum);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), (ushort)f.DelayDen);
            fctl[24] = (byte)f.Dispose;
            fctl[25] = (byte)f.Blend;
            byte[] z = Zlib(RawRgba(f.Rgba, f.W, f.H));
            if (i == 0 && hiddenDefault != null)
            {
                parts.Add(Chunk("IDAT", Zlib(RawRgba(hiddenDefault, w, h))));
                parts.Add(Chunk("fcTL", fctl));
                parts.Add(Chunk("fdAT", Concat(SeqBytes(seq++), z)));
            }
            else if (i == 0)
            {
                parts.Add(Chunk("fcTL", fctl));
                parts.Add(Chunk("IDAT", z));
            }
            else
            {
                parts.Add(Chunk("fcTL", fctl));
                // Split the data over two fdAT chunks to cover continuation.
                int half = z.Length / 2;
                parts.Add(Chunk("fdAT", Concat(SeqBytes(seq++), z[..half])));
                parts.Add(Chunk("fdAT", Concat(SeqBytes(seq++), z[half..])));
            }
        }

        parts.Add(Chunk("IEND", []));
        return Concat([.. parts]);
    }

    private static byte[] SeqBytes(int s)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, s);
        return b;
    }

    /// <summary>A plain non-interlaced PNG from raw (already filtered) scanlines.</summary>
    public static byte[] Plain(int w, int h, int depth, int colorType, byte[] scanlines, params byte[][] extraBeforeIdat) =>
        Concat([Signature, Chunk("IHDR", Ihdr(w, h, depth, colorType)), .. extraBeforeIdat, Chunk("IDAT", Zlib(scanlines)), Chunk("IEND", [])]);
}
