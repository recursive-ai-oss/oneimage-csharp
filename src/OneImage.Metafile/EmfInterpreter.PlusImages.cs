using System.Numerics;

namespace OneImage.Metafile;

/// <summary>EMF+ text and images.</summary>
internal sealed partial class EmfInterpreter
{
    private FontSpec PlusFont(PlusFontObj f)
    {
        // The em size is in the font's own unit. In world units it is used as is; other units are device sizes, divided by the scale of
        // the current transform to get world units (the command stream states sizes in the units of the path).
        double em = f.EmSize;
        if (f.Unit != 0)
        {
            double scale = PlusTotal().MeanScale;
            em = scale > 0 ? f.EmSize * UnitPx(f.Unit, true) / scale : f.EmSize;
        }

        int quality = _gs.TextHint is 1 or 2 ? 3 : 4;
        return new FontSpec(
            f.Family,
            -em,
            Weight: (f.Style & 1) != 0 ? 700 : 400,
            Italic: (f.Style & 2) != 0,
            Underline: (f.Style & 4) != 0,
            StrikeOut: (f.Style & 8) != 0,
            Quality: quality);
    }

    private void DrawStringRecord(ushort flags, ref PlusReader r)
    {
        BrushSpec? brush = BrushFor(flags, r.U32());
        uint formatId = r.U32();
        uint len = r.U32();
        var layout = new RectD(r.F32(), r.F32(), 0, 0);
        float w = r.F32(), h = r.F32();
        layout = new RectD(layout.Left, layout.Top, layout.Left + w, layout.Top + h);
        if (len > _lim.MaxTextCharacters)
        {
            throw new PlusDataException("the string is longer than the limit allows");
        }

        r.Need(len, 2);
        string text = System.Text.Encoding.Unicode.GetString(r.Bytes((int)len * 2).Span);
        AddWork(len);
        if (Obj(flags & 0xFF) is not PlusFontObj font)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ string uses a font object that does not exist; it is skipped.");
            return;
        }

        if (brush is null || text.Length == 0)
        {
            return;
        }

        StringFormatSpec fmt = formatId < 64 && _plusObjects[formatId] is PlusFormatObj fo ? fo.Spec : new StringFormatSpec(0, 0, false, false, false, false, 0, false);
        EnsurePlusTransform();
        Emit(new DrawStringCommand(text, PlusFont(font), layout, fmt, brush));
    }

    private void DrawDriverStringRecord(ushort flags, ref PlusReader r)
    {
        BrushSpec? brush = BrushFor(flags, r.U32());
        uint options = r.U32();
        uint matrixPresent = r.U32();
        uint count = r.U32();
        if (count > _lim.MaxTextCharacters)
        {
            throw new PlusDataException("the string is longer than the limit allows");
        }

        r.Need(count, 2);
        string text = System.Text.Encoding.Unicode.GetString(r.Bytes((int)count * 2).Span);
        r.Align4();
        bool realized = (options & 4) != 0;
        uint nPos = realized ? Math.Min(count, 1u) : count;
        r.Need(nPos, 8);
        var pos = new Vector2[nPos];
        for (int i = 0; i < pos.Length; i++)
        {
            pos[i] = new Vector2(r.F32(), r.F32());
        }

        Matrix3x2? m = matrixPresent != 0 ? ReadMatrix(ref r) : null;
        AddWork(count);
        if (Obj(flags & 0xFF) is not PlusFontObj font)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ driver string uses a font object that does not exist; it is skipped.");
            return;
        }

        if (brush is null || count == 0)
        {
            return;
        }

        EnsurePlusTransform();
        Emit(new DrawGlyphsCommand(text, (options & 1) == 0, realized ? null : pos, pos.Length > 0 ? pos[0] : Vector2.Zero, PlusFont(font), brush, (options & 2) != 0, m));
    }

    // ---- images ------------------------------------------------------------------------------------------------------

    private void DrawImageRecord(ushort flags, ref PlusReader r)
    {
        uint attr = r.U32();
        uint srcUnit = r.U32();
        var src = new RectD(r.F32(), r.F32(), 0, 0);
        float sw = r.F32(), sh = r.F32();
        src = new RectD(src.Left, src.Top, src.Left + sw, src.Top + sh);
        RectD dst = ReadRect(ref r, (flags & 0x4000) != 0);
        PlaceImage(flags & 0xFF, attr, srcUnit, src, new Vector2((float)dst.Left, (float)dst.Top), new Vector2((float)dst.Right, (float)dst.Top), new Vector2((float)dst.Left, (float)dst.Bottom));
    }

    private void DrawImagePoints(ushort flags, ref PlusReader r)
    {
        uint attr = r.U32();
        uint srcUnit = r.U32();
        var src = new RectD(r.F32(), r.F32(), 0, 0);
        float sw = r.F32(), sh = r.F32();
        src = new RectD(src.Left, src.Top, src.Left + sw, src.Top + sh);
        uint n = r.U32();
        if (n != 3)
        {
            throw new PlusDataException("DrawImagePoints needs three points");
        }

        List<Vector2> p = ReadPoints(ref r, n, flags);
        PlaceImage(flags & 0xFF, attr, srcUnit, src, p[0], p[1], p[2]);
    }

    private void PlaceImage(int imageId, uint attrId, uint srcUnit, RectD src, Vector2 tl, Vector2 tr, Vector2 bl)
    {
        if (Obj(imageId) is not PlusImageObj img)
        {
            Diag(DiagnosticSeverity.Warning, "An EMF+ image record uses an image object that does not exist; it is skipped.");
            return;
        }

        if (srcUnit != 2)
        {
            PlusNote("srcunit", DiagnosticSeverity.Warning, "An EMF+ image source rectangle is not in pixels; it is taken as pixels.");
        }

        EdgeWrap wrap = EdgeWrap.Tile;
        Rgba32 clamp = default;
        if (attrId < 64 && _plusObjects[attrId] is PlusAttributesObj a)
        {
            wrap = a.Wrap;
            clamp = a.Clamp;
        }
        else
        {
            wrap = EdgeWrap.Clamp;
        }

        if (img.Bitmap is { } bmp)
        {
            EnsurePlusTransform();
            Emit(new DrawImageCommand(bmp, src, tl, tr, bl, _gs.Interp, wrap, clamp));
            return;
        }

        PlayNestedMetafile(img, src, tl, tr, bl);
    }

    private void PlayNestedMetafile(PlusImageObj img, RectD src, Vector2 tl, Vector2 tr, Vector2 bl)
    {
        if (NestingDepth + 1 > _lim.MaxNestedMetafileDepth)
        {
            Diag(DiagnosticSeverity.Warning, $"Metafiles embedded in EMF+ images nest deeper than {_lim.MaxNestedMetafileDepth}; the inner one is skipped.");
            return;
        }

        List<DrawingCommand> cmds;
        MetafileInfo ni;
        try
        {
            // MetafileDataType 1 and 2 are Windows metafiles (plain and placeable): convert the records to EMF and play those.
            EmfInterpreter nested;
            if (img.MetafileType is 1 or 2)
            {
                WmfConverter.Result wmf = new WmfConverter(img.Metafile, _options, _cancel).Convert(false);
                nested = new EmfInterpreter(wmf.Emf, _options, wmf.Info, wmf.Map, _cancel) { NestingDepth = NestingDepth + 1 };
            }
            else
            {
                nested = new EmfInterpreter(img.Metafile, _options, _cancel) { NestingDepth = NestingDepth + 1 };
            }

            ni = nested.Info;
            cmds = nested.Run();
        }
        catch (ImageFormatException e)
        {
            Diag(DiagnosticSeverity.Warning, "An embedded metafile cannot be read: " + e.Message);
            return;
        }

        RectD pr = ni.PictureRectangle;
        double sw = src.Width > 0 ? src.Width : pr.Width, sh = src.Height > 0 ? src.Height : pr.Height;
        if (!(sw > 0) || !(sh > 0))
        {
            return;
        }

        Vector2 u = (tr - tl) / (float)sw, v = (bl - tl) / (float)sh;
        Mat toDest = Mat.Translate(-(pr.Left + src.Left), -(pr.Top + src.Top)).Then(new Mat(u.X, u.Y, v.X, v.Y, tl.X, tl.Y));
        Mat dev = PlusDeviceMatrix();
        Mat full = toDest.Then(dev);
        Matrix3x2 fullM = full.ToMatrix3x2();

        Mat savedEmitted = _emitted;
        Emit(new SaveStateCommand());
        Vector2 br = tr + bl - tl;
        MetafilePath quad = PolyPath([tl, tr, br, bl], true).Transform(dev.ToMatrix3x2());
        Emit(new ClipCommand(ClipMode.Intersect, quad));
        Emit(new SetTransformCommand(fullM));
        foreach (DrawingCommand c in cmds)
        {
            switch (c)
            {
                case BeginPictureCommand:
                    break;
                case SetTransformCommand st:
                    Emit(st with { Transform = st.Transform * fullM });
                    break;
                case ClipCommand { Path: { } cp } cc:
                    Emit(cc with { Path = cp.Transform(fullM) });
                    break;
                case OffsetClipCommand oc:
                    Emit(oc with { Delta = Vector2.TransformNormal(oc.Delta, fullM) });
                    break;
                default:
                    Emit(c);
                    break;
            }
        }

        Emit(new RestoreStateCommand());
        _emitted = savedEmitted;
    }
}
