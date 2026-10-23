using System.Buffers.Binary;
using System.Text;
using OneImage.Drawing;
using OneImage.Layers;
using DPath = OneImage.Drawing.Path;

namespace OneImage.Psd;

/// <summary>Builds the layer tree of a <see cref="PsdFile"/> from parsed layer records, reading channel data a band of rows at a time.</summary>
internal sealed class LayerLoader
{
    private static readonly (string Key, BlendMode Mode)[] BlendKeys =
    [
        ("norm", BlendMode.Normal), ("mul ", BlendMode.Multiply), ("scrn", BlendMode.Screen), ("over", BlendMode.Overlay), ("dark", BlendMode.Darken),
        ("lite", BlendMode.Lighten), ("div ", BlendMode.ColorDodge), ("idiv", BlendMode.ColorBurn), ("hLit", BlendMode.HardLight), ("sLit", BlendMode.SoftLight),
        ("diff", BlendMode.Difference), ("smud", BlendMode.Exclusion), ("lddg", BlendMode.LinearDodge), ("fsub", BlendMode.Subtract), ("lbrn", BlendMode.LinearBurn),
        ("vLit", BlendMode.VividLight), ("lLit", BlendMode.LinearLight), ("pLit", BlendMode.PinLight), ("hMix", BlendMode.HardMix), ("fdiv", BlendMode.Divide),
        ("hue ", BlendMode.Hue), ("sat ", BlendMode.Saturation), ("colr", BlendMode.Color), ("lum ", BlendMode.Luminosity),
    ];

    private readonly PsdFile _file;
    private readonly PsdStructure _s;
    private readonly SampleKind _kind;
    private readonly bool _gray;
    private readonly ColorProfile? _sourceProfile;
    private readonly ImageLimits _limits;
    private readonly PsdLimits _psd;
    private readonly CancellationToken _ct;
    private readonly Document _doc;
    private readonly List<GroupLayer> _stack = [];

    public LayerLoader(PsdFile file, PsdStructure s, SampleKind kind, bool gray, ColorProfile? sourceProfile, ImageLimits limits, PsdLimits psd, CancellationToken ct)
    {
        _file = file;
        _s = s;
        _kind = kind;
        _gray = gray;
        _sourceProfile = sourceProfile;
        _limits = limits;
        _psd = psd;
        _ct = ct;
        _doc = file.Document;
        _stack.Add(_doc.Root);
    }

    public static BlendMode? MapBlend(uint key)
    {
        string k = Fourcc.Name(key);
        foreach ((string name, BlendMode mode) in BlendKeys)
        {
            if (name == k)
            {
                return mode;
            }
        }

        return null;
    }

    public static string BlendKeyFor(BlendMode mode)
    {
        foreach ((string name, BlendMode m) in BlendKeys)
        {
            if (m == mode)
            {
                return name;
            }
        }

        return "norm";
    }

    public void Run()
    {
        foreach (RawLayer rl in _s.Layers)
        {
            _ct.ThrowIfCancellationRequested();
            Process(rl);
        }
    }

    private GroupLayer Current => _stack[^1];

    private void Process(RawLayer rl)
    {
        var info = new PsdLayerInfo { SourceFlags = rl.Flags };
        PsdBlock? sectionBlock = null;
        PsdBlock? unicodeName = null;
        PsdBlock? fillOpacity = null;
        PsdBlock? lockBlock = null;
        PsdBlock? idBlock = null;
        var keys = new HashSet<string>();
        foreach (PsdBlock b in rl.Blocks)
        {
            keys.Add(b.Key);
            switch (b.Key)
            {
                case "lsct" or "lsdk":
                    sectionBlock ??= b;
                    break;
                case "luni":
                    unicodeName ??= b;
                    break;
                case "iOpa":
                    fillOpacity ??= b;
                    break;
                case "lspf":
                    lockBlock ??= b;
                    break;
                case "lyid":
                    idBlock ??= b;
                    break;
            }
        }

        int divider = 0;
        if (sectionBlock != null && sectionBlock.Data.Length >= 4)
        {
            divider = (int)BinaryPrimitives.ReadUInt32BigEndian(sectionBlock.Data.Span);
        }

        if (divider == 3)
        {
            // The bottom marker of a group: everything up to the matching top record belongs to it.
            if (_stack.Count > _psd.MaxNesting)
            {
                throw new ImageLimitException($"Groups are nested deeper than {_psd.MaxNesting}.");
            }

            GroupLayer opened = _doc.CreateGroup("Group");
            Current.Add(opened);
            _stack.Add(opened);
            return;
        }

        string name = unicodeName != null ? ReadUnicodeName(unicodeName.Data.Span, rl.Name) : rl.Name;
        if (idBlock != null && idBlock.Data.Length >= 4)
        {
            info.LayerId = BinaryPrimitives.ReadInt32BigEndian(idBlock.Data.Span);
        }

        Layer layer;
        if (divider is 1 or 2)
        {
            GroupLayer g;
            if (_stack.Count > 1)
            {
                g = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
            }
            else
            {
                g = _doc.CreateGroup(name);
                Current.Add(g);
            }

            g.PassThrough = Fourcc.Name(rl.BlendKey) == "pass" || (sectionBlock!.Data.Length >= 12 && Fourcc.Name(BinaryPrimitives.ReadUInt32BigEndian(sectionBlock.Data.Span.Slice(8))) == "pass");
            info.Kind = PsdLayerKind.Group;
            info.GroupOpen = divider == 1;
            layer = g;
        }
        else
        {
            layer = CreateContentLayer(rl, name, keys, info, out PsdBlock? consumed);
            foreach (PsdBlock b in rl.Blocks)
            {
                if (!ReferenceEquals(b, consumed) && !ReferenceEquals(b, unicodeName) && !ReferenceEquals(b, fillOpacity) && !ReferenceEquals(b, lockBlock) && !ReferenceEquals(b, idBlock) && !ReferenceEquals(b, sectionBlock))
                {
                    info.Blocks.Add(b);
                }
            }
        }

        if (layer is GroupLayer)
        {
            foreach (PsdBlock b in rl.Blocks)
            {
                if (!ReferenceEquals(b, unicodeName) && !ReferenceEquals(b, fillOpacity) && !ReferenceEquals(b, lockBlock) && !ReferenceEquals(b, idBlock) && !ReferenceEquals(b, sectionBlock))
                {
                    info.Blocks.Add(b);
                }
            }
        }

        layer.Name = name;
        layer.Visible = (rl.Flags & 2) == 0;
        layer.Opacity = rl.Opacity / 255f;
        if (fillOpacity != null && fillOpacity.Data.Length >= 1)
        {
            layer.FillOpacity = fillOpacity.Data.Span[0] / 255f;
        }

        if (layer is GroupLayer { PassThrough: false } isolatedGroup)
        {
            // An isolated group keeps its own blend mode; a pass-through group has none.
            isolatedGroup.BlendMode = MapBlend(rl.BlendKey) ?? BlendMode.Normal;
        }

        if (layer is not GroupLayer)
        {
            BlendMode? mapped = MapBlend(rl.BlendKey);
            string bk = Fourcc.Name(rl.BlendKey);
            if (mapped is null)
            {
                // Dissolve and the darker/lighter color modes have no equivalent: keep the key, render with the nearest mode.
                info.SourceBlendKey = bk;
                mapped = bk switch { "dkCl" => BlendMode.Darken, "lgCl" => BlendMode.Lighten, _ => BlendMode.Normal };
                info.SourceBlendMode = mapped.Value;
            }

            layer.BlendMode = mapped.Value;
        }

        layer.ClipToBelow = rl.Clipping != 0;
        LayerLocks locks = (rl.Flags & 1) != 0 ? LayerLocks.TransparentPixels : LayerLocks.None;
        if (lockBlock != null && lockBlock.Data.Length >= 4)
        {
            uint f = BinaryPrimitives.ReadUInt32BigEndian(lockBlock.Data.Span);
            if ((f & 0x80000000) != 0)
            {
                locks = LayerLocks.All;
            }
            else
            {
                locks |= ((f & 1) != 0 ? LayerLocks.TransparentPixels : 0) | ((f & 2) != 0 ? LayerLocks.Pixels : 0) | ((f & 4) != 0 ? LayerLocks.Position : 0);
            }
        }

        layer.Locks = locks;
        PsdBlock? vector = rl.Blocks.FirstOrDefault(static b => b.Key is "vmsk" or "vsms");
        if (rl.Mask != null || vector != null)
        {
            ReadMask(layer, rl, vector);
        }

        _file.Infos[layer] = info;
    }

    private static string ReadUnicodeName(ReadOnlySpan<byte> data, string fallback)
    {
        try
        {
            var r = new SpanReader(data);
            string s = r.UnicodeString();
            return s.Length > 0 ? s : fallback;
        }
        catch (ImageFormatException)
        {
            return fallback;
        }
    }

    private Layer CreateContentLayer(RawLayer rl, string name, HashSet<string> keys, PsdLayerInfo info, out PsdBlock? consumed)
    {
        consumed = null;
        foreach (PsdBlock b in rl.Blocks)
        {
            if (AdjustmentCodec.IsAdjustmentKey(b.Key))
            {
                Adjustment? adj = AdjustmentCodec.Read(b.Key, b.Data.Span, _s.Header.Mode);
                info.SourceAdjustmentBlock = b;
                consumed = b;
                if (adj == null)
                {
                    // Kept, written back, but not reproduced: an identity placeholder holds its place in the stack.
                    adj = Adjustment.Custom("psd-" + b.Key.Trim(), static i => i);
                    info.Kind = PsdLayerKind.UnsupportedAdjustment;
                }
                else
                {
                    info.Kind = PsdLayerKind.Adjustment;
                }

                info.SourceAdjustment = adj;
                AdjustmentLayer al = _doc.CreateAdjustmentLayer(name, adj);
                Current.Add(al);
                return al;
            }
        }

        if (keys.Contains("SoCo"))
        {
            PsdBlock so = rl.Blocks.First(static b => b.Key == "SoCo");
            Rgba32? color = ReadSolidColor(so.Data.Span);
            if (color is { } c)
            {
                consumed = so;
                info.Kind = PsdLayerKind.SolidColorFill;
                FillLayer fl = _doc.CreateFillLayer(name, c);
                Current.Add(fl);
                return fl;
            }
        }

        info.Kind = keys.Contains("TySh") || keys.Contains("tySh") ? PsdLayerKind.Text
            : keys.Contains("SoLd") || keys.Contains("PlLd") || keys.Contains("SoLE") ? PsdLayerKind.SmartObject
            : keys.Contains("vmsk") || keys.Contains("vsms") ? PsdLayerKind.Shape
            : keys.Contains("GdFl") || keys.Contains("PtFl") ? PsdLayerKind.GradientOrPatternFill
            : PsdLayerKind.Pixel;

        int w = rl.Width;
        int h = rl.Height;
        PixelLayer pl;
        if (w <= 0 || h <= 0)
        {
            info.WasEmpty = true;
            pl = _doc.CreatePixelLayer(name, 1, 1, rl.Left, rl.Top);
        }
        else
        {
            pl = _doc.CreatePixelLayer(name, w, h, rl.Left, rl.Top);
            ReadPixels(pl, rl);
        }

        Current.Add(pl);
        return pl;
    }

    private Rgba32? ReadSolidColor(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 8)
            {
                return null;
            }

            Descriptor d = Descriptor.Parse(data.Slice(4), _psd);
            if (d.Get("Clr ") is Descriptor clr && clr.Number("Rd  ") is { } r && clr.Number("Grn ") is { } g && clr.Number("Bl  ") is { } b)
            {
                return new Rgba32(ToByte(r), ToByte(g), ToByte(b), 255);
            }

            return null;
        }
        catch (ImageFormatException)
        {
            return null;
        }
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static int BandRows(int w, int bytesPerRowAllChannels) =>
        Math.Clamp((int)(8L * 1024 * 1024 / Math.Max(1, (long)w * bytesPerRowAllChannels)), 1, 128);

    private void ReadPixels(PixelLayer pl, RawLayer rl)
    {
        PsdHeader hd = _s.Header;
        int w = rl.Width;
        int h = rl.Height;
        int nColor = BandConverter.ColorChannelCount(hd.Mode);
        var color = new ChannelReader?[nColor];
        ChannelReader? alpha = null;
        foreach (RawChannel ch in rl.Channels)
        {
            if (ch.Id is < -1 or > 63 || (ch.Id >= nColor) || ch.Length <= 2)
            {
                continue;
            }

            ChannelReader reader = ChannelReader.ForLayerChannel(_s.Io, ch.Position, ch.Length, w, h, hd.Depth, hd.Psb, _psd, _limits);
            if (ch.Id == -1)
            {
                alpha = reader;
            }
            else
            {
                color[ch.Id] = reader;
            }
        }

        if (alpha == null && Array.TrueForAll(color, static c => c == null))
        {
            return;
        }

        int rowBytes = ChannelReader.RowBytesFor(w, hd.Depth);
        int rows = BandRows(w, Math.Max(rowBytes, w * 4) * (nColor + 3));
        var conv = new BandConverter(hd.Mode, hd.Depth, _kind, w, rows, _s.ColorModeData, _doc.ColorProfile, hd.Mode is PsdColorMode.Cmyk or PsdColorMode.Lab ? _s.IccProfile : null);
        byte[][] colorBuf = new byte[nColor][];
        for (int c = 0; c < nColor; c++)
        {
            colorBuf[c] = new byte[rows * rowBytes];
        }

        byte[]? alphaBuf = alpha != null ? new byte[rows * rowBytes] : null;
        int cap = w * rows;
        Rgba32[]? p8 = null;
        GrayAlpha8[]? pg = null;
        Rgba64[]? p16 = null;
        RgbaFloat[]? pf = null;
        if (_gray)
        {
            pg = new GrayAlpha8[cap];
        }
        else if (_kind == SampleKind.U8)
        {
            p8 = new Rgba32[cap];
        }
        else if (_kind == SampleKind.U16)
        {
            p16 = new Rgba64[cap];
        }
        else
        {
            pf = new RgbaFloat[cap];
        }

        for (int y0 = 0; y0 < h; y0 += rows)
        {
            _ct.ThrowIfCancellationRequested();
            int n = Math.Min(rows, h - y0);
            for (int c = 0; c < nColor; c++)
            {
                for (int y = 0; y < n; y++)
                {
                    Span<byte> dst = colorBuf[c].AsSpan(y * rowBytes, rowBytes);
                    if (color[c] is { } reader)
                    {
                        reader.ReadRow(y0 + y, dst);
                    }
                    else
                    {
                        dst.Clear();
                    }
                }
            }

            if (alpha != null)
            {
                for (int y = 0; y < n; y++)
                {
                    alpha.ReadRow(y0 + y, alphaBuf.AsSpan(y * rowBytes, rowBytes));
                }
            }

            Planes planes = conv.Convert(colorBuf, alphaBuf, n, rowBytes);
            int count = w * n;
            var rect = new ImageRect(0, y0, w, n);
            if (pg != null)
            {
                Interleave.ToGrayAlpha8(planes, count, pg);
                pl.Pixels.CopyFrom<GrayAlpha8>(pg.AsSpan(0, count), rect);
            }
            else if (p8 != null)
            {
                Interleave.ToRgba32(planes, count, p8);
                pl.Pixels.CopyFrom<Rgba32>(p8.AsSpan(0, count), rect);
            }
            else if (p16 != null)
            {
                Interleave.ToRgba64(planes, count, p16);
                pl.Pixels.CopyFrom<Rgba64>(p16.AsSpan(0, count), rect);
            }
            else
            {
                Interleave.ToRgbaFloat(planes, count, pf!);
                pl.Pixels.CopyFrom<RgbaFloat>(pf.AsSpan(0, count), rect);
            }
        }
    }

    // A layer's mask is the raster mask channel, the vector mask rasterized, or both multiplied. A raster mask whose flags say it was
    // rendered from other data already contains the vector mask (that is how this library writes one), so the path is not applied again.
    private void ReadMask(Layer layer, RawLayer rl, PsdBlock? vector)
    {
        RawMask? m = rl.Mask;
        PsdHeader hd = _s.Header;
        RawChannel? ch = m == null ? null : rl.Channels.FirstOrDefault(static c => c.Id == -3) ?? rl.Channels.FirstOrDefault(static c => c.Id == -2);
        bool real = ch?.Id == -3 && m!.HasReal;
        byte flags = m == null ? (byte)0 : real ? m.RealFlags : m.Flags;
        int top = 0, left = 0, bottom = 0, right = 0;
        byte background = 0;
        if (m != null)
        {
            top = real ? m.RealTop : m.Top;
            left = real ? m.RealLeft : m.Left;
            bottom = real ? m.RealBottom : m.Bottom;
            right = real ? m.RealRight : m.Right;
            background = real ? m.RealDefault : m.DefaultColor;
        }

        long mw = (long)right - left;
        long mh = (long)bottom - top;
        bool hasPixels = ch != null && ch.Length > 2 && mw > 0 && mh > 0 && mw <= int.MaxValue && mh <= int.MaxValue;
        ImageRect rect = hasPixels ? new ImageRect(left, top, (int)mw, (int)mh) : new ImageRect(0, 0, 1, 1);

        DPath? path = null;
        bool invert = false;
        bool vectorDisabled = false;
        if (vector != null && (flags & 8) == 0)
        {
            path = VectorMask.Parse(vector.Data.Span, _doc.Width, _doc.Height, 0, 0, out invert, out vectorDisabled);
            if (vectorDisabled && m != null)
            {
                // A disabled vector mask beside a raster mask is not applied at all.
                path = null;
            }
        }

        // The canvas covers the raster mask's rectangle, grown to the document when a path is applied.
        ImageRect area = rect;
        if (path != null)
        {
            area = m == null || !hasPixels ? _doc.Bounds : UnionOf(rect, _doc.Bounds);
            if (area.X != 0 || area.Y != 0)
            {
                path = VectorMask.Parse(vector!.Data.Span, _doc.Width, _doc.Height, area.X, area.Y, out _, out _);
            }
        }

        if (m == null)
        {
            background = invert ? (byte)255 : (byte)0;
        }

        LayerMask mask = layer.AddMask(background == 255, area);
        mask.OutsideValue = background;
        mask.Pixels.Clear(new Rgba32(background, background, background, 255));
        if (hasPixels)
        {
            ReadMaskPixels(mask, ch!, rect, area);
        }

        if (path != null)
        {
            ApplyPath(mask, path, invert, m != null && hasPixels);
            // Outside the canvas the raster mask's background shows through the vector mask's own outside value.
            mask.OutsideValue = (byte)(m == null ? (invert ? 255 : 0) : background * (invert ? 255 : 0) / 255);
        }

        if (m != null)
        {
            mask.Density = m.Density / 255f;
            mask.Feather = (float)Math.Clamp(double.IsFinite(m.Feather) ? m.Feather : 0, 0, 1000);
            mask.Enabled = (flags & 2) == 0;
        }
        else
        {
            mask.Enabled = !vectorDisabled;
        }
    }

    private static ImageRect UnionOf(ImageRect a, ImageRect b)
    {
        int x0 = Math.Min(a.X, b.X);
        int y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.Right, b.Right);
        int y1 = Math.Max(a.Bottom, b.Bottom);
        return new ImageRect(x0, y0, x1 - x0, y1 - y0);
    }

    private void ReadMaskPixels(LayerMask mask, RawChannel ch, ImageRect rect, ImageRect area)
    {
        PsdHeader hd = _s.Header;
        int w = rect.Width;
        int h = rect.Height;
        ChannelReader reader = ChannelReader.ForLayerChannel(_s.Io, ch.Position, ch.Length, w, h, hd.Depth, hd.Psb, _psd, _limits);
        int rowBytes = reader.RowBytes;
        int rows = BandRows(w, rowBytes + w * 2);
        byte[] buf = new byte[rows * rowBytes];
        var px = new Gray8[w * rows];
        byte[] line = new byte[w];
        for (int y0 = 0; y0 < h; y0 += rows)
        {
            _ct.ThrowIfCancellationRequested();
            int n = Math.Min(rows, h - y0);
            for (int y = 0; y < n; y++)
            {
                reader.ReadRow(y0 + y, buf.AsSpan(y * rowBytes, rowBytes));
                Samples.ToU8(buf.AsSpan(y * rowBytes, rowBytes), hd.Depth, w, line);
                for (int x = 0; x < w; x++)
                {
                    px[y * w + x] = new Gray8(line[x]);
                }
            }

            mask.Pixels.CopyFrom<Gray8>(px.AsSpan(0, w * n), new ImageRect(rect.X - area.X, rect.Y - area.Y + y0, w, n));
        }
    }

    // Multiplies the mask by the path's coverage (or paints the path into it when there is no raster mask).
    private void ApplyPath(LayerMask mask, DPath path, bool invert, bool multiply)
    {
        Canvas target = mask.Pixels;
        if (!multiply)
        {
            target.Clear(new Rgba32(invert ? (byte)255 : (byte)0, invert ? (byte)255 : (byte)0, invert ? (byte)255 : (byte)0, 255));
            byte c = invert ? (byte)0 : (byte)255;
            target.Fill(new Rgba32(c, c, c, 255), path, rule: VectorRule);
            return;
        }

        using var cover = new Canvas(target.Width, target.Height, PixelFormat.Gray8, _limits);
        cover.Clear(new Rgba32(invert ? (byte)255 : (byte)0, invert ? (byte)255 : (byte)0, invert ? (byte)255 : (byte)0, 255));
        byte fill = invert ? (byte)0 : (byte)255;
        cover.Fill(new Rgba32(fill, fill, fill, 255), path, rule: VectorRule);
        int w = target.Width;
        int rows = BandRows(w, 4);
        var a = new Gray8[w * rows];
        var b = new Gray8[w * rows];
        for (int y0 = 0; y0 < target.Height; y0 += rows)
        {
            int n = Math.Min(rows, target.Height - y0);
            var r = new ImageRect(0, y0, w, n);
            target.CopyTo<Gray8>(a.AsSpan(0, w * n), r);
            cover.CopyTo<Gray8>(b.AsSpan(0, w * n), r);
            for (int i = 0; i < w * n; i++)
            {
                a[i] = new Gray8((byte)((a[i].V * b[i].V + 127) / 255));
            }

            target.CopyFrom<Gray8>(a.AsSpan(0, w * n), r);
        }
    }

    private const IntersectionRule VectorRule = IntersectionRule.NonZero;
}
