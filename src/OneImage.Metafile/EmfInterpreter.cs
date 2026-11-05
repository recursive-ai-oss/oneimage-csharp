using System.Buffers.Binary;
using System.Numerics;

namespace OneImage.Metafile;

/// <summary>A graphics object in the file's object table.</summary>
internal abstract record GdiObject;

/// <summary>A pen as created: the raw GDI style, the width in logical units and the brush it paints with.</summary>
internal sealed record PenObj(uint Style, double Width, Rgba32 Color, bool Legacy, BrushObj? Brush, double[]? UserDashes) : GdiObject;

/// <summary>A brush as created. <see cref="Kind"/>: 0 solid, 1 null, 2 hatch, 3 pattern bitmap.</summary>
internal sealed record BrushObj(int Kind, Rgba32 Color, HatchKind Hatch, DeviceIndependentBitmap? Bitmap) : GdiObject;

internal sealed record FontObj(FontSpec Spec) : GdiObject;

/// <summary>An object that does not affect drawing here (palette, color space).</summary>
internal sealed record InertObj : GdiObject;

/// <summary>The device-context attributes that SaveDC and RestoreDC keep.</summary>
internal sealed class Dc
{
    public PenObj? Pen;
    public BrushObj? Brush;
    public FontObj? Font;
    public int MapMode = 1;
    public double WOrgX, WOrgY, WExtX = 1, WExtY = 1, VOrgX, VOrgY, VExtX = 1, VExtY = 1;
    public Mat World = Mat.Identity;
    public int BkMode = 2;
    public Rgba32 BkColor = new(255, 255, 255, 255);
    public Rgba32 TextColor = new(0, 0, 0, 255);
    public uint TextAlign;
    public MixMode Rop2 = MixMode.CopyPen;
    public int PolyFillMode = 1;
    public StretchMode Stretch = StretchMode.BlackOnWhite;
    public int ArcDirection = 1;
    public double MiterLimit = 10;
    public double BrushOrgX, BrushOrgY;
    public double CurX, CurY;
    public int CharExtra;
    public double BreakExtra;
    public int BreakCount;

    public Dc Clone() => (Dc)MemberwiseClone();
}

internal sealed partial class EmfInterpreter
{
    private const int HeaderMin = 88;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly MetafileLimits _lim;
    private readonly MetafileReadOptions _options;
    private readonly CancellationToken _cancel;
    private readonly List<DrawingCommand> _out = [];
    private readonly Dictionary<uint, GdiObject> _objects = [];
    private readonly Stack<(Dc State, Mat Emitted)> _saved = new();
    private readonly HashSet<int> _quietReported = [];
    private readonly MetafilePathBuilder _bracket;
    private Dc _dc = new();
    private Mat _emitted = Mat.Identity;
    private int _record;
    private int _recordType;
    private long _work;
    private long _bitmapPixels;
    private bool _inPath;
    private MetafilePath? _closedPath;
    private double _pxPerMmX = 1, _pxPerMmY = 1;

    public EmfInterpreter(ReadOnlyMemory<byte> data, MetafileReadOptions options, CancellationToken cancel)
    {
        _data = data;
        _options = options;
        _lim = options.Limits;
        _cancel = cancel;
        _bracket = new MetafilePathBuilder(_lim.MaxPathPoints);
        Info = ReadInfo(data, _lim);
    }

    public MetafileInfo Info { get; private set; }

    public static MetafileInfo ReadInfo(ReadOnlyMemory<byte> data, MetafileLimits limits)
    {
        ReadOnlySpan<byte> d = data.Span;
        if (!MetafileDocument.IsEmf(d))
        {
            throw new ImageFormatException("The data is not an enhanced metafile (no EMR_HEADER with the ' EMF' signature).");
        }

        uint hsize = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
        if (hsize < HeaderMin || hsize > d.Length || (hsize & 3) != 0)
        {
            throw new ImageFormatException("The EMF header record has an invalid size.");
        }

        var bounds = new RectD(I32(d, 8), I32(d, 12), I32(d, 16), I32(d, 20));
        var frame = new RectD(I32(d, 24), I32(d, 28), I32(d, 32), I32(d, 36));
        int records = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d[52..]), int.MaxValue);
        uint nDesc = BinaryPrimitives.ReadUInt32LittleEndian(d[60..]);
        uint offDesc = BinaryPrimitives.ReadUInt32LittleEndian(d[64..]);
        int devW = I32(d, 72), devH = I32(d, 76), mmW = I32(d, 80), mmH = I32(d, 84);
        double mmw = mmW, mmh = mmH;
        if (hsize >= 108)
        {
            uint umx = BinaryPrimitives.ReadUInt32LittleEndian(d[100..]);
            uint umy = BinaryPrimitives.ReadUInt32LittleEndian(d[104..]);
            if (umx > 0 && umy > 0)
            {
                mmw = umx / 1000.0;
                mmh = umy / 1000.0;
            }
        }

        // A reference device that does not make sense is replaced by a 96 dpi display, so that mapping modes still give sizes.
        if (devW <= 0 || devH <= 0 || mmw <= 0 || mmh <= 0)
        {
            devW = 1024;
            devH = 768;
            mmw = 1024 * 25.4 / 96;
            mmh = 768 * 25.4 / 96;
        }

        string description = string.Empty;
        if (nDesc > 0 && nDesc <= 4096 && (ulong)offDesc + ((ulong)nDesc * 2) <= hsize)
        {
            description = System.Text.Encoding.Unicode.GetString(d.Slice((int)offDesc, (int)nDesc * 2)).Replace('\0', '|').TrimEnd('|');
        }

        double ppmX = devW / mmw, ppmY = devH / mmh;
        RectD picture;
        if (frame.Right > frame.Left && frame.Bottom > frame.Top)
        {
            picture = new RectD(frame.Left / 100 * ppmX, frame.Top / 100 * ppmY, frame.Right / 100 * ppmX, frame.Bottom / 100 * ppmY);
        }
        else
        {
            RectD nb = bounds.Normalized;
            picture = new RectD(nb.Left, nb.Top, nb.Right + 1, nb.Bottom + 1);
        }

        (bool plus, bool plusOnly) = DetectEmfPlus(d, (int)hsize);
        return new MetafileInfo(MetafileKind.Emf, bounds, frame, devW, devH, mmw, mmh, picture, description, records, plus, plusOnly);
    }

    private static (bool HasPlus, bool PlusOnly) DetectEmfPlus(ReadOnlySpan<byte> d, int off)
    {
        // The first record after the header is a comment starting with "EMF+" when EMF+ records follow. An EMF+ header record inside carries a
        // flag that tells whether the file is "dual" (it also has GDI records).
        if (off + 12 <= d.Length && BinaryPrimitives.ReadUInt32LittleEndian(d[off..]) == 70)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d[(off + 4)..]);
            if (size >= 16 && off + size <= d.Length && BinaryPrimitives.ReadUInt32LittleEndian(d[(off + 12)..]) == 0x2B464D45)
            {
                // The EMF+ header record: Type(2) Flags(2) Size(4) DataSize(4) Version(4) EmfPlusFlags(4); bit 0 of the flags is "dual".
                bool dual = false;
                if (size >= 16 + 12 + 8 + 4)
                {
                    uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d[(off + 16 + 4 + 12)..]);
                    dual = (flags & 1) != 0;
                }

                return (true, !dual);
            }
        }

        return (false, false);
    }

    private static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);

    public List<DrawingCommand> Run()
    {
        _pxPerMmX = Info.DeviceWidth / Info.DeviceWidthMillimeters;
        _pxPerMmY = Info.DeviceHeight / Info.DeviceHeightMillimeters;
        _out.Add(new BeginPictureCommand(Info));
        ReadOnlyMemory<byte> all = _data;
        int off = 0;
        int length = all.Length;
        while (off + 8 <= length)
        {
            _cancel.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> head = all.Span.Slice(off, 8);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(head);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            if (size < 8 || (size & 3) != 0)
            {
                Diag(DiagnosticSeverity.Error, $"Record {_record} (type {type}) has an invalid size {size}; the rest of the file is ignored.", (int)type);
                break;
            }

            if ((ulong)off + size > (ulong)length)
            {
                Diag(DiagnosticSeverity.Warning, $"Record {_record} (type {type}) runs past the end of the file; the file is truncated.", (int)type);
                break;
            }

            if (_record >= _lim.MaxRecords)
            {
                throw new ImageLimitException($"The metafile has more than {_lim.MaxRecords} records.");
            }

            AddWork(1);
            _recordType = (int)type;
            ReadOnlyMemory<byte> rec = all.Slice(off, (int)size);
            if (type == 14)
            {
                break;
            }

            if (_record == 0)
            {
                if (type != 1)
                {
                    break;
                }

                InitFromHeader();
            }
            else
            {
                Dispatch(type, rec);
            }

            _record++;
            off += (int)size;
        }

        if (_inPath)
        {
            Diag(DiagnosticSeverity.Warning, "The file ended inside a path bracket.");
        }

        while (_saved.Count > 0)
        {
            // Leave the stream balanced for consumers.
            _saved.Pop();
            Emit(new RestoreStateCommand());
        }

        return _out;
    }

    private void InitFromHeader()
    {
        if (Info.HasEmfPlus)
        {
            Diag(
                Info.IsEmfPlusOnly ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                Info.IsEmfPlusOnly
                    ? "The file holds EMF+ records only; they are not interpreted yet, so the picture is empty."
                    : "The file holds EMF+ records and GDI fallback records; the GDI records are drawn and the EMF+ records are not interpreted yet.");
        }
    }

    // ---- bookkeeping -------------------------------------------------------------------------------------------------

    private void AddWork(long n)
    {
        _work += n;
        if (_work > _lim.MaxWork)
        {
            throw new ImageLimitException("The metafile asks for more work than the limit allows.");
        }
    }

    private void Emit(DrawingCommand c)
    {
        if (_out.Count >= _lim.MaxCommands)
        {
            throw new ImageLimitException($"The metafile produces more than {_lim.MaxCommands} drawing commands.");
        }

        _out.Add(c with { RecordIndex = _record });
    }

    private void Diag(DiagnosticSeverity severity, string message, int recordType = -1)
    {
        if (severity != DiagnosticSeverity.Info || _out.Count < _lim.MaxCommands)
        {
            _out.Add(new DiagnosticCommand(severity, message, recordType < 0 ? _recordType : recordType) { RecordIndex = _record });
        }
    }

    /// <summary>Reports a record that has no effect on the picture here, once per record type.</summary>
    private void Quiet(string what)
    {
        if (_quietReported.Add(_recordType))
        {
            Diag(DiagnosticSeverity.Info, $"{what} (record type {_recordType}) has no effect on the picture and is ignored.");
        }
    }

    private void Unsupported(string what) =>
        Diag(DiagnosticSeverity.Warning, $"{what} (record type {_recordType}, record {_record}) is not supported; it is skipped.");

    private void Truncated(string name) =>
        Diag(DiagnosticSeverity.Warning, $"Record {_record} ({name}) is shorter than its fields need; it is skipped.");

    // ---- field readers -----------------------------------------------------------------------------------------------

    private static int Int(ReadOnlyMemory<byte> r, int o) => BinaryPrimitives.ReadInt32LittleEndian(r.Span[o..]);

    private static uint UInt(ReadOnlyMemory<byte> r, int o) => BinaryPrimitives.ReadUInt32LittleEndian(r.Span[o..]);

    private static float Float(ReadOnlyMemory<byte> r, int o) => BinaryPrimitives.ReadSingleLittleEndian(r.Span[o..]);

    private static Rgba32 Color(uint c) => new((byte)c, (byte)(c >> 8), (byte)(c >> 16), 255);

    // ---- transform ---------------------------------------------------------------------------------------------------

    /// <summary>The transform from logical coordinates to device units of the picture.</summary>
    private Mat Ctm()
    {
        Dc d = _dc;
        double sx = d.WExtX == 0 ? 1 : d.VExtX / d.WExtX;
        double sy = d.WExtY == 0 ? 1 : d.VExtY / d.WExtY;
        if (d.MapMode == 7)
        {
            // Isotropic: both axes use the smaller scale, keeping each sign.
            double m = Math.Min(Math.Abs(sx), Math.Abs(sy));
            sx = Math.Sign(sx == 0 ? 1 : sx) * m;
            sy = Math.Sign(sy == 0 ? 1 : sy) * m;
        }

        Mat page = d.World;
        Mat toDevice = Mat.Translate(-d.WOrgX, -d.WOrgY).Then(Mat.Scale(sx, sy)).Then(Mat.Translate(d.VOrgX, d.VOrgY));
        return page.Then(toDevice);
    }

    private void EnsureTransform()
    {
        Mat m = Ctm();
        if (!m.Equals(_emitted))
        {
            Emit(new SetTransformCommand(m.ToMatrix3x2()));
            _emitted = m;
        }
    }

    private void SetMapMode(int mode)
    {
        Dc d = _dc;
        if (mode < 1 || mode > 8)
        {
            Diag(DiagnosticSeverity.Warning, $"Unknown mapping mode {mode} is ignored.");
            return;
        }

        d.MapMode = mode;
        if (mode >= 7)
        {
            return; // extents stay as they are
        }

        double unitMm = mode switch
        {
            2 => 0.1,
            3 => 0.01,
            4 => 0.254,
            5 => 0.0254,
            6 => 25.4 / 1440,
            _ => 0,
        };
        if (mode == 1)
        {
            (d.WExtX, d.WExtY, d.VExtX, d.VExtY) = (1, 1, 1, 1);
        }
        else
        {
            (d.WExtX, d.WExtY) = (1, 1);
            d.VExtX = unitMm * _pxPerMmX;
            d.VExtY = -unitMm * _pxPerMmY;
        }
    }

    /// <summary>Device-space shift that moves a point by half a device pixel, expressed in logical units.</summary>
    private static (double X, double Y) HalfPixelInLogical(Mat m)
    {
        if (!m.IsInvertible || !m.IsAxisAligned)
        {
            return (0, 0);
        }

        return m.InverseVector(0.5, 0.5);
    }

    // ---- dispatch ----------------------------------------------------------------------------------------------------

    private void Dispatch(uint type, ReadOnlyMemory<byte> r)
    {
        switch (type)
        {
            case 2: PolyBezier(r, false, false); break;
            case 3: PolyPoly(r, false, true); break;
            case 4: PolyPoly(r, false, false); break;
            case 5: PolyBezier(r, true, false); break;
            case 6: PolyLineTo(r, false); break;
            case 7: PolyPolyRecord(r, false, false); break;
            case 8: PolyPolyRecord(r, false, true); break;
            case 9: if (Need(r, 16, "SETWINDOWEXTEX")) { SetExt(true, Int(r, 8), Int(r, 12)); } break;
            case 10: if (Need(r, 16, "SETWINDOWORGEX")) { (_dc.WOrgX, _dc.WOrgY) = (Int(r, 8), Int(r, 12)); } break;
            case 11: if (Need(r, 16, "SETVIEWPORTEXTEX")) { SetExt(false, Int(r, 8), Int(r, 12)); } break;
            case 12: if (Need(r, 16, "SETVIEWPORTORGEX")) { (_dc.VOrgX, _dc.VOrgY) = (Int(r, 8), Int(r, 12)); } break;
            case 13: if (Need(r, 16, "SETBRUSHORGEX")) { (_dc.BrushOrgX, _dc.BrushOrgY) = (Int(r, 8), Int(r, 12)); } break;
            case 15: if (Need(r, 20, "SETPIXELV")) { SetPixel(Int(r, 8), Int(r, 12), Color(UInt(r, 16))); } break;
            case 16: Quiet("The font mapper flags"); break;
            case 17: if (Need(r, 12, "SETMAPMODE")) { SetMapMode(Int(r, 8)); } break;
            case 18: if (Need(r, 12, "SETBKMODE")) { _dc.BkMode = Int(r, 8); } break;
            case 19: if (Need(r, 12, "SETPOLYFILLMODE")) { _dc.PolyFillMode = Int(r, 8) == 2 ? 2 : 1; } break;
            case 20: if (Need(r, 12, "SETROP2")) { SetRop2(Int(r, 8)); } break;
            case 21: if (Need(r, 12, "SETSTRETCHBLTMODE")) { SetStretch(Int(r, 8)); } break;
            case 22: if (Need(r, 12, "SETTEXTALIGN")) { _dc.TextAlign = UInt(r, 8); } break;
            case 23: Quiet("The color adjustment"); break;
            case 24: if (Need(r, 12, "SETTEXTCOLOR")) { _dc.TextColor = Color(UInt(r, 8)); } break;
            case 25: if (Need(r, 12, "SETBKCOLOR")) { _dc.BkColor = Color(UInt(r, 8)); } break;
            case 26: if (Need(r, 16, "OFFSETCLIPRGN")) { OffsetClip(Int(r, 8), Int(r, 12)); } break;
            case 27: if (Need(r, 16, "MOVETOEX")) { MoveTo(Int(r, 8), Int(r, 12)); } break;
            case 28: Unsupported("SetMetaRgn"); break;
            case 29: if (Need(r, 24, "EXCLUDECLIPRECT")) { ClipRect(ClipMode.Exclude, r); } break;
            case 30: if (Need(r, 24, "INTERSECTCLIPRECT")) { ClipRect(ClipMode.Intersect, r); } break;
            case 31: if (Need(r, 24, "SCALEVIEWPORTEXTEX")) { ScaleExt(false, r); } break;
            case 32: if (Need(r, 24, "SCALEWINDOWEXTEX")) { ScaleExt(true, r); } break;
            case 33: SaveDc(); break;
            case 34: if (Need(r, 12, "RESTOREDC")) { RestoreDc(Int(r, 8)); } break;
            case 35: if (Need(r, 32, "SETWORLDTRANSFORM")) { _dc.World = ReadXform(r, 8); } break;
            case 36: if (Need(r, 36, "MODIFYWORLDTRANSFORM")) { ModifyWorld(ReadXform(r, 8), UInt(r, 32)); } break;
            case 37: if (Need(r, 12, "SELECTOBJECT")) { SelectObject(UInt(r, 8)); } break;
            case 38: CreatePen(r); break;
            case 39: CreateBrushIndirect(r); break;
            case 40: if (Need(r, 12, "DELETEOBJECT")) { _objects.Remove(UInt(r, 8)); } break;
            case 41: AngleArc(r); break;
            case 42: Box(r, "ELLIPSE", 0); break;
            case 43: Box(r, "RECTANGLE", 1); break;
            case 44: Box(r, "ROUNDRECT", 2); break;
            case 45: ArcRecord(r, 0); break;
            case 46: ArcRecord(r, 1); break;
            case 47: ArcRecord(r, 2); break;
            case 48: case 49: case 50: case 51: case 52: case 111:
                if (type == 49 && Need(r, 12, "CREATEPALETTE")) { _objects[UInt(r, 8)] = new InertObj(); }
                Quiet("Palette handling");
                break;
            case 53: Unsupported("ExtFloodFill"); break;
            case 54: if (Need(r, 16, "LINETO")) { LineTo(Int(r, 8), Int(r, 12)); } break;
            case 55: ArcRecord(r, 3); break;
            case 56: PolyDraw(r, false); break;
            case 57: if (Need(r, 12, "SETARCDIRECTION")) { _dc.ArcDirection = Int(r, 8) == 2 ? 2 : 1; } break;
            case 58: if (Need(r, 12, "SETMITERLIMIT")) { _dc.MiterLimit = Math.Max(1, UInt(r, 8) > 0x40000000 ? Float(r, 8) : UInt(r, 8)); } break;
            case 59: BeginPath(); break;
            case 60: EndPath(); break;
            case 61: CloseFigure(); break;
            case 62: PathPaint(true, false); break;
            case 63: PathPaint(true, true); break;
            case 64: PathPaint(false, true); break;
            case 65: if (!_inPath) { Diag(DiagnosticSeverity.Info, "FlattenPath has no effect: paths are kept as curves and flattened when drawn."); } break;
            case 66: WidenPathRecord(); break;
            case 67: if (Need(r, 12, "SELECTCLIPPATH")) { SelectClipPath(Int(r, 8)); } break;
            case 68: AbortPath(); break;
            case 70: Comment(r); break;
            case 71: RegionRecord(r, 0); break;
            case 72: RegionRecord(r, 1); break;
            case 73: RegionRecord(r, 2); break;
            case 74: RegionRecord(r, 3); break;
            case 75: ExtSelectClipRgn(r); break;
            case 76: BitBlt(r, 0); break;
            case 77: BitBlt(r, 1); break;
            case 78: BitBlt(r, 2); break;
            case 79: PlgBlt(r); break;
            case 80: SetDibitsToDevice(r); break;
            case 81: StretchDibits(r); break;
            case 82: ExtCreateFont(r); break;
            case 83: ExtTextOut(r, false); break;
            case 84: ExtTextOut(r, true); break;
            case 85: PolyBezier(r, false, true); break;
            case 86: PolyPoly(r, true, true); break;
            case 87: PolyPoly(r, true, false); break;
            case 88: PolyBezier(r, true, true); break;
            case 89: PolyLineTo(r, true); break;
            case 90: PolyPolyRecord(r, true, false); break;
            case 91: PolyPolyRecord(r, true, true); break;
            case 92: PolyDraw(r, true); break;
            case 93: CreateMonoBrush(r); break;
            case 94: CreateDibPatternBrush(r); break;
            case 95: ExtCreatePen(r); break;
            case 96: PolyTextOut(r, false); break;
            case 97: PolyTextOut(r, true); break;
            case 98: case 112: case 113: Quiet("Image color management settings"); break;
            case 99: case 122: if (Need(r, 12, "CREATECOLORSPACE")) { _objects[UInt(r, 8)] = new InertObj(); } Quiet("A color space"); break;
            case 100: case 101: case 121: Quiet("A color space"); break;
            case 102: case 103: Unsupported("An OpenGL record"); break;
            case 104: Quiet("The pixel format"); break;
            case 105: case 106: case 110: Quiet("A driver escape"); break;
            case 108: SmallTextOut(r); break;
            case 109: case 119: Quiet("Font mapping information"); break;
            case 114: AlphaOrTransparent(r, true); break;
            case 115: if (Need(r, 12, "SETLAYOUT") && (UInt(r, 8) & 1) != 0) { Unsupported("Right-to-left layout"); } break;
            case 116: AlphaOrTransparent(r, false); break;
            case 118: GradientFill(r); break;
            case 120: if (Need(r, 16, "SETTEXTJUSTIFICATION")) { (_dc.BreakExtra, _dc.BreakCount) = (Int(r, 8), Int(r, 12)); } break;
            default:
                Diag(DiagnosticSeverity.Warning, $"Unknown record type {type} (record {_record}, {r.Length} bytes) is skipped.", (int)type);
                break;
        }
    }

    private bool Need(ReadOnlyMemory<byte> r, int bytes, string name)
    {
        if (r.Length >= bytes)
        {
            return true;
        }

        Truncated(name);
        return false;
    }

    private static Mat ReadXform(ReadOnlyMemory<byte> r, int o) =>
        new(Float(r, o), Float(r, o + 4), Float(r, o + 8), Float(r, o + 12), Float(r, o + 16), Float(r, o + 20));

    private void SetExt(bool window, int x, int y)
    {
        Dc d = _dc;
        if (d.MapMode < 7)
        {
            return; // GDI refuses to change the extents of a fixed mapping mode
        }

        if (window)
        {
            (d.WExtX, d.WExtY) = (x, y);
        }
        else
        {
            (d.VExtX, d.VExtY) = (x, y);
        }
    }

    private void ScaleExt(bool window, ReadOnlyMemory<byte> r)
    {
        Dc d = _dc;
        if (d.MapMode < 7)
        {
            return;
        }

        int xn = Int(r, 8), xd = Int(r, 12), yn = Int(r, 16), yd = Int(r, 20);
        if (xd == 0 || yd == 0)
        {
            return;
        }

        if (window)
        {
            (d.WExtX, d.WExtY) = (d.WExtX * xn / xd, d.WExtY * yn / yd);
        }
        else
        {
            (d.VExtX, d.VExtY) = (d.VExtX * xn / xd, d.VExtY * yn / yd);
        }
    }

    private void SetRop2(int v)
    {
        if (v is >= 1 and <= 16)
        {
            _dc.Rop2 = (MixMode)v;
        }
    }

    private void SetStretch(int v)
    {
        _dc.Stretch = v switch
        {
            2 => StretchMode.WhiteOnBlack,
            3 => StretchMode.ColorOnColor,
            4 => StretchMode.Halftone,
            _ => StretchMode.BlackOnWhite,
        };
    }

    private void ModifyWorld(Mat x, uint mode)
    {
        _dc.World = mode switch
        {
            1 => Mat.Identity,
            2 => x.Then(_dc.World),
            3 => _dc.World.Then(x),
            4 => x,
            _ => _dc.World,
        };
        if (mode is < 1 or > 4)
        {
            Diag(DiagnosticSeverity.Warning, $"Unknown world transform mode {mode} is ignored.");
        }
    }

    private void SaveDc()
    {
        if (_saved.Count >= _lim.MaxSaveDepth)
        {
            throw new ImageLimitException($"Saved device-context states are nested deeper than {_lim.MaxSaveDepth}.");
        }

        _saved.Push((_dc.Clone(), _emitted));
        Emit(new SaveStateCommand());
    }

    private void RestoreDc(int n)
    {
        // Negative: relative (-1 is the most recent save). Positive: absolute index of a save.
        int pops = n < 0 ? -n : _saved.Count - n + 1;
        if (n == 0 || pops < 1 || pops > _saved.Count)
        {
            Diag(DiagnosticSeverity.Warning, $"RestoreDC({n}) does not match a saved state; it is ignored.");
            return;
        }

        for (int i = 0; i < pops; i++)
        {
            (Dc state, Mat emitted) = _saved.Pop();
            Emit(new RestoreStateCommand());
            if (i == pops - 1)
            {
                _dc = state;
                _emitted = emitted;
            }
        }
    }

    private void SelectObject(uint index)
    {
        if ((index & 0x80000000) != 0)
        {
            SelectStock((int)(index & 0x7FFFFFFF));
            return;
        }

        if (!_objects.TryGetValue(index, out GdiObject? o))
        {
            Diag(DiagnosticSeverity.Warning, $"SelectObject names object {index}, which does not exist; the selection is ignored.");
            return;
        }

        switch (o)
        {
            case PenObj p: _dc.Pen = p; break;
            case BrushObj b: _dc.Brush = b; break;
            case FontObj f: _dc.Font = f; break;
        }
    }

    private void SelectStock(int id)
    {
        switch (id)
        {
            case 0: _dc.Brush = new BrushObj(0, new Rgba32(255, 255, 255, 255), 0, null); break;
            case 1: _dc.Brush = new BrushObj(0, new Rgba32(192, 192, 192, 255), 0, null); break;
            case 2: _dc.Brush = new BrushObj(0, new Rgba32(128, 128, 128, 255), 0, null); break;
            case 3: _dc.Brush = new BrushObj(0, new Rgba32(64, 64, 64, 255), 0, null); break;
            case 4: _dc.Brush = new BrushObj(0, new Rgba32(0, 0, 0, 255), 0, null); break;
            case 5: _dc.Brush = new BrushObj(1, default, 0, null); break;
            case 6: _dc.Pen = new PenObj(0, 0, new Rgba32(255, 255, 255, 255), true, null, null); break;
            case 7: _dc.Pen = new PenObj(0, 0, new Rgba32(0, 0, 0, 255), true, null, null); break;
            case 8: _dc.Pen = new PenObj(5, 0, default, true, null, null); break;
            case 10: _dc.Font = new FontObj(new FontSpec("Terminal", 12, Weight: 400, PitchAndFamily: 1)); break;
            case 11: _dc.Font = new FontObj(new FontSpec("Courier", 12, PitchAndFamily: 1)); break;
            case 12: _dc.Font = new FontObj(new FontSpec("MS Sans Serif", 12)); break;
            case 13: case 14: _dc.Font = new FontObj(new FontSpec("System", 16, Weight: 700)); break;
            case 16: _dc.Font = new FontObj(new FontSpec("Fixedsys", 15, PitchAndFamily: 1)); break;
            case 17: _dc.Font = new FontObj(new FontSpec("Segoe UI", -12)); break;
            case 15: break;
            case 18:
                // The device-context brush and pen take their color from a call that is not recorded; the default is white and black.
                _dc.Brush = new BrushObj(0, new Rgba32(255, 255, 255, 255), 0, null);
                Diag(DiagnosticSeverity.Info, "The DC brush stock object is selected; its color is white because no record can set it.");
                break;
            case 19:
                _dc.Pen = new PenObj(0, 0, new Rgba32(0, 0, 0, 255), true, null, null);
                Diag(DiagnosticSeverity.Info, "The DC pen stock object is selected; its color is black because no record can set it.");
                break;
            default:
                Diag(DiagnosticSeverity.Warning, $"Unknown stock object {id} is ignored.");
                break;
        }
    }

    private bool StoreObject(uint index, GdiObject o)
    {
        if (index == 0 || index >= _lim.MaxObjects)
        {
            Diag(DiagnosticSeverity.Warning, $"Object index {index} is outside 1..{_lim.MaxObjects - 1}; the object is not created.");
            return false;
        }

        _objects[index] = o;
        return true;
    }

    private void CreatePen(ReadOnlyMemory<byte> r)
    {
        // ihPen, LogPen { PenStyle, Width (POINTL), ColorRef }
        if (!Need(r, 28, "CREATEPEN"))
        {
            return;
        }

        uint style = UInt(r, 12);
        double width = Math.Abs(Int(r, 16));
        StoreObject(UInt(r, 8), new PenObj(style, width, Color(UInt(r, 24)), true, null, null));
    }

    private void CreateBrushIndirect(ReadOnlyMemory<byte> r)
    {
        if (!Need(r, 24, "CREATEBRUSHINDIRECT"))
        {
            return;
        }

        StoreObject(UInt(r, 8), LogBrush(UInt(r, 12), UInt(r, 16), UInt(r, 20)));
    }

    private static BrushObj LogBrush(uint style, uint color, uint hatch) => style switch
    {
        0 => new BrushObj(0, Color(color), 0, null),
        1 => new BrushObj(1, default, 0, null),
        2 => new BrushObj(2, Color(color), hatch <= 5 ? (HatchKind)hatch : HatchKind.Horizontal, null),
        _ => new BrushObj(0, Color(color), 0, null),
    };

    private void CreateMonoBrush(ReadOnlyMemory<byte> r)
    {
        // ihBrush, Usage, offBmi, cbBmi, offBits, cbBits
        if (!Need(r, 32, "CREATEMONOBRUSH"))
        {
            return;
        }

        DeviceIndependentBitmap? dib = ParseDib(r, UInt(r, 16), UInt(r, 20), UInt(r, 24), UInt(r, 28));
        StoreObject(UInt(r, 8), dib is null ? new BrushObj(0, new Rgba32(128, 128, 128, 255), 0, null) : new BrushObj(4, default, 0, dib));
    }

    private void CreateDibPatternBrush(ReadOnlyMemory<byte> r)
    {
        if (!Need(r, 32, "CREATEDIBPATTERNBRUSHPT"))
        {
            return;
        }

        DeviceIndependentBitmap? dib = ParseDib(r, UInt(r, 16), UInt(r, 20), UInt(r, 24), UInt(r, 28));
        StoreObject(UInt(r, 8), dib is null ? new BrushObj(0, new Rgba32(128, 128, 128, 255), 0, null) : new BrushObj(3, default, 0, dib));
    }

    private void ExtCreatePen(ReadOnlyMemory<byte> r)
    {
        // ihPen, offBmi, cbBmi, offBits, cbBits, elp { PenStyle, Width, BrushStyle, ColorRef, BrushHatch, NumEntries, EntryStyle[] }
        if (!Need(r, 52, "EXTCREATEPEN"))
        {
            return;
        }

        uint style = UInt(r, 28);
        double width = UInt(r, 32);
        uint brushStyle = UInt(r, 36);
        Rgba32 color = Color(UInt(r, 40));
        uint hatch = UInt(r, 44);
        uint entries = UInt(r, 48);
        double[]? dashes = null;
        if ((style & 0xF) == 7)
        {
            if (entries > _lim.MaxDashEntries || 52 + (ulong)entries * 4 > (ulong)r.Length)
            {
                Diag(DiagnosticSeverity.Warning, "A pen with a user style has an impossible number of dash entries; it is drawn solid.");
            }
            else
            {
                dashes = new double[entries];
                for (int i = 0; i < entries; i++)
                {
                    dashes[i] = UInt(r, 52 + (i * 4));
                }
            }
        }

        BrushObj? brush;
        if (brushStyle is 3 or 5 or 6 or 8 or 9)
        {
            DeviceIndependentBitmap? dib = ParseDib(r, UInt(r, 12), UInt(r, 16), UInt(r, 20), UInt(r, 24));
            brush = dib is null ? null : new BrushObj(3, default, 0, dib);
        }
        else
        {
            brush = brushStyle == 1 ? new BrushObj(1, default, 0, null) : LogBrush(brushStyle, UInt(r, 40), hatch);
        }

        StoreObject(UInt(r, 8), new PenObj(style, width, color, false, brush, dashes));
    }

    private void ExtCreateFont(ReadOnlyMemory<byte> r)
    {
        // ihFont, then a LOGFONT (92 bytes), possibly followed by the longer LOGFONTEX forms.
        if (!Need(r, 8 + 92, "EXTCREATEFONTINDIRECTW"))
        {
            return;
        }

        int h = Int(r, 12), w = Int(r, 16), esc = Int(r, 20), ori = Int(r, 24), weight = Int(r, 28);
        ReadOnlySpan<byte> s = r.Span;
        bool italic = s[32] != 0, underline = s[33] != 0, strike = s[34] != 0;
        int charset = s[35], quality = s[38], pitch = s[39];
        int len = 0;
        while (len < 32 && BinaryPrimitives.ReadUInt16LittleEndian(s[(40 + (len * 2))..]) != 0)
        {
            len++;
        }

        string face = System.Text.Encoding.Unicode.GetString(s.Slice(40, len * 2));
        StoreObject(UInt(r, 8), new FontObj(new FontSpec(face, h, w, weight, italic, underline, strike, esc, ori, charset, pitch, quality)));
    }

    private DeviceIndependentBitmap? ParseDib(ReadOnlyMemory<byte> r, uint offBmi, uint cbBmi, uint offBits, uint cbBits)
    {
        DeviceIndependentBitmap? dib = DibDecoder.TryParse(r, offBmi, cbBmi, offBits, cbBits, _lim, out string? error);
        if (dib is null)
        {
            Diag(DiagnosticSeverity.Warning, $"A bitmap in record {_record} is skipped: {error}.");
            return null;
        }

        _bitmapPixels += (long)dib.Width * dib.Height;
        if (_bitmapPixels > _lim.MaxTotalBitmapPixels)
        {
            throw new ImageLimitException("The bitmaps of the metafile have more pixels in total than the limit allows.");
        }

        AddWork(((long)dib.Width * dib.Height / 64) + 1);
        return dib;
    }
}
