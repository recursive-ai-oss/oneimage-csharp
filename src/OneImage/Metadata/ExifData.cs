using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OneImage;

/// <summary>A GPS position read from EXIF. Latitude is positive north, longitude positive east, both in degrees.</summary>
/// <param name="Latitude">Degrees north (negative for south).</param>
/// <param name="Longitude">Degrees east (negative for west).</param>
/// <param name="Altitude">Meters above sea level (negative below), or <see langword="null"/> when absent.</param>
public sealed record GpsPosition(double Latitude, double Longitude, double? Altitude);

/// <summary>
/// The commonly used EXIF tags, read from an EXIF blob (a TIFF structure, with or without the leading <c>Exif\0\0</c>).
/// Reading is bounds-checked: offsets, counts and sizes are validated before use, loops between IFDs are cut, and only the
/// main IFD and its Exif and GPS sub-IFDs are visited, so malformed data gives <see cref="ImageFormatException"/> but never a crash or a hang.
/// Tags that are missing or have an unexpected type read as <see langword="null"/>.
/// </summary>
public sealed class ExifData
{
    private ExifData()
    {
    }

    /// <summary>Gets the orientation (1..8), or <see langword="null"/> when the tag is absent or out of range.</summary>
    public int? Orientation { get; private set; }

    /// <summary>Gets the camera manufacturer.</summary>
    public string? Make { get; private set; }

    /// <summary>Gets the camera model.</summary>
    public string? Model { get; private set; }

    /// <summary>Gets the software that last wrote the file.</summary>
    public string? Software { get; private set; }

    /// <summary>Gets the time the image was captured (DateTimeOriginal), falling back to DateTimeDigitized and then DateTime. No time zone is implied.</summary>
    public DateTime? CaptureTime { get; private set; }

    /// <summary>Gets the image width recorded in EXIF (PixelXDimension, or ImageWidth when missing).</summary>
    public int? PixelWidth { get; private set; }

    /// <summary>Gets the image height recorded in EXIF (PixelYDimension, or ImageLength when missing).</summary>
    public int? PixelHeight { get; private set; }

    /// <summary>Gets the GPS position when both latitude and longitude are present and valid.</summary>
    public GpsPosition? Gps { get; private set; }

    /// <summary>Parses an EXIF blob.</summary>
    /// <param name="exif">The blob.</param>
    /// <returns>The typed tags.</returns>
    /// <exception cref="ImageFormatException">The blob is not a valid TIFF/EXIF structure.</exception>
    public static ExifData Parse(ReadOnlySpan<byte> exif)
    {
        var tiff = new TiffReader(exif);
        var result = new ExifData();
        result.Read(tiff);
        return result;
    }

    /// <summary>Parses an EXIF blob without throwing.</summary>
    /// <param name="exif">The blob.</param>
    /// <param name="data">The tags on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryParse(ReadOnlySpan<byte> exif, out ExifData? data)
    {
        try
        {
            data = Parse(exif);
            return true;
        }
        catch (ImageFormatException)
        {
            data = null;
            return false;
        }
    }

    private void Read(TiffReader t)
    {
        var seen = new HashSet<uint>();
        uint ifd0 = t.FirstIfd;
        seen.Add(ifd0);
        uint exifIfd = 0, gpsIfd = 0;
        string? dateTime = null;
        int? w0 = null, h0 = null;
        foreach (TiffEntry e in t.Entries(ifd0))
        {
            switch (e.Tag)
            {
                case 0x0112:
                    int? o = t.Int(e);
                    Orientation = o is >= 1 and <= 8 ? o : null;
                    break;
                case 0x010F:
                    Make = t.Text(e);
                    break;
                case 0x0110:
                    Model = t.Text(e);
                    break;
                case 0x0131:
                    Software = t.Text(e);
                    break;
                case 0x0132:
                    dateTime = t.Text(e);
                    break;
                case 0x0100:
                    w0 = t.Int(e);
                    break;
                case 0x0101:
                    h0 = t.Int(e);
                    break;
                case 0x8769:
                    exifIfd = (uint)(t.Int(e) ?? 0);
                    break;
                case 0x8825:
                    gpsIfd = (uint)(t.Int(e) ?? 0);
                    break;
            }
        }

        string? original = null, digitized = null;
        int? px = null, py = null;
        if (exifIfd != 0 && seen.Add(exifIfd))
        {
            foreach (TiffEntry e in t.Entries(exifIfd))
            {
                switch (e.Tag)
                {
                    case 0x9003:
                        original = t.Text(e);
                        break;
                    case 0x9004:
                        digitized = t.Text(e);
                        break;
                    case 0xA002:
                        px = t.Int(e);
                        break;
                    case 0xA003:
                        py = t.Int(e);
                        break;
                }
            }
        }

        PixelWidth = px ?? w0;
        PixelHeight = py ?? h0;
        CaptureTime = ParseTime(original) ?? ParseTime(digitized) ?? ParseTime(dateTime);

        if (gpsIfd != 0 && seen.Add(gpsIfd))
        {
            string? latRef = null, lonRef = null;
            double[]? lat = null, lon = null;
            double? alt = null;
            bool below = false;
            foreach (TiffEntry e in t.Entries(gpsIfd))
            {
                switch (e.Tag)
                {
                    case 1:
                        latRef = t.Text(e);
                        break;
                    case 2:
                        lat = t.Rationals(e, 3);
                        break;
                    case 3:
                        lonRef = t.Text(e);
                        break;
                    case 4:
                        lon = t.Rationals(e, 3);
                        break;
                    case 5:
                        below = t.Int(e) == 1;
                        break;
                    case 6:
                        alt = t.Rationals(e, 1)?[0];
                        break;
                }
            }

            if (lat != null && lon != null && latRef is { Length: > 0 } && lonRef is { Length: > 0 })
            {
                double la = lat[0] + (lat[1] / 60) + (lat[2] / 3600);
                double lo = lon[0] + (lon[1] / 60) + (lon[2] / 3600);
                if (latRef[0] is 'S' or 's')
                {
                    la = -la;
                }

                if (lonRef[0] is 'W' or 'w')
                {
                    lo = -lo;
                }

                if (la is >= -90 and <= 90 && lo is >= -180 and <= 180)
                {
                    Gps = new GpsPosition(la, lo, alt.HasValue ? (below ? -alt.Value : alt.Value) : null);
                }
            }
        }
    }

    private static DateTime? ParseTime(string? s) =>
        s != null && DateTime.TryParseExact(s.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? d : null;
}

internal readonly record struct TiffEntry(ushort Tag, ushort Type, uint Count, int ValueOffset, int EntryOffset);

/// <summary>Bounds-checked access to a TIFF structure inside an EXIF blob.</summary>
internal sealed class TiffReader
{
    private static readonly int[] s_typeSize = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4, 0, 0, 8, 8, 8];

    private readonly byte[] _d;

    public TiffReader(ReadOnlySpan<byte> blob)
    {
        int start = blob.Length >= 6 && blob[..6].SequenceEqual("Exif\0\0"u8) ? 6 : 0;
        _d = blob[start..].ToArray();
        if (_d.Length < 8)
        {
            throw new ImageFormatException("EXIF data is too short for a TIFF header.");
        }

        if (_d[0] == 'I' && _d[1] == 'I')
        {
            LittleEndian = true;
        }
        else if (!(_d[0] == 'M' && _d[1] == 'M'))
        {
            throw new ImageFormatException("EXIF data has no TIFF byte-order mark.");
        }

        if (U16(2) != 42)
        {
            throw new ImageFormatException("EXIF TIFF header magic is not 42.");
        }

        FirstIfd = U32(4);
        HasPrefix = start != 0;
    }

    public bool HasPrefix { get; }

    public uint FirstIfd { get; }

    public bool LittleEndian { get; }

    public byte[] Data => _d;

    public ushort U16(int offset)
    {
        if ((uint)offset > (uint)_d.Length || _d.Length - offset < 2)
        {
            throw new ImageFormatException("EXIF offset lies outside the data.");
        }

        return LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(offset)) : BinaryPrimitives.ReadUInt16BigEndian(_d.AsSpan(offset));
    }

    public uint U32(int offset)
    {
        if ((uint)offset > (uint)_d.Length || _d.Length - offset < 4)
        {
            throw new ImageFormatException("EXIF offset lies outside the data.");
        }

        return LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(offset)) : BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(offset));
    }

    /// <summary>Lists the entries of the IFD at <paramref name="offset"/>, validating the table and every out-of-line value range.</summary>
    public List<TiffEntry> Entries(uint offset)
    {
        if (offset > (uint)_d.Length || _d.Length - offset < 2)
        {
            throw new ImageFormatException("EXIF IFD offset lies outside the data.");
        }

        int n = U16((int)offset);
        if (n > (_d.Length - (int)offset - 2) / 12)
        {
            throw new ImageFormatException("EXIF IFD entry table does not fit in the data.");
        }

        var list = new List<TiffEntry>(n);
        for (int i = 0; i < n; i++)
        {
            int e = (int)offset + 2 + (i * 12);
            ushort tag = U16(e);
            ushort type = U16(e + 2);
            uint count = U32(e + 4);
            int size = type is >= 1 and <= 18 ? s_typeSize[type] : 0;
            if (size == 0)
            {
                continue; // Unknown type: skip the entry, as readers are expected to.
            }

            long bytes = (long)count * size;
            int valueOffset;
            if (bytes <= 4)
            {
                valueOffset = e + 8;
            }
            else
            {
                uint vo = U32(e + 8);
                if (vo > _d.Length || bytes > _d.Length - vo)
                {
                    throw new ImageFormatException("EXIF value lies outside the data.");
                }

                valueOffset = (int)vo;
            }

            list.Add(new TiffEntry(tag, type, count, valueOffset, e));
        }

        return list;
    }

    /// <summary>Reads the first value of an integer entry (BYTE, SHORT, LONG, SSHORT, SLONG).</summary>
    public int? Int(TiffEntry e)
    {
        if (e.Count < 1)
        {
            return null;
        }

        return e.Type switch
        {
            1 or 7 => _d[e.ValueOffset],
            3 => U16(e.ValueOffset),
            4 or 13 => (int)Math.Min(U32(e.ValueOffset), int.MaxValue),
            8 => (short)U16(e.ValueOffset),
            9 => (int)U32(e.ValueOffset),
            _ => null,
        };
    }

    public string? Text(TiffEntry e)
    {
        if (e.Type is not (2 or 1 or 7) || e.Count == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> s = _d.AsSpan(e.ValueOffset, (int)Math.Min(e.Count, (uint)(_d.Length - e.ValueOffset)));
        int nul = s.IndexOf((byte)0);
        if (nul >= 0)
        {
            s = s[..nul];
        }

        string text = Encoding.UTF8.GetString(s).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Reads <paramref name="n"/> unsigned rationals as doubles; null if the entry has fewer or a zero denominator.</summary>
    public double[]? Rationals(TiffEntry e, int n)
    {
        if (e.Type != 5 || e.Count < n)
        {
            return null;
        }

        var r = new double[n];
        for (int i = 0; i < n; i++)
        {
            uint num = U32(e.ValueOffset + (i * 8));
            uint den = U32(e.ValueOffset + (i * 8) + 4);
            if (den == 0)
            {
                return null;
            }

            r[i] = (double)num / den;
        }

        return r;
    }
}
