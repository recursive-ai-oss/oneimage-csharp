namespace OneImage.Codecs.Jbig2;

/// <summary>A parsed segment header (T.88 7.2).</summary>
internal readonly struct Jbig2SegmentHeader
{
    public Jbig2SegmentHeader(uint number, int type, uint page, uint[] referred, int dataStart, long dataLength)
    {
        Number = number;
        Type = type;
        Page = page;
        Referred = referred;
        DataStart = dataStart;
        DataLength = dataLength;
    }

    /// <summary>Gets the segment number.</summary>
    public uint Number { get; }

    /// <summary>Gets the segment type (0 to 63).</summary>
    public int Type { get; }

    /// <summary>Gets the page the segment belongs to, or 0.</summary>
    public uint Page { get; }

    /// <summary>Gets the numbers of the segments this one refers to.</summary>
    public uint[] Referred { get; }

    /// <summary>Gets the offset of the first byte after the header.</summary>
    public int DataStart { get; }

    /// <summary>Gets the length of the segment data, or -1 when the header leaves it open (an immediate generic region).</summary>
    public long DataLength { get; }
}

internal sealed class Jbig2SymbolDictionaryResult
{
    public required Jbig2Bitmap[] Symbols { get; init; }

    public byte[]? GenericContexts { get; init; }

    public byte[]? RefinementContexts { get; init; }
}

internal sealed class Jbig2AuxiliaryRegion
{
    public required Jbig2Bitmap Bitmap { get; init; }

    public long X { get; init; }

    public long Y { get; init; }

    public Jbig2CombinationOperator Operator { get; init; }
}

/// <summary>
/// The control decoding procedure (T.88 clause 7) and page make-up (clause 8): reads segments in order, decodes the
/// dictionaries and regions that belong to the chosen page and draws the immediate regions into the page buffer.
/// </summary>
internal sealed class Jbig2Session
{
    private readonly Jbig2Work _work;
    private readonly Dictionary<uint, object> _segments = [];
    private int _targetPage;
    private int _segmentCount;
    private Jbig2Bitmap? _page;
    private int _pageWidth;
    private bool _heightUnknown;
    private int _pageDefault;
    private uint _xRes;
    private uint _yRes;
    private long _knownHeight = -1;
    private bool _pageDone;

    public Jbig2Session(Jbig2Work work, int targetPage)
    {
        _work = work;
        _targetPage = targetPage;
    }

    /// <summary>Parses a segment header at <paramref name="pos"/>; returns false when the bytes do not form one.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> buf, int pos, out Jbig2SegmentHeader header)
    {
        header = default;
        long remaining = buf.Length - (long)pos;
        if (remaining < 6)
        {
            return false;
        }

        uint number = ReadU32(buf, pos);
        int flags = buf[pos + 4];
        int type = flags & 0x3F;
        bool longPage = (flags & 0x40) != 0;
        int countByte = buf[pos + 5];
        int tag = countByte >> 5;
        long count;
        long p;
        if (tag <= 4)
        {
            count = tag;
            p = pos + 6L;
        }
        else if (tag == 7)
        {
            if (remaining < 9)
            {
                return false;
            }

            count = ReadU32(buf, pos + 5) & 0x1FFFFFFF;
            p = pos + 9L + ((count + 8) / 8);
        }
        else
        {
            return false;
        }

        int refSize = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
        long need = (count * refSize) + (longPage ? 4 : 1) + 4;
        if (p + need > buf.Length)
        {
            return false;
        }

        var referred = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int at = (int)p + (i * refSize);
            referred[i] = refSize == 1 ? buf[at] : refSize == 2 ? (uint)((buf[at] << 8) | buf[at + 1]) : ReadU32(buf, at);
        }

        p += count * refSize;
        uint page;
        if (longPage)
        {
            page = ReadU32(buf, (int)p);
            p += 4;
        }
        else
        {
            page = buf[(int)p];
            p += 1;
        }

        uint length = ReadU32(buf, (int)p);
        p += 4;
        header = new Jbig2SegmentHeader(number, type, page, referred, (int)p, length == 0xFFFFFFFF ? -1 : length);
        return true;
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int pos) => (uint)((b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]);

    /// <summary>Processes segments stored one after another (header, then data), the sequential and embedded organisations.</summary>
    public void ProcessSequential(byte[] buf, int pos)
    {
        while (pos < buf.Length && !_pageDone)
        {
            if (!TryParseHeader(buf, pos, out Jbig2SegmentHeader h))
            {
                throw new ImageFormatException("A JBIG2 segment header is malformed or truncated.");
            }

            long length = h.DataLength;
            if (length < 0)
            {
                length = FindOpenEnd(buf, h);
            }

            long end = h.DataStart + length;
            if (end > buf.Length)
            {
                throw new ImageFormatException("A JBIG2 segment extends past the end of the data.");
            }

            if (!Handle(h, buf, h.DataStart, (int)end))
            {
                return;
            }

            pos = (int)end;
        }
    }

    /// <summary>Processes the random-access organisation: all segment headers first, then the data in the same order.</summary>
    public void ProcessRandomAccess(byte[] buf, int pos)
    {
        var headers = new List<Jbig2SegmentHeader>();
        while (true)
        {
            if (!TryParseHeader(buf, pos, out Jbig2SegmentHeader h))
            {
                throw new ImageFormatException("A JBIG2 segment header is malformed or truncated.");
            }

            if (headers.Count >= _work.Jbig2.MaxSegments)
            {
                throw new ImageLimitException("The JBIG2 file has more segments than the limit allows.");
            }

            headers.Add(h);
            pos = h.DataStart;
            if (h.Type == 51)
            {
                break;
            }
        }

        foreach (Jbig2SegmentHeader h in headers)
        {
            long length = h.DataLength;
            if (length < 0)
            {
                length = FindOpenEnd(buf, pos, h);
            }

            long end = pos + length;
            if (end > buf.Length)
            {
                throw new ImageFormatException("A JBIG2 segment extends past the end of the data.");
            }

            if (_pageDone || !Handle(h, buf, pos, (int)end))
            {
                return;
            }

            pos = (int)end;
        }
    }

    private static long FindOpenEnd(byte[] buf, Jbig2SegmentHeader h) => FindOpenEnd(buf, h.DataStart, h);

    // T.88 7.2.7: an immediate generic region of unknown length ends with a two-byte marker and a four-byte row count.
    private static long FindOpenEnd(byte[] buf, int dataStart, Jbig2SegmentHeader h)
    {
        if (h.Type is not (38 or 39) || dataStart + 18 > buf.Length)
        {
            throw new ImageFormatException("A JBIG2 segment of unknown length is not an immediate generic region.");
        }

        bool mmr = (buf[dataStart + 17] & 1) != 0;
        byte m0 = mmr ? (byte)0x00 : (byte)0xFF;
        byte m1 = mmr ? (byte)0x00 : (byte)0xAC;
        for (int i = dataStart + 18; i + 1 < buf.Length; i++)
        {
            if (buf[i] == m0 && buf[i + 1] == m1)
            {
                if (i + 6 > buf.Length)
                {
                    break;
                }

                return i + 6 - dataStart;
            }
        }

        throw new ImageFormatException("The end of a JBIG2 generic region of unknown length was not found.");
    }

    /// <summary>Returns the page, once every segment has been processed.</summary>
    public Jbig2Page Finish()
    {
        if (_page is null)
        {
            throw new ImageFormatException("The JBIG2 data contains no page.");
        }

        Jbig2Bitmap page = _page;
        if (_heightUnknown)
        {
            long h = Math.Max(_knownHeight, 0);
            if (h != page.Height)
            {
                page = page.Resized((int)h, _pageDefault);
            }
        }

        if (page.Width > 0 && page.Stride > 0)
        {
            int pad = (page.Stride * 8) - page.Width;
            if (pad > 0)
            {
                byte keep = (byte)(0xFF << pad);
                for (int y = 0; y < page.Height; y++)
                {
                    page.Data[(y * page.Stride) + page.Stride - 1] &= keep;
                }
            }
        }

        return new Jbig2Page(page.Width, page.Height, page.Data, _xRes, _yRes);
    }

    // Returns false when decoding should stop (the page is complete).
    private bool Handle(Jbig2SegmentHeader h, byte[] buf, int start, int end)
    {
        _work.Cancellation.ThrowIfCancellationRequested();
        if (++_segmentCount > _work.Jbig2.MaxSegments)
        {
            throw new ImageLimitException("The JBIG2 data has more segments than the limit allows.");
        }

        if (h.Referred.Length > _work.Jbig2.MaxReferredSegments)
        {
            throw new ImageLimitException("A JBIG2 segment refers to more segments than the limit allows.");
        }

        if (h.Type == 48)
        {
            if (_targetPage == 0)
            {
                _targetPage = (int)Math.Min(h.Page, int.MaxValue);
            }

            if (h.Page == _targetPage && _page is null)
            {
                PageInformation(buf, start, end);
            }

            return true;
        }

        bool mine = h.Page == 0 || _targetPage == 0 || h.Page == _targetPage;
        if (!mine)
        {
            return true;
        }

        switch (h.Type)
        {
            case 0:
                _segments[h.Number] = SymbolDictionary(h, buf, start, end);
                break;
            case 4 or 6 or 7:
                Region(h, buf, start, end, TextRegion);
                break;
            case 16:
                _segments[h.Number] = PatternDictionary(buf, start, end);
                break;
            case 20 or 22 or 23:
                Region(h, buf, start, end, HalftoneRegion);
                break;
            case 36 or 38 or 39:
                Region(h, buf, start, end, GenericRegion);
                break;
            case 40 or 42 or 43:
                Region(h, buf, start, end, RefinementRegion);
                break;
            case 49:
                if (_page is not null)
                {
                    _pageDone = true;
                    return false;
                }

                break;
            case 50:
                EndOfStripe(buf, start, end);
                break;
            case 51:
                return false;
            case 53:
                _segments[h.Number] = Jbig2HuffmanTable.Parse(buf, start, end, _work.Jbig2.MaxHuffmanTableLines);
                break;
            default:
                // Profiles (52), extensions (62) and reserved types carry nothing a bitmap depends on.
                break;
        }

        return true;
    }

    private static uint U32(byte[] b, ref int pos, int end)
    {
        if (pos + 4 > end)
        {
            throw new ImageFormatException("A JBIG2 segment is shorter than its header fields.");
        }

        uint v = ReadU32(b, pos);
        pos += 4;
        return v;
    }

    private static int U8(byte[] b, ref int pos, int end)
    {
        if (pos + 1 > end)
        {
            throw new ImageFormatException("A JBIG2 segment is shorter than its header fields.");
        }

        return b[pos++];
    }

    private static int U16(byte[] b, ref int pos, int end)
    {
        if (pos + 2 > end)
        {
            throw new ImageFormatException("A JBIG2 segment is shorter than its header fields.");
        }

        int v = (b[pos] << 8) | b[pos + 1];
        pos += 2;
        return v;
    }

    private static int ToInt(uint v)
    {
        if (v > int.MaxValue)
        {
            throw new ImageLimitException("A JBIG2 size field is larger than 2^31.");
        }

        return (int)v;
    }

    // 7.4.8
    private void PageInformation(byte[] buf, int start, int end)
    {
        int p = start;
        uint width = U32(buf, ref p, end);
        uint height = U32(buf, ref p, end);
        _xRes = U32(buf, ref p, end);
        _yRes = U32(buf, ref p, end);
        int flags = U8(buf, ref p, end);
        int striping = U16(buf, ref p, end);
        _pageDefault = (flags >> 2) & 1;
        _heightUnknown = height == 0xFFFFFFFF;
        int w = ToInt(width);
        _pageWidth = w;
        if (_heightUnknown)
        {
            // Rows are added as stripes arrive (8.2 step 2); the first stripe size is the starting height.
            _work.Limits.CheckFrame(w, Math.Max(striping & 0x7FFF, 1));
            _page = new Jbig2Bitmap(w, 0);
            return;
        }

        int h = ToInt(height);
        _work.Limits.CheckFrame(w, h);
        _work.ChargePixels(w, h);
        _page = new Jbig2Bitmap(w, h);
        if (_pageDefault != 0)
        {
            _page.Fill(1);
        }
    }

    private void EnsureHeight(long rows)
    {
        if (!_heightUnknown || _page is null || rows <= _page.Height)
        {
            return;
        }

        _work.Limits.CheckFrame(_pageWidth, rows);
        _work.ChargePixels(_pageWidth, rows);
        _page = _page.Resized((int)rows, _pageDefault);
        _knownHeight = Math.Max(_knownHeight, rows);
    }

    // 7.4.10
    private void EndOfStripe(byte[] buf, int start, int end)
    {
        if (_page is null)
        {
            return;
        }

        int p = start;
        uint row = U32(buf, ref p, end);
        if (_heightUnknown)
        {
            long rows = (long)row + 1;
            EnsureHeight(rows);
            _knownHeight = rows;
        }
    }

    private delegate Jbig2Bitmap RegionDecoder(Jbig2SegmentHeader h, byte[] buf, int start, int end, int width, int height, long x, long y, ref int pos);

    // The region segment information field (7.4.1) and what follows it, common to all region segments.
    private void Region(Jbig2SegmentHeader h, byte[] buf, int start, int end, RegionDecoder decode)
    {
        int p = start;
        uint w = U32(buf, ref p, end);
        uint hgt = U32(buf, ref p, end);
        uint x = U32(buf, ref p, end);
        uint y = U32(buf, ref p, end);
        int flags = U8(buf, ref p, end);
        int op = flags & 7;
        if (op > 4)
        {
            throw new ImageFormatException("A JBIG2 region has an invalid combination operator.");
        }

        if (_page is null)
        {
            throw new ImageFormatException("A JBIG2 region appears before the page information.");
        }

        int width = ToInt(w);
        int height = ToInt(hgt);
        _work.CheckRegion(width, height);
        Jbig2Bitmap bitmap = decode(h, buf, start, end, width, height, ((long)(int)x), ((long)(int)y), ref p);
        var combine = (Jbig2CombinationOperator)op;
        bool immediate = (h.Type & 2) != 0;
        if (immediate)
        {
            if (_heightUnknown)
            {
                EnsureHeight((long)(int)y + bitmap.Height);
            }

            _page.Compose(bitmap, (int)x, (int)y, combine);
        }
        else
        {
            _work.ChargeRetained((long)bitmap.Stride * bitmap.Height);
            _segments[h.Number] = new Jbig2AuxiliaryRegion { Bitmap = bitmap, X = (int)x, Y = (int)y, Operator = combine };
        }
    }

    private T Referred<T>(uint number, string what)
        where T : class
    {
        if (_segments.TryGetValue(number, out object? o) && o is T t)
        {
            return t;
        }

        throw new ImageFormatException($"A JBIG2 segment refers to segment {number}, which is not {what}.");
    }

    // 7.4.6
    private Jbig2Bitmap GenericRegion(Jbig2SegmentHeader h, byte[] buf, int start, int end, int width, int height, long x, long y, ref int p)
    {
        int flags = U8(buf, ref p, end);
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool tpgd = (flags & 8) != 0;
        if ((flags & 0xF0) != 0)
        {
            throw new ImageFormatException("A JBIG2 generic region uses a template extension this decoder does not support.");
        }

        int[] at = new int[8];
        if (!mmr)
        {
            int n = template == 0 ? 8 : 2;
            for (int i = 0; i < n; i++)
            {
                at[i] = (sbyte)U8(buf, ref p, end);
            }
        }

        int dataEnd = end;
        if (h.DataLength < 0)
        {
            // The last four bytes are the number of rows actually coded.
            int rows = (int)Math.Min(ReadU32(buf, end - 4), int.MaxValue);
            dataEnd = end - 4;
            if (rows > height)
            {
                throw new ImageFormatException("A JBIG2 generic region of unknown length has more rows than its header.");
            }

            height = rows;
        }

        if (p > dataEnd)
        {
            throw new ImageFormatException("A JBIG2 generic region has no coded data.");
        }

        if (mmr)
        {
            return Jbig2Generic.DecodeMmr(_work, buf, p, dataEnd, width, height, out _);
        }

        _work.CheckDensity(width, height, dataEnd - p);
        var mq = new Jbig2ArithmeticDecoder(buf, p, dataEnd);
        return Jbig2Generic.DecodeArithmetic(_work, mq, new byte[1 << 16], width, height, template, tpgd, null, at);
    }

    // 7.4.7
    private Jbig2Bitmap RefinementRegion(Jbig2SegmentHeader h, byte[] buf, int start, int end, int width, int height, long x, long y, ref int p)
    {
        int flags = U8(buf, ref p, end);
        int template = flags & 1;
        bool tpgr = (flags & 2) != 0;
        var at = new sbyte[4];
        if (template == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                at[i] = (sbyte)U8(buf, ref p, end);
            }
        }

        Jbig2Bitmap reference;
        if (h.Referred.Length > 0)
        {
            var aux = Referred<Jbig2AuxiliaryRegion>(h.Referred[0], "an intermediate region");
            _segments.Remove(h.Referred[0]);
            reference = aux.Bitmap;
        }
        else
        {
            // 7.4.7.4: the part of the page buffer the region covers.
            reference = _page!.Extract((int)x, (int)y, width, height);
        }

        var mq = new Jbig2ArithmeticDecoder(buf, p, end);
        return Jbig2Generic.DecodeRefinement(_work, mq, new byte[1 << 13], width, height, template, reference, 0, 0, tpgr, at);
    }

    // 7.4.4
    private Jbig2Bitmap[] PatternDictionary(byte[] buf, int start, int end)
    {
        int p = start;
        int flags = U8(buf, ref p, end);
        int pw = U8(buf, ref p, end);
        int ph = U8(buf, ref p, end);
        uint grayMax = U32(buf, ref p, end);
        return Jbig2Halftone.DecodePatternDictionary(_work, (flags & 1) != 0, (flags >> 1) & 3, pw, ph, grayMax, buf, p, end);
    }

    // 7.4.5
    private Jbig2Bitmap HalftoneRegion(Jbig2SegmentHeader h, byte[] buf, int start, int end, int width, int height, long x, long y, ref int p)
    {
        int flags = U8(buf, ref p, end);
        uint gw = U32(buf, ref p, end);
        uint gh = U32(buf, ref p, end);
        int gx = (int)U32(buf, ref p, end);
        int gy = (int)U32(buf, ref p, end);
        int rx = U16(buf, ref p, end);
        int ry = U16(buf, ref p, end);
        if (h.Referred.Length != 1)
        {
            throw new ImageFormatException("A JBIG2 halftone region must refer to exactly one pattern dictionary.");
        }

        var patterns = Referred<Jbig2Bitmap[]>(h.Referred[0], "a pattern dictionary");
        int combine = (flags >> 4) & 7;
        if (combine > 4)
        {
            throw new ImageFormatException("A JBIG2 halftone region has an invalid combination operator.");
        }

        var hp = new Jbig2HalftoneParameters
        {
            Width = width,
            Height = height,
            Mmr = (flags & 1) != 0,
            Template = (flags >> 1) & 3,
            EnableSkip = (flags & 8) != 0,
            CombinationOperator = (Jbig2CombinationOperator)combine,
            DefaultPixel = (flags >> 7) & 1,
            GridWidth = ToInt(gw),
            GridHeight = ToInt(gh),
            GridX = gx,
            GridY = gy,
            VectorX = rx,
            VectorY = ry,
            Patterns = patterns,
        };
        return Jbig2Halftone.Decode(_work, hp, buf, p, end);
    }

    // 7.4.2
    private Jbig2SymbolDictionaryResult SymbolDictionary(Jbig2SegmentHeader h, byte[] buf, int start, int end)
    {
        int p = start;
        int flags = U16(buf, ref p, end);
        bool huff = (flags & 1) != 0;
        bool refAgg = (flags & 2) != 0;
        int dhSel = (flags >> 2) & 3;
        int dwSel = (flags >> 4) & 3;
        int bmSel = (flags >> 6) & 1;
        int aggSel = (flags >> 7) & 1;
        bool contextUsed = (flags & 0x100) != 0;
        bool contextRetained = (flags & 0x200) != 0;
        int template = (flags >> 10) & 3;
        int rTemplate = (flags >> 12) & 1;
        if ((flags & 0xE000) != 0)
        {
            throw new ImageFormatException("A JBIG2 symbol dictionary has reserved flag bits set.");
        }

        var at = new int[8];
        if (!huff)
        {
            int n = template == 0 ? 8 : 2;
            for (int i = 0; i < n; i++)
            {
                at[i] = (sbyte)U8(buf, ref p, end);
            }
        }

        var rat = new sbyte[] { -1, -1, -1, -1 };
        if (refAgg && rTemplate == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                rat[i] = (sbyte)U8(buf, ref p, end);
            }
        }

        uint numEx = U32(buf, ref p, end);
        uint numNew = U32(buf, ref p, end);
        if (numEx > int.MaxValue || numNew > int.MaxValue)
        {
            throw new ImageLimitException("A JBIG2 symbol dictionary declares an impossible number of symbols.");
        }

        var inSymbols = new List<Jbig2Bitmap>();
        var tables = new List<Jbig2HuffmanTable>();
        Jbig2SymbolDictionaryResult? lastDictionary = null;
        foreach (uint r in h.Referred)
        {
            if (!_segments.TryGetValue(r, out object? o))
            {
                throw new ImageFormatException($"A JBIG2 segment refers to segment {r}, which does not exist.");
            }

            if (o is Jbig2SymbolDictionaryResult d)
            {
                inSymbols.AddRange(d.Symbols);
                lastDictionary = d;
                if (inSymbols.Count > _work.Jbig2.MaxSymbolsPerDictionary)
                {
                    throw new ImageLimitException("A JBIG2 symbol dictionary imports more symbols than the limit allows.");
                }
            }
            else if (o is Jbig2HuffmanTable t)
            {
                tables.Add(t);
            }
        }

        int tableIndex = 0;
        Jbig2HuffmanTable? dh = null;
        Jbig2HuffmanTable? dw = null;
        Jbig2HuffmanTable? bm = null;
        Jbig2HuffmanTable? agg = null;
        if (huff)
        {
            dh = dhSel switch { 0 => Jbig2HuffmanTable.Standard(4), 1 => Jbig2HuffmanTable.Standard(5), 3 => UserTable(tables, ref tableIndex), _ => throw new ImageFormatException("A JBIG2 symbol dictionary selects an invalid Huffman table.") };
            dw = dwSel switch { 0 => Jbig2HuffmanTable.Standard(2), 1 => Jbig2HuffmanTable.Standard(3), 3 => UserTable(tables, ref tableIndex), _ => throw new ImageFormatException("A JBIG2 symbol dictionary selects an invalid Huffman table.") };
            bm = bmSel == 0 ? Jbig2HuffmanTable.Standard(1) : UserTable(tables, ref tableIndex);
            agg = aggSel == 0 ? Jbig2HuffmanTable.Standard(1) : UserTable(tables, ref tableIndex);
        }

        byte[] gbCx = new byte[1 << 16];
        byte[] grCx = new byte[1 << 13];
        if (contextUsed)
        {
            if (lastDictionary?.GenericContexts is null || lastDictionary.RefinementContexts is null)
            {
                throw new ImageFormatException("A JBIG2 symbol dictionary reuses the contexts of a dictionary that did not keep them.");
            }

            lastDictionary.GenericContexts.CopyTo(gbCx, 0);
            lastDictionary.RefinementContexts.CopyTo(grCx, 0);
        }

        var parameters = new Jbig2SymbolDictionaryParameters
        {
            Huffman = huff,
            RefineAggregate = refAgg,
            InputSymbols = inSymbols,
            NewSymbolCount = (int)numNew,
            ExportCount = (int)numEx,
            HuffDh = dh,
            HuffDw = dw,
            HuffBitmapSize = bm,
            HuffAggregateInstances = agg,
            Template = template,
            At = at,
            RefinementTemplate = rTemplate,
            RefinementAt = rat,
        };
        Jbig2Bitmap[] symbols = Jbig2SymbolDictionary.Decode(_work, parameters, buf, p, end, gbCx, grCx);
        return new Jbig2SymbolDictionaryResult
        {
            Symbols = symbols,
            GenericContexts = contextRetained ? gbCx : null,
            RefinementContexts = contextRetained ? grCx : null,
        };
    }

    private static Jbig2HuffmanTable UserTable(List<Jbig2HuffmanTable> tables, ref int index)
    {
        if (index >= tables.Count)
        {
            throw new ImageFormatException("A JBIG2 segment selects more user-supplied Huffman tables than it refers to.");
        }

        return tables[index++];
    }

    // 7.4.3
    private Jbig2Bitmap TextRegion(Jbig2SegmentHeader h, byte[] buf, int start, int end, int width, int height, long x, long y, ref int p)
    {
        int flags = U16(buf, ref p, end);
        bool huff = (flags & 1) != 0;
        bool refine = (flags & 2) != 0;
        int logStrips = (flags >> 2) & 3;
        int corner = (flags >> 4) & 3;
        bool transposed = (flags & 0x40) != 0;
        int combine = (flags >> 7) & 3;
        int defPixel = (flags >> 9) & 1;
        int dsOffset = (flags >> 10) & 31;
        if (dsOffset > 15)
        {
            dsOffset -= 32;
        }

        int rTemplate = (flags >> 15) & 1;
        int hflags = 0;
        if (huff)
        {
            hflags = U16(buf, ref p, end);
        }

        var rat = new sbyte[] { -1, -1, -1, -1 };
        if (refine && rTemplate == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                rat[i] = (sbyte)U8(buf, ref p, end);
            }
        }

        uint numInstances = U32(buf, ref p, end);
        var symbols = new List<Jbig2Bitmap>();
        var tables = new List<Jbig2HuffmanTable>();
        foreach (uint r in h.Referred)
        {
            if (!_segments.TryGetValue(r, out object? o))
            {
                throw new ImageFormatException($"A JBIG2 segment refers to segment {r}, which does not exist.");
            }

            if (o is Jbig2SymbolDictionaryResult d)
            {
                symbols.AddRange(d.Symbols);
                if (symbols.Count > _work.Jbig2.MaxSymbolsPerDictionary)
                {
                    throw new ImageLimitException("A JBIG2 text region uses more symbols than the limit allows.");
                }
            }
            else if (o is Jbig2HuffmanTable t)
            {
                tables.Add(t);
            }
        }

        int codeLength = Jbig2TextRegion.CeilLog2(symbols.Count);
        Jbig2ArithmeticDecoder? mq = null;
        Jbig2BitReader? bits = null;
        Jbig2IntegerContexts? ints = null;
        Jbig2HuffmanTable? fs = null;
        Jbig2HuffmanTable? ds = null;
        Jbig2HuffmanTable? dt = null;
        Jbig2HuffmanTable? rdw = null;
        Jbig2HuffmanTable? rdh = null;
        Jbig2HuffmanTable? rdx = null;
        Jbig2HuffmanTable? rdy = null;
        Jbig2HuffmanTable? rsize = null;
        Jbig2HuffmanTable? symbolCodes = null;
        int dataStart = p;
        if (huff)
        {
            int ti = 0;
            fs = (hflags & 3) switch { 0 => Jbig2HuffmanTable.Standard(6), 1 => Jbig2HuffmanTable.Standard(7), 3 => UserTable(tables, ref ti), _ => throw BadTable() };
            ds = ((hflags >> 2) & 3) switch { 0 => Jbig2HuffmanTable.Standard(8), 1 => Jbig2HuffmanTable.Standard(9), 2 => Jbig2HuffmanTable.Standard(10), _ => UserTable(tables, ref ti) };
            dt = ((hflags >> 4) & 3) switch { 0 => Jbig2HuffmanTable.Standard(11), 1 => Jbig2HuffmanTable.Standard(12), 2 => Jbig2HuffmanTable.Standard(13), _ => UserTable(tables, ref ti) };
            rdw = RefinementTable((hflags >> 6) & 3, tables, ref ti);
            rdh = RefinementTable((hflags >> 8) & 3, tables, ref ti);
            rdx = RefinementTable((hflags >> 10) & 3, tables, ref ti);
            rdy = RefinementTable((hflags >> 12) & 3, tables, ref ti);
            rsize = ((hflags >> 14) & 1) == 0 ? Jbig2HuffmanTable.Standard(1) : UserTable(tables, ref ti);
            bits = new Jbig2BitReader(buf, p, end);
            symbolCodes = ReadSymbolCodes(bits, symbols.Count);
            dataStart = bits.BytePosition;
            bits = new Jbig2BitReader(buf, dataStart, end);
        }
        else
        {
            mq = new Jbig2ArithmeticDecoder(buf, p, end);
            ints = new Jbig2IntegerContexts(codeLength);
        }

        var tp = new Jbig2TextParameters
        {
            Huffman = huff,
            Refine = refine,
            Width = width,
            Height = height,
            InstanceCount = numInstances,
            LogStrips = logStrips,
            Symbols = symbols,
            SymbolCodeLength = codeLength,
            SymbolCodes = symbolCodes,
            DefaultPixel = defPixel,
            CombinationOperator = (Jbig2CombinationOperator)combine,
            Transposed = transposed,
            ReferenceCorner = corner,
            DeltaSOffset = dsOffset,
            HuffFs = fs,
            HuffDs = ds,
            HuffDt = dt,
            HuffRdw = rdw,
            HuffRdh = rdh,
            HuffRdx = rdx,
            HuffRdy = rdy,
            HuffRsize = rsize,
            RefinementTemplate = rTemplate,
            RefinementAt = rat,
        };
        return Jbig2TextRegion.Decode(_work, tp, mq, bits, ints, new byte[1 << 13]);
    }

    private static ImageFormatException BadTable() => new("A JBIG2 text region selects an invalid Huffman table.");

    private static Jbig2HuffmanTable? RefinementTable(int selection, List<Jbig2HuffmanTable> tables, ref int index) => selection switch
    {
        0 => Jbig2HuffmanTable.Standard(14),
        1 => Jbig2HuffmanTable.Standard(15),
        3 => UserTable(tables, ref index),
        _ => throw BadTable(),
    };

    // 7.4.3.1.7: the symbol ID code lengths, run-length coded with a Huffman code of their own.
    private Jbig2HuffmanTable ReadSymbolCodes(Jbig2BitReader bits, int symbolCount)
    {
        var runLengths = new int[35];
        for (int i = 0; i < 35; i++)
        {
            runLengths[i] = (int)bits.ReadBits(4);
        }

        Jbig2HuffmanTable runCodes = Jbig2HuffmanTable.FromCodeLengths(runLengths);
        var lengths = new int[symbolCount];
        int n = 0;
        while (n < symbolCount)
        {
            _work.Cancellation.ThrowIfCancellationRequested();
            if (!runCodes.Decode(bits, out int code))
            {
                throw new ImageFormatException("Invalid JBIG2 symbol ID run code.");
            }

            if (code < 32)
            {
                lengths[n++] = code;
                continue;
            }

            int repeat;
            int value = 0;
            if (code == 32)
            {
                if (n == 0)
                {
                    throw new ImageFormatException("A JBIG2 symbol ID table repeats a length before giving one.");
                }

                repeat = (int)bits.ReadBits(2) + 3;
                value = lengths[n - 1];
            }
            else if (code == 33)
            {
                repeat = (int)bits.ReadBits(3) + 3;
            }
            else
            {
                repeat = (int)bits.ReadBits(7) + 11;
            }

            if (n + repeat > symbolCount)
            {
                throw new ImageFormatException("A JBIG2 symbol ID table has more lengths than symbols.");
            }

            for (int k = 0; k < repeat; k++)
            {
                lengths[n++] = value;
            }
        }

        bits.Align();
        return Jbig2HuffmanTable.FromCodeLengths(lengths);
    }
}
