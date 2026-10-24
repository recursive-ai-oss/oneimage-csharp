using System.Buffers;
using System.Buffers.Binary;
using OneImage.Compression;
using OneImage.Ops.Quantize;

namespace OneImage.Codecs.Gif;

/// <summary>
/// GIF encoder. Frames are read once, in order, and turned into palette indices; the file is assembled at the end because the global
/// color table and every LZW minimum code size depend on all frames.
/// </summary>
/// <remarks>
/// Palette choice: while the colors of all frames so far fit one table, frames index into a shared table (the global table). The first
/// frame that does not fit switches to per-frame tables: earlier frames get the prefix of the shared table they used, later
/// frames are quantized on their own. Frame differencing (opaque animations): a frame after the first stores the bounding rectangle of
/// the pixels that differ from the previous frame, and pixels inside it that did not change are written as one reserved
/// transparent index (index 0 of the shared table), which LZW compresses to long runs; a frame identical to its predecessor becomes
/// a single transparent pixel so its delay is kept. Animations that contain transparency (or whose frames differ in size) cannot be
/// patched this way, because a transparent pixel cannot erase what is already shown; each of their frames is trimmed to its
/// visible bounding box and removed after display ("restore to background").
/// When frames need their own palettes, a palette made for one frame is reused for the following frames as long as it represents them
/// about as well as it did its own frame (mean squared RGB error within twice the original, or under a small floor); otherwise the
/// frame is quantized afresh. A first frame whose palette is reused becomes the global table and the frames that share it carry no table.
/// Animation palettes always keep a transparent entry so that a palette can serve frames that patch over their predecessor.
/// </remarks>
internal sealed class GifEncoder : IImageEncoder
{
    private const int AlphaThreshold = 128;

    /// <summary>Frames up to this size are read one ahead; bigger ones are read in line so three frame buffers stay modest.</summary>
    private const int PrefetchMaxBytes = 16 << 20;

    /// <summary>Frames smaller than this are read inline: the overlap gains nothing measurable at that size (200x200 resized frames take the same time either way) and the extra reader costs about 3 MiB of resident memory.</summary>
    private const int PrefetchMinBytes = 1 << 20;

    public static GifEncoder Instance { get; } = new();

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } = [PixelFormat.Rgba8];

    public bool SupportsMultipleFrames => true;

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        var options = request.Options as GifEncoderOptions ?? new GifEncoderOptions();
        IReadOnlyList<ImageFrameReader> frames = request.Frames;
        if (frames.Count == 0)
        {
            throw new ImageOperationException("There is no frame to write.");
        }

        int sw = 0;
        int sh = 0;
        bool uniform = true;
        foreach (ImageFrameReader f in frames)
        {
            if (f.Width > 65535 || f.Height > 65535)
            {
                throw new ImageOperationException("GIF frames can be at most 65535 pixels wide or high.");
            }

            sw = Math.Max(sw, f.Width);
            sh = Math.Max(sh, f.Height);
            uniform &= f.Width == frames[0].Width && f.Height == frames[0].Height;
        }

        Plan? plan = Build(frames, options, sw, sh, uniform, forceSelfContained: false, cancellationToken)
            ?? Build(frames, options, sw, sh, uniform, forceSelfContained: true, cancellationToken);
        try
        {
            Write(plan!, request, destination, sw, sh, options, cancellationToken);
        }
        finally
        {
            plan!.Release();
        }
    }

    private sealed class PendingFrame
    {
        public int X, Y, W, H;
        public byte[] Indices = []; // rented from the shared pool, longer than IndexCount
        public int IndexCount;
        public int Disposal;
        public int DelayCs;
        public int Transparent = -1;
        public byte[]? LocalRgb; // RGB triplets, unpadded
        public int LocalCount;
        public int SharedCount; // entries of the shared table this frame may index (when LocalRgb is null)

        public void ReleaseIndices()
        {
            byte[] b = Indices;
            Indices = [];
            IndexCount = 0;
            if (b.Length > 0)
            {
                Pools<byte>.Pool.Return(b);
            }
        }
    }

    private sealed class Plan
    {
        public readonly List<PendingFrame> Frames = [];
        public ColorSet Shared = new();
        public bool SharedAlive = true;
        public bool ReserveT;

        /// <summary>Gives the frames' index buffers back to the pool.</summary>
        public void Release()
        {
            foreach (PendingFrame f in Frames)
            {
                f.ReleaseIndices();
            }
        }
    }

    /// <summary>The palette made for an earlier frame, kept so following frames that it still suits do not each pay for a new quantization.</summary>
    private sealed class Anchor(QuantizedPalette palette, byte[] rgb, double meanError)
    {
        public readonly QuantizedPalette Palette = palette;
        public readonly byte[] Rgb = rgb;
        public readonly double MeanError = meanError;
    }

    /// <summary>Reads all frames and decides indices and rectangles. Returns null when an opaque-mode assumption was broken and the caller must retry self-contained.</summary>
    private static Plan? Build(IReadOnlyList<ImageFrameReader> frames, GifEncoderOptions options, int sw, int sh, bool uniform, bool forceSelfContained, CancellationToken ct)
    {
        bool multi = frames.Count > 1;
        bool selfContained = multi && (forceSelfContained || !uniform || !options.FrameDifferencing);
        var plan = new Plan { ReserveT = multi && !selfContained };
        int sharedCap = options.MaxColors - (plan.ReserveT ? 1 : 0);
        byte[]? prev = null;
        byte[]? cur = null;
        int curLen = 0;
        int prevLen = 0;
        Anchor? anchor = null;
        bool ok = false;

        // While one frame is indexed, the next is read on another thread into a third buffer. This is for frames that cost real work
        // to produce (resized, converted, ...); a plain decode is a copy, and a spare frame buffer plus a thread would only add memory. Frames are still
        // requested one at a time and in order, so what is read and what is written do not depend on timing.
        bool prefetch = multi && uniform && frames[0].Degree > 1 && !frames[0].IsPlainDecode && (long)frames[0].Width * frames[0].Height * 4 is >= PrefetchMinBytes and <= PrefetchMaxBytes;
        byte[]? ahead = null;
        Task? pending = null;
        try
        {
            for (int i = 0; i < frames.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ImageFrameReader fr = frames[i];
                int w = fr.Width;
                int h = fr.Height;
                long need = (long)w * h * 4;
                if (need > Array.MaxLength)
                {
                    throw new ImageLimitException("The frame is too large to encode as GIF in one piece.");
                }

                if (cur == null || curLen != need)
                {
                    if (cur != null)
                    {
                        Pools<byte>.Pool.Return(cur);
                    }

                    cur = Pools<byte>.Pool.Rent((int)need);
                    curLen = (int)need;
                }

                // One request for the whole frame: the engine then computes each tile once and keeps none of them, where reading
                // in tile-row bands would leave every frame's tiles in the cache until the save ends.
                if (pending != null)
                {
                    Task read = pending;
                    pending = null;
                    read.GetAwaiter().GetResult();
                }
                else
                {
                    fr.ReadRows(0, h, cur.AsSpan(0, curLen));
                }

                if (prefetch && i + 1 < frames.Count)
                {
                    ahead ??= Pools<byte>.Pool.Rent(curLen);
                    byte[] target = ahead;
                    int bytes = curLen;
                    ImageFrameReader next = frames[i + 1];
                    pending = Task.Run(() => next.ReadRows(0, next.Height, target.AsSpan(0, bytes)), CancellationToken.None);
                }

                bool hasTransparency = HasTransparency(cur.AsSpan(0, curLen));
                if (i == 0 && multi && uniform && hasTransparency && !selfContained)
                {
                    selfContained = true;
                    plan.ReserveT = false;
                    sharedCap = options.MaxColors;
                }
                else if (hasTransparency && plan.ReserveT)
                {
                    return null; // an earlier frame was already stored as a patch over its predecessor
                }

                int x0 = 0, y0 = 0, rw = w, rh = h;
                bool diff = plan.ReserveT && i > 0 && prev != null;
                if (diff)
                {
                    if (!ChangedBounds(prev!, cur, w, h, out x0, out y0, out rw, out rh))
                    {
                        (x0, y0, rw, rh) = (0, 0, 1, 1);
                    }
                }
                else if (selfContained && multi)
                {
                    if (!VisibleBounds(cur, w, h, out x0, out y0, out rw, out rh))
                    {
                        (x0, y0, rw, rh) = (0, 0, 1, 1);
                    }
                }

                // Region pixels; pixels that need not be drawn (unchanged since the previous frame) become transparent.
                int workLen = rw * rh * 4;
                byte[] workBuf = Pools<byte>.Pool.Rent(workLen);
                Span<byte> workSpan = workBuf.AsSpan(0, workLen);
                for (int y = 0; y < rh; y++)
                {
                    ReadOnlySpan<byte> src = cur.AsSpan((((y0 + y) * w) + x0) * 4, rw * 4);
                    Span<byte> dst = workSpan.Slice(y * rw * 4, rw * 4);
                    if (!diff)
                    {
                        src.CopyTo(dst);
                        continue;
                    }

                    ReadOnlySpan<byte> psrc = prev.AsSpan((((y0 + y) * w) + x0) * 4, rw * 4);
                    for (int x = 0; x < rw; x++)
                    {
                        uint a = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(x * 4, 4));
                        uint b = BinaryPrimitives.ReadUInt32LittleEndian(psrc.Slice(x * 4, 4));
                        if (a != b && ColorQuantizer.Key(a, QuantizeAlpha.Binary, AlphaThreshold) != ColorQuantizer.Key(b, QuantizeAlpha.Binary, AlphaThreshold))
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(x * 4, 4), a);
                        }
                        else
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(x * 4, 4), 0);
                        }
                    }
                }

                var pf = new PendingFrame
                {
                    X = x0,
                    Y = y0,
                    W = rw,
                    H = rh,
                    Disposal = !multi ? 0 : selfContained ? 2 : 1,
                    DelayCs = (int)Math.Clamp(Math.Round(fr.Delay.TotalMilliseconds / 10.0), 0, 65535),
                };
                plan.Frames.Add(pf);
                try
                {
                    if (plan.SharedAlive && !TryIndexShared(plan, workSpan, rw, rh, sharedCap, pf))
                    {
                        plan.SharedAlive = false;
                    }

                    if (!plan.SharedAlive)
                    {
                        IndexLocal(workSpan, rw, rh, options, pf, multi, ref anchor);
                    }
                }
                finally
                {
                    Pools<byte>.Pool.Return(workBuf);
                }

                if (pending != null)
                {
                    (prev, cur, ahead) = (cur, ahead, prev);
                    prevLen = curLen = (int)need;
                }
                else
                {
                    (prev, cur) = (cur, prev);
                    (prevLen, curLen) = (curLen, prevLen);
                }
            }

            ok = true;
            return plan;
        }
        finally
        {
            if (pending != null)
            {
                // The read still owns its buffer; wait for it before the buffers go back. Its own error, if any, is secondary to the
                // one that is already leaving this method.
                pending.ContinueWith(static _ => { }, TaskScheduler.Default).GetAwaiter().GetResult();
            }

            if (ahead != null)
            {
                Pools<byte>.Pool.Return(ahead);
            }

            if (cur != null)
            {
                Pools<byte>.Pool.Return(cur);
            }

            if (prev != null)
            {
                Pools<byte>.Pool.Return(prev);
            }

            if (!ok)
            {
                plan.Release();
            }
        }
    }

    private static bool HasTransparency(ReadOnlySpan<byte> rgba) => Kernels.QuantizeKernels.HasAlphaBelow(rgba, AlphaThreshold);

    /// <summary>
    /// Bounding box of the pixels that differ between two frames. Rows are compared as raw bytes first (vectorized), so identical
    /// rows cost almost nothing; a pixel that differs only in ways the GIF palette cannot show still widens the box, which is harmless
    /// because pixels are compared again by color when the rectangle is written.
    /// </summary>
    private static bool ChangedBounds(byte[] prev, byte[] cur, int w, int h, out int x0, out int y0, out int rw, out int rh)
    {
        int minX = w, maxX = -1, minY = h, maxY = -1;
        int rowBytes = w * 4;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> a = prev.AsSpan(y * rowBytes, rowBytes);
            ReadOnlySpan<byte> b = cur.AsSpan(y * rowBytes, rowBytes);
            int head = a.CommonPrefixLength(b) >> 2;
            if (head >= w)
            {
                continue;
            }

            // Walk back from the end in 64-byte blocks, then pixel by pixel inside the first block that differs.
            int end = rowBytes;
            while (end >= 64 && a.Slice(end - 64, 64).SequenceEqual(b.Slice(end - 64, 64)))
            {
                end -= 64;
            }

            int lastPx = (end >> 2) - 1;
            while (lastPx > head && a.Slice(lastPx * 4, 4).SequenceEqual(b.Slice(lastPx * 4, 4)))
            {
                lastPx--;
            }

            minX = Math.Min(minX, head);
            maxX = Math.Max(maxX, lastPx);
            minY = Math.Min(minY, y);
            maxY = y;
        }

        x0 = minX;
        y0 = minY;
        rw = maxX - minX + 1;
        rh = maxY - minY + 1;
        return maxX >= 0;
    }

    private static bool VisibleBounds(byte[] cur, int w, int h, out int x0, out int y0, out int rw, out int rh)
    {
        int minX = w, maxX = -1, minY = h, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> row = cur.AsSpan(y * w * 4, w * 4);
            for (int x = 0; x < w; x++)
            {
                if (row[(x * 4) + 3] >= AlphaThreshold)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = y;
                }
            }
        }

        x0 = minX;
        y0 = minY;
        rw = maxX - minX + 1;
        rh = maxY - minY + 1;
        return maxX >= 0;
    }

    /// <summary>Indexes the region against the shared table, adding new colors; returns false (leaving the frame unindexed) when the table would overflow.</summary>
    private static bool TryIndexShared(Plan plan, ReadOnlySpan<byte> work, int rw, int rh, int cap, PendingFrame pf)
    {
        int offset = plan.ReserveT ? 1 : 0;
        int count = rw * rh;
        byte[] indices = Pools<byte>.Pool.Rent(count);
        bool usesT = false;
        uint lastKey = 0;
        int lastIdx = -1;
        bool have = false;
        ColorSet set = plan.Shared;
        for (int i = 0; i < count; i++)
        {
            uint key = ColorQuantizer.Key(BinaryPrimitives.ReadUInt32LittleEndian(work.Slice(i * 4, 4)), QuantizeAlpha.Binary, AlphaThreshold);
            if (!have || key != lastKey)
            {
                int idx;
                if (key == 0 && plan.ReserveT)
                {
                    idx = 0;
                    usesT = true;
                }
                else
                {
                    int s = set.Find(key);
                    if (s < 0)
                    {
                        if (set.Count >= cap)
                        {
                            Pools<byte>.Pool.Return(indices);
                            return false;
                        }

                        s = set.Add(key);
                    }

                    idx = s + offset;
                }

                lastIdx = idx;
                lastKey = key;
                have = true;
            }

            indices[i] = (byte)lastIdx;
        }

        pf.Indices = indices;
        pf.IndexCount = count;
        pf.SharedCount = set.Count + offset;
        if (usesT)
        {
            pf.Transparent = 0;
        }
        else if (!plan.ReserveT)
        {
            int t = set.Find(0);
            if (t >= 0 && indices.AsSpan(0, count).Contains((byte)t))
            {
                pf.Transparent = t;
            }
        }

        return true;
    }

    /// <summary>A later frame reuses the palette made for an earlier one when its mean squared RGB error stays within this much of the earlier frame's (or the floor below).</summary>
    private const double ReuseErrorFactor = 2.0;

    /// <summary>Mean squared RGB error per pixel (summed over the channels, roughly 2.8 levels RMS per channel) that is always accepted when reusing a palette.</summary>
    private const double ReuseErrorFloor = 24.0;

    /// <summary>
    /// Quantizes a frame region. In an animation the palette of the last frame that needed one (the anchor) is tried first: a frame
    /// whose colors it still represents about as well maps onto it directly, which costs a table lookup per pixel instead of a
    /// histogram, cumulative moments and box search per frame. Otherwise the region gets its own palette and becomes the anchor.
    /// Every palette of an animation keeps a transparent entry, so any anchor can serve frames that patch over their predecessor.
    /// </summary>
    private static void IndexLocal(ReadOnlySpan<byte> work, int rw, int rh, GifEncoderOptions options, PendingFrame pf, bool multi, ref Anchor? anchor)
    {
        QuantizedPalette pal;
        byte[] rgb;
        if (anchor != null && HasUsableAnchor(anchor, work))
        {
            pal = anchor.Palette;
            rgb = anchor.Rgb;
        }
        else
        {
            var q = new PaletteBuilder(options.Quantizer, options.MaxColors, QuantizeAlpha.Binary, AlphaThreshold, reserveTransparent: multi);
            q.AddRows(work);
            pal = q.Build();
            rgb = new byte[pal.Count * 3];
            for (int i = 0; i < pal.Count; i++)
            {
                rgb[i * 3] = pal.Rgba[i * 4];
                rgb[(i * 3) + 1] = pal.Rgba[(i * 4) + 1];
                rgb[(i * 3) + 2] = pal.Rgba[(i * 4) + 2];
            }

            if (multi)
            {
                long err = pal.ColorError(work, out long px);
                anchor = new Anchor(pal, rgb, px == 0 ? 0 : (double)err / px);
            }
        }

        var mapper = new PaletteMapper(pal, rw, options.Dither);
        byte[] indices = Pools<byte>.Pool.Rent(rw * rh);
        for (int y = 0; y < rh; y++)
        {
            mapper.MapRow(y, work.Slice(y * rw * 4, rw * 4), indices.AsSpan(y * rw, rw));
        }

        pf.Indices = indices;
        pf.IndexCount = rw * rh;
        pf.LocalRgb = rgb;
        pf.LocalCount = pal.Count;
        pf.Transparent = pal.TransparentIndex;
    }

    private static bool HasUsableAnchor(Anchor anchor, ReadOnlySpan<byte> work)
    {
        // A frame with transparent pixels needs a transparent entry.
        if (anchor.Palette.TransparentIndex < 0 && Kernels.QuantizeKernels.HasAlphaBelow(work, AlphaThreshold))
        {
            return false;
        }

        long err = anchor.Palette.ColorError(work, out long px);
        return px == 0 || (double)err / px <= Math.Max(anchor.MeanError * ReuseErrorFactor, ReuseErrorFloor);
    }

    /// <summary>Gets the table size exponent: smallest n >= 1 with 2^n >= count.</summary>
    private static int TableBits(int count)
    {
        int bits = 1;
        while ((1 << bits) < count)
        {
            bits++;
        }

        return bits;
    }

    private static byte[] SharedRgb(Plan plan, int count)
    {
        int offset = plan.ReserveT ? 1 : 0;
        var rgb = new byte[count * 3];
        for (int i = 0; i < count - offset; i++)
        {
            uint k = plan.Shared[i];
            rgb[(i + offset) * 3] = (byte)k;
            rgb[((i + offset) * 3) + 1] = (byte)(k >> 8);
            rgb[((i + offset) * 3) + 2] = (byte)(k >> 16);
        }

        return rgb;
    }

    private static void WriteTable(Stream s, ReadOnlySpan<byte> rgb, int bits)
    {
        var table = new byte[(1 << bits) * 3];
        rgb.CopyTo(table);
        s.Write(table);
    }

    private static void Write(Plan plan, ImageEncodeRequest request, Stream dest, int sw, int sh, GifEncoderOptions options, CancellationToken ct)
    {
        _ = options;
        List<PendingFrame> frames = plan.Frames;
        bool multi = frames.Count > 1;

        // The first frame's own palette becomes the global table; frames that share it (a single frame, or an animation whose later
        // frames kept the first palette) then carry no table of their own.
        byte[]? globalRgb = null;
        int globalCount = 0;
        if (plan.SharedAlive)
        {
            globalCount = Math.Max(plan.Shared.Count + (plan.ReserveT ? 1 : 0), 1);
            globalRgb = SharedRgb(plan, globalCount);
        }
        else if (frames[0].LocalRgb != null)
        {
            globalRgb = frames[0].LocalRgb;
            globalCount = frames[0].LocalCount;
        }

        Span<byte> head = stackalloc byte[13];
        "GIF89a"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt16LittleEndian(head[6..], (ushort)sw);
        BinaryPrimitives.WriteUInt16LittleEndian(head[8..], (ushort)sh);
        int gBits = globalRgb != null ? TableBits(globalCount) : 0;
        head[10] = (byte)(globalRgb != null ? 0x80 | 0x70 | (gBits - 1) : 0x70);
        head[11] = 0;
        head[12] = 0;
        dest.Write(head);
        if (globalRgb != null)
        {
            WriteTable(dest, globalRgb, gBits);
        }

        if (multi && request.LoopCount != 1)
        {
            Span<byte> ns = stackalloc byte[19];
            ns[0] = 0x21;
            ns[1] = 0xFF;
            ns[2] = 11;
            "NETSCAPE2.0"u8.CopyTo(ns[3..]);
            ns[14] = 3;
            ns[15] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(ns[16..], (ushort)(request.LoopCount == 0 ? 0 : Math.Clamp(request.LoopCount - 1, 1, 65535)));
            ns[18] = 0;
            dest.Write(ns);
        }

        ReadOnlyMemory<byte> xmp = request.Metadata.Xmp;
        if (xmp.Length > 0)
        {
            Span<byte> hdr = stackalloc byte[14];
            hdr[0] = 0x21;
            hdr[1] = 0xFF;
            hdr[2] = 11;
            "XMP DataXMP"u8.CopyTo(hdr[3..]);
            dest.Write(hdr[..14]);
            dest.Write(xmp.Span);
            var ramp = new byte[258];
            ramp[0] = 1;
            for (int i = 1; i <= 255; i++)
            {
                ramp[i] = (byte)(256 - i);
            }

            dest.Write(ramp);
        }

        var lzwSink = new ArrayBufferWriter<byte>(1 << 16);
        LzwEncoder? enc = null;
        Span<byte> small = stackalloc byte[18];
        foreach (PendingFrame f in frames)
        {
            ct.ThrowIfCancellationRequested();
            byte[]? table;
            int count;
            if (f.LocalRgb != null && ReferenceEquals(f.LocalRgb, globalRgb))
            {
                table = null;
                count = globalCount;
            }
            else if (f.LocalRgb != null)
            {
                table = f.LocalRgb;
                count = f.LocalCount;
            }
            else if (globalRgb != null)
            {
                table = null;
                count = globalCount;
            }
            else
            {
                // The shared table did not survive to the end: this frame carries the part of it that it used.
                count = f.SharedCount;
                table = SharedRgb(plan, count);
            }

            int bits = TableBits(count);
            if (multi || f.Transparent >= 0)
            {
                small[0] = 0x21;
                small[1] = 0xF9;
                small[2] = 4;
                small[3] = (byte)((f.Disposal << 2) | (f.Transparent >= 0 ? 1 : 0));
                BinaryPrimitives.WriteUInt16LittleEndian(small[4..], (ushort)f.DelayCs);
                small[6] = (byte)Math.Max(f.Transparent, 0);
                small[7] = 0;
                dest.Write(small[..8]);
            }

            small[0] = 0x2C;
            BinaryPrimitives.WriteUInt16LittleEndian(small[1..], (ushort)f.X);
            BinaryPrimitives.WriteUInt16LittleEndian(small[3..], (ushort)f.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(small[5..], (ushort)f.W);
            BinaryPrimitives.WriteUInt16LittleEndian(small[7..], (ushort)f.H);
            small[9] = (byte)(table != null ? 0x80 | (bits - 1) : 0);
            dest.Write(small[..10]);
            if (table != null)
            {
                WriteTable(dest, table, bits);
            }

            int minCode = Math.Max(2, bits);
            lzwSink.ResetWrittenCount();
            if (enc == null)
            {
                enc = new LzwEncoder(lzwSink, LzwVariant.Gif, minCode);
            }
            else
            {
                enc.Reset(minCode);
            }

            enc.Write(f.Indices.AsSpan(0, f.IndexCount));
            enc.Finish();
            dest.WriteByte((byte)minCode);
            ReadOnlySpan<byte> data = lzwSink.WrittenSpan;
            for (int off = 0; off < data.Length; off += 255)
            {
                int n = Math.Min(255, data.Length - off);
                dest.WriteByte((byte)n);
                dest.Write(data.Slice(off, n));
            }

            dest.WriteByte(0);
            f.ReleaseIndices();
        }

        dest.WriteByte(0x3B);
    }
}
