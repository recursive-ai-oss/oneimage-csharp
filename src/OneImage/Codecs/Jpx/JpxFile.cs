using System.Buffers.Binary;

namespace OneImage.Codecs.Jpx;

/// <summary>What the JP2/JPX file format wrapper adds to a codestream (T.800 Annex I).</summary>
internal sealed class JpxFileInfo
{
    public bool IsFile;
    public ReadOnlyMemory<byte> Codestream;
    public JpxColorSpace ColorSpace;
    public ReadOnlyMemory<byte> Icc;
    public bool HasPalette;

    /// <summary>Per palette column: bit depth and signedness.</summary>
    public (int Bits, bool Signed)[] PaletteColumns = [];

    /// <summary>Palette values, indexed [column][entry].</summary>
    public ushort[][] PaletteValues = [];

    public (int Component, int Type, int Column)[] ComponentMap = [];
    public (int Channel, int Type, int Association)[] ChannelDefinitions = [];
}

/// <summary>Reads the box structure of a JP2/JPX file down to the first codestream.</summary>
internal static class JpxFile
{
    private const uint BoxJp2h = 0x6A703268; // jp2h
    private const uint BoxJp2c = 0x6A703263; // jp2c
    private const uint BoxColr = 0x636F6C72; // colr
    private const uint BoxPclr = 0x70636C72; // pclr
    private const uint BoxCmap = 0x636D6170; // cmap
    private const uint BoxCdef = 0x63646566; // cdef

    /// <summary>True when the bytes start like a JP2/JPX file or a raw codestream.</summary>
    public static bool LooksLikeJpx(ReadOnlySpan<byte> d) =>
        (d.Length >= 12 && d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 0x0C && d[4] == 'j' && d[5] == 'P' && d[6] == 0x20 && d[7] == 0x20)
        || (d.Length >= 4 && d[0] == 0xFF && d[1] == 0x4F && d[2] == 0xFF && d[3] == 0x51);

    public static JpxFileInfo Parse(ReadOnlyMemory<byte> data, JpxDecodeOptions options)
    {
        ReadOnlySpan<byte> s = data.Span;
        var info = new JpxFileInfo();
        if (s.Length >= 2 && s[0] == 0xFF && s[1] == 0x4F)
        {
            info.Codestream = data;
            return info;
        }

        if (!LooksLikeJpx(s))
        {
            throw new ImageFormatException("Not a JPEG 2000 file.");
        }

        info.IsFile = true;
        int pos = 0;
        bool haveColr = false;
        while (pos + 8 <= s.Length)
        {
            if (!ReadBox(s, pos, s.Length, out uint type, out int bodyStart, out int boxEnd))
            {
                break;
            }

            if (type == BoxJp2h)
            {
                ParseHeaderBox(data, bodyStart, boxEnd, info, ref haveColr, options);
            }
            else if (type == BoxJp2c)
            {
                info.Codestream = data.Slice(bodyStart, boxEnd - bodyStart);
                break;
            }

            pos = boxEnd;
        }

        if (info.Codestream.IsEmpty)
        {
            throw new ImageFormatException("JPEG 2000 file contains no codestream box.");
        }

        return info;
    }

    private static bool ReadBox(ReadOnlySpan<byte> s, int pos, int limit, out uint type, out int bodyStart, out int boxEnd)
    {
        type = 0;
        bodyStart = 0;
        boxEnd = 0;
        if (limit - pos < 8)
        {
            return false;
        }

        uint lbox = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(pos));
        type = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(pos + 4));
        long length;
        int header = 8;
        if (lbox == 1)
        {
            if (limit - pos < 16)
            {
                return false;
            }

            ulong x = BinaryPrimitives.ReadUInt64BigEndian(s.Slice(pos + 8));
            length = x > int.MaxValue ? int.MaxValue : (long)x;
            header = 16;
        }
        else if (lbox == 0)
        {
            length = limit - pos;
        }
        else
        {
            length = lbox;
        }

        if (length < header)
        {
            throw new ImageFormatException("Invalid JPEG 2000 box length.");
        }

        // A box may claim more than the file has (a truncated file): clamp rather than fail, the codestream parser decides.
        long end = Math.Min(pos + length, limit);
        bodyStart = pos + header;
        boxEnd = (int)end;
        return bodyStart <= boxEnd;
    }

    private static void ParseHeaderBox(ReadOnlyMemory<byte> data, int start, int end, JpxFileInfo info, ref bool haveColr, JpxDecodeOptions options)
    {
        ReadOnlySpan<byte> s = data.Span;
        int pos = start;
        while (pos + 8 <= end)
        {
            if (!ReadBox(s, pos, end, out uint type, out int bodyStart, out int boxEnd))
            {
                break;
            }

            ReadOnlySpan<byte> body = s.Slice(bodyStart, boxEnd - bodyStart);
            switch (type)
            {
                case BoxColr when !haveColr:
                    haveColr = ParseColr(data, bodyStart, body, info, options);
                    break;
                case BoxPclr:
                    ParsePclr(body, info);
                    break;
                case BoxCmap:
                    ParseCmap(body, info);
                    break;
                case BoxCdef:
                    ParseCdef(body, info);
                    break;
            }

            pos = boxEnd;
        }
    }

    private static bool ParseColr(ReadOnlyMemory<byte> data, int bodyStart, ReadOnlySpan<byte> body, JpxFileInfo info, JpxDecodeOptions options)
    {
        if (body.Length < 3)
        {
            return false;
        }

        int meth = body[0];
        if (meth == 1 && body.Length >= 7)
        {
            uint cs = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(3));
            info.ColorSpace = cs switch
            {
                16 => JpxColorSpace.Srgb,
                17 => JpxColorSpace.Gray,
                18 => JpxColorSpace.Sycc,
                12 => JpxColorSpace.Cmyk,
                14 => JpxColorSpace.CieLab,
                20 => JpxColorSpace.ESrgb,
                22 => JpxColorSpace.RommRgb,
                _ => JpxColorSpace.Unspecified,
            };
            return info.ColorSpace != JpxColorSpace.Unspecified;
        }

        if ((meth == 2 || meth == 3) && body.Length > 3 + 128)
        {
            if (body.Length - 3 > options.Limits.MaxMetadataBytes)
            {
                throw new ImageLimitException("The embedded ICC profile is larger than the metadata limit.");
            }

            info.ColorSpace = JpxColorSpace.Icc;
            info.Icc = data.Slice(bodyStart + 3, body.Length - 3);
            return true;
        }

        return false;
    }

    private static void ParsePclr(ReadOnlySpan<byte> body, JpxFileInfo info)
    {
        var c = new JpxCursor(body);
        int entries = c.U16();
        int columns = c.U8();
        if (entries < 1 || entries > 1024 || columns < 1)
        {
            throw new ImageFormatException("Invalid palette box.");
        }

        var cols = new (int Bits, bool Signed)[columns];
        for (int i = 0; i < columns; i++)
        {
            int b = c.U8();
            cols[i] = ((b & 0x7F) + 1, (b & 0x80) != 0);
            if (cols[i].Bits > 16)
            {
                throw new ImageFormatException("Palette entries deeper than 16 bits are not supported.");
            }
        }

        var values = new ushort[columns][];
        for (int i = 0; i < columns; i++)
        {
            values[i] = new ushort[entries];
        }

        for (int e = 0; e < entries; e++)
        {
            for (int i = 0; i < columns; i++)
            {
                int bytes = (cols[i].Bits + 7) / 8;
                int v = 0;
                for (int k = 0; k < bytes; k++)
                {
                    v = (v << 8) | c.U8();
                }

                int mask = (1 << cols[i].Bits) - 1;
                v &= mask;
                if (cols[i].Signed)
                {
                    // Signed palette entries are offset to unsigned like signed components.
                    v = (v + (1 << (cols[i].Bits - 1))) & mask;
                }

                values[i][e] = (ushort)v;
            }
        }

        info.HasPalette = true;
        info.PaletteColumns = cols;
        info.PaletteValues = values;
    }

    private static void ParseCmap(ReadOnlySpan<byte> body, JpxFileInfo info)
    {
        if (body.Length % 4 != 0 || body.Length == 0 || body.Length / 4 > 1024)
        {
            throw new ImageFormatException("Invalid component mapping box.");
        }

        var map = new (int, int, int)[body.Length / 4];
        for (int i = 0; i < map.Length; i++)
        {
            map[i] = (BinaryPrimitives.ReadUInt16BigEndian(body.Slice(i * 4)), body[(i * 4) + 2], body[(i * 4) + 3]);
        }

        info.ComponentMap = map;
    }

    private static void ParseCdef(ReadOnlySpan<byte> body, JpxFileInfo info)
    {
        var c = new JpxCursor(body);
        int n = c.U16();
        if (n < 1 || n > 1024 || body.Length < 2 + (n * 6))
        {
            throw new ImageFormatException("Invalid channel definition box.");
        }

        var defs = new (int, int, int)[n];
        for (int i = 0; i < n; i++)
        {
            defs[i] = (c.U16(), c.U16(), c.U16());
        }

        info.ChannelDefinitions = defs;
    }
}
