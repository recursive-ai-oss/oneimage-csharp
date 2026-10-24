using System.Buffers.Binary;

namespace OneImage.Codecs.Jpeg;

/// <summary>One image component of a frame (T.81 B.2.2).</summary>
internal sealed class JpegComponent
{
    public int Index;
    public int Id;
    public int H;
    public int V;
    public int Tq;

    /// <summary>Blocks per row, padded to a whole number of MCUs.</summary>
    public int BlocksPerLine;

    /// <summary>Block rows, padded to a whole number of MCUs.</summary>
    public int BlockRows;

    /// <summary>Blocks per row actually covering the component (used by non-interleaved scans).</summary>
    public int WidthInBlocks;

    /// <summary>Block rows actually covering the component.</summary>
    public int HeightInBlocks;

    /// <summary>Quantization factors in natural order, latched when the component's first scan starts.</summary>
    public int[]? Quant;

    // Per-scan selections and predictors.
    public int DcTable;
    public int AcTable;
    public int DcPred;
}

/// <summary>The frame header and the application markers that affect decoding.</summary>
internal sealed class JpegFrame
{
    public int Marker;
    public bool Progressive;
    public bool Arithmetic;
    public int Precision;
    public int Width;
    public int Height;
    public JpegComponent[] Components = [];
    public int Hmax;
    public int Vmax;
    public int McusX;
    public int McusY;
}

/// <summary>Everything the header walk learned.</summary>
internal sealed class JpegHeaders
{
    public JpegFrame? Frame;
    public bool Jfif;
    public int AdobeTransform = -1;
    public ReadOnlyMemory<byte> Icc;
    public ReadOnlyMemory<byte> Exif;
    public ReadOnlyMemory<byte> Xmp;
    public ReadOnlyMemory<byte> Iptc;
}

/// <summary>The scan header (T.81 B.2.3).</summary>
internal sealed class JpegScan
{
    public JpegComponent[] Components = [];
    public int Ss;
    public int Se;
    public int Ah;
    public int Al;
}

/// <summary>Quantization, Huffman and arithmetic-conditioning tables defined so far.</summary>
internal sealed class JpegTables
{
    public readonly int[]?[] Quant = new int[4][];
    public readonly JpegHuffmanTable?[] Dc = new JpegHuffmanTable[4];
    public readonly JpegHuffmanTable?[] Ac = new JpegHuffmanTable[4];

    // Arithmetic conditioning (T.81 B.2.4.3), defaults L=0, U=1, Kx=5.
    public readonly byte[] DcL = new byte[16];
    public readonly byte[] DcU = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];
    public readonly byte[] AcKx = [5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5];

    public int RestartInterval;
}

/// <summary>Parses JPEG marker segments. Every length is validated before use.</summary>
internal static class JpegParser
{
    /// <summary>
    /// Walks markers from the start of the file up to the first SOS (or the end). Tables found on the way are stored.
    /// </summary>
    /// <param name="reader">Positioned at the first byte.</param>
    /// <param name="limits">Limits checked as soon as the frame header is known.</param>
    /// <param name="tables">Receives tables; may be shared with the scan loop.</param>
    /// <param name="collectMetadata">Whether to keep ICC/EXIF/XMP/IPTC blobs.</param>
    /// <returns>The headers; <see cref="JpegHeaders.Frame"/> is set. The SOS marker has been consumed.</returns>
    public static JpegHeaders ReadHeaders(JpegReader reader, ImageLimits limits, JpegTables tables, bool collectMetadata)
    {
        if (reader.TryReadByte() != 0xFF || reader.TryReadByte() != JpegConstants.Soi)
        {
            throw new ImageFormatException("Not a JPEG file: missing SOI marker.");
        }

        var h = new JpegHeaders();
        var meta = collectMetadata ? new MetadataCollector(limits.MaxMetadataBytes) : null;
        while (true)
        {
            int m = reader.NextMarker();
            if (m < 0)
            {
                throw new ImageFormatException("JPEG ended before the image data.");
            }

            if (m == JpegConstants.Sos)
            {
                if (h.Frame is null)
                {
                    throw new ImageFormatException("JPEG scan before frame header.");
                }

                meta?.Finish(h);
                return h;
            }

            if (m == JpegConstants.Eoi)
            {
                throw new ImageFormatException("JPEG has no image data.");
            }

            if (m is JpegConstants.Soi or 0x01 || (m is >= 0xD0 and <= 0xD7))
            {
                continue;
            }

            ParseSegment(reader, m, h, tables, limits, meta);
        }
    }

    /// <summary>Handles one non-scan segment whose marker was just read. Used between scans as well.</summary>
    public static void ParseSegment(JpegReader reader, int marker, JpegHeaders h, JpegTables tables, ImageLimits limits, MetadataCollector? meta)
    {
        int len = reader.ReadSegmentLength();
        switch (marker)
        {
            case JpegConstants.Sof0:
            case JpegConstants.Sof1:
            case JpegConstants.Sof2:
            case JpegConstants.Sof9:
            case JpegConstants.Sof10:
                if (h.Frame is not null)
                {
                    throw new ImageFormatException("Multiple JPEG frame headers.");
                }

                h.Frame = ParseSof(reader, marker, len, limits);
                break;
            case 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC8 or 0xCB or 0xCD or 0xCE or 0xCF:
                throw new ImageFormatException("Unsupported JPEG process (lossless, hierarchical or differential).");
            case JpegConstants.Dht:
                ParseDht(reader, len, tables);
                break;
            case JpegConstants.Dqt:
                ParseDqt(reader, len, tables);
                break;
            case JpegConstants.Dri:
                if (len != 2)
                {
                    throw new ImageFormatException("Invalid JPEG restart interval segment.");
                }

                tables.RestartInterval = reader.ReadUInt16();
                break;
            case JpegConstants.Dac:
                ParseDac(reader, len, tables);
                break;
            case >= JpegConstants.App0 and <= 0xEF:
                ParseApp(reader, marker, len, h, meta);
                break;
            default:
                reader.Skip(len);
                break;
        }
    }

    private static JpegFrame ParseSof(JpegReader r, int marker, int len, ImageLimits limits)
    {
        if (len < 6)
        {
            throw new ImageFormatException("Invalid JPEG frame header.");
        }

        var f = new JpegFrame
        {
            Marker = marker,
            Progressive = marker is JpegConstants.Sof2 or JpegConstants.Sof10,
            Arithmetic = marker is JpegConstants.Sof9 or JpegConstants.Sof10,
            Precision = r.ReadByte(),
            Height = r.ReadUInt16(),
            Width = r.ReadUInt16(),
        };
        int nf = r.ReadByte();
        if (len != 6 + (3 * nf))
        {
            throw new ImageFormatException("Invalid JPEG frame header length.");
        }

        if (f.Precision is not (8 or 12))
        {
            throw new ImageFormatException($"Unsupported JPEG sample precision {f.Precision}.");
        }

        if (f.Height == 0)
        {
            throw new ImageFormatException("JPEG frames with a deferred height (DNL) are not supported.");
        }

        if (f.Width == 0)
        {
            throw new ImageFormatException("Invalid JPEG width.");
        }

        if (nf is not (1 or 3 or 4))
        {
            throw new ImageFormatException($"Unsupported JPEG component count {nf}.");
        }

        // Check before anything is sized from the header values.
        limits.CheckFrame(f.Width, f.Height);
        f.Components = new JpegComponent[nf];
        for (int i = 0; i < nf; i++)
        {
            int id = r.ReadByte();
            int hv = r.ReadByte();
            int tq = r.ReadByte();
            int hs = hv >> 4;
            int vs = hv & 15;
            if (hs is < 1 or > 4 || vs is < 1 or > 4 || tq > 3)
            {
                throw new ImageFormatException("Invalid JPEG component description.");
            }

            for (int j = 0; j < i; j++)
            {
                if (f.Components[j].Id == id)
                {
                    throw new ImageFormatException("Duplicate JPEG component id.");
                }
            }

            f.Components[i] = new JpegComponent { Index = i, Id = id, H = nf == 1 ? 1 : hs, V = nf == 1 ? 1 : vs, Tq = tq };
        }

        foreach (JpegComponent c in f.Components)
        {
            f.Hmax = Math.Max(f.Hmax, c.H);
            f.Vmax = Math.Max(f.Vmax, c.V);
        }

        foreach (JpegComponent c in f.Components)
        {
            if (f.Hmax % c.H != 0 || f.Vmax % c.V != 0)
            {
                throw new ImageFormatException("Unsupported JPEG sampling factors.");
            }
        }

        f.McusX = (f.Width + (8 * f.Hmax) - 1) / (8 * f.Hmax);
        f.McusY = (f.Height + (8 * f.Vmax) - 1) / (8 * f.Vmax);
        foreach (JpegComponent c in f.Components)
        {
            c.BlocksPerLine = f.McusX * c.H;
            c.BlockRows = f.McusY * c.V;
            long dw = ((long)f.Width * c.H) + f.Hmax - 1;
            long dh = ((long)f.Height * c.V) + f.Vmax - 1;
            c.WidthInBlocks = (int)(((dw / f.Hmax) + 7) / 8);
            c.HeightInBlocks = (int)(((dh / f.Vmax) + 7) / 8);
        }

        return f;
    }

    private static void ParseDqt(JpegReader r, int len, JpegTables tables)
    {
        while (len > 0)
        {
            int pt = r.ReadByte();
            len--;
            int precision = pt >> 4;
            int id = pt & 15;
            if (precision > 1 || id > 3)
            {
                throw new ImageFormatException("Invalid JPEG quantization table header.");
            }

            int need = precision == 0 ? 64 : 128;
            if (len < need)
            {
                throw new ImageFormatException("Truncated JPEG quantization table.");
            }

            int[] q = new int[64];
            for (int i = 0; i < 64; i++)
            {
                q[JpegConstants.Zigzag[i]] = precision == 0 ? r.ReadByte() : r.ReadUInt16();
            }

            tables.Quant[id] = q;
            len -= need;
        }
    }

    private static void ParseDht(JpegReader r, int len, JpegTables tables)
    {
        // One table per call: a method that loops and also uses stackalloc is compiled with full optimization at once, which
        // costs far more memory on the first load than the quick tier does.
        while (len > 0)
        {
            len = ParseOneDht(r, len, tables);
        }
    }

    // Reads one Huffman table and returns the bytes left in the segment.
    private static int ParseOneDht(JpegReader r, int len, JpegTables tables)
    {
        Span<byte> counts = stackalloc byte[16];
        Span<byte> values = stackalloc byte[256];
        if (len < 17)
        {
            throw new ImageFormatException("Truncated JPEG Huffman table.");
        }

        int tc = r.ReadByte();
        r.ReadExactly(counts);
        len -= 17;
        int total = 0;
        foreach (byte c in counts)
        {
            total += c;
        }

        if (total > 256 || total > len)
        {
            throw new ImageFormatException("Invalid JPEG Huffman table size.");
        }

        values.Clear();
        r.ReadExactly(values[..total]);
        len -= total;
        int cls = tc >> 4;
        int id = tc & 15;
        if (cls > 1 || id > 3)
        {
            throw new ImageFormatException("Invalid JPEG Huffman table id.");
        }

        var t = new JpegHuffmanTable(counts, values);
        if (cls == 0)
        {
            tables.Dc[id] = t;
        }
        else
        {
            tables.Ac[id] = t;
        }

        return len;
    }

    private static void ParseDac(JpegReader r, int len, JpegTables tables)
    {
        if ((len & 1) != 0)
        {
            throw new ImageFormatException("Invalid JPEG arithmetic conditioning segment.");
        }

        for (; len > 0; len -= 2)
        {
            int tc = r.ReadByte();
            int v = r.ReadByte();
            int id = tc & 15;
            int cls = tc >> 4;
            if (id > 3 || cls > 1)
            {
                throw new ImageFormatException("Invalid JPEG arithmetic conditioning table.");
            }

            if (cls == 0)
            {
                int l = v & 15;
                int u = v >> 4;
                if (l > u)
                {
                    throw new ImageFormatException("Invalid JPEG arithmetic DC conditioning.");
                }

                tables.DcL[id] = (byte)l;
                tables.DcU[id] = (byte)u;
            }
            else
            {
                if (v is < 1 or > 63)
                {
                    throw new ImageFormatException("Invalid JPEG arithmetic AC conditioning.");
                }

                tables.AcKx[id] = (byte)v;
            }
        }
    }

    private static void ParseApp(JpegReader r, int marker, int len, JpegHeaders h, MetadataCollector? meta)
    {
        bool wanted = marker is JpegConstants.App0 or JpegConstants.App14 || (meta is not null && marker is JpegConstants.App1 or JpegConstants.App2 or JpegConstants.App13);
        if (!wanted || len < 5)
        {
            r.Skip(len);
            return;
        }

        if (marker is JpegConstants.App0 or JpegConstants.App14)
        {
            // Only the small identifying prefix is needed.
            Span<byte> head = stackalloc byte[Math.Min(len, 14)];
            r.ReadExactly(head);
            r.Skip(len - head.Length);
            if (marker == JpegConstants.App0 && head.Length >= 5 && head[..5].SequenceEqual("JFIF\0"u8))
            {
                h.Jfif = true;
            }
            else if (marker == JpegConstants.App14 && head.Length >= 12 && head[..5].SequenceEqual("Adobe"u8))
            {
                h.AdobeTransform = head[11];
            }

            return;
        }

        byte[] body = new byte[len];
        meta!.Charge(len);
        r.ReadExactly(body);
        meta.Add(marker, body);
    }

    /// <summary>Gathers metadata segments and checks their total size against the limit before keeping them.</summary>
    internal sealed class MetadataCollector
    {
        private readonly long _limit;
        private long _total;
        private byte[]? _exif;
        private byte[]? _xmp;
        private byte[]? _iptc;
        private readonly SortedDictionary<int, byte[]> _icc = [];
        private int _iccCount;

        public MetadataCollector(long limit) => _limit = limit;

        public void Charge(int bytes)
        {
            _total += bytes;
            if (_total > _limit)
            {
                throw new ImageLimitException($"JPEG metadata exceeds the limit of {_limit} bytes.");
            }
        }

        public void Add(int marker, byte[] body)
        {
            ReadOnlySpan<byte> b = body;
            if (marker == JpegConstants.App1)
            {
                if (_exif is null && b.Length > 6 && b[..6].SequenceEqual("Exif\0\0"u8))
                {
                    _exif = b[6..].ToArray();
                }
                else if (_xmp is null && b.StartsWith("http://ns.adobe.com/xap/1.0/\0"u8))
                {
                    _xmp = b["http://ns.adobe.com/xap/1.0/\0"u8.Length..].ToArray();
                }
            }
            else if (marker == JpegConstants.App2)
            {
                if (b.Length > 14 && b[..12].SequenceEqual("ICC_PROFILE\0"u8))
                {
                    int seq = b[12];
                    int count = b[13];
                    if (seq >= 1 && count >= 1 && (_iccCount == 0 || _iccCount == count) && !_icc.ContainsKey(seq))
                    {
                        _iccCount = count;
                        _icc[seq] = b[14..].ToArray();
                    }
                }
            }
            else if (marker == JpegConstants.App13 && _iptc is null && b.StartsWith("Photoshop 3.0\0"u8))
            {
                _iptc = FindIptc(b["Photoshop 3.0\0"u8.Length..]);
            }
        }

        public void Finish(JpegHeaders h)
        {
            if (_exif is not null)
            {
                h.Exif = _exif;
            }

            if (_xmp is not null)
            {
                h.Xmp = _xmp;
            }

            if (_iptc is not null)
            {
                h.Iptc = _iptc;
            }

            if (_iccCount > 0 && _icc.Count == _iccCount)
            {
                bool ok = true;
                int total = 0;
                int expect = 1;
                foreach (KeyValuePair<int, byte[]> kv in _icc)
                {
                    ok &= kv.Key == expect++;
                    total += kv.Value.Length;
                }

                if (ok)
                {
                    byte[] icc = new byte[total];
                    int o = 0;
                    foreach (byte[] part in _icc.Values)
                    {
                        part.CopyTo(icc, o);
                        o += part.Length;
                    }

                    h.Icc = icc;
                }
            }
        }

        /// <summary>Unwraps the IPTC-NAA record (resource 0x0404) from the Photoshop resource blocks (8BIM).</summary>
        private static byte[]? FindIptc(ReadOnlySpan<byte> s)
        {
            while (s.Length >= 12 && s[..4].SequenceEqual("8BIM"u8))
            {
                int id = BinaryPrimitives.ReadUInt16BigEndian(s[4..]);
                int nameLen = s[6];
                int nameBytes = (1 + nameLen + 1) & ~1;
                if (s.Length < 6 + nameBytes + 4)
                {
                    return null;
                }

                uint size = BinaryPrimitives.ReadUInt32BigEndian(s[(6 + nameBytes)..]);
                s = s[(6 + nameBytes + 4)..];
                if (size > s.Length)
                {
                    return null;
                }

                if (id == 0x0404)
                {
                    return s[..(int)size].ToArray();
                }

                int adv = (int)Math.Min((long)s.Length, (size + 1) & ~1u);
                s = s[adv..];
            }

            return null;
        }
    }

    /// <summary>Reads a scan header after the SOS marker.</summary>
    public static JpegScan ParseSos(JpegReader r, JpegFrame f)
    {
        int len = r.ReadSegmentLength();
        int ns = r.ReadByte();
        if (ns is < 1 or > 4 || len != 4 + (2 * ns))
        {
            throw new ImageFormatException("Invalid JPEG scan header.");
        }

        var s = new JpegScan { Components = new JpegComponent[ns] };
        int blocks = 0;
        for (int i = 0; i < ns; i++)
        {
            int cs = r.ReadByte();
            int tt = r.ReadByte();
            JpegComponent? comp = null;
            foreach (JpegComponent c in f.Components)
            {
                if (c.Id == cs)
                {
                    comp = c;
                }
            }

            if (comp is null)
            {
                throw new ImageFormatException("JPEG scan refers to an unknown component.");
            }

            for (int j = 0; j < i; j++)
            {
                if (s.Components[j] == comp)
                {
                    throw new ImageFormatException("JPEG scan repeats a component.");
                }
            }

            if ((tt >> 4) > 3 || (tt & 15) > 3)
            {
                throw new ImageFormatException("Invalid JPEG table selector.");
            }

            comp.DcTable = tt >> 4;
            comp.AcTable = tt & 15;
            s.Components[i] = comp;
            blocks += comp.H * comp.V;
        }

        s.Ss = r.ReadByte();
        s.Se = r.ReadByte();
        int a = r.ReadByte();
        s.Ah = a >> 4;
        s.Al = a & 15;
        if (ns > 1 && blocks > 10)
        {
            throw new ImageFormatException("JPEG scan has too many blocks per MCU.");
        }

        if (f.Progressive)
        {
            bool dc = s.Ss == 0;
            if ((dc && s.Se != 0) || (!dc && (ns != 1 || s.Se < s.Ss || s.Se > 63)) || s.Al > 13 || (s.Ah != 0 && s.Ah != s.Al + 1))
            {
                throw new ImageFormatException("Invalid progressive JPEG scan parameters.");
            }
        }
        else
        {
            s.Ss = 0;
            s.Se = 63;
            s.Ah = 0;
            s.Al = 0;
        }

        return s;
    }
}
