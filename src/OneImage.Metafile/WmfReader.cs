using System.Buffers.Binary;

namespace OneImage.Metafile;

/// <summary>What the header and a first walk over the records of a Windows metafile say about the picture.</summary>
internal readonly record struct WmfHeader(bool Placeable, int HeaderStart, int RecordsStart, short Left, short Top, short Right, short Bottom, ushort UnitsPerInch);

/// <summary>Where the records of a synthesized enhanced metafile came from, so diagnostics can name the original WMF record and function.</summary>
internal sealed class WmfRecordMap
{
    private readonly List<int> _index = [];
    private readonly List<int> _function = [];

    public void Add(int wmfIndex, int function)
    {
        _index.Add(wmfIndex);
        _function.Add(function);
    }

    public int Index(int emfRecord) => (uint)emfRecord < (uint)_index.Count ? _index[emfRecord] : emfRecord;

    public int Function(int emfRecord) => (uint)emfRecord < (uint)_function.Count ? _function[emfRecord] : -1;
}

/// <summary>Reading the Windows metafile header and describing the picture, shared by <c>Identify</c> and the converter.</summary>
internal static class WmfReader
{
    public const uint PlaceableKey = 0x9AC6CDD7;

    /// <summary>The reference device of a converted picture: a 96 dpi display. Logical units of a WMF without a unit of its own are pixels of it.</summary>
    public const double Dpi = 96;

    public static bool IsWmf(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header) == PlaceableKey)
        {
            return true;
        }

        // META_HEADER: Type (1 memory, 2 disk), HeaderSize (9 words), Version (0x0100 or 0x0300).
        return header.Length >= 6
            && BinaryPrimitives.ReadUInt16LittleEndian(header) is 1 or 2
            && BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) == 9
            && BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) is 0x0100 or 0x0300;
    }

    public static bool IsPlaceable(ReadOnlySpan<byte> header) => header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header) == PlaceableKey;

    public static WmfHeader ReadHeader(ReadOnlySpan<byte> d)
    {
        bool placeable = IsPlaceable(d);
        int start = placeable ? 22 : 0;
        if (!IsWmf(d) || d.Length < start + 18 || (placeable && !IsWmf(d[start..])))
        {
            throw new ImageFormatException("The data is not a Windows metafile (no META_HEADER with a valid type, size and version).");
        }

        int headerBytes = BinaryPrimitives.ReadUInt16LittleEndian(d[(start + 2)..]) * 2;
        int records = start + Math.Max(18, headerBytes);
        if (records > d.Length)
        {
            throw new ImageFormatException("The metafile header is cut short.");
        }

        if (!placeable)
        {
            return new WmfHeader(false, start, records, 0, 0, 0, 0, 0);
        }

        return new WmfHeader(
            true,
            start,
            records,
            BinaryPrimitives.ReadInt16LittleEndian(d[6..]),
            BinaryPrimitives.ReadInt16LittleEndian(d[8..]),
            BinaryPrimitives.ReadInt16LittleEndian(d[10..]),
            BinaryPrimitives.ReadInt16LittleEndian(d[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(d[14..]));
    }

    /// <summary>The records of the file: a first pass over record headers that needs no object table.</summary>
    public readonly record struct Scan(int Records, bool HasWindowOrg, int OrgX, int OrgY, bool HasWindowExt, int ExtX, int ExtY, bool HasBounds, int MinX, int MinY, int MaxX, int MaxY);

    public static Scan ScanRecords(ReadOnlySpan<byte> d, int offset, MetafileLimits limits, bool needBounds)
    {
        int records = 0;
        bool hasOrg = false, hasExt = false, hasBounds = false;
        int orgX = 0, orgY = 0, extX = 0, extY = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        void Point(int x, int y)
        {
            hasBounds = true;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        int off = offset;
        while (off + 6 <= d.Length)
        {
            uint words = BinaryPrimitives.ReadUInt32LittleEndian(d[off..]);
            int function = BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 4)..]);
            if (words < 3 || (ulong)off + (words * 2UL) > (ulong)d.Length)
            {
                break;
            }

            if (++records > limits.MaxRecords)
            {
                throw new ImageLimitException($"The metafile has more than {limits.MaxRecords} records.");
            }

            ReadOnlySpan<byte> r = d.Slice(off, (int)(words * 2));
            if (function == 0)
            {
                break;
            }

            switch (function)
            {
                case 0x020B when r.Length >= 10 && !hasOrg:
                    (hasOrg, orgY, orgX) = (true, S16(r, 6), S16(r, 8));
                    break;
                case 0x020C when r.Length >= 10 && !hasExt:
                    (hasExt, extY, extX) = (true, S16(r, 6), S16(r, 8));
                    break;
                case 0x041B or 0x0418 when needBounds && r.Length >= 14:
                    Point(S16(r, 12), S16(r, 10));
                    Point(S16(r, 8), S16(r, 6));
                    break;
                case 0x061C when needBounds && r.Length >= 18:
                    Point(S16(r, 16), S16(r, 14));
                    Point(S16(r, 12), S16(r, 10));
                    break;
                case 0x0213 or 0x0214 when needBounds && r.Length >= 10:
                    Point(S16(r, 8), S16(r, 6));
                    break;
                case 0x0324 or 0x0325 when needBounds && r.Length >= 8:
                    {
                        int n = Math.Min(S16(r, 6), (r.Length - 8) / 4);
                        for (int i = 0; i < n; i++)
                        {
                            Point(S16(r, 8 + (i * 4)), S16(r, 10 + (i * 4)));
                        }

                        break;
                    }
            }

            off += (int)(words * 2);
        }

        return new Scan(records, hasOrg, orgX, orgY, hasExt, extX, extY, hasBounds, minX, minY, maxX, maxY);
    }

    public static int S16(ReadOnlySpan<byte> r, int o) => BinaryPrimitives.ReadInt16LittleEndian(r[o..]);

    public static int U16(ReadOnlySpan<byte> r, int o) => BinaryPrimitives.ReadUInt16LittleEndian(r[o..]);

    /// <summary>The window and the picture size, in the logical units the file draws in and in device pixels.</summary>
    internal readonly record struct Layout(int WindowOrgX, int WindowOrgY, int WindowExtX, int WindowExtY, double PixelWidth, double PixelHeight, int RecordCount);

    public static Layout Describe(ReadOnlySpan<byte> d, MetafileLimits limits)
    {
        WmfHeader h = ReadHeader(d);
        bool needBounds = true;
        if (h.Placeable)
        {
            int w0 = h.Right - h.Left, h0 = h.Bottom - h.Top;
            needBounds = w0 == 0 || h0 == 0;
        }

        Scan s = ScanRecords(d, h.RecordsStart, limits, needBounds);
        int ox, oy, ex, ey;
        double scale;
        if (h.Placeable && h.Right != h.Left && h.Bottom != h.Top)
        {
            (ox, oy, ex, ey) = (h.Left, h.Top, h.Right - h.Left, h.Bottom - h.Top);
            scale = Dpi / (h.UnitsPerInch == 0 ? 1440 : h.UnitsPerInch);
        }
        else
        {
            // No frame: the window the file sets is the picture, in pixels of the reference device; failing that, what it draws.
            scale = 1;
            if (s.HasWindowExt && s.ExtX != 0 && s.ExtY != 0)
            {
                (ox, oy, ex, ey) = (s.HasWindowOrg ? s.OrgX : 0, s.HasWindowOrg ? s.OrgY : 0, s.ExtX, s.ExtY);
            }
            else if (s.HasBounds)
            {
                (ox, oy, ex, ey) = (s.MinX, s.MinY, Math.Max(1, s.MaxX - s.MinX), Math.Max(1, s.MaxY - s.MinY));
            }
            else
            {
                (ox, oy, ex, ey) = (0, 0, 256, 256);
            }
        }

        return new Layout(ox, oy, ex, ey, Math.Abs((double)ex) * scale, Math.Abs((double)ey) * scale, s.Records);
    }

    public static MetafileInfo Identify(ReadOnlySpan<byte> d, MetafileLimits limits)
    {
        Layout l = Describe(d, limits);
        const double mmPerPixel = 25.4 / Dpi;
        double w = Math.Max(1e-3, l.PixelWidth), h = Math.Max(1e-3, l.PixelHeight);
        return new MetafileInfo(
            MetafileKind.Wmf,
            new RectD(0, 0, Math.Max(0, Math.Ceiling(w) - 1), Math.Max(0, Math.Ceiling(h) - 1)),
            new RectD(0, 0, w * mmPerPixel * 100, h * mmPerPixel * 100),
            1024,
            768,
            1024 * mmPerPixel,
            768 * mmPerPixel,
            new RectD(0, 0, w, h),
            string.Empty,
            l.RecordCount,
            false,
            false);
    }
}
