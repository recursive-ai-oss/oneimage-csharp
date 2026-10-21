using System.Buffers.Binary;

namespace OneImage.Codecs.WebP;

/// <summary>One image of a WebP file: a still image or one animation frame, located by stream offsets.</summary>
internal sealed class WebPFrameRecord
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public int DurationMs;

    /// <summary>Gets or sets whether the frame is alpha-blended onto the canvas (false: it overwrites its rectangle).</summary>
    public bool Blend = true;

    public bool DisposeToBackground;
    public bool Lossless;
    public bool HasAlpha;
    public long DataOffset;
    public int DataLength;
    public long AlphaOffset;
    public int AlphaLength;
}

/// <summary>The parsed structure of a WebP file (RFC 9649 section 2).</summary>
internal sealed class WebPFileInfo
{
    public int CanvasWidth;
    public int CanvasHeight;
    public bool Animated;
    public bool HasAlpha;
    public int LoopCount;
    public uint BackgroundColor;
    public List<WebPFrameRecord> Frames = [];
    public ImageMetadata Metadata = ImageMetadata.Empty;
}

/// <summary>Walks the RIFF chunks of a WebP file on a seekable stream without reading pixel data.</summary>
internal static class WebPContainer
{
    private const uint FourCcRiff = 0x46464952;
    private const uint FourCcWebp = 0x50424557;
    private const uint FourCcVp8 = 0x20385056;
    private const uint FourCcVp8L = 0x4c385056;
    private const uint FourCcVp8X = 0x58385056;
    private const uint FourCcAlph = 0x48504c41;
    private const uint FourCcAnim = 0x4d494e41;
    private const uint FourCcAnmf = 0x464d4e41;
    private const uint FourCcIccp = 0x50434349;
    private const uint FourCcExif = 0x46495845;
    private const uint FourCcXmp = 0x20504d58;

    public static bool Matches(ReadOnlySpan<byte> h) =>
        h.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(h) == FourCcRiff && BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) == FourCcWebp;

    public static WebPFileInfo Parse(Stream s, ImageLimits limits)
    {
        long length = s.Length;
        Span<byte> hdr = stackalloc byte[12];
        s.Position = 0;
        ReadExactly(s, hdr);
        if (!Matches(hdr))
        {
            throw new ImageFormatException("Not a WebP file.");
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);
        if (riffSize < 12 - 8 + 8)
        {
            throw new ImageFormatException("Invalid RIFF size.");
        }

        long end = riffSize + 8L;
        if (end > length)
        {
            throw new ImageFormatException("The file is truncated.");
        }

        var info = new WebPFileInfo();
        byte[]? icc = null;
        byte[]? exif = null;
        byte[]? xmp = null;
        long totalMeta = 0;
        long pos = 12;
        bool sawVp8X = false;
        bool still = false;
        long alphaOffset = 0;
        int alphaLength = 0;
        long frameCountGuard = 0;
        Span<byte> scratch = stackalloc byte[16];

        while (pos + 8 <= end)
        {
            ReadChunkHeader(s, pos, out uint fourCc, out uint size);
            long payload = pos + 8;
            if (payload + size > end)
            {
                throw new ImageFormatException("A chunk extends past the end of the file.");
            }

            long next = payload + size + (size & 1);
            switch (fourCc)
            {
                case FourCcVp8X when !sawVp8X && pos == 12:
                    {
                        if (size < 10)
                        {
                            throw new ImageFormatException("Invalid VP8X chunk.");
                        }

                        sawVp8X = true;
                        Span<byte> b = scratch[..10];
                        s.Position = payload;
                        ReadExactly(s, b);
                        info.Animated = (b[0] & 0x02) != 0;
                        info.HasAlpha = (b[0] & 0x10) != 0;
                        info.CanvasWidth = 1 + (b[4] | (b[5] << 8) | (b[6] << 16));
                        info.CanvasHeight = 1 + (b[7] | (b[8] << 8) | (b[9] << 16));
                        limits.CheckFrame(info.CanvasWidth, info.CanvasHeight);
                        break;
                    }

                case FourCcIccp:
                    icc ??= ReadMeta(s, payload, size, limits, ref totalMeta);
                    break;
                case FourCcExif:
                    exif ??= ReadMeta(s, payload, size, limits, ref totalMeta);
                    break;
                case FourCcXmp:
                    xmp ??= ReadMeta(s, payload, size, limits, ref totalMeta);
                    break;
                case FourCcAnim:
                    if (size < 6)
                    {
                        throw new ImageFormatException("Invalid ANIM chunk.");
                    }

                    {
                        Span<byte> b = scratch[..6];
                        s.Position = payload;
                        ReadExactly(s, b);
                        info.BackgroundColor = BinaryPrimitives.ReadUInt32LittleEndian(b);
                        info.LoopCount = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
                    }

                    break;
                case FourCcAnmf:
                    {
                        if (!sawVp8X || !info.Animated)
                        {
                            break;
                        }

                        if (size < 16)
                        {
                            throw new ImageFormatException("Invalid ANMF chunk.");
                        }

                        if (++frameCountGuard > limits.MaxFrames)
                        {
                            throw new ImageLimitException($"Frame count exceeds the limit of {limits.MaxFrames}.");
                        }

                        Span<byte> b = scratch[..16];
                        s.Position = payload;
                        ReadExactly(s, b);
                        var f = new WebPFrameRecord
                        {
                            X = 2 * (b[0] | (b[1] << 8) | (b[2] << 16)),
                            Y = 2 * (b[3] | (b[4] << 8) | (b[5] << 16)),
                            Width = 1 + (b[6] | (b[7] << 8) | (b[8] << 16)),
                            Height = 1 + (b[9] | (b[10] << 8) | (b[11] << 16)),
                            DurationMs = b[12] | (b[13] << 8) | (b[14] << 16),
                            DisposeToBackground = (b[15] & 1) != 0,
                            Blend = (b[15] & 2) == 0,
                        };
                        if (f.X + (long)f.Width > info.CanvasWidth || f.Y + (long)f.Height > info.CanvasHeight)
                        {
                            throw new ImageFormatException("An animation frame lies outside the canvas.");
                        }

                        ParseFrameChunks(s, payload + 16, payload + size, f);
                        info.Frames.Add(f);
                        break;
                    }

                case FourCcAlph:
                    if (alphaLength == 0 && !info.Animated)
                    {
                        alphaOffset = payload;
                        alphaLength = (int)size;
                    }

                    break;
                case FourCcVp8 or FourCcVp8L:
                    if (info.Animated && sawVp8X)
                    {
                        break;
                    }

                    if (still)
                    {
                        break;
                    }

                    still = true;
                    {
                        var f = new WebPFrameRecord { Lossless = fourCc == FourCcVp8L, DataOffset = payload, DataLength = (int)size };
                        if (!f.Lossless && alphaLength > 0)
                        {
                            f.AlphaOffset = alphaOffset;
                            f.AlphaLength = alphaLength;
                        }

                        ReadBitstreamHeader(s, f);
                        info.Frames.Add(f);
                    }

                    break;
                default:
                    break;
            }

            pos = next;
        }

        if (info.Frames.Count == 0)
        {
            throw new ImageFormatException("The WebP file contains no image data.");
        }

        if (info.Animated && sawVp8X)
        {
            foreach (WebPFrameRecord f in info.Frames)
            {
                info.HasAlpha |= f.HasAlpha;
            }
        }
        else
        {
            WebPFrameRecord f = info.Frames[0];
            if (sawVp8X && (f.Width != info.CanvasWidth || f.Height != info.CanvasHeight))
            {
                throw new ImageFormatException("The image size does not match the canvas size.");
            }

            info.CanvasWidth = f.Width;
            info.CanvasHeight = f.Height;
            info.HasAlpha |= f.HasAlpha;
            info.Animated = false;
        }

        limits.CheckFrame(info.CanvasWidth, info.CanvasHeight);
        long frames = info.Frames.Count;
        if (frames > limits.MaxFrames)
        {
            throw new ImageLimitException($"Frame count {frames} exceeds the limit of {limits.MaxFrames}.");
        }

        long totalPixels = 0;
        foreach (WebPFrameRecord f in info.Frames)
        {
            limits.CheckFrame(f.Width, f.Height);
            totalPixels += (long)(info.Animated ? info.CanvasWidth : f.Width) * (info.Animated ? info.CanvasHeight : f.Height);
            if (totalPixels > limits.MaxTotalPixels)
            {
                throw new ImageLimitException($"Total pixel count exceeds the limit of {limits.MaxTotalPixels}.");
            }
        }

        if (icc != null || exif != null || xmp != null)
        {
            var parsed = new ImageMetadata { IccProfile = icc, Exif = exif, Xmp = xmp };
            int orientation = parsed.TryReadExif(out ExifData? tags) && tags?.Orientation is int o ? o : 1;
            info.Metadata = parsed.WithOrientation(orientation);
        }

        return info;
    }

    private static void ParseFrameChunks(Stream s, long pos, long end, WebPFrameRecord f)
    {
        bool gotImage = false;
        while (pos + 8 <= end)
        {
            ReadChunkHeader(s, pos, out uint fourCc, out uint size);
            long payload = pos + 8;
            if (payload + size > end)
            {
                throw new ImageFormatException("A frame chunk extends past the end of its frame.");
            }

            if (fourCc == FourCcAlph && f.AlphaLength == 0 && !gotImage)
            {
                f.AlphaOffset = payload;
                f.AlphaLength = (int)size;
            }
            else if ((fourCc == FourCcVp8 || fourCc == FourCcVp8L) && !gotImage)
            {
                gotImage = true;
                f.Lossless = fourCc == FourCcVp8L;
                f.DataOffset = payload;
                f.DataLength = (int)size;
                if (f.Lossless)
                {
                    f.AlphaLength = 0;
                }

                int w = f.Width;
                int h = f.Height;
                ReadBitstreamHeader(s, f);
                if (f.Width != w || f.Height != h)
                {
                    throw new ImageFormatException("The frame size does not match its bitstream.");
                }
            }

            pos = payload + size + (size & 1);
        }

        if (!gotImage)
        {
            throw new ImageFormatException("An animation frame has no image data.");
        }
    }

    // Reads the dimensions (and alpha presence) from the start of a VP8 or VP8L bitstream.
    private static void ReadBitstreamHeader(Stream s, WebPFrameRecord f)
    {
        Span<byte> b = stackalloc byte[10];
        s.Position = f.DataOffset;
        int n = Math.Min(10, f.DataLength);
        ReadExactly(s, b[..n]);
        if (f.Lossless)
        {
            if (!VP8LDecoder.TryReadHeader(b[..n], out int w, out int h, out bool alpha))
            {
                throw new ImageFormatException("Invalid VP8L header.");
            }

            f.Width = w;
            f.Height = h;
            f.HasAlpha = alpha;
            return;
        }

        if (n < 10 || (b[0] & 1) != 0 || b[3] != 0x9d || b[4] != 0x01 || b[5] != 0x2a)
        {
            throw new ImageFormatException("Invalid VP8 frame header.");
        }

        f.Width = (b[6] | (b[7] << 8)) & 0x3fff;
        f.Height = (b[8] | (b[9] << 8)) & 0x3fff;
        if (f.Width == 0 || f.Height == 0)
        {
            throw new ImageFormatException("Invalid VP8 frame size.");
        }

        f.HasAlpha = f.AlphaLength > 0;
    }

    private static byte[] ReadMeta(Stream s, long payload, uint size, ImageLimits limits, ref long total)
    {
        total += size;
        if (total > limits.MaxMetadataBytes)
        {
            throw new ImageLimitException($"Metadata exceeds the limit of {limits.MaxMetadataBytes} bytes.");
        }

        byte[] data = new byte[size];
        s.Position = payload;
        ReadExactly(s, data);
        return data;
    }

    private static void ReadChunkHeader(Stream s, long pos, out uint fourCc, out uint size)
    {
        Span<byte> h = stackalloc byte[8];
        s.Position = pos;
        ReadExactly(s, h);
        fourCc = BinaryPrimitives.ReadUInt32LittleEndian(h);
        size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
    }

    internal static void ReadExactly(Stream s, Span<byte> buffer)
    {
        try
        {
            s.ReadExactly(buffer);
        }
        catch (EndOfStreamException e)
        {
            throw new ImageFormatException("The file is truncated.", e);
        }
    }
}
