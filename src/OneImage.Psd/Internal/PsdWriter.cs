using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace OneImage.Psd;

internal enum EntryKind
{
    Normal,
    GroupDivider,
    GroupOpener,
}

/// <summary>One layer record to write, with its already-decided fields and where its pixels come from.</summary>
internal sealed class LayerEntry
{
    public EntryKind Kind;
    public string Name = "";
    public int Top, Left, Bottom, Right;
    public string BlendKey = "norm";
    public byte Opacity = 255;
    public bool Clipped;
    public byte Flags = 8;
    public IBandSource? Pixels;
    public IBandSource? MaskPixels;
    public int MaskTop, MaskLeft, MaskBottom, MaskRight;
    public byte MaskDefault;
    public bool MaskDisabled;
    public bool MaskRendered;
    public byte MaskDensity = 255;
    public double MaskFeather;
    public List<PsdBlock> Blocks { get; } = [];
}

/// <summary>Everything needed to write a file; the layered writer and the flat-image encoder both fill one in.</summary>
internal sealed class WriteSpec
{
    public int Width, Height, Depth;
    public bool Gray;
    public bool CompositeAlpha;
    public byte[]? Icc;
    public List<PsdResource> Resources { get; } = [];
    public List<PsdBlock> DocumentBlocks { get; } = [];
    public List<LayerEntry> Layers { get; } = [];

    /// <summary>Canvases created while building layer entries (rendered fills); disposed after the file is written.</summary>
    public List<IDisposable> Temporary { get; } = [];

    public required IBandSource Composite { get; init; }
}

/// <summary>Writes PSD and PSB files. The output stream must be seekable: lengths that precede their data are patched afterwards.</summary>
internal sealed class PsdWriter
{
    private readonly Stream _out;
    private readonly bool _psb;
    private readonly PsdCompression _compression;
    private readonly CancellationToken _ct;

    private PsdWriter(Stream output, bool psb, PsdCompression compression, CancellationToken ct)
    {
        _out = output;
        _psb = psb;
        _compression = compression;
        _ct = ct;
    }

    public static void Write(WriteSpec spec, Stream output, PsdWriteOptions options, CancellationToken ct)
    {
        bool psb = options.Large ?? (spec.Width > 30_000 || spec.Height > 30_000);
        if (!psb && (spec.Width > 30_000 || spec.Height > 30_000))
        {
            throw new ImageOperationException("A PSD is at most 30,000 pixels in each direction; write a PSB (PsdWriteOptions.Large) instead.");
        }

        if (psb && (spec.Width > 300_000 || spec.Height > 300_000))
        {
            throw new ImageOperationException("A PSB is at most 300,000 pixels in each direction.");
        }

        if (spec.Layers.Count > 32767)
        {
            throw new ImageLimitException("A PSD file holds at most 32,767 layer records.");
        }

        if (output.CanSeek)
        {
            new PsdWriter(output, psb, options.Compression, ct).WriteAll(spec);
            return;
        }

        // A sink that cannot seek gets the file through a temporary one.
        string temp = System.IO.Path.GetTempFileName();
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose))
            {
                new PsdWriter(fs, psb, options.Compression, ct).WriteAll(spec);
                fs.Position = 0;
                fs.CopyTo(output);
            }
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    private void U8(int v) => _out.WriteByte((byte)v);

    private void U16(int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        _out.Write(b);
    }

    private void U32(long v)
    {
        if (v < 0 || v > uint.MaxValue)
        {
            throw new ImageLimitException("A PSD section is larger than 4 GiB; write a PSB (PsdWriteOptions.Large) instead.");
        }

        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v);
        _out.Write(b);
    }

    private void I32(int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        _out.Write(b);
    }

    private void Len(long v)
    {
        if (_psb)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v);
            _out.Write(b);
        }
        else
        {
            U32(v);
        }
    }

    private void Tag(string t) => _out.Write(Encoding.ASCII.GetBytes(t));

    private void Patch(long at, long value, bool wide)
    {
        long end = _out.Position;
        _out.Position = at;
        if (wide)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)value);
            _out.Write(b);
        }
        else
        {
            U32(value);
        }

        _out.Position = end;
    }

    private void WriteAll(WriteSpec spec)
    {
        int colorPlanes = spec.Gray ? 1 : 3;
        int totalChannels = colorPlanes + (spec.CompositeAlpha ? 1 : 0);
        Tag("8BPS");
        U16(_psb ? 2 : 1);
        _out.Write(new byte[6]);
        U16(totalChannels);
        U32(spec.Height);
        U32(spec.Width);
        U16(spec.Depth);
        U16(spec.Gray ? 1 : 3);

        U32(0); // color mode data

        WriteResources(spec);
        WriteLayerSection(spec);
        WriteComposite(spec, totalChannels);
    }

    private void WriteResources(WriteSpec spec)
    {
        long lenAt = _out.Position;
        U32(0);
        void Resource(int id, string name, ReadOnlySpan<byte> data)
        {
            Tag("8BIM");
            U16(id);
            byte[] nb = Encoding.Latin1.GetBytes(name.Length > 255 ? name[..255] : name);
            U8(nb.Length);
            _out.Write(nb);
            if ((nb.Length + 1) % 2 != 0)
            {
                U8(0);
            }

            U32(data.Length);
            _out.Write(data);
            if ((data.Length & 1) != 0)
            {
                U8(0);
            }
        }

        if (spec.Icc is { Length: > 0 })
        {
            Resource(1039, "", spec.Icc);
        }

        foreach (PsdResource r in spec.Resources)
        {
            if (r.Id != 1039)
            {
                Resource(r.Id, r.Name, r.Data.Span);
            }
        }

        Patch(lenAt, _out.Position - lenAt - 4, false);
    }

    private void WriteLayerSection(WriteSpec spec)
    {
        long sectionAt = _out.Position;
        Len(0);
        bool inBlock = spec.Depth != 8;
        string wrapKey = spec.Depth == 16 ? "Lr16" : "Lr32";
        long blockLenAt = -1;
        long infoAt = -1;
        if (spec.Layers.Count > 0 && !inBlock)
        {
            infoAt = _out.Position;
            Len(0);
            WriteLayerInfoBody(spec);
            if ((_out.Position - infoAt - (_psb ? 8 : 4)) % 2 != 0)
            {
                U8(0);
            }

            Patch(infoAt, _out.Position - infoAt - (_psb ? 8 : 4), _psb);
        }
        else
        {
            Len(0); // empty layer info, or the layers are in a tagged block below
        }

        U32(0); // global layer mask info
        if (spec.Layers.Count > 0 && inBlock)
        {
            Tag("8BIM");
            Tag(wrapKey);
            blockLenAt = _out.Position;
            Len(0);
            long bodyAt = _out.Position;
            WriteLayerInfoBody(spec);
            if ((_out.Position - bodyAt) % 2 != 0)
            {
                U8(0);
            }

            Patch(blockLenAt, _out.Position - bodyAt, _psb);
        }

        foreach (PsdBlock b in spec.DocumentBlocks)
        {
            WriteTagged(b, padInLength: false);
        }

        Patch(sectionAt, _out.Position - sectionAt - (_psb ? 8 : 4), _psb);
    }

    /// <summary>Writes one tagged block padded to an even length. Document-level blocks in real files state the unpadded length and put the pad byte after it, so a block read from such a file writes back to the same bytes; layer-level blocks count the pad in the length, as the format description says.</summary>
    private void WriteTagged(PsdBlock b, bool padInLength)
    {
        Tag(b.Large ? "8B64" : "8BIM");
        Tag(b.Key.PadRight(4)[..4]);
        int pad = b.Data.Length & 1;
        int stated = padInLength ? b.Data.Length + pad : b.Data.Length;
        if (_psb && PsdParser.IsLargeKey(b.Key))
        {
            Len(stated);
        }
        else
        {
            U32(stated);
        }

        _out.Write(b.Data.Span);
        if (pad != 0)
        {
            U8(0);
        }
    }

    private sealed class ChannelPlan
    {
        public int Id;
        public long LengthAt;
        public IBandSource? Source;
        public int Plane;
        public bool IsMask;
    }

    private void WriteLayerInfoBody(WriteSpec spec)
    {
        // The count is negative when the merged image carries the transparency of the layers.
        int count = spec.Layers.Count;
        U16(spec.CompositeAlpha ? -count & 0xFFFF : count);
        var plans = new List<List<ChannelPlan>>(count);
        foreach (LayerEntry e in spec.Layers)
        {
            plans.Add(WriteRecord(e, spec));
        }

        for (int i = 0; i < count; i++)
        {
            LayerEntry e = spec.Layers[i];
            foreach (ChannelPlan ch in plans[i])
            {
                _ct.ThrowIfCancellationRequested();
                long start = _out.Position;
                if (ch.Source == null)
                {
                    U16(0); // an empty channel: a raw code and no rows
                }
                else
                {
                    WriteChannel(ch.Source, ch.Plane, ch.IsMask ? e.MaskRect() : e.PixelRect());
                }

                Patch(ch.LengthAt, _out.Position - start, _psb);
            }
        }
    }

    private List<ChannelPlan> WriteRecord(LayerEntry e, WriteSpec spec)
    {
        I32(e.Top);
        I32(e.Left);
        I32(e.Bottom);
        I32(e.Right);
        var chans = new List<ChannelPlan>();
        int colorPlanes = spec.Gray ? 1 : 3;
        chans.Add(new ChannelPlan { Id = -1, Source = e.Pixels is { HasAlpha: true } ? e.Pixels : null, Plane = colorPlanes });
        for (int c = 0; c < colorPlanes; c++)
        {
            chans.Add(new ChannelPlan { Id = c, Source = e.Pixels, Plane = c });
        }

        if (e.MaskPixels != null)
        {
            chans.Add(new ChannelPlan { Id = -2, Source = e.MaskPixels, Plane = 0, IsMask = true });
        }

        U16(chans.Count);
        foreach (ChannelPlan ch in chans)
        {
            U16(ch.Id & 0xFFFF);
            ch.LengthAt = _out.Position;
            Len(0);
        }

        Tag("8BIM");
        Tag(e.BlendKey);
        U8(e.Opacity);
        U8(e.Clipped ? 1 : 0);
        U8(e.Flags);
        U8(0);
        long extraAt = _out.Position;
        U32(0);

        // Layer mask data.
        if (e.MaskPixels != null || e.MaskDisabled)
        {
            bool parameters = e.MaskDensity != 255 || e.MaskFeather != 0;
            long maskAt = _out.Position;
            U32(0);
            I32(e.MaskTop);
            I32(e.MaskLeft);
            I32(e.MaskBottom);
            I32(e.MaskRight);
            U8(e.MaskDefault);
            int flags = (e.MaskDisabled ? 2 : 0) | (e.MaskRendered ? 8 : 0) | (parameters ? 16 : 0);
            U8(flags);
            if (parameters)
            {
                int p = (e.MaskDensity != 255 ? 1 : 0) | (e.MaskFeather != 0 ? 2 : 0);
                U8(p);
                if ((p & 1) != 0)
                {
                    U8(e.MaskDensity);
                }

                if ((p & 2) != 0)
                {
                    Span<byte> d = stackalloc byte[8];
                    BinaryPrimitives.WriteUInt64BigEndian(d, (ulong)BitConverter.DoubleToInt64Bits(e.MaskFeather));
                    _out.Write(d);
                }

                U8(flags & ~16 & 0xFF);
                U8(e.MaskDefault);
                I32(e.MaskTop);
                I32(e.MaskLeft);
                I32(e.MaskBottom);
                I32(e.MaskRight);
            }
            else
            {
                U16(0);
            }

            Patch(maskAt, _out.Position - maskAt - 4, false);
        }
        else
        {
            U32(0);
        }

        U32(0); // blending ranges

        byte[] name = PascalName(e.Name);
        _out.Write(name);
        foreach (PsdBlock b in e.Blocks)
        {
            WriteTagged(b, padInLength: true);
        }

        long extra = _out.Position - extraAt - 4;
        Patch(extraAt, extra, false);
        return chans;
    }

    private static byte[] PascalName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(c is >= ' ' and <= '~' ? c : '?');
            if (sb.Length == 255)
            {
                break;
            }
        }

        int total = (1 + sb.Length + 3) / 4 * 4;
        byte[] bytes = new byte[total];
        bytes[0] = (byte)sb.Length;
        Encoding.ASCII.GetBytes(sb.ToString(), 0, sb.Length, bytes, 1);
        return bytes;
    }

    private static int BandRows(int width, int bytesPerSample, int planes) =>
        Math.Clamp((int)(4L * 1024 * 1024 / Math.Max(1, (long)width * bytesPerSample * Math.Max(1, planes))), 1, 128);

    // One channel of a layer: its compression code, then its rows. The source is read one band at a time, one plane of it kept.
    private void WriteChannel(IBandSource src, int plane, (int W, int H) size)
    {
        int bps = src.Depth / 8;
        int w = size.W;
        int h = size.H;
        int rowBytes = checked(w * bps);
        int planeCount = src.ColorPlanes + (src.HasAlpha ? 1 : 0);
        int rows = BandRows(w, bps, planeCount);
        byte[][] bands = new byte[planeCount][];
        for (int p = 0; p < planeCount; p++)
        {
            bands[p] = new byte[rows * rowBytes];
        }

        if (_compression == PsdCompression.Raw || w == 0 || h == 0)
        {
            U16(0);
            for (int y0 = 0; y0 < h; y0 += rows)
            {
                int n = Math.Min(rows, h - y0);
                src.Read(y0, n, bands, _ct);
                _out.Write(bands[plane], 0, n * rowBytes);
            }

            return;
        }

        U16(1);
        int cs = _psb ? 4 : 2;
        long tableAt = _out.Position;
        _out.Write(new byte[h * cs]);
        int[] lengths = new int[h];
        byte[] packed = new byte[rowBytes + rowBytes / 128 + 2];
        for (int y0 = 0; y0 < h; y0 += rows)
        {
            _ct.ThrowIfCancellationRequested();
            int n = Math.Min(rows, h - y0);
            src.Read(y0, n, bands, _ct);
            for (int y = 0; y < n; y++)
            {
                int len = Rle.Pack(bands[plane].AsSpan(y * rowBytes, rowBytes), packed);
                lengths[y0 + y] = len;
                _out.Write(packed, 0, len);
            }
        }

        long end = _out.Position;
        _out.Position = tableAt;
        byte[] table = new byte[h * cs];
        for (int y = 0; y < h; y++)
        {
            if (_psb)
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(y * 4), (uint)lengths[y]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(y * 2), (ushort)lengths[y]);
            }
        }

        _out.Write(table);
        _out.Position = end;
    }

    private void WriteComposite(WriteSpec spec, int totalChannels)
    {
        IBandSource src = spec.Composite;
        int bps = spec.Depth / 8;
        int w = spec.Width;
        int h = spec.Height;
        int rowBytes = w * bps;
        int planeCount = src.ColorPlanes + (src.HasAlpha ? 1 : 0);
        int rows = BandRows(w, bps, planeCount);
        byte[][] bands = new byte[planeCount][];
        for (int p = 0; p < planeCount; p++)
        {
            bands[p] = new byte[rows * rowBytes];
        }

        if (_compression == PsdCompression.Raw)
        {
            U16(0);
            long start = _out.Position;
            long planeSize = (long)rowBytes * h;
            _out.SetLength(Math.Max(_out.Length, start + planeSize * totalChannels));
            for (int y0 = 0; y0 < h; y0 += rows)
            {
                _ct.ThrowIfCancellationRequested();
                int n = Math.Min(rows, h - y0);
                src.Read(y0, n, bands, _ct);
                for (int p = 0; p < totalChannels; p++)
                {
                    _out.Position = start + p * planeSize + (long)y0 * rowBytes;
                    _out.Write(bands[p], 0, n * rowBytes);
                }
            }

            _out.Position = start + planeSize * totalChannels;
            return;
        }

        // Run-length data is grouped by channel but produced by band, so each channel's lines are held (compressed) until the end.
        U16(1);
        int cs = _psb ? 4 : 2;
        var held = new MemoryStream[totalChannels];
        var lengths = new int[totalChannels][];
        for (int p = 0; p < totalChannels; p++)
        {
            held[p] = new MemoryStream();
            lengths[p] = new int[h];
        }

        byte[] packed = new byte[rowBytes + rowBytes / 128 + 2];
        for (int y0 = 0; y0 < h; y0 += rows)
        {
            _ct.ThrowIfCancellationRequested();
            int n = Math.Min(rows, h - y0);
            src.Read(y0, n, bands, _ct);
            for (int p = 0; p < totalChannels; p++)
            {
                for (int y = 0; y < n; y++)
                {
                    int len = Rle.Pack(bands[p].AsSpan(y * rowBytes, rowBytes), packed);
                    lengths[p][y0 + y] = len;
                    held[p].Write(packed, 0, len);
                }
            }
        }

        byte[] table = new byte[h * cs];
        for (int p = 0; p < totalChannels; p++)
        {
            for (int y = 0; y < h; y++)
            {
                if (_psb)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(y * 4), (uint)lengths[p][y]);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(y * 2), (ushort)lengths[p][y]);
                }
            }

            _out.Write(table);
        }

        for (int p = 0; p < totalChannels; p++)
        {
            held[p].Position = 0;
            held[p].CopyTo(_out);
            held[p].Dispose();
        }
    }
}

internal static class LayerEntryExtensions
{
    public static (int W, int H) PixelRect(this LayerEntry e) => (e.Right - e.Left, e.Bottom - e.Top);

    public static (int W, int H) MaskRect(this LayerEntry e) => (e.MaskRight - e.MaskLeft, e.MaskBottom - e.MaskTop);
}
