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

    // The canvas only covers the box that frames can ever draw in (the union of the frame rectangles inside the screen); everything
    // outside it is transparent for every frame. A small file that declares a huge screen so costs memory in proportion to its frames.
    public int BoxX, BoxY, BoxW, BoxH;
    public uint[]? GlobalPalette;
    public GifFrameRecord[] Frames = [];
    public readonly object Gate = new();

    // The composited canvas as it looks right after frame LastDrawn was drawn. That frame's disposal is applied lazily, when the
    // next frame is asked for, so asking for the same frame again (tile after tile) costs nothing.
    public byte[]? Canvas;
    public int LastDrawn = -1;
    public byte[]? Saved; // pixels under the last frame's rectangle, kept for disposal "restore to previous"

    // Canvases up to PooledCanvasBytes come from, and go back to, one process-wide spare, so repeated loads of small animations reuse the
    // same memory; images have no Dispose, so the finalizer gives the canvas back. The shared array pool is not used here: it trims what it
    // holds after collections, which turned every load into a fresh megabyte. Bigger canvases are plain arrays, so a process decoding many
    // large screens does not keep them all.
    internal const int PooledCanvasBytes = 1 << 20;

    private static byte[]? s_spareCanvas;

    private bool _canvasPooled;

    ~GifState() => ReleaseCanvas();

    /// <summary>Gets a zeroed canvas of <paramref name="bytes"/> bytes (the array may be longer when pooled).</summary>
    public byte[] AcquireCanvas(int bytes)
    {
        if (bytes <= PooledCanvasBytes)
        {
            _canvasPooled = true;
            byte[]? spare = Interlocked.Exchange(ref s_spareCanvas, null);
            if (spare == null || spare.Length < bytes || spare.Length / 2 > bytes)
            {
                spare = new byte[bytes]; // a spare of the wrong size is dropped rather than held for an animation that no longer exists
            }

            spare.AsSpan(0, bytes).Clear();
            return spare;
        }

        _canvasPooled = false;
        return new byte[bytes];
    }

    /// <summary>Works out the canvas box from the frame records and the screen size.</summary>
    public void ComputeBox()
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
        foreach (GifFrameRecord f in Frames)
        {
            int fx1 = Math.Min(f.X + f.Width, ScreenWidth);
            int fy1 = Math.Min(f.Y + f.Height, ScreenHeight);
            if (fx1 <= f.X || fy1 <= f.Y)
            {
                continue;
            }

            x0 = Math.Min(x0, f.X);
            y0 = Math.Min(y0, f.Y);
            x1 = Math.Max(x1, fx1);
            y1 = Math.Max(y1, fy1);
        }

        if (x0 == int.MaxValue)
        {
            (BoxX, BoxY, BoxW, BoxH) = (0, 0, 0, 0);
            return;
        }

        (BoxX, BoxY, BoxW, BoxH) = (x0, y0, x1 - x0, y1 - y0);
    }

    public void ReleaseCanvas()
    {
        byte[]? c = Canvas;
        Canvas = null;
        LastDrawn = -1;
        Saved = null;
        if (c != null && _canvasPooled)
        {
            Interlocked.CompareExchange(ref s_spareCanvas, c, null);
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
internal sealed class GifDecoder : IImageDecoder, ICheapTileDecoder
{
    public static GifDecoder Instance { get; } = new();

    // One spare decoder for the whole process, taken and given back around each frame. Frames are decoded one at a time per image, but a
    // thread-pool worker may decode any of them, so a per-thread decoder would end up on every worker that ever touched a GIF and keep its
    // tables alive with the thread. A second image decoding at the same moment simply makes its own and drops it afterwards.
    private static LzwDecoder? s_lzw;

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
        state.ComputeBox();
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
            int boxRow = state.BoxW * 4;
            if (region.Width == w && state.BoxW == w && state.BoxX == 0)
            {
                // Whole rows, and the canvas has them: rows inside the box come straight from it, others are transparent.
                int above = Math.Clamp(state.BoxY - region.Y, 0, region.Height);
                int inside = Math.Clamp(Math.Min(region.Bottom, state.BoxY + state.BoxH) - Math.Max(region.Y, state.BoxY), 0, region.Height - above);
                WriteBlank(sink, 0, above, w * 4);
                for (int y = above; y < above + inside;)
                {
                    int n = Math.Min(64, above + inside - y);
                    sink.WriteRows(y, canvas.AsSpan((region.Y + y - state.BoxY) * boxRow, n * boxRow), n);
                    y += n;
                }

                WriteBlank(sink, above + inside, region.Height - above - inside, w * 4);
                return;
            }

            int outRow = region.Width * 4;
            byte[] tmp = ArrayPool<byte>.Shared.Rent(outRow * Math.Min(region.Height, 64));
            try
            {
                int cx0 = Math.Max(region.X, state.BoxX);
                int cx1 = Math.Min(region.Right, state.BoxX + state.BoxW);
                for (int y = 0; y < region.Height;)
                {
                    int n = Math.Min(64, region.Height - y);
                    tmp.AsSpan(0, n * outRow).Clear();
                    for (int r = 0; r < n; r++)
                    {
                        int sy = region.Y + y + r - state.BoxY;
                        if (sy >= 0 && sy < state.BoxH && cx1 > cx0)
                        {
                            canvas.AsSpan(((sy * state.BoxW) + (cx0 - state.BoxX)) * 4, (cx1 - cx0) * 4).CopyTo(tmp.AsSpan((r * outRow) + ((cx0 - region.X) * 4)));
                        }
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

    /// <summary>Delivers <paramref name="rows"/> fully transparent rows starting at row <paramref name="y"/>.</summary>
    private static void WriteBlank(IRowSink sink, int y, int rows, int rowBytes)
    {
        if (rows <= 0)
        {
            return;
        }

        int chunk = Math.Min(rows, 64);
        byte[] zeros = ArrayPool<byte>.Shared.Rent(chunk * rowBytes);
        try
        {
            zeros.AsSpan(0, chunk * rowBytes).Clear();
            for (int done = 0; done < rows;)
            {
                int n = Math.Min(chunk, rows - done);
                sink.WriteRows(y + done, zeros.AsSpan(0, n * rowBytes), n);
                done += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
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
            long bytes = (long)state.BoxW * state.BoxH * 4;
            if (bytes > Array.MaxLength)
            {
                throw new ImageLimitException("The frames cover too large an area to composite.");
            }

            if (state.Canvas == null)
            {
                state.Canvas = state.AcquireCanvas((int)bytes);
            }
            else
            {
                state.Canvas.AsSpan(0, (int)bytes).Clear();
            }

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
            canvas.AsSpan((((y - s.BoxY) * s.BoxW) + (x0 - s.BoxX)) * 4, (x1 - x0) * 4).CopyTo(saved.AsSpan((y - y0) * (x1 - x0) * 4));
        }

        return saved;
    }

    private static void RestoreRect(byte[] canvas, GifState s, GifFrameRecord f, byte[] saved)
    {
        (int x0, int x1, int y0, int y1) = Clip(s, f);
        for (int y = y0; y < y1; y++)
        {
            saved.AsSpan((y - y0) * (x1 - x0) * 4, (x1 - x0) * 4).CopyTo(canvas.AsSpan((((y - s.BoxY) * s.BoxW) + (x0 - s.BoxX)) * 4));
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
            canvas.AsSpan((((y - s.BoxY) * s.BoxW) + (x0 - s.BoxX)) * 4, (x1 - x0) * 4).Clear();
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
        // The ratio guard of the LZW decoder is tuned for deflate; LZW can legitimately shrink a solid color by more than 1000x. Output is
        // capped by the frame's own size instead, and one 12-bit code expands to at most 4094 bytes, so input bounds the work.
        _ = limits;
        // The decoder's tables are about 68 KB, so one is reused across frames; the frame limit is set again by Reset.
        LzwDecoder lzw = Interlocked.Exchange(ref s_lzw, null) ?? new LzwDecoder(LzwVariant.Gif);
        lzw.Reset(f.MinCodeSize, (long)f.Width * f.Height, 0);
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
                    if (y >= s.BoxY && y < s.BoxY + s.BoxH)
                    {
                        Span<byte> line = canvas.AsSpan((y - s.BoxY) * s.BoxW * 4, s.BoxW * 4);
                        for (int x = f.X; x < xEnd; x++)
                        {
                            int idx = rowBuf[x - f.X];
                            if (idx != tIdx)
                            {
                                BinaryPrimitives.WriteUInt32LittleEndian(line.Slice((x - s.BoxX) * 4, 4), pal[idx]);
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
            Interlocked.CompareExchange(ref s_lzw, lzw, null);
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
