namespace OneImage.Codecs.Tiff;

/// <summary>Compression schemes the decoder understands.</summary>
internal enum TiffCodec
{
    None,
    Lzw,
    LzwOldStyle,
    Deflate,
    PackBits,
    CcittRle,
    CcittG3,
    CcittG4,
    Jpeg,
    OldJpeg,
}

/// <summary>Everything the decoder needs to know about one image file directory, after validation.</summary>
internal sealed class TiffFrame
{
    private const int MaxBlocks = 1 << 22;
    private const long MaxBlockRawBytes = 1L << 30;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Bits { get; private set; }

    public int SamplesPerPixel { get; private set; }

    public bool Planar { get; private set; }

    public int Predictor { get; private set; }

    public bool FloatSamples { get; private set; }

    public TiffCodec Codec { get; private set; }

    public bool FillOrderReversed { get; private set; }

    public int T4Options { get; private set; }

    public bool Photometric0 { get; private set; }

    public bool Tiled { get; private set; }

    public int BlockWidth { get; private set; }

    public int BlockHeight { get; private set; }

    public int BlocksAcross { get; private set; }

    public int BlocksDown { get; private set; }

    public int Planes { get; private set; }

    public long[] Offsets { get; private set; } = [];

    public long[] Counts { get; private set; } = [];

    public byte[]? JpegTables { get; private set; }

    public bool YCbCr { get; private set; }

    /// <summary>Gets the bytes this frame keeps alive (offset tables, palette, shared JPEG tables).</summary>
    public long RetainedBytes => ((long)(Offsets.Length + Counts.Length) * 8) + (JpegTables?.Length ?? 0) + (Palette?.Length ?? 0);

    private byte[]? Palette { get; set; }

    public PixelFormat Format { get; private set; }

    public bool UnpremultiplyOnRead { get; private set; }

    public TiffPixelConverter Converter { get; private set; } = null!;

    /// <summary>Gets the stored samples per pixel in one block: all of them when contiguous, one when planar.</summary>
    public int SamplesPerBlockPixel => Planar ? 1 : SamplesPerPixel;

    public int BlocksPerPlane => BlocksAcross * BlocksDown;

    /// <summary>Gets the byte size of one stored row of a block.</summary>
    public long BlockRowBytes => (((long)BlockWidth * SamplesPerBlockPixel * Bits) + 7) / 8;

    public int RowsInBlock(int blockIndexInPlane)
    {
        if (Tiled || Codec == TiffCodec.OldJpeg)
        {
            return BlockHeight;
        }

        int by = blockIndexInPlane / BlocksAcross;
        return Math.Min(BlockHeight, Height - (by * BlockHeight));
    }

    public static TiffFrame Parse(TiffFile file, List<TiffEntry> entries, ImageLimits limits)
    {
        var f = new TiffFrame();
        f.ParseCore(file, entries, limits);
        return f;
    }

    private static TiffEntry? Find(List<TiffEntry> entries, int tag)
    {
        foreach (TiffEntry e in entries)
        {
            if (e.Tag == tag)
            {
                return e;
            }
        }

        return null;
    }

    private static long One(TiffFile file, List<TiffEntry> entries, int tag, long fallback)
    {
        TiffEntry? e = Find(entries, tag);
        if (e is not { } en || en.Count < 1)
        {
            return fallback;
        }

        long[] v = file.ReadIntegers(en, 4096);
        return v[0];
    }

    private static long[]? Many(TiffFile file, List<TiffEntry> entries, int tag, long maxCount = 4096)
    {
        TiffEntry? e = Find(entries, tag);
        return e is { } en ? file.ReadIntegers(en, maxCount) : null;
    }

    private void ParseCore(TiffFile file, List<TiffEntry> entries, ImageLimits limits)
    {
        long w = One(file, entries, TiffTag.ImageWidth, 0);
        long h = One(file, entries, TiffTag.ImageLength, 0);
        limits.CheckFrame(w, h);
        Width = (int)w;
        Height = (int)h;

        long spp = One(file, entries, TiffTag.SamplesPerPixel, 1);
        if (spp is < 1 or > 32)
        {
            throw new ImageFormatException($"Unsupported TIFF samples per pixel: {spp}.");
        }

        SamplesPerPixel = (int)spp;
        Bits = SingleValued(Many(file, entries, TiffTag.BitsPerSample), 1, "bits per sample");
        int sampleFormat = SingleValued(Many(file, entries, TiffTag.SampleFormat), 1, "sample format");
        FloatSamples = sampleFormat == 3;
        if (sampleFormat is 2 or 5 or 6)
        {
            throw new ImageFormatException("Signed integer and complex TIFF samples are not supported.");
        }

        if (FloatSamples ? Bits != 32 : Bits is < 1 or > 16)
        {
            throw new ImageFormatException($"Unsupported TIFF sample size: {Bits} bits{(FloatSamples ? " (float)" : string.Empty)}.");
        }

        int photometric = (int)One(file, entries, TiffTag.Photometric, spp == 1 ? 1 : 2);
        long compression = One(file, entries, TiffTag.Compression, 1);
        Codec = compression switch
        {
            1 => TiffCodec.None,
            2 => TiffCodec.CcittRle,
            3 => TiffCodec.CcittG3,
            4 => TiffCodec.CcittG4,
            5 => TiffCodec.Lzw,
            6 => TiffCodec.OldJpeg,
            7 => TiffCodec.Jpeg,
            8 or 32946 => TiffCodec.Deflate,
            32773 => TiffCodec.PackBits,
            _ => throw new ImageFormatException($"Unsupported TIFF compression {compression}."),
        };

        long planarCfg = One(file, entries, TiffTag.PlanarConfiguration, 1);
        if (planarCfg is not (1 or 2))
        {
            throw new ImageFormatException($"Invalid TIFF planar configuration {planarCfg}.");
        }

        Planar = planarCfg == 2 && SamplesPerPixel > 1;
        Planes = Planar ? SamplesPerPixel : 1;
        Predictor = (int)One(file, entries, TiffTag.Predictor, 1);
        FillOrderReversed = One(file, entries, TiffTag.FillOrder, 1) == 2;
        T4Options = (int)One(file, entries, TiffTag.T4Options, 0);
        bool isCcitt = Codec is TiffCodec.CcittRle or TiffCodec.CcittG3 or TiffCodec.CcittG4;
        bool isJpeg = Codec is TiffCodec.Jpeg or TiffCodec.OldJpeg;
        if (isCcitt && (Bits != 1 || SamplesPerPixel != 1))
        {
            throw new ImageFormatException("CCITT compression needs 1-bit single-sample data.");
        }

        if (Codec == TiffCodec.CcittG3 && (T4Options & 2) != 0)
        {
            throw new ImageFormatException("CCITT uncompressed mode is not supported.");
        }

        if (isJpeg && (Bits != 8 || Planar))
        {
            throw new ImageFormatException("JPEG-compressed TIFF needs contiguous 8-bit samples.");
        }

        if (Predictor == 2 && Bits is not (8 or 16))
        {
            throw new ImageFormatException("The horizontal predictor needs 8 or 16 bits per sample.");
        }

        if (Predictor == 3 && !FloatSamples)
        {
            throw new ImageFormatException("The floating point predictor needs float samples.");
        }

        if (Predictor is not (1 or 2 or 3))
        {
            throw new ImageFormatException($"Unsupported TIFF predictor {Predictor}.");
        }

        ChooseFormat(file, entries, photometric, isJpeg);
        ParseBlocks(file, entries, limits);
    }

    private static int SingleValued(long[]? values, int fallback, string what)
    {
        if (values is null || values.Length == 0)
        {
            return fallback;
        }

        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] != values[0])
            {
                throw new ImageFormatException($"TIFF samples with different {what} are not supported.");
            }
        }

        return (int)Math.Clamp(values[0], 0, 1 << 20);
    }

    private void ChooseFormat(TiffFile file, List<TiffEntry> entries, int photometric, bool isJpeg)
    {
        int spp = SamplesPerPixel;
        int alphaKind = 0; // 0 none, 1 associated, 2 unassociated
        byte[]? palette = null;
        ColorModel model;
        int baseChannels;
        switch (photometric)
        {
            case 0:
            case 1:
                model = ColorModel.Gray;
                baseChannels = 1;
                Photometric0 = photometric == 0;
                break;
            case 2:
                model = ColorModel.Rgb;
                baseChannels = 3;
                break;
            case 3:
                model = ColorModel.Rgb;
                baseChannels = 1;
                palette = ReadPalette(file, entries);
                break;
            case 5:
                if (One(file, entries, TiffTag.InkSet, 1) != 1)
                {
                    throw new ImageFormatException("Only CMYK ink sets are supported for separated TIFF.");
                }

                model = ColorModel.Cmyk;
                baseChannels = 4;
                break;
            case 6 when isJpeg:
                model = ColorModel.Rgb;
                baseChannels = 3;
                YCbCr = true;
                break;
            default:
                throw new ImageFormatException($"Unsupported TIFF photometric interpretation {photometric}.");
        }

        int extras = spp - baseChannels;
        if (extras < 0)
        {
            throw new ImageFormatException("Too few samples per pixel for the TIFF photometric interpretation.");
        }

        bool hasAlpha = false;
        if (extras > 0 && (model == ColorModel.Gray || (model == ColorModel.Rgb && palette is null)))
        {
            long[] es = Many(file, entries, TiffTag.ExtraSamples) ?? [];
            long first = es.Length > 0 ? es[0] : 0;
            if (first is 0 or 1 or 2)
            {
                hasAlpha = true;
                alphaKind = first == 1 ? 1 : 2;
            }
        }

        if (isJpeg && extras > 0)
        {
            throw new ImageFormatException("JPEG-compressed TIFF with extra samples is not supported.");
        }

        AlphaMode alpha = AlphaMode.None;
        bool wide = Bits > 8;
        if (FloatSamples)
        {
            if (model == ColorModel.Cmyk || palette is not null)
            {
                throw new ImageFormatException("Float CMYK and palette TIFF are not supported.");
            }

            if (model == ColorModel.Gray && hasAlpha)
            {
                throw new ImageFormatException("Float gray+alpha TIFF is not supported.");
            }

            alpha = hasAlpha ? (alphaKind == 1 ? AlphaMode.Premultiplied : AlphaMode.Straight) : AlphaMode.None;
            Format = new PixelFormat(model, SampleType.F32, alpha);
        }
        else if (model == ColorModel.Cmyk)
        {
            if (Bits is not (8 or 16))
            {
                throw new ImageFormatException("CMYK TIFF needs 8 or 16 bits per sample.");
            }

            Format = wide ? PixelFormat.Cmyk16 : PixelFormat.Cmyk8;
        }
        else if (palette is not null)
        {
            Format = PixelFormat.Rgb8;
        }
        else if (wide)
        {
            if (hasAlpha && model == ColorModel.Gray)
            {
                throw new ImageFormatException("16-bit gray+alpha TIFF is not supported.");
            }

            if (hasAlpha)
            {
                // There is no premultiplied 16-bit format, so associated alpha is converted to straight on read.
                UnpremultiplyOnRead = alphaKind == 1;
                alpha = AlphaMode.Straight;
            }

            Format = new PixelFormat(model, SampleType.U16, alpha);
        }
        else
        {
            if (hasAlpha)
            {
                if (model == ColorModel.Gray)
                {
                    UnpremultiplyOnRead = alphaKind == 1;
                    alpha = AlphaMode.Straight;
                }
                else
                {
                    alpha = alphaKind == 1 ? AlphaMode.Premultiplied : AlphaMode.Straight;
                }
            }

            Format = new PixelFormat(model, SampleType.U8, alpha);
        }

        Palette = palette;
        Converter = TiffPixelConverter.Create(this, file.BigEndian && Predictor != 3, Format, palette, hasAlpha, isJpeg);
    }

    private static byte[] ReadPalette(TiffFile file, List<TiffEntry> entries)
    {
        TiffEntry? e = Find(entries, TiffTag.ColorMap) ?? throw new ImageFormatException("Palette TIFF without a color map.");
        long[] cm = file.ReadIntegers(e.Value, 3L * 65536);
        if (cm.Length == 0 || cm.Length % 3 != 0)
        {
            throw new ImageFormatException("Invalid TIFF color map size.");
        }

        int n = cm.Length / 3;
        bool small = true;
        foreach (long v in cm)
        {
            if (v >= 256)
            {
                small = false;
                break;
            }
        }

        // Same convention as libtiff: maps whose values all fit in a byte are taken as 8-bit values, otherwise the high byte is used.
        byte[] p = new byte[n * 3];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                long v = cm[(c * n) + i];
                p[(i * 3) + c] = (byte)(small ? v : v >> 8);
            }
        }

        return p;
    }

    private void ParseBlocks(TiffFile file, List<TiffEntry> entries, ImageLimits limits)
    {
        if (Codec == TiffCodec.OldJpeg)
        {
            // Old-style JPEG: the whole image is one self-contained JPEG stream named by the interchange-format tags.
            long off = One(file, entries, TiffTag.JpegInterchangeFormat, 0);
            if (off <= 0 || off >= file.Length)
            {
                throw new ImageFormatException("Old-style JPEG TIFF without an interchange-format stream is not supported.");
            }

            long len = One(file, entries, TiffTag.JpegInterchangeFormatLength, file.Length - off);
            len = Math.Min(len, file.Length - off);
            BlockWidth = Width;
            BlockHeight = Height;
            BlocksAcross = 1;
            BlocksDown = 1;
            Offsets = [off];
            Counts = [len];
            return;
        }

        TiffEntry? tw = Find(entries, TiffTag.TileWidth);
        TiffEntry? tl = Find(entries, TiffTag.TileLength);
        Tiled = tw is not null && tl is not null;
        int offTag;
        int cntTag;
        if (Tiled)
        {
            long bw = One(file, entries, TiffTag.TileWidth, 0);
            long bh = One(file, entries, TiffTag.TileLength, 0);
            if (bw is < 1 or > (1 << 24) || bh is < 1 or > (1 << 24))
            {
                throw new ImageFormatException("Invalid TIFF tile size.");
            }

            BlockWidth = (int)bw;
            BlockHeight = (int)bh;
            offTag = TiffTag.TileOffsets;
            cntTag = TiffTag.TileByteCounts;
        }
        else
        {
            long rps = One(file, entries, TiffTag.RowsPerStrip, Height);
            if (rps < 1 || rps > Height)
            {
                rps = Height;
            }

            BlockWidth = Width;
            BlockHeight = (int)rps;
            offTag = TiffTag.StripOffsets;
            cntTag = TiffTag.StripByteCounts;
        }

        BlocksAcross = (int)(((long)Width + BlockWidth - 1) / BlockWidth);
        BlocksDown = (int)(((long)Height + BlockHeight - 1) / BlockHeight);
        long total = (long)BlocksAcross * BlocksDown * Planes;
        if (total > MaxBlocks)
        {
            throw new ImageLimitException($"The TIFF has {total} strips or tiles, more than the {MaxBlocks} allowed.");
        }

        long rowBytes = BlockRowBytes;
        if (rowBytes * BlockHeight > MaxBlockRawBytes || rowBytes > int.MaxValue / 2)
        {
            throw new ImageLimitException("A TIFF strip or tile is too large to decode.");
        }

        TiffEntry offsets = Find(entries, offTag) ?? throw new ImageFormatException("The TIFF has no strip or tile offsets.");
        if (offsets.Count < total)
        {
            throw new ImageFormatException("The TIFF has fewer strip or tile offsets than its size requires.");
        }

        Offsets = file.ReadIntegers(offsets, MaxBlocks);
        TiffEntry? counts = Find(entries, cntTag);
        if (counts is { } c)
        {
            if (c.Count < total)
            {
                throw new ImageFormatException("The TIFF has fewer strip or tile byte counts than its size requires.");
            }

            Counts = file.ReadIntegers(c, MaxBlocks);
        }
        else if (Codec == TiffCodec.None)
        {
            // Some writers omit the counts of uncompressed data; the raw size is then implied.
            Counts = new long[Offsets.Length];
            for (int i = 0; i < Counts.Length && i < total; i++)
            {
                Counts[i] = rowBytes * RowsInBlock(i % BlocksPerPlane);
            }
        }
        else
        {
            throw new ImageFormatException("The TIFF has no strip or tile byte counts.");
        }

        _ = limits;
        if (Codec == TiffCodec.Jpeg)
        {
            TiffEntry? jt = Find(entries, TiffTag.JpegTables);
            if (jt is { } t)
            {
                JpegTables = file.ReadBytes(t, 1 << 20);
            }
        }
    }
}
