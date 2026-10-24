using System.Buffers;
using System.Buffers.Binary;
using OneImage.Compression;
using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// PNG and APNG decoder (W3C PNG 3rd edition, including the acTL/fcTL/fdAT animation chunks).
/// </summary>
/// <remarks>
/// <para>Pixel formats delivered: gray 1-8 bit becomes Gray8 (scaled), 16-bit gray Gray16, palette images RGB8 (RGBA8 when
/// a tRNS chunk is present), truecolor RGB8/RGB16, alpha types GrayAlpha8/RGBA8/RGBA16. A tRNS color key adds an alpha
/// channel (gray with a key becomes GrayAlpha8, or RGBA16 at 16 bits, since there is no 16-bit gray+alpha format).
/// Animated files always deliver RGBA8 (RGBA16 for 16-bit files), one full composited canvas per frame.</para>
/// <para>Non-interlaced images decode in memory proportional to the width. Adam7 images are assembled in memory.</para>
/// <para>Error policy: critical chunks (IHDR, PLTE, IDAT) with a bad CRC, a malformed header, a missing or truncated
/// image data stream, or an unknown critical chunk raise <see cref="ImageFormatException"/>. Ancillary chunks that are
/// malformed or have a bad CRC are ignored, except that sizes beyond the limits raise <see cref="ImageLimitException"/>.
/// Data after the last needed row (extra IDAT bytes, a missing IEND) is not an error.</para>
/// </remarks>
internal sealed class PngDecoder : IImageDecoder
{
    public static PngDecoder Instance { get; } = new();

    public DecoderCapabilities Capabilities => DecoderCapabilities.Region;

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        long length = source.Length;
        source.Position = 0;
        // A small array rather than stackalloc: a method with both a loop and stackalloc cannot be compiled by the quick tier,
        // and the optimizing compile of this large method cost several MB of resident memory on the first load.
        Span<byte> hdr = new byte[8];
        if (ContainerIo.ReadUpTo(source, hdr) < 8 || !hdr.SequenceEqual(PngChunk.Signature))
        {
            throw new ImageFormatException("Not a PNG file: bad signature.");
        }

        // IHDR must come first.
        if (ContainerIo.ReadUpTo(source, hdr) < 8 || ContainerIo.U32Be(hdr) != 13 || ContainerIo.U32Be(hdr[4..]) != PngChunk.IHDR)
        {
            throw new ImageFormatException("The first chunk is not a valid IHDR.");
        }

        byte[] ihdr = ReadBody(source, 13, PngChunk.IHDR, length, out bool ihdrCrc);
        if (!ihdrCrc)
        {
            throw new ImageFormatException("CRC mismatch in IHDR.");
        }

        long width = ContainerIo.U32Be(ihdr);
        long height = ContainerIo.U32Be(ihdr.AsSpan(4));
        int depth = ihdr[8];
        int colorType = ihdr[9];
        if (ihdr[10] != 0 || ihdr[11] != 0 || ihdr[12] > 1)
        {
            throw new ImageFormatException("Unsupported compression, filter or interlace method.");
        }

        bool validDepth = colorType switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => depth is 8 or 16,
            _ => false,
        };
        if (!validDepth)
        {
            throw new ImageFormatException($"Invalid color type {colorType} with bit depth {depth}.");
        }

        limits.CheckFrame(width, height);

        var st = new PngInfo
        {
            Width = (int)width,
            Height = (int)height,
            ColorType = colorType,
            BitDepth = depth,
            Interlaced = ihdr[12] == 1,
            Channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 },
        };

        var sc = new PngScan();

        while (ContainerIo.ReadUpTo(source, hdr) == 8)
        {
            long chunkStart = source.Position - 8;
            long bodyStart = source.Position;
            uint len = ContainerIo.U32Be(hdr);
            uint type = ContainerIo.U32Be(hdr[4..]);
            if (!PngChunk.IsValidType(type) || len > 0x7FFFFFFF)
            {
                throw new ImageFormatException("Invalid chunk header.");
            }

            long end = bodyStart + len + 4;
            if (end > length)
            {
                if (sc.SeenIdat)
                {
                    break;
                }

                throw new ImageFormatException("The file ends inside a chunk before any image data.");
            }

            if (type == PngChunk.IEND)
            {
                break;
            }

            ReadChunk(source, st, sc, type, len, chunkStart, bodyStart, end, length, limits);

            if (source.Position != end)
            {
                source.Position = end;
            }
        }

        if (!sc.SeenIdat)
        {
            throw new ImageFormatException("The file has no image data.");
        }

        if (colorType == 3 && (sc.Plte == null || sc.Plte.Length == 0))
        {
            throw new ImageFormatException("A palette image needs a PLTE chunk.");
        }

        Configure(st, sc.Plte, sc.Trns, depth, colorType);

        PngFrameDesc[] descs;
        if (sc.Animation && sc.Frames.Count > 0)
        {
            if (sc.Frames.Count != sc.NumFrames)
            {
                throw new ImageFormatException("The number of frame control chunks does not match acTL.");
            }

            foreach (PngFrameDesc f in sc.Frames)
            {
                if (f.DataOffset < 0)
                {
                    throw new ImageFormatException("An APNG frame has no image data.");
                }
            }

            descs = sc.Frames.ToArray();
            st.IsAnimated = true;
            st.CanvasFormat = depth == 16 ? PixelFormat.Rgba16 : PixelFormat.Rgba8;
        }
        else
        {
            descs = [new PngFrameDesc { Width = st.Width, Height = st.Height, DataOffset = sc.FirstIdat }];
            sc.NumPlays = 0;
        }

        st.Frames = descs;
        var infos = new List<FrameInfo>(descs.Length);
        foreach (PngFrameDesc f in descs)
        {
            long ticks = st.IsAnimated ? f.DelayNum * TimeSpan.TicksPerSecond / (f.DelayDen == 0 ? 100 : f.DelayDen) : 0;
            infos.Add(new FrameInfo(st.Width, st.Height, st.IsAnimated ? st.CanvasFormat : st.Natural, TimeSpan.FromTicks(ticks)));
        }

        var meta = new ImageMetadata
        {
            IccProfile = sc.Icc ?? default,
            Exif = sc.Exif ?? default,
            Xmp = sc.Xmp ?? default,
        };
        return new ImageInfo(PngFormat.Instance, infos, (int)Math.Min(sc.NumPlays, int.MaxValue), meta, st);
    }

    // The per-chunk work of ReadInfo lives in its own method: one method holding every chunk type's locals is a very large
    // function for the JIT, and compiling it on the first load cost several MB of resident memory.
    private static void ReadChunk(Stream source, PngInfo st, PngScan sc, uint type, uint len, long chunkStart, long bodyStart, long end, long length, ImageLimits limits)
    {
        switch (type)
        {
            case PngChunk.PLTE:
                {
                    if (len % 3 != 0 || len == 0 || len > 768)
                    {
                        throw new ImageFormatException("Invalid PLTE chunk length.");
                    }

                    byte[] body = ReadBody(source, len, type, length, out bool ok);
                    if (!ok)
                    {
                        throw new ImageFormatException("CRC mismatch in PLTE.");
                    }

                    if (!sc.SeenIdat && sc.Plte == null)
                    {
                        sc.Plte = body;
                    }

                    break;
                }

            case PngChunk.IDAT:
                if (!sc.SeenIdat)
                {
                    sc.SeenIdat = true;
                    sc.FirstIdat = chunkStart;
                    if (sc.Pending >= 0)
                    {
                        PngFrameDesc f = sc.Frames[sc.Pending];
                        f.DataOffset = chunkStart;
                        sc.Frames[sc.Pending] = f;
                        sc.Pending = -1;
                    }
                }

                source.Position = end;
                break;
            case PngChunk.tRNS:
                if (!sc.SeenIdat && len <= 256)
                {
                    byte[]? body = ReadBodyChecked(source, len, type, length);
                    if (body != null)
                    {
                        sc.Trns = body;
                    }
                }
                else
                {
                    source.Position = end;
                }

                break;
            case PngChunk.gAMA:
                if (len == 4)
                {
                    byte[]? b = ReadBodyChecked(source, len, type, length);
                    if (b != null)
                    {
                        st.Gamma = ContainerIo.U32Be(b);
                    }
                }
                else
                {
                    source.Position = end;
                }

                break;
            case PngChunk.cHRM:
                if (len == 32)
                {
                    byte[]? b = ReadBodyChecked(source, len, type, length);
                    if (b != null)
                    {
                        var v = new int[8];
                        for (int i = 0; i < 8; i++)
                        {
                            v[i] = (int)ContainerIo.U32Be(b.AsSpan(i * 4));
                        }

                        st.Chromaticities = v;
                    }
                }
                else
                {
                    source.Position = end;
                }

                break;
            case PngChunk.sRGB:
                if (len == 1)
                {
                    byte[]? b = ReadBodyChecked(source, len, type, length);
                    if (b != null)
                    {
                        st.SrgbIntent = b[0];
                    }
                }
                else
                {
                    source.Position = end;
                }

                break;
            case PngChunk.cICP:
                if (len == 4)
                {
                    byte[]? b = ReadBodyChecked(source, len, type, length);
                    if (b != null)
                    {
                        st.CicP = new int[] { b[0], b[1], b[2], b[3] };
                    }
                }
                else
                {
                    source.Position = end;
                }

                break;
            case PngChunk.iCCP:
                if (sc.Icc == null && !sc.SeenIdat && len >= 4)
                {
                    CheckMetadataSize(len, limits);
                    byte[]? b = ReadBodyChecked(source, len, type, length);
                    if (b != null)
                    {
                        sc.Icc = ParseIccp(b, limits);
                    }
                }
                else
                {
                    source.Position = end;
                }

                break;
            case PngChunk.eXIf:
                if (sc.Exif == null && len >= 8)
                {
                    CheckMetadataSize(len, limits);
                    byte[]? b = ReadBodyChecked(source, len, type, length);
                    if (b != null)
                    {
                        sc.Exif = b;
                    }
                }
                else
                {
                    source.Position = end;
                }

                break;
            case PngChunk.iTXt:
            case PngChunk.tEXt:
                if (sc.Xmp == null && len >= 22)
                {
                    // Cheap pre-check of the keyword so unrelated text chunks are never read into memory.
                    if (HasXmpKeyword(source, bodyStart))
                    {
                        CheckMetadataSize(len, limits);
                        byte[]? b = ReadBodyChecked(source, len, type, length);
                        if (b != null)
                        {
                            sc.Xmp = ParseXmp(b, type == PngChunk.iTXt, limits);
                        }

                        break;
                    }
                }

                source.Position = end;
                break;
            case PngChunk.acTL:
                if (!sc.SeenIdat && len == 8 && !sc.Animation)
                {
                    byte[]? b = ReadBodyChecked(source, len, type, length);
                    if (b != null)
                    {
                        sc.NumFrames = ContainerIo.U32Be(b);
                        sc.NumPlays = ContainerIo.U32Be(b.AsSpan(4));
                        sc.Animation = sc.NumFrames > 0 && sc.NumFrames <= 0x7FFFFFFF;
                        if (sc.NumFrames > limits.MaxFrames)
                        {
                            throw new ImageLimitException($"Frame count {sc.NumFrames} exceeds the limit of {limits.MaxFrames}.");
                        }
                    }
                }
                else
                {
                    source.Position = end;
                }

                break;
            case PngChunk.fcTL:
                if (sc.Animation && len == 26)
                {
                    byte[]? b = ReadBodyChecked(source, len, type, length);
                    if (b != null)
                    {
                        if (sc.Frames.Count >= limits.MaxFrames)
                        {
                            throw new ImageLimitException($"Frame count exceeds the limit of {limits.MaxFrames}.");
                        }

                        var f = new PngFrameDesc
                        {
                            Width = (int)Math.Min(ContainerIo.U32Be(b.AsSpan(4)), int.MaxValue),
                            Height = (int)Math.Min(ContainerIo.U32Be(b.AsSpan(8)), int.MaxValue),
                            X = (int)Math.Min(ContainerIo.U32Be(b.AsSpan(12)), int.MaxValue),
                            Y = (int)Math.Min(ContainerIo.U32Be(b.AsSpan(16)), int.MaxValue),
                            DelayNum = ContainerIo.U16Be(b.AsSpan(20)),
                            DelayDen = ContainerIo.U16Be(b.AsSpan(22)),
                            Dispose = b[24],
                            Blend = b[25],
                            DataOffset = -1,
                            IsFdat = sc.SeenIdat,
                        };
                        if (f.Width <= 0 || f.Height <= 0 || f.Width > st.Width || f.Height > st.Height
                            || f.X > st.Width - f.Width || f.Y > st.Height - f.Height || f.Dispose > 2 || f.Blend > 1)
                        {
                            throw new ImageFormatException("Invalid APNG frame control chunk.");
                        }

                        sc.Frames.Add(f);
                        sc.Pending = sc.Frames.Count - 1;
                    }
                }
                else
                {
                    source.Position = end;
                }

                break;
            case PngChunk.fdAT:
                if (sc.Pending >= 0 && sc.SeenIdat)
                {
                    PngFrameDesc f = sc.Frames[sc.Pending];
                    f.DataOffset = chunkStart;
                    sc.Frames[sc.Pending] = f;
                    sc.Pending = -1;
                }

                source.Position = end;
                break;
            default:
                if (PngChunk.IsCritical(type))
                {
                    throw new ImageFormatException($"Unknown critical chunk {(char)(type >> 24)}{(char)((type >> 16) & 0xFF)}{(char)((type >> 8) & 0xFF)}{(char)(type & 0xFF)}.");
                }

                source.Position = end;
                break;
        }
    }

    /// <summary>What the chunk walk of <see cref="ReadInfo"/> has found so far.</summary>
    private sealed class PngScan
    {
        public byte[]? Plte { get; set; }

        public byte[]? Trns { get; set; }

        public byte[]? Icc { get; set; }

        public byte[]? Exif { get; set; }

        public byte[]? Xmp { get; set; }

        public List<PngFrameDesc> Frames { get; } = [];

        public bool Animation { get; set; }

        public uint NumFrames { get; set; }

        public uint NumPlays { get; set; }

        public bool SeenIdat { get; set; }

        public long FirstIdat { get; set; } = -1;

        public int Pending { get; set; } = -1;
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(sink);
        var st = (PngInfo?)info.DecoderState ?? throw new ImageOperationException("The image info was not produced by the PNG decoder.");
        if ((uint)frameIndex >= (uint)st.Frames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        ImageRect r = request.Region;
        if (r.X < 0 || r.Y < 0 || r.Width != sink.Width || r.Height != sink.Height || r.X + r.Width > st.Width || r.Y + r.Height > st.Height)
        {
            throw new ArgumentException("The requested region does not match the sink or the image.", nameof(request));
        }

        if (st.IsAnimated)
        {
            DecodeAnimated(source, st, frameIndex, r, sink, limits, cancellationToken);
            return;
        }

        var target = new SinkTarget(sink, r, st.Natural.BytesPerPixel, st.Width);
        try
        {
            PngImageReader.Decode(source, st, st.Frames[0], target, r.Y + r.Height, limits, cancellationToken);
        }
        finally
        {
            target.Dispose();
        }
    }

    private static void DecodeAnimated(Stream source, PngInfo st, int index, ImageRect region, IRowSink sink, ImageLimits limits, CancellationToken ct)
    {
        int cbpp = st.CanvasFormat.BytesPerPixel;
        int stride = st.Width * cbpp;
        long canvasBytes = (long)st.Height * stride;
        if (canvasBytes > Array.MaxLength)
        {
            throw new ImageLimitException("The animation canvas is too large.");
        }

        ApngWork? work = st.Cache.Take();
        if (work == null || work.Index > index)
        {
            if (work != null)
            {
                st.Cache.Put(work);
            }

            work = new ApngWork { Canvas = new byte[canvasBytes] };
        }

        {
            for (int i = work.Index + 1; i <= index; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i > 0)
                {
                    // Dispose the previous frame (APNG spec 5.4): background clears to transparent black, previous restores.
                    PngFrameDesc prevFrame = st.Frames[i - 1];
                    int op = prevFrame.Dispose;
                    if (op == 2 && work.Snapshot == null)
                    {
                        op = 1;
                    }

                    for (int y = prevFrame.Y; y < prevFrame.Y + prevFrame.Height; y++)
                    {
                        Span<byte> line = work.Canvas.AsSpan((y * stride) + (prevFrame.X * cbpp), prevFrame.Width * cbpp);
                        if (op == 1)
                        {
                            line.Clear();
                        }
                        else if (op == 2)
                        {
                            work.Snapshot.AsSpan((y * stride) + (prevFrame.X * cbpp), prevFrame.Width * cbpp).CopyTo(line);
                        }
                    }
                }

                PngFrameDesc f = st.Frames[i];
                if (f.Dispose == 2)
                {
                    work.Snapshot ??= new byte[canvasBytes];
                    work.Canvas.CopyTo(work.Snapshot, 0);
                }
                else
                {
                    work.Snapshot = null;
                }

                using var comp = new CompositeTarget(st, f, work.Canvas, stride);
                PngImageReader.Decode(source, st, f, comp, f.Height, limits, ct);
                work.Index = i;
            }

            int rowBytes = region.Width * cbpp;
            for (int y = 0; y < region.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                sink.WriteRows(y, work.Canvas.AsSpan(((region.Y + y) * stride) + (region.X * cbpp), rowBytes), 1);
            }
        }

        // On an exception the half-drawn canvas is dropped instead of being put back.
        st.Cache.Put(work);
    }

    // ---- Targets ----

    private sealed class SinkTarget : IPngRowTarget, IDisposable
    {
        private readonly IRowSink _sink;
        private readonly ImageRect _r;
        private readonly int _bpp;
        private readonly int _fullStride;
        private readonly bool _full;
        private byte[]? _buf;

        public SinkTarget(IRowSink sink, ImageRect r, int bpp, int fullWidth)
        {
            _sink = sink;
            _r = r;
            _bpp = bpp;
            _fullStride = fullWidth * bpp;
            _full = r.X == 0 && r.Width == fullWidth;
        }

        public void Rows(int y, ReadOnlySpan<byte> rows, int count)
        {
            int from = Math.Max(y, _r.Y);
            int to = Math.Min(y + count, _r.Y + _r.Height);
            if (from >= to)
            {
                return;
            }

            int n = to - from;
            if (_full)
            {
                _sink.WriteRows(from - _r.Y, rows.Slice((from - y) * _fullStride, n * _fullStride), n);
                return;
            }

            int outStride = _r.Width * _bpp;
            _buf ??= Pools<byte>.Pool.Rent(Math.Max(outStride * Math.Min(count, _r.Height), 1));
            if (_buf.Length < n * outStride)
            {
                ContainerIo.Return(ref _buf);
                _buf = Pools<byte>.Pool.Rent(n * outStride);
            }

            for (int i = 0; i < n; i++)
            {
                rows.Slice(((from - y + i) * _fullStride) + (_r.X * _bpp), outStride).CopyTo(_buf.AsSpan(i * outStride, outStride));
            }

            _sink.WriteRows(from - _r.Y, _buf.AsSpan(0, n * outStride), n);
        }

        public void Dispose() => ContainerIo.Return(ref _buf);
    }

    private sealed class CompositeTarget : IPngRowTarget, IDisposable
    {
        private readonly PngInfo _st;
        private readonly PngFrameDesc _f;
        private readonly byte[] _canvas;
        private readonly int _stride;
        private byte[]? _tmp;

        public CompositeTarget(PngInfo st, PngFrameDesc f, byte[] canvas, int stride)
        {
            _st = st;
            _f = f;
            _canvas = canvas;
            _stride = stride;
        }

        public void Rows(int y, ReadOnlySpan<byte> rows, int count)
        {
            PixelFormat canvasFormat = _st.CanvasFormat;
            int cbpp = canvasFormat.BytesPerPixel;
            int nbpp = _st.Natural.BytesPerPixel;
            int w = _f.Width;
            bool convert = _st.Natural != canvasFormat;
            if (convert)
            {
                _tmp ??= Pools<byte>.Pool.Rent(w * cbpp);
            }

            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> line = rows.Slice(i * w * nbpp, w * nbpp);
                if (convert)
                {
                    ConvertKernel.ConvertRow(line, _st.Natural, _tmp, canvasFormat, w);
                    line = _tmp.AsSpan(0, w * cbpp);
                }

                Span<byte> dst = _canvas.AsSpan(((_f.Y + y + i) * _stride) + (_f.X * cbpp), w * cbpp);
                if (_f.Blend == 0)
                {
                    line.CopyTo(dst);
                }
                else if (cbpp == 4)
                {
                    PngPixels.Over8(line, dst, w);
                }
                else
                {
                    PngPixels.Over16(line, dst, w);
                }
            }
        }

        public void Dispose() => ContainerIo.Return(ref _tmp);
    }

    // ---- Header helpers ----

    // Whether the text chunk whose body starts at bodyStart is keyed "XML:com.adobe.xmp"; leaves the stream at bodyStart.
    private static bool HasXmpKeyword(Stream source, long bodyStart)
    {
        Span<byte> kw = stackalloc byte[17];
        ContainerIo.ReadFully(source, kw);
        source.Position = bodyStart;
        return kw.SequenceEqual("XML:com.adobe.xmp"u8);
    }

    // The body of a chunk whose CRC matched, or null when it did not.
    private static byte[]? ReadBodyChecked(Stream s, long len, uint type, long streamLength)
    {
        byte[] body = ReadBody(s, len, type, streamLength, out bool ok);
        return ok ? body : null;
    }

    private static byte[] ReadBody(Stream s, long len, uint type, long streamLength, out bool crcOk)
    {
        if (len > streamLength - s.Position - 4)
        {
            throw new ImageFormatException("A chunk runs past the end of the file.");
        }

        byte[] body = new byte[len];
        ContainerIo.ReadFully(s, body);
        Span<byte> tail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(tail, type);
        ContainerIo.ReadFully(s, tail.Slice(4, 4));
        uint crc = Crc32.Update(Crc32.Compute(tail[..4]), body);
        crcOk = crc == ContainerIo.U32Be(tail[4..]);
        return body;
    }

    private static void CheckMetadataSize(long len, ImageLimits limits)
    {
        if (len > limits.MaxMetadataBytes)
        {
            throw new ImageLimitException($"A metadata chunk of {len} bytes exceeds the limit of {limits.MaxMetadataBytes}.");
        }
    }

    // iCCP: keyword, NUL, compression method, zlib data. A damaged profile is dropped, not an error.
    private static byte[]? ParseIccp(byte[] body, ImageLimits limits)
    {
        int nul = Array.IndexOf(body, (byte)0);
        if (nul < 1 || nul > 79 || nul + 2 >= body.Length || body[nul + 1] != 0)
        {
            return null;
        }

        try
        {
            return InflateBounded(body.AsSpan(nul + 2), limits);
        }
        catch (ImageFormatException)
        {
            return null;
        }
    }

    // XMP lives in iTXt (keyword, NUL, flag, method, language, NUL, translated keyword, NUL, text) or, older, in tEXt.
    private static byte[]? ParseXmp(byte[] body, bool itxt, ImageLimits limits)
    {
        int nul = Array.IndexOf(body, (byte)0);
        if (nul < 0)
        {
            return null;
        }

        if (!itxt)
        {
            return body.AsSpan(nul + 1).ToArray();
        }

        if (nul + 3 > body.Length)
        {
            return null;
        }

        byte compressed = body[nul + 1];
        int p = nul + 3;
        for (int skip = 0; skip < 2; skip++)
        {
            int z = body.AsSpan(p).IndexOf((byte)0);
            if (z < 0)
            {
                return null;
            }

            p += z + 1;
        }

        if (compressed == 0)
        {
            return body.AsSpan(p).ToArray();
        }

        try
        {
            return InflateBounded(body.AsSpan(p), limits);
        }
        catch (ImageFormatException)
        {
            return null;
        }
    }

    private static byte[] InflateBounded(ReadOnlySpan<byte> zdata, ImageLimits limits)
    {
        long cap = limits.MaxMetadataBytes;
        using var inf = new Inflater(true, cap, limits.MaxExpansionRatio);
        byte[] buf = new byte[Math.Min(cap, Math.Max(4096, (long)zdata.Length * 4))];
        int have = 0;
        while (true)
        {
            InflateStatus status = inf.Inflate(zdata, buf.AsSpan(have), out int consumed, out int written);
            zdata = zdata[consumed..];
            have += written;
            if (status == InflateStatus.Done)
            {
                return buf.AsSpan(0, have).ToArray();
            }

            if (status == InflateStatus.NeedInput)
            {
                throw new ImageFormatException("Compressed metadata is truncated.");
            }

            if (have == buf.Length)
            {
                if (have >= cap)
                {
                    throw new ImageLimitException("Decompressed metadata exceeds the metadata limit.");
                }

                Array.Resize(ref buf, (int)Math.Min(cap, (long)buf.Length * 2));
            }
        }
    }

    private static void Configure(PngInfo st, byte[]? plte, byte[]? trns, int depth, int colorType)
    {
        PngUnpackContext c = st.Unpack;
        c.BitDepth = depth;
        int mask = (1 << Math.Min(depth, 16)) - 1;
        bool key = false;
        if (colorType == 0 && trns is { Length: 2 })
        {
            c.KeyR = ContainerIo.U16Be(trns) & mask;
            key = true;
        }
        else if (colorType == 2 && trns is { Length: 6 })
        {
            c.KeyR = ContainerIo.U16Be(trns) & mask;
            c.KeyG = ContainerIo.U16Be(trns.AsSpan(2)) & mask;
            c.KeyB = ContainerIo.U16Be(trns.AsSpan(4)) & mask;
            key = true;
        }

        switch (colorType)
        {
            case 0:
                if (depth == 16)
                {
                    st.Natural = key ? PixelFormat.Rgba16 : PixelFormat.Gray16;
                    c.Mode = key ? PngUnpackMode.Gray16ToRgba : PngUnpackMode.Swap16;
                }
                else
                {
                    st.Natural = key ? PixelFormat.GrayAlpha8 : PixelFormat.Gray8;
                    c.Mode = PngUnpackMode.LowGray;
                }

                break;
            case 2:
                if (depth == 16)
                {
                    st.Natural = key ? PixelFormat.Rgba16 : PixelFormat.Rgb16;
                    c.Mode = key ? PngUnpackMode.Rgb16Key : PngUnpackMode.Swap16;
                }
                else
                {
                    st.Natural = key ? PixelFormat.Rgba8 : PixelFormat.Rgb8;
                    c.Mode = key ? PngUnpackMode.Rgb8Key : PngUnpackMode.Copy;
                }

                break;
            case 3:
                {
                    byte[] pal = c.Palette;
                    for (int i = 0; i < 256; i++)
                    {
                        pal[(i * 4) + 3] = 255;
                    }

                    int entries = plte!.Length / 3;
                    for (int i = 0; i < Math.Min(entries, 256); i++)
                    {
                        pal[i * 4] = plte[i * 3];
                        pal[(i * 4) + 1] = plte[(i * 3) + 1];
                        pal[(i * 4) + 2] = plte[(i * 3) + 2];
                    }

                    if (trns is { Length: > 0 })
                    {
                        for (int i = 0; i < Math.Min(trns.Length, 256); i++)
                        {
                            pal[(i * 4) + 3] = trns[i];
                        }

                        c.PaletteHasAlpha = true;
                    }

                    st.Natural = c.PaletteHasAlpha ? PixelFormat.Rgba8 : PixelFormat.Rgb8;
                    c.Mode = PngUnpackMode.Palette;
                    break;
                }

            case 4:
                if (depth == 16)
                {
                    st.Natural = PixelFormat.Rgba16;
                    c.Mode = PngUnpackMode.Gray16ToRgba;
                    c.HasAlphaSample = true;
                }
                else
                {
                    st.Natural = PixelFormat.GrayAlpha8;
                    c.Mode = PngUnpackMode.Copy;
                }

                break;
            default:
                st.Natural = depth == 16 ? PixelFormat.Rgba16 : PixelFormat.Rgba8;
                c.Mode = depth == 16 ? PngUnpackMode.Swap16 : PngUnpackMode.Copy;
                break;
        }
    }
}
