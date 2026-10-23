using System.Buffers.Binary;
using OneImage.Drawing;
using OneImage.Layers;

namespace OneImage.Psd;

public sealed partial class PsdFile
{
    /// <summary>Saves the document as a PSD (or PSB) file.</summary>
    /// <param name="path">The file to create or replace.</param>
    /// <param name="options">Large-document and compression options; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ImageOperationException">A layer has no PSD equivalent (a custom adjustment), a layer style holds more than one outer glow, inner glow or satin (PSD stores one of each), or the document is wider or taller than a PSD or PSB allows.</exception>
    /// <exception cref="ImageLimitException">The file would need more than 4 GiB in a section of a PSD (use <see cref="PsdWriteOptions.Large"/>) or has more than 32,767 layer records.</exception>
    public void Save(string path, PsdWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        Save(fs, options, cancellationToken);
    }

    /// <summary>
    /// Saves the document to a stream. Every property the model holds is written: pixel layers at their offsets, nested groups (pass-through or
    /// isolated), blend modes, opacity, fill opacity, visibility, clipping, locks, masks with density and feather, solid color fills and the
    /// adjustments that have a PSD equivalent, gradient and pattern fills (as fill settings with the pattern resources, or as pixels when the brush has no
    /// equivalent) and layer styles (as an effects block; an untouched style keeps the block it was read from), followed by each layer's kept blocks unchanged, the image resources and document blocks, and the
    /// merged composite. Channels are run-length coded unless <see cref="PsdWriteOptions.Compression"/> says otherwise.
    /// </summary>
    /// <param name="stream">A writable stream; a seekable one is written in place, any other through a temporary file.</param>
    /// <param name="options">Large-document and compression options; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// The composite is rendered from the layers; it carries a transparency channel when <see cref="Document.Background"/> is not opaque.
    /// An RGB document is written as RGB at its own depth and a gray 8-bit document as gray; a file read from CMYK or Lab is written as RGB.
    /// The document's color profile is embedded.
    /// </remarks>
    /// <exception cref="ImageOperationException">See <see cref="Save(string, PsdWriteOptions?, CancellationToken)"/>.</exception>
    /// <exception cref="ImageLimitException">See <see cref="Save(string, PsdWriteOptions?, CancellationToken)"/>.</exception>
    public void Save(Stream stream, PsdWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new PsdWriteOptions();
        if (options.Large is null && WasLarge)
        {
            options = new PsdWriteOptions { Large = true, Compression = options.Compression };
        }

        Document doc = Document;
        PixelFormat fmt = doc.Format;
        bool gray = fmt.Model == ColorModel.Gray;
        int depth = fmt.Sample switch { SampleType.U16 => 16, SampleType.F32 => 32, _ => 8 };
        bool compositeAlpha = doc.Background.A < 255;
        Image rendered = doc.Render();
        var spec = new WriteSpec
        {
            Width = doc.Width,
            Height = doc.Height,
            Depth = depth,
            Gray = gray,
            CompositeAlpha = compositeAlpha,
            Icc = doc.ColorProfile.IccBytes.Length > 0 ? doc.ColorProfile.IccBytes.ToArray() : null,
            Composite = ImageSource(rendered, fmt, doc.Width, doc.Height, compositeAlpha),
        };
        spec.Resources.AddRange(Resources);
        spec.DocumentBlocks.AddRange(DocumentBlocks);
        try
        {
            Flatten(spec, cancellationToken);
            PsdWriter.Write(spec, stream, options, cancellationToken);
        }
        finally
        {
            foreach (IDisposable d in spec.Temporary)
            {
                d.Dispose();
            }
        }
    }

    private static IBandSource ImageSource(Image image, PixelFormat fmt, int w, int h, bool alpha)
    {
        if (fmt == PixelFormat.GrayAlpha8)
        {
            return TypedSource.For<GrayAlpha8>(fmt, w, h, alpha, (y, rows, buf) => image.CopyPixelsTo<GrayAlpha8>(buf.AsSpan(0, w * rows), new ImageRect(0, y, w, rows)));
        }

        if (fmt == PixelFormat.Rgba16)
        {
            return TypedSource.For<Rgba64>(fmt, w, h, alpha, (y, rows, buf) => image.CopyPixelsTo<Rgba64>(buf.AsSpan(0, w * rows), new ImageRect(0, y, w, rows)));
        }

        if (fmt == PixelFormat.RgbaF32)
        {
            return TypedSource.For<RgbaFloat>(fmt, w, h, alpha, (y, rows, buf) => image.CopyPixelsTo<RgbaFloat>(buf.AsSpan(0, w * rows), new ImageRect(0, y, w, rows)));
        }

        return TypedSource.For<Rgba32>(fmt, w, h, alpha, (y, rows, buf) => image.CopyPixelsTo<Rgba32>(buf.AsSpan(0, w * rows), new ImageRect(0, y, w, rows)));
    }

    private static IBandSource CanvasSource(Canvas canvas, CancellationToken ct)
    {
        PixelFormat fmt = canvas.Format;
        int w = canvas.Width;
        int h = canvas.Height;
        if (fmt == PixelFormat.GrayAlpha8)
        {
            return TypedSource.For<GrayAlpha8>(fmt, w, h, true, (y, rows, buf) => canvas.CopyTo<GrayAlpha8>(buf.AsSpan(0, w * rows), new ImageRect(0, y, w, rows), ct));
        }

        if (fmt == PixelFormat.Rgba16)
        {
            return TypedSource.For<Rgba64>(fmt, w, h, true, (y, rows, buf) => canvas.CopyTo<Rgba64>(buf.AsSpan(0, w * rows), new ImageRect(0, y, w, rows), ct));
        }

        if (fmt == PixelFormat.RgbaF32)
        {
            return TypedSource.For<RgbaFloat>(fmt, w, h, true, (y, rows, buf) => canvas.CopyTo<RgbaFloat>(buf.AsSpan(0, w * rows), new ImageRect(0, y, w, rows), ct));
        }

        return TypedSource.For<Rgba32>(fmt, w, h, true, (y, rows, buf) => canvas.CopyTo<Rgba32>(buf.AsSpan(0, w * rows), new ImageRect(0, y, w, rows), ct));
    }

    // A mask is a Gray8 canvas; the file stores it at the document's depth.
    private static TypedSource MaskSource(Canvas mask, int depth, CancellationToken ct)
    {
        int w = mask.Width;
        int h = mask.Height;
        Gray8[]? buf = null;
        return new TypedSource(w, h, depth, 1, false, (y, _, planes, width, rows) =>
        {
            buf ??= new Gray8[w * 128];
            if (buf.Length < w * rows)
            {
                buf = new Gray8[w * rows];
            }

            mask.CopyTo<Gray8>(buf.AsSpan(0, w * rows), new ImageRect(0, y, w, rows), ct);
            for (int i = 0; i < w * rows; i++)
            {
                byte v = buf[i].V;
                switch (depth)
                {
                    case 8:
                        planes[0][i] = v;
                        break;
                    case 16:
                        BinaryPrimitives.WriteUInt16BigEndian(planes[0].AsSpan(i * 2), (ushort)(v * 257));
                        break;
                    default:
                        BinaryPrimitives.WriteSingleBigEndian(planes[0].AsSpan(i * 4), v / 255f);
                        break;
                }
            }

            return true;
        });
    }

    private void Flatten(WriteSpec spec, CancellationToken ct)
    {
        const int Close = -1;
        var stack = new Stack<(GroupLayer Group, int Index)>();
        stack.Push((Document.Root, 0));
        while (stack.Count > 0)
        {
            (GroupLayer g, int i) = stack.Pop();
            if (i == Close)
            {
                spec.Layers.Add(BuildEntry(g, EntryKind.GroupOpener, spec, ct));
                continue;
            }

            if (i >= g.Children.Count)
            {
                continue;
            }

            stack.Push((g, i + 1));
            Layer l = g.Children[i];
            if (l is GroupLayer sub)
            {
                spec.Layers.Add(BuildEntry(sub, EntryKind.GroupDivider, spec, ct));
                stack.Push((sub, Close));
                stack.Push((sub, 0));
            }
            else
            {
                spec.Layers.Add(BuildEntry(l, EntryKind.Normal, spec, ct));
            }
        }
    }

    private static PsdBlock Block(string key, params byte[] data) => new(key, data);

    private static byte[] Be32(uint v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private LayerEntry BuildEntry(Layer l, EntryKind kind, WriteSpec spec, CancellationToken ct)
    {
        PsdLayerInfo info = GetInfo(l);
        int depth = spec.Depth;
        var e = new LayerEntry { Kind = kind, Name = l.Name };
        if (kind == EntryKind.GroupDivider)
        {
            e.Name = "</Layer group>";
            e.Blocks.Add(Block("lsct", [.. Be32(3), .. "8BIM"u8, .. "norm"u8]));
            return e;
        }

        e.Opacity = (byte)Math.Clamp((int)Math.Round(l.Opacity * 255f), 0, 255);
        e.Clipped = l.ClipToBelow;
        e.Flags = (byte)(8 | (l.Visible ? 0 : 2) | ((l.Locks & LayerLocks.TransparentPixels) != 0 ? 1 : 0) | (info.SourceFlags & 0x10));

        // Blocks the model regenerates come first, the kept ones after.
        e.Blocks.Add(UnicodeNameBlock(l.Name));
        if (info.LayerId is { } id)
        {
            e.Blocks.Add(Block("lyid", Be32((uint)id)));
        }

        if (l.FillOpacity < 1f)
        {
            e.Blocks.Add(Block("iOpa", (byte)Math.Clamp((int)Math.Round(l.FillOpacity * 255f), 0, 255), 0, 0, 0));
        }

        if (l.Locks != LayerLocks.None)
        {
            uint f = l.Locks == LayerLocks.All ? 0x80000000 : (uint)(((l.Locks & LayerLocks.TransparentPixels) != 0 ? 1 : 0) | ((l.Locks & LayerLocks.Pixels) != 0 ? 2 : 0) | ((l.Locks & LayerLocks.Position) != 0 ? 4 : 0));
            e.Blocks.Add(Block("lspf", Be32(f)));
        }

        // An untouched style keeps the effects blocks it was read from (they are among the kept blocks below). A style that was replaced or edited gets a
        // new block that keeps the items of the old one the model does not carry; the old block and its legacy twin (which would still show the old
        // effects) are left out.
        bool styleEdited = !ReferenceEquals(l.Style, info.SourceStyle);
        var stylePatterns = new List<EffectPattern>();
        byte[]? styleData = styleEdited ? EffectsWriter.Write(l.Style, info.SourceFx, stylePatterns) : null;
        foreach (EffectPattern ep in stylePatterns)
        {
            if (PatternLibrary.Find(spec.DocumentBlocks, FillWriter.PatternId(ep)) == null)
            {
                AddPattern(spec, FillWriter.ResourceFor(ep, spec.Depth) ?? throw new ImageOperationException("A pattern in a layer style cannot be stored in this document (it has transparent pixels, or the document is not 8 or 16 bits)."));
            }
        }

        switch (l)
        {
            case GroupLayer g:
                e.BlendKey = g.PassThrough ? "pass" : LayerLoader.BlendKeyFor(g.BlendMode);
                e.Blocks.Add(Block("lsct", [.. Be32(info.GroupOpen ? 1u : 2u), .. "8BIM"u8, .. System.Text.Encoding.ASCII.GetBytes(e.BlendKey)]));
                break;
            case PixelLayer pl:
                e.BlendKey = BlendKey(l, info);
                e.Left = pl.X;
                e.Top = pl.Y;
                e.Right = pl.X + pl.Pixels.Width;
                e.Bottom = pl.Y + pl.Pixels.Height;
                e.Pixels = CanvasSource(pl.Pixels, ct);
                break;
            case FillLayer fl:
                {
                    e.BlendKey = BlendKey(l, info);
                    e.Left = 0;
                    e.Top = 0;
                    e.Right = Document.Width;
                    e.Bottom = Document.Height;
                    if (info.SourceFillBlock != null && ReferenceEquals(fl.Brush, info.SourceFillBrush))
                    {
                        // An unchanged gradient or pattern fill keeps its settings block. The raster Photoshop stores beside it is the fill drawn over the document.
                        e.Pixels = CanvasSource(DrawFill(fl.Brush, spec), ct);
                        e.Blocks.Add(info.SourceFillBlock);
                        break;
                    }

                    if (fl.Brush.TryGetSolid(out Rgba32 c))
                    {
                        e.Pixels = ConstantColor(c, spec);
                        e.Blocks.Add(new PsdBlock("SoCo", Descriptor.WriteSolidColor(c.R, c.G, c.B)));
                        break;
                    }

                    // A new or edited gradient or pattern brush becomes the settings Photoshop keeps for such a fill when it maps to one, with the fill drawn
                    // beside them as for a fill that was read. A brush that does not map is written as the pixels it draws.
                    FillPlan? plan = spec.Gray || spec.Depth > 16 ? null : FillWriter.Map(fl.Brush, Document.Width, Document.Height, spec.Depth);
                    e.Pixels = CanvasSource(DrawFill(fl.Brush, spec), ct);
                    if (plan != null)
                    {
                        e.Blocks.Add(new PsdBlock(plan.Key, plan.Data));
                        if (plan.Pattern != null)
                        {
                            AddPattern(spec, plan.Pattern);
                        }
                    }

                    break;
                }

            case AdjustmentLayer al:
                {
                    e.BlendKey = BlendKey(l, info);
                    // An adjustment kept only as a placeholder was made for CMYK or Lab pixels (or could not be read); its parameters mean nothing in the
                    // RGB document written here, so an identity levels adjustment takes its place in the stack.
                    bool foreignSpace = info.Kind == PsdLayerKind.UnsupportedAdjustment && SourceColorMode is not (PsdColorMode.Rgb or PsdColorMode.Grayscale);
                    PsdBlock? block = !foreignSpace && ReferenceEquals(info.SourceAdjustment, al.Adjustment) && info.SourceAdjustmentBlock != null
                        ? info.SourceAdjustmentBlock
                        : AdjustmentCodec.Write(foreignSpace ? Adjustment.Levels() : al.Adjustment);
                    if (block == null)
                    {
                        throw new ImageOperationException($"The adjustment layer '{l.Name}' ({al.Adjustment.Name}) has no PSD equivalent.");
                    }

                    e.Blocks.Add(block);
                    break;
                }
        }

        bool styleWritten = false;
        foreach (PsdBlock b in info.Blocks)
        {
            if (styleEdited && b.Key is "lfx2" or "lmfx" or "lrFX")
            {
                if (styleData != null && b.Key is "lfx2" or "lmfx")
                {
                    e.Blocks.Add(new PsdBlock(b.Key, styleData, b.Large));
                    styleWritten = true;
                }

                continue;
            }

            e.Blocks.Add(b);
        }

        if (styleData != null && !styleWritten)
        {
            e.Blocks.Add(new PsdBlock("lfx2", styleData));
        }

        if (l.Mask is { } m)
        {
            ImageRect a = m.Area;
            e.MaskPixels = MaskSource(m.Pixels, depth, ct);
            e.MaskLeft = a.X;
            e.MaskTop = a.Y;
            e.MaskRight = a.Right;
            e.MaskBottom = a.Bottom;
            e.MaskDefault = m.OutsideValue;
            e.MaskDisabled = !m.Enabled;
            // A mask built from a vector mask says so; the reader then does not apply the path a second time.
            e.MaskRendered = info.Blocks.Any(static b => b.Key is "vmsk" or "vsms");
            e.MaskDensity = (byte)Math.Clamp((int)Math.Round(m.Density * 255f), 0, 255);
            e.MaskFeather = m.Feather;
        }

        return e;
    }

    private Canvas DrawFill(Brush brush, WriteSpec spec)
    {
        var canvas = new Canvas(Document.Width, Document.Height, Document.Format);
        spec.Temporary.Add(canvas);
        canvas.Fill(brush, OneImage.Drawing.Path.Rectangle(0, 0, Document.Width, Document.Height), antialias: false, composition: AlphaComposition.Src);
        return canvas;
    }

    // Adds a pattern to the pattern resources the file is written with (the model's own blocks are left alone). Patterns are stored per bit depth, and a
    // pattern the document already holds is not stored twice.
    private static void AddPattern(WriteSpec spec, PatternResource pattern)
    {
        if (PatternLibrary.Find(spec.DocumentBlocks, pattern.Id) != null)
        {
            return;
        }

        string key = spec.Depth == 16 ? "Pat2" : "Patt";
        int at = spec.DocumentBlocks.FindIndex(b => b.Key == key);
        if (at < 0)
        {
            spec.DocumentBlocks.Add(new PsdBlock(key, pattern.Entry));
        }
        else
        {
            PsdBlock old = spec.DocumentBlocks[at];
            spec.DocumentBlocks[at] = new PsdBlock(key, old.Data.Span.ToArray().Concat(pattern.Entry).ToArray(), old.Large);
        }
    }

    private static string BlendKey(Layer l, PsdLayerInfo info) =>
        info.SourceBlendKey != null && l.BlendMode == info.SourceBlendMode ? info.SourceBlendKey : LayerLoader.BlendKeyFor(l.BlendMode);

    private static PsdBlock UnicodeNameBlock(string name)
    {
        byte[] utf = System.Text.Encoding.BigEndianUnicode.GetBytes(name);
        byte[] data = new byte[4 + utf.Length + 2];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)(name.Length + 1));
        utf.CopyTo(data, 4);
        return new PsdBlock("luni", data);
    }

    private ConstantSource ConstantColor(Rgba32 c, WriteSpec spec)
    {
        int depth = spec.Depth;
        int bps = depth / 8;
        byte[] Sample(byte v)
        {
            byte[] b = new byte[bps];
            switch (depth)
            {
                case 8:
                    b[0] = v;
                    break;
                case 16:
                    BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)(v * 257));
                    break;
                default:
                    BinaryPrimitives.WriteSingleBigEndian(b, v / 255f);
                    break;
            }

            return b;
        }

        byte[][] values = spec.Gray ? [Sample(c.R), Sample(255)] : [Sample(c.R), Sample(c.G), Sample(c.B), Sample(255)];
        return new ConstantSource(Document.Width, Document.Height, depth, spec.Gray ? 1 : 3, true, values);
    }
}
