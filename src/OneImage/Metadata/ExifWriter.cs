using System.Buffers.Binary;

namespace OneImage;

/// <summary>
/// Corrects the dimension and orientation tags of an EXIF blob for output. Existing data (maker notes, thumbnails, long values)
/// is never moved: a changed IFD is rewritten at the end of the blob and the pointer to it is updated, so offsets stored inside
/// vendor data stay valid. The blob is parsed with the same bounds checks as <see cref="ExifData"/>.
/// </summary>
internal static class ExifWriter
{
    /// <summary>
    /// Returns a copy of <paramref name="exif"/> with orientation set to <paramref name="orientation"/> (an existing tag is always
    /// rewritten; a missing one is added only for values other than 1) and the pixel dimensions set to
    /// <paramref name="width"/> × <paramref name="height"/> (ImageWidth/ImageLength only when already present; PixelXDimension and
    /// PixelYDimension are added when missing). A leading <c>Exif\0\0</c> is kept when present.
    /// </summary>
    /// <exception cref="ImageFormatException">The blob is malformed.</exception>
    public static byte[] Patch(ReadOnlySpan<byte> exif, int width, int height, int orientation)
    {
        if (orientation is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(orientation));
        }

        var t = new TiffReader(exif);
        bool le = t.LittleEndian;
        var buf = new List<byte>(t.Data.Length + 128);
        buf.AddRange(t.Data);

        IfdEdit ifd0 = Load(t, t.FirstIfd);
        bool changed0 = false;

        // Orientation.
        if (ifd0.Entries.TryGetValue(0x0112, out byte[]? oe))
        {
            byte[] ne = Short(0x0112, orientation, le);
            if (!oe.AsSpan().SequenceEqual(ne))
            {
                ifd0.Entries[0x0112] = ne;
                changed0 = true;
            }
        }
        else if (orientation != 1)
        {
            ifd0.Entries[0x0112] = Short(0x0112, orientation, le);
            changed0 = true;
        }

        // ImageWidth/ImageLength are rewritten only when present.
        foreach ((ushort tag, int value) in new[] { ((ushort)0x0100, width), ((ushort)0x0101, height) })
        {
            if (ifd0.Entries.TryGetValue(tag, out byte[]? e))
            {
                byte[] ne = value <= 0xFFFF ? Short(tag, value, le) : Long(tag, (uint)value, le);
                if (!e.AsSpan().SequenceEqual(ne))
                {
                    ifd0.Entries[tag] = ne;
                    changed0 = true;
                }
            }
        }

        // Exif sub-IFD: PixelXDimension and PixelYDimension.
        IfdEdit exifIfd;
        if (ifd0.Entries.TryGetValue(0x8769, out byte[]? ptr))
        {
            exifIfd = Load(t, Read32(ptr, 8, le));
        }
        else
        {
            exifIfd = new IfdEdit();
        }

        bool changedExif = false;
        foreach ((ushort tag, int value) in new[] { ((ushort)0xA002, width), ((ushort)0xA003, height) })
        {
            byte[] ne = Long(tag, (uint)value, le);
            if (!exifIfd.Entries.TryGetValue(tag, out byte[]? e) || !e.AsSpan().SequenceEqual(ne))
            {
                exifIfd.Entries[tag] = ne;
                changedExif = true;
            }
        }

        if (!changed0 && !changedExif)
        {
            return WithPrefix(t, buf.ToArray());
        }

        if (changedExif)
        {
            uint newExif = Append(buf, exifIfd, le);
            ifd0.Entries[0x8769] = Long(0x8769, newExif, le);
        }

        uint newIfd0 = Append(buf, ifd0, le);
        byte[] arr = buf.ToArray();
        Write32(arr, 4, newIfd0, le);
        return WithPrefix(t, arr);
    }

    private static byte[] WithPrefix(TiffReader t, byte[] tiff)
    {
        if (!t.HasPrefix)
        {
            return tiff;
        }

        var r = new byte[tiff.Length + 6];
        "Exif\0\0"u8.CopyTo(r);
        tiff.CopyTo(r, 6);
        return r;
    }

    private sealed class IfdEdit
    {
        public SortedDictionary<ushort, byte[]> Entries { get; } = new();

        public uint Next { get; set; }
    }

    private static IfdEdit Load(TiffReader t, uint offset)
    {
        // Validate through the reader (table bounds and value ranges), then copy the raw 12-byte entries so unknown types survive.
        _ = t.Entries(offset);
        var ifd = new IfdEdit();
        int n = t.U16((int)offset);
        for (int i = 0; i < n; i++)
        {
            int e = (int)offset + 2 + (i * 12);
            ifd.Entries[t.U16(e)] = t.Data.AsSpan(e, 12).ToArray();
        }

        int nextPos = (int)offset + 2 + (n * 12);
        ifd.Next = nextPos <= t.Data.Length - 4 ? t.U32(nextPos) : 0;
        return ifd;
    }

    private static uint Append(List<byte> buf, IfdEdit ifd, bool le)
    {
        if ((buf.Count & 1) != 0)
        {
            buf.Add(0);
        }

        uint offset = (uint)buf.Count;
        var head = new byte[2];
        Put16(head, 0, (ushort)ifd.Entries.Count, le);
        buf.AddRange(head);
        foreach (byte[] e in ifd.Entries.Values)
        {
            buf.AddRange(e);
        }

        var next = new byte[4];
        Write32(next, 0, ifd.Next, le);
        buf.AddRange(next);
        return offset;
    }

    private static byte[] Short(ushort tag, int value, bool le)
    {
        var e = new byte[12];
        Put16(e, 0, tag, le);
        Put16(e, 2, 3, le);
        Write32(e, 4, 1, le);
        Put16(e, 8, (ushort)value, le);
        return e;
    }

    private static byte[] Long(ushort tag, uint value, bool le)
    {
        var e = new byte[12];
        Put16(e, 0, tag, le);
        Put16(e, 2, 4, le);
        Write32(e, 4, 1, le);
        Write32(e, 8, value, le);
        return e;
    }

    private static void Put16(byte[] b, int o, ushort v, bool le)
    {
        if (le)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v);
        }
    }

    private static void Write32(byte[] b, int o, uint v, bool le)
    {
        if (le)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);
        }
    }

    private static uint Read32(byte[] b, int o, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
}
