using System.Buffers;
using System.Buffers.Binary;
using OneImage.Compression;

namespace OneImage.Codecs.Gif;

/// <summary>One image descriptor found by the header scan.</summary>
internal sealed class GifFrameRecord
{
    public int X, Y, Width, Height;
    public bool Interlaced;
    public uint[]? LocalPalette;
    public long DataOffset;
    public int MinCodeSize;
    public int Disposal;
    public int TransparentIndex = -1;
    public int DelayCentiseconds;
}

/// <summary>What the header scan learned, plus the running canvas used to composite frames in order.</summary>
internal sealed class GifState
{
    public int ScreenWidth, ScreenHeight;
    public uint[]? GlobalPalette;
    public GifFrameRecord[] Frames = [];
    public readonly object Gate = new();

    // The composited canvas as it looks right after frame LastDrawn was drawn. That frame's disposal is applied lazily, when the
    // next frame is asked for, so asking for the same frame again (tile after tile) costs nothing.
    public byte[]? Canvas;
    public int LastDrawn = -1;
    public byte[]? Saved; // pixels under the last frame's rectangle, kept for disposal "restore to previous"

    // The canvas is rented from the shared pool so repeated loads reuse memory; images have no Dispose, so the finalizer gives it back.
    ~GifState() => ReleaseCanvas();

    public void ReleaseCanvas()
    {
        byte[]? c = Canvas;
        Canvas = null;
        LastDrawn = -1;
        Saved = null;
        if (c != null)
        {
            ArrayPool<byte>.Shared.Return(c);
        }
    }
}

/// <summary>
/// GIF decoder (GIF89a, and GIF87a). Every frame is delivered as a full-screen straight-alpha RGBA8 image with earlier frames
/// composited under it according to each frame's disposal method.
/// </summary>
/// <remarks>
/// Policies (they follow what browsers do):
/// the canvas starts fully transparent and "restore to background" clears to transparent, ignoring the background color index;
/// frame rectangles are clipped to the logical screen; a logical screen of zero width or height is replaced by the bounding box of
/// the frames; disposal "restore to previous" on the first frame restores transparency; data that ends before all pixels
/// are decoded (missing end code, truncated file) leaves the remaining pixels of that frame untouched, so they show what was under
/// them; a file cut inside a frame's data keeps that frame; invalid LZW codes raise <see cref="ImageFormatException"/>.
/// </remarks>
internal sealed class GifDecoder : IImageDecoder
{
    public static GifDecoder Instance { get; } = new();

    // Frames are composited from the start; the decoder keeps the canvas of the last frame it produced, so reading frames in order and
    // reading one frame tile by tile are both cheap. Going back to an earlier frame replays from the first.
    public DecoderCapabilities Capabilities => DecoderCapabilities.RandomAccess;

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        using var r = new GifByteReader(source);
        Span<byte> head = stackalloc byte[13];
        if (r.Read(head) < 13 || !(head[..5].SequenceEqual("GIF87"u8) || head[..5].SequenceEqual("GIF89"u8)) || head[5] != (byte)'a')
        {
            throw new ImageFormatException("Not a GIF file or the header is truncated.");
        }

        int sw = BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
        int sh = BinaryPrimitives.ReadUInt16LittleEndian(head[8..]);
        int packed = head[10];
        if (sw > 0 && sh > 0)
        {
            limits.CheckFrame(sw, sh);
        }

        var state = new GifState { ScreenWidth = sw, ScreenHeight = sh };
        if ((packed & 0x80) != 0)
        {
            state.GlobalPalette = ReadPalette(r, 2 << (packed & 7)) ?? throw new ImageFormatException("The global color table is truncated.");
        }

        var frames = new List<GifFrameRecord>();
        int loop = 1;
        ReadOnlyMemory<byte> xmp = default;
        int gDisposal = 0;
        int gTransparent = -1;
        int gDelay = 0;
        Span<byte> buf = stackalloc byte[16];
        while (true)
        {
            int b = r.ReadByte();
            if (b < 0 || b == 0x3B)
            {
                break;
            }

            if (b == 0x21)
            {
                int label = r.ReadByte();
                if (label < 0)
                {
                    break;
                }

                if (label == 0xF9)
                {
                    int len = r.ReadByte();
                    if (len < 0)
                    {
                        break;
                    }

                    if (len >= 4)
                    {
                        if (r.Read(buf[..4]) < 4)
                        {
                            break;
                        }

                        gDisposal = (buf[0] >> 2) & 7;
                        gDelay = BinaryPrimitives.ReadUInt16LittleEndian(buf[1..]);
                        gTransparent = (buf[0] & 1) != 0 ? buf[3] : -1;
                        if (!r.Skip(len - 4))
                        {
                            break;
                        }
                    }
                    else if (!r.Skip(len))
                    {
                        break;
                    }

                    if (!r.SkipSubBlocks())
                    {
                        break;
                    }
                }
                else if (label == 0xFF)
                {
                    if (!ReadApplicationExtension(r, limits, ref loop, ref xmp))
                    {
                        break;
                    }
                }
                else if (!r.SkipSubBlocks())
                {
                    break;
                }

                continue;
            }

            if (b != 0x2C)
            {
                if (b == 0)
                {
                    continue; // stray padding between blocks
                }

                break; // unknown block: stop and keep what was read
            }

            Span<byte> d = buf[..9];
            if (r.Read(d) < 9)
            {
                break;
            }

            int ipacked = d[8];
            var f = new GifFrameRecord
            {
                X = BinaryPrimitives.ReadUInt16LittleEndian(d),
                Y = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]),
                Width = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]),
                Height = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]),
                Interlaced = (ipacked & 0x40) != 0,
                Disposal = gDisposal,
                TransparentIndex = gTransparent,
                DelayCentiseconds = gDelay,
            };
            gDisposal = 0;
            gTransparent = -1;
            gDelay = 0;
            if ((ipacked & 0x80) != 0)
            {
                f.LocalPalette = ReadPalette(r, 2 << (ipacked & 7));
                if (f.LocalPalette == null)
                {
                    break;
                }
            }

            int min = r.ReadByte();
            if (min < 0)
            {
                break;
            }

            f.MinCodeSize = min;
            f.DataOffset = r.Position;
            if (frames.Count >= limits.MaxFrames)
            {
                throw new ImageLimitException($"Frame count exceeds the limit of {limits.MaxFrames}.");
            }

            frames.Add(f);
            if (!r.SkipSubBlocks())
            {
                break;
            }
        }

        if (frames.Count == 0)
        {
            throw new ImageFormatException("The GIF contains no image data.");
        }

        if (sw == 0 || sh == 0)
        {
            foreach (GifFrameRecord f in frames)
            {
                sw = Math.Max(sw, f.X + f.Width);
                sh = Math.Max(sh, f.Y + f.Height);
            }

            limits.CheckFrame(sw, sh);
            state.ScreenWidth = sw;
            state.ScreenHeight = sh;
        }

        state.Frames = frames.ToArray();
        var infos = new FrameInfo[frames.Count];
        for (int i = 0; i < infos.Length; i++)
        {
            infos[i] = new FrameInfo(sw, sh, PixelFormat.Rgba8, TimeSpan.FromMilliseconds(frames[i].DelayCentiseconds * 10.0));
        }

        var metadata = xmp.Length > 0 ? new ImageMetadata { Xmp = xmp } : ImageMetadata.Empty;
        return new ImageInfo(GifFormat.Instance, infos, loop, metadata, state);
    }

    /// <summary>
    /// Reads an application extension (the label byte was consumed). Returns false when the data ended.
    /// Loop count comes from NETSCAPE2.0 / ANIMEXTS1.0; an XMP packet is returned without its 258-byte sub-block ramp.
    /// </summary>
    private static bool ReadApplicationExtension(GifByteReader r, ImageLimits limits, ref int loop, ref ReadOnlyMemory<byte> xmp)
    {
        int len = r.ReadByte();
        if (len < 0)
        {
            return false;
        }

        Span<byte> id = stackalloc byte[11];
        if (len != 11)
        {
            return r.Skip(len) && r.SkipSubBlocks();
        }

        if (r.Read(id) < 11)
        {
            return false;
        }

        if (id.SequenceEqual("NETSCAPE2.0"u8) || id.SequenceEqual("ANIMEXTS1.0"u8))
        {
            int blen = r.ReadByte();
            if (blen < 0)
            {
                return false;
            }

            if (blen >= 3)
            {
                Span<byte> d = stackalloc byte[3];
                if (r.Read(d) < 3)
                {
                    return false;
                }

                if (d[0] == 1)
                {
                    int v = BinaryPrimitives.ReadUInt16LittleEndian(d[1..]);
                    loop = v == 0 ? 0 : v + 1; // the stored value counts repeats after the first play
                }

                if (!r.Skip(blen - 3))
                {
                    return false;
                }
            }
            else if (!r.Skip(blen))
            {
                return false;
            }

            return r.SkipSubBlocks();
        }

        if (id.SequenceEqual("XMP DataXMP"u8))
        {
            // The packet follows the 11-byte header verbatim, then a 258-byte ramp makes it parse as sub-blocks.
            long start = r.Position;
            if (!r.SkipSubBlocks())
            {
                return false;
            }

            long total = r.Position - start;
            long packet = total - 258;
            if (packet > 0 && xmp.Length == 0)
            {
                if (packet > limits.MaxMetadataBytes)
                {
                    throw new ImageLimitException($"Metadata of {packet} bytes exceeds the limit of {limits.MaxMetadataBytes}.");
                }

                long resume = r.Position;
                r.SeekTo(start);
                var data = new byte[packet];
                int got = r.Read(data);
                r.SeekTo(resume);
                if (got == packet)
                {
                    xmp = data;
                }
            }

            return true;
        }

        return r.SkipSubBlocks();
    }

    private static uint[]? ReadPalette(GifByteReader r, int count)
    {
        byte[] raw = ArrayPool<byte>.Shared.Rent(count * 3);
        try
        {
            if (r.Read(raw.AsSpan(0, count * 3)) < count * 3)
            {
                return null;
            }

            // Entries beyond the table (index out of range) show as opaque black.
            var pal = new uint[256];
            Array.Fill(pal, 0xFF000000u);
            for (int i = 0; i < count; i++)
            {
                pal[i] = 0xFF000000u | raw[i * 3] | ((uint)raw[(i * 3) + 1] << 8) | ((uint)raw[(i * 3) + 2] << 16);
            }

            return pal;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(limits);
        var state = (GifState?)info.DecoderState ?? throw new ImageOperationException("The image info was not produced by the GIF decoder.");
        if ((uint)frameIndex >= (uint)state.Frames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        int w = state.ScreenWidth;
        int h = state.ScreenHeight;
        ImageRect region = request.Region;
        if (request.ScaleDenominator != 1 || !region.IsInside(w, h) || sink.Width != region.Width || sink.Height != region.Height)
        {
            throw new ImageOperationException("The requested region does not match the GIF frame.");
        }

        lock (state.Gate)
        {
            Advance(source, state, frameIndex, limits, cancellationToken);
            byte[] canvas = state.Canvas!;
            int rowBytes = w * 4;
            if (region.Width == w)
            {
                for (int y = 0; y < region.Height;)
                {
                    int n = Math.Min(64, region.Height - y);
                    sink.WriteRows(y, canvas.AsSpan((region.Y + y) * rowBytes, n * rowBytes), n);
                    y += n;
                }

                return;
            }

            int outRow = region.Width * 4;
            byte[] tmp = ArrayPool<byte>.Shared.Rent(outRow * Math.Min(region.Height, 64));
            try
            {
                for (int y = 0; y < region.Height;)
                {
                    int n = Math.Min(64, region.Height - y);
                    for (int r = 0; r < n; r++)
                    {
                        canvas.AsSpan((((region.Y + y + r) * w) + region.X) * 4, outRow).CopyTo(tmp.AsSpan(r * outRow));
                    }

                    sink.WriteRows(y, tmp.AsSpan(0, n * outRow), n);
                    y += n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }
    }

    /// <summary>Brings the state canvas to "frame <paramref name="target"/> just drawn". Replays from the start when asked to go backwards.</summary>
    private static void Advance(Stream source, GifState state, int target, ImageLimits limits, CancellationToken ct)
    {
        if (state.Canvas != null && state.LastDrawn == target)
        {
            return;
        }

        if (state.Canvas == null || state.LastDrawn > target)
        {
            long bytes = (long)state.ScreenWidth * state.ScreenHeight * 4;
            if (bytes > Array.MaxLength)
            {
                throw new ImageLimitException("The logical screen is too large to composite.");
            }

            state.Canvas ??= ArrayPool<byte>.Shared.Rent((int)bytes);
            state.Canvas.AsSpan(0, (int)bytes).Clear();

            state.LastDrawn = -1;
            state.Saved = null;
        }

        bool ok = false;
        try
        {
            using var reader = new GifByteReader(source);
            byte[] canvas = state.Canvas;
            for (int i = state.LastDrawn + 1; i <= target; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (state.LastDrawn >= 0)
                {
                    GifFrameRecord prev = state.Frames[state.LastDrawn];
                    if (prev.Disposal == 2)
                    {
                        ClearRect(canvas, state, prev);
                    }
                    else if (state.Saved != null)
                    {
                        RestoreRect(canvas, state, prev, state.Saved);
                    }
                }

                GifFrameRecord f = state.Frames[i];
                state.Saved = f.Disposal == 3 ? SaveRect(canvas, state, f) : null;
                DrawFrame(reader, state, f, canvas, limits, ct);
                state.LastDrawn = i;
            }

            ok = true;
        }
        finally
        {
            if (!ok)
            {
                state.ReleaseCanvas();
            }
        }
    }

    private static (int X0, int X1, int Y0, int Y1) Clip(GifState s, GifFrameRecord f) =>
        (f.X, Math.Min(f.X + f.Width, s.ScreenWidth), f.Y, Math.Min(f.Y + f.Height, s.ScreenHeight));

    private static byte[]? SaveRect(byte[] canvas, GifState s, GifFrameRecord f)
    {
        (int x0, int x1, int y0, int y1) = Clip(s, f);
        if (x1 <= x0 || y1 <= y0)
        {
            return null;
        }

        var saved = new byte[(x1 - x0) * (y1 - y0) * 4];
        for (int y = y0; y < y1; y++)
        {
            canvas.AsSpan(((y * s.ScreenWidth) + x0) * 4, (x1 - x0) * 4).CopyTo(saved.AsSpan((y - y0) * (x1 - x0) * 4));
        }

        return saved;
    }

    private static void RestoreRect(byte[] canvas, GifState s, GifFrameRecord f, byte[] saved)
    {
        (int x0, int x1, int y0, int y1) = Clip(s, f);
        for (int y = y0; y < y1; y++)
        {
            saved.AsSpan((y - y0) * (x1 - x0) * 4, (x1 - x0) * 4).CopyTo(canvas.AsSpan(((y * s.ScreenWidth) + x0) * 4));
        }
    }

    private static void ClearRect(byte[] canvas, GifState s, GifFrameRecord f)
    {
        (int x0, int x1, int y0, int y1) = Clip(s, f);
        if (x1 <= x0)
        {
            return;
        }

        for (int y = y0; y < y1; y++)
        {
            canvas.AsSpan(((y * s.ScreenWidth) + x0) * 4, (x1 - x0) * 4).Clear();
        }
    }

    private static void DrawFrame(GifByteReader reader, GifState s, GifFrameRecord f, byte[] canvas, ImageLimits limits, CancellationToken ct)
    {
        if (f.Width == 0 || f.Height == 0)
        {
            return;
        }

        uint[] pal = f.LocalPalette ?? s.GlobalPalette ?? throw new ImageFormatException("A frame has no color table.");
        if (f.MinCodeSize is < 2 or > 8)
        {
            throw new ImageFormatException($"Invalid LZW minimum code size {f.MinCodeSize}.");
        }

        reader.SeekTo(f.DataOffset);
        var lzw = new LzwDecoder(LzwVariant.Gif, f.MinCodeSize, long.MaxValue, limits.MaxExpansionRatio);
        byte[] rowBuf = ArrayPool<byte>.Shared.Rent(f.Width);
        byte[] inBuf = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int inPos = 0;
            int inLen = 0;
            bool eof = false;
            int have = 0;
            int rows = 0;
            int screenW = s.ScreenWidth;
            int xEnd = Math.Min(f.X + f.Width, screenW);
            int tIdx = f.TransparentIndex;
            while (rows < f.Height)
            {
                InflateStatus st = lzw.Decode(inBuf.AsSpan(inPos, inLen - inPos), rowBuf.AsSpan(have, f.Width - have), out int used, out int wrote);
                inPos += used;
                have += wrote;
                if (have == f.Width)
                {
                    int y = f.Y + (f.Interlaced ? InterlacedRow(rows, f.Height) : rows);
                    if (y < s.ScreenHeight)
                    {
                        Span<byte> line = canvas.AsSpan(y * screenW * 4, screenW * 4);
                        for (int x = f.X; x < xEnd; x++)
                        {
                            int idx = rowBuf[x - f.X];
                            if (idx != tIdx)
                            {
                                BinaryPrimitives.WriteUInt32LittleEndian(line.Slice(x * 4, 4), pal[idx]);
                            }
                        }
                    }

                    rows++;
                    have = 0;
                    if ((rows & 31) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    continue;
                }

                if (st == InflateStatus.Done)
                {
                    break; // end code before the frame was full: leave the rest untouched
                }

                if (st == InflateStatus.NeedInput || inPos >= inLen)
                {
                    if (eof || !Refill(reader, inBuf, ref inPos, ref inLen, ref eof))
                    {
                        break; // data ended: keep what was decoded
                    }
                }
                else if (used == 0 && wrote == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuf);
            ArrayPool<byte>.Shared.Return(inBuf);
        }
    }

    /// <summary>Reads sub-blocks into the buffer; returns false when no more data is available.</summary>
    private static bool Refill(GifByteReader reader, byte[] buf, ref int pos, ref int len, ref bool eof)
    {
        pos = 0;
        len = 0;
        while (len + 255 <= buf.Length)
        {
            int n = reader.ReadByte();
            if (n <= 0)
            {
                eof = true;
                break;
            }

            int got = reader.Read(buf.AsSpan(len, n));
            len += got;
            if (got < n)
            {
                eof = true;
                break;
            }
        }

        return len > 0;
    }

    /// <summary>Maps the n-th stored row of an interlaced image (four passes: every 8th from 0, every 8th from 4, every 4th from 2, every 2nd from 1) to its picture row.</summary>
    internal static int InterlacedRow(int n, int height)
    {
        int c0 = (height + 7) >> 3;
        if (n < c0)
        {
            return n * 8;
        }

        n -= c0;
        int c1 = (height + 3) >> 3;
        if (n < c1)
        {
            return (n * 8) + 4;
        }

        n -= c1;
        int c2 = (height + 1) >> 2;
        if (n < c2)
        {
            return (n * 4) + 2;
        }

        n -= c2;
        return (n * 2) + 1;
    }
}
