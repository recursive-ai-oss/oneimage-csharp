using System.Buffers;
using System.Buffers.Binary;
using OneImage.Compression;
using OneImage.Kernels;

namespace OneImage;

/// <summary>Reads <paramref name="count"/> rows starting at <paramref name="y"/> into <paramref name="destination"/>.</summary>
internal delegate void PngRowReader(int y, int count, Span<byte> destination);

/// <summary>A color table for an indexed PNG, with the per-pixel indices when a quantizer produced them.</summary>
internal sealed class PngPalette
{
    private readonly ulong[] _table = new ulong[1024];

    public PngPalette(uint[] colors, byte[]? indices)
    {
        Colors = colors;
        Indices = indices;
        for (int i = 0; i < colors.Length; i++)
        {
            Insert(colors[i], i);
        }
    }

    /// <summary>Entries as little-endian RGBA words (red in the low byte); non-opaque entries come first.</summary>
    public uint[] Colors { get; }

    /// <summary>Row-major palette indices for the whole image, or <see langword="null"/> when pixels are looked up by color.</summary>
    public byte[]? Indices { get; }

    public int Lookup(uint color)
    {
        uint h = (color * 0x9E3779B1u) >> 22;
        while (true)
        {
            ulong e = _table[h];
            if (e == 0)
            {
                return 0;
            }

            if ((uint)e == color)
            {
                return (int)((e >> 32) & 0xFF);
            }

            h = (h + 1) & 1023;
        }
    }

    private void Insert(uint color, int index)
    {
        uint h = (color * 0x9E3779B1u) >> 22;
        while (_table[h] != 0)
        {
            h = (h + 1) & 1023;
        }

        _table[h] = (1UL << 40) | ((ulong)index << 32) | color;
    }
}

/// <summary>
/// Where a shared palette quantizer plugs in. <see cref="TryQuantize"/> is called when a PNG is requested as indexed
/// (<see cref="PngColorMode.Indexed"/>) but has more than 256 colors. The shared quantizer reduces it without dithering and keeps alpha.
/// </summary>
internal static class PngQuantizerHook
{
    /// <summary>
    /// Reduces the image to at most 256 colors. A real implementation returns the palette (alpha-bearing entries first)
    /// together with the row-major indices for the whole image.
    /// </summary>
    public static PngPalette? TryQuantize(PngRowReader rows, int width, int height, PixelFormat format)
    {
        int bpp = format.BytesPerPixel;
        if (format.Sample != SampleType.U8 || bpp is not (3 or 4) || (long)width * height > Array.MaxLength)
        {
            return null;
        }

        int stride = width * bpp;
        int stripRows = Math.Max(1, Math.Min(height, (1 << 18) / stride));
        byte[] strip = ArrayPool<byte>.Shared.Rent(stripRows * stride);
        byte[] rgba = ArrayPool<byte>.Shared.Rent(stripRows * width * 4);
        try
        {
            // Two forward passes over the rows: statistics, then mapping. Opaque input uses the cheaper binary-alpha statistics.
            var quantizer = new Ops.Quantize.ColorQuantizer(256, format.HasAlpha ? QuantizeAlpha.Full : QuantizeAlpha.Binary);
            for (int y0 = 0; y0 < height; y0 += stripRows)
            {
                int n = Math.Min(stripRows, height - y0);
                rows(y0, n, strip);
                ToRgba(strip, rgba, n * width, bpp);
                quantizer.AddRows(rgba.AsSpan(0, n * width * 4));
            }

            Ops.Quantize.QuantizedPalette q = quantizer.BuildPalette();
            var mapper = new Ops.Quantize.PaletteMapper(q, width, Dither.None);
            var indices = new byte[width * height];
            for (int y0 = 0; y0 < height; y0 += stripRows)
            {
                int n = Math.Min(stripRows, height - y0);
                rows(y0, n, strip);
                ToRgba(strip, rgba, n * width, bpp);
                for (int r = 0; r < n; r++)
                {
                    mapper.MapRow(y0 + r, rgba.AsSpan(r * width * 4, width * 4), indices.AsSpan((y0 + r) * width, width));
                }
            }

            // PNG wants the entries that carry alpha first (they get tRNS values); renumber the palette and the indices.
            int count = q.Count;
            var order = new int[count];
            int next = 0;
            for (int i = 0; i < count; i++)
            {
                if (q.Rgba[(i * 4) + 3] != 255)
                {
                    order[i] = next++;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (q.Rgba[(i * 4) + 3] == 255)
                {
                    order[i] = next++;
                }
            }

            var colors = new uint[count];
            for (int i = 0; i < count; i++)
            {
                colors[order[i]] = BinaryPrimitives.ReadUInt32LittleEndian(q.Rgba.AsSpan(i * 4, 4));
            }

            var map = new byte[256];
            for (int i = 0; i < count; i++)
            {
                map[i] = (byte)order[i];
            }

            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = map[indices[i]];
            }

            return new PngPalette(colors, indices);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(strip);
            ArrayPool<byte>.Shared.Return(rgba);
        }
    }

    private static void ToRgba(byte[] source, byte[] destination, int pixels, int bpp)
    {
        if (bpp == 4)
        {
            source.AsSpan(0, pixels * 4).CopyTo(destination);
            return;
        }

        for (int i = 0; i < pixels; i++)
        {
            destination[i * 4] = source[i * 3];
            destination[(i * 4) + 1] = source[(i * 3) + 1];
            destination[(i * 4) + 2] = source[(i * 3) + 2];
            destination[(i * 4) + 3] = 255;
        }
    }
}

/// <summary>Writes a stream of bytes as consecutive IDAT or fdAT chunks.</summary>
internal sealed class PngChunkWriter : IBufferWriter<byte>, IDisposable
{
    private const int Target = 65536;
    private readonly Stream _s;
    private readonly uint _type;
    private readonly int[]? _seq;
    private byte[]? _buf;
    private int _len;

    public PngChunkWriter(Stream s, uint type, int[]? sequence)
    {
        _s = s;
        _type = type;
        _seq = sequence;
        _buf = ArrayPool<byte>.Shared.Rent(Target + 8192);
    }

    public void Advance(int count)
    {
        _len += count;
        if (_len >= Target)
        {
            Flush();
        }
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buf.AsMemory(_len);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buf.AsSpan(_len);
    }

    public void Flush()
    {
        if (_len == 0)
        {
            return;
        }

        Span<byte> head = stackalloc byte[12];
        bool fdat = _seq != null;
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)(_len + (fdat ? 4 : 0)));
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], _type);
        int headLen = 8;
        if (fdat)
        {
            BinaryPrimitives.WriteInt32BigEndian(head[8..], _seq![0]++);
            headLen = 12;
        }

        uint crc = Crc32.Update(Crc32.Update(0, head[4..headLen]), _buf.AsSpan(0, _len));
        _s.Write(head[..headLen]);
        _s.Write(_buf!, 0, _len);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        _s.Write(tail);
        _len = 0;
    }

    public void Dispose() => ContainerIo.Return(ref _buf);

    private void Ensure(int hint)
    {
        hint = Math.Max(hint, 1);
        if (_buf!.Length - _len >= hint)
        {
            return;
        }

        Flush();
        if (_buf.Length < hint)
        {
            ArrayPool<byte>.Shared.Return(_buf);
            _buf = ArrayPool<byte>.Shared.Rent(hint);
        }
    }
}

/// <summary>
/// PNG and APNG encoder. Rows stream through the filter and the deflater, so memory use is proportional to the width
/// (animations hold two full frames). Output is deterministic.
/// </summary>
/// <remarks>
/// <para>Filter choice: <see cref="PngFilterMode.Adaptive"/> computes all five filtered versions of each row and keeps the one
/// with the smallest sum of absolute values of its bytes read as signed numbers; ties go to the earlier filter in the order
/// None, Sub, Up, Average, Paeth. Images with a bit depth under 8 use no filter.</para>
/// <para>Written chunk order: IHDR, iCCP, PLTE, tRNS, eXIf, iTXt (XMP), acTL, then image data. A palette is written
/// when the policy allows and the image has at most 256 colors; the palette lists non-opaque colors first so tRNS stays short.</para>
/// <para>Animations: frames are stored as the changed rectangle against the previous frame with blend op "source" and
/// disposal "none", and the first frame fills the canvas and doubles as the default image. Frame delays are written as
/// milliseconds (denominator 1000) when they fit, else whole seconds. All frames must have the same size and are
/// converted to a common pixel format. Metadata: ICC as iCCP, EXIF as eXIf without any leading "Exif\0\0", XMP as
/// iTXt. The EXIF orientation tag is written as given.</para>
/// </remarks>
internal sealed class PngEncoder : IImageEncoder
{
    public static PngEncoder Instance { get; } = new();

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } =
    [
        PixelFormat.Rgba8, PixelFormat.Rgb8, PixelFormat.Gray8, PixelFormat.GrayAlpha8,
        PixelFormat.Rgba16, PixelFormat.Rgb16, PixelFormat.Gray16,
    ];

    public bool SupportsMultipleFrames => true;

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        var options = request.Options as PngEncoderOptions ?? new PngEncoderOptions();
        if (request.Frames.Count == 1)
        {
            ImageFrameReader f = request.Frames[0];
            EncodeStill(destination, f.ReadRows, f.Width, f.Height, f.Format, request.Metadata, options, cancellationToken);
        }
        else
        {
            EncodeAnimated(destination, request.Frames, request.LoopCount, request.Metadata, options, cancellationToken);
        }
    }

    /// <summary>Encodes one image given a row source. Used by the ICO writer for PNG entries.</summary>
    internal static void EncodeStill(Stream dest, PngRowReader rows, int width, int height, PixelFormat format, ImageMetadata meta, PngEncoderOptions options, CancellationToken ct)
    {
        (int colorType, int depth) = Layout(format);
        PngPalette? palette = null;
        if (options.ColorMode != PngColorMode.Truecolor && (format == PixelFormat.Rgb8 || format == PixelFormat.Rgba8))
        {
            palette = ScanPalette(rows, width, height, format, ct);
            if (palette == null && options.ColorMode == PngColorMode.Indexed)
            {
                palette = PngQuantizerHook.TryQuantize(rows, width, height, format)
                    ?? throw new ImageOperationException("The image has more than 256 colors and no palette quantizer is available.");
            }
        }

        int paletteBits = 0;
        if (palette != null)
        {
            int n = palette.Colors.Length;
            paletteBits = n <= 2 ? 1 : n <= 4 ? 2 : n <= 16 ? 4 : 8;
            colorType = 3;
            depth = paletteBits;
        }

        dest.Write(PngChunk.Signature);
        WriteHeader(dest, width, height, depth, colorType);
        WriteMetadata(dest, meta, palette);
        using (var idat = new PngChunkWriter(dest, PngChunk.IDAT, null))
        {
            using var deflater = new Deflater(idat, options.CompressionLevel);
            deflater.FilteredStrategy = UsesFilters(options.Filter, palette != null && paletteBits < 8);
            WriteImageData(rows, width, height, format, palette, paletteBits, deflater, options.Filter, ct);
            deflater.Finish();
            idat.Flush();
        }

        WriteChunk(dest, PngChunk.IEND, []);
    }

    private static void EncodeAnimated(Stream dest, IReadOnlyList<ImageFrameReader> frames, int loopCount, ImageMetadata meta, PngEncoderOptions options, CancellationToken ct)
    {
        int width = frames[0].Width;
        int height = frames[0].Height;
        bool gray = true;
        bool alpha = false;
        bool wide = false;
        foreach (ImageFrameReader f in frames)
        {
            if (f.Width != width || f.Height != height)
            {
                throw new ImageOperationException("APNG frames must all have the same size.");
            }

            gray &= f.Format.Model == ColorModel.Gray;
            alpha |= f.Format.HasAlpha;
            wide |= f.Format.Sample == SampleType.U16;
        }

        PixelFormat format = (gray, alpha, wide) switch
        {
            (true, false, false) => PixelFormat.Gray8,
            (true, true, false) => PixelFormat.GrayAlpha8,
            (true, false, true) => PixelFormat.Gray16,
            (false, false, false) => PixelFormat.Rgb8,
            (false, true, false) => PixelFormat.Rgba8,
            (false, false, true) => PixelFormat.Rgb16,
            _ => PixelFormat.Rgba16,
        };
        (int colorType, int depth) = Layout(format);
        int bpp = format.BytesPerPixel;
        long frameBytes = (long)width * height * bpp;
        if (frameBytes > Array.MaxLength / 2)
        {
            throw new ImageLimitException("The animation frames are too large to hold in memory.");
        }

        int stride = width * bpp;
        byte[] prev = new byte[frameBytes];
        byte[] cur = new byte[frameBytes];
        int[] seq = [0];

        dest.Write(PngChunk.Signature);
        WriteHeader(dest, width, height, depth, colorType);
        WriteMetadata(dest, meta, null);
        Span<byte> actl = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames.Count);
        BinaryPrimitives.WriteUInt32BigEndian(actl[4..], (uint)Math.Max(0, loopCount));
        WriteChunk(dest, PngChunk.acTL, actl);

        Span<byte> fctl = stackalloc byte[26];
        for (int i = 0; i < frames.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ImageFrameReader f = frames[i];
            ReadFrame(f, format, cur, stride, ct);
            int rx = 0, ry = 0, rw = width, rh = height;
            if (i > 0)
            {
                DiffBounds(prev, cur, width, height, bpp, out rx, out ry, out rw, out rh);
            }

            BinaryPrimitives.WriteInt32BigEndian(fctl, seq[0]++);
            BinaryPrimitives.WriteUInt32BigEndian(fctl[4..], (uint)rw);
            BinaryPrimitives.WriteUInt32BigEndian(fctl[8..], (uint)rh);
            BinaryPrimitives.WriteUInt32BigEndian(fctl[12..], (uint)rx);
            BinaryPrimitives.WriteUInt32BigEndian(fctl[16..], (uint)ry);
            (ushort num, ushort den) = EncodeDelay(f.Delay);
            BinaryPrimitives.WriteUInt16BigEndian(fctl[20..], num);
            BinaryPrimitives.WriteUInt16BigEndian(fctl[22..], den);
            fctl[24] = 0;
            fctl[25] = 0;
            WriteChunk(dest, PngChunk.fcTL, fctl);

            byte[] frameBuf = cur;
            int fx = rx, fy = ry, fw = rw;
            void Rows(int y, int count, Span<byte> d)
            {
                for (int r = 0; r < count; r++)
                {
                    frameBuf.AsSpan(((fy + y + r) * stride) + (fx * bpp), fw * bpp).CopyTo(d.Slice(r * fw * bpp, fw * bpp));
                }
            }

            using (var writer = new PngChunkWriter(dest, i == 0 ? PngChunk.IDAT : PngChunk.fdAT, i == 0 ? null : seq))
            {
                using var deflater = new Deflater(writer, options.CompressionLevel);
                deflater.FilteredStrategy = UsesFilters(options.Filter, false);
                WriteImageData(Rows, rw, rh, format, null, 0, deflater, options.Filter, ct);
                deflater.Finish();
                writer.Flush();
            }

            (prev, cur) = (cur, prev);
        }

        WriteChunk(dest, PngChunk.IEND, []);
    }

    private static void ReadFrame(ImageFrameReader f, PixelFormat target, byte[] buffer, int stride, CancellationToken ct)
    {
        int height = f.Height;
        if (f.Format == target)
        {
            int strip = Math.Max(1, (1 << 20) / Math.Max(1, stride));
            for (int y = 0; y < height; y += strip)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(strip, height - y);
                f.ReadRows(y, n, buffer.AsSpan(y * stride, n * stride));
            }

            return;
        }

        int srcStride = f.Width * f.Format.BytesPerPixel;
        int rowsPer = Math.Max(1, (1 << 20) / Math.Max(1, srcStride));
        byte[] tmp = ArrayPool<byte>.Shared.Rent(rowsPer * srcStride);
        try
        {
            for (int y = 0; y < height; y += rowsPer)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(rowsPer, height - y);
                f.ReadRows(y, n, tmp);
                for (int r = 0; r < n; r++)
                {
                    ConvertKernel.ConvertRow(tmp.AsSpan(r * srcStride, srcStride), f.Format, buffer.AsSpan((y + r) * stride, stride), target, f.Width);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    private static void DiffBounds(byte[] a, byte[] b, int width, int height, int bpp, out int x, out int y, out int w, out int h)
    {
        int stride = width * bpp;
        int top = -1, bottom = -1, left = width, right = -1;
        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> ra = a.AsSpan(row * stride, stride);
            ReadOnlySpan<byte> rb = b.AsSpan(row * stride, stride);
            if (ra.SequenceEqual(rb))
            {
                continue;
            }

            if (top < 0)
            {
                top = row;
            }

            bottom = row;
            int first = ra.CommonPrefixLength(rb) / bpp;
            left = Math.Min(left, first);
            int last = stride - 1;
            while (ra[last] == rb[last])
            {
                last--;
            }

            right = Math.Max(right, last / bpp);
        }

        if (top < 0)
        {
            // Identical frame: the smallest legal rectangle.
            x = 0;
            y = 0;
            w = 1;
            h = 1;
            return;
        }

        x = left;
        y = top;
        w = right - left + 1;
        h = bottom - top + 1;
    }

    // Like libpng, rows that are really filtered are deflated with the "filtered" strategy, which ignores short matches.
    private static bool UsesFilters(PngFilterMode mode, bool lowBitDepth) =>
        mode != PngFilterMode.None && !(lowBitDepth && mode == PngFilterMode.Adaptive);

    private static (ushort Num, ushort Den) EncodeDelay(TimeSpan delay)
    {
        long ms = Math.Max(0, (delay.Ticks + (TimeSpan.TicksPerMillisecond / 2)) / TimeSpan.TicksPerMillisecond);
        if (ms <= ushort.MaxValue)
        {
            return ((ushort)ms, 1000);
        }

        return ((ushort)Math.Min(ushort.MaxValue, (ms + 500) / 1000), 1);
    }

    private static (int ColorType, int Depth) Layout(PixelFormat f)
    {
        if (f == PixelFormat.Gray8)
        {
            return (0, 8);
        }

        if (f == PixelFormat.GrayAlpha8)
        {
            return (4, 8);
        }

        if (f == PixelFormat.Gray16)
        {
            return (0, 16);
        }

        if (f == PixelFormat.Rgb8)
        {
            return (2, 8);
        }

        if (f == PixelFormat.Rgba8)
        {
            return (6, 8);
        }

        if (f == PixelFormat.Rgb16)
        {
            return (2, 16);
        }

        if (f == PixelFormat.Rgba16)
        {
            return (6, 16);
        }

        throw new ImageOperationException($"PNG cannot store pixel format {f}.");
    }

    // ---- Chunks ----

    private static void WriteChunk(Stream s, uint type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], type);
        uint crc = Crc32.Update(Crc32.Compute(head[4..]), data);
        s.Write(head);
        s.Write(data);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        s.Write(tail);
    }

    private static void WriteHeader(Stream s, int width, int height, int depth, int colorType)
    {
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(s, PngChunk.IHDR, ihdr);
    }

    private static void WriteMetadata(Stream s, ImageMetadata meta, PngPalette? palette)
    {
        if (meta.IccProfile.Length > 0)
        {
            byte[] z = Deflater.Compress(meta.IccProfile.Span, 6);
            byte[] body = new byte[4 + 1 + z.Length];
            "ICC"u8.CopyTo(body);
            body[3] = 0;
            body[4] = 0;
            z.CopyTo(body, 5);
            WriteChunk(s, PngChunk.iCCP, body);
        }

        if (palette != null)
        {
            uint[] colors = palette.Colors;
            byte[] plte = new byte[colors.Length * 3];
            int lastAlpha = -1;
            for (int i = 0; i < colors.Length; i++)
            {
                plte[i * 3] = (byte)colors[i];
                plte[(i * 3) + 1] = (byte)(colors[i] >> 8);
                plte[(i * 3) + 2] = (byte)(colors[i] >> 16);
                if ((colors[i] >> 24) != 255)
                {
                    lastAlpha = i;
                }
            }

            WriteChunk(s, PngChunk.PLTE, plte);
            if (lastAlpha >= 0)
            {
                byte[] trns = new byte[lastAlpha + 1];
                for (int i = 0; i <= lastAlpha; i++)
                {
                    trns[i] = (byte)(colors[i] >> 24);
                }

                WriteChunk(s, PngChunk.tRNS, trns);
            }
        }

        if (meta.Exif.Length > 0)
        {
            ReadOnlySpan<byte> exif = meta.Exif.Span;
            if (exif.StartsWith("Exif\0\0"u8))
            {
                exif = exif[6..];
            }

            if (exif.Length >= 8)
            {
                WriteChunk(s, PngChunk.eXIf, exif);
            }
        }

        if (meta.Xmp.Length > 0)
        {
            ReadOnlySpan<byte> key = "XML:com.adobe.xmp"u8;
            byte[] body = new byte[key.Length + 5 + meta.Xmp.Length];
            key.CopyTo(body);
            // keyword NUL, compression flag 0, method 0, empty language NUL, empty translated keyword NUL, text
            meta.Xmp.Span.CopyTo(body.AsSpan(key.Length + 5));
            WriteChunk(s, PngChunk.iTXt, body);
        }
    }

    // ---- Palette detection ----

    private static PngPalette? ScanPalette(PngRowReader rows, int width, int height, PixelFormat format, CancellationToken ct)
    {
        int bpp = format.BytesPerPixel;
        int stride = width * bpp;
        int stripRows = Math.Max(1, Math.Min(height, (1 << 18) / stride));
        var seen = new ulong[1024];
        var colors = new uint[257];
        int count = 0;
        byte[] strip = ArrayPool<byte>.Shared.Rent(stripRows * stride);
        try
        {
            for (int y0 = 0; y0 < height; y0 += stripRows)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(stripRows, height - y0);
                rows(y0, n, strip);
                ReadOnlySpan<byte> px = strip.AsSpan(0, n * stride);
                uint last = 0;
                bool haveLast = false;
                for (int i = 0; i < n * width; i++)
                {
                    uint c = bpp == 4
                        ? BinaryPrimitives.ReadUInt32LittleEndian(px.Slice(i * 4, 4))
                        : px[i * 3] | ((uint)px[(i * 3) + 1] << 8) | ((uint)px[(i * 3) + 2] << 16) | 0xFF000000u;
                    if (haveLast && c == last)
                    {
                        continue;
                    }

                    last = c;
                    haveLast = true;
                    uint h = (c * 0x9E3779B1u) >> 22;
                    while (true)
                    {
                        ulong e = seen[h];
                        if (e == 0)
                        {
                            if (count == 256)
                            {
                                return null;
                            }

                            seen[h] = (1UL << 32) | c;
                            colors[count++] = c;
                            break;
                        }

                        if ((uint)e == c)
                        {
                            break;
                        }

                        h = (h + 1) & 1023;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(strip);
        }

        // Non-opaque colors first (in order of first appearance), then the opaque ones.
        var ordered = new uint[count];
        int k = 0;
        for (int i = 0; i < count; i++)
        {
            if ((colors[i] >> 24) != 255)
            {
                ordered[k++] = colors[i];
            }
        }

        for (int i = 0; i < count; i++)
        {
            if ((colors[i] >> 24) == 255)
            {
                ordered[k++] = colors[i];
            }
        }

        return new PngPalette(ordered, null);
    }

    // ---- Image data ----

    private static void WriteImageData(PngRowReader rows, int width, int height, PixelFormat srcFormat, PngPalette? palette, int paletteBits, Deflater deflater, PngFilterMode mode, CancellationToken ct)
    {
        int srcBpp = srcFormat.BytesPerPixel;
        int srcStride = width * srcBpp;
        int rawLen = palette != null ? ((width * paletteBits) + 7) / 8 : srcStride;
        int filterBpp = palette != null ? 1 : srcBpp;
        bool wide = palette == null && srcFormat.Sample == SampleType.U16;
        PngFilterMode effective = mode;
        if (palette != null && paletteBits < 8 && mode == PngFilterMode.Adaptive)
        {
            effective = PngFilterMode.None;
        }

        int stripRows = Math.Max(1, Math.Min(height, (1 << 18) / Math.Max(1, srcStride)));
        byte[]? strip = ArrayPool<byte>.Shared.Rent(stripRows * srcStride);
        byte[]? cur = ArrayPool<byte>.Shared.Rent(rawLen);
        byte[]? prev = ArrayPool<byte>.Shared.Rent(rawLen);
        byte[]?[] cand = new byte[5][];
        try
        {
            for (int i = 0; i < 5; i++)
            {
                cand[i] = ArrayPool<byte>.Shared.Rent(rawLen + 1);
            }

            Array.Clear(prev, 0, rawLen);
            byte[]? indices = palette?.Indices;
            for (int y0 = 0; y0 < height; y0 += stripRows)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(stripRows, height - y0);
                if (indices == null)
                {
                    rows(y0, n, strip);
                }

                for (int r = 0; r < n; r++)
                {
                    Span<byte> raw = cur.AsSpan(0, rawLen);
                    ReadOnlySpan<byte> src = strip.AsSpan(r * srcStride, srcStride);
                    if (palette != null)
                    {
                        PackIndices(palette, indices != null ? indices.AsSpan((y0 + r) * width, width) : default, src, srcBpp, width, paletteBits, raw);
                    }
                    else if (wide)
                    {
                        PngPixels.Swap16(src, raw);
                    }
                    else
                    {
                        src.CopyTo(raw);
                    }

                    ReadOnlySpan<byte> above = prev.AsSpan(0, rawLen);
                    byte[] best = ChooseFilter(effective, raw, above, filterBpp, cand!);
                    deflater.Write(best.AsSpan(0, rawLen + 1));
                    (cur, prev) = (prev, cur);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(strip);
            ArrayPool<byte>.Shared.Return(cur!);
            ArrayPool<byte>.Shared.Return(prev!);
            foreach (byte[]? c in cand)
            {
                if (c != null)
                {
                    ArrayPool<byte>.Shared.Return(c);
                }
            }
        }
    }

    // cand[0] holds the unfiltered row; cand[f] the row filtered with filter f. Each starts with its filter-type byte.
    private static byte[] ChooseFilter(PngFilterMode mode, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> above, int bpp, byte[][] cand)
    {
        int n = raw.Length;
        if (mode == PngFilterMode.None)
        {
            cand[0][0] = 0;
            raw.CopyTo(cand[0].AsSpan(1));
            return cand[0];
        }

        if (mode != PngFilterMode.Adaptive)
        {
            int f = mode switch { PngFilterMode.Sub => 1, PngFilterMode.Up => 2, PngFilterMode.Average => 3, _ => 4 };
            cand[f][0] = (byte)f;
            PngFilter.Apply(f, raw, above, cand[f].AsSpan(1, n), bpp);
            return cand[f];
        }

        cand[0][0] = 0;
        raw.CopyTo(cand[0].AsSpan(1));
        long bestScore = PngFilter.SumAbs(raw);
        int best = 0;
        for (int f = 1; f <= 4; f++)
        {
            cand[f][0] = (byte)f;
            Span<byte> d = cand[f].AsSpan(1, n);
            PngFilter.Apply(f, raw, above, d, bpp);
            long score = PngFilter.SumAbs(d);
            if (score < bestScore)
            {
                bestScore = score;
                best = f;
            }
        }

        return cand[best];
    }

    private static void PackIndices(PngPalette palette, ReadOnlySpan<byte> given, ReadOnlySpan<byte> src, int srcBpp, int width, int bits, Span<byte> dest)
    {
        dest.Clear();
        uint lastColor = 0;
        int lastIndex = -1;
        for (int x = 0; x < width; x++)
        {
            int idx;
            if (!given.IsEmpty)
            {
                idx = given[x];
            }
            else
            {
                uint c = srcBpp == 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(x * 4, 4))
                    : src[x * 3] | ((uint)src[(x * 3) + 1] << 8) | ((uint)src[(x * 3) + 2] << 16) | 0xFF000000u;
                if (lastIndex >= 0 && c == lastColor)
                {
                    idx = lastIndex;
                }
                else
                {
                    idx = palette.Lookup(c);
                    lastColor = c;
                    lastIndex = idx;
                }
            }

            if (bits == 8)
            {
                dest[x] = (byte)idx;
            }
            else
            {
                int bitPos = x * bits;
                dest[bitPos >> 3] |= (byte)(idx << (8 - bits - (bitPos & 7)));
            }
        }
    }
}
