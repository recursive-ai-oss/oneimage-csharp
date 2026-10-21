using System.Buffers.Binary;

namespace OneImage.Codecs.Tiff;

/// <summary>TIFF tag numbers used by the codec.</summary>
internal static class TiffTag
{
    public const int NewSubfileType = 254;
    public const int ImageWidth = 256;
    public const int ImageLength = 257;
    public const int BitsPerSample = 258;
    public const int Compression = 259;
    public const int Photometric = 262;
    public const int FillOrder = 266;
    public const int ImageDescription = 270;
    public const int Make = 271;
    public const int Model = 272;
    public const int StripOffsets = 273;
    public const int Orientation = 274;
    public const int SamplesPerPixel = 277;
    public const int RowsPerStrip = 278;
    public const int StripByteCounts = 279;
    public const int PlanarConfiguration = 284;
    public const int T4Options = 292;
    public const int T6Options = 293;
    public const int PageNumber = 297;
    public const int Software = 305;
    public const int DateTime = 306;
    public const int Artist = 315;
    public const int Predictor = 317;
    public const int ColorMap = 320;
    public const int TileWidth = 322;
    public const int TileLength = 323;
    public const int TileOffsets = 324;
    public const int TileByteCounts = 325;
    public const int SubIfd = 330;
    public const int InkSet = 332;
    public const int ExtraSamples = 338;
    public const int SampleFormat = 339;
    public const int JpegTables = 347;
    public const int JpegInterchangeFormat = 513;
    public const int JpegInterchangeFormatLength = 514;
    public const int Xmp = 700;
    public const int Copyright = 33432;
    public const int Iptc = 33723;
    public const int ExifIfd = 34665;
    public const int IccProfile = 34675;
    public const int GpsIfd = 34853;
    public const int InteropIfd = 40965;
}

/// <summary>One directory entry as stored: the value field is kept raw, in file byte order.</summary>
internal readonly struct TiffEntry
{
    public TiffEntry(int tag, int type, long count, byte[] valueField)
    {
        Tag = tag;
        Type = type;
        Count = count;
        ValueField = valueField;
    }

    public int Tag { get; }

    public int Type { get; }

    public long Count { get; }

    /// <summary>The 4 (classic) or 8 (BigTIFF) bytes after the count: the inline value or the offset to it.</summary>
    public byte[] ValueField { get; }

    public static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0,
    };
}

/// <summary>
/// Bounds-checked reader over a TIFF stream: byte order, classic or BigTIFF layout, and directory entries.
/// Every offset and count that comes from the file is checked against the stream length before it is used to
/// read or allocate.
/// </summary>
internal sealed class TiffFile
{
    private readonly Stream _stream;
    private readonly ImageLimits _limits;

    private TiffFile(Stream stream, bool bigEndian, bool bigTiff, long firstIfd, ImageLimits limits)
    {
        _stream = stream;
        BigEndian = bigEndian;
        BigTiff = bigTiff;
        FirstIfd = firstIfd;
        Length = stream.Length;
        _limits = limits;
    }

    /// <summary>Gets a reader over another stream holding the same file (the header is not read again).</summary>
    public TiffFile Rebind(Stream stream) => new(stream, BigEndian, BigTiff, FirstIfd, _limits);

    public bool BigEndian { get; }

    public bool BigTiff { get; }

    public long FirstIfd { get; }

    public long Length { get; }

    public static TiffFile Open(Stream stream, ImageLimits limits)
    {
        Span<byte> h = stackalloc byte[16];
        stream.Position = 0;
        int got = stream.ReadAtLeast(h[..8], 8, throwOnEndOfStream: false);
        if (got < 8)
        {
            throw new ImageFormatException("The TIFF header is truncated.");
        }

        bool be;
        if (h[0] == 'I' && h[1] == 'I')
        {
            be = false;
        }
        else if (h[0] == 'M' && h[1] == 'M')
        {
            be = true;
        }
        else
        {
            throw new ImageFormatException("Not a TIFF file: bad byte-order mark.");
        }

        int magic = be ? BinaryPrimitives.ReadUInt16BigEndian(h[2..]) : BinaryPrimitives.ReadUInt16LittleEndian(h[2..]);
        if (magic == 42)
        {
            uint off = be ? BinaryPrimitives.ReadUInt32BigEndian(h[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            return new TiffFile(stream, be, false, off, limits);
        }

        if (magic == 43)
        {
            if (stream.ReadAtLeast(h[8..], 8, throwOnEndOfStream: false) < 8)
            {
                throw new ImageFormatException("The BigTIFF header is truncated.");
            }

            int offSize = be ? BinaryPrimitives.ReadUInt16BigEndian(h[4..]) : BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
            int zero = be ? BinaryPrimitives.ReadUInt16BigEndian(h[6..]) : BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
            if (offSize != 8 || zero != 0)
            {
                throw new ImageFormatException("Unsupported BigTIFF offset size.");
            }

            ulong off = be ? BinaryPrimitives.ReadUInt64BigEndian(h[8..]) : BinaryPrimitives.ReadUInt64LittleEndian(h[8..]);
            if (off > long.MaxValue)
            {
                throw new ImageFormatException("The first directory offset is out of range.");
            }

            return new TiffFile(stream, be, true, (long)off, limits);
        }

        throw new ImageFormatException("Not a TIFF file: bad version number.");
    }

    public ushort U16(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    public uint U32(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);

    public ulong U64(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s);

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes at <paramref name="offset"/> or fails with a format error.</summary>
    public void ReadAt(long offset, Span<byte> destination)
    {
        if (offset < 0 || destination.Length > Length - offset)
        {
            throw new ImageFormatException("A TIFF offset points outside the file.");
        }

        _stream.Position = offset;
        _stream.ReadExactly(destination);
    }

    /// <summary>Reads one directory: its entries and the offset of the next directory (0 for none).</summary>
    public List<TiffEntry> ReadIfd(long offset, out long nextIfd)
    {
        int headSize = BigTiff ? 8 : 2;
        Span<byte> head = stackalloc byte[8];
        ReadAt(offset, head[..headSize]);
        long count = BigTiff ? Pos(U64(head)) : U16(head);
        int entrySize = BigTiff ? 20 : 12;
        // An entry table that does not fit in the file cannot be valid, whatever the count says.
        if (count < 0 || count > (Length - offset - headSize) / entrySize)
        {
            throw new ImageFormatException("A TIFF directory extends past the end of the file.");
        }

        byte[] table = new byte[(count * entrySize) + (BigTiff ? 8 : 4)];
        ReadAt(offset + headSize, table);
        var list = new List<TiffEntry>((int)count);
        int valueSize = BigTiff ? 8 : 4;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = table.AsSpan(i * entrySize, entrySize);
            int tag = U16(e);
            int type = U16(e[2..]);
            long c = BigTiff ? Pos(U64(e[4..])) : U32(e[4..]);
            list.Add(new TiffEntry(tag, type, c, e.Slice(BigTiff ? 12 : 8, valueSize).ToArray()));
        }

        ReadOnlySpan<byte> tail = table.AsSpan((int)(count * entrySize));
        long next = BigTiff ? Pos(U64(tail)) : U32(tail);
        nextIfd = next;
        return list;
    }

    private static long Pos(ulong v) => v <= long.MaxValue ? (long)v : throw new ImageFormatException("A TIFF offset or count is out of range.");

    public long ValueOffset(in TiffEntry e) => BigTiff ? Pos(U64(e.ValueField)) : U32(e.ValueField);

    /// <summary>Returns the entry's value bytes in file byte order, at most <paramref name="maxBytes"/> of them.</summary>
    public byte[] ReadBytes(in TiffEntry e, long maxBytes)
    {
        int ts = TiffEntry.TypeSize(e.Type);
        if (ts == 0 || e.Count < 0)
        {
            throw new ImageFormatException("A TIFF entry has an unknown type.");
        }

        if (e.Count > Length / ts + 1)
        {
            throw new ImageFormatException("A TIFF entry is larger than the file.");
        }

        long size = e.Count * ts;
        if (size > maxBytes)
        {
            throw new ImageLimitException($"A TIFF entry of {size} bytes exceeds the allowed size.");
        }

        int inline = BigTiff ? 8 : 4;
        if (size <= inline)
        {
            return e.ValueField.AsSpan(0, (int)size).ToArray();
        }

        byte[] data = new byte[size];
        ReadAt(ValueOffset(e), data);
        return data;
    }

    /// <summary>Reads an integer-valued entry (BYTE, SHORT, LONG, LONG8 and signed forms) as 64-bit values.</summary>
    public long[] ReadIntegers(in TiffEntry e, long maxCount = 1 << 22)
    {
        if (e.Count > maxCount)
        {
            throw new ImageLimitException($"A TIFF entry has {e.Count} values, more than the {maxCount} allowed.");
        }

        byte[] raw = ReadBytes(e, maxCount * 8);
        long[] r = new long[e.Count];
        switch (e.Type)
        {
            case 1 or 7:
                for (int i = 0; i < r.Length; i++)
                {
                    r[i] = raw[i];
                }

                break;
            case 3:
                for (int i = 0; i < r.Length; i++)
                {
                    r[i] = U16(raw.AsSpan(i * 2));
                }

                break;
            case 4 or 13:
                for (int i = 0; i < r.Length; i++)
                {
                    r[i] = U32(raw.AsSpan(i * 4));
                }

                break;
            case 16 or 18:
                for (int i = 0; i < r.Length; i++)
                {
                    ulong v = U64(raw.AsSpan(i * 8));
                    if (v > long.MaxValue)
                    {
                        throw new ImageFormatException("A TIFF value is out of range.");
                    }

                    r[i] = (long)v;
                }

                break;
            default:
                throw new ImageFormatException($"TIFF tag {e.Tag} has an unexpected value type {e.Type}.");
        }

        return r;
    }

    /// <summary>Gets the metadata size cap for a single blob.</summary>
    public long BlobLimit => _limits.MaxMetadataBytes;
}
