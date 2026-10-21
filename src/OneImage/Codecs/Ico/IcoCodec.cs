using System.Buffers.Binary;
using OneImage.Kernels;

namespace OneImage;

/// <summary>The ICO format registration (and CUR files, read-only): signature detection plus the decoder and encoder.</summary>
public sealed class IcoFormat : IImageFormat
{
    private IcoFormat()
    {
    }

    /// <summary>Gets the shared instance. Register it with <see cref="ImageConfiguration.Add"/>.</summary>
    public static IcoFormat Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "ico";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".ico", ".cur"];

    /// <inheritdoc />
    public string MimeType => "image/x-icon";

    /// <inheritdoc />
    public int HeaderLength => 10;

    /// <inheritdoc />
    public IImageDecoder? Decoder => IcoDecoder.Instance;

    /// <inheritdoc />
    public IImageEncoder? Encoder => IcoEncoder.Instance;

    /// <inheritdoc />
    public bool Matches(ReadOnlySpan<byte> header) =>
        header.Length >= 10 && header[0] == 0 && header[1] == 0 && header[2] is 1 or 2 && header[3] == 0
        && BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) > 0 && header[9] == 0;
}

/// <summary>ICO writer settings.</summary>
public sealed class IcoEncoderOptions : ImageEncoderOptions
{
    /// <summary>
    /// Gets the size from which an entry is stored as PNG instead of a BMP DIB: entries whose larger side is at least
    /// this many pixels use PNG. The default 256 follows the Windows convention (only 256x256 icons are PNG).
    /// </summary>
    public int PngThreshold { get; init; } = 256;
}

/// <summary>A read-only view of part of a seekable stream; position 0 is the first byte of the part.</summary>
internal sealed class WindowStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _pos;

    public WindowStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _pos;
        set => _pos = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _pos;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        _inner.Position = _start + _pos;
        int n = _inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => _length + offset,
        };
        return _pos;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// ICO/CUR decoder. Every directory entry is a frame, in file order (not sorted by size). Entries may hold a PNG or a
/// BMP DIB; all frames are delivered as RGBA8.
/// </summary>
/// <remarks>
/// For BMP entries below 32 bits per pixel, the 1-bit AND mask makes masked pixels transparent. A 32-bit entry uses
/// its own alpha channel, falling back to the AND mask when every alpha value is zero (older icons). The frame size
/// is the size in the image data, not the directory entry. Cursor hotspots are ignored.
/// </remarks>
internal sealed class IcoDecoder : IImageDecoder
{
    public static IcoDecoder Instance { get; } = new();

    public DecoderCapabilities Capabilities => DecoderCapabilities.Region;

    private sealed class Entry
    {
        public long Offset;
        public long Length;
        public ImageInfo? Png;
        public BmpState? Bmp;
        public int Width;
        public int Height;
    }

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        long length = source.Length;
        source.Position = 0;
        Span<byte> hdr = stackalloc byte[6];
        if (ContainerIo.ReadUpTo(source, hdr) < 6 || hdr[0] != 0 || hdr[1] != 0 || hdr[2] is not (1 or 2) || hdr[3] != 0)
        {
            throw new ImageFormatException("Not an ICO file: bad header.");
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(hdr[4..]);
        if (count == 0)
        {
            throw new ImageFormatException("The icon file has no images.");
        }

        if (count > limits.MaxFrames)
        {
            throw new ImageLimitException($"Icon image count {count} exceeds the limit of {limits.MaxFrames}.");
        }

        if (6 + (16L * count) > length)
        {
            throw new ImageFormatException("The icon directory runs past the end of the file.");
        }

        byte[] dir = new byte[16 * count];
        ContainerIo.ReadFully(source, dir);
        var entries = new Entry[count];
        var frames = new List<FrameInfo>(count);
        Span<byte> sig = stackalloc byte[8];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = dir.AsSpan(i * 16, 16);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (offset >= length || size < 8)
            {
                throw new ImageFormatException("An icon entry points outside the file.");
            }

            // Some writers store a size that runs past the end; use what is there.
            size = Math.Min(size, length - offset);
            var entry = new Entry { Offset = offset, Length = size };
            var window = new WindowStream(source, offset, size);
            ContainerIo.ReadFully(window, sig);
            window.Position = 0;
            if (sig.SequenceEqual(PngChunk.Signature))
            {
                ImageInfo png = PngDecoder.Instance.ReadInfo(window, limits);
                entry.Png = png;
                entry.Width = png.Width;
                entry.Height = png.Height;
            }
            else
            {
                BmpState bmp = BmpDecoder.ParseDib(window, 0, true, 0, limits);
                entry.Bmp = bmp;
                entry.Width = bmp.Width;
                entry.Height = bmp.Height;
            }

            entries[i] = entry;
            frames.Add(new FrameInfo(entry.Width, entry.Height, PixelFormat.Rgba8));
        }

        return new ImageInfo(IcoFormat.Instance, frames, 0, null, entries);
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(sink);
        var entries = (Entry[]?)info.DecoderState ?? throw new ImageOperationException("The image info was not produced by the ICO decoder.");
        if ((uint)frameIndex >= (uint)entries.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        Entry e = entries[frameIndex];
        ImageRect r = request.Region;
        if (r.X < 0 || r.Y < 0 || r.X + r.Width > e.Width || r.Y + r.Height > e.Height || r.Width != sink.Width || r.Height != sink.Height)
        {
            throw new ArgumentException("The requested region does not match the sink or the image.", nameof(request));
        }

        var window = new WindowStream(source, e.Offset, e.Length);
        byte[] rgba;
        if (e.Png != null)
        {
            FrameInfo fi = e.Png.Frames[0];
            var buf = new BufferSink(fi.Width, fi.Height, fi.Format);
            PngDecoder.Instance.DecodeFrame(window, e.Png, 0, new DecodeRequest(new ImageRect(0, 0, fi.Width, fi.Height)), buf, limits, cancellationToken);
            rgba = new byte[(long)fi.Width * fi.Height * 4];
            int srcRow = fi.Width * fi.Format.BytesPerPixel;
            for (int y = 0; y < fi.Height; y++)
            {
                ConvertKernel.ConvertRow(buf.Data.AsSpan(y * srcRow, srcRow), fi.Format, rgba.AsSpan(y * fi.Width * 4, fi.Width * 4), PixelFormat.Rgba8, fi.Width);
            }
        }
        else
        {
            BmpState b = e.Bmp!;
            var buf = new BufferSink(b.Width, b.Height, b.Format);
            BmpDecoder.DecodeRows(window, b, new ImageRect(0, 0, b.Width, b.Height), buf, cancellationToken);
            rgba = ToRgbaWithMask(window, b, buf.Data);
        }

        int stride = e.Width * 4;
        for (int y = 0; y < r.Height; y++)
        {
            sink.WriteRows(y, rgba.AsSpan(((r.Y + y) * stride) + (r.X * 4), r.Width * 4), 1);
        }
    }

    private static byte[] ToRgbaWithMask(Stream s, BmpState b, byte[] pixels)
    {
        int w = b.Width;
        int h = b.Height;
        byte[] rgba = new byte[(long)w * h * 4];
        bool hasAlpha = b.Format == PixelFormat.Rgba8;
        if (hasAlpha)
        {
            pixels.CopyTo(rgba, 0);
            bool any = false;
            for (int i = 3; i < rgba.Length && !any; i += 4)
            {
                any = rgba[i] != 0;
            }

            hasAlpha = any;
        }
        else
        {
            for (int i = 0; i < w * h; i++)
            {
                pixels.AsSpan(i * 3, 3).CopyTo(rgba.AsSpan(i * 4, 3));
                rgba[(i * 4) + 3] = 255;
            }
        }

        if (hasAlpha)
        {
            return rgba;
        }

        // AND mask: 1 bit per pixel, rows padded to 32 bits, stored in the same row order as the pixels.
        long maskStride = BmpDecoder.Stride(w, 1);
        long maskStart = b.DataOffset + (BmpDecoder.Stride(w, b.Bpp) * h);
        if (maskStart + (maskStride * h) > s.Length)
        {
            // No (complete) mask: the pixels are opaque, or keep their own alpha values if they had any.
            if (b.Format != PixelFormat.Rgba8)
            {
                return rgba;
            }

            for (int i = 3; i < rgba.Length; i += 4)
            {
                rgba[i] = 255;
            }

            return rgba;
        }

        byte[] mask = new byte[maskStride * h];
        s.Position = maskStart;
        ContainerIo.ReadFully(s, mask);
        for (int y = 0; y < h; y++)
        {
            int fileRow = b.TopDown ? y : h - 1 - y;
            ReadOnlySpan<byte> m = mask.AsSpan((int)(fileRow * maskStride), (int)maskStride);
            for (int x = 0; x < w; x++)
            {
                bool transparent = (m[x >> 3] & (0x80 >> (x & 7))) != 0;
                rgba[(((y * w) + x) * 4) + 3] = transparent ? (byte)0 : (byte)255;
            }
        }

        return rgba;
    }
}

/// <summary>
/// ICO encoder: one directory entry per frame (up to 256x256 each, frame order kept). Entries whose larger side reaches
/// <see cref="IcoEncoderOptions.PngThreshold"/> are PNG, smaller ones 32-bit BMP DIBs with an AND mask.
/// </summary>
internal sealed class IcoEncoder : IImageEncoder
{
    public static IcoEncoder Instance { get; } = new();

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } = [PixelFormat.Rgba8];

    public bool SupportsMultipleFrames => true;

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        int threshold = (request.Options as IcoEncoderOptions)?.PngThreshold ?? 256;
        int count = request.Frames.Count;
        if (count > ushort.MaxValue)
        {
            throw new ImageOperationException("An ICO file holds at most 65535 images.");
        }

        var blobs = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageFrameReader f = request.Frames[i];
            if (f.Width > 256 || f.Height > 256)
            {
                throw new ImageOperationException($"ICO images are at most 256x256 pixels; frame {i} is {f.Width}x{f.Height}.");
            }

            using var ms = new MemoryStream();
            if (Math.Max(f.Width, f.Height) >= threshold)
            {
                PngEncoder.EncodeStill(ms, f.ReadRows, f.Width, f.Height, PixelFormat.Rgba8, ImageMetadata.Empty, new PngEncoderOptions { ColorMode = PngColorMode.Truecolor }, cancellationToken);
            }
            else
            {
                BmpEncoder.WriteBmp(ms, f.ReadRows, f.Width, f.Height, PixelFormat.Rgba8, ImageMetadata.Empty, includeFileHeader: false, cancellationToken, icon: true);
            }

            blobs[i] = ms.ToArray();
        }

        byte[] head = new byte[6 + (16 * count)];
        head[2] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), (ushort)count);
        long offset = head.Length;
        for (int i = 0; i < count; i++)
        {
            ImageFrameReader f = request.Frames[i];
            Span<byte> e = head.AsSpan(6 + (i * 16), 16);
            e[0] = (byte)(f.Width == 256 ? 0 : f.Width);
            e[1] = (byte)(f.Height == 256 ? 0 : f.Height);
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)blobs[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)offset);
            offset += blobs[i].Length;
        }

        destination.Write(head);
        foreach (byte[] b in blobs)
        {
            destination.Write(b);
        }
    }
}
