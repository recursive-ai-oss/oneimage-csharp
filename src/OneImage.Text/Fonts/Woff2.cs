using OneImage.Text.Compression;

namespace OneImage.Text.Fonts;

/// <summary>
/// WOFF 2.0 (W3C): a Brotli-compressed table block with optional glyf/loca and hmtx transforms. The result is one table map
/// per face, like the other containers. Every size taken from the file is checked before it allocates anything: the declared
/// table sizes must add up to what the compressed block can plausibly hold, and the Brotli decoder refuses output past that.
/// </summary>
internal static class Woff2
{
    /// <summary>Most output bytes per compressed byte accepted for the table block (Brotli reaches far higher only on degenerate data).</summary>
    public const int MaxExpansionRatio = 2048;

    private const int MaxDirectoryEntries = 2048;
    private const uint FlagsTransformMask = 0xC0;

    private static readonly uint[] KnownTags = BuildKnownTags();

    private static uint[] BuildKnownTags()
    {
        string[] names =
        [
            "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
            "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
            "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
            "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
        ];
        var tags = new uint[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            tags[i] = Be.Tag(names[i]);
        }

        return tags;
    }

    private struct Entry
    {
        public uint Tag;
        public int Version;
        public int Length;      // bytes this table occupies in the decompressed block
        public int Offset;      // start in the decompressed block
        public bool Transformed;
    }

    /// <summary>Parses every face of a WOFF2 file.</summary>
    public static List<FontFace> Parse(byte[] data)
    {
        // Header: signature, flavor, length, numTables, reserved, totalSfntSize, totalCompressedSize, versions, meta and private blocks.
        if (data.Length < 48)
        {
            throw Be.Truncated();
        }

        uint flavor = Be.U32(data, 4);
        uint length = Be.U32(data, 8);
        int numTables = Be.U16(data, 12);
        uint compressedSize = Be.U32(data, 20);
        if (length > data.Length || numTables == 0 || numTables > MaxDirectoryEntries)
        {
            throw new FontFormatException("The WOFF2 header is invalid.");
        }

        var r = new Cursor(data.AsSpan(0, (int)length), 48);
        var entries = new Entry[numTables];
        long total = 0;
        uint glyfTag = Be.Tag("glyf");
        uint locaTag = Be.Tag("loca");
        uint hmtxTag = Be.Tag("hmtx");
        for (int i = 0; i < numTables; i++)
        {
            int flags = r.U8();
            int known = flags & 0x3F;
            uint tag = known == 63 ? r.U32() : KnownTags[known];
            int version = (flags & (int)FlagsTransformMask) >> 6;
            long orig = r.Base128();
            long stored = orig;
            bool transformed;
            if (tag == glyfTag || tag == locaTag)
            {
                // Version 0 is the transform, 3 the null transform; 1 and 2 are undefined.
                if (version == 1 || version == 2)
                {
                    throw new FontFormatException("A WOFF2 glyf or loca table uses an unknown transform.");
                }

                transformed = version == 0;
            }
            else if (tag == hmtxTag)
            {
                if (version > 1)
                {
                    throw new FontFormatException("A WOFF2 hmtx table uses an unknown transform.");
                }

                transformed = version == 1;
            }
            else
            {
                if (version != 0)
                {
                    throw new FontFormatException("A WOFF2 table uses an unknown transform.");
                }

                transformed = false;
            }

            if (transformed)
            {
                stored = r.Base128();
                if (tag == locaTag && stored != 0)
                {
                    throw new FontFormatException("A transformed WOFF2 loca table must be empty.");
                }
            }

            if (stored > FontFile.MaxTableBytes)
            {
                throw new FontFormatException("A WOFF2 table is too large.");
            }

            total += stored;
            entries[i] = new Entry { Tag = tag, Version = version, Length = (int)stored, Transformed = transformed };
        }

        if (total > FontFile.MaxWoffBytes)
        {
            throw new FontFormatException("The WOFF2 tables are too large.");
        }

        // Collection directory (only when the flavor is 'ttcf'): which directory entries each face uses.
        List<int[]>? faceIndices = null;
        if (flavor == 0x74746366)
        {
            r.U32();
            int fonts = r.Base255();
            if (fonts == 0 || fonts > FontFile.MaxFacesPerCollection)
            {
                throw new FontFormatException("The WOFF2 collection header is invalid.");
            }

            faceIndices = new List<int[]>(fonts);
            for (int f = 0; f < fonts; f++)
            {
                int n = r.Base255();
                r.U32();
                if (n == 0 || n > FontFile.MaxTablesPerFace)
                {
                    throw new FontFormatException("A WOFF2 collection face has an invalid table count.");
                }

                var idx = new int[n];
                for (int k = 0; k < n; k++)
                {
                    idx[k] = r.Base255();
                    if (idx[k] >= numTables)
                    {
                        throw new FontFormatException("A WOFF2 collection face refers to a missing table.");
                    }
                }

                faceIndices.Add(idx);
            }
        }

        int start = r.Position;
        if (compressedSize == 0 || compressedSize > data.Length - start)
        {
            throw new FontFormatException("The WOFF2 compressed block lies outside the file.");
        }

        byte[] block;
        int blockLength;
        try
        {
            block = Brotli.Decode(data.AsSpan(start, (int)compressedSize), (int)total, MaxExpansionRatio, (int)total, out blockLength);
        }
        catch (ImageLimitException ex)
        {
            throw new FontFormatException("The WOFF2 table block is too large: " + ex.Message, ex);
        }

        if (blockLength != total)
        {
            throw new FontFormatException("The WOFF2 table block is shorter than its directory says.");
        }

        int pos = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i].Offset = pos;
            pos += entries[i].Length;
        }

        var faces = new List<FontFace>();
        var glyfCache = new Dictionary<int, GlyfResult>();
        if (faceIndices is null)
        {
            var all = new int[numTables];
            for (int i = 0; i < all.Length; i++)
            {
                all[i] = i;
            }

            faces.Add(FontFace.Create(BuildFace(all, entries, block, glyfCache)));
        }
        else
        {
            foreach (int[] idx in faceIndices)
            {
                faces.Add(FontFace.Create(BuildFace(idx, entries, block, glyfCache)));
            }
        }

        return faces;
    }

    private static Dictionary<uint, ReadOnlyMemory<byte>> BuildFace(int[] indices, Entry[] entries, byte[] block, Dictionary<int, GlyfResult> glyfCache)
    {
        uint glyfTag = Be.Tag("glyf");
        uint locaTag = Be.Tag("loca");
        uint hmtxTag = Be.Tag("hmtx");
        uint headTag = Be.Tag("head");
        uint hheaTag = Be.Tag("hhea");

        var tables = new Dictionary<uint, ReadOnlyMemory<byte>>(indices.Length);
        int glyfIndex = -1;
        int locaIndex = -1;
        int hmtxIndex = -1;
        foreach (int i in indices)
        {
            uint tag = entries[i].Tag;
            if (tag == glyfTag)
            {
                glyfIndex = i;
            }
            else if (tag == locaTag)
            {
                locaIndex = i;
            }
            else if (tag == hmtxTag && entries[i].Transformed)
            {
                hmtxIndex = i;
            }
            else
            {
                tables.TryAdd(tag, new ReadOnlyMemory<byte>(block, entries[i].Offset, entries[i].Length));
            }
        }

        GlyfResult? glyf = null;
        if (glyfIndex >= 0)
        {
            Entry g = entries[glyfIndex];
            if (g.Transformed)
            {
                // The transformed loca must directly follow its glyf table in the directory (W3C WOFF 2.0 section 5.5).
                if (locaIndex != glyfIndex + 1 || !entries[locaIndex].Transformed)
                {
                    throw new FontFormatException("A transformed WOFF2 glyf table needs its transformed loca table right after it.");
                }

                if (!glyfCache.TryGetValue(glyfIndex, out glyf))
                {
                    glyf = ReconstructGlyf(new ReadOnlySpan<byte>(block, g.Offset, g.Length));
                    glyfCache[glyfIndex] = glyf;
                }

                tables.TryAdd(glyfTag, glyf.Glyf);
                tables.TryAdd(locaTag, glyf.Loca);

                // The loca format the glyf header chose has to agree with head.indexToLocFormat.
                if (tables.TryGetValue(headTag, out ReadOnlyMemory<byte> head) && head.Length >= 54 && Be.U16(head.Span, 50) != glyf.IndexFormat)
                {
                    byte[] fixedHead = head.ToArray();
                    fixedHead[50] = 0;
                    fixedHead[51] = (byte)glyf.IndexFormat;
                    tables[headTag] = fixedHead;
                }
            }
            else
            {
                if (locaIndex < 0 || entries[locaIndex].Transformed)
                {
                    throw new FontFormatException("A WOFF2 glyf table and its loca table must both be transformed or both plain.");
                }

                tables.TryAdd(glyfTag, new ReadOnlyMemory<byte>(block, g.Offset, g.Length));
                tables.TryAdd(locaTag, new ReadOnlyMemory<byte>(block, entries[locaIndex].Offset, entries[locaIndex].Length));
            }
        }
        else if (locaIndex >= 0)
        {
            tables.TryAdd(locaTag, new ReadOnlyMemory<byte>(block, entries[locaIndex].Offset, entries[locaIndex].Length));
        }

        if (hmtxIndex >= 0)
        {
            if (glyf is null)
            {
                throw new FontFormatException("A transformed WOFF2 hmtx table needs a transformed glyf table.");
            }

            if (!tables.TryGetValue(hheaTag, out ReadOnlyMemory<byte> hhea))
            {
                throw new FontFormatException("A transformed WOFF2 hmtx table needs an hhea table.");
            }

            int numHMetrics = Be.U16(hhea.Span, 34);
            Entry h = entries[hmtxIndex];
            tables.TryAdd(hmtxTag, ReconstructHmtx(new ReadOnlySpan<byte>(block, h.Offset, h.Length), numHMetrics, glyf));
        }

        return tables;
    }

    // ---- hmtx (section 5.4) ----------------------------------------------------------------------------------------

    private static byte[] ReconstructHmtx(ReadOnlySpan<byte> t, int numHMetrics, GlyfResult glyf)
    {
        int flags = Be.U8(t, 0);
        bool noLsb = (flags & 1) != 0;
        bool noLeft = (flags & 2) != 0;
        if ((flags & 3) == 0 || (flags & 0xFC) != 0)
        {
            throw new FontFormatException("The WOFF2 hmtx transform flags are invalid.");
        }

        int n = glyf.NumGlyphs;
        if (numHMetrics > n || numHMetrics == 0)
        {
            throw new FontFormatException("The hhea table counts more metrics than there are glyphs.");
        }

        long size = (4L * numHMetrics) + (2L * (n - numHMetrics));
        var o = new byte[size];
        int p = 1;
        int advAt = p;
        p += 2 * numHMetrics;
        int lsbAt = -1;
        if (!noLsb)
        {
            lsbAt = p;
            p += 2 * numHMetrics;
        }

        int leftAt = -1;
        if (!noLeft)
        {
            leftAt = p;
            p += 2 * (n - numHMetrics);
        }

        if (p > t.Length)
        {
            throw Be.Truncated();
        }

        for (int i = 0; i < n; i++)
        {
            short lsb;
            if (i < numHMetrics)
            {
                lsb = noLsb ? glyf.XMin(i) : (short)Be.U16(t, lsbAt + (2 * i));
                int adv = Be.U16(t, advAt + (2 * i));
                o[4 * i] = (byte)(adv >> 8);
                o[(4 * i) + 1] = (byte)adv;
                o[(4 * i) + 2] = (byte)(lsb >> 8);
                o[(4 * i) + 3] = (byte)lsb;
            }
            else
            {
                lsb = noLeft ? glyf.XMin(i) : (short)Be.U16(t, leftAt + (2 * (i - numHMetrics)));
                int at = (4 * numHMetrics) + (2 * (i - numHMetrics));
                o[at] = (byte)(lsb >> 8);
                o[at + 1] = (byte)lsb;
            }
        }

        return o;
    }

    // ---- glyf and loca (sections 5.1 to 5.3) -------------------------------------------------------------------------

    private sealed class GlyfResult
    {
        public required byte[] Glyf;
        public required byte[] Loca;
        public required int[] Offsets;
        public required int NumGlyphs;
        public required int IndexFormat;

        public short XMin(int glyph)
        {
            int a = Offsets[glyph];
            return a == Offsets[glyph + 1] ? (short)0 : Be.I16(Glyf, a + 2);
        }
    }

    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _d;
        private int _p;

        public Cursor(ReadOnlySpan<byte> d, int p)
        {
            _d = d;
            _p = p;
        }

        public readonly int Position => _p;

        public byte U8() => Be.U8(_d, _p++);

        public int U16()
        {
            int v = Be.U16(_d, _p);
            _p += 2;
            return v;
        }

        public uint U32()
        {
            uint v = Be.U32(_d, _p);
            _p += 4;
            return v;
        }

        public ReadOnlySpan<byte> Take(int n)
        {
            ReadOnlySpan<byte> s = Be.Slice(_d, _p, n);
            _p += n;
            return s;
        }

        // UIntBase128 (WOFF 2.0 section 6.1.1): big-endian 7-bit groups, at most five bytes, no leading zero group.
        public uint Base128()
        {
            uint acc = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = U8();
                if (i == 0 && b == 0x80)
                {
                    throw new FontFormatException("A WOFF2 integer has a leading zero group.");
                }

                if ((acc & 0xFE000000) != 0)
                {
                    throw new FontFormatException("A WOFF2 integer overflows.");
                }

                acc = (acc << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    return acc;
                }
            }

            throw new FontFormatException("A WOFF2 integer is too long.");
        }

        // 255UInt16 (section 6.1.2).
        public int Base255()
        {
            int code = U8();
            return code switch
            {
                253 => U16(),
                254 => 506 + U8(),
                255 => 253 + U8(),
                _ => code,
            };
        }
    }

    private sealed class Sink
    {
        private byte[] _b;
        private int _n;

        public Sink(int capacity) => _b = new byte[Math.Max(capacity, 64)];

        public int Length => _n;

        public byte[] Buffer => _b;

        private void Ensure(int extra)
        {
            if (_n > FontFile.MaxTableBytes - extra)
            {
                throw new FontFormatException("The reconstructed glyf table is too large.");
            }

            if (_n + extra > _b.Length)
            {
                Array.Resize(ref _b, (int)Math.Min(Math.Max(2L * _b.Length, (long)_n + extra), FontFile.MaxTableBytes));
            }
        }

        public void U8(int v)
        {
            Ensure(1);
            _b[_n++] = (byte)v;
        }

        public void U16(int v)
        {
            Ensure(2);
            _b[_n++] = (byte)(v >> 8);
            _b[_n++] = (byte)v;
        }

        public void Bytes(ReadOnlySpan<byte> s)
        {
            Ensure(s.Length);
            s.CopyTo(_b.AsSpan(_n));
            _n += s.Length;
        }

        public void PadTo4()
        {
            int pad = (-_n) & 3;
            Ensure(pad);
            _n += pad;      // the buffer is zero-filled past what was written
        }
    }

    private static GlyfResult ReconstructGlyf(ReadOnlySpan<byte> t)
    {
        var h = new Cursor(t, 0);
        h.U16();
        int optionFlags = h.U16();
        int numGlyphs = h.U16();
        int indexFormat = h.U16();
        if (indexFormat > 1)
        {
            throw new FontFormatException("The WOFF2 loca format is invalid.");
        }

        long nContourSize = h.U32();
        long nPointsSize = h.U32();
        long flagSize = h.U32();
        long glyphSize = h.U32();
        long compositeSize = h.U32();
        long bboxSize = h.U32();
        long instrSize = h.U32();
        int pos = h.Position;
        long bitmapLen = 4L * ((numGlyphs + 31) / 32);
        long overlapLen = (optionFlags & 1) != 0 ? (numGlyphs + 7) / 8 : 0;
        long sum = nContourSize + nPointsSize + flagSize + glyphSize + compositeSize + bboxSize + instrSize + overlapLen;
        if (sum > t.Length - pos || bboxSize < bitmapLen || nContourSize < 2L * numGlyphs)
        {
            throw new FontFormatException("The WOFF2 glyf streams do not fit the table.");
        }

        var nContour = new Cursor(t.Slice(pos, (int)nContourSize), 0);
        pos += (int)nContourSize;
        var nPoints = new Cursor(t.Slice(pos, (int)nPointsSize), 0);
        pos += (int)nPointsSize;
        var flagS = new Cursor(t.Slice(pos, (int)flagSize), 0);
        pos += (int)flagSize;
        var glyphS = new Cursor(t.Slice(pos, (int)glyphSize), 0);
        pos += (int)glyphSize;
        var composite = new Cursor(t.Slice(pos, (int)compositeSize), 0);
        pos += (int)compositeSize;
        ReadOnlySpan<byte> bboxBitmap = t.Slice(pos, (int)bitmapLen);
        var bboxS = new Cursor(t.Slice(pos + (int)bitmapLen, (int)(bboxSize - bitmapLen)), 0);
        pos += (int)bboxSize;
        var instr = new Cursor(t.Slice(pos, (int)instrSize), 0);
        pos += (int)instrSize;
        ReadOnlySpan<byte> overlap = t.Slice(pos, (int)overlapLen);

        // Plain bytes written per glyph are bounded by a small multiple of the transformed bytes; the sink also enforces the table cap.
        var sink = new Sink((int)Math.Min((t.Length * 3L) + 1024, FontFile.MaxTableBytes));
        var offsets = new int[numGlyphs + 1];
        var endPts = new List<int>();
        var xs = new List<int>();
        var ys = new List<int>();
        var flags = new List<byte>();

        for (int g = 0; g < numGlyphs; g++)
        {
            offsets[g] = sink.Length;
            int nc = (short)nContour.U16();
            bool hasBbox = (bboxBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;
            if (nc == 0)
            {
                if (hasBbox)
                {
                    throw new FontFormatException("An empty WOFF2 glyph carries a bounding box.");
                }

                continue;
            }

            if (nc > 0)
            {
                endPts.Clear();
                xs.Clear();
                ys.Clear();
                flags.Clear();
                int totalPoints = 0;
                for (int c = 0; c < nc; c++)
                {
                    totalPoints += nPoints.Base255();
                    if (totalPoints == 0 || totalPoints > 65536)
                    {
                        throw new FontFormatException("A WOFF2 glyph has an invalid point count.");
                    }

                    endPts.Add(totalPoints - 1);
                }

                ReadOnlySpan<byte> fl = flagS.Take(totalPoints);
                int x = 0;
                int y = 0;
                int minX = int.MaxValue;
                int minY = int.MaxValue;
                int maxX = int.MinValue;
                int maxY = int.MinValue;
                for (int p = 0; p < totalPoints; p++)
                {
                    int f = fl[p];
                    (int dx, int dy) = DecodeTriplet(f & 0x7F, ref glyphS);
                    if (dx is < short.MinValue or > short.MaxValue || dy is < short.MinValue or > short.MaxValue)
                    {
                        throw new FontFormatException("A WOFF2 coordinate delta is out of range.");
                    }

                    x += dx;
                    y += dy;
                    if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue)
                    {
                        throw new FontFormatException("A WOFF2 coordinate is out of range.");
                    }

                    xs.Add(dx);
                    ys.Add(dy);
                    flags.Add((byte)((f & 0x80) == 0 ? 1 : 0));
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }

                int instrLen = glyphS.Base255();
                ReadOnlySpan<byte> code = instr.Take(instrLen);
                if (overlapLen != 0 && (overlap[g >> 3] & (0x80 >> (g & 7))) != 0)
                {
                    flags[0] |= 0x40;
                }

                sink.U16(nc);
                if (hasBbox)
                {
                    sink.U16(bboxS.U16());
                    sink.U16(bboxS.U16());
                    sink.U16(bboxS.U16());
                    sink.U16(bboxS.U16());
                }
                else
                {
                    sink.U16(minX);
                    sink.U16(minY);
                    sink.U16(maxX);
                    sink.U16(maxY);
                }

                foreach (int e in endPts)
                {
                    sink.U16(e);
                }

                sink.U16(instrLen);
                sink.Bytes(code);
                foreach (byte f in flags)
                {
                    sink.U8(f);
                }

                // Every coordinate as a signed 16-bit delta: flag bits 1 and 2 (short vectors) and 4 and 5 (same/positive) stay clear.
                foreach (int dx in xs)
                {
                    sink.U16(dx);
                }

                foreach (int dy in ys)
                {
                    sink.U16(dy);
                }
            }
            else if (nc == -1)
            {
                if (!hasBbox)
                {
                    throw new FontFormatException("A WOFF2 composite glyph has no bounding box.");
                }

                sink.U16(-1);
                sink.U16(bboxS.U16());
                sink.U16(bboxS.U16());
                sink.U16(bboxS.U16());
                sink.U16(bboxS.U16());
                bool haveInstructions = false;
                bool more;
                int components = 0;
                do
                {
                    if (++components > 4096)
                    {
                        throw new FontFormatException("A WOFF2 composite glyph has too many components.");
                    }

                    int cf = composite.U16();
                    sink.U16(cf);
                    int argBytes = 2 + ((cf & 1) != 0 ? 4 : 2);
                    if ((cf & 0x08) != 0)
                    {
                        argBytes += 2;
                    }
                    else if ((cf & 0x40) != 0)
                    {
                        argBytes += 4;
                    }
                    else if ((cf & 0x80) != 0)
                    {
                        argBytes += 8;
                    }

                    sink.Bytes(composite.Take(argBytes));
                    haveInstructions |= (cf & 0x100) != 0;
                    more = (cf & 0x20) != 0;
                }
                while (more);

                if (haveInstructions)
                {
                    int instrLen = glyphS.Base255();
                    sink.U16(instrLen);
                    sink.Bytes(instr.Take(instrLen));
                }
            }
            else
            {
                throw new FontFormatException("A WOFF2 glyph has an invalid contour count.");
            }

            sink.PadTo4();
        }

        offsets[numGlyphs] = sink.Length;

        var loca = new byte[(numGlyphs + 1) * (indexFormat == 0 ? 2 : 4)];
        for (int i = 0; i <= numGlyphs; i++)
        {
            if (indexFormat == 0)
            {
                int v = offsets[i] >> 1;
                if (v > ushort.MaxValue)
                {
                    throw new FontFormatException("The reconstructed glyf table is too large for a short loca table.");
                }

                loca[2 * i] = (byte)(v >> 8);
                loca[(2 * i) + 1] = (byte)v;
            }
            else
            {
                int v = offsets[i];
                loca[4 * i] = (byte)(v >> 24);
                loca[(4 * i) + 1] = (byte)(v >> 16);
                loca[(4 * i) + 2] = (byte)(v >> 8);
                loca[(4 * i) + 3] = (byte)v;
            }
        }

        var glyf = new byte[sink.Length];
        Buffer.BlockCopy(sink.Buffer, 0, glyf, 0, sink.Length);
        return new GlyfResult { Glyf = glyf, Loca = loca, Offsets = offsets, NumGlyphs = numGlyphs, IndexFormat = indexFormat };
    }

    // Triplet encoding (section 5.2): the flag (without the on-curve bit) selects how many bytes follow and how they split into dx and dy.
    private static (int Dx, int Dy) DecodeTriplet(int f, ref Cursor s)
    {
        int dx;
        int dy;
        if (f < 10)
        {
            int b0 = s.U8();
            dx = 0;
            dy = Sign(f, ((f & 14) << 7) + b0);
        }
        else if (f < 20)
        {
            int b0 = s.U8();
            dx = Sign(f, (((f - 10) & 14) << 7) + b0);
            dy = 0;
        }
        else if (f < 84)
        {
            int b = f - 20;
            int b0 = s.U8();
            dx = Sign(f, 1 + (b & 0x30) + (b0 >> 4));
            dy = Sign(f >> 1, 1 + ((b & 0x0C) << 2) + (b0 & 15));
        }
        else if (f < 120)
        {
            int b = f - 84;
            int b0 = s.U8();
            int b1 = s.U8();
            dx = Sign(f, 1 + ((b / 12) << 8) + b0);
            dy = Sign(f >> 1, 1 + (((b % 12) >> 2) << 8) + b1);
        }
        else if (f < 124)
        {
            int b0 = s.U8();
            int b1 = s.U8();
            int b2 = s.U8();
            dx = Sign(f, (b0 << 4) + (b1 >> 4));
            dy = Sign(f >> 1, ((b1 & 15) << 8) + b2);
        }
        else
        {
            int b0 = s.U8();
            int b1 = s.U8();
            int b2 = s.U8();
            int b3 = s.U8();
            dx = Sign(f, (b0 << 8) + b1);
            dy = Sign(f >> 1, (b2 << 8) + b3);
        }

        return (dx, dy);
    }

    private static int Sign(int flag, int v) => (flag & 1) != 0 ? v : -v;
}
