using System.Buffers.Binary;

namespace OneImage.Psd;

internal sealed class PsdHeader
{
    public bool Psb { get; init; }

    public int Channels { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int Depth { get; init; }

    public PsdColorMode Mode { get; init; }
}

internal sealed class RawMask
{
    public int Top, Left, Bottom, Right;
    public byte DefaultColor;
    public byte Flags;
    public int Density = 255;
    public double Feather;
    public bool HasReal;
    public int RealTop, RealLeft, RealBottom, RealRight;
    public byte RealDefault;
    public byte RealFlags;
}

internal sealed class RawChannel
{
    public int Id;
    public long Length;
    public long Position;
}

internal sealed class RawLayer
{
    public int Top, Left, Bottom, Right;
    public List<RawChannel> Channels { get; } = [];
    public uint BlendKey;
    public byte Opacity;
    public byte Clipping;
    public byte Flags;
    public RawMask? Mask;
    public string Name = "";
    public List<PsdBlock> Blocks { get; } = [];

    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

internal sealed class PsdStructure
{
    public required FileIo Io { get; init; }

    public required PsdHeader Header { get; init; }

    public byte[] ColorModeData { get; set; } = [];

    public List<PsdResource> Resources { get; } = [];

    public List<RawLayer> Layers { get; } = [];

    /// <summary>True when the layer count was negative: the first alpha channel of the composite is its transparency.</summary>
    public bool CompositeHasAlpha { get; set; }

    public List<PsdBlock> GlobalBlocks { get; } = [];

    public long ImageDataPosition { get; set; }

    public byte[]? IccProfile { get; set; }
}

/// <summary>Parses the sections of a PSD or PSB file. Every length is checked against the file before memory is allocated for it.</summary>
internal static class PsdParser
{
    // Keys whose length field is 8 bytes in a PSB (Adobe's file format specification, "Additional Layer Information").
    private static readonly HashSet<string> LargeKeys = ["LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"];

    public static bool IsLargeKey(string key) => LargeKeys.Contains(key);

    public static PsdStructure Parse(FileIo io, PsdLimits limits, ImageLimits imageLimits, bool layers)
    {
        byte[] head = new byte[26];
        io.ReadAt(0, head);
        if (head[0] != (byte)'8' || head[1] != (byte)'B' || head[2] != (byte)'P' || head[3] != (byte)'S')
        {
            throw new ImageFormatException("Not a PSD or PSB file: the signature is not 8BPS.");
        }

        int version = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        if (version is not (1 or 2))
        {
            throw new ImageFormatException($"Unknown PSD version {version}.");
        }

        bool psb = version == 2;
        int channels = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(12));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(14));
        uint width = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(18));
        int depth = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(22));
        int mode = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(24));
        if (channels is < 1 or > 56)
        {
            throw new ImageFormatException($"A PSD file has {channels} channels; 1 to 56 are allowed.");
        }

        uint maxSide = psb ? 300_000u : 30_000u;
        if (width is 0 || height is 0 || width > maxSide || height > maxSide)
        {
            throw new ImageFormatException($"The PSD size {width}x{height} is outside what the format allows.");
        }

        if (depth is not (1 or 8 or 16 or 32))
        {
            throw new ImageFormatException($"Unsupported PSD bit depth {depth}.");
        }

        if (mode is not (0 or 1 or 2 or 3 or 4 or 7 or 8 or 9))
        {
            throw new ImageFormatException($"Unknown PSD color mode {mode}.");
        }

        imageLimits.CheckFrame(width, height);
        var header = new PsdHeader { Psb = psb, Channels = channels, Width = (int)width, Height = (int)height, Depth = depth, Mode = (PsdColorMode)mode };
        var s = new PsdStructure { Io = io, Header = header };
        long pos = 26;

        // Color mode data.
        long len = ReadLen4(io, ref pos);
        CheckSection(io, pos, len, "color mode data");
        if (len > limits.MaxBlobBytes)
        {
            throw new ImageLimitException("The color mode data is larger than the blob limit.");
        }

        s.ColorModeData = io.ReadAt(pos, len, limits.MaxBlobBytes);
        pos += len;

        // Image resources.
        len = ReadLen4(io, ref pos);
        CheckSection(io, pos, len, "image resources");
        ParseResources(io.ReadAt(pos, len, limits.MaxTotalBlobBytes), s, limits, imageLimits);
        pos += len;

        // Layer and mask information.
        len = psb ? ReadLen8(io, ref pos) : ReadLen4(io, ref pos);
        CheckSection(io, pos, len, "layer and mask information");
        long layerEnd = pos + len;
        if (len > 0)
        {
            ParseLayerSection(io, pos, layerEnd, s, limits, layers);
        }

        pos = layerEnd;
        s.ImageDataPosition = pos;
        return s;
    }

    private static long ReadLen4(FileIo io, ref long pos)
    {
        Span<byte> b = stackalloc byte[4];
        io.ReadAt(pos, b);
        pos += 4;
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private static long ReadLen8(FileIo io, ref long pos)
    {
        Span<byte> b = stackalloc byte[8];
        io.ReadAt(pos, b);
        pos += 8;
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(b);
        if (v > long.MaxValue)
        {
            throw new ImageFormatException("A section length is too large.");
        }

        return (long)v;
    }

    private static void CheckSection(FileIo io, long pos, long len, string what)
    {
        if (len < 0 || len > io.Length - pos)
        {
            throw new ImageFormatException($"The {what} section extends past the end of the file.");
        }
    }

    private static void ParseResources(byte[] data, PsdStructure s, PsdLimits limits, ImageLimits imageLimits)
    {
        var r = new SpanReader(data);
        long kept = 0;
        while (r.Remaining >= 12)
        {
            uint sig = r.U32();
            if (sig != Fourcc.Of("8BIM") && sig != Fourcc.Of("8B64") && sig != Fourcc.Of("MeSa") && sig != Fourcc.Of("AgHg") && sig != Fourcc.Of("PHUT") && sig != Fourcc.Of("DCSR"))
            {
                throw new ImageFormatException("An image resource block has a bad signature.");
            }

            int id = r.U16();
            string name = r.PascalString(2);
            uint size = r.U32();
            if (size > (uint)r.Remaining)
            {
                throw new ImageFormatException("An image resource block extends past its section.");
            }

            ReadOnlySpan<byte> body = r.Bytes(size);
            if ((size & 1) != 0 && r.Remaining > 0)
            {
                r.Skip(1);
            }

            if (id == 1039)
            {
                if (size > imageLimits.MaxMetadataBytes)
                {
                    throw new ImageLimitException("The embedded ICC profile is larger than the metadata limit.");
                }

                s.IccProfile = body.ToArray();
                continue;
            }

            kept += size;
            if (size > limits.MaxBlobBytes || kept > limits.MaxTotalBlobBytes)
            {
                throw new ImageLimitException("The image resources are larger than the blob limits.");
            }

            s.Resources.Add(new PsdResource(id, name, body.ToArray()));
        }
    }

    private static void ParseLayerSection(FileIo io, long start, long end, PsdStructure s, PsdLimits limits, bool wantLayers)
    {
        bool psb = s.Header.Psb;
        long pos = start;

        // Layer info.
        long infoLen = psb ? ReadLen8(io, ref pos) : ReadLen4(io, ref pos);
        if (infoLen < 0 || infoLen > end - pos)
        {
            throw new ImageFormatException("The layer info extends past the layer and mask section.");
        }

        long infoStart = pos;
        long infoEnd = pos + infoLen;
        if (infoLen >= 2)
        {
            Span<byte> cb = stackalloc byte[2];
            io.ReadAt(pos, cb);
            short count = BinaryPrimitives.ReadInt16BigEndian(cb);
            s.CompositeHasAlpha = count < 0;
            if (wantLayers)
            {
                ParseLayerInfo(io, infoStart, infoEnd, s, limits);
            }
        }

        pos = infoEnd;

        // Global layer mask info.
        if (end - pos >= 4)
        {
            long gl = ReadLen4(io, ref pos);
            if (gl > end - pos)
            {
                throw new ImageFormatException("The global layer mask info extends past the layer and mask section.");
            }

            pos += gl;
        }

        // Global tagged blocks until the end of the section. The layer info of 16- and 32-bit files lives in a block of its own, which
        // can hold gigabytes of channel data, so blocks are walked by header and only the small ones are read.
        long kept = 0;
        while (end - pos >= 12)
        {
            Span<byte> bh = new byte[12];
            io.ReadAt(pos, bh);
            uint sig = BinaryPrimitives.ReadUInt32BigEndian(bh);
            bool big = sig == Fourcc.Of("8B64");
            if (sig != Fourcc.Of("8BIM") && !big)
            {
                // An odd-length block is followed by a pad byte that its length field may not count.
                int skip = 1;
                while (skip <= 3 && end - pos >= 12 + skip)
                {
                    io.ReadAt(pos + skip, bh);
                    sig = BinaryPrimitives.ReadUInt32BigEndian(bh);
                    big = sig == Fourcc.Of("8B64");
                    if (sig == Fourcc.Of("8BIM") || big)
                    {
                        break;
                    }

                    skip++;
                }

                if (skip > 3 || end - pos < 12 + skip)
                {
                    break;
                }

                pos += skip;
            }

            string key = Fourcc.Name(BinaryPrimitives.ReadUInt32BigEndian(bh.Slice(4)));
            long hdr = 12;
            long len;
            if (psb && LargeKeys.Contains(key))
            {
                if (end - pos < 16)
                {
                    break;
                }

                long p2 = pos + 8;
                len = ReadLen8(io, ref p2);
                hdr = 16;
            }
            else
            {
                len = BinaryPrimitives.ReadUInt32BigEndian(bh.Slice(8));
            }

            pos += hdr;
            if (len > end - pos)
            {
                throw new ImageFormatException($"The tagged block '{key}' extends past its section.");
            }

            if (key is "Lr16" or "Lr32" or "Layr")
            {
                if (len >= 2)
                {
                    Span<byte> cb = new byte[2];
                    io.ReadAt(pos, cb);
                    bool neg = BinaryPrimitives.ReadInt16BigEndian(cb) < 0;
                    if (wantLayers && s.Layers.Count == 0)
                    {
                        s.CompositeHasAlpha = neg;
                        ParseLayerInfo(io, pos, pos + len, s, limits);
                    }
                    else
                    {
                        s.CompositeHasAlpha |= neg;
                    }
                }
            }
            else
            {
                kept += len;
                if (len > limits.MaxBlobBytes || kept > limits.MaxTotalBlobBytes)
                {
                    throw new ImageLimitException($"The tagged block '{key}' exceeds the blob limits.");
                }

                s.GlobalBlocks.Add(new PsdBlock(key, io.ReadAt(pos, len, limits.MaxBlobBytes), big));
            }

            pos += len;
        }
    }

    /// <summary>Parses tagged blocks. Each length field includes the block's padding. A trailer that is not a block (a few writers pad the section) ends the walk.</summary>
    public static void ParseBlocks(ReadOnlySpan<byte> data, bool psb, List<PsdBlock> into, PsdLimits limits, ref long kept, bool tolerateTrailer)
    {
        var r = new SpanReader(data);
        while (r.Remaining >= 12)
        {
            uint sig = r.U32();
            bool big = sig == Fourcc.Of("8B64");
            if (sig != Fourcc.Of("8BIM") && !big && r.Position > 4)
            {
                // An odd-length block is followed by a pad byte that its length field may not count: look for the next signature up to 3 bytes on.
                int start = r.Position - 4;
                for (int skip = 1; skip <= 3 && start + skip + 12 <= data.Length; skip++)
                {
                    uint cand = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(start + skip));
                    if (cand == Fourcc.Of("8BIM") || cand == Fourcc.Of("8B64"))
                    {
                        sig = cand;
                        r.Seek(start + skip + 4);
                        big = sig == Fourcc.Of("8B64");
                        break;
                    }
                }
            }

            if (sig != Fourcc.Of("8BIM") && !big)
            {
                if (tolerateTrailer)
                {
                    return;
                }

                throw new ImageFormatException("A tagged block has a bad signature.");
            }

            string key = Fourcc.Name(r.U32());
            long len = r.Length(psb && LargeKeys.Contains(key));
            if (len > r.Remaining)
            {
                throw new ImageFormatException($"The tagged block '{key}' extends past its section.");
            }

            kept += len;
            if (len > limits.MaxBlobBytes || kept > limits.MaxTotalBlobBytes)
            {
                throw new ImageLimitException($"The tagged block '{key}' exceeds the blob limits.");
            }

            into.Add(new PsdBlock(key, r.Bytes(len).ToArray(), big));
        }
    }

    private static void ParseLayerInfo(FileIo io, long start, long end, PsdStructure s, PsdLimits limits)
    {
        bool psb = s.Header.Psb;
        long pos = start;
        Span<byte> two = stackalloc byte[2];
        io.ReadAt(pos, two);
        pos += 2;
        int count = Math.Abs(BinaryPrimitives.ReadInt16BigEndian(two));
        if (count > limits.MaxLayers)
        {
            throw new ImageLimitException($"The file has {count} layer records; the limit is {limits.MaxLayers}.");
        }

        int channelInfo = psb ? 10 : 6;
        long keptBlobs = 0;
        long decoded = 0;
        var layers = new List<RawLayer>(count);
        for (int i = 0; i < count; i++)
        {
            Span<byte> fixedPart = new byte[18];
            if (end - pos < 18)
            {
                throw new ImageFormatException("The layer records end early.");
            }

            io.ReadAt(pos, fixedPart);
            pos += 18;
            var layer = new RawLayer
            {
                Top = BinaryPrimitives.ReadInt32BigEndian(fixedPart),
                Left = BinaryPrimitives.ReadInt32BigEndian(fixedPart.Slice(4)),
                Bottom = BinaryPrimitives.ReadInt32BigEndian(fixedPart.Slice(8)),
                Right = BinaryPrimitives.ReadInt32BigEndian(fixedPart.Slice(12)),
            };
            int nch = BinaryPrimitives.ReadUInt16BigEndian(fixedPart.Slice(16));
            if (nch > 64)
            {
                throw new ImageFormatException($"A layer has {nch} channels.");
            }

            long w = (long)layer.Right - layer.Left;
            long h = (long)layer.Bottom - layer.Top;
            if (w > int.MaxValue || h > int.MaxValue)
            {
                throw new ImageFormatException("A layer rectangle is too large.");
            }

            // An inverted rectangle (some writers produce one for a layer with nothing in it) is an empty layer.
            w = Math.Max(0, w);
            h = Math.Max(0, h);

            if (end - pos < (long)nch * channelInfo + 16)
            {
                throw new ImageFormatException("The layer records end early.");
            }

            byte[] rest = io.ReadAt(pos, (long)nch * channelInfo + 16, 1 << 20);
            pos += rest.Length;
            var rr = new SpanReader(rest);
            for (int c = 0; c < nch; c++)
            {
                int id = rr.I16();
                long clen = rr.Length(psb);
                layer.Channels.Add(new RawChannel { Id = id, Length = clen });
                if (clen < 0 || clen > io.Length)
                {
                    throw new ImageFormatException("A channel length exceeds the file.");
                }
            }

            if (rr.U32() != Fourcc.Of("8BIM"))
            {
                throw new ImageFormatException("A layer record has a bad blend mode signature.");
            }

            layer.BlendKey = rr.U32();
            layer.Opacity = rr.U8();
            layer.Clipping = rr.U8();
            layer.Flags = rr.U8();
            rr.U8();
            uint extra = rr.U32();
            if (extra > end - pos)
            {
                throw new ImageFormatException("A layer record's extra data extends past the layer info.");
            }

            if (extra > limits.MaxBlobBytes)
            {
                throw new ImageLimitException("A layer record's extra data is larger than the blob limit.");
            }

            // Bound the decoded size of the channels the layer will need, before anything is allocated for them.
            int bytesPer = Math.Max(1, s.Header.Depth / 8);
            decoded = checked(decoded + w * h * bytesPer * Math.Max(1, nch));
            if (decoded > limits.MaxDecodedChannelBytes)
            {
                throw new ImageLimitException("The layers' channels decode to more than the allowed total.");
            }

            ParseExtra(io.ReadAt(pos, extra, limits.MaxBlobBytes), layer, psb, limits, ref keptBlobs);
            pos += extra;
            layers.Add(layer);
        }

        // Channel image data follows all the records, layer by layer, channel by channel.
        foreach (RawLayer layer in layers)
        {
            foreach (RawChannel ch in layer.Channels)
            {
                ch.Position = pos;
                if (ch.Length > end - pos)
                {
                    throw new ImageFormatException("A channel's data extends past the layer info.");
                }

                pos += ch.Length;
            }
        }

        s.Layers.AddRange(layers);
    }

    private static void ParseExtra(byte[] extra, RawLayer layer, bool psb, PsdLimits limits, ref long kept)
    {
        var r = new SpanReader(extra);
        uint maskLen = r.U32();
        if (maskLen > (uint)r.Remaining)
        {
            throw new ImageFormatException("A layer mask record extends past the layer's extra data.");
        }

        if (maskLen > 0)
        {
            layer.Mask = ParseMask(r.Bytes(maskLen));
        }

        uint blendLen = r.U32();
        if (blendLen > (uint)r.Remaining)
        {
            throw new ImageFormatException("The blending ranges extend past the layer's extra data.");
        }

        r.Skip(blendLen);
        layer.Name = r.Remaining > 0 ? r.PascalString(4) : "";
        ParseBlocks(extra.AsSpan(r.Position), psb, layer.Blocks, limits, ref kept, tolerateTrailer: true);
    }

    private static RawMask ParseMask(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data);
        var m = new RawMask { Top = r.I32(), Left = r.I32(), Bottom = r.I32(), Right = r.I32(), DefaultColor = r.U8(), Flags = r.U8() };
        if (data.Length == 20)
        {
            return m;
        }

        int afterFlags = r.Position;
        // The format description puts the parameters (flags 0x10) before the second rectangle, but Photoshop also writes them after it.
        // A parameter byte always has one of its four low bits set, so a zero or larger byte there is the second rectangle's flags.
        bool hasParameters = (m.Flags & 16) != 0 && r.Remaining >= 1;
        bool parametersFirst = hasParameters && data[afterFlags] is >= 1 and <= 15;
        if (parametersFirst)
        {
            ReadMaskParameters(ref r, m);
        }

        if (r.Remaining >= 18)
        {
            ReadRealMask(ref r, m);
            if (hasParameters && !parametersFirst && r.Remaining >= 1 && data[r.Position] is >= 1 and <= 15)
            {
                ReadMaskParameters(ref r, m);
            }
        }

        return m;
    }

    private static void ReadRealMask(ref SpanReader r, RawMask m)
    {
        m.RealFlags = r.U8();
        m.RealDefault = r.U8();
        m.RealTop = r.I32();
        m.RealLeft = r.I32();
        m.RealBottom = r.I32();
        m.RealRight = r.I32();
        m.HasReal = true;
    }

    private static void ReadMaskParameters(ref SpanReader r, RawMask m)
    {
        byte p = r.U8();
        if ((p & 1) != 0)
        {
            m.Density = r.U8();
        }

        if ((p & 2) != 0)
        {
            m.Feather = r.F64();
        }

        if ((p & 4) != 0)
        {
            r.U8();
        }

        if ((p & 8) != 0)
        {
            r.F64();
        }
    }
}
