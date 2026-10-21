using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace OneImage;

/// <summary>The BMP format registration: signature detection plus the decoder and encoder.</summary>
public sealed class BmpFormat : IImageFormat
{
    private BmpFormat()
    {
    }

    /// <summary>Gets the shared instance. Register it with <see cref="ImageConfiguration.Add"/>.</summary>
    public static BmpFormat Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "bmp";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".bmp", ".dib"];

    /// <inheritdoc />
    public string MimeType => "image/bmp";

    /// <inheritdoc />
    public int HeaderLength => 18;

    /// <inheritdoc />
    public IImageDecoder? Decoder => BmpDecoder.Instance;

    /// <inheritdoc />
    public IImageEncoder? Encoder => BmpEncoder.Instance;

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length < 18 || header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            return false;
        }

        // The DIB header size distinguishes BMP from other files that start with "BM".
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
        return size is 12 or 40 or 52 or 56 or 64 or 108 or 124;
    }
}

/// <summary>Everything the header of one DIB (a BMP without its 14-byte file header) says.</summary>
internal sealed class BmpState
{
    public long DibOffset;
    public int HeaderSize;
    public int Width;
    public int Height;
    public bool TopDown;
    public int Bpp;
    public int Compression;
    public byte[] Palette = new byte[1024];
    public int PaletteCount;
    public uint MaskR;
    public uint MaskG;
    public uint MaskB;
    public uint MaskA;
    public long DataOffset;
    public PixelFormat Format;
    public byte[]? Icc;

    /// <summary>For icons: the 1-bit AND mask follows the pixels, and the stored height is twice the image height.</summary>
    public bool IsIcon;
}

/// <summary>
/// BMP decoder: 1, 4, 8, 16, 24 and 32 bits per pixel, RLE4 and RLE8, BITFIELDS masks, and the core, Windows 3 (40),
/// V4 (108) and V5 (124) headers (plus the 52/56-byte variants and OS/2 2.x 64-byte headers without OS/2 compression),
/// bottom-up and top-down.
/// </summary>
/// <remarks>
/// <para>Output is RGB8, or RGBA8 when an alpha mask is present (BITFIELDS with an alpha mask). A 32-bit BI_RGB bitmap
/// has no defined alpha and is read as opaque RGB. Channels narrower or wider than 8 bits scale to 0..255 by
/// round-to-nearest (v * 255 / max). Palette entries past the table read as black.</para>
/// <para>RLE bitmaps are decoded in full in memory (they are stored bottom-up); pixels skipped by delta or end-of-line
/// codes take palette entry 0. Uncompressed bitmaps stream a block of rows at a time.</para>
/// <para>A V5 header with an embedded profile reports the profile as ICC metadata; linked profiles and color
/// space endpoints are ignored. BI_JPEG and BI_PNG payloads are not supported.</para>
/// </remarks>
internal sealed class BmpDecoder : IImageDecoder
{
    public static BmpDecoder Instance { get; } = new();

    public DecoderCapabilities Capabilities => DecoderCapabilities.Region;

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        source.Position = 0;
        Span<byte> fh = stackalloc byte[14];
        if (ContainerIo.ReadUpTo(source, fh) < 14 || fh[0] != (byte)'B' || fh[1] != (byte)'M')
        {
            throw new ImageFormatException("Not a BMP file: bad signature.");
        }

        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(fh[10..]);
        BmpState st = ParseDib(source, 14, false, dataOffset, limits);
        var meta = new ImageMetadata { IccProfile = st.Icc ?? default };
        var frames = new[] { new FrameInfo(st.Width, st.Height, st.Format) };
        return new ImageInfo(BmpFormat.Instance, frames, 0, meta, st);
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(sink);
        var st = (BmpState?)info.DecoderState ?? throw new ImageOperationException("The image info was not produced by the BMP decoder.");
        ArgumentOutOfRangeException.ThrowIfNotEqual(frameIndex, 0);

        DecodeRows(source, st, request.Region, sink, cancellationToken);
    }

    /// <summary>Parses the DIB header at <paramref name="dibOffset"/>. For icons <paramref name="dataOffset"/> is ignored (pixels follow the tables).</summary>
    internal static BmpState ParseDib(Stream s, long dibOffset, bool icon, long dataOffset, ImageLimits limits)
    {
        long length = s.Length;
        s.Position = dibOffset;
        Span<byte> h = stackalloc byte[124];
        int got = ContainerIo.ReadUpTo(s, h[..4]);
        if (got < 4)
        {
            throw new ImageFormatException("The file ends inside the BMP header.");
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (size is not (12 or 40 or 52 or 56 or 64 or 108 or 124))
        {
            throw new ImageFormatException($"Unsupported BMP header size {size}.");
        }

        int have = ContainerIo.ReadUpTo(s, h.Slice(4, (int)size - 4));
        if (have < (int)size - 4)
        {
            throw new ImageFormatException("The file ends inside the BMP header.");
        }

        var st = new BmpState { DibOffset = dibOffset, HeaderSize = (int)size, IsIcon = icon };
        long width;
        long height;
        if (size == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
            st.Bpp = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
        }
        else
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
            st.Bpp = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
            st.Compression = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(h[16..]), 100);
        }

        if (st.Bpp is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            throw new ImageFormatException($"Unsupported BMP bit depth {st.Bpp}.");
        }

        if (width <= 0 || height == 0)
        {
            throw new ImageFormatException($"Invalid BMP size {width}x{height}.");
        }

        st.TopDown = height < 0;
        height = Math.Abs(height);
        if (icon)
        {
            height /= 2;
            if (height == 0)
            {
                throw new ImageFormatException("Invalid icon bitmap height.");
            }
        }

        limits.CheckFrame(width, height);
        st.Width = (int)width;
        st.Height = (int)height;

        int comp = st.Compression;
        bool bitfields = comp is 3 or 6;
        switch (comp)
        {
            case 0:
                break;
            case 1 when st.Bpp == 8:
            case 2 when st.Bpp == 4:
                if (icon)
                {
                    throw new ImageFormatException("Icons cannot use RLE compression.");
                }

                break;
            case 3 or 6 when st.Bpp is 16 or 32:
                break;
            case 4 or 5:
                throw new ImageFormatException("BMP files with embedded JPEG or PNG data are not supported.");
            default:
                throw new ImageFormatException($"Unsupported BMP compression {comp} for {st.Bpp} bits per pixel.");
        }

        // Where the palette (and for old 40-byte headers, the masks) start.
        long tableStart = dibOffset + size;
        if (bitfields)
        {
            if (size >= 52)
            {
                st.MaskR = BinaryPrimitives.ReadUInt32LittleEndian(h[40..]);
                st.MaskG = BinaryPrimitives.ReadUInt32LittleEndian(h[44..]);
                st.MaskB = BinaryPrimitives.ReadUInt32LittleEndian(h[48..]);
                if (size >= 56)
                {
                    st.MaskA = BinaryPrimitives.ReadUInt32LittleEndian(h[52..]);
                }
            }
            else
            {
                int n = comp == 6 ? 16 : 12;
                Span<byte> m = stackalloc byte[16];
                s.Position = tableStart;
                if (ContainerIo.ReadUpTo(s, m[..n]) < n)
                {
                    throw new ImageFormatException("The file ends inside the BMP color masks.");
                }

                st.MaskR = BinaryPrimitives.ReadUInt32LittleEndian(m);
                st.MaskG = BinaryPrimitives.ReadUInt32LittleEndian(m[4..]);
                st.MaskB = BinaryPrimitives.ReadUInt32LittleEndian(m[8..]);
                if (n == 16)
                {
                    st.MaskA = BinaryPrimitives.ReadUInt32LittleEndian(m[12..]);
                }

                tableStart += n;
            }

            if (size < 108 && comp == 3)
            {
                st.MaskA = 0;
            }
        }
        else if (st.Bpp == 16)
        {
            (st.MaskR, st.MaskG, st.MaskB) = (0x7C00, 0x03E0, 0x001F);
        }
        else if (st.Bpp == 32)
        {
            (st.MaskR, st.MaskG, st.MaskB) = (0x00FF0000, 0x0000FF00, 0x000000FF);
            if (icon)
            {
                st.MaskA = 0xFF000000;
            }
        }

        if (st.Bpp is 16 or 32)
        {
            CheckMask(st.MaskR);
            CheckMask(st.MaskG);
            CheckMask(st.MaskB);
            CheckMask(st.MaskA);
            if (st.MaskR == 0 || st.MaskG == 0 || st.MaskB == 0)
            {
                throw new ImageFormatException("A BMP color mask is empty.");
            }

            if (st.Bpp == 16 && ((st.MaskR | st.MaskG | st.MaskB | st.MaskA) >> 16) != 0)
            {
                throw new ImageFormatException("A 16-bit BMP color mask is wider than 16 bits.");
            }
        }

        st.Format = st.MaskA != 0 ? PixelFormat.Rgba8 : PixelFormat.Rgb8;

        // Palette.
        long paletteBytes = 0;
        if (st.Bpp <= 8)
        {
            int entrySize = size == 12 ? 3 : 4;
            long clrUsed = size == 12 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(h[32..]);
            long count = clrUsed == 0 || clrUsed > (1 << st.Bpp) ? 1 << st.Bpp : clrUsed;
            paletteBytes = count * entrySize;
            if (tableStart + paletteBytes > length)
            {
                throw new ImageFormatException("The file ends inside the BMP palette.");
            }

            byte[] raw = new byte[paletteBytes];
            s.Position = tableStart;
            ContainerIo.ReadFully(s, raw);
            st.PaletteCount = (int)count;
            for (int i = 0; i < 256; i++)
            {
                st.Palette[(i * 4) + 3] = 255;
            }

            for (int i = 0; i < count; i++)
            {
                st.Palette[i * 4] = raw[(i * entrySize) + 2];
                st.Palette[(i * 4) + 1] = raw[(i * entrySize) + 1];
                st.Palette[(i * 4) + 2] = raw[i * entrySize];
            }
        }

        st.DataOffset = icon ? tableStart + paletteBytes : dataOffset;
        if (st.DataOffset <= 0 || st.DataOffset >= length)
        {
            throw new ImageFormatException("The BMP pixel data offset is outside the file.");
        }

        // V5 embedded profile (PROFILE_EMBEDDED = 'MBED').
        if (size == 124 && BinaryPrimitives.ReadUInt32LittleEndian(h[56..]) == 0x4D424544)
        {
            long off = dibOffset + BinaryPrimitives.ReadUInt32LittleEndian(h[112..]);
            long len = BinaryPrimitives.ReadUInt32LittleEndian(h[116..]);
            if (len > 0 && len <= limits.MaxMetadataBytes && off >= 0 && off + len <= length)
            {
                s.Position = off;
                st.Icc = ContainerIo.ReadArray(s, len);
            }
        }

        return st;
    }

    private static void CheckMask(uint m)
    {
        if (m == 0)
        {
            return;
        }

        uint v = m >> BitOperations.TrailingZeroCount(m);
        if ((v & (v + 1)) != 0)
        {
            throw new ImageFormatException("A BMP color mask is not a contiguous run of bits.");
        }
    }

    internal static long Stride(int width, int bpp) => (((long)width * bpp) + 31) / 32 * 4;

    /// <summary>Decodes the region of the bitmap into the sink, top to bottom.</summary>
    internal static void DecodeRows(Stream s, BmpState st, ImageRect region, IRowSink sink, CancellationToken ct)
    {
        if (region.X < 0 || region.Y < 0 || region.X + region.Width > st.Width || region.Y + region.Height > st.Height
            || region.Width != sink.Width || region.Height != sink.Height)
        {
            throw new ArgumentException("The requested region does not match the sink or the image.", nameof(region));
        }

        var conv = new RowConverter(st);
        int outBpp = st.Format.BytesPerPixel;
        int outStride = region.Width * outBpp;
        byte[]? rowBuf = ArrayPool<byte>.Shared.Rent(st.Width * outBpp);
        byte[]? outBuf = ArrayPool<byte>.Shared.Rent(outStride);
        try
        {
            if (st.Compression is 1 or 2)
            {
                byte[] indices = DecodeRle(s, st, ct);
                for (int y = 0; y < region.Height; y++)
                {
                    ct.ThrowIfCancellationRequested();
                    int srcY = region.Y + y;
                    int fileRow = st.TopDown ? srcY : st.Height - 1 - srcY;
                    conv.Indices(indices.AsSpan(fileRow * st.Width, st.Width), rowBuf);
                    rowBuf.AsSpan(region.X * outBpp, outStride).CopyTo(outBuf);
                    sink.WriteRows(y, outBuf.AsSpan(0, outStride), 1);
                }

                return;
            }

            long stride = Stride(st.Width, st.Bpp);
            long wanted = st.DataOffset + (stride * st.Height);
            if (wanted > s.Length)
            {
                throw new ImageFormatException("The BMP pixel data is truncated.");
            }

            int batch = (int)Math.Max(1, Math.Min(region.Height, (1 << 20) / stride));
            byte[]? block = ArrayPool<byte>.Shared.Rent((int)(batch * stride));
            try
            {
                for (int y0 = 0; y0 < region.Height; y0 += batch)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(batch, region.Height - y0);
                    int firstSrc = region.Y + y0;
                    // The n output rows are contiguous in the file; bottom-up files hold them in reverse order.
                    int firstFileRow = st.TopDown ? firstSrc : st.Height - firstSrc - n;
                    s.Position = st.DataOffset + (firstFileRow * stride);
                    ContainerIo.ReadFully(s, block.AsSpan(0, (int)(n * stride)));
                    for (int i = 0; i < n; i++)
                    {
                        int fileIndex = st.TopDown ? i : n - 1 - i;
                        conv.Pixels(block.AsSpan((int)(fileIndex * stride), (int)stride), rowBuf);
                        rowBuf.AsSpan(region.X * outBpp, outStride).CopyTo(outBuf);
                        sink.WriteRows(y0 + i, outBuf.AsSpan(0, outStride), 1);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(block);
            }
        }
        finally
        {
            ContainerIo.Return(ref rowBuf);
            ContainerIo.Return(ref outBuf);
        }
    }

    // Returns palette indices for the whole bitmap in file row order (row 0 is the first row stored).
    private static byte[] DecodeRle(Stream s, BmpState st, CancellationToken ct)
    {
        int w = st.Width;
        int h = st.Height;
        byte[] idx = new byte[(long)w * h];
        s.Position = st.DataOffset;
        byte[] buf = ArrayPool<byte>.Shared.Rent(1 << 16);
        int pos = 0;
        int len = 0;
        bool four = st.Compression == 2;
        int x = 0;
        int y = 0;
        int Next()
        {
            if (pos == len)
            {
                len = s.Read(buf, 0, buf.Length);
                pos = 0;
                if (len <= 0)
                {
                    throw new ImageFormatException("The RLE data ends before the bitmap is complete.");
                }
            }

            return buf[pos++];
        }

        try
        {
            while (y < h)
            {
                if ((x & 0x3FF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                int n = Next();
                int c = Next();
                if (n > 0)
                {
                    // Encoded run: n pixels of c (RLE8) or alternating nibbles of c (RLE4).
                    for (int i = 0; i < n && x < w; i++, x++)
                    {
                        idx[(y * w) + x] = four ? (byte)((i & 1) == 0 ? c >> 4 : c & 15) : (byte)c;
                    }

                    if (x > w)
                    {
                        x = w;
                    }

                    continue;
                }

                switch (c)
                {
                    case 0:
                        x = 0;
                        y++;
                        break;
                    case 1:
                        y = h;
                        break;
                    case 2:
                        {
                            int dx = Next();
                            int dy = Next();
                            x += dx;
                            y += dy;
                            if (x > w || y > h)
                            {
                                throw new ImageFormatException("An RLE delta moves outside the bitmap.");
                            }

                            break;
                        }

                    default:
                        {
                            // Absolute run of c pixels, padded to a 16-bit boundary.
                            int bytes = four ? (c + 1) / 2 : c;
                            int written = 0;
                            for (int b = 0; b < bytes; b++)
                            {
                                int v = Next();
                                if (four)
                                {
                                    for (int k = 0; k < 2 && written < c; k++, written++)
                                    {
                                        if (x < w)
                                        {
                                            idx[(y * w) + x++] = (byte)(k == 0 ? v >> 4 : v & 15);
                                        }
                                    }
                                }
                                else if (x < w)
                                {
                                    idx[(y * w) + x++] = (byte)v;
                                }
                            }

                            if ((bytes & 1) != 0)
                            {
                                Next();
                            }

                            break;
                        }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return idx;
    }

    /// <summary>Turns stored rows into RGB8 or RGBA8 rows.</summary>
    private sealed class RowConverter
    {
        private readonly BmpState _st;
        private readonly byte[]?[] _lut = new byte[4][];
        private readonly int[] _shift = new int[4];
        private readonly int[] _bits = new int[4];

        public RowConverter(BmpState st)
        {
            _st = st;
            uint[] masks = [st.MaskR, st.MaskG, st.MaskB, st.MaskA];
            for (int c = 0; c < 4; c++)
            {
                if (masks[c] == 0)
                {
                    continue;
                }

                _shift[c] = BitOperations.TrailingZeroCount(masks[c]);
                _bits[c] = BitOperations.PopCount(masks[c]);
                if (_bits[c] <= 16 && _bits[c] != 8)
                {
                    int max = (1 << _bits[c]) - 1;
                    byte[] lut = new byte[max + 1];
                    for (int v = 0; v <= max; v++)
                    {
                        lut[v] = (byte)(((v * 255L) + (max / 2)) / max);
                    }

                    _lut[c] = lut;
                }
            }
        }

        private int Scale(int c, uint v)
        {
            if (_bits[c] == 8)
            {
                return (int)v;
            }

            if (_lut[c] is { } lut)
            {
                return lut[v];
            }

            long max = (1L << _bits[c]) - 1;
            return (int)(((v * 255L) + (max / 2)) / max);
        }

        public void Indices(ReadOnlySpan<byte> idx, Span<byte> dst)
        {
            byte[] pal = _st.Palette;
            for (int x = 0; x < idx.Length; x++)
            {
                pal.AsSpan(idx[x] * 4, 3).CopyTo(dst.Slice(x * 3, 3));
            }
        }

        public void Pixels(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int w = _st.Width;
            byte[] pal = _st.Palette;
            switch (_st.Bpp)
            {
                case 1:
                case 4:
                case 8:
                    {
                        int bits = _st.Bpp;
                        int mask = (1 << bits) - 1;
                        for (int x = 0; x < w; x++)
                        {
                            int bitPos = x * bits;
                            int i = (src[bitPos >> 3] >> (8 - bits - (bitPos & 7))) & mask;
                            pal.AsSpan(i * 4, 3).CopyTo(dst.Slice(x * 3, 3));
                        }

                        break;
                    }

                case 24:
                    for (int x = 0; x < w; x++)
                    {
                        ReadOnlySpan<byte> p = src.Slice(x * 3, 3);
                        Span<byte> d = dst.Slice(x * 3, 3);
                        d[0] = p[2];
                        d[1] = p[1];
                        d[2] = p[0];
                    }

                    break;
                default:
                    {
                        bool wide = _st.Bpp == 32;
                        bool alpha = _st.MaskA != 0;
                        int outBpp = alpha ? 4 : 3;
                        for (int x = 0; x < w; x++)
                        {
                            uint v = wide ? BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(x * 4, 4)) : BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(x * 2, 2));
                            Span<byte> d = dst.Slice(x * outBpp, outBpp);
                            d[0] = (byte)Scale(0, (v & _st.MaskR) >> _shift[0]);
                            d[1] = (byte)Scale(1, (v & _st.MaskG) >> _shift[1]);
                            d[2] = (byte)Scale(2, (v & _st.MaskB) >> _shift[2]);
                            if (alpha)
                            {
                                d[3] = (byte)Scale(3, (v & _st.MaskA) >> _shift[3]);
                            }
                        }

                        break;
                    }
            }
        }
    }
}

/// <summary>An <see cref="IRowSink"/> that keeps all rows in one array.</summary>
internal sealed class BufferSink : IRowSink
{
    private readonly int _rowBytes;
    private int _next;

    public BufferSink(int width, int height, PixelFormat format)
    {
        Width = width;
        Height = height;
        Format = format;
        _rowBytes = width * format.BytesPerPixel;
        Data = new byte[(long)_rowBytes * height];
    }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public byte[] Data { get; }

    public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
    {
        if (y != _next || y + rowCount > Height)
        {
            throw new ImageOperationException("Rows arrived out of order.");
        }

        rows[..(rowCount * _rowBytes)].CopyTo(Data.AsSpan(y * _rowBytes));
        _next += rowCount;
    }
}
