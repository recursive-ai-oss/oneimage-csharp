using System.Buffers.Binary;
using OneImage.Compression;

namespace OneImage.Text.Fonts;

/// <summary>
/// Reads the container around the tables: a plain sfnt (TrueType or OpenType), a TrueType Collection, or a WOFF 1.0 or WOFF 2.0 wrapper.
/// The result is one table map per face; nothing here interprets the tables.
/// </summary>
internal static class FontFile
{
    public const int MaxFacesPerCollection = 256;
    public const int MaxTablesPerFace = 256;
    public const int MaxTableBytes = 128 << 20;
    public const int MaxWoffBytes = 256 << 20;

    private const uint TagWoff = 0x774F4646;
    private const uint TagWoff2 = 0x774F4632;
    private const uint TagOtto = 0x4F54544F;

    /// <summary>
    /// Parses every face in the file. A plain sfnt or collection is handed to OneFont as it is; a WOFF or WOFF2 file is unpacked and its
    /// tables are laid out again as an sfnt, which is what OneFont reads.
    /// </summary>
    public static List<FontFace> Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12)
        {
            throw new FontFormatException("The data is too short to be a font.");
        }

        uint magic = Be.U32(data, 0);
        var faces = new List<FontFace>();
        if (magic == TagWoff)
        {
            faces.Add(Load(BuildSfnt(ReadWoff(data)), 0));
        }
        else if (magic == TagWoff2)
        {
            foreach (Dictionary<uint, ReadOnlyMemory<byte>> tables in Woff2.Parse(data))
            {
                faces.Add(Load(BuildSfnt(tables), 0));
            }
        }
        else
        {
            int count = Translate(() => OneFont.FontFace.GetFaceCount(data));
            for (int i = 0; i < count; i++)
            {
                faces.Add(Load(data, i));
            }
        }

        return faces;
    }

    private static FontFace Load(byte[] sfnt, int index) => new(Translate(() => OneFont.FontFace.Load(sfnt, index)));

    // OneFont's exception type is its own; the package reports the one its callers already catch.
    private static T Translate<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (OneFont.FontFormatException ex)
        {
            throw new FontFormatException(ex.Message);
        }
    }

    // Lays the tables of an unpacked WOFF or WOFF2 face out as a plain sfnt: header, directory sorted by tag, 4-byte aligned tables.
    // Checksums are not filled in; readers of the font do not check them.
    internal static byte[] BuildSfnt(Dictionary<uint, ReadOnlyMemory<byte>> tables)
    {
        var tags = new List<uint>(tables.Keys);
        tags.Sort();
        long size = 12 + (16L * tags.Count);
        foreach (uint tag in tags)
        {
            size += (tables[tag].Length + 3L) & ~3L;
        }

        if (size > int.MaxValue)
        {
            throw new FontFormatException("The font tables are too large.");
        }

        var sfnt = new byte[size];
        Span<byte> o = sfnt;
        BinaryPrimitives.WriteUInt32BigEndian(o, tables.ContainsKey(Be.Tag("CFF ")) ? TagOtto : 0x00010000u);
        BinaryPrimitives.WriteUInt16BigEndian(o[4..], (ushort)tags.Count);
        int at = 12 + (16 * tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            ReadOnlyMemory<byte> t = tables[tags[i]];
            Span<byte> e = o.Slice(12 + (16 * i), 16);
            BinaryPrimitives.WriteUInt32BigEndian(e, tags[i]);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(e[12..], (uint)t.Length);
            t.Span.CopyTo(o[at..]);
            at += (t.Length + 3) & ~3;
        }

        return sfnt;
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

                if (status == InflateStatus.NeedInput && outPos == originalLength)
                {
                    // Every byte of the table is there and the zlib trailer is missing or cut short: accepted. A complete trailer
                    // that does not match has already thrown.
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
