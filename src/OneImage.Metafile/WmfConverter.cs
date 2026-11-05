using System.Buffers.Binary;

namespace OneImage.Metafile;

/// <summary>
/// Turns a Windows metafile into the equivalent enhanced metafile, record by record, so that one interpreter carries the GDI semantics of both
/// (the same translation Windows itself performs when it converts a WMF). Coordinates widen from 16 to 32 bits, WMF's reversed parameter order is
/// put right, and the object table of a WMF (the lowest free slot is reused) becomes explicit EMF handles. Everything here works on spans of the
/// input with explicit length checks; nothing is allocated from a size the file declares without a limit being checked first.
/// </summary>
internal sealed class WmfConverter
{
    private const int GeneratedHandleBase = 1;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly MetafileLimits _lim;
    private readonly CancellationToken _cancel;
    private readonly long _maxOutput;
    private byte[] _buf = new byte[4096];
    private int _len;
    private readonly WmfRecordMap _map = new();
    private readonly List<DiagnosticCommand> _notes = [];
    private readonly HashSet<int> _reported = [];

    // The WMF object table: kind per slot (0 free), and the region rectangles of region objects.
    private enum Kind : byte { Free, Pen, Brush, Font, Region, Palette }

    private readonly List<Kind> _slots = [];
    private readonly SortedSet<int> _freeSlots = [];
    private readonly Dictionary<int, byte[]> _regions = [];
    private int _wmfRecord;
    private int _function;
    private int _emfRecords;
    private long _bitmapPixels;

    public WmfConverter(ReadOnlyMemory<byte> data, MetafileReadOptions options, CancellationToken cancel)
    {
        _data = data;
        _lim = options.Limits;
        _cancel = cancel;
        _maxOutput = Math.Max(1L << 20, (long)_lim.MaxFileBytes * 2);
    }

    /// <summary>The result of converting one file.</summary>
    internal sealed class Result
    {
        public required byte[] Emf { get; init; }

        public required MetafileInfo Info { get; init; }

        public required WmfRecordMap Map { get; init; }

        public required IReadOnlyList<DiagnosticCommand> Notes { get; init; }

        /// <summary>The enhanced metafile embedded in the file's comment records, when there is a valid one.</summary>
        public byte[]? EmbeddedEmf { get; init; }
    }

    public Result Convert(bool findEmbeddedEmf)
    {
        ReadOnlySpan<byte> d = _data.Span;
        WmfHeader header = WmfReader.ReadHeader(d);
        WmfReader.Layout layout = WmfReader.Describe(d, _lim);
        MetafileInfo info = WmfReader.Identify(d, _lim);
        byte[]? embedded = null;
        if (findEmbeddedEmf)
        {
            embedded = EmbeddedEmf.Find(d, header.RecordsStart, _lim, out string? problem);
            if (problem is not null)
            {
                Note(DiagnosticSeverity.Warning, -1, 0x0626, problem);
            }
        }

        AddHeader(layout);
        Walk(d, header.RecordsStart);
        _function = 0;
        Ints(14, 0, 16, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(48), (uint)_len);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(52), (uint)_emfRecords);
        return new Result { Emf = _buf.AsSpan(0, _len).ToArray(), Info = info, Map = _map, Notes = _notes, EmbeddedEmf = embedded };
    }

    // ---- the synthesized header ----------------------------------------------------------------------------------------

    private void AddHeader(WmfReader.Layout l)
    {
        const double mmPerPixel = 25.4 / WmfReader.Dpi;
        Span<byte> h = Rec(1, 108, -1, 0x0000);
        double pw = Math.Max(1e-3, l.PixelWidth), ph = Math.Max(1e-3, l.PixelHeight);
        Put(h, 8, 0, 0, (int)Math.Max(0, Math.Ceiling(pw) - 1), (int)Math.Max(0, Math.Ceiling(ph) - 1));
        Put(h, 24, 0, 0, (int)Math.Round(pw * mmPerPixel * 100), (int)Math.Round(ph * mmPerPixel * 100));
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], 0x464D4520);
        BinaryPrimitives.WriteUInt32LittleEndian(h[44..], 0x00010000);
        Put(h, 72, 1024, 768, (int)Math.Round(1024 * mmPerPixel), (int)Math.Round(768 * mmPerPixel));
        BinaryPrimitives.WriteUInt32LittleEndian(h[100..], (uint)Math.Round(1024 * mmPerPixel * 1000));
        BinaryPrimitives.WriteUInt32LittleEndian(h[104..], (uint)Math.Round(768 * mmPerPixel * 1000));

        // The window of the file maps onto the whole picture until the file says otherwise.
        IntsAt(17, -1, 0, 8);
        IntsAt(10, -1, 0, l.WindowOrgX, l.WindowOrgY);
        IntsAt(9, -1, 0, l.WindowExtX, l.WindowExtY);
        IntsAt(12, -1, 0, 0, 0);
        IntsAt(11, -1, 0, (int)Math.Round(pw), (int)Math.Round(ph));
    }

    private static void Put(Span<byte> s, int at, int a, int b, int c, int d)
    {
        BinaryPrimitives.WriteInt32LittleEndian(s[at..], a);
        BinaryPrimitives.WriteInt32LittleEndian(s[(at + 4)..], b);
        BinaryPrimitives.WriteInt32LittleEndian(s[(at + 8)..], c);
        BinaryPrimitives.WriteInt32LittleEndian(s[(at + 12)..], d);
    }

    // ---- output ----------------------------------------------------------------------------------------------------------

    private Span<byte> Rec(uint type, int size, int wmfIndex, int function)
    {
        size = (size + 3) & ~3;
        if (_len + (long)size > _maxOutput)
        {
            throw new ImageLimitException("The metafile converts to more data than the limits allow.");
        }

        if (_len + size > _buf.Length)
        {
            Array.Resize(ref _buf, (int)Math.Min(Math.Max((long)_buf.Length * 2, _len + (long)size), int.MaxValue / 2));
        }

        Span<byte> s = _buf.AsSpan(_len, size);
        s.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(s, type);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)size);
        _len += size;
        _emfRecords++;
        _map.Add(wmfIndex, function);
        return s;
    }

    private Span<byte> Rec(uint type, int size) => Rec(type, size, _wmfRecord, _function);

    private void IntsAt(uint type, int wmfIndex, int function, params ReadOnlySpan<int> values)
    {
        Span<byte> s = Rec(type, 8 + (values.Length * 4), wmfIndex, function);
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[(8 + (i * 4))..], values[i]);
        }
    }

    private void Ints(uint type, params ReadOnlySpan<int> values) => IntsAt(type, _wmfRecord, _function, values);

    private void Note(DiagnosticSeverity severity, int wmfIndex, int function, string message) =>
        _notes.Add(new DiagnosticCommand(severity, message, function) { RecordIndex = wmfIndex });

    /// <summary>Reports a function that has no effect on the picture here, once per function.</summary>
    private void Quiet(string what)
    {
        if (_reported.Add(_function))
        {
            Note(DiagnosticSeverity.Info, _wmfRecord, _function, $"{what} (WMF function 0x{_function:X4}) has no effect on the picture and is ignored.");
        }
    }

    private void Skip(string message) => Note(DiagnosticSeverity.Warning, _wmfRecord, _function, message);

    private static uint Rgb(ReadOnlySpan<byte> r, int o) => BinaryPrimitives.ReadUInt32LittleEndian(r[o..]) & 0xFFFFFF;

    // ---- the record loop ---------------------------------------------------------------------------------------------

    private void Walk(ReadOnlySpan<byte> d, int offset)
    {
        int off = offset;
        _wmfRecord = 0;
        while (off + 6 <= d.Length)
        {
            _cancel.ThrowIfCancellationRequested();
            uint words = BinaryPrimitives.ReadUInt32LittleEndian(d[off..]);
            int function = BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 4)..]);
            _function = function;
            if (words < 3)
            {
                Note(DiagnosticSeverity.Error, _wmfRecord, function, $"Record {_wmfRecord} has an invalid size of {words} words; the rest of the file is ignored.");
                break;
            }

            if ((ulong)off + (words * 2UL) > (ulong)d.Length)
            {
                Note(DiagnosticSeverity.Warning, _wmfRecord, function, $"Record {_wmfRecord} runs past the end of the file; the file is truncated.");
                break;
            }

            if (_wmfRecord >= _lim.MaxRecords)
            {
                throw new ImageLimitException($"The metafile has more than {_lim.MaxRecords} records.");
            }

            if (function == 0)
            {
                break;
            }

            Handle(function, d.Slice(off, (int)(words * 2)));
            off += (int)(words * 2);
            _wmfRecord++;
        }
    }

    private bool Need(ReadOnlySpan<byte> r, int bytes)
    {
        if (r.Length >= bytes)
        {
            return true;
        }

        Skip($"Record {_wmfRecord} is shorter than its fields need; it is skipped.");
        return false;
    }

    private static int S(ReadOnlySpan<byte> r, int o) => WmfReader.S16(r, o);

    private static int U(ReadOnlySpan<byte> r, int o) => WmfReader.U16(r, o);

    private void Handle(int function, ReadOnlySpan<byte> r)
    {
        // The bitmap records carry a parameter count in their high byte, so they are told apart by the low byte, as GDI does.
        switch (function & 0xFF)
        {
            case 0x22: BitBlt(r, false); return;
            case 0x40: BitBlt(r, true); return;
            case 0x23: StretchBlt(r, false); return;
            case 0x41: StretchBlt(r, true); return;
            case 0x33: SetDibToDevice(r); return;
            case 0x43: StretchDib(r); return;
        }

        switch (function)
        {
            // ---- device-context state
            case 0x001E: Ints(33); break;
            case 0x0127: if (Need(r, 8)) { Ints(34, S(r, 6)); } break;
            case 0x0102: if (Need(r, 8)) { Ints(18, U(r, 6)); } break;
            case 0x0103: if (Need(r, 8)) { Ints(17, U(r, 6)); } break;
            case 0x0104: if (Need(r, 8)) { Ints(20, U(r, 6)); } break;
            case 0x0106: if (Need(r, 8)) { Ints(19, U(r, 6)); } break;
            case 0x0107: if (Need(r, 8)) { Ints(21, U(r, 6)); } break;
            case 0x012E: if (Need(r, 8)) { Ints(22, U(r, 6)); } break;
            case 0x0201: if (Need(r, 10)) { Ints(25, (int)Rgb(r, 6)); } break;
            case 0x0209: if (Need(r, 10)) { Ints(24, (int)Rgb(r, 6)); } break;
            case 0x020A: if (Need(r, 10)) { Ints(120, S(r, 8), S(r, 6)); } break;
            case 0x0108: if (Need(r, 8)) { Ints(0x10003, S(r, 6)); } break;
            case 0x0105: Quiet("Relative-absolute mode"); break;
            case 0x0231: Quiet("The font mapper flags"); break;
            case 0x0149: if (Need(r, 8)) { Ints(115, U(r, 6)); } break;

            // ---- mapping
            case 0x020B: if (Need(r, 10)) { Ints(10, S(r, 8), S(r, 6)); } break;
            case 0x020C: if (Need(r, 10)) { Ints(9, S(r, 8), S(r, 6)); } break;
            case 0x020D: if (Need(r, 10)) { Ints(12, S(r, 8), S(r, 6)); } break;
            case 0x020E: if (Need(r, 10)) { Ints(11, S(r, 8), S(r, 6)); } break;
            case 0x020F: if (Need(r, 10)) { Ints(0x10001, S(r, 8), S(r, 6)); } break;
            case 0x0211: if (Need(r, 10)) { Ints(0x10002, S(r, 8), S(r, 6)); } break;
            case 0x0410: if (Need(r, 14)) { Ints(32, S(r, 12), S(r, 10), S(r, 8), S(r, 6)); } break;
            case 0x0412: if (Need(r, 14)) { Ints(31, S(r, 12), S(r, 10), S(r, 8), S(r, 6)); } break;

            // ---- clipping
            case 0x0415: if (Need(r, 14)) { Ints(29, S(r, 12), S(r, 10), S(r, 8), S(r, 6)); } break;
            case 0x0416: if (Need(r, 14)) { Ints(30, S(r, 12), S(r, 10), S(r, 8), S(r, 6)); } break;
            case 0x0220: if (Need(r, 10)) { Ints(26, S(r, 8), S(r, 6)); } break;
            case 0x012C: if (Need(r, 8)) { SelectClipRegion(U(r, 6)); } break;

            // ---- drawing
            case 0x0213: if (Need(r, 10)) { Ints(54, S(r, 8), S(r, 6)); } break;
            case 0x0214: if (Need(r, 10)) { Ints(27, S(r, 8), S(r, 6)); } break;
            case 0x041B: if (Need(r, 14)) { Box(43, r, 6); } break;
            case 0x0418: if (Need(r, 14)) { Box(42, r, 6); } break;
            case 0x061C: if (Need(r, 18)) { RoundRect(r); } break;
            case 0x0817: if (Need(r, 22)) { Arc(45, r, 6); } break;
            case 0x081A: if (Need(r, 22)) { Arc(47, r, 6); } break;
            case 0x0830: if (Need(r, 22)) { Arc(46, r, 6); } break;
            case 0x041F: if (Need(r, 14)) { Ints(15, S(r, 12), S(r, 10), (int)Rgb(r, 6)); } break;
            case 0x0324: Poly(86, r); break;
            case 0x0325: Poly(87, r); break;
            case 0x0538: PolyPolygon(r); break;
            case 0x061D: if (Need(r, 18)) { PatBlt(r); } break;
            case 0x0419: case 0x0548: Skip("Flood fills are not supported; the record is skipped."); break;
            case 0x0521: TextOut(r); break;
            case 0x0A32: ExtTextOut(r); break;
            case 0x0228: FillRegion(r); break;
            case 0x0429: FrameRegion(r); break;
            case 0x012A: PaintRegion(73, r); break;
            case 0x012B: PaintRegion(74, r); break;

            // ---- objects
            case 0x012D: if (Need(r, 8)) { SelectObject(U(r, 6)); } break;
            case 0x01F0: if (Need(r, 8)) { DeleteObject(U(r, 6)); } break;
            case 0x02FA: if (Need(r, 16)) { CreatePen(r); } break;
            case 0x02FC: if (Need(r, 14)) { CreateBrush(r); } break;
            case 0x02FB: if (Need(r, 56)) { CreateFont(r); } break;
            case 0x06FF: CreateRegion(r); break;
            case 0x0142: DibCreatePatternBrush(r); break;
            case 0x01F9: CreatePatternBrush(r); break;
            case 0x00F7: CreatePalette(); break;
            case 0x0234: if (Need(r, 8)) { SelectPalette(U(r, 6)); } break;
            case 0x0035: Quiet("Palette handling"); break;
            case 0x0037: case 0x0436: Quiet("Palette handling"); break;
            case 0x0139: Quiet("Palette handling"); break;

            // ---- escapes and printing
            case 0x0626: Escape(r); break;
            case 0x014C or 0x014D or 0x014F or 0x0150 or 0x0152 or 0x015E: Quiet("A printing control record"); break;
            default:
                if (_reported.Add(function))
                {
                    Skip($"Unknown WMF function 0x{function:X4} (record {_wmfRecord}, {r.Length} bytes) is skipped.");
                }

                break;
        }
    }

    // ---- shapes and lines --------------------------------------------------------------------------------------------

    private void Box(uint emfType, ReadOnlySpan<byte> r, int at)
    {
        // Bottom, Right, Top, Left
        Ints(emfType, S(r, at + 6), S(r, at + 4), S(r, at + 2), S(r, at));
    }

    private void RoundRect(ReadOnlySpan<byte> r)
    {
        // Height, Width, Bottom, Right, Top, Left
        Ints(44, S(r, 16), S(r, 14), S(r, 12), S(r, 10), S(r, 8), S(r, 6));
    }

    private void Arc(uint emfType, ReadOnlySpan<byte> r, int at)
    {
        // YEnd XEnd YStart XStart Bottom Right Top Left
        int yEnd = S(r, at), xEnd = S(r, at + 2), yStart = S(r, at + 4), xStart = S(r, at + 6);
        int bottom = S(r, at + 8), right = S(r, at + 10), top = S(r, at + 12), left = S(r, at + 14);
        Ints(emfType, left, top, right, bottom, xStart, yStart, xEnd, yEnd);
    }

    private void Poly(uint emfType, ReadOnlySpan<byte> r)
    {
        if (!Need(r, 8))
        {
            return;
        }

        int n = U(r, 6);
        if (n * 4L + 8 > r.Length)
        {
            Skip($"A poly record declares {n} points that do not fit; it is skipped.");
            return;
        }

        Span<byte> s = Rec(emfType, 28 + (n * 4));
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], n);
        r.Slice(8, n * 4).CopyTo(s[28..]);
    }

    private void PolyPolygon(ReadOnlySpan<byte> r)
    {
        if (!Need(r, 8))
        {
            return;
        }

        int polys = U(r, 6);
        if (8L + (polys * 2L) > r.Length)
        {
            Skip($"A PolyPolygon declares {polys} polygons that do not fit; it is skipped.");
            return;
        }

        long total = 0;
        for (int i = 0; i < polys; i++)
        {
            total += U(r, 8 + (i * 2));
        }

        int pointsAt = 8 + (polys * 2);
        if (pointsAt + (total * 4) > r.Length)
        {
            Skip("A PolyPolygon declares more points than it holds; it is skipped.");
            return;
        }

        Span<byte> s = Rec(91, 32 + (polys * 4) + (int)(total * 4));
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], polys);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], (int)total);
        for (int i = 0; i < polys; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[(32 + (i * 4))..], U(r, 8 + (i * 2)));
        }

        r.Slice(pointsAt, (int)(total * 4)).CopyTo(s[(32 + (polys * 4))..]);
    }

    private void PatBlt(ReadOnlySpan<byte> r)
    {
        // RasterOperation(4), Height, Width, YLeft, XLeft
        uint rop = BinaryPrimitives.ReadUInt32LittleEndian(r[6..]);
        BitBltNoSource(S(r, 16), S(r, 14), S(r, 12), S(r, 10), rop);
    }

    /// <summary>An EMF BITBLT without a source bitmap: the form GDI records for PatBlt, and for a block transfer whose source is the device itself.</summary>
    private void BitBltNoSource(int x, int y, int cx, int cy, uint rop)
    {
        Span<byte> s = Rec(76, 100);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], x);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], y);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], cx);
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], cy);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], rop);
        BinaryPrimitives.WriteSingleLittleEndian(s[52..], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(s[68..], 1f);
    }

    // ---- text --------------------------------------------------------------------------------------------------------

    private void TextOut(ReadOnlySpan<byte> r)
    {
        // StringLength, String (padded to a word), Y, X
        if (!Need(r, 8))
        {
            return;
        }

        int n = S(r, 6);
        int padded = (n + 1) & ~1;
        if (n < 0 || n > _lim.MaxTextCharacters || 8L + padded + 4 > r.Length)
        {
            Skip("A TextOut record has a string that does not fit in the record; it is skipped.");
            return;
        }

        int y = S(r, 8 + padded), x = S(r, 10 + padded);
        EmitText(x, y, 0, default, r.Slice(8, n), default);
    }

    private void ExtTextOut(ReadOnlySpan<byte> r)
    {
        // Y, X, StringLength, Options, [Rectangle(8)], String (padded), [Dx]
        if (!Need(r, 14))
        {
            return;
        }

        int y = S(r, 6), x = S(r, 8), n = S(r, 10), options = U(r, 12);
        int at = 14;
        int rl = 0, rt = 0, rr = 0, rb = 0;
        if ((options & 6) != 0)
        {
            if (!Need(r, 22))
            {
                return;
            }

            (rl, rt, rr, rb) = (S(r, 14), S(r, 16), S(r, 18), S(r, 20));
            at = 22;
        }

        int padded = (n + 1) & ~1;
        if (n < 0 || n > _lim.MaxTextCharacters || (long)at + padded > r.Length)
        {
            Skip("An ExtTextOut record has a string that does not fit in the record; it is skipped.");
            return;
        }

        ReadOnlySpan<byte> text = r.Slice(at, n);
        ReadOnlySpan<byte> dx = default;
        if (n > 0 && (long)at + padded + (n * 2L) <= r.Length)
        {
            dx = r.Slice(at + padded, n * 2);
        }

        EmitText(x, y, options, (rl, rt, rr, rb), text, dx);
    }

    private void EmitText(int x, int y, int options, (int L, int T, int R, int B) rect, ReadOnlySpan<byte> text, ReadOnlySpan<byte> dx16)
    {
        int n = text.Length;
        int strBytes = (n + 3) & ~3;
        int dxCount = dx16.Length / 2;
        Span<byte> s = Rec(83, 76 + strBytes + (dxCount * 4));
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], x);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], y);
        BinaryPrimitives.WriteInt32LittleEndian(s[44..], n);
        BinaryPrimitives.WriteInt32LittleEndian(s[48..], 76);
        BinaryPrimitives.WriteInt32LittleEndian(s[52..], options & 0xFFFF);
        Put(s, 56, rect.L, rect.T, rect.R, rect.B);
        BinaryPrimitives.WriteInt32LittleEndian(s[72..], dxCount > 0 ? 76 + strBytes : 0);
        text.CopyTo(s[76..]);
        for (int i = 0; i < dxCount; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[(76 + strBytes + (i * 4))..], WmfReader.S16(dx16, i * 2));
        }
    }

    // ---- objects -------------------------------------------------------------------------------------------------------

    private int AllocateSlot(Kind kind)
    {
        int slot;
        if (_freeSlots.Count > 0)
        {
            slot = _freeSlots.Min;
            _freeSlots.Remove(slot);
        }
        else
        {
            slot = _slots.Count;
            if (slot + GeneratedHandleBase >= _lim.MaxObjects)
            {
                Skip($"The object table is full ({_lim.MaxObjects} objects); the object is not created.");
                return -1;
            }

            _slots.Add(Kind.Free);
        }

        _slots[slot] = kind;
        return slot;
    }

    private static int Hnd(int slot) => slot + GeneratedHandleBase;

    private bool IsKind(int slot, Kind kind) => (uint)slot < (uint)_slots.Count && _slots[slot] == kind;

    private void SelectObject(int slot)
    {
        if ((uint)slot >= (uint)_slots.Count || _slots[slot] == Kind.Free)
        {
            Skip($"SelectObject names object {slot}, which does not exist; the selection is ignored.");
            return;
        }

        if (_slots[slot] == Kind.Region)
        {
            SelectClipRegion(slot);
            return;
        }

        if (_slots[slot] != Kind.Palette)
        {
            Ints(37, Hnd(slot));
        }
    }

    private void DeleteObject(int slot)
    {
        if ((uint)slot >= (uint)_slots.Count || _slots[slot] == Kind.Free)
        {
            Skip($"DeleteObject names object {slot}, which does not exist; ignored.");
            return;
        }

        if (_slots[slot] is not (Kind.Palette or Kind.Region))
        {
            Ints(40, Hnd(slot));
        }

        _regions.Remove(slot);
        _slots[slot] = Kind.Free;
        _freeSlots.Add(slot);
    }

    private void CreatePen(ReadOnlySpan<byte> r)
    {
        // PenStyle, Width (PointS), ColorRef
        int slot = AllocateSlot(Kind.Pen);
        if (slot < 0)
        {
            return;
        }

        Ints(38, Hnd(slot), U(r, 6), S(r, 8), 0, (int)Rgb(r, 12));
    }

    private void CreateBrush(ReadOnlySpan<byte> r)
    {
        // BrushStyle, ColorRef, BrushHatch
        int slot = AllocateSlot(Kind.Brush);
        if (slot < 0)
        {
            return;
        }

        Ints(39, Hnd(slot), U(r, 6), (int)Rgb(r, 8), U(r, 12));
    }

    private void CreateFont(ReadOnlySpan<byte> r)
    {
        // Height Width Escapement Orientation Weight, Italic Underline StrikeOut CharSet OutPrecision ClipPrecision Quality PitchAndFamily, Facename[32]
        int slot = AllocateSlot(Kind.Font);
        if (slot < 0)
        {
            return;
        }

        Span<byte> s = Rec(82, 104);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], Hnd(slot));
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], S(r, 6));
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], S(r, 8));
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], S(r, 10));
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], S(r, 12));
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], S(r, 14));
        r.Slice(16, 8).CopyTo(s[32..]);
        ReadOnlySpan<byte> name = r.Slice(24, 32);
        int len = name.IndexOf((byte)0);
        string face = EmfInterpreter.Decode8(len < 0 ? name : name[..len]);
        for (int i = 0; i < Math.Min(face.Length, 31); i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[(40 + (i * 2))..], face[i]);
        }
    }

    private void CreatePalette()
    {
        int slot = AllocateSlot(Kind.Palette);
        if (slot >= 0)
        {
            Quiet("Palette handling");
        }
    }

    private void SelectPalette(int slot)
    {
        if (IsKind(slot, Kind.Palette))
        {
            Quiet("Palette handling");
        }
    }

    // ---- regions -------------------------------------------------------------------------------------------------------

    private void CreateRegion(ReadOnlySpan<byte> r)
    {
        // nextInChain(2) ObjectType(2) ObjectCount(4) RegionSize(2) ScanCount(2) maxScan(2) BoundingRectangle(8) aScans
        int slot = AllocateSlot(Kind.Region);
        if (slot < 0)
        {
            return;
        }

        byte[]? data = null;
        if (r.Length >= 28 && S(r, 8) == 6)
        {
            data = RegionData(r, 28, U(r, 16));
        }

        if (data is null)
        {
            Skip("A region object is damaged; it is treated as empty.");
            data = EmptyRegion();
        }

        _regions[slot] = data;
    }

    private static byte[] EmptyRegion()
    {
        var d = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(d, 32);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), 1);
        return d;
    }

    /// <summary>Converts the scan lines of a WMF region to the rectangle list of an EMF RGNDATA, or returns null when they do not fit.</summary>
    private byte[]? RegionData(ReadOnlySpan<byte> r, int at, int scans)
    {
        // Count the rectangles first: the allocation must follow a checked number.
        long rects = 0;
        int p = at;
        for (int i = 0; i < scans; i++)
        {
            if (p + 8 > r.Length)
            {
                return null;
            }

            int count = U(r, p);
            if (p + 8L + (count * 2L) > r.Length || (count & 1) != 0)
            {
                return null;
            }

            rects += count / 2;
            if (rects > _lim.MaxRegionRectangles)
            {
                throw new ImageLimitException($"A region has more than {_lim.MaxRegionRectangles} rectangles.");
            }

            p += 8 + (count * 2);
        }

        var d = new byte[32 + (int)(rects * 16)];
        BinaryPrimitives.WriteInt32LittleEndian(d, 32);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(8), (int)rects);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(12), (int)(rects * 16));
        int n = 0;
        int minL = int.MaxValue, minT = int.MaxValue, maxR = int.MinValue, maxB = int.MinValue;
        p = at;
        for (int i = 0; i < scans; i++)
        {
            int count = U(r, p), top = S(r, p + 2), bottom = S(r, p + 4);
            for (int k = 0; k + 1 < count; k += 2)
            {
                int left = S(r, p + 6 + (k * 2)), right = S(r, p + 8 + (k * 2));
                int o = 32 + (n * 16);
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o), left);
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 4), top);
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 8), right);
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 12), bottom);
                minL = Math.Min(minL, left);
                minT = Math.Min(minT, top);
                maxR = Math.Max(maxR, right);
                maxB = Math.Max(maxB, bottom);
                n++;
            }

            p += 8 + (count * 2);
        }

        if (n > 0)
        {
            Put(d, 16, minL, minT, maxR, maxB);
        }

        return d;
    }

    private static void Put(byte[] d, int at, int a, int b, int c, int e) => Put(d.AsSpan(), at, a, b, c, e);

    private void SelectClipRegion(int slot)
    {
        if (!_regions.TryGetValue(slot, out byte[]? data))
        {
            // A null region removes the clip.
            Ints(75, 0, 5);
            return;
        }

        Span<byte> s = Rec(75, 16 + data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], 5);
        data.CopyTo(s[16..]);
    }

    private void FillRegion(ReadOnlySpan<byte> r)
    {
        if (!Need(r, 10))
        {
            return;
        }

        int region = U(r, 6), brush = U(r, 8);
        if (!_regions.TryGetValue(region, out byte[]? data) || !IsKind(brush, Kind.Brush))
        {
            Skip("FillRegion names a region or brush that does not exist; skipped.");
            return;
        }

        Span<byte> s = Rec(71, 32 + data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], Hnd(brush));
        data.CopyTo(s[32..]);
    }

    private void FrameRegion(ReadOnlySpan<byte> r)
    {
        // Region, Brush, Height, Width
        if (!Need(r, 14))
        {
            return;
        }

        int region = U(r, 6), brush = U(r, 8);
        if (!_regions.TryGetValue(region, out byte[]? data) || !IsKind(brush, Kind.Brush))
        {
            Skip("FrameRegion names a region or brush that does not exist; skipped.");
            return;
        }

        Span<byte> s = Rec(72, 40 + data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], Hnd(brush));
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], S(r, 12));
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], S(r, 10));
        data.CopyTo(s[40..]);
    }

    private void PaintRegion(uint emfType, ReadOnlySpan<byte> r)
    {
        if (!Need(r, 8))
        {
            return;
        }

        if (!_regions.TryGetValue(U(r, 6), out byte[]? data))
        {
            Skip("The record names a region that does not exist; skipped.");
            return;
        }

        Span<byte> s = Rec(emfType, 28 + data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], data.Length);
        data.CopyTo(s[28..]);
    }

    // ---- bitmaps ---------------------------------------------------------------------------------------------------------

    /// <summary>Finds where the bitmap info of a packed DIB ends and its pixels begin.</summary>
    internal static bool TrySplitPackedDib(ReadOnlySpan<byte> dib, out int infoLength)
    {
        infoLength = 0;
        if (dib.Length < 12)
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        long length;
        if (size == 12)
        {
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
            length = 12 + (bits <= 8 ? (1L << bits) * 3 : 0);
        }
        else
        {
            if (size < 40 || size > dib.Length)
            {
                return false;
            }

            int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            int compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
            uint used = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
            long colors = bits <= 8 ? (used > 0 ? Math.Min(used, 1L << bits) : 1L << bits) : Math.Min(used, 256);
            length = size + (colors * 4);
            if (compression == 3 && size == 40)
            {
                length += 12;
            }
        }

        if (length > dib.Length)
        {
            return false;
        }

        infoLength = (int)length;
        return true;
    }

    /// <summary>A device-dependent bitmap (the Bitmap16 object) as a top-down DIB, or false when the bitmap cannot be represented.</summary>
    private bool TryBitmap16(ReadOnlySpan<byte> r, int at, int bitsAt, out byte[] bmi, out byte[] bits, out int width, out int height)
    {
        bmi = bits = [];
        width = height = 0;
        if (at + 10 > r.Length)
        {
            return false;
        }

        width = S(r, at + 2);
        height = S(r, at + 4);
        int widthBytes = S(r, at + 6);
        int planes = r[at + 8], bpp = r[at + 9];
        if (width <= 0 || height <= 0 || planes != 1 || bpp is not (1 or 16 or 24 or 32))
        {
            Skip(bpp is 4 or 8 && width > 0 && height > 0
                ? "A device-dependent bitmap with a palette (4 or 8 bits per pixel) cannot be reproduced; the record is skipped."
                : "A device-dependent bitmap has an unsupported format; the record is skipped.");
            return false;
        }

        long needed = (long)widthBytes * height;
        if (widthBytes < (((width * bpp) + 15) >> 4 << 1) || bitsAt + needed > r.Length)
        {
            Skip("A device-dependent bitmap is larger than its record; the record is skipped.");
            return false;
        }

        _bitmapPixels += (long)width * height;
        if ((long)width * height > _lim.MaxBitmapPixels || _bitmapPixels > _lim.MaxTotalBitmapPixels)
        {
            throw new ImageLimitException("The metafile has bitmaps with more pixels than the limit allows.");
        }

        int stride = ((width * bpp) + 31) >> 5 << 2;
        int paletteBytes = bpp == 1 ? 8 : 0;
        bmi = new byte[40 + paletteBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bmi, 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmi.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmi.AsSpan(8), -height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmi.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmi.AsSpan(14), (ushort)bpp);
        if (bpp == 1)
        {
            // Monochrome device bitmaps: bit 0 is black, bit 1 is white.
            bmi[44] = bmi[45] = bmi[46] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(bmi.AsSpan(32), 2);
        }

        bits = new byte[(long)stride * height > int.MaxValue ? 0 : stride * height];
        int copy = Math.Min(widthBytes, stride);
        for (int y = 0; y < height; y++)
        {
            r.Slice(bitsAt + (y * widthBytes), copy).CopyTo(bits.AsSpan(y * stride));
        }

        return true;
    }

    private void StretchDibitsRecord(int xd, int yd, int cxd, int cyd, int xs, int ys, int cxs, int cys, uint rop, int usage, ReadOnlySpan<byte> bmi, ReadOnlySpan<byte> bits)
    {
        int bmiPad = (bmi.Length + 3) & ~3, bitsPad = (bits.Length + 3) & ~3;
        Span<byte> s = Rec(81, 80 + bmiPad + bitsPad);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], xd);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], yd);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], xs);
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], ys);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], cxs);
        BinaryPrimitives.WriteInt32LittleEndian(s[44..], cys);
        BinaryPrimitives.WriteInt32LittleEndian(s[48..], 80);
        BinaryPrimitives.WriteInt32LittleEndian(s[52..], bmi.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[56..], 80 + bmiPad);
        BinaryPrimitives.WriteInt32LittleEndian(s[60..], bits.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[64..], usage);
        BinaryPrimitives.WriteUInt32LittleEndian(s[68..], rop);
        BinaryPrimitives.WriteInt32LittleEndian(s[72..], cxd);
        BinaryPrimitives.WriteInt32LittleEndian(s[76..], cyd);
        bmi.CopyTo(s[80..]);
        bits.CopyTo(s[(80 + bmiPad)..]);
    }

    /// <summary>Splits the DIB that starts at <paramref name="at"/> and checks its color usage; false (with a note) when it cannot be drawn.</summary>
    private bool TryDib(ReadOnlySpan<byte> r, int at, int usage, out ReadOnlySpan<byte> bmi, out ReadOnlySpan<byte> bits)
    {
        bmi = bits = default;
        if (at >= r.Length || !TrySplitPackedDib(r[at..], out int info) || at + info >= r.Length)
        {
            Skip("A bitmap record has no usable bitmap; it is skipped.");
            return false;
        }

        if (usage == 1)
        {
            Skip("A bitmap whose colors index the device palette (DIB_PAL_COLORS) is not supported; the record is skipped.");
            return false;
        }

        bmi = r.Slice(at, info);
        bits = r[(at + info)..];
        return true;
    }

    private void BitBlt(ReadOnlySpan<byte> r, bool dib)
    {
        // RasterOperation(4), YSrc, XSrc, [Reserved,] Height, Width, YDest, XDest, [Bitmap16 | DIB]
        if (!Need(r, 22))
        {
            return;
        }

        uint rop = BinaryPrimitives.ReadUInt32LittleEndian(r[6..]);
        int ySrc = S(r, 10), xSrc = S(r, 12);
        if ((r.Length / 2) == (_function >> 8) + 3)
        {
            // Without a bitmap: Reserved sits between XSrc and Height.
            if (!Need(r, 24))
            {
                return;
            }

            BitBltNoSource(S(r, 22), S(r, 20), S(r, 18), S(r, 16), rop);
            return;
        }

        int height = S(r, 14), width = S(r, 16), yDest = S(r, 18), xDest = S(r, 20);
        if (dib)
        {
            if (TryDib(r, 22, 0, out ReadOnlySpan<byte> bmi, out ReadOnlySpan<byte> bits))
            {
                StretchDibitsRecord(xDest, yDest, width, height, xSrc, ySrc, width, height, rop, 0, bmi, bits);
            }
        }
        else if (TryBitmap16(r, 22, 32, out byte[] b, out byte[] px, out _, out _))
        {
            StretchDibitsRecord(xDest, yDest, width, height, xSrc, ySrc, width, height, rop, 0, b, px);
        }
    }

    private void StretchBlt(ReadOnlySpan<byte> r, bool dib)
    {
        // RasterOperation(4), SrcHeight, SrcWidth, YSrc, XSrc, [Reserved,] DestHeight, DestWidth, YDest, XDest, [Bitmap16 | DIB]
        if (!Need(r, 26))
        {
            return;
        }

        uint rop = BinaryPrimitives.ReadUInt32LittleEndian(r[6..]);
        if ((r.Length / 2) == (_function >> 8) + 3)
        {
            if (!Need(r, 28))
            {
                return;
            }

            BitBltNoSource(S(r, 26), S(r, 24), S(r, 22), S(r, 20), rop);
            return;
        }

        int srcH = S(r, 10), srcW = S(r, 12), ySrc = S(r, 14), xSrc = S(r, 16);
        int dstH = S(r, 18), dstW = S(r, 20), yDest = S(r, 22), xDest = S(r, 24);
        if (dib)
        {
            if (TryDib(r, 26, 0, out ReadOnlySpan<byte> bmi, out ReadOnlySpan<byte> bits))
            {
                StretchDibitsRecord(xDest, yDest, dstW, dstH, xSrc, ySrc, srcW, srcH, rop, 0, bmi, bits);
            }
        }
        else if (TryBitmap16(r, 26, 36, out byte[] b, out byte[] px, out _, out _))
        {
            StretchDibitsRecord(xDest, yDest, dstW, dstH, xSrc, ySrc, srcW, srcH, rop, 0, b, px);
        }
    }

    private void StretchDib(ReadOnlySpan<byte> r)
    {
        // RasterOperation(4), ColorUsage, SrcHeight, SrcWidth, YSrc, XSrc, DestHeight, DestWidth, yDst, xDst, DIB
        if (!Need(r, 28))
        {
            return;
        }

        uint rop = BinaryPrimitives.ReadUInt32LittleEndian(r[6..]);
        int usage = U(r, 10);
        if (TryDib(r, 28, usage, out ReadOnlySpan<byte> bmi, out ReadOnlySpan<byte> bits))
        {
            StretchDibitsRecord(S(r, 26), S(r, 24), S(r, 22), S(r, 20), S(r, 18), S(r, 16), S(r, 14), S(r, 12), rop, usage, bmi, bits);
        }
    }

    private void SetDibToDevice(ReadOnlySpan<byte> r)
    {
        // ColorUsage, ScanCount, StartScan, yDib, xDib, Height, Width, yDest, xDest, DIB
        if (!Need(r, 24))
        {
            return;
        }

        int usage = U(r, 6);
        if (!TryDib(r, 24, usage, out ReadOnlySpan<byte> bmi, out ReadOnlySpan<byte> bits))
        {
            return;
        }

        int bmiPad = (bmi.Length + 3) & ~3, bitsPad = (bits.Length + 3) & ~3;
        Span<byte> s = Rec(80, 76 + bmiPad + bitsPad);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], S(r, 22));
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], S(r, 20));
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], S(r, 14));
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], S(r, 12));
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], S(r, 18));
        BinaryPrimitives.WriteInt32LittleEndian(s[44..], S(r, 16));
        BinaryPrimitives.WriteInt32LittleEndian(s[48..], 76);
        BinaryPrimitives.WriteInt32LittleEndian(s[52..], bmi.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[56..], 76 + bmiPad);
        BinaryPrimitives.WriteInt32LittleEndian(s[60..], bits.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[64..], usage);
        BinaryPrimitives.WriteInt32LittleEndian(s[68..], U(r, 10));
        BinaryPrimitives.WriteInt32LittleEndian(s[72..], U(r, 8));
        bmi.CopyTo(s[76..]);
        bits.CopyTo(s[(76 + bmiPad)..]);
    }

    // ---- brushes made from bitmaps -----------------------------------------------------------------------------------------

    private void DibCreatePatternBrush(ReadOnlySpan<byte> r)
    {
        // Style, ColorUsage, DIB or Bitmap16
        if (!Need(r, 10))
        {
            return;
        }

        int style = U(r, 6), usage = U(r, 8);
        byte[] bmiArr, bitsArr;
        if (style == 3)
        {
            if (!TryBitmap16(r, 10, 20, out bmiArr, out bitsArr, out _, out _))
            {
                SolidFallback();
                return;
            }

            MakePatternBrush(bmiArr, bitsArr, bitsArr.Length > 0 && bmiArr[14] == 1);
            return;
        }

        if (!TryDib(r, 10, usage, out ReadOnlySpan<byte> bmi, out ReadOnlySpan<byte> bits))
        {
            SolidFallback();
            return;
        }

        MakePatternBrush(bmi, bits, false);
    }

    private void CreatePatternBrush(ReadOnlySpan<byte> r)
    {
        // Bitmap16 header (14 bytes: Type Width Height WidthBytes Planes BitsPixel, 4 ignored bytes), 18 reserved bytes, pattern bits
        if (!Need(r, 6 + 32))
        {
            return;
        }

        if (!TryBitmap16(r, 6, 38, out byte[] bmi, out byte[] bits, out _, out _))
        {
            SolidFallback();
            return;
        }

        MakePatternBrush(bmi, bits, bmi[14] == 1);
    }

    private void SolidFallback()
    {
        int slot = AllocateSlot(Kind.Brush);
        if (slot >= 0)
        {
            Ints(39, Hnd(slot), 0, 0x808080, 0);
        }
    }

    private void MakePatternBrush(ReadOnlySpan<byte> bmi, ReadOnlySpan<byte> bits, bool mono)
    {
        int slot = AllocateSlot(Kind.Brush);
        if (slot < 0)
        {
            return;
        }

        int bmiPad = (bmi.Length + 3) & ~3, bitsPad = (bits.Length + 3) & ~3;
        Span<byte> s = Rec(mono ? 93u : 94u, 32 + bmiPad + bitsPad);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], Hnd(slot));
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], bmi.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], 32 + bmiPad);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], bits.Length);
        bmi.CopyTo(s[32..]);
        bits.CopyTo(s[(32 + bmiPad)..]);
    }

    // ---- escapes -----------------------------------------------------------------------------------------------------------

    private void Escape(ReadOnlySpan<byte> r)
    {
        if (!Need(r, 10))
        {
            return;
        }

        int escape = U(r, 6);
        if (escape == 0x000F)
        {
            Quiet("An embedded-metafile comment");
            return;
        }

        if (_reported.Add(0x10000 | escape))
        {
            Note(DiagnosticSeverity.Info, _wmfRecord, _function, $"The escape 0x{escape:X4} has no effect on the picture and is ignored.");
        }
    }
}
