using System.Buffers.Binary;

namespace OneImage.Codecs.Jpx;

/// <summary>One tile-part: where its packet data lies in the codestream and which of its marker segments carried packed headers.</summary>
internal sealed class JpxTilePart
{
    public int DataOffset;
    public int DataLength;
    public int PptStart;
    public int PptEnd;

    /// <summary>The slice of the PPM data holding this tile-part's packet headers, or (-1, 0).</summary>
    public int PpmOffset = -1;

    public int PpmLength;
}

/// <summary>A tile: the marker segments of its first tile-part header and all its tile-parts.</summary>
internal sealed class JpxTileEntry
{
    public JpxTileEntry(int components)
    {
        Set = new JpxMarkerSet(components);
    }

    public JpxMarkerSet Set { get; }

    public List<JpxTilePart> Parts { get; } = [];
}

/// <summary>The main header of a codestream and an index of its tile-parts (T.800 Annex A).</summary>
internal sealed class JpxCodestream
{
    private JpxCodestream(ReadOnlyMemory<byte> data, JpxSiz siz, JpxMarkerSet main)
    {
        Data = data;
        Siz = siz;
        Main = main;
        Tiles = new JpxTileEntry?[siz.TileCount];
    }

    public ReadOnlyMemory<byte> Data { get; }

    public JpxSiz Siz { get; }

    public JpxMarkerSet Main { get; }

    public JpxTileEntry?[] Tiles { get; }

    /// <summary>The PPM marker data, reassembled in order, or empty.</summary>
    public byte[] PpmData { get; private set; } = [];

    /// <summary>True when the codestream ended inside a tile-part or lost its end marker's tile data.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Parses the main header and, unless <paramref name="headerOnly"/>, indexes the tile-parts.</summary>
    public static JpxCodestream Parse(ReadOnlyMemory<byte> data, JpxDecodeOptions options, bool headerOnly)
    {
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length < 4 || s[0] != 0xFF || s[1] != 0x4F)
        {
            throw new ImageFormatException("Not a JPEG 2000 codestream (no SOC marker).");
        }

        int pos = 2;
        if (s.Length < pos + 4 || s[pos] != 0xFF || s[pos + 1] != 0x51)
        {
            throw new ImageFormatException("JPEG 2000 codestream does not start with a SIZ marker segment.");
        }

        int lsiz = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(pos + 2));
        if (lsiz < 41 || pos + 2 + lsiz > s.Length)
        {
            throw new ImageFormatException("JPEG 2000 SIZ marker segment is truncated.");
        }

        JpxSiz siz = JpxHeaderParser.ParseSiz(s.Slice(pos + 4, lsiz - 2), options);
        if (lsiz != 38 + (3 * siz.Csiz))
        {
            throw new ImageFormatException("JPEG 2000 SIZ marker segment has the wrong length.");
        }

        pos += 2 + lsiz;
        var main = new JpxMarkerSet(siz.Csiz);
        var cs = new JpxCodestream(data, siz, main);

        // Main header: marker segments up to the first SOT.
        while (true)
        {
            if (pos + 2 > s.Length)
            {
                throw new ImageFormatException("JPEG 2000 main header is truncated.");
            }

            if (s[pos] != 0xFF)
            {
                throw new ImageFormatException("Invalid marker in the JPEG 2000 main header.");
            }

            int marker = 0xFF00 | s[pos + 1];
            if (marker == JpxHeaderParser.Sot)
            {
                break;
            }

            if (marker == JpxHeaderParser.Eoc || marker == JpxHeaderParser.Sod)
            {
                throw new ImageFormatException("JPEG 2000 codestream has no tile data.");
            }

            if (marker is >= 0xFF30 and <= 0xFF3F)
            {
                // Reserved markers 0xFF30..0xFF3F have no length field (A.1.2).
                pos += 2;
                continue;
            }

            if (pos + 4 > s.Length)
            {
                throw new ImageFormatException("JPEG 2000 main header is truncated.");
            }

            int len = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(pos + 2));
            if (len < 2 || pos + 2 + len > s.Length)
            {
                throw new ImageFormatException("JPEG 2000 marker segment overruns the data.");
            }

            JpxHeaderParser.ParseSegment(marker, s.Slice(pos + 4, len - 2), pos + 4, main, siz);
            pos += 2 + len;
        }

        if (!main.HasCod || main.Qcd is null)
        {
            throw new ImageFormatException("JPEG 2000 main header lacks a COD or QCD marker segment.");
        }

        if (headerOnly)
        {
            return cs;
        }

        cs.BuildPpm(s, options);
        cs.IndexTileParts(s, pos);
        return cs;
    }

    private void BuildPpm(ReadOnlySpan<byte> s, JpxDecodeOptions options)
    {
        if (Main.Ppm.Count == 0)
        {
            return;
        }

        long total = 0;
        foreach ((int _, int _, int length) in Main.Ppm)
        {
            total += length;
        }

        if (total > s.Length)
        {
            throw new ImageFormatException("PPM marker segments are larger than the file.");
        }

        _ = options;
        var parts = Main.Ppm.OrderBy(p => p.Z).ToList();
        var buf = new byte[total];
        int at = 0;
        foreach ((int _, int offset, int length) in parts)
        {
            s.Slice(offset, length).CopyTo(buf.AsSpan(at));
            at += length;
        }

        PpmData = buf;
    }

    private void IndexTileParts(ReadOnlySpan<byte> s, int pos)
    {
        int ppmCursor = 0;
        int partSeq = 0;
        while (pos + 12 <= s.Length && s[pos] == 0xFF && s[pos + 1] == 0x90)
        {
            int sotPos = pos;
            int lsot = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(pos + 2));
            int tile = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(pos + 4));
            uint psot = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(pos + 6));
            if (lsot != 10 || tile >= Siz.TileCount)
            {
                throw new ImageFormatException("Invalid SOT marker segment.");
            }

            pos += 12;
            JpxTileEntry entry = Tiles[tile] ??= new JpxTileEntry(Siz.Csiz);
            var part = new JpxTilePart { PptStart = entry.Set.Ppt.Count };

            // Tile-part header: marker segments until SOD.
            while (true)
            {
                if (pos + 2 > s.Length)
                {
                    Truncated = true;
                    return;
                }

                if (s[pos] != 0xFF)
                {
                    throw new ImageFormatException("Invalid marker in a JPEG 2000 tile-part header.");
                }

                int marker = 0xFF00 | s[pos + 1];
                if (marker == JpxHeaderParser.Sod)
                {
                    pos += 2;
                    break;
                }

                if (marker is >= 0xFF30 and <= 0xFF3F)
                {
                    pos += 2;
                    continue;
                }

                if (pos + 4 > s.Length)
                {
                    Truncated = true;
                    return;
                }

                int len = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(pos + 2));
                if (len < 2 || pos + 2 + len > s.Length)
                {
                    Truncated = true;
                    return;
                }

                JpxHeaderParser.ParseSegment(marker, s.Slice(pos + 4, len - 2), pos + 4, entry.Set, Siz);
                pos += 2 + len;
            }

            part.PptEnd = entry.Set.Ppt.Count;
            long end;
            if (psot == 0)
            {
                end = s.Length;
                if (end - 2 >= pos && s[(int)end - 2] == 0xFF && s[(int)end - 1] == 0xD9)
                {
                    end -= 2;
                }
            }
            else
            {
                end = sotPos + (long)psot;
                if (end < pos)
                {
                    throw new ImageFormatException("Tile-part length is shorter than its header.");
                }

                if (end > s.Length)
                {
                    end = s.Length;
                    Truncated = true;
                }
            }

            part.DataOffset = pos;
            part.DataLength = (int)(end - pos);
            if (PpmData.Length > 0)
            {
                if (ppmCursor + 4 <= PpmData.Length)
                {
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(PpmData.AsSpan(ppmCursor));
                    ppmCursor += 4;
                    if (n > PpmData.Length - ppmCursor)
                    {
                        throw new ImageFormatException("PPM packet header run is longer than the PPM data.");
                    }

                    part.PpmOffset = ppmCursor;
                    part.PpmLength = (int)n;
                    ppmCursor += (int)n;
                }
                else
                {
                    part.PpmOffset = 0;
                    part.PpmLength = 0;
                }
            }

            entry.Parts.Add(part);
            partSeq++;
            pos = (int)end;
            if (partSeq > 1_000_000)
            {
                throw new ImageLimitException("Too many tile-parts.");
            }
        }
    }
}
