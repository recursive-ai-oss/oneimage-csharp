using OneImage.Compression;

namespace OneImage.Text.Fonts;

/// <summary>
/// Reads the container around the tables: a plain sfnt (TrueType or OpenType), a TrueType Collection or a WOFF 1.0 wrapper.
/// The result is one table map per face; nothing here interprets the tables.
/// </summary>
internal static class FontFile
{
    public const int MaxFacesPerCollection = 256;
    public const int MaxTablesPerFace = 256;
    public const int MaxTableBytes = 128 << 20;
    public const int MaxWoffBytes = 256 << 20;

    private const uint TagTtcf = 0x74746366;
    private const uint TagWoff = 0x774F4646;
    private const uint TagWoff2 = 0x774F4632;
    private const uint TagOtto = 0x4F54544F;
    private const uint TagTrue = 0x74727565;
    private const uint TagTyp1 = 0x74797031;

    /// <summary>Parses every face in the file.</summary>
    public static List<FontFace> Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12)
        {
            throw new FontFormatException("The data is too short to be a font.");
        }

        uint magic = Be.U32(data, 0);
        var faces = new List<FontFace>();
        if (magic == TagTtcf)
        {
            // TTC header: tag, version, numFonts, offsets[numFonts].
            uint count = Be.U32(data, 8);
            if (count == 0 || count > MaxFacesPerCollection || 12L + (4L * count) > data.Length)
            {
                throw new FontFormatException("The font collection header is invalid.");
            }

            for (int i = 0; i < count; i++)
            {
                uint off = Be.U32(data, 12 + (4 * i));
                faces.Add(FontFace.Create(ReadSfntDirectory(data, off)));
            }
        }
        else if (magic == TagWoff)
        {
            faces.Add(FontFace.Create(ReadWoff(data)));
        }
        else if (magic == TagWoff2)
        {
            throw new FontFormatException("WOFF2 fonts are not supported.");
        }
        else
        {
            faces.Add(FontFace.Create(ReadSfntDirectory(data, 0)));
        }

        return faces;
    }

    private static Dictionary<uint, ReadOnlyMemory<byte>> ReadSfntDirectory(byte[] data, uint offset)
    {
        if (offset > data.Length - 12L)
        {
            throw Be.Truncated();
        }

        int o = (int)offset;
        uint version = Be.U32(data, o);
        if (version != 0x00010000 && version != TagOtto && version != TagTrue && version != TagTyp1)
        {
            throw new FontFormatException("Not an sfnt font: unknown version tag.");
        }

        int numTables = Be.U16(data, o + 4);
        if (numTables == 0 || numTables > MaxTablesPerFace || o + 12L + (16L * numTables) > data.Length)
        {
            throw new FontFormatException("The table directory is invalid.");
        }

        var tables = new Dictionary<uint, ReadOnlyMemory<byte>>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            int e = o + 12 + (16 * i);
            uint tag = Be.U32(data, e);
            uint tOff = Be.U32(data, e + 8);
            uint tLen = Be.U32(data, e + 12);
            if (tOff > data.Length || tLen > data.Length - tOff)
            {
                // A table running past the end of the file: skip it rather than fail the whole font; a required one is reported later.
                continue;
            }

            tables.TryAdd(tag, new ReadOnlyMemory<byte>(data, (int)tOff, (int)tLen));
        }

        return tables;
    }

    // WOFF 1.0 (W3C): 44-byte header, then 20-byte table records; each table is stored raw or zlib-compressed.
    private static Dictionary<uint, ReadOnlyMemory<byte>> ReadWoff(byte[] data)
    {
        uint length = Be.U32(data, 8);
        int numTables = Be.U16(data, 12);
        if (length > data.Length || numTables == 0 || numTables > MaxTablesPerFace || 44L + (20L * numTables) > data.Length)
        {
            throw new FontFormatException("The WOFF header is invalid.");
        }

        var tables = new Dictionary<uint, ReadOnlyMemory<byte>>(numTables);
        long total = 0;
        for (int i = 0; i < numTables; i++)
        {
            int e = 44 + (20 * i);
            uint tag = Be.U32(data, e);
            uint off = Be.U32(data, e + 4);
            uint comp = Be.U32(data, e + 8);
            uint orig = Be.U32(data, e + 12);
            if (off > data.Length || comp > data.Length - off || comp > orig)
            {
                throw new FontFormatException("A WOFF table lies outside the file.");
            }

            total += orig;
            if (orig > MaxTableBytes || total > MaxWoffBytes)
            {
                throw new FontFormatException("The WOFF tables are too large.");
            }

            if (comp == orig)
            {
                tables.TryAdd(tag, new ReadOnlyMemory<byte>(data, (int)off, (int)orig));
                continue;
            }

            // Refuse to allocate the declared size unless the compressed bytes could plausibly produce it (deflate tops out near 1032:1).
            if (orig > (1100L * comp) + 65536)
            {
                throw new FontFormatException("A WOFF table declares an implausible expansion.");
            }

            tables.TryAdd(tag, Inflate(data.AsSpan((int)off, (int)comp), (int)orig));
        }

        return tables;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> input, int originalLength)
    {
        var output = new byte[originalLength];
        using var inflater = new Inflater(zlib: true, maxOutputBytes: originalLength);
        int inPos = 0;
        int outPos = 0;
        try
        {
            while (true)
            {
                InflateStatus status = inflater.Inflate(input[inPos..], output.AsSpan(outPos), out int used, out int written);
                inPos += used;
                outPos += written;
                if (status == InflateStatus.Done)
                {
                    break;
                }

                if (status == InflateStatus.NeedInput || (status == InflateStatus.NeedOutput && outPos >= originalLength))
                {
                    throw new FontFormatException("A WOFF table is truncated or longer than declared.");
                }
            }
        }
        catch (ImageFormatException ex) when (ex is not FontFormatException)
        {
            throw new FontFormatException("A WOFF table does not decompress: " + ex.Message, ex);
        }
        catch (ImageLimitException ex)
        {
            throw new FontFormatException("A WOFF table is longer than declared.", ex);
        }

        if (outPos != originalLength)
        {
            throw new FontFormatException("A WOFF table decompressed to the wrong length.");
        }

        return output;
    }
}
