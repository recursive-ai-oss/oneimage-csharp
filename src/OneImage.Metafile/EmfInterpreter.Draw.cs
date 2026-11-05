using System.Buffers.Binary;
using System.Numerics;

namespace OneImage.Metafile;

internal sealed partial class EmfInterpreter
{
    // ---- brushes and pens ----------------------------------------------------------------------------------------------

    private static readonly BrushObj DefaultBrush = new(0, new Rgba32(255, 255, 255, 255), 0, null);
    private static readonly PenObj DefaultPen = new(0, 0, new Rgba32(0, 0, 0, 255), true, null, null);

    private BrushSpec? ToBrushSpec(BrushObj b) => b.Kind switch
    {
        0 => new SolidBrushSpec(b.Color),
        2 => new HatchBrushSpec(b.Hatch, b.Color, _dc.BkMode == 2 ? _dc.BkColor : null, BrushOrigin()),
        3 => new PatternBrushSpec(b.Bitmap!, _dc.TextColor, _dc.BkColor, BrushOrigin()),
        4 => new PatternBrushSpec(b.Bitmap!, _dc.TextColor, _dc.BkColor, BrushOrigin(), true),
        _ => null,
    };

    private Vector2 BrushOrigin() => new((float)_dc.BrushOrgX, (float)_dc.BrushOrgY);

    private BrushSpec? CurrentBrush() => ToBrushSpec(_dc.Brush ?? DefaultBrush);

    private static readonly double[][] DashBase =
    [
        [],
        [18, 6],
        [3, 3],
        [9, 6, 3, 6],
        [9, 3, 3, 3, 3, 3],
    ];

    /// <summary>
    /// Dash patterns of a wide (geometric) pen, measured from Windows output: the unit is the pen width and the patterns are dash 3 on 1 off,
    /// dot 1 on 1 off, dash-dot 3 1 1 1 and dash-dot-dot 3 1 1 1 1 1. With round or square caps the cap is part of the dash, so each dash
    /// is shortened by the width and the gap grows by what was taken (a round-capped dot is a disc of the pen width every two widths).
    /// </summary>
    private static double[] GeometricDashes(uint style, double width, LineCap cap)
    {
        double[] units = style switch { 1 => [3, 1], 2 => [1, 1], 3 => [3, 1, 1, 1], _ => [3, 1, 1, 1, 1, 1] };
        double[] d = [.. units.Select(u => u * width)];
        if (cap != LineCap.Flat)
        {
            for (int i = 0; i + 1 < d.Length; i += 2)
            {
                double taken = Math.Min(d[i], width);
                d[i] -= taken;
                d[i + 1] += taken;
            }
        }

        return d;
    }

    private PenSpec? CurrentPen()
    {
        PenObj p = _dc.Pen ?? DefaultPen;
        uint style = p.Style & 0xF;
        if (style == 5)
        {
            return null;
        }

        bool geometric = p.Legacy ? p.Width > 1 : (p.Style & 0xF0000) == 0x10000;
        double width = geometric ? Math.Max(p.Width, 1) : 1;
        LineCap cap = LineCap.Round;
        LineJoin join = LineJoin.Round;
        if (!p.Legacy && geometric)
        {
            cap = (p.Style & 0xF00) switch { 0x100 => LineCap.Square, 0x200 => LineCap.Flat, _ => LineCap.Round };
            join = (p.Style & 0xF000) switch { 0x1000 => LineJoin.Bevel, 0x2000 => LineJoin.Miter, _ => LineJoin.Round };
        }

        double[]? dashes = null;
        if (style is >= 1 and <= 4 && (!p.Legacy || !geometric))
        {
            if (geometric && width > 1)
            {
                dashes = GeometricDashes(style, width, cap);
            }
            else
            {
                dashes = [.. DashBase[style]];
            }
        }
        else if (style == 7 && p.UserDashes is { Length: > 0 } user)
        {
            dashes = user;
        }
        else if (style == 8)
        {
            dashes = [1, 1];
        }

        Rgba32 color = p.Color;
        BrushSpec? brush = null;
        if (p.Brush is { } pb)
        {
            if (pb.Kind == 1)
            {
                return null;
            }

            if (pb.Kind == 0)
            {
                color = pb.Color;
            }
            else
            {
                brush = ToBrushSpec(pb);
                color = pb.Color;
            }
        }

        return new PenSpec(
            width,
            !geometric,
            color,
            cap,
            join,
            _dc.MiterLimit,
            dashes,
            dashes is not null && _dc.BkMode == 2 ? _dc.BkColor : null,
            brush,
            geometric && style == 6);
    }

    // ---- drawing core --------------------------------------------------------------------------------------------------

    private FillRule PolyRule() => _dc.PolyFillMode == 2 ? FillRule.NonZero : FillRule.EvenOdd;

    private void DrawShape(MetafilePath fill, MetafilePath stroke, FillRule rule, bool doFill, bool doStroke)
    {
        if (_inPath)
        {
            _bracket.AddPath(fill.Transform(Ctm().ToMatrix3x2()));
            return;
        }

        BrushSpec? brush = doFill ? CurrentBrush() : null;
        PenSpec? pen = doStroke ? CurrentPen() : null;
        if (brush is null && pen is null)
        {
            return;
        }

        EnsureTransform();
        if (brush is not null)
        {
            Emit(new FillPathCommand(fill, rule, brush, _dc.Rop2));
        }

        if (pen is not null)
        {
            Emit(new StrokePathCommand(stroke, pen, _dc.Rop2));
        }
    }

    /// <summary>A stroke of points that GDI draws through pixel centers: the path moves half a device pixel right and down.</summary>
    private MetafilePath Centered(MetafilePath p)
    {
        (double hx, double hy) = HalfPixelInLogical(Ctm());
        return hx == 0 && hy == 0 ? p : p.Transform(Matrix3x2.CreateTranslation((float)hx, (float)hy));
    }

    private void Stroke(MetafilePath logicalPath, bool continueFigure)
    {
        if (_inPath)
        {
            AppendToBracket(logicalPath, continueFigure);
            return;
        }

        PenSpec? pen = CurrentPen();
        if (pen is null || logicalPath.IsEmpty)
        {
            return;
        }

        EnsureTransform();
        Emit(new StrokePathCommand(Centered(logicalPath), pen, _dc.Rop2));
    }

    private void AppendToBracket(MetafilePath logicalPath, bool continueFigure)
    {
        MetafilePath dev = logicalPath.Transform(Ctm().ToMatrix3x2());
        if (continueFigure && _bracket.HasCurrentPoint && dev.Verbs.Count > 0 && dev.Verbs[0] == PathVerb.MoveTo)
        {
            // Continue the open figure: the leading move is the current position, which the figure already ends at.
            var b = new MetafilePathBuilder();
            b.AddPath(dev);
            MetafilePath p = b.Build();
            AddWithoutFirstMove(p);
            return;
        }

        _bracket.AddPath(dev);
    }

    private void AddWithoutFirstMove(MetafilePath p)
    {
        int pt = 1;
        for (int i = 1; i < p.Verbs.Count; i++)
        {
            switch (p.Verbs[i])
            {
                case PathVerb.MoveTo: _bracket.MoveTo(p.Points[pt++]); break;
                case PathVerb.LineTo: _bracket.LineTo(p.Points[pt++]); break;
                case PathVerb.CubicTo:
                    _bracket.CubicTo(p.Points[pt], p.Points[pt + 1], p.Points[pt + 2]);
                    pt += 3;
                    break;
                default: _bracket.Close(); break;
            }
        }
    }

    private static Vector2 V(double x, double y) => new((float)x, (float)y);

    private void MoveTo(int x, int y)
    {
        (_dc.CurX, _dc.CurY) = (x, y);
        if (_inPath)
        {
            (double dx, double dy) = Ctm().Apply(x, y);
            _bracket.MoveTo(V(dx, dy));
        }
    }

    private void LineTo(int x, int y)
    {
        var b = new MetafilePathBuilder(8);
        b.MoveTo(V(_dc.CurX, _dc.CurY));
        b.LineTo(V(x, y));
        Stroke(b.Build(), true);
        (_dc.CurX, _dc.CurY) = (x, y);
    }

    private void SetPixel(int x, int y, Rgba32 color)
    {
        if (_inPath)
        {
            return;
        }

        EnsureTransform();
        var b = new MetafilePathBuilder(8);
        b.AddRectangle(x, y, x + 1, y + 1);
        Emit(new FillPathCommand(b.Build(), FillRule.NonZero, new SolidBrushSpec(color), _dc.Rop2));
    }

    /// <summary>Insets of a box so that a pen centered on the outline covers the outermost pixels GDI covers (right and bottom are exclusive).</summary>
    private (double X, double Y) BoxInset(PenSpec? pen)
    {
        Mat m = Ctm();
        if (pen is { IsInsideFrame: true } && pen.Width > 0)
        {
            return (pen.Width / 2, pen.Width / 2);
        }

        if (!m.IsInvertible || !m.IsAxisAligned)
        {
            return (0, 0);
        }

        (double x, double y) = m.InverseVector(0.5, 0.5);
        return (Math.Abs(x), Math.Abs(y));
    }

    /// <summary>
    /// GDI writes the bounding box of a shape inclusive of its far edges (a Rectangle(10, 10, 45, 80) call is recorded as 10, 10, 44, 79) and
    /// plays it back covering that last pixel, so the far device edges move out by one device pixel. Measured from Windows output.
    /// </summary>
    private void ExpandBox(ref double l, ref double t, ref double rr, ref double b)
    {
        if (_wmf is not null)
        {
            // A Windows metafile records the box as it was passed, right and bottom exclusive.
            return;
        }

        Mat m = Ctm();
        if (!m.IsInvertible || !m.IsAxisAligned)
        {
            return;
        }

        (double ix, double iy) = m.InverseVector(1, 1);
        ix = Math.Abs(ix);
        iy = Math.Abs(iy);
        if (m.M11 > 0)
        {
            rr += ix;
        }
        else
        {
            l -= ix;
        }

        if (m.M22 > 0)
        {
            b += iy;
        }
        else
        {
            t -= iy;
        }
    }

    private void Box(ReadOnlyMemory<byte> r, string name, int kind)
    {
        // Box: Left, Top, Right, Bottom (and for a rounded rectangle the corner size)
        if (!Need(r, kind == 2 ? 32 : 24, name))
        {
            return;
        }

        double l = Math.Min(Int(r, 8), Int(r, 16)), rr = Math.Max(Int(r, 8), Int(r, 16));
        double t = Math.Min(Int(r, 12), Int(r, 20)), b = Math.Max(Int(r, 12), Int(r, 20));
        double cw = kind == 2 ? Int(r, 24) : 0, ch = kind == 2 ? Int(r, 28) : 0;
        ExpandBox(ref l, ref t, ref rr, ref b);
        var fill = new MetafilePathBuilder(64);
        var stroke = new MetafilePathBuilder(64);
        PenSpec? pen = _inPath ? null : CurrentPen();
        (double ix, double iy) = BoxInset(pen);
        double sl = l + ix, sr = rr - ix, st = t + iy, sb = b - iy;
        if (sl > sr)
        {
            sl = sr = (l + rr) / 2;
        }

        if (st > sb)
        {
            st = sb = (t + b) / 2;
        }

        // With a raster operation that depends on what is underneath, GDI keeps the brush out from under a wide pen, so no pixel is
        // combined twice (measured from Windows output); with plain copying the pen covers the overlap anyway.
        double fl = l, ft = t, fr = rr, fb = b;
        bool fillEmpty = false;
        if (!_inPath && pen is { IsCosmetic: false, Width: > 1 } && _dc.Rop2 != MixMode.CopyPen)
        {
            double hw = pen.Width / 2;
            (fl, ft, fr, fb) = (sl + hw, st + hw, sr - hw, sb - hw);
            fillEmpty = fl >= fr || ft >= fb;
        }

        switch (kind)
        {
            case 0:
                if (!fillEmpty)
                {
                    ArcGeometry.AddEllipse(fill, fl, ft, fr, fb);
                }

                ArcGeometry.AddEllipse(stroke, sl, st, sr, sb);
                break;
            case 1:
                if (!fillEmpty)
                {
                    fill.AddRectangle((float)fl, (float)ft, (float)fr, (float)fb);
                }

                stroke.AddRectangle((float)sl, (float)st, (float)sr, (float)sb);
                break;
            default:
                if (!fillEmpty)
                {
                    ArcGeometry.AddRoundRect(fill, fl, ft, fr, fb, cw, ch);
                }

                ArcGeometry.AddRoundRect(stroke, sl, st, sr, sb, cw, ch);
                break;
        }

        if (pen is { IsInsideFrame: true })
        {
            pen = pen with { IsInsideFrame = false };
        }

        DrawBox(fill.Build(), stroke.Build(), pen);
    }

    private void DrawBox(MetafilePath fill, MetafilePath stroke, PenSpec? pen)
    {
        if (_inPath)
        {
            _bracket.AddPath(fill.Transform(Ctm().ToMatrix3x2()));
            return;
        }

        BrushSpec? brush = CurrentBrush();
        if (brush is null && pen is null)
        {
            return;
        }

        EnsureTransform();
        if (brush is not null)
        {
            Emit(new FillPathCommand(fill, FillRule.NonZero, brush, _dc.Rop2));
        }

        if (pen is not null)
        {
            Emit(new StrokePathCommand(stroke, pen, _dc.Rop2));
        }
    }

    // kind: 0 Arc, 1 Chord, 2 Pie, 3 ArcTo
    private void ArcRecord(ReadOnlyMemory<byte> r, int kind)
    {
        if (!Need(r, 40, "ARC"))
        {
            return;
        }

        double l = Math.Min(Int(r, 8), Int(r, 16)), rr = Math.Max(Int(r, 8), Int(r, 16));
        double t = Math.Min(Int(r, 12), Int(r, 20)), bt = Math.Max(Int(r, 12), Int(r, 20));
        double cx = (l + rr) / 2, cy = (t + bt) / 2, rx = (rr - l) / 2, ry = (bt - t) / 2;
        if (rx <= 0 || ry <= 0)
        {
            Diag(DiagnosticSeverity.Info, "An arc with an empty bounding box draws nothing.");
            return;
        }

        double a1 = ArcGeometry.RayAngle(cx, cy, rx, ry, Int(r, 24), Int(r, 28));
        double a2 = ArcGeometry.RayAngle(cx, cy, rx, ry, Int(r, 32), Int(r, 36));
        Mat m = Ctm();
        double sweep = ArcGeometry.Sweep(a1, a2, _dc.ArcDirection == 1, m.Determinant < 0);
        var b = new MetafilePathBuilder(256);
        if (kind == 3)
        {
            // ArcTo draws a line from the current position to the start of the arc.
            b.MoveTo(V(_dc.CurX, _dc.CurY));
            ArcGeometry.AddArc(b, cx, cy, rx, ry, a1, sweep, true);
            Stroke(b.Build(), true);
            (_dc.CurX, _dc.CurY) = (cx + (rx * Math.Cos(a1 + sweep)), cy + (ry * Math.Sin(a1 + sweep)));
            return;
        }

        ArcGeometry.AddArc(b, cx, cy, rx, ry, a1, sweep, false);
        if (kind == 2)
        {
            b.LineTo(V(cx, cy));
        }

        if (kind != 0)
        {
            b.Close();
        }

        MetafilePath path = b.Build();
        if (kind == 0)
        {
            Stroke(path, false);
        }
        else
        {
            // The ellipse's own edge is the pixel-center path for the pen; the fill covers the same shape.
            (double hx, double hy) = HalfPixelInLogical(m);
            MetafilePath strokePath = hx == 0 && hy == 0 ? path : path.Transform(Matrix3x2.CreateTranslation((float)hx, (float)hy));
            DrawShape(path, strokePath, FillRule.NonZero, true, true);
        }
    }

    private void AngleArc(ReadOnlyMemory<byte> r)
    {
        // Center (x, y), Radius, StartAngle (float, degrees), SweepAngle (float)
        if (!Need(r, 28, "ANGLEARC"))
        {
            return;
        }

        double cx = Int(r, 8), cy = Int(r, 12), rad = UInt(r, 16);
        double start = Float(r, 20) * Math.PI / 180, sweep = Float(r, 24) * Math.PI / 180;
        if (!double.IsFinite(start) || !double.IsFinite(sweep) || !double.IsFinite(rad))
        {
            return;
        }

        // AngleArc angles are counterclockwise from the +x axis in a y-up sense: in y-down logical coordinates the parametric angle is the negative.
        var b = new MetafilePathBuilder(256);
        b.MoveTo(V(_dc.CurX, _dc.CurY));
        double a1 = -start;
        double sw = Math.Clamp(-sweep, -2 * Math.PI, 2 * Math.PI);
        if (Math.Abs(sweep) > 2 * Math.PI)
        {
            sw = Math.Sign(-sweep) * 2 * Math.PI;
        }

        ArcGeometry.AddArc(b, cx, cy, rad, rad, a1, sw, true);
        Stroke(b.Build(), true);
        (_dc.CurX, _dc.CurY) = (cx + (rad * Math.Cos(a1 + sw)), cy + (rad * Math.Sin(a1 + sw)));
    }

    // ---- poly records --------------------------------------------------------------------------------------------------

    private bool ReadPoints(ReadOnlyMemory<byte> r, int offset, long count, bool is16, string name, out Vector2[] pts)
    {
        pts = [];
        int size = is16 ? 4 : 8;
        if (count < 0 || count > _lim.MaxPathPoints || offset + (count * size) > r.Length)
        {
            Diag(DiagnosticSeverity.Warning, $"Record {_record} ({name}) declares {count} points that do not fit; it is skipped.");
            return false;
        }

        AddWork(count);
        pts = new Vector2[count];
        ReadOnlySpan<byte> s = r.Span;
        for (int i = 0; i < pts.Length; i++)
        {
            int o = offset + (i * size);
            pts[i] = is16
                ? new Vector2(BinaryPrimitives.ReadInt16LittleEndian(s[o..]), BinaryPrimitives.ReadInt16LittleEndian(s[(o + 2)..]))
                : new Vector2(BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..]));
        }

        return true;
    }

    private void PolyBezier(ReadOnlyMemory<byte> r, bool to, bool is16)
    {
        if (!Need(r, 28, "POLYBEZIER") || !ReadPoints(r, 28, UInt(r, 24), is16, "POLYBEZIER", out Vector2[] pts))
        {
            return;
        }

        var b = new MetafilePathBuilder(_lim.MaxPathPoints);
        int i = 0;
        if (to)
        {
            b.MoveTo(V(_dc.CurX, _dc.CurY));
        }
        else if (pts.Length > 0)
        {
            b.MoveTo(pts[i++]);
        }

        for (; i + 2 < pts.Length; i += 3)
        {
            b.CubicTo(pts[i], pts[i + 1], pts[i + 2]);
        }

        if (pts.Length > 0 && to)
        {
            (_dc.CurX, _dc.CurY) = (pts[^1].X, pts[^1].Y);
        }

        Stroke(b.Build(), to);
    }

    private void PolyLineTo(ReadOnlyMemory<byte> r, bool is16)
    {
        if (!Need(r, 28, "POLYLINETO") || !ReadPoints(r, 28, UInt(r, 24), is16, "POLYLINETO", out Vector2[] pts))
        {
            return;
        }

        var b = new MetafilePathBuilder(_lim.MaxPathPoints);
        b.MoveTo(V(_dc.CurX, _dc.CurY));
        foreach (Vector2 p in pts)
        {
            b.LineTo(p);
        }

        if (pts.Length > 0)
        {
            (_dc.CurX, _dc.CurY) = (pts[^1].X, pts[^1].Y);
        }

        Stroke(b.Build(), true);
    }

    // A single polygon or polyline record.
    private void PolyPoly(ReadOnlyMemory<byte> r, bool is16, bool closed)
    {
        if (!Need(r, 28, "POLYGON") || !ReadPoints(r, 28, UInt(r, 24), is16, "POLYGON", out Vector2[] pts) || pts.Length == 0)
        {
            return;
        }

        var b = new MetafilePathBuilder(_lim.MaxPathPoints);
        b.MoveTo(pts[0]);
        for (int i = 1; i < pts.Length; i++)
        {
            b.LineTo(pts[i]);
        }

        if (closed)
        {
            b.Close();
            MetafilePath p = b.Build();
            DrawShape(p, Centered(p), PolyRule(), true, true);
        }
        else
        {
            Stroke(b.Build(), false);
        }
    }

    private void PolyPolyRecord(ReadOnlyMemory<byte> r, bool is16, bool closed)
    {
        // Bounds, NumberOfPolys, Count, PolyCounts[], points
        if (!Need(r, 32, "POLYPOLY"))
        {
            return;
        }

        uint polys = UInt(r, 24);
        uint total = UInt(r, 28);
        if (polys > _lim.MaxPathPoints || 32 + ((ulong)polys * 4) > (ulong)r.Length)
        {
            Diag(DiagnosticSeverity.Warning, $"Record {_record} (POLYPOLY) declares {polys} polygons that do not fit; it is skipped.");
            return;
        }

        int pointsAt = 32 + ((int)polys * 4);
        if (!ReadPoints(r, pointsAt, total, is16, "POLYPOLY", out Vector2[] pts))
        {
            return;
        }

        var b = new MetafilePathBuilder(_lim.MaxPathPoints);
        long at = 0;
        for (int i = 0; i < polys; i++)
        {
            uint n = UInt(r, 32 + (i * 4));
            if (n > pts.Length - at)
            {
                Diag(DiagnosticSeverity.Warning, $"Record {_record} (POLYPOLY) has polygon counts larger than its points; the rest is skipped.");
                break;
            }

            if (n > 0)
            {
                b.MoveTo(pts[at]);
                for (long k = 1; k < n; k++)
                {
                    b.LineTo(pts[at + k]);
                }

                if (closed)
                {
                    b.Close();
                }
            }

            at += n;
        }

        MetafilePath p = b.Build();
        if (closed)
        {
            DrawShape(p, Centered(p), PolyRule(), true, true);
        }
        else
        {
            Stroke(p, false);
        }
    }

    private void PolyDraw(ReadOnlyMemory<byte> r, bool is16)
    {
        if (!Need(r, 28, "POLYDRAW"))
        {
            return;
        }

        uint n = UInt(r, 24);
        if (!ReadPoints(r, 28, n, is16, "POLYDRAW", out Vector2[] pts))
        {
            return;
        }

        int typesAt = 28 + (int)n * (is16 ? 4 : 8);
        if (typesAt + (long)n > r.Length)
        {
            Diag(DiagnosticSeverity.Warning, $"Record {_record} (POLYDRAW) has no room for its point types; it is skipped.");
            return;
        }

        var b = new MetafilePathBuilder(_lim.MaxPathPoints);
        b.MoveTo(V(_dc.CurX, _dc.CurY));
        ReadOnlySpan<byte> types = r.Span.Slice(typesAt, (int)n);
        for (int i = 0; i < pts.Length; i++)
        {
            byte t = types[i];
            switch (t & ~1)
            {
                case 6: // PT_MOVETO
                    b.MoveTo(pts[i]);
                    break;
                case 2: // PT_LINETO
                    b.LineTo(pts[i]);
                    break;
                case 4: // PT_BEZIERTO takes three points
                    if (i + 2 < pts.Length)
                    {
                        b.CubicTo(pts[i], pts[i + 1], pts[i + 2]);
                        i += 2;
                        t = types[i];
                    }

                    break;
                default:
                    Diag(DiagnosticSeverity.Warning, $"PolyDraw point type {t} is not valid; the point is skipped.");
                    continue;
            }

            if ((t & 1) != 0)
            {
                b.Close();
            }
        }

        if (pts.Length > 0)
        {
            (_dc.CurX, _dc.CurY) = (b.CurrentPoint.X, b.CurrentPoint.Y);
        }

        Stroke(b.Build(), true);
    }

    // ---- paths ---------------------------------------------------------------------------------------------------------

    private void BeginPath()
    {
        _bracket.Clear();
        _inPath = true;
        _closedPath = null;
    }

    private void EndPath()
    {
        if (!_inPath)
        {
            Diag(DiagnosticSeverity.Warning, "EndPath without BeginPath is ignored.");
            return;
        }

        _inPath = false;
        _closedPath = _bracket.Build();
        _bracket.Clear();
    }

    private void CloseFigure()
    {
        if (_inPath)
        {
            _bracket.Close();
        }
    }

    private void AbortPath()
    {
        _inPath = false;
        _closedPath = null;
        _bracket.Clear();
    }

    private void WidenPathRecord() => Unsupported("WidenPath");

    private bool TakePath(out MetafilePath path)
    {
        if (_closedPath is null)
        {
            Diag(DiagnosticSeverity.Warning, "A path record without a completed path bracket is ignored.");
            path = MetafilePath.Empty;
            return false;
        }

        path = _closedPath;
        _closedPath = null;
        return true;
    }

    private void PathPaint(bool fill, bool stroke)
    {
        if (!TakePath(out MetafilePath path) || path.IsEmpty)
        {
            return;
        }

        BrushSpec? brush = fill ? CurrentBrush() : null;
        PenSpec? pen = stroke ? CurrentPen() : null;
        if (brush is null && pen is null)
        {
            return;
        }

        Mat m = Ctm();
        Emit(new SaveStateCommand());
        Emit(new SetTransformCommand(Matrix3x2.Identity));
        if (brush is not null)
        {
            Emit(new FillPathCommand(path, PolyRule(), brush, _dc.Rop2));
        }

        if (pen is not null)
        {
            // The path is already in device units, so a geometric pen's width and dashes are scaled to them.
            double k = pen.IsCosmetic ? 1 : m.MeanScale;
            PenSpec scaled = pen with { Width = pen.Width * k, Dashes = pen.IsCosmetic ? pen.Dashes : pen.Dashes?.Select(d => d * k).ToArray() };
            Emit(new StrokePathCommand(path.Transform(Matrix3x2.CreateTranslation(0.5f, 0.5f)), scaled, _dc.Rop2));
        }

        Emit(new RestoreStateCommand());
    }

    private static ClipMode RegionMode(int rgn) => rgn switch
    {
        1 => ClipMode.Intersect,
        2 => ClipMode.Union,
        3 => ClipMode.Xor,
        4 => ClipMode.Exclude,
        _ => ClipMode.Replace,
    };

    private void SelectClipPath(int mode)
    {
        if (mode is < 1 or > 5)
        {
            Diag(DiagnosticSeverity.Warning, $"SelectClipPath mode {mode} is not valid; ignored.");
            return;
        }

        if (TakePath(out MetafilePath path))
        {
            if (path.IsEmpty)
            {
                // GDI's SelectClipPath fails on a path without figures (for instance one that only held text drawn with a bitmap font)
                // and leaves the clip as it was; Windows output shows the unclipped drawing that follows.
                Diag(DiagnosticSeverity.Info, "SelectClipPath with an empty path leaves the clip unchanged.");
                return;
            }

            Emit(new ClipCommand(RegionMode(mode), path, PolyRule()));
        }
    }

    // ---- clipping ------------------------------------------------------------------------------------------------------

    private MetafilePath DeviceQuad(double l, double t, double r, double b)
    {
        Mat m = Ctm();
        var pb = new MetafilePathBuilder(8);
        pb.MoveTo(m.ApplyV(l, t));
        pb.LineTo(m.ApplyV(r, t));
        pb.LineTo(m.ApplyV(r, b));
        pb.LineTo(m.ApplyV(l, b));
        pb.Close();
        return pb.Build();
    }

    private void ClipRect(ClipMode mode, ReadOnlyMemory<byte> r)
    {
        double l = Math.Min(Int(r, 8), Int(r, 16)), rr = Math.Max(Int(r, 8), Int(r, 16));
        double t = Math.Min(Int(r, 12), Int(r, 20)), b = Math.Max(Int(r, 12), Int(r, 20));
        Emit(new ClipCommand(mode, DeviceQuad(l, t, rr, b)));
    }

    private void OffsetClip(int dx, int dy)
    {
        Mat m = Ctm();
        Emit(new OffsetClipCommand(V((dx * m.M11) + (dy * m.M21), (dx * m.M12) + (dy * m.M22))));
    }

    /// <summary>Reads a RegionData object into a path of rectangles in logical coordinates, or null when it is malformed.</summary>
    private MetafilePath? ReadRegion(ReadOnlyMemory<byte> r, int at, uint size)
    {
        if (size < 32 || (ulong)at + size > (ulong)r.Length)
        {
            return null;
        }

        uint count = UInt(r, at + 8);
        if (count > _lim.MaxRegionRectangles || 32 + ((ulong)count * 16) > size)
        {
            Diag(DiagnosticSeverity.Warning, $"A region in record {_record} declares {count} rectangles that do not fit; it is skipped.");
            return null;
        }

        AddWork(count);
        var pb = new MetafilePathBuilder(Math.Max(16, (int)Math.Min((long)count * 4 + 4, _lim.MaxPathPoints)));
        for (int i = 0; i < count; i++)
        {
            int o = at + 32 + (i * 16);
            double l = Math.Min(Int(r, o), Int(r, o + 8)), rr = Math.Max(Int(r, o), Int(r, o + 8));
            double t = Math.Min(Int(r, o + 4), Int(r, o + 12)), b = Math.Max(Int(r, o + 4), Int(r, o + 12));
            pb.AddRectangle((float)l, (float)t, (float)rr, (float)b);
        }

        return pb.Build();
    }

    private void ExtSelectClipRgn(ReadOnlyMemory<byte> r)
    {
        if (!Need(r, 16, "EXTSELECTCLIPRGN"))
        {
            return;
        }

        uint size = UInt(r, 8);
        int mode = Int(r, 12);
        if (mode is < 1 or > 5)
        {
            Diag(DiagnosticSeverity.Warning, $"ExtSelectClipRgn mode {mode} is not valid; ignored.");
            return;
        }

        if (size == 0)
        {
            // A copy of "no region" removes the clip; combining with it changes nothing for AND, OR, XOR or DIFF with the full plane.
            if (mode == 5)
            {
                Emit(new ClipCommand(ClipMode.Reset, null));
            }

            return;
        }

        MetafilePath? logical = ReadRegion(r, 16, size);
        if (logical is null)
        {
            Diag(DiagnosticSeverity.Warning, "The region of an ExtSelectClipRgn record is damaged; the clip is not changed.");
            return;
        }

        Emit(new ClipCommand(RegionMode(mode), logical.Transform(Ctm().ToMatrix3x2())));
    }

    // kind: 0 fill, 1 frame, 2 invert, 3 paint
    private void RegionRecord(ReadOnlyMemory<byte> r, int kind)
    {
        int hdr = kind switch { 0 => 32, 1 => 40, _ => 28 };
        if (!Need(r, hdr, "region record"))
        {
            return;
        }

        uint size = UInt(r, 24);
        MetafilePath? region = ReadRegion(r, hdr, size);
        if (region is null)
        {
            Diag(DiagnosticSeverity.Warning, "The region of a region-painting record is damaged; the record is skipped.");
            return;
        }

        if (_inPath)
        {
            return;
        }

        if (kind <= 1)
        {
            uint ih = UInt(r, 28);
            if (!_objects.TryGetValue(ih, out GdiObject? o) || o is not BrushObj bo)
            {
                Diag(DiagnosticSeverity.Warning, $"A region record names brush {ih}, which does not exist; skipped.");
                return;
            }

            BrushSpec? brush = ToBrushSpec(bo);
            if (brush is null)
            {
                return;
            }

            EnsureTransform();
            if (kind == 0)
            {
                Emit(new FillPathCommand(region, FillRule.NonZero, brush, _dc.Rop2));
                return;
            }

            // FrameRegion: GDI paints the frame inside the region, as the part of it that is not still covered when the region is moved by the
            // frame size in each of the four directions (measured from Windows output).
            double w = Int(r, 32), h = Int(r, 36);
            Matrix3x2 toDevice = Ctm().ToMatrix3x2();
            foreach ((double dx, double dy) in new[] { (-w, 0.0), (w, 0.0), (0.0, -h), (0.0, h) })
            {
                Emit(new SaveStateCommand());
                Emit(new ClipCommand(ClipMode.Intersect, region.Transform(toDevice)));
                Emit(new ClipCommand(ClipMode.Exclude, region.Transform(Matrix3x2.CreateTranslation((float)dx, (float)dy) * toDevice)));
                Emit(new FillPathCommand(region, FillRule.NonZero, brush, _dc.Rop2));
                Emit(new RestoreStateCommand());
            }

            return;
        }

        EnsureTransform();
        if (kind == 3)
        {
            BrushSpec? brush = CurrentBrush();
            if (brush is not null)
            {
                Emit(new FillPathCommand(region, FillRule.NonZero, brush, _dc.Rop2));
            }

            return;
        }

        // InvertRegion: invert what lies inside the region.
        RectD bb = region.Bounds;
        Emit(new SaveStateCommand());
        Emit(new ClipCommand(ClipMode.Intersect, region.Transform(Ctm().ToMatrix3x2())));
        Emit(PatBlt(bb.Left, bb.Top, bb.Width, bb.Height, 0x00550009));
        Emit(new RestoreStateCommand());
    }

    // ---- bitmaps -------------------------------------------------------------------------------------------------------

    private BlitCommand PatBlt(double x, double y, double w, double h, uint rop) => new(
        null, default, V(x, y), V(x + w, y), V(x, y + h), rop, CurrentBrush(), _dc.Stretch, BlitKind.RasterOperation, 255, false, default, null, default, _dc.TextColor, _dc.BkColor);

    private static bool RopNeedsSource(uint rop)
    {
        // A ternary raster operation index: the low 8 bits of the high word. It depends on the source when its table differs between S=0 and S=1.
        int index = (int)((rop >> 16) & 0xFF);
        // Bit layout of the result table: the index bits order is P=0x...; checking dependence on S: for each (P,D) the result for S=0 vs S=1.
        for (int p = 0; p < 2; p++)
        {
            for (int d = 0; d < 2; d++)
            {
                int i0 = (p << 2) | (0 << 1) | d; // order P S D, P most significant
                int i1 = (p << 2) | (1 << 1) | d;
                if (((index >> i0) & 1) != ((index >> i1) & 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // kind: 0 BitBlt, 1 StretchBlt, 2 MaskBlt
    private void BitBlt(ReadOnlyMemory<byte> r, int kind)
    {
        int min = kind switch { 0 => 100, 1 => 108, _ => 128 };
        if (!Need(r, min, "BITBLT"))
        {
            return;
        }

        double xd = Int(r, 24), yd = Int(r, 28), cx = Int(r, 32), cy = Int(r, 36);
        uint rop = UInt(r, 40);
        double xs = Int(r, 44), ys = Int(r, 48);
        Rgba32 bk = Color(UInt(r, 76));
        double cxs = kind == 1 ? Int(r, 100) : cx, cys = kind == 1 ? Int(r, 104) : cy;
        DeviceIndependentBitmap? dib = null;
        if (UInt(r, 88) != 0)
        {
            dib = ParseDib(r, UInt(r, 84), UInt(r, 88), UInt(r, 92), UInt(r, 96));
        }

        DeviceIndependentBitmap? mask = null;
        double mx = 0, my = 0;
        if (kind == 2 && UInt(r, 116) != 0)
        {
            mask = ParseDib(r, UInt(r, 112), UInt(r, 116), UInt(r, 120), UInt(r, 124));
            mx = Int(r, 100);
            my = Int(r, 104);
        }

        if (dib is null && RopNeedsSource(rop))
        {
            Diag(DiagnosticSeverity.Warning, "A block transfer reads a source bitmap that the record does not contain; it is skipped.");
            return;
        }

        EmitBlit(dib, xs, ys, cxs, cys, xd, yd, cx, cy, rop, BlitKind.RasterOperation, 255, false, default, mask, mx, my, bk, (int)UInt(r, 80));
    }

    private void EmitBlit(DeviceIndependentBitmap? dib, double xs, double ys, double cxs, double cys, double xd, double yd, double cx, double cy, uint rop, BlitKind kind, byte alpha, bool perPixel, Rgba32 key, DeviceIndependentBitmap? mask, double mx, double my, Rgba32 bk, int usage)
    {
        _ = usage;
        if (_inPath)
        {
            return;
        }

        EnsureTransform();
        RectD src = default;
        Vector2 tl = V(xd, yd), tr = V(xd + cx, yd), bl = V(xd, yd + cy);
        if (dib is not null)
        {
            double left = xs, top = ys, w = cxs, h = cys;
            bool flipX = false, flipY = false;
            if (w < 0)
            {
                left += w;
                w = -w;
                flipX = true;
            }

            if (h < 0)
            {
                top += h;
                h = -h;
                flipY = true;
            }

            src = new RectD(left, top, left + w, top + h);
            if (flipX)
            {
                (tl, tr, bl) = (tr, tl, bl + (tr - tl));
            }

            if (flipY)
            {
                (tl, bl, tr) = (bl, tl, tr + (bl - tl));
            }
        }

        Emit(new BlitCommand(dib, src, tl, tr, bl, rop, CurrentBrush(), _dc.Stretch, kind, alpha, perPixel, key, mask, V(mx, my), _dc.TextColor, bk));
    }

    private void PlgBlt(ReadOnlyMemory<byte> r)
    {
        if (!Need(r, 140, "PLGBLT"))
        {
            return;
        }

        Vector2 a = V(Int(r, 24), Int(r, 28)), b = V(Int(r, 32), Int(r, 36)), c = V(Int(r, 40), Int(r, 44));
        double xs = Int(r, 48), ys = Int(r, 52), cxs = Int(r, 56), cys = Int(r, 60);
        Rgba32 bk = Color(UInt(r, 88));
        DeviceIndependentBitmap? dib = UInt(r, 100) != 0 ? ParseDib(r, UInt(r, 96), UInt(r, 100), UInt(r, 104), UInt(r, 108)) : null;
        if (dib is null)
        {
            Diag(DiagnosticSeverity.Warning, "A PlgBlt record has no source bitmap; it is skipped.");
            return;
        }

        DeviceIndependentBitmap? mask = UInt(r, 128) != 0 ? ParseDib(r, UInt(r, 124), UInt(r, 128), UInt(r, 132), UInt(r, 136)) : null;
        if (_inPath)
        {
            return;
        }

        EnsureTransform();
        Emit(new BlitCommand(dib, new RectD(xs, ys, xs + cxs, ys + cys), a, b, c, 0x00CC0020, CurrentBrush(), _dc.Stretch, BlitKind.RasterOperation, 255, false, default, mask, V(Int(r, 112), Int(r, 116)), _dc.TextColor, bk));
    }

    private void StretchDibits(ReadOnlyMemory<byte> r)
    {
        // Bounds, xDest, yDest, xSrc, ySrc, cxSrc, cySrc, offBmi, cbBmi, offBits, cbBits, Usage, Rop, cxDest, cyDest
        if (!Need(r, 80, "STRETCHDIBITS"))
        {
            return;
        }

        double xd = Int(r, 24), yd = Int(r, 28), xs = Int(r, 32), ys = Int(r, 36), cxs = Int(r, 40), cys = Int(r, 44);
        uint rop = UInt(r, 68);
        double cxd = Int(r, 72), cyd = Int(r, 76);
        DeviceIndependentBitmap? dib = ParseDib(r, UInt(r, 48), UInt(r, 52), UInt(r, 56), UInt(r, 60));
        if (dib is null)
        {
            return;
        }

        // The source y of a bottom-up DIB counts from the bottom row.
        double top = dib.IsTopDown ? ys : dib.Height - ys - Math.Abs(cys);
        if (cys < 0)
        {
            top = dib.IsTopDown ? ys + cys : dib.Height - ys;
        }

        EmitBlit(dib, xs, top, cxs, cys, xd, yd, cxd, cyd, rop, BlitKind.RasterOperation, 255, false, default, null, 0, 0, _dc.BkColor, (int)UInt(r, 64));
    }

    private void SetDibitsToDevice(ReadOnlyMemory<byte> r)
    {
        // Bounds, xDest, yDest, xSrc, ySrc, cxSrc, cySrc, offBmi, cbBmi, offBits, cbBits, Usage, StartScan, cScans
        if (!Need(r, 76, "SETDIBITSTODEVICE"))
        {
            return;
        }

        double xd = Int(r, 24), yd = Int(r, 28), xs = Int(r, 32), ys = Int(r, 36), cxs = Int(r, 40), cys = Int(r, 44);
        DeviceIndependentBitmap? dib = ParseDib(r, UInt(r, 48), UInt(r, 52), UInt(r, 56), UInt(r, 60));
        if (dib is null)
        {
            return;
        }

        uint start = UInt(r, 68), scans = UInt(r, 72);
        if (start != 0 || scans != dib.Height)
        {
            Diag(DiagnosticSeverity.Warning, "SetDIBitsToDevice with a partial set of scan lines is drawn as if the bitmap held all of them.");
        }

        double top = dib.IsTopDown ? ys : dib.Height - ys - cys;
        EmitBlit(dib, xs, top, cxs, cys, xd, yd, cxs, cys, 0x00CC0020, BlitKind.RasterOperation, 255, false, default, null, 0, 0, _dc.BkColor, 0);
    }

    private void AlphaOrTransparent(ReadOnlyMemory<byte> r, bool alpha)
    {
        if (!Need(r, 108, alpha ? "ALPHABLEND" : "TRANSPARENTBLT"))
        {
            return;
        }

        double xd = Int(r, 24), yd = Int(r, 28), cx = Int(r, 32), cy = Int(r, 36);
        uint blend = UInt(r, 40);
        double xs = Int(r, 44), ys = Int(r, 48), cxs = Int(r, 100), cys = Int(r, 104);
        Rgba32 bk = Color(UInt(r, 76));
        DeviceIndependentBitmap? dib = ParseDib(r, UInt(r, 84), UInt(r, 88), UInt(r, 92), UInt(r, 96));
        if (dib is null)
        {
            return;
        }

        if (alpha)
        {
            byte constant = (byte)(blend >> 16);
            bool perPixel = ((blend >> 24) & 1) != 0 && dib.BitCount == 32;
            EmitBlit(dib, xs, ys, cxs, cys, xd, yd, cx, cy, 0, BlitKind.AlphaBlend, constant, perPixel, default, null, 0, 0, bk, 0);
        }
        else
        {
            EmitBlit(dib, xs, ys, cxs, cys, xd, yd, cx, cy, 0x00CC0020, BlitKind.TransparentColor, 255, false, Color(blend), null, 0, 0, bk, 0);
        }
    }

    private void GradientFill(ReadOnlyMemory<byte> r)
    {
        if (!Need(r, 36, "GRADIENTFILL"))
        {
            return;
        }

        uint nVer = UInt(r, 24), nTri = UInt(r, 28), mode = UInt(r, 32);
        if (mode > 2)
        {
            Diag(DiagnosticSeverity.Warning, $"GradientFill mode {mode} is not valid; skipped.");
            return;
        }

        int per = mode == 2 ? 3 : 2;
        if (nVer > _lim.MaxPathPoints || nTri > _lim.MaxPathPoints || 36 + ((ulong)nVer * 16) + ((ulong)nTri * (ulong)per * 4) > (ulong)r.Length)
        {
            Diag(DiagnosticSeverity.Warning, "A GradientFill record declares more vertices or triangles than it holds; skipped.");
            return;
        }

        AddWork(nVer + nTri);
        var verts = new GradientVertex[nVer];
        for (int i = 0; i < nVer; i++)
        {
            int o = 36 + (i * 16);
            ReadOnlySpan<byte> s = r.Span;
            verts[i] = new GradientVertex(
                V(Int(r, o), Int(r, o + 4)),
                new Rgba32(
                    (byte)(BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 8)..]) >> 8),
                    (byte)(BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 10)..]) >> 8),
                    (byte)(BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 12)..]) >> 8),
                    255)); // GDI's GradientFill paints opaque colors; the vertex alpha is not used.
        }

        var idx = new int[nTri * per];
        int meshAt = 36 + ((int)nVer * 16);
        for (int i = 0; i < idx.Length; i++)
        {
            uint v = UInt(r, meshAt + (i * 4));
            if (v >= nVer)
            {
                Diag(DiagnosticSeverity.Warning, "A GradientFill record indexes a vertex that does not exist; skipped.");
                return;
            }

            idx[i] = (int)v;
        }

        if (_inPath)
        {
            return;
        }

        EnsureTransform();
        Emit(new GradientFillCommand(verts, idx, (GradientMode)mode));
    }

    // ---- text ----------------------------------------------------------------------------------------------------------

    internal static string Decode8(ReadOnlySpan<byte> s)
    {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            chars[i] = s[i] is >= 0x80 and <= 0x9F ? Cp1252[s[i] - 0x80] : (char)s[i];
        }

        return new string(chars);
    }

    private static readonly char[] Cp1252 =
    [
        '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021', '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014', '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
    ];

    private FontSpec CurrentFont() => (_dc.Font ?? new FontObj(new FontSpec("System", 16, Weight: 700))).Spec;

    private void ExtTextOut(ReadOnlyMemory<byte> r, bool wide)
    {
        // Bounds, iGraphicsMode, exScale, eyScale, EmrText
        if (!Need(r, 36 + 40, "EXTTEXTOUT"))
        {
            return;
        }

        EmitText(r, 36, wide);
    }

    private void PolyTextOut(ReadOnlyMemory<byte> r, bool wide)
    {
        if (!Need(r, 28, "POLYTEXTOUT"))
        {
            return;
        }

        uint n = UInt(r, 24);
        if (n > _lim.MaxRecords || 28 + ((ulong)n * 40) > (ulong)r.Length)
        {
            Diag(DiagnosticSeverity.Warning, "A PolyTextOut record declares more strings than it holds; skipped.");
            return;
        }

        for (int i = 0; i < n; i++)
        {
            EmitText(r, 28 + (i * 40), wide);
        }
    }

    private void EmitText(ReadOnlyMemory<byte> r, int at, bool wide)
    {
        // EmrText at 'at': Reference(8) Chars offString Options Rectangle(16) offDx
        double rx = Int(r, at), ry = Int(r, at + 4);
        uint chars = UInt(r, at + 8), offString = UInt(r, at + 12), options = UInt(r, at + 16);
        var rect = new RectD(Int(r, at + 20), Int(r, at + 24), Int(r, at + 28), Int(r, at + 32));
        uint offDx = UInt(r, at + 36);
        int unit = wide ? 2 : 1;
        if (chars > _lim.MaxTextCharacters || (ulong)offString + ((ulong)chars * (ulong)unit) > (ulong)r.Length)
        {
            Diag(DiagnosticSeverity.Warning, "A text record has a string that does not fit in the record; skipped.");
            return;
        }

        AddWork(chars);
        ReadOnlySpan<byte> raw = r.Span.Slice((int)offString, (int)chars * unit);
        string text = wide ? System.Text.Encoding.Unicode.GetString(raw) : Decode8(raw);
        double[]? dx = null;
        if (offDx != 0 && chars > 0)
        {
            int per = (options & 0x2000) != 0 ? 2 : 1;
            if ((ulong)offDx + ((ulong)chars * (ulong)per * 4) <= (ulong)r.Length)
            {
                dx = new double[chars];
                for (int i = 0; i < chars; i++)
                {
                    dx[i] = Int(r, (int)offDx + (i * per * 4));
                }
            }
            else
            {
                Diag(DiagnosticSeverity.Warning, "The character spacing array of a text record does not fit; font advances are used.");
            }
        }

        bool noRect = (options & 0x100) != 0;
        EmitTextRun(text, (options & 0x10) != 0, rx, ry, options, noRect ? null : rect, dx);
    }

    private void SmallTextOut(ReadOnlyMemory<byte> r)
    {
        // x, y, cChars, fuOptions, iGraphicsMode, exScale, eyScale, [Bounds], TextString
        if (!Need(r, 36, "SMALLTEXTOUT"))
        {
            return;
        }

        double x = Int(r, 8), y = Int(r, 12);
        uint chars = UInt(r, 16), options = UInt(r, 20);
        int at = 36 + ((options & 0x100) != 0 ? 0 : 16);
        bool small = (options & 0x200) != 0;
        int unit = small ? 1 : 2;
        if (chars > _lim.MaxTextCharacters || (ulong)at + ((ulong)chars * (ulong)unit) > (ulong)r.Length)
        {
            Diag(DiagnosticSeverity.Warning, "A SmallTextOut record has a string that does not fit; skipped.");
            return;
        }

        AddWork(chars);
        ReadOnlySpan<byte> raw = r.Span.Slice(at, (int)chars * unit);
        string text = small ? Decode8(raw) : System.Text.Encoding.Unicode.GetString(raw);
        EmitTextRun(text, (options & 0x10) != 0, x, y, options, null, null);
    }

    private void EmitTextRun(string text, bool glyphs, double x, double y, uint options, RectD? rect, double[]? dx)
    {
        uint align = _dc.TextAlign;
        bool updateCp = (align & 1) != 0;
        if (updateCp)
        {
            (x, y) = (_dc.CurX, _dc.CurY);
        }

        TextHorizontalAlignment h = (align & 6) switch { 2 => TextHorizontalAlignment.Right, 6 => TextHorizontalAlignment.Center, _ => TextHorizontalAlignment.Left };
        TextVerticalAlignment v = (align & 24) switch { 8 => TextVerticalAlignment.Bottom, 24 => TextVerticalAlignment.Baseline, _ => TextVerticalAlignment.Top };
        FontSpec font = CurrentFont();
        if (_inPath)
        {
            Unsupported("Text inside a path bracket");
            return;
        }

        EnsureTransform();
        RectD? opaque = (options & 2) != 0 ? rect : null;
        RectD? clip = (options & 4) != 0 ? rect : null;
        Emit(new DrawTextCommand(
            text,
            glyphs,
            V(x, y),
            h,
            v,
            font,
            _dc.TextColor,
            dx,
            _dc.BkMode == 2 ? _dc.BkColor : null,
            opaque,
            _dc.BkColor,
            clip,
            (options & 0x80) != 0 || (align & 256) != 0,
            _dc.CharExtra,
            _dc.BreakExtra,
            _dc.BreakCount));
        if (updateCp)
        {
            double adv = dx is not null ? dx.Sum() : (_options.MeasureText?.Invoke(font, text) ?? text.Length * Math.Abs(font.Height) * 0.5);
            _dc.CurX += (align & 256) != 0 || (options & 0x80) != 0 ? -adv : adv;
        }
    }

    // ---- comments ------------------------------------------------------------------------------------------------------

    private void Comment(ReadOnlyMemory<byte> r)
    {
        // DataSize, then data. A public comment starts with the identifier 0x43494447 ("GDIC"); EMF+ with "EMF+".
        if (!Need(r, 12, "GDICOMMENT"))
        {
            return;
        }

        uint size = UInt(r, 8);
        if ((ulong)size + 12 > (ulong)r.Length)
        {
            Diag(DiagnosticSeverity.Warning, "A comment record declares more data than it holds; skipped.");
            return;
        }

        ReadOnlyMemory<byte> data = r.Slice(12, (int)size);
        CommentKind kind = CommentKind.Generic;
        if (size >= 4)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.Span);
            if (id == 0x2B464D45)
            {
                kind = CommentKind.EmfPlus;
                data = data[4..];
            }
            else if (id == 0x43494447)
            {
                kind = CommentKind.Public;
                data = data[4..];
            }
            else if (id == 0x30495053 || id == 0x32495053)
            {
                kind = CommentKind.Spool;
            }
        }

        if (kind == CommentKind.EmfPlus)
        {
            PlusComment(data);
            return;
        }

        Emit(new CommentCommand(kind, data));
    }
}
