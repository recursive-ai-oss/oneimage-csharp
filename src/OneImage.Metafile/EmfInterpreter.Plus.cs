using System.Buffers.Binary;
using System.Numerics;

namespace OneImage.Metafile;

/// <summary>
/// EMF+ (GDI+) records. They live in EMR_COMMENT records; the interpreter walks them with its own graphics state (world, page and container
/// transforms, saved states) and emits the same command stream as for GDI records.
/// </summary>
internal sealed partial class EmfInterpreter
{
    /// <summary>
    /// GDI+ places the pixel grid so that integer coordinates are pixel centers unless the pixel offset mode is Half. Measured against
    /// Windows output, shapes drawn at integer coordinates cover the same pixels as ours with no shift, so this stays 0.
    /// </summary>
    private const double PlusPixelCenterShift = 0;

    private readonly PlusObj?[] _plusObjects = new PlusObj?[64];
    private readonly List<PlusFrame> _plusStack = [];
    private PlusGs _gs = new();
    private bool _plusMode;
    private bool _inGetDc;
    private bool _plusVideo = true;
    private double _plusDpiX = 96, _plusDpiY = 96;
    private (Dc Dc, Mat Emitted, int SavedDepth)? _getDcSaved;
    private readonly HashSet<string> _plusNotes = [];

    /// <summary>Gets how deep this interpreter is nested in metafiles embedded in EMF+ images (0 for a top-level file).</summary>
    internal int NestingDepth { get; init; }

    private sealed class PlusGs
    {
        public Mat World = Mat.Identity;
        public int PageUnit = 2;
        public double PageScale = 1;
        public Mat Base = Mat.Identity;
        public bool Smooth;
        public bool SourceCopy;
        public bool PixelHalf;
        public ImageInterpolation Interp = ImageInterpolation.Bilinear;
        public int TextHint;
        public int TextContrast = 4000;
        public int CompQuality;
        public Vector2 Origin;

        public PlusGs Clone() => (PlusGs)MemberwiseClone();
    }

    private readonly record struct PlusFrame(uint Index, bool IsContainer, PlusGs State, Mat Emitted);

    // ---- hooks used by the GDI side ----------------------------------------------------------------------------------

    /// <summary>Plays a GDI record, unless EMF+ is in charge: EMF+ players skip the GDI records except inside a GetDC section.</summary>
    private void DispatchGdi(uint type, ReadOnlyMemory<byte> rec)
    {
        if (_plusMode && !_inGetDc && type != 70)
        {
            return;
        }

        Dispatch(type, rec);
    }

    private void PlusFinish()
    {
        EndGetDc();
        while (_plusStack.Count > 0)
        {
            PopFrame();
        }
    }

    private void PlusNote(string key, DiagnosticSeverity severity, string message)
    {
        if (_plusNotes.Add(key))
        {
            Diag(severity, message);
        }
    }

    // ---- the record walker -------------------------------------------------------------------------------------------

    /// <summary>Walks the EMF+ records of one EMR_COMMENT (the data after the "EMF+" identifier).</summary>
    private void PlusComment(ReadOnlyMemory<byte> data)
    {
        int off = 0;
        while (off + 12 <= data.Length)
        {
            _cancel.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> h = data.Span.Slice(off, 12);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(h);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(h[2..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
            if (size < 12 || size > (uint)(data.Length - off) || dataSize > size - 12)
            {
                Diag(DiagnosticSeverity.Warning, $"An EMF+ record (type 0x{type:x4}) has an impossible size; the rest of the comment is skipped.");
                return;
            }

            AddWork(1);
            if (type >= 0x4001 && type != 0x4008 && type != 0x4004 && _inGetDc)
            {
                // The next EMF+ record after a GetDC section ends the GDI part.
                EndGetDc();
            }

            try
            {
                PlusRecord(type, flags, data.Slice(off + 12, (int)dataSize));
            }
            catch (PlusDataException e)
            {
                Diag(DiagnosticSeverity.Warning, $"EMF+ record 0x{type:x4} is damaged ({e.Message}); it is skipped.");
            }

            off += (int)size;
        }
    }

    private void PlusRecord(ushort type, ushort flags, ReadOnlyMemory<byte> d)
    {
        var r = new PlusReader(d);
        switch (type)
        {
            case 0x4001: PlusHeader(flags, ref r); break;
            case 0x4002: break; // end of file
            case 0x4003: break; // comment
            case 0x4004: BeginGetDc(); break;
            case 0x4005:
            case 0x4006:
            case 0x4007:
                PlusNote("multiformat", DiagnosticSeverity.Info, "EMF+ multi-format records are skipped.");
                break;
            case 0x4008: PlusObject(flags, d); break;
            case 0x4009: PlusClear(ref r); break;
            case 0x400A: FillRects(flags, ref r); break;
            case 0x400B: DrawRects(flags, ref r); break;
            case 0x400C: FillPolygon(flags, ref r); break;
            case 0x400D: DrawLines(flags, ref r); break;
            case 0x400E: FillEllipse(flags, ref r); break;
            case 0x400F: DrawEllipse(flags, ref r); break;
            case 0x4010: FillPie(flags, ref r); break;
            case 0x4011: DrawArcOrPie(flags, ref r, true); break;
            case 0x4012: DrawArcOrPie(flags, ref r, false); break;
            case 0x4013: FillRegion(flags, ref r); break;
            case 0x4014: FillPathRecord(flags, ref r); break;
            case 0x4015: DrawPathRecord(flags, ref r); break;
            case 0x4016: FillClosedCurve(flags, ref r); break;
            case 0x4017: DrawClosedCurve(flags, ref r); break;
            case 0x4018: DrawCurve(flags, ref r); break;
            case 0x4019: DrawBeziers(flags, ref r); break;
            case 0x401A: DrawImageRecord(flags, ref r); break;
            case 0x401B: DrawImagePoints(flags, ref r); break;
            case 0x401C: DrawStringRecord(flags, ref r); break;
            case 0x401D: _gs.Origin = new Vector2(r.I32(), r.I32()); EmitHints(); break;
            case 0x401E: _gs.Smooth = (flags & 1) != 0; EmitHints(); break;
            case 0x401F: _gs.TextHint = flags & 0xFF; EmitHints(); break;
            case 0x4020: _gs.TextContrast = flags & 0xFFF; EmitHints(); break;
            case 0x4021: _gs.Interp = (flags & 0xFF) is 0 or 1 or 5 ? ImageInterpolation.Nearest : ImageInterpolation.Bilinear; EmitHints(); break;
            case 0x4022: _gs.PixelHalf = (flags & 0xFF) is 3 or 4; EmitHints(); break;
            case 0x4023: _gs.SourceCopy = (flags & 0xFF) == 1; EmitHints(); break;
            case 0x4024: _gs.CompQuality = flags & 0xFF; EmitHints(); break;
            case 0x4025: PushFrame(r.U32(), false); break;
            case 0x4026: PopToFrame(r.U32()); break;
            case 0x4027: BeginContainer(flags, ref r); break;
            case 0x4028: BeginContainerNoParams(ref r); break;
            case 0x4029: PopToFrame(r.U32()); break;
            case 0x402A: _gs.World = ToMat(ReadMatrix(ref r)); break;
            case 0x402B: _gs.World = Mat.Identity; break;
            case 0x402C: ApplyWorld(ToMat(ReadMatrix(ref r)), (flags & 0x2000) != 0); break;
            case 0x402D: ApplyWorld(Mat.Translate(r.F32(), r.F32()), (flags & 0x2000) != 0); break;
            case 0x402E: ApplyWorld(Mat.Scale(r.F32(), r.F32()), (flags & 0x2000) != 0); break;
            case 0x402F:
                {
                    double a = r.F32() * Math.PI / 180;
                    double c = Math.Cos(a), s = Math.Sin(a);
                    ApplyWorld(new Mat(c, s, -s, c, 0, 0), (flags & 0x2000) != 0);
                    break;
                }

            case 0x4030: SetPageTransform(flags, ref r); break;
            case 0x4031: Emit(new ClipCommand(ClipMode.Reset, null)); break;
            case 0x4032: ClipRectRecord(flags, ref r); break;
            case 0x4033: ClipPathRecord(flags, ref r); break;
            case 0x4034: ClipRegionRecord(flags, ref r); break;
            case 0x4035: OffsetClipRecord(ref r); break;
            case 0x4036: DrawDriverStringRecord(flags, ref r); break;
            case 0x4037: Diag(DiagnosticSeverity.Warning, "EmfPlusStrokeFillPath has no defined layout; it is skipped.", 0x4037); break;
            case 0x4038: PlusNote("effects", DiagnosticSeverity.Info, "EMF+ image effects are stored but not applied."); break;
            case 0x4039:
            case 0x403A:
                PlusNote("tsg", DiagnosticSeverity.Info, "EMF+ terminal-services records have no effect on the picture and are skipped.");
                break;
            default:
                Diag(DiagnosticSeverity.Warning, $"Unknown EMF+ record type 0x{type:x4} is skipped.", type);
                break;
        }
    }

    private void PlusHeader(ushort flags, ref PlusReader r)
    {
        _plusMode = true;
        r.U32(); // graphics version
        uint plusFlags = r.U32();
        uint dx = r.U32(), dy = r.U32();
        _plusVideo = (plusFlags & 1) != 0;
        if (dx is > 0 and < 100000)
        {
            _plusDpiX = dx;
        }

        if (dy is > 0 and < 100000)
        {
            _plusDpiY = dy;
        }

        _ = flags; // bit 0 says "dual"; the GDI records of a dual file are skipped whether or not it is set
        _gs = new PlusGs { Smooth = false };
        EmitHints();
    }

    // ---- GetDC sections ----------------------------------------------------------------------------------------------

    private void BeginGetDc()
    {
        if (_inGetDc)
        {
            return;
        }

        _inGetDc = true;
        Emit(new SaveStateCommand());
        _getDcSaved = (_dc, _emitted, _saved.Count);
        _dc = new Dc { World = PlusTotal() };
        _emitted = Mat.Identity;
        // Force the next GDI drawing to set its own transform: the renderer's transform is whatever EMF+ left.
        _emitted = new Mat(double.NaN, 0, 0, 1, 0, 0);
    }

    private void EndGetDc()
    {
        if (!_inGetDc || _getDcSaved is not { } s)
        {
            _inGetDc = false;
            return;
        }

        _inGetDc = false;
        while (_saved.Count > s.SavedDepth)
        {
            _saved.Pop();
            Emit(new RestoreStateCommand());
        }

        Emit(new RestoreStateCommand());
        _dc = s.Dc;
        _emitted = s.Emitted;
        _getDcSaved = null;
    }

    // ---- transforms and state ----------------------------------------------------------------------------------------

    private static Mat ToMat(Matrix3x2 m) => new(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32);

    private static Matrix3x2 ToM32(Mat m) => m.ToMatrix3x2();

    private double UnitPx(int unit, bool horizontal)
    {
        double dpi = horizontal ? _plusDpiX : _plusDpiY;
        return unit switch
        {
            1 => dpi / (_plusVideo ? 75.0 : 100.0),
            3 => dpi / 72.0,
            4 => dpi,
            5 => dpi / 300.0,
            6 => dpi / 25.4,
            _ => 1.0,
        };
    }

    /// <summary>The matrix from world coordinates to the device units of the picture: world, then page, then the container base.</summary>
    private Mat PlusTotal()
    {
        PlusGs g = _gs;
        Mat page = Mat.Scale(UnitPx(g.PageUnit, true) * g.PageScale, UnitPx(g.PageUnit, false) * g.PageScale);
        return g.World.Then(page).Then(g.Base);
    }

    private Mat PlusDeviceMatrix()
    {
        Mat t = PlusTotal();
        return PlusPixelCenterShift == 0 || _gs.PixelHalf ? t : t.Then(Mat.Translate(PlusPixelCenterShift, PlusPixelCenterShift));
    }

    private void EnsurePlusTransform()
    {
        Mat m = PlusDeviceMatrix();
        if (!m.Equals(_emitted))
        {
            Emit(new SetTransformCommand(m.ToMatrix3x2()));
            _emitted = m;
        }
    }

    private void ApplyWorld(Mat m, bool post) => _gs.World = post ? _gs.World.Then(m) : m.Then(_gs.World);

    private void SetPageTransform(ushort flags, ref PlusReader r)
    {
        int unit = flags & 0xFF;
        double scale = r.F32();
        if (unit is < 1 or > 6 || !(scale > 0))
        {
            Diag(DiagnosticSeverity.Warning, $"EMF+ page transform with unit {unit} and scale {scale} is not valid; it is skipped.");
            return;
        }

        _gs.PageUnit = unit;
        _gs.PageScale = scale;
    }

    private void EmitHints()
    {
        PlusGs g = _gs;
        Emit(new RenderingHintsCommand(g.Smooth, g.SourceCopy, g.PixelHalf, g.Interp, g.TextHint, g.TextContrast, g.CompQuality, g.Origin));
    }

    private void PushFrame(uint index, bool container)
    {
        if (_plusStack.Count >= _lim.MaxSaveDepth)
        {
            throw new ImageLimitException($"The EMF+ records nest saved states deeper than {_lim.MaxSaveDepth}.");
        }

        Emit(new SaveStateCommand());
        _plusStack.Add(new PlusFrame(index, container, _gs.Clone(), _emitted));
    }

    private void PopFrame()
    {
        PlusFrame f = _plusStack[^1];
        _plusStack.RemoveAt(_plusStack.Count - 1);
        Emit(new RestoreStateCommand());
        _gs = f.State;
        _emitted = f.Emitted;
    }

    private void PopToFrame(uint index)
    {
        int at = -1;
        for (int i = _plusStack.Count - 1; i >= 0; i--)
        {
            if (_plusStack[i].Index == index)
            {
                at = i;
                break;
            }
        }

        if (at < 0)
        {
            Diag(DiagnosticSeverity.Warning, $"EMF+ restore of state {index}, which was not saved; it is skipped.");
            return;
        }

        while (_plusStack.Count > at)
        {
            PopFrame();
        }
    }

    private void BeginContainer(ushort flags, ref PlusReader r)
    {
        _ = flags; // the page unit of the rectangles; the container's own page unit is pixels
        var dest = new RectD(r.F32(), r.F32(), 0, 0);
        float dw = r.F32(), dh = r.F32();
        dest = new RectD(dest.Left, dest.Top, dest.Left + dw, dest.Top + dh);
        float sx = r.F32(), sy = r.F32(), sw = r.F32(), sh = r.F32();
        uint index = r.U32();
        Mat parent = PlusTotal();
        PushFrame(index, true);
        if (sw == 0 || sh == 0)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ container has an empty source rectangle; its transform is left unchanged.");
            _gs.World = Mat.Identity;
            _gs.Base = parent;
            _gs.PageUnit = 2;
            _gs.PageScale = 1;
            return;
        }

        Mat map = Mat.Translate(-sx, -sy).Then(Mat.Scale(dw / sw, dh / sh)).Then(Mat.Translate(dest.Left, dest.Top));
        _gs.World = Mat.Identity;
        _gs.PageUnit = 2;
        _gs.PageScale = 1;
        _gs.Base = map.Then(parent);
    }

    private void BeginContainerNoParams(ref PlusReader r)
    {
        uint index = r.U32();
        Mat parent = PlusTotal();
        PushFrame(index, true);
        _gs.World = Mat.Identity;
        _gs.PageUnit = 2;
        _gs.PageScale = 1;
        _gs.Base = parent;
    }

    // ---- clip --------------------------------------------------------------------------------------------------------

    private bool ClipModeFor(ushort flags, out ClipMode mode)
    {
        int cm = (flags >> 8) & 0xF;
        switch (cm)
        {
            case 0: mode = ClipMode.Replace; return true;
            case 1: mode = ClipMode.Intersect; return true;
            case 2: mode = ClipMode.Union; return true;
            case 3: mode = ClipMode.Xor; return true;
            case 4: mode = ClipMode.Exclude; return true;
            default:
                PlusNote("complement", DiagnosticSeverity.Warning, "EMF+ clip combine mode Complement is not supported; the clip is replaced instead.");
                mode = ClipMode.Replace;
                return true;
        }
    }

    private MetafilePath ToDevice(MetafilePath p) => p.Transform(PlusDeviceMatrix().ToMatrix3x2());

    private void ClipRectRecord(ushort flags, ref PlusReader r)
    {
        ClipModeFor(flags, out ClipMode mode);
        float x = r.F32(), y = r.F32(), w = r.F32(), h = r.F32();
        var b = new MetafilePathBuilder(8);
        b.AddRectangle(x, y, x + w, y + h);
        Emit(new ClipCommand(mode, ToDevice(b.Build()), FillRule.NonZero));
    }

    private void ClipPathRecord(ushort flags, ref PlusReader r)
    {
        _ = r;
        ClipModeFor(flags, out ClipMode mode);
        if (Obj((flags & 0xFF)) is not PlusPathObj p)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ clip refers to a path object that does not exist; it is skipped.");
            return;
        }

        Emit(new ClipCommand(mode, ToDevice(p.Path), p.Winding ? FillRule.NonZero : FillRule.EvenOdd));
    }

    private void ClipRegionRecord(ushort flags, ref PlusReader r)
    {
        _ = r;
        ClipModeFor(flags, out ClipMode mode);
        if (Obj(flags & 0xFF) is not PlusRegionObj reg)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ clip refers to a region object that does not exist; it is skipped.");
            return;
        }

        MetafilePath? path = RegionToPath(reg);
        if (path is null)
        {
            return;
        }

        Emit(new ClipCommand(mode, ToDevice(path), FillRule.NonZero));
    }

    private void OffsetClipRecord(ref PlusReader r)
    {
        double dx = r.F32(), dy = r.F32();
        Mat t = PlusDeviceMatrix();
        Emit(new OffsetClipCommand(new Vector2((float)((dx * t.M11) + (dy * t.M21)), (float)((dx * t.M12) + (dy * t.M22)))));
    }

    private PlusObj? Obj(int id) => id is >= 0 and < 64 ? _plusObjects[id] : null;

    // ---- emitting shapes ---------------------------------------------------------------------------------------------

    private BrushSpec? BrushFor(ushort flags, uint brushId)
    {
        if ((flags & 0x8000) != 0)
        {
            return new SolidBrushSpec(Argb(brushId));
        }

        if (brushId < 64 && _plusObjects[brushId] is PlusBrushObj b)
        {
            return b.Spec;
        }

        Diag(DiagnosticSeverity.Warning, $"An EMF+ record uses brush {brushId}, which does not exist; it is skipped.");
        return null;
    }

    private void Fill(MetafilePath path, FillRule rule, BrushSpec? brush)
    {
        if (brush is null || path.IsEmpty)
        {
            return;
        }

        EnsurePlusTransform();
        Emit(new FillPathCommand(path, rule, brush));
    }

    private PlusPenObj? PenFor(int id)
    {
        if (Obj(id) is PlusPenObj p)
        {
            return p;
        }

        Diag(DiagnosticSeverity.Warning, $"An EMF+ record uses pen {id}, which does not exist; it is skipped.");
        return null;
    }

    /// <summary>The width of a pen in logical (world) units: pens in world units are as they are, others are device sizes divided by the scale.</summary>
    private double PenWidthLogical(PlusPenObj p)
    {
        if (p.Unit == 0)
        {
            return p.Width;
        }

        double scale = PlusTotal().MeanScale;
        return scale > 0 ? p.Width * UnitPx(p.Unit, true) / scale : p.Width;
    }

    private PenSpec MakePen(PlusPenObj p)
    {
        double w = PenWidthLogical(p);
        bool cosmetic = !(w > 0);
        if (cosmetic)
        {
            w = 1;
        }

        double[]? dashes = p.Dashes is null ? null : [.. p.Dashes.Select(v => v * (cosmetic ? 1 : w))];
        Rgba32 color = p.Brush is SolidBrushSpec s ? s.Color : new Rgba32(0, 0, 0, 255);
        BrushSpec? brush = p.Brush is SolidBrushSpec ? null : p.Brush;
        LineCap cap = dashes is not null ? p.DashCap : p.Cap;
        if (p.StartCap >= 3 || p.EndCap >= 3)
        {
            PlusNote("cap" + p.StartCap + "/" + p.EndCap, DiagnosticSeverity.Info, "GDI+ pen line caps other than flat, square and round (triangle, anchors, custom) are not drawn; the end is drawn with a flat cap.");
        }

        if (p.Compound is not null)
        {
            PlusNote("compound", DiagnosticSeverity.Info, "GDI+ compound pens are drawn as solid lines.");
        }

        var plus = new PlusPenData(p.DashOffset * (cosmetic ? 1 : w), p.DashCap, p.Alignment, p.StartCap >= 3 ? p.StartCap : -1, p.EndCap >= 3 ? p.EndCap : -1, p.Compound, p.Transform);
        return new PenSpec(w, cosmetic, color, cap, p.Join, p.MiterLimit, dashes, null, brush, p.Alignment == PenAlignment.Inset) { Plus = plus };
    }

    private void Stroke(MetafilePath path, PlusPenObj? pen)
    {
        if (pen is null || path.IsEmpty)
        {
            return;
        }

        EnsurePlusTransform();
        Emit(new StrokePathCommand(path, MakePen(pen)));
    }

    private static MetafilePath RectPath(double l, double t, double r, double b)
    {
        var pb = new MetafilePathBuilder(8);
        pb.AddRectangle((float)l, (float)t, (float)r, (float)b);
        return pb.Build();
    }

    private static MetafilePath EllipsePath(double l, double t, double r, double b)
    {
        var pb = new MetafilePathBuilder(32);
        ArcGeometry.AddEllipse(pb, l, t, r, b);
        return pb.Build();
    }

    private static MetafilePath PolyPath(List<Vector2> pts, bool close)
    {
        var pb = new MetafilePathBuilder(Math.Max(pts.Count + 1, 8));
        for (int i = 0; i < pts.Count; i++)
        {
            if (i == 0)
            {
                pb.MoveTo(pts[i]);
            }
            else
            {
                pb.LineTo(pts[i]);
            }
        }

        if (close && pts.Count > 0)
        {
            pb.Close();
        }

        return pb.Build();
    }

    // ---- reading shapes ----------------------------------------------------------------------------------------------

    private List<RectD> ReadRects(ref PlusReader r, uint count, bool compressed)
    {
        if (count > _lim.MaxPathPoints)
        {
            throw new PlusDataException("too many rectangles");
        }

        r.Need(count, compressed ? 8 : 16);
        var list = new List<RectD>((int)count);
        for (int i = 0; i < count; i++)
        {
            double x, y, w, h;
            if (compressed)
            {
                x = r.I16();
                y = r.I16();
                w = r.I16();
                h = r.I16();
            }
            else
            {
                x = r.F32();
                y = r.F32();
                w = r.F32();
                h = r.F32();
            }

            list.Add(new RectD(x, y, x + w, y + h));
        }

        return list;
    }

    private RectD ReadRect(ref PlusReader r, bool compressed) => ReadRects(ref r, 1, compressed)[0];

    private List<Vector2> ReadPoints(ref PlusReader r, uint count, ushort flags)
    {
        if (count > _lim.MaxPathPoints)
        {
            throw new PlusDataException("too many points");
        }

        bool relative = (flags & 0x0800) != 0;
        bool compressed = (flags & 0x4000) != 0;
        var pts = new Vector2[count];
        if (relative)
        {
            ReadRelativePoints(ref r, pts);
        }
        else if (compressed)
        {
            r.Need(count, 4);
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = new Vector2(r.I16(), r.I16());
            }
        }
        else
        {
            r.Need(count, 8);
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = new Vector2(r.F32(), r.F32());
            }
        }

        AddWork(count);
        return [.. pts];
    }

    // ---- drawing records ---------------------------------------------------------------------------------------------

    private void PlusClear(ref PlusReader r)
    {
        Rgba32 c = Argb(r.U32());
        EnsurePlusTransform();
        Emit(new ClearCommand(c));
    }

    private void FillRects(ushort flags, ref PlusReader r)
    {
        BrushSpec? brush = BrushFor(flags, r.U32());
        uint n = r.U32();
        foreach (RectD q in ReadRects(ref r, n, (flags & 0x4000) != 0))
        {
            Fill(RectPath(q.Left, q.Top, q.Right, q.Bottom), FillRule.NonZero, brush);
        }
    }

    private void DrawRects(ushort flags, ref PlusReader r)
    {
        PlusPenObj? pen = PenFor(flags & 0xFF);
        uint n = r.U32();
        foreach (RectD q in ReadRects(ref r, n, (flags & 0x4000) != 0))
        {
            RectD s = Inset(pen, q);
            Stroke(RectPath(s.Left, s.Top, s.Right, s.Bottom), pen);
        }
    }

    /// <summary>For an inset pen, shrinks a rectangle or ellipse box by half the pen width, which keeps the stroke inside it.</summary>
    private RectD Inset(PlusPenObj? pen, RectD q)
    {
        if (pen is not { Alignment: PenAlignment.Inset })
        {
            return q;
        }

        double h = PenWidthLogical(pen) / 2;
        RectD n = q.Normalized;
        return new RectD(n.Left + h, n.Top + h, Math.Max(n.Left + h, n.Right - h), Math.Max(n.Top + h, n.Bottom - h));
    }

    private void FillPolygon(ushort flags, ref PlusReader r)
    {
        BrushSpec? brush = BrushFor(flags, r.U32());
        uint n = r.U32();
        Fill(PolyPath(ReadPoints(ref r, n, flags), true), FillRule.EvenOdd, brush);
    }

    private void DrawLines(ushort flags, ref PlusReader r)
    {
        PlusPenObj? pen = PenFor(flags & 0xFF);
        uint n = r.U32();
        Stroke(PolyPath(ReadPoints(ref r, n, flags), (flags & 0x2000) != 0), pen);
    }

    private void FillEllipse(ushort flags, ref PlusReader r)
    {
        BrushSpec? brush = BrushFor(flags, r.U32());
        RectD q = ReadRect(ref r, (flags & 0x4000) != 0);
        Fill(EllipsePath(q.Left, q.Top, q.Right, q.Bottom), FillRule.NonZero, brush);
    }

    private void DrawEllipse(ushort flags, ref PlusReader r)
    {
        PlusPenObj? pen = PenFor(flags & 0xFF);
        RectD q = Inset(pen, ReadRect(ref r, (flags & 0x4000) != 0));
        Stroke(EllipsePath(q.Left, q.Top, q.Right, q.Bottom), pen);
    }

    private static MetafilePath ArcPath(RectD q, double startDeg, double sweepDeg, bool pie)
    {
        RectD n = q.Normalized;
        double cx = (n.Left + n.Right) / 2, cy = (n.Top + n.Bottom) / 2, rx = n.Width / 2, ry = n.Height / 2;
        var pb = new MetafilePathBuilder(64);
        if (rx <= 0 || ry <= 0)
        {
            return MetafilePath.Empty;
        }

        if (Math.Abs(sweepDeg) >= 360)
        {
            ArcGeometry.AddEllipse(pb, n.Left, n.Top, n.Right, n.Bottom);
            return pb.Build();
        }

        // GDI+ gives angles as the direction of a ray from the center; the arc code wants the parametric angle of the ellipse.
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        double t0 = Math.Atan2(rx * Math.Sin(a0), ry * Math.Cos(a0)), t1 = Math.Atan2(rx * Math.Sin(a1), ry * Math.Cos(a1));
        double delta = t1 - t0;
        const double TwoPi = Math.PI * 2;
        if (sweepDeg >= 0)
        {
            while (delta < 0)
            {
                delta += TwoPi;
            }
        }
        else
        {
            while (delta > 0)
            {
                delta -= TwoPi;
            }
        }

        if (Math.Abs(sweepDeg) < 1e-9)
        {
            delta = 0;
        }

        if (pie)
        {
            pb.MoveTo(new Vector2((float)cx, (float)cy));
            ArcGeometry.AddArc(pb, cx, cy, rx, ry, t0, delta, true);
            pb.Close();
        }
        else
        {
            ArcGeometry.AddArc(pb, cx, cy, rx, ry, t0, delta, false);
        }

        return pb.Build();
    }

    private void FillPie(ushort flags, ref PlusReader r)
    {
        BrushSpec? brush = BrushFor(flags, r.U32());
        float start = r.F32(), sweep = r.F32();
        RectD q = ReadRect(ref r, (flags & 0x4000) != 0);
        Fill(ArcPath(q, start, sweep, true), FillRule.NonZero, brush);
    }

    private void DrawArcOrPie(ushort flags, ref PlusReader r, bool pie)
    {
        PlusPenObj? pen = PenFor(flags & 0xFF);
        float start = r.F32(), sweep = r.F32();
        RectD q = Inset(pen, ReadRect(ref r, (flags & 0x4000) != 0));
        Stroke(ArcPath(q, start, sweep, pie), pen);
    }

    private void FillRegion(ushort flags, ref PlusReader r)
    {
        BrushSpec? brush = BrushFor(flags, r.U32());
        if (Obj(flags & 0xFF) is not PlusRegionObj reg)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ fill refers to a region object that does not exist; it is skipped.");
            return;
        }

        if (RegionToPath(reg) is { } path)
        {
            Fill(path, FillRule.NonZero, brush);
        }
    }

    private void FillPathRecord(ushort flags, ref PlusReader r)
    {
        BrushSpec? brush = BrushFor(flags, r.U32());
        if (Obj(flags & 0xFF) is not PlusPathObj p)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ fill refers to a path object that does not exist; it is skipped.");
            return;
        }

        Fill(p.Path, p.Winding ? FillRule.NonZero : FillRule.EvenOdd, brush);
    }

    private void DrawPathRecord(ushort flags, ref PlusReader r)
    {
        PlusPenObj? pen = PenFor((int)r.U32());
        if (Obj(flags & 0xFF) is not PlusPathObj p)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ draw refers to a path object that does not exist; it is skipped.");
            return;
        }

        Stroke(p.Path, pen);
    }

    private void DrawBeziers(ushort flags, ref PlusReader r)
    {
        PlusPenObj? pen = PenFor(flags & 0xFF);
        uint n = r.U32();
        List<Vector2> pts = ReadPoints(ref r, n, flags);
        if (pts.Count < 4)
        {
            return;
        }

        var pb = new MetafilePathBuilder(pts.Count + 4);
        pb.MoveTo(pts[0]);
        for (int i = 1; i + 2 < pts.Count; i += 3)
        {
            pb.CubicTo(pts[i], pts[i + 1], pts[i + 2]);
        }

        Stroke(pb.Build(), pen);
    }

    private void FillClosedCurve(ushort flags, ref PlusReader r)
    {
        BrushSpec? brush = BrushFor(flags, r.U32());
        float tension = r.F32();
        uint n = r.U32();
        List<Vector2> pts = ReadPoints(ref r, n, flags);
        Fill(ClosedSpline(pts, tension), (flags & 0x2000) != 0 ? FillRule.NonZero : FillRule.EvenOdd, brush);
    }

    private void DrawClosedCurve(ushort flags, ref PlusReader r)
    {
        PlusPenObj? pen = PenFor(flags & 0xFF);
        float tension = r.F32();
        uint n = r.U32();
        Stroke(ClosedSpline(ReadPoints(ref r, n, flags), tension), pen);
    }

    private void DrawCurve(ushort flags, ref PlusReader r)
    {
        PlusPenObj? pen = PenFor(flags & 0xFF);
        float tension = r.F32();
        uint offset = r.U32();
        uint segments = r.U32();
        uint n = r.U32();
        List<Vector2> pts = ReadPoints(ref r, n, flags);
        if (pts.Count < 2 || segments == 0 || offset >= pts.Count - 1)
        {
            return;
        }

        segments = (uint)Math.Min(segments, pts.Count - 1 - (int)offset);
        Stroke(OpenSpline(pts, (int)offset, (int)segments, tension), pen);
    }

    // ---- splines -----------------------------------------------------------------------------------------------------

    private static MetafilePath ClosedSpline(List<Vector2> p, float tension)
    {
        int n = p.Count;
        if (n < 3)
        {
            return PolyPath(p, true);
        }

        var pb = new MetafilePathBuilder((n * 3) + 4);
        float k = tension / 3f;
        pb.MoveTo(p[0]);
        for (int i = 0; i < n; i++)
        {
            Vector2 p0 = p[(i + n - 1) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
            pb.CubicTo(p1 + (k * (p2 - p0)), p2 - (k * (p3 - p1)), p2);
        }

        pb.Close();
        return pb.Build();
    }

    private static MetafilePath OpenSpline(List<Vector2> p, int offset, int segments, float tension)
    {
        int n = p.Count;
        var pb = new MetafilePathBuilder((segments * 3) + 4);
        float k = tension / 3f;
        pb.MoveTo(p[offset]);
        for (int i = offset; i < offset + segments && i + 1 < n; i++)
        {
            Vector2 p0 = i > 0 ? p[i - 1] : p[i];
            Vector2 p1 = p[i], p2 = p[i + 1];
            Vector2 p3 = i + 2 < n ? p[i + 2] : p[i + 1];
            pb.CubicTo(p1 + (k * (p2 - p0)), p2 - (k * (p3 - p1)), p2);
        }

        return pb.Build();
    }

    // ---- regions -----------------------------------------------------------------------------------------------------

    private MetafilePath? RegionToPath(PlusRegionObj reg)
    {
        double scale = Math.Max(PlusTotal().MeanScale, 1e-3);
        float tol = (float)Math.Clamp(0.1 / scale, 1e-4, 100);
        var stack = new Stack<(OneImage.Drawing.Path Path, OneImage.Drawing.IntersectionRule Rule)>();
        RegionNode[] nodes = reg.Nodes;
        // Pre-order storage: walking it backwards puts the right subtree's result below the left one's.
        for (int i = nodes.Length - 1; i >= 0; i--)
        {
            _cancel.ThrowIfCancellationRequested();
            RegionNode n = nodes[i];
            switch (n.Type)
            {
                case 0x10000000:
                    stack.Push((OneImage.Drawing.Path.Rectangle((float)n.Rect.Left, (float)n.Rect.Top, (float)n.Rect.Width, (float)n.Rect.Height), OneImage.Drawing.IntersectionRule.NonZero));
                    break;
                case 0x10000001:
                    stack.Push((n.Path!.ToDrawingPath(), n.Winding ? OneImage.Drawing.IntersectionRule.NonZero : OneImage.Drawing.IntersectionRule.EvenOdd));
                    break;
                case 0x10000002:
                    stack.Push((OneImage.Drawing.Path.Empty, OneImage.Drawing.IntersectionRule.NonZero));
                    break;
                case 0x10000003:
                    stack.Push((OneImage.Drawing.Path.Rectangle(-4_000_000, -4_000_000, 8_000_000, 8_000_000), OneImage.Drawing.IntersectionRule.NonZero));
                    break;
                default:
                    {
                        if (stack.Count < 2)
                        {
                            Diag(DiagnosticSeverity.Warning, "An EMF+ region tree is malformed; the region is skipped.");
                            return null;
                        }

                        (OneImage.Drawing.Path l, OneImage.Drawing.IntersectionRule lr) = stack.Pop();
                        (OneImage.Drawing.Path rt, OneImage.Drawing.IntersectionRule rr) = stack.Pop();
                        OneImage.Drawing.Path res = n.Type switch
                        {
                            1 => OneImage.Drawing.PathBoolean.Intersect(l, rt, lr, rr, tol),
                            2 => OneImage.Drawing.PathBoolean.Union(l, rt, lr, rr, tol),
                            3 => OneImage.Drawing.PathBoolean.Xor(l, rt, lr, rr, tol),
                            4 => OneImage.Drawing.PathBoolean.Subtract(l, rt, lr, rr, tol),
                            _ => OneImage.Drawing.PathBoolean.Subtract(rt, l, rr, lr, tol),
                        };
                        stack.Push((res, OneImage.Drawing.IntersectionRule.NonZero));
                        break;
                    }
            }
        }

        if (stack.Count != 1)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ region tree is malformed; the region is skipped.");
            return null;
        }

        OneImage.Drawing.Path result = stack.Pop().Path;
        var pb = new MetafilePathBuilder(_lim.MaxPathPoints);
        foreach (OneImage.Drawing.Contour c in result.Flatten(tol))
        {
            for (int i = 0; i < c.Points.Length; i++)
            {
                if (i == 0)
                {
                    pb.MoveTo(c.Points[i]);
                }
                else
                {
                    pb.LineTo(c.Points[i]);
                }
            }

            if (c.IsClosed)
            {
                pb.Close();
            }
        }

        return pb.Build();
    }
}
