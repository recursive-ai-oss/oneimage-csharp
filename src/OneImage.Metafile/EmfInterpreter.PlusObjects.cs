using System.Numerics;

namespace OneImage.Metafile;

/// <summary>An entry of the EMF+ object table.</summary>
internal abstract record PlusObj;

internal sealed record PlusBrushObj(BrushSpec Spec) : PlusObj;

/// <summary>A pen as stored. Widths and dash lengths are as in the file; the interpreter converts them when the pen is used.</summary>
internal sealed record PlusPenObj(
    double Width,
    int Unit,
    LineCap Cap,
    LineJoin Join,
    double MiterLimit,
    double[]? Dashes,
    double DashOffset,
    LineCap DashCap,
    PenAlignment Alignment,
    int StartCap,
    int EndCap,
    float[]? Compound,
    Matrix3x2? Transform,
    BrushSpec Brush) : PlusObj;

internal sealed record PlusPathObj(MetafilePath Path, bool Winding) : PlusObj;

/// <summary>A node of a region's tree, in the pre-order of the file. <see cref="Type"/> 1-5 are combine nodes (and, or, xor, exclude, complement); the others are leaves.</summary>
internal readonly record struct RegionNode(uint Type, MetafilePath? Path, bool Winding, RectD Rect);

internal sealed record PlusRegionObj(RegionNode[] Nodes) : PlusObj;

internal sealed record PlusImageObj(RasterImage? Bitmap, ReadOnlyMemory<byte> Metafile, int MetafileType) : PlusObj;

internal sealed record PlusFontObj(string Family, double EmSize, int Unit, int Style) : PlusObj;

internal sealed record PlusFormatObj(StringFormatSpec Spec, double LeadingMargin, double TrailingMargin) : PlusObj;

internal sealed record PlusAttributesObj(EdgeWrap Wrap, Rgba32 Clamp) : PlusObj;

/// <summary>An object that is stored and ignored (custom line caps).</summary>
internal sealed record PlusInertObj(string What) : PlusObj;

internal sealed partial class EmfInterpreter
{
    // ---- small decoders ----------------------------------------------------------------------------------------------

    private static Rgba32 Argb(uint v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24));

    private static Matrix3x2 ReadMatrix(ref PlusReader r) => new(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());

    private static EdgeWrap ToWrap(uint v) => v <= 4 ? (EdgeWrap)v : EdgeWrap.Tile;

    // ---- object records ----------------------------------------------------------------------------------------------

    private sealed class PendingObject
    {
        public int Id;
        public int Type;
        public int Total;
        public byte[] Buffer = [];
        public int Filled;
    }

    private PendingObject? _pending;

    private void PlusObject(ushort flags, ReadOnlyMemory<byte> data)
    {
        int id = flags & 0xFF;
        int type = (flags >> 8) & 0x7F;
        bool cont = (flags & 0x8000) != 0;
        if (id > 63)
        {
            Diag(DiagnosticSeverity.Warning, $"EMF+ object index {id} is out of range (0 to 63); the object is skipped.");
            return;
        }

        if (_pending is { } p && p.Id == id && p.Type == type)
        {
            // A later part of a continued object. The first record carries the total size; some writers repeat it, which is recognizable
            // because the repeated value equals the total.
            ReadOnlyMemory<byte> chunk = data;
            if (cont && data.Length >= 4 && BitConverter.ToUInt32(data.Span[..4]) == (uint)p.Total)
            {
                chunk = data[4..];
            }

            if (chunk.Length > p.Total - p.Filled)
            {
                Diag(DiagnosticSeverity.Warning, $"EMF+ object {id} continues past its declared size; it is skipped.");
                _pending = null;
                return;
            }

            chunk.CopyTo(p.Buffer.AsMemory(p.Filled));
            p.Filled += chunk.Length;
            if (p.Filled >= p.Total)
            {
                _pending = null;
                DefineObject(id, type, p.Buffer);
            }

            return;
        }

        _pending = null;
        if (!cont)
        {
            DefineObject(id, type, data);
            return;
        }

        if (data.Length < 4)
        {
            Truncated("EmfPlusObject");
            return;
        }

        uint total = BitConverter.ToUInt32(data.Span[..4]);
        if (total == 0 || total > (uint)_lim.MaxPlusObjectBytes)
        {
            Diag(DiagnosticSeverity.Warning, $"EMF+ object {id} declares {total} bytes, more than the limit of {_lim.MaxPlusObjectBytes}; it is skipped.");
            return;
        }

        ReadOnlyMemory<byte> first = data[4..];
        if (first.Length >= total)
        {
            DefineObject(id, type, first[..(int)total]);
            return;
        }

        // The size was checked against the limit before this allocation.
        var np = new PendingObject { Id = id, Type = type, Total = (int)total, Buffer = new byte[(int)total] };
        first.CopyTo(np.Buffer);
        np.Filled = first.Length;
        _pending = np;
    }

    private void DefineObject(int id, int type, ReadOnlyMemory<byte> data)
    {
        try
        {
            AddWork(data.Length / 64 + 1);
            PlusObj? obj = type switch
            {
                1 => ParseBrushObject(data),
                2 => ParsePenObject(data),
                3 => ParsePathObject(data),
                4 => ParseRegionObject(data),
                5 => ParseImageObject(data),
                6 => ParseFontObject(data),
                7 => ParseFormatObject(data),
                8 => ParseAttributesObject(data),
                9 => new PlusInertObj("custom line cap"),
                _ => null,
            };
            if (obj is null)
            {
                Diag(DiagnosticSeverity.Warning, $"EMF+ object type {type} (index {id}) is not known; it is skipped.");
                _plusObjects[id] = null;
                return;
            }

            if (obj is PlusInertObj inert)
            {
                Diag(DiagnosticSeverity.Info, $"EMF+ {inert.What} (index {id}) is stored but not drawn.");
            }

            _plusObjects[id] = obj;
        }
        catch (PlusDataException e)
        {
            _plusObjects[id] = null;
            Diag(DiagnosticSeverity.Warning, $"EMF+ object {id} (type {type}) is damaged ({e.Message}); it is skipped.");
        }
    }

    // ---- brushes -----------------------------------------------------------------------------------------------------

    private PlusBrushObj ParseBrushObject(ReadOnlyMemory<byte> data)
    {
        var r = new PlusReader(data);
        r.U32(); // version
        return new PlusBrushObj(ReadBrushBody(ref r, 0));
    }

    /// <summary>Reads the type and data of a brush; <paramref name="depth"/> limits a texture brush's image from holding another brush.</summary>
    private BrushSpec ReadBrushBody(ref PlusReader r, int depth)
    {
        uint type = r.U32();
        switch (type)
        {
            case 0:
                return new SolidBrushSpec(Argb(r.U32()));
            case 1:
                {
                    int style = (int)r.U32();
                    Rgba32 fore = Argb(r.U32());
                    Rgba32 back = Argb(r.U32());
                    return new StyleHatchBrushSpec(style, fore, back);
                }

            case 2:
                return ReadTextureBrush(ref r);
            case 3:
                return ReadPathGradient(ref r);
            case 4:
                return ReadLinearGradient(ref r);
            default:
                throw new PlusDataException($"brush type {type} is not known");
        }
    }

    private TextureBrushSpec ReadTextureBrush(ref PlusReader r)
    {
        uint flags = r.U32();
        EdgeWrap wrap = ToWrap(r.U32());
        Matrix3x2 m = (flags & 2) != 0 ? ReadMatrix(ref r) : Matrix3x2.Identity;
        PlusObj img = ReadImageBody(ref r);
        if (img is PlusImageObj { Bitmap: { } bmp })
        {
            return new TextureBrushSpec(bmp, wrap, m);
        }

        throw new PlusDataException("a texture brush needs a bitmap");
    }

    private static (float[] Positions, uint[] Colors) ReadBlendColors(ref PlusReader r)
    {
        uint n = r.U32();
        if (n > 4096)
        {
            throw new PlusDataException("too many gradient positions");
        }

        r.Need(n, 8);
        var pos = new float[n];
        for (int i = 0; i < n; i++)
        {
            pos[i] = r.F32();
        }

        var col = new uint[n];
        for (int i = 0; i < n; i++)
        {
            col[i] = r.U32();
        }

        return (pos, col);
    }

    private static (float[] Positions, float[] Factors) ReadBlendFactors(ref PlusReader r)
    {
        uint n = r.U32();
        if (n > 4096)
        {
            throw new PlusDataException("too many gradient positions");
        }

        r.Need(n, 8);
        var pos = new float[n];
        for (int i = 0; i < n; i++)
        {
            pos[i] = r.F32();
        }

        var f = new float[n];
        for (int i = 0; i < n; i++)
        {
            f[i] = r.F32();
        }

        return (pos, f);
    }

    private static Rgba32 Lerp(Rgba32 a, Rgba32 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        byte L(byte x, byte y) => (byte)Math.Clamp((int)MathF.Round(x + ((y - x) * t)), 0, 255);
        return new Rgba32(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B), L(a.A, b.A));
    }

    private static LinearGradientBrushSpec ReadLinearGradient(ref PlusReader r)
    {
        uint flags = r.U32();
        EdgeWrap wrap = ToWrap(r.U32());
        float x = r.F32(), y = r.F32(), w = r.F32(), h = r.F32();
        Rgba32 start = Argb(r.U32()), end = Argb(r.U32());
        r.U32();
        r.U32();
        Matrix3x2 m = (flags & 2) != 0 ? ReadMatrix(ref r) : Matrix3x2.Identity;
        var stops = new List<(float, Rgba32)>();
        if ((flags & 4) != 0)
        {
            (float[] pos, uint[] col) = ReadBlendColors(ref r);
            for (int i = 0; i < pos.Length; i++)
            {
                stops.Add((pos[i], Argb(col[i])));
            }
        }
        else if ((flags & 8) != 0)
        {
            (float[] pos, float[] fac) = ReadBlendFactors(ref r);
            for (int i = 0; i < pos.Length; i++)
            {
                stops.Add((pos[i], Lerp(start, end, fac[i])));
            }
        }

        if (stops.Count < 2)
        {
            stops.Clear();
            stops.Add((0f, start));
            stops.Add((1f, end));
        }
        else
        {
            // GDI+ extends the first and last colors to the ends of the gradient.
            if (stops[0].Item1 > 0)
            {
                stops.Insert(0, (0f, stops[0].Item2));
            }

            if (stops[^1].Item1 < 1)
            {
                stops.Add((1f, stops[^1].Item2));
            }
        }

        // The gradient runs along the rectangle's x axis in brush space; angles and scaling are in the brush transform.
        if (w <= 0)
        {
            w = 1e-3f;
        }

        return new LinearGradientBrushSpec(new Vector2(x, y), new Vector2(x + w, y), stops, wrap, m);
    }

    private PathGradientBrushSpec ReadPathGradient(ref PlusReader r)
    {
        uint flags = r.U32();
        EdgeWrap wrap = ToWrap(r.U32());
        Rgba32 center = Argb(r.U32());
        var cp = new Vector2(r.F32(), r.F32());
        uint nc = r.U32();
        if (nc == 0 || nc > _lim.MaxPathPoints)
        {
            throw new PlusDataException("a path gradient needs surround colors");
        }

        r.Need(nc, 4);
        var surround = new Rgba32[nc];
        for (int i = 0; i < nc; i++)
        {
            surround[i] = Argb(r.U32());
        }

        var boundary = new List<Vector2>();
        if ((flags & 1) != 0)
        {
            uint size = r.U32();
            r.Need(size, 1);
            ReadOnlyMemory<byte> pathData = r.Bytes((int)size);
            var pr = new PlusReader(pathData);
            pr.U32();
            (MetafilePath path, _) = ReadPathBody(ref pr);
            boundary.AddRange(FlattenFirstContour(path));
        }
        else
        {
            uint n = r.U32();
            if (n > _lim.MaxPathPoints)
            {
                throw new PlusDataException("too many boundary points");
            }

            r.Need(n, 8);
            for (int i = 0; i < n; i++)
            {
                boundary.Add(new Vector2(r.F32(), r.F32()));
            }
        }

        Matrix3x2 m = Matrix3x2.Identity;
        if ((flags & 2) != 0)
        {
            m = ReadMatrix(ref r);
        }

        // Preset colors and blend factors change the ramp; the interior ramp here is linear, so they are recorded as a diagnostic only.
        if ((flags & 0xC) != 0)
        {
            Diag(DiagnosticSeverity.Info, "A path gradient brush has a blend ramp, which is drawn as a plain two-color ramp.");
        }

        if (boundary.Count < 3)
        {
            throw new PlusDataException("a path gradient needs at least three boundary points");
        }

        var colors = new Rgba32[boundary.Count];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = surround[Math.Min(i, surround.Length - 1)];
        }

        return new PathGradientBrushSpec(boundary, colors, center, cp, wrap, m);
    }

    private static List<Vector2> FlattenFirstContour(MetafilePath path)
    {
        var pts = new List<Vector2>();
        foreach (OneImage.Drawing.Contour c in path.ToDrawingPath().Flatten(0.25f))
        {
            pts.AddRange(c.Points);
            break;
        }

        return pts;
    }

    // ---- pens --------------------------------------------------------------------------------------------------------

    private static LineCap PlainCap(int cap) => cap switch
    {
        1 => LineCap.Square,
        2 => LineCap.Round,
        _ => LineCap.Flat,
    };

    private PlusPenObj ParsePenObject(ReadOnlyMemory<byte> data)
    {
        var r = new PlusReader(data);
        r.U32(); // version
        r.U32(); // type, zero
        uint flags = r.U32();
        int unit = (int)r.U32();
        double width = r.F32();
        Matrix3x2? transform = null;
        int startCap = 0, endCap = 0;
        LineJoin join = LineJoin.Miter;
        double miter = 10;
        int style = 0, dashCap = 0;
        double dashOffset = 0;
        double[]? dashes = null;
        PenAlignment align = PenAlignment.Center;
        float[]? compound = null;
        if ((flags & 0x1) != 0)
        {
            transform = ReadMatrix(ref r);
        }

        if ((flags & 0x2) != 0)
        {
            startCap = r.I32();
        }

        if ((flags & 0x4) != 0)
        {
            endCap = r.I32();
        }

        if ((flags & 0x8) != 0)
        {
            join = r.I32() switch { 1 => LineJoin.Bevel, 2 => LineJoin.Round, _ => LineJoin.Miter };
        }

        if ((flags & 0x10) != 0)
        {
            miter = r.F32();
        }

        if ((flags & 0x20) != 0)
        {
            style = r.I32();
        }

        if ((flags & 0x40) != 0)
        {
            dashCap = r.I32();
        }

        if ((flags & 0x80) != 0)
        {
            dashOffset = r.F32();
        }

        if ((flags & 0x100) != 0)
        {
            uint n = r.U32();
            if (n > _lim.MaxDashEntries)
            {
                throw new PlusDataException("too many dash entries");
            }

            r.Need(n, 4);
            dashes = new double[n];
            for (int i = 0; i < n; i++)
            {
                dashes[i] = r.F32();
            }
        }

        if ((flags & 0x200) != 0)
        {
            align = r.I32() == 1 ? PenAlignment.Inset : PenAlignment.Center;
        }

        if ((flags & 0x400) != 0)
        {
            uint n = r.U32();
            if (n > _lim.MaxDashEntries)
            {
                throw new PlusDataException("too many compound line entries");
            }

            r.Need(n, 4);
            compound = new float[n];
            for (int i = 0; i < n; i++)
            {
                compound[i] = r.F32();
            }
        }

        if ((flags & 0x800) != 0)
        {
            r.Skip((int)Math.Min(r.U32(), (uint)r.Remaining));
        }

        if ((flags & 0x1000) != 0)
        {
            r.Skip((int)Math.Min(r.U32(), (uint)r.Remaining));
        }

        r.U32(); // the brush's version
        BrushSpec brush = ReadBrushBody(ref r, 0);

        // The line style names a preset pattern, in units of the pen width; a custom one is the dash data.
        double[]? pattern = style switch
        {
            1 => [3, 1],
            2 => [1, 1],
            3 => [3, 1, 1, 1],
            4 => [3, 1, 1, 1, 1, 1],
            _ => dashes, // GDI+ writes a custom pattern without a line style flag
        };
        if (pattern is { Length: 0 } || (pattern is not null && pattern.Any(v => !(v > 0))))
        {
            pattern = pattern is { Length: > 0 } ? [.. pattern.Select(v => Math.Max(v, 1e-3))] : null;
        }

        LineCap cap = startCap is 1 or 2 ? PlainCap(startCap) : LineCap.Flat;
        if (cap == LineCap.Flat && startCap == 0 && endCap is 1 or 2)
        {
            cap = PlainCap(endCap);
        }

        return new PlusPenObj(
            width,
            unit,
            cap,
            join,
            Math.Max(1, miter),
            pattern,
            dashOffset,
            dashCap == 2 ? LineCap.Round : dashCap == 3 ? LineCap.Flat : LineCap.Flat,
            align,
            startCap,
            endCap,
            compound,
            transform,
            brush);
    }

    // ---- paths and regions -------------------------------------------------------------------------------------------

    private PlusPathObj ParsePathObject(ReadOnlyMemory<byte> data)
    {
        var r = new PlusReader(data);
        r.U32();
        (MetafilePath path, bool winding) = ReadPathBody(ref r);
        return new PlusPathObj(path, winding);
    }

    /// <summary>Reads the count, flags, points and types of a path (after its version).</summary>
    private (MetafilePath Path, bool Winding) ReadPathBody(ref PlusReader r)
    {
        uint count = r.U32();
        uint flags = r.U32();
        if (count > _lim.MaxPathPoints)
        {
            throw new PlusDataException("the path has more points than the limit allows");
        }

        r.Need(count, 1);
        bool relative = (flags & 0x800) != 0;
        bool compressed = (flags & 0x4000) != 0;
        // The winding fill mode is bit 0x2000 of these flags in files GDI+ writes.
        bool winding = (flags & 0x2000) != 0;
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

        var types = new byte[count];
        if (relative)
        {
            int filled = 0;
            while (filled < types.Length)
            {
                byte b = r.U8();
                int run = b & 0x3F;
                byte t = r.U8();
                for (int i = 0; i < run && filled < types.Length; i++)
                {
                    types[filled++] = t;
                }
            }
        }
        else
        {
            for (int i = 0; i < types.Length; i++)
            {
                types[i] = r.U8();
            }
        }

        AddWork(count);
        var b2 = new MetafilePathBuilder(_lim.MaxPathPoints);
        BuildPath(b2, pts, types);
        return (b2.Build(), winding);
    }

    private static void ReadRelativePoints(ref PlusReader r, Vector2[] pts)
    {
        int x = 0, y = 0;
        static int Int(ref PlusReader rd)
        {
            byte b = rd.U8();
            if ((b & 0x80) != 0)
            {
                int v = ((b & 0x7F) << 8) | rd.U8();
                return (v & 0x4000) != 0 ? v - 0x8000 : v;
            }

            return (b & 0x40) != 0 ? b - 0x80 : b;
        }

        for (int i = 0; i < pts.Length; i++)
        {
            x += Int(ref r);
            y += Int(ref r);
            pts[i] = new Vector2(x, y);
        }
    }

    /// <summary>Turns GDI+ path points and point types into path steps.</summary>
    private static void BuildPath(MetafilePathBuilder b, Vector2[] pts, byte[] types)
    {
        for (int i = 0; i < pts.Length; i++)
        {
            int t = types[i] & 0x0F;
            bool close = (types[i] & 0x80) != 0;
            switch (t)
            {
                case 0:
                    b.MoveTo(pts[i]);
                    break;
                case 1:
                    if (!b.HasCurrentPoint)
                    {
                        b.MoveTo(pts[i]);
                    }
                    else
                    {
                        b.LineTo(pts[i]);
                    }

                    break;
                case 3:
                    if (i + 2 < pts.Length && b.HasCurrentPoint)
                    {
                        b.CubicTo(pts[i], pts[i + 1], pts[i + 2]);
                        i += 2;
                        close = (types[i] & 0x80) != 0;
                    }
                    else
                    {
                        i = pts.Length;
                    }

                    break;
                default:
                    break;
            }

            if (close && b.HasCurrentPoint)
            {
                b.Close();
            }
        }
    }

    private PlusRegionObj ParseRegionObject(ReadOnlyMemory<byte> data)
    {
        var r = new PlusReader(data);
        r.U32();
        uint count = r.U32();
        // The tree has count + 1 nodes (the field counts the children of the root).
        long total = (long)count + 1;
        if (total > _lim.MaxRegionRectangles)
        {
            throw new PlusDataException("the region has more nodes than the limit allows");
        }

        r.Need(total, 4);
        var nodes = new RegionNode[total];
        for (int i = 0; i < nodes.Length; i++)
        {
            uint type = r.U32();
            switch (type)
            {
                case >= 1 and <= 5:
                    nodes[i] = new RegionNode(type, null, false, default);
                    break;
                case 0x10000000:
                    {
                        float x = r.F32(), y = r.F32(), w = r.F32(), h = r.F32();
                        nodes[i] = new RegionNode(type, null, false, new RectD(x, y, x + w, y + h));
                        break;
                    }

                case 0x10000001:
                    {
                        uint len = r.U32();
                        r.Need(len, 1);
                        var pr = new PlusReader(r.Bytes((int)len));
                        pr.U32();
                        (MetafilePath path, bool winding) = ReadPathBody(ref pr);
                        nodes[i] = new RegionNode(type, path, winding, default);
                        break;
                    }

                case 0x10000002:
                case 0x10000003:
                    nodes[i] = new RegionNode(type, null, false, default);
                    break;
                default:
                    throw new PlusDataException($"region node type {type:x} is not known");
            }
        }

        return new PlusRegionObj(nodes);
    }

    // ---- images ------------------------------------------------------------------------------------------------------

    private PlusImageObj ParseImageObject(ReadOnlyMemory<byte> data)
    {
        var r = new PlusReader(data);
        return (PlusImageObj)ReadImageBody(ref r);
    }

    private PlusImageObj ReadImageBody(ref PlusReader r)
    {
        r.U32(); // version
        uint type = r.U32();
        if (type == 1)
        {
            return new PlusImageObj(ReadBitmap(ref r), default, 0);
        }

        if (type == 2)
        {
            int mtype = (int)r.U32();
            uint size = r.U32();
            r.Need(size, 1);
            return new PlusImageObj(null, r.Bytes((int)size), mtype);
        }

        throw new PlusDataException("the image is neither a bitmap nor a metafile");
    }

    private RasterImage ReadBitmap(ref PlusReader r)
    {
        int w = r.I32(), h = r.I32(), stride = r.I32();
        uint fmt = r.U32();
        uint kind = r.U32();
        if (kind == 1)
        {
            // A compressed bitmap is a whole image file; its size comes from the file, checked against the limits when it is decoded.
            return DecodeCompressed(r.Bytes(r.Remaining));
        }

        if (w <= 0 || h <= 0 || (long)w * h > _lim.MaxBitmapPixels)
        {
            throw new PlusDataException("the bitmap size is out of range");
        }

        _bitmapPixels += (long)w * h;
        if (_bitmapPixels > _lim.MaxTotalBitmapPixels)
        {
            throw new ImageLimitException("The metafile's bitmaps hold more pixels than the limit allows.");
        }

        AddWork(((long)w * h) >> 6);

        int bpp = (int)((fmt >> 8) & 0xFF);
        bool indexed = (fmt & 0x00010000) != 0;
        uint[]? palette = null;
        if (indexed)
        {
            r.U32(); // palette style flags
            uint n = r.U32();
            if (n > 256)
            {
                throw new PlusDataException("the palette is too large");
            }

            r.Need(n, 4);
            palette = new uint[n];
            for (int i = 0; i < n; i++)
            {
                palette[i] = r.U32();
            }
        }

        if (bpp is not (1 or 4 or 8 or 16 or 24 or 32 or 48 or 64))
        {
            throw new PlusDataException($"pixel format {fmt:x} is not supported");
        }

        long rowBytes = (((long)w * bpp) + 7) / 8;
        int absStride = Math.Abs(stride);
        if (absStride < rowBytes)
        {
            throw new PlusDataException("the stride is smaller than a row");
        }

        // The pixel data is checked against what is left before the output is allocated.
        r.Need((long)absStride * (h - 1) + rowBytes, 1);
        ReadOnlyMemory<byte> pixels = r.Bytes((int)Math.Min((long)absStride * h, r.Remaining));
        var rgba = new byte[(long)w * h * 4];
        ReadOnlySpan<byte> px = pixels.Span;
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * absStride;
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 4;
                (byte R, byte G, byte B, byte A) c = DecodePixel(px, rowStart, x, bpp, fmt, palette);
                rgba[o] = c.R;
                rgba[o + 1] = c.G;
                rgba[o + 2] = c.B;
                rgba[o + 3] = c.A;
            }
        }

        return new RasterImage(w, h, rgba);
    }

    private static (byte R, byte G, byte B, byte A) DecodePixel(ReadOnlySpan<byte> px, int row, int x, int bpp, uint fmt, uint[]? palette)
    {
        static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
        static byte Expand6(int v) => (byte)((v << 2) | (v >> 4));
        switch (bpp)
        {
            case 1:
            case 4:
            case 8:
                {
                    int bitPos = x * bpp;
                    int idx = (px[row + (bitPos >> 3)] >> (8 - bpp - (bitPos & 7))) & ((1 << bpp) - 1);
                    if (palette is null || idx >= palette.Length)
                    {
                        return (0, 0, 0, 0);
                    }

                    uint c = palette[idx];
                    return ((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
                }

            case 16:
                {
                    int v = px[row + (x * 2)] | (px[row + (x * 2) + 1] << 8);
                    if ((fmt & 0xFFFF) == 0x1006)
                    {
                        return (Expand5(v >> 11), Expand6((v >> 5) & 63), Expand5(v & 31), 255);
                    }

                    byte a = (fmt & 0x00040000) != 0 ? (byte)((v & 0x8000) != 0 ? 255 : 0) : (byte)255;
                    return (Expand5((v >> 10) & 31), Expand5((v >> 5) & 31), Expand5(v & 31), a);
                }

            case 24:
                return (px[row + (x * 3) + 2], px[row + (x * 3) + 1], px[row + (x * 3)], 255);
            case 32:
                {
                    int o = row + (x * 4);
                    byte b = px[o], g = px[o + 1], r = px[o + 2], a = px[o + 3];
                    if ((fmt & 0x00040000) == 0)
                    {
                        return (r, g, b, 255);
                    }

                    if ((fmt & 0x00080000) != 0 && a is > 0 and < 255)
                    {
                        r = (byte)Math.Min(255, ((r * 255) + (a / 2)) / a);
                        g = (byte)Math.Min(255, ((g * 255) + (a / 2)) / a);
                        b = (byte)Math.Min(255, ((b * 255) + (a / 2)) / a);
                    }

                    return (r, g, b, a);
                }

            case 48:
                return (px[row + (x * 6) + 5], px[row + (x * 6) + 3], px[row + (x * 6) + 1], 255);
            default:
                {
                    int o = row + (x * 8);
                    byte b = px[o + 1], g = px[o + 3], r = px[o + 5], a = px[o + 7];
                    if ((fmt & 0x00080000) != 0 && a is > 0 and < 255)
                    {
                        r = (byte)Math.Min(255, ((r * 255) + (a / 2)) / a);
                        g = (byte)Math.Min(255, ((g * 255) + (a / 2)) / a);
                        b = (byte)Math.Min(255, ((b * 255) + (a / 2)) / a);
                    }

                    return (r, g, b, a);
                }
        }
    }

    private RasterImage DecodeCompressed(ReadOnlyMemory<byte> file)
    {
        // The embedded picture is a PNG, JPEG, GIF, TIFF or BMP file; the core decoders read it under the same pixel limit.
        var limits = new ImageLimits { MaxPixelsPerFrame = _lim.MaxBitmapPixels, MaxFrames = 1 };
        try
        {
            Image img = Image.Load(file, new LoadOptions { Limits = limits });
            int w = img.Width, h = img.Height;
            if (w <= 0 || h <= 0 || (long)w * h > _lim.MaxBitmapPixels)
            {
                throw new PlusDataException("the embedded image size is out of range");
            }

            _bitmapPixels += (long)w * h;
            if (_bitmapPixels > _lim.MaxTotalBitmapPixels)
            {
                throw new ImageLimitException("The metafile's bitmaps hold more pixels than the limit allows.");
            }

            AddWork(((long)w * h) >> 6);
            Rgba32[] px = img.ToArray<Rgba32>();
            var rgba = new byte[px.Length * 4];
            for (int i = 0; i < px.Length; i++)
            {
                rgba[i * 4] = px[i].R;
                rgba[(i * 4) + 1] = px[i].G;
                rgba[(i * 4) + 2] = px[i].B;
                rgba[(i * 4) + 3] = px[i].A;
            }

            return new RasterImage(w, h, rgba);
        }
        catch (ImageFormatException e)
        {
            throw new PlusDataException("the embedded picture cannot be read: " + e.Message);
        }
        catch (ImageLimitException e)
        {
            throw new PlusDataException("the embedded picture is over a limit: " + e.Message);
        }
    }

    // ---- fonts, string formats, attributes ----------------------------------------------------------------------------

    private static PlusFontObj ParseFontObject(ReadOnlyMemory<byte> data)
    {
        var r = new PlusReader(data);
        r.U32();
        float em = r.F32();
        int unit = (int)r.U32();
        int style = r.I32();
        r.U32();
        uint len = r.U32();
        if (len > 1024)
        {
            throw new PlusDataException("the font name is too long");
        }

        r.Need(len, 2);
        string name = System.Text.Encoding.Unicode.GetString(r.Bytes((int)len * 2).Span).TrimEnd('\0');
        return new PlusFontObj(name, em, unit, style);
    }

    private static PlusFormatObj ParseFormatObject(ReadOnlyMemory<byte> data)
    {
        var r = new PlusReader(data);
        r.U32();
        uint flags = r.U32();
        r.U32(); // language
        int align = (int)r.U32();
        int lineAlign = (int)r.U32();
        r.U32();
        r.U32();
        r.F32(); // first tab offset
        r.U32();
        float lead = r.F32(), trail = r.F32();
        r.F32();
        int trimming = (int)r.U32();
        return new PlusFormatObj(
            new StringFormatSpec(align, lineAlign, (flags & 0x1000) != 0, (flags & 2) != 0, (flags & 1) != 0, (flags & 0x4000) != 0, trimming, true, lead, trail),
            lead,
            trail);
    }

    private static PlusAttributesObj ParseAttributesObject(ReadOnlyMemory<byte> data)
    {
        var r = new PlusReader(data);
        r.U32();
        r.U32();
        EdgeWrap wrap = ToWrap(r.U32());
        Rgba32 clamp = Argb(r.U32());
        return new PlusAttributesObj(wrap, clamp);
    }
}
