using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OneImage.Tests.Jpx;

/// <summary>Helpers shared by the JPEG 2000 tests: the committed corpus, a JP2 box writer and a tiny codestream builder.</summary>
internal static class JpxTestStreams
{
    public static string GenDir { get; } = Path.Combine(AppContext.BaseDirectory, "testdata", "jpx", "gen");

    public static byte[] Gen(string name) => File.ReadAllBytes(Path.Combine(GenDir, name));

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ---- JP2 boxes ----

    public static byte[] Box(string type, params byte[][] payload)
    {
        int len = 8 + payload.Sum(p => p.Length);
        var b = new List<byte>();
        b.AddRange(U32((uint)len));
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        foreach (byte[] p in payload)
        {
            b.AddRange(p);
        }

        return b.ToArray();
    }

    public static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    public static byte[] U16(int v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        return b;
    }

    /// <summary>Wraps a raw codestream in a JP2 file with the given colour specification and optional extra header boxes.</summary>
    public static byte[] Jp2(byte[] codestream, int width, int height, int components, int bits, uint enumCs, params byte[][] extraHeaderBoxes)
    {
        byte[] ihdr = Box("ihdr", U32((uint)height), U32((uint)width), U16(components), [(byte)(bits - 1), 7, 0, 0]);
        byte[] colr = Box("colr", [1, 0, 0], U32(enumCs));
        var header = new List<byte[]> { ihdr, colr };
        header.AddRange(extraHeaderBoxes);
        return Concat(
            Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
            Box("ftyp", System.Text.Encoding.ASCII.GetBytes("jp2 "), U32(0), System.Text.Encoding.ASCII.GetBytes("jp2 ")),
            Box("jp2h", header.ToArray()),
            Box("jp2c", codestream));
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // ---- codestreams ----

    /// <summary>Builder for a one-tile-part codestream with whatever header values a test needs; the packet data is supplied by the caller.</summary>
    public sealed class Stream
    {
        public int Width = 64;
        public int Height = 64;
        public int TileWidth;
        public int TileHeight;
        public int Components = 1;
        public int Precision = 8;
        public int CbWidthExp = 6;
        public int CbHeightExp = 6;
        public int Layers = 1;
        public bool Reversible = true;
        public int Guard = 2;
        public int Exponent = 10;
        public byte[]? PrecinctSizes;
        public byte[] Extra = [];
        public byte[] Packets = [];
        public bool WithTile = true;

        public byte[] Build()
        {
            int tw = TileWidth == 0 ? Width : TileWidth;
            int th = TileHeight == 0 ? Height : TileHeight;
            var s = new List<byte> { 0xFF, 0x4F };
            // SIZ
            s.AddRange([0xFF, 0x51]);
            s.AddRange(U16(38 + (3 * Components)));
            s.AddRange(U16(0));
            s.AddRange(U32((uint)Width));
            s.AddRange(U32((uint)Height));
            s.AddRange(U32(0));
            s.AddRange(U32(0));
            s.AddRange(U32((uint)tw));
            s.AddRange(U32((uint)th));
            s.AddRange(U32(0));
            s.AddRange(U32(0));
            s.AddRange(U16(Components));
            for (int i = 0; i < Components; i++)
            {
                s.AddRange([(byte)(Precision - 1), 1, 1]);
            }

            // COD
            int prec = PrecinctSizes?.Length ?? 0;
            s.AddRange([0xFF, 0x52]);
            s.AddRange(U16(12 + prec));
            s.Add((byte)(prec > 0 ? 1 : 0));
            s.Add(0); // progression order LRCP
            s.AddRange(U16(Layers));
            s.Add(0); // no component transform
            s.Add(0); // no decomposition levels
            s.Add((byte)(CbWidthExp - 2));
            s.Add((byte)(CbHeightExp - 2));
            s.Add(0); // plain code-block style
            s.Add((byte)(Reversible ? 1 : 0));
            if (PrecinctSizes is not null)
            {
                s.AddRange(PrecinctSizes);
            }

            // QCD: no quantization (reversible) with one exponent per sub-band.
            const int bands = 1;
            s.AddRange([0xFF, 0x5C]);
            s.AddRange(U16(3 + bands));
            s.Add((byte)(Guard << 5));
            for (int i = 0; i < bands; i++)
            {
                s.Add((byte)(Exponent << 3));
            }

            s.AddRange(Extra);
            if (WithTile)
            {
                s.AddRange([0xFF, 0x90]);
                s.AddRange(U16(10));
                s.AddRange(U16(0));
                s.AddRange(U32((uint)(14 + Packets.Length)));
                s.AddRange([0, 1]);
                s.AddRange([0xFF, 0x93]);
                s.AddRange(Packets);
            }

            s.AddRange([0xFF, 0xD9]);
            return s.ToArray();
        }
    }

    /// <summary>Writes bits MSB first with the JPEG 2000 packet header rule: after an 0xFF byte the next byte carries 7 bits.</summary>
    public sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _cur;
        private int _n;
        private int _cap = 8;

        public void Bit(int b)
        {
            _cur = (_cur << 1) | (b & 1);
            if (++_n == _cap)
            {
                _bytes.Add((byte)_cur);
                _cap = _cur == 0xFF ? 7 : 8;
                _cur = 0;
                _n = 0;
            }
        }

        public void Bits(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                Bit((value >> i) & 1);
            }
        }

        public byte[] Finish()
        {
            while (_n != 0)
            {
                Bit(0);
            }

            if (_bytes.Count > 0 && _bytes[^1] == 0xFF)
            {
                _bytes.Add(0);
            }

            return _bytes.ToArray();
        }
    }

    /// <summary>
    /// One packet for a single-resolution, single-component tile that includes every code-block of a <paramref name="gw"/> x
    /// <paramref name="gh"/> grid in its first layer with <paramref name="passes"/> coding passes but no data bytes, and
    /// <paramref name="zeroBitPlanes"/> missing bit-planes. Real encoders never write this; it is how a few bytes ask for a lot of work.
    /// </summary>
    public static byte[] EmptyBlocksPacket(int gw, int gh, int passes, int zeroBitPlanes)
    {
        var w = new BitWriter();
        w.Bit(1);
        var dims = new List<(int W, int H)>();
        for (int cw = gw, ch = gh; ; cw = (cw + 1) / 2, ch = (ch + 1) / 2)
        {
            dims.Add((cw, ch));
            if (cw == 1 && ch == 1)
            {
                break;
            }
        }

        var seenIncl = dims.Select(d => new bool[d.W * d.H]).ToArray();
        var seenZbp = dims.Select(d => new bool[d.W * d.H]).ToArray();
        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++)
            {
                // Inclusion tree: every node has value 0, so its first visit writes a single 1.
                for (int l = dims.Count - 1; l >= 0; l--)
                {
                    int idx = ((y >> l) * dims[l].W) + (x >> l);
                    if (!seenIncl[l][idx])
                    {
                        seenIncl[l][idx] = true;
                        w.Bit(1);
                    }
                }

                // Zero-bit-plane tree: all values equal, so only the root writes the zeros.
                for (int l = dims.Count - 1; l >= 0; l--)
                {
                    int idx = ((y >> l) * dims[l].W) + (x >> l);
                    if (!seenZbp[l][idx])
                    {
                        seenZbp[l][idx] = true;
                        if (l == dims.Count - 1)
                        {
                            for (int z = 0; z < zeroBitPlanes; z++)
                            {
                                w.Bit(0);
                            }
                        }

                        w.Bit(1);
                    }
                }

                // Pass count (B.10.6), then Lblock stays 3 and the length is zero.
                if (passes == 1)
                {
                    w.Bit(0);
                }
                else if (passes == 2)
                {
                    w.Bits(0b10, 2);
                }
                else if (passes <= 5)
                {
                    w.Bits(0b11, 2);
                    w.Bits(passes - 3, 2);
                }
                else if (passes <= 36)
                {
                    w.Bits(0b1111, 4);
                    w.Bits(passes - 6, 5);
                }
                else
                {
                    w.Bits(0x1FF, 9);
                    w.Bits(passes - 37, 7);
                }

                w.Bit(0);
                int lengthBits = 3 + (int)Math.Floor(Math.Log2(passes));
                for (int i = 0; i < lengthBits; i++)
                {
                    w.Bit(0);
                }
            }
        }

        return w.Finish();
    }
}
