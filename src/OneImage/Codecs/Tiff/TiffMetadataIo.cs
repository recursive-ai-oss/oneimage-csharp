using System.Buffers.Binary;

namespace OneImage.Codecs.Tiff;

/// <summary>A directory entry with its complete value, in the byte order of the structure it came from.</summary>
internal sealed class TiffTreeEntry
{
    public TiffTreeEntry(int tag, int type, long count, byte[] value)
    {
        Tag = tag;
        Type = type;
        Count = count;
        Value = value;
    }

    public int Tag { get; }

    public int Type { get; set; }

    public long Count { get; set; }

    public byte[] Value { get; set; }

    /// <summary>Gets or sets a nested directory (EXIF, GPS, interoperability); then <see cref="Value"/> is ignored.</summary>
    public List<TiffTreeEntry>? Sub { get; set; }
}

/// <summary>Growable byte buffer with in-place patching, used to lay out directories.</summary>
internal sealed class TiffByteBuilder
{
    private byte[] _buf = new byte[256];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _buf.AsSpan(0, Length);

    public void Append(ReadOnlySpan<byte> data)
    {
        Reserve(data.Length);
        data.CopyTo(_buf.AsSpan(Length));
        Length += data.Length;
    }

    public void AppendZeros(int n)
    {
        Reserve(n);
        _buf.AsSpan(Length, n).Clear();
        Length += n;
    }

    public void AlignEven()
    {
        if ((Length & 1) != 0)
        {
            AppendZeros(1);
        }
    }

    public Span<byte> At(int pos, int len) => _buf.AsSpan(pos, len);

    private void Reserve(int n)
    {
        if ((long)Length + n > int.MaxValue / 2)
        {
            throw new ImageLimitException("The TIFF directory data is too large.");
        }

        if (Length + n > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        }
    }
}

/// <summary>
/// Metadata handling: reads ICC/XMP/IPTC/EXIF/orientation from the first directory, and lays out directories (the
/// image's own and nested EXIF ones) for the encoder. The EXIF blob exposed to callers is a small self-contained
/// TIFF structure (header, directory with the descriptive tags and the EXIF/GPS pointers, and those directories),
/// the same shape as the payload of a JPEG EXIF segment, so it can move between formats unchanged.
/// </summary>
internal static class TiffMetadataIo
{
    private static readonly int[] s_descriptiveTags =
    [
        TiffTag.ImageDescription, TiffTag.Make, TiffTag.Model, TiffTag.Software, TiffTag.DateTime, TiffTag.Artist, TiffTag.Copyright,
    ];

    public static ImageMetadata Read(TiffFile file, List<TiffEntry> ifd0, ImageLimits limits)
    {
        long budget = limits.MaxMetadataBytes;
        byte[] icc = [];
        byte[] xmp = [];
        byte[] iptc = [];
        int orientation = 1;
        foreach (TiffEntry e in ifd0)
        {
            switch (e.Tag)
            {
                case TiffTag.IccProfile:
                    icc = ReadBlob(file, e, ref budget);
                    break;
                case TiffTag.Xmp:
                    xmp = ReadBlob(file, e, ref budget);
                    break;
                case TiffTag.Iptc:
                    iptc = ReadBlob(file, e, ref budget);
                    break;
                case TiffTag.Orientation when e.Type == 3 && e.Count >= 1:
                {
                    long o = file.ReadIntegers(e, 8)[0];
                    orientation = o is >= 1 and <= 8 ? (int)o : 1;
                    break;
                }
            }
        }

        byte[] exif = BuildExifBlob(file, ifd0, ref budget);
        return new ImageMetadata { IccProfile = icc, Exif = exif, Xmp = xmp, Iptc = iptc, Orientation = orientation };
    }

    private static byte[] ReadBlob(TiffFile file, TiffEntry e, ref long budget)
    {
        int ts = TiffEntry.TypeSize(e.Type);
        if (ts == 0 || e.Count * ts > budget)
        {
            if (ts != 0 && e.Count * ts > budget)
            {
                throw new ImageLimitException("TIFF metadata exceeds the metadata size limit.");
            }

            return [];
        }

        byte[] b = file.ReadBytes(e, budget);
        budget -= b.Length;
        return b;
    }

    private static byte[] BuildExifBlob(TiffFile file, List<TiffEntry> ifd0, ref long budget)
    {
        List<TiffTreeEntry>? tree = ReadTree(file, ifd0, 0, ref budget);
        if (tree is null || tree.Count == 0)
        {
            return [];
        }

        var b = new TiffByteBuilder();
        b.AppendZeros(8);
        Span<byte> h = b.At(0, 8);
        h[0] = h[1] = file.BigEndian ? (byte)'M' : (byte)'I';
        PutU16(h[2..], 42, file.BigEndian);
        PutU32(h[4..], 8, file.BigEndian);
        WriteIfd(b, 0, tree, bigTiff: false, file.BigEndian, 0);
        return b.Written.ToArray();
    }

    /// <summary>Reads a directory into a tree. Nesting is capped at EXIF -> interoperability, so input cannot drive recursion depth.</summary>
    private static List<TiffTreeEntry>? ReadTree(TiffFile file, List<TiffEntry> entries, int depth, ref long budget)
    {
        var list = new List<TiffTreeEntry>();
        foreach (TiffEntry e in entries)
        {
            bool pointer = e.Tag is TiffTag.ExifIfd or TiffTag.GpsIfd or TiffTag.InteropIfd;
            if (depth == 0 && !pointer && Array.IndexOf(s_descriptiveTags, e.Tag) < 0)
            {
                continue;
            }

            if (pointer)
            {
                bool wanted = depth == 0 ? e.Tag != TiffTag.InteropIfd : depth == 1 && e.Tag == TiffTag.InteropIfd;
                if (!wanted || e.Count != 1 || e.Type is not (4 or 13 or 16 or 18))
                {
                    continue;
                }

                long off = file.ReadIntegers(e, 1)[0];
                List<TiffTreeEntry>? sub;
                try
                {
                    sub = ReadTree(file, file.ReadIfd(off, out _), depth + 1, ref budget);
                }
                catch (ImageFormatException)
                {
                    continue; // a damaged nested directory costs only that directory
                }

                if (sub is { Count: > 0 })
                {
                    list.Add(new TiffTreeEntry(e.Tag, 4, 1, []) { Sub = sub });
                }

                continue;
            }

            int ts = TiffEntry.TypeSize(e.Type);
            if (ts == 0 || e.Type >= 14)
            {
                continue;
            }

            long size = e.Count * ts;
            if (e.Count > file.Length || size > budget)
            {
                if (size > budget && e.Count <= file.Length)
                {
                    throw new ImageLimitException("TIFF metadata exceeds the metadata size limit.");
                }

                continue;
            }

            byte[] value = file.ReadBytes(e, budget);
            budget -= value.Length;
            list.Add(new TiffTreeEntry(e.Tag, e.Type, e.Count, value));
        }

        list.Sort((a, c) => a.Tag.CompareTo(c.Tag));
        return list;
    }

    /// <summary>Parses an EXIF blob (optionally with the "Exif\0\0" prefix) into entries with little-endian values; null when it is not usable.</summary>
    public static List<TiffTreeEntry>? ParseBlob(ReadOnlyMemory<byte> blob, ImageLimits limits)
    {
        ReadOnlySpan<byte> s = blob.Span;
        if (s.Length >= 6 && s[0] == 'E' && s[1] == 'x' && s[2] == 'i' && s[3] == 'f' && s[4] == 0 && s[5] == 0)
        {
            blob = blob[6..];
        }

        if (blob.Length < 8)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(blob.ToArray(), writable: false);
            TiffFile f = TiffFile.Open(ms, limits);
            if (f.BigTiff)
            {
                return null;
            }

            List<TiffEntry> ifd0 = f.ReadIfd(f.FirstIfd, out _);
            long budget = limits.MaxMetadataBytes;
            List<TiffTreeEntry>? tree = ReadTree(f, ifd0, 0, ref budget);
            if (tree is not null && f.BigEndian)
            {
                SwapTree(tree);
            }

            return tree;
        }
        catch (ImageFormatException)
        {
            return null; // damaged EXIF is dropped rather than failing the whole save
        }
    }

    private static void SwapTree(List<TiffTreeEntry> tree)
    {
        foreach (TiffTreeEntry e in tree)
        {
            if (e.Sub is not null)
            {
                SwapTree(e.Sub);
                continue;
            }

            int unit = e.Type switch
            {
                3 or 8 => 2,
                4 or 9 or 11 or 13 or 5 or 10 => 4,
                12 => 8,
                _ => 1,
            };
            if (unit > 1)
            {
                for (int i = 0; i + unit <= e.Value.Length; i += unit)
                {
                    Array.Reverse(e.Value, i, unit);
                }
            }
        }
    }

    /// <summary>
    /// Appends the directory (entries must be sorted by tag) with its out-of-line values and nested directories to
    /// <paramref name="b"/>. Offsets are written as <paramref name="absBase"/> plus the position in the buffer.
    /// </summary>
    /// <returns>The absolute offset of the directory.</returns>
    public static long WriteIfd(TiffByteBuilder b, long absBase, List<TiffTreeEntry> entries, bool bigTiff, bool bigEndian, long next)
    {
        b.AlignEven();
        int start = b.Length;
        int head = bigTiff ? 8 : 2;
        int esz = bigTiff ? 20 : 12;
        int inline = bigTiff ? 8 : 4;
        b.AppendZeros(head + (entries.Count * esz) + inline);
        if (bigTiff)
        {
            PutU64(b.At(start, 8), (ulong)entries.Count, bigEndian);
        }
        else
        {
            PutU16(b.At(start, 2), (ushort)entries.Count, bigEndian);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            TiffTreeEntry e = entries[i];
            int pos = start + head + (i * esz);
            byte[] value = e.Value;
            int type = e.Type;
            long count = e.Count;
            if (e.Sub is not null)
            {
                long off = WriteIfd(b, absBase, e.Sub, bigTiff, bigEndian, 0);
                value = new byte[inline];
                if (bigTiff)
                {
                    PutU64(value, (ulong)off, bigEndian);
                }
                else
                {
                    PutU32(value, checked((uint)off), bigEndian);
                }

                type = 4;
                count = 1;
            }
            else if (value.Length > inline)
            {
                b.AlignEven();
                long off = absBase + b.Length;
                b.Append(value);
                byte[] ptr = new byte[inline];
                if (bigTiff)
                {
                    PutU64(ptr, (ulong)off, bigEndian);
                }
                else
                {
                    if (off > uint.MaxValue)
                    {
                        throw new ImageLimitException("The TIFF is too large for classic offsets; use BigTIFF.");
                    }

                    PutU32(ptr, (uint)off, bigEndian);
                }

                value = ptr;
            }

            Span<byte> p = b.At(pos, esz);
            PutU16(p, (ushort)e.Tag, bigEndian);
            PutU16(p[2..], (ushort)type, bigEndian);
            if (bigTiff)
            {
                PutU64(p[4..], (ulong)count, bigEndian);
                value.CopyTo(p[12..]);
            }
            else
            {
                PutU32(p[4..], (uint)count, bigEndian);
                value.CopyTo(p[8..]);
            }
        }

        Span<byte> tail = b.At(start + head + (entries.Count * esz), inline);
        if (bigTiff)
        {
            PutU64(tail, (ulong)next, bigEndian);
        }
        else
        {
            PutU32(tail, (uint)next, bigEndian);
        }

        return absBase + start;
    }

    public static void PutU16(Span<byte> d, ushort v, bool be)
    {
        if (be)
        {
            BinaryPrimitives.WriteUInt16BigEndian(d, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, v);
        }
    }

    public static void PutU32(Span<byte> d, uint v, bool be)
    {
        if (be)
        {
            BinaryPrimitives.WriteUInt32BigEndian(d, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, v);
        }
    }

    public static void PutU64(Span<byte> d, ulong v, bool be)
    {
        if (be)
        {
            BinaryPrimitives.WriteUInt64BigEndian(d, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(d, v);
        }
    }
}
