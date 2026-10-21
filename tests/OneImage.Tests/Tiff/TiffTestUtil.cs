using System.Buffers.Binary;
using OneImage.Codecs.Tiff;
using OneImage.Compression;
using OneImage.Kernels;

namespace OneImage.Tests.Tiff;

/// <summary>Collects the rows a decoder delivers.</summary>
internal sealed class CollectSink : IRowSink
{
    private readonly byte[] _data;
    private readonly int _rowBytes;

    public CollectSink(int width, int height, PixelFormat format)
    {
        Width = width;
        Height = height;
        Format = format;
        _rowBytes = width * format.BytesPerPixel;
        _data = new byte[_rowBytes * height];
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public int RowsWritten { get; private set; }

    public byte[] Data => _data;

    public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
    {
        if (y != RowsWritten)
        {
            throw new InvalidOperationException("rows out of order");
        }

        rows[..(rowCount * _rowBytes)].CopyTo(_data.AsSpan(y * _rowBytes));
        RowsWritten += rowCount;
    }
}

internal static class TiffTestUtil
{
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "testdata", "tiff");

    public static ImageInfo Info(byte[] file, ImageLimits? limits = null)
    {
        using var ms = new MemoryStream(file);
        return TiffFormat.Instance.Decoder!.ReadInfo(ms, limits ?? ImageLimits.Default);
    }

    public static byte[] Decode(byte[] file, int frame = 0, ImageRect? region = null, ImageInfo? info = null, ImageLimits? limits = null)
    {
        using var ms = new MemoryStream(file);
        limits ??= ImageLimits.Default;
        info ??= TiffFormat.Instance.Decoder!.ReadInfo(ms, limits);
        FrameInfo f = info.Frames[frame];
        ImageRect r = region ?? new ImageRect(0, 0, f.Width, f.Height);
        var sink = new CollectSink(r.Width, r.Height, f.Format);
        TiffFormat.Instance.Decoder!.DecodeFrame(ms, info, frame, new DecodeRequest(r), sink, limits, CancellationToken.None);
        if (sink.RowsWritten != r.Height)
        {
            throw new InvalidOperationException("short decode");
        }

        return sink.Data;
    }

    public static PixelFormat FormatByName(string name) => name switch
    {
        "Gray8" => PixelFormat.Gray8,
        "GrayAlpha8" => PixelFormat.GrayAlpha8,
        "Gray16" => PixelFormat.Gray16,
        "GrayF32" => PixelFormat.GrayF32,
        "Rgb8" => PixelFormat.Rgb8,
        "Rgba8" => PixelFormat.Rgba8,
        "Rgba8Premultiplied" => PixelFormat.Rgba8Premultiplied,
        "Rgb16" => PixelFormat.Rgb16,
        "Rgba16" => PixelFormat.Rgba16,
        "RgbF32" => PixelFormat.RgbF32,
        "RgbaF32" => PixelFormat.RgbaF32,
        "Cmyk8" => PixelFormat.Cmyk8,
        "Cmyk16" => PixelFormat.Cmyk16,
        _ => throw new ArgumentException(name),
    };
}

/// <summary>
/// Builds TIFF files with any layout the decoder accepts, from explicit sample values, so tests can reach layouts no common
/// writer produces (12-bit, 4-bit RGB, planar 16-bit, big-endian tiles, fill order 2, ...). Compression uses the library's own coders.
/// </summary>
internal sealed class TiffBuilder
{
    public int Width { get; set; } = 21;

    public int Height { get; set; } = 17;

    public int Bits { get; set; } = 8;

    public int Spp { get; set; } = 1;

    public int Photometric { get; set; } = 1;

    public bool Planar { get; set; }

    public int TileW { get; set; }

    public int TileH { get; set; }

    public int RowsPerStrip { get; set; } = 5;

    public int Compression { get; set; } = 1;

    public int Predictor { get; set; } = 1;

    public bool BigEndian { get; set; }

    public bool BigTiff { get; set; }

    public int FillOrder { get; set; } = 1;

    public ushort[]? ColorMap { get; set; }

    public int[] ExtraSamples { get; set; } = [];

    public bool FloatSamples { get; set; }

    /// <summary>Optional extra entries (tag, type, values) appended to the directory.</summary>
    public List<(int Tag, int Type, byte[] Value, long Count)> Extra { get; } = [];

    /// <summary>Sample values [y, x, s]; for float samples the bit pattern of the float.</summary>
    public uint[,,] Samples { get; set; } = new uint[0, 0, 0];

    public bool Tiled => TileW > 0;

    public uint[,,] MakeSamples(int seed, bool smooth = true)
    {
        var rnd = new Random(seed);
        uint max = Bits >= 32 ? uint.MaxValue : (1u << Bits) - 1;
        var s = new uint[Height, Width, Spp];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                for (int c = 0; c < Spp; c++)
                {
                    s[y, x, c] = smooth ? (uint)(((y * 7) + (x * 3) + (c * 11) + rnd.Next(3)) % (max + 1)) : (uint)(rnd.NextInt64(max + 1L));
                }
            }
        }

        Samples = s;
        return s;
    }

    // Packs one row (or tile row) of samples for the given planes into file bytes, before prediction.
    private byte[] PackRow(int y, int x0, int w, int plane)
    {
        int spp = Planar ? 1 : Spp;
        int bits = Bits;
        int rowBytes = ((w * spp * bits) + 7) / 8;
        byte[] row = new byte[rowBytes];
        long bitPos = 0;
        for (int x = 0; x < w; x++)
        {
            for (int c = 0; c < spp; c++)
            {
                uint v = 0;
                int sx = x0 + x;
                if (y < Height && sx < Width)
                {
                    v = Samples[y, sx, Planar ? plane : c];
                }

                if (bits == 8)
                {
                    row[(int)(bitPos >> 3)] = (byte)v;
                }
                else if (bits == 16)
                {
                    int i = (int)(bitPos >> 3);
                    if (BigEndian)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(i), (ushort)v);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(i), (ushort)v);
                    }
                }
                else if (bits == 32)
                {
                    int i = (int)(bitPos >> 3);
                    if (BigEndian)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(i), v);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(i), v);
                    }
                }
                else
                {
                    for (int b = bits - 1; b >= 0; b--)
                    {
                        if (((v >> b) & 1) != 0)
                        {
                            row[(int)(bitPos >> 3)] |= (byte)(0x80 >> (int)(bitPos & 7));
                        }

                        bitPos++;
                    }

                    continue;
                }

                bitPos += bits;
            }
        }

        return row;
    }

    private byte[] Compress(byte[] raw, int rowBytes, int channels)
    {
        byte[] work = (byte[])raw.Clone();
        if (Predictor == 2)
        {
            TiffKernels.ApplyHorizontal(BigEndian && Bits == 16 ? SwapForPredictor(work) : work, rowBytes, channels, Bits);
            if (BigEndian && Bits == 16)
            {
                SwapForPredictor(work);
            }
        }
        else if (Predictor == 3)
        {
            // Float predictor input is native little-endian floats: rows hold the big-endian bit patterns when the file is big-endian.
            if (BigEndian)
            {
                TiffKernels.SwapBytes(work, work, 4);
            }

            TiffKernels.ApplyFloat(work, rowBytes, channels, new byte[rowBytes]);
        }

        byte[] data = Compression switch
        {
            1 => work,
            5 => LzwEncoder.Compress(work, LzwVariant.Tiff),
            8 or 32946 => Deflater.Compress(work, 6, zlib: true),
            32773 => PackBitsAll(work, rowBytes),
            _ => throw new NotSupportedException(),
        };
        if (FillOrder == 2)
        {
            data = (byte[])data.Clone();
            TiffKernels.ReverseBits(data);
        }

        return data;
    }

    // The kernel's 16-bit predictor works on little-endian rows; swap a big-endian block around the call.
    private static byte[] SwapForPredictor(byte[] data)
    {
        TiffKernels.SwapBytes(data, data, 2);
        return data;
    }

    private static byte[] PackBitsAll(byte[] raw, int rowBytes)
    {
        var ms = new MemoryStream();
        byte[] tmp = new byte[rowBytes + (rowBytes / 128) + 4];
        for (int r = 0; r + rowBytes <= raw.Length; r += rowBytes)
        {
            int n = TiffKernels.PackBitsEncode(raw.AsSpan(r, rowBytes), tmp);
            ms.Write(tmp, 0, n);
        }

        return ms.ToArray();
    }

    /// <summary>Compresses a block assembled by the test (CCITT and friends) in place of the built-in packers.</summary>
    public Func<int, byte[]>? BlockOverride { get; set; }

    public byte[] Build()
    {
        var blocks = new List<byte[]>();
        int planes = Planar ? Spp : 1;
        int spp = Planar ? 1 : Spp;
        int bw = Tiled ? TileW : Width;
        int bh = Tiled ? TileH : Math.Min(RowsPerStrip, Height);
        int across = (Width + bw - 1) / bw;
        int down = (Height + bh - 1) / bh;
        for (int p = 0; p < planes; p++)
        {
            for (int by = 0; by < down; by++)
            {
                for (int bx = 0; bx < across; bx++)
                {
                    int rows = Tiled ? bh : Math.Min(bh, Height - (by * bh));
                    var raw = new List<byte>();
                    int rowBytes = ((bw * spp * Bits) + 7) / 8;
                    for (int r = 0; r < rows; r++)
                    {
                        raw.AddRange(PackRow((by * bh) + r, bx * bw, bw, p));
                    }

                    blocks.Add(BlockOverride is { } ov ? ov(blocks.Count) : Compress(raw.ToArray(), rowBytes, spp));
                }
            }
        }

        bool be = BigEndian;
        var ms = new MemoryStream();
        void W16(int v) { Span<byte> b = stackalloc byte[2]; TiffMetadataIo.PutU16(b, (ushort)v, be); ms.Write(b); }
        void W32(long v) { Span<byte> b = stackalloc byte[4]; TiffMetadataIo.PutU32(b, (uint)v, be); ms.Write(b); }
        void W64(long v) { Span<byte> b = stackalloc byte[8]; TiffMetadataIo.PutU64(b, (ulong)v, be); ms.Write(b); }

        // Layout: header, strip data, then the directory with its arrays.
        ms.Write([(byte)(be ? 'M' : 'I'), (byte)(be ? 'M' : 'I')]);
        int headerLen = BigTiff ? 16 : 8;
        if (BigTiff)
        {
            W16(43);
            W16(8);
            W16(0);
            W64(0);
        }
        else
        {
            W16(42);
            W32(0);
        }

        var offsets = new List<long>();
        foreach (byte[] b in blocks)
        {
            if ((ms.Length & 1) != 0)
            {
                ms.WriteByte(0);
            }

            offsets.Add(ms.Length);
            ms.Write(b);
        }

        if ((ms.Length & 1) != 0)
        {
            ms.WriteByte(0);
        }

        var entries = new List<TiffTreeEntry>();
        TiffTreeEntry Short(int tag, params int[] v)
        {
            byte[] d = new byte[v.Length * 2];
            for (int i = 0; i < v.Length; i++)
            {
                TiffMetadataIo.PutU16(d.AsSpan(i * 2), (ushort)v[i], be);
            }

            return new TiffTreeEntry(tag, 3, v.Length, d);
        }

        TiffTreeEntry Long(int tag, int type, params long[] v)
        {
            int w = type == 16 ? 8 : 4;
            byte[] d = new byte[v.Length * w];
            for (int i = 0; i < v.Length; i++)
            {
                if (w == 8)
                {
                    TiffMetadataIo.PutU64(d.AsSpan(i * 8), (ulong)v[i], be);
                }
                else
                {
                    TiffMetadataIo.PutU32(d.AsSpan(i * 4), (uint)v[i], be);
                }
            }

            return new TiffTreeEntry(tag, type, v.Length, d);
        }

        int numType = BigTiff ? 16 : 4;
        entries.Add(Long(256, 4, Width));
        entries.Add(Long(257, 4, Height));
        entries.Add(Short(258, Enumerable.Repeat(Bits, Spp).ToArray()));
        entries.Add(Short(259, Compression));
        entries.Add(Short(262, Photometric));
        if (FillOrder != 1)
        {
            entries.Add(Short(266, FillOrder));
        }

        entries.Add(Short(277, Spp));
        entries.Add(Short(284, Planar ? 2 : 1));
        if (Predictor != 1)
        {
            entries.Add(Short(317, Predictor));
        }

        if (Tiled)
        {
            entries.Add(Long(322, 4, TileW));
            entries.Add(Long(323, 4, TileH));
            entries.Add(Long(324, numType, offsets.ToArray()));
            entries.Add(Long(325, numType, blocks.Select(b => (long)b.Length).ToArray()));
        }
        else
        {
            entries.Add(Long(273, numType, offsets.ToArray()));
            entries.Add(Long(278, 4, bh));
            entries.Add(Long(279, numType, blocks.Select(b => (long)b.Length).ToArray()));
        }

        if (ColorMap is not null)
        {
            entries.Add(Short(320, ColorMap.Select(v => (int)v).ToArray()));
        }

        if (ExtraSamples.Length > 0)
        {
            entries.Add(Short(338, ExtraSamples));
        }

        if (FloatSamples)
        {
            entries.Add(Short(339, Enumerable.Repeat(3, Spp).ToArray()));
        }

        foreach ((int tag, int type, byte[] value, long count) in Extra)
        {
            entries.Add(new TiffTreeEntry(tag, type, count, value));
        }

        entries.Sort((a, c) => a.Tag.CompareTo(c.Tag));
        long ifdPos = ms.Length;
        var b2 = new TiffByteBuilder();
        TiffMetadataIo.WriteIfd(b2, ifdPos, entries, BigTiff, be, 0);
        ms.Write(b2.Written);
        byte[] file = ms.ToArray();
        if (BigTiff)
        {
            TiffMetadataIo.PutU64(file.AsSpan(8), (ulong)ifdPos, be);
        }
        else
        {
            TiffMetadataIo.PutU32(file.AsSpan(4), (uint)ifdPos, be);
        }

        _ = headerLen;
        return file;
    }
}
