using System.Buffers.Binary;

namespace OneImage.Codecs.WebP;

/// <summary>Encoder for WebP: lossless (VP8L), lossy (VP8) with lossless alpha, and animation.</summary>
internal sealed class WebPEncoderImpl : IImageEncoder
{
    public IReadOnlyList<PixelFormat> SupportedFormats { get; } = [PixelFormat.Rgba8, PixelFormat.Rgb8];

    public bool SupportsMultipleFrames => true;

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        var options = request.Options as WebPEncoderOptions ?? new WebPEncoderOptions();
        if (options.Quality is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "WebP quality must be between 0 and 100.");
        }

        if (options.Effort is < 0 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "WebP effort must be between 0 and 6.");
        }

        IReadOnlyList<ImageFrameReader> frames = request.Frames;
        ImageFrameReader first = frames[0];
        if (first.Width > 16383 || first.Height > 16383)
        {
            throw new ImageOperationException("WebP images are limited to 16383 pixels per side.");
        }

        ImageMetadata meta = request.Metadata;
        // The core has already corrected the EXIF dimension and orientation tags for this output.
        byte[] exifBlob = meta.Exif.ToArray();
        bool animated = frames.Count > 1;
        var chunks = new List<(uint fourCc, byte[] data)>();
        bool anyAlpha = false;

        if (!animated)
        {
            EncodedFrame f = EncodeFrame(first, options, cancellationToken);
            anyAlpha = f.HasAlpha;
            if (f.Alpha != null)
            {
                chunks.Add((Alph, f.Alpha));
            }

            chunks.Add((f.Lossless ? Vp8L : Vp8, f.Data));
        }
        else
        {
            byte[] canvas = new byte[checked(first.Width * first.Height * 4)];
            byte[]? previous = null;
            foreach (ImageFrameReader fr in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fr.Width != first.Width || fr.Height != first.Height)
                {
                    throw new ImageOperationException("All frames of a WebP animation must have the same size.");
                }

                byte[] rgba = ReadRgba(fr);
                ChangedRect(previous, rgba, fr.Width, fr.Height, out int x, out int y, out int w, out int h);
                byte[] sub = Crop(rgba, fr.Width, x, y, w, h);
                EncodedFrame f = EncodeSub(sub, w, h, options, first.Degree, cancellationToken);
                anyAlpha |= f.HasAlpha;
                var payload = new List<byte>(16 + f.Data.Length + (f.Alpha?.Length ?? 0) + 24);
                WriteUInt24(payload, x / 2);
                WriteUInt24(payload, y / 2);
                WriteUInt24(payload, w - 1);
                WriteUInt24(payload, h - 1);
                WriteUInt24(payload, (int)Math.Clamp((long)fr.Delay.TotalMilliseconds, 0, 0xffffff));
                payload.Add(0x02); // do not blend (the rectangle overwrites), do not dispose
                if (f.Alpha != null)
                {
                    AppendChunk(payload, Alph, f.Alpha);
                }

                AppendChunk(payload, f.Lossless ? Vp8L : Vp8, f.Data);
                chunks.Add((Anmf, payload.ToArray()));
                previous = rgba;
            }

            _ = canvas;
        }

        bool needExtended = animated || meta.IccProfile.Length > 0 || exifBlob.Length > 0 || meta.Xmp.Length > 0 || chunks.Exists(c => c.fourCc == Alph);
        var output = new List<byte>(1024);
        output.AddRange("RIFF"u8);
        output.AddRange(new byte[4]);
        output.AddRange("WEBP"u8);
        if (needExtended)
        {
            byte flags = 0;
            if (meta.IccProfile.Length > 0)
            {
                flags |= 0x20;
            }

            if (anyAlpha)
            {
                flags |= 0x10;
            }

            if (exifBlob.Length > 0)
            {
                flags |= 0x08;
            }

            if (meta.Xmp.Length > 0)
            {
                flags |= 0x04;
            }

            if (animated)
            {
                flags |= 0x02;
            }

            var vp8x = new List<byte>(10) { flags, 0, 0, 0 };
            WriteUInt24(vp8x, first.Width - 1);
            WriteUInt24(vp8x, first.Height - 1);
            AppendChunk(output, Vp8X, vp8x.ToArray());
            if (meta.IccProfile.Length > 0)
            {
                AppendChunk(output, Iccp, meta.IccProfile.ToArray());
            }

            if (animated)
            {
                var anim = new List<byte>(6) { 0, 0, 0, 0 };
                anim.Add((byte)(Math.Clamp(request.LoopCount, 0, 65535) & 0xff));
                anim.Add((byte)(Math.Clamp(request.LoopCount, 0, 65535) >> 8));
                AppendChunk(output, Anim, anim.ToArray());
            }
        }

        // The image chunks can be large, so they go to the destination as they are, not through a second copy of the whole file.
        var tail = new List<byte>(16);
        if (needExtended)
        {
            if (exifBlob.Length > 0)
            {
                AppendChunk(tail, Exif, exifBlob);
            }

            if (meta.Xmp.Length > 0)
            {
                AppendChunk(tail, Xmp, meta.Xmp.ToArray());
            }
        }

        long total = output.Count + tail.Count;
        foreach ((_, byte[] data) in chunks)
        {
            total += 8 + data.Length + (data.Length & 1);
        }

        byte[] head = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), checked((uint)(total - 8)));
        destination.Write(head);
        Span<byte> padding = stackalloc byte[1];
        Span<byte> header = stackalloc byte[8];
        foreach ((uint fourCc, byte[] data) in chunks)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header, fourCc);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);
            destination.Write(header);
            destination.Write(data);
            if ((data.Length & 1) != 0)
            {
                destination.Write(padding);
            }
        }

        destination.Write(tail.ToArray());
    }

    private const uint Vp8 = 0x20385056;
    private const uint Vp8L = 0x4c385056;
    private const uint Vp8X = 0x58385056;
    private const uint Alph = 0x48504c41;
    private const uint Anim = 0x4d494e41;
    private const uint Anmf = 0x464d4e41;
    private const uint Iccp = 0x50434349;
    private const uint Exif = 0x46495845;
    private const uint Xmp = 0x20504d58;

    private sealed class EncodedFrame
    {
        public byte[] Data = [];
        public byte[]? Alpha { get; set; }
        public bool Lossless;
        public bool HasAlpha;
    }

    private static void WriteUInt24(List<byte> list, int v)
    {
        list.Add((byte)v);
        list.Add((byte)(v >> 8));
        list.Add((byte)(v >> 16));
    }

    private static void AppendChunk(List<byte> output, uint fourCc, byte[] data)
    {
        Span<byte> h = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(h, fourCc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)data.Length);
        foreach (byte b in h)
        {
            output.Add(b);
        }

        output.AddRange(data);
        if ((data.Length & 1) != 0)
        {
            output.Add(0);
        }
    }

    // Reads the frame as straight RGBA.
    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static byte[] ReadRgba(ImageFrameReader fr)
    {
        int w = fr.Width;
        int h = fr.Height;
        int bpp = fr.Format.BytesPerPixel;
        byte[] rgba = new byte[checked(w * h * 4)];
        byte[] strip = new byte[w * bpp * 16];
        for (int y = 0; y < h; y += 16)
        {
            int n = Math.Min(16, h - y);
            fr.ReadRows(y, n, strip);
            for (int r = 0; r < n; r++)
            {
                ReadOnlySpan<byte> src = strip.AsSpan(r * w * bpp, w * bpp);
                Span<byte> dst = rgba.AsSpan((y + r) * w * 4, w * 4);
                if (bpp == 4)
                {
                    src.CopyTo(dst);
                }
                else
                {
                    for (int x = 0; x < w; x++)
                    {
                        dst[x * 4] = src[x * 3];
                        dst[(x * 4) + 1] = src[(x * 3) + 1];
                        dst[(x * 4) + 2] = src[(x * 3) + 2];
                        dst[(x * 4) + 3] = 255;
                    }
                }
            }
        }

        return rgba;
    }

    // A still image is read band by band into the coder's own input form; see the two methods below.
    private static EncodedFrame EncodeFrame(ImageFrameReader fr, WebPEncoderOptions options, CancellationToken token) =>
        options.Lossless ? EncodeLosslessStreaming(fr, options, token) : EncodeLossyStreaming(fr, options, token);

    // A lossless still image is packed to 0xAARRGGBB words band by band, without the 4-bytes-per-pixel copy of the whole frame.
    private static EncodedFrame EncodeLosslessStreaming(ImageFrameReader fr, WebPEncoderOptions options, CancellationToken token)
    {
        int w = fr.Width;
        int h = fr.Height;
        int bpp = fr.Format.BytesPerPixel;
        int degree = fr.Degree;
        uint[] argb = new uint[checked(w * h)];
        int bandRows = Math.Max(16, Math.Min(h, (1 << 20) / w / 16 * 16));
        byte[] band = new byte[checked(w * bpp * bandRows)];
        bool translucent = false;
        for (int y = 0; y < h; y += bandRows)
        {
            token.ThrowIfCancellationRequested();
            int n = Math.Min(bandRows, h - y);
            fr.ReadRows(y, n, band);
            translucent |= PackArgb(band, bpp, argb, y * w, n * w, options.Exact, degree);
        }

        var lo = new VP8LEncodeOptions { Effort = options.Effort, Degree = degree };
        byte[] data = VP8LEncoder.EncodeArray(argb, w, h, translucent, lo, token);
        return new EncodedFrame { Data = data, Lossless = true, HasAlpha = translucent };
    }

    // A lossy still image goes from the frame's rows straight into the encoder's YUV planes, so the 4-bytes-per-pixel copy of the
    // whole frame that the animation and lossless paths need is never made. Only when a pixel turns out to be translucent is the
    // alpha channel gathered, one byte per pixel, for the alpha coder. The bytes written equal those of the whole-frame path.
    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static EncodedFrame EncodeLossyStreaming(ImageFrameReader fr, WebPEncoderOptions options, CancellationToken token)
    {
        int w = fr.Width;
        int h = fr.Height;
        int bpp = fr.Format.BytesPerPixel;
        int degree = fr.Degree;
        VP8Encoder enc = VP8Encoder.Create(w, h, options.Quality, options.Effort, token, degree);
        byte[] strip = new byte[checked(w * bpp * 16)];
        byte[]? alphaPlane = null;
        for (int y = 0; y < h; y += 16)
        {
            token.ThrowIfCancellationRequested();
            int n = Math.Min(16, h - y);
            fr.ReadRows(y, n, strip);
            enc.AddRows(strip, bpp, y, n);
            if (bpp != 4)
            {
                continue;
            }

            ReadOnlySpan<byte> px = strip.AsSpan(0, w * n * 4);
            if (alphaPlane == null)
            {
                bool opaque = true;
                for (int i = 3; i < px.Length; i += 4)
                {
                    if (px[i] != 255)
                    {
                        opaque = false;
                        break;
                    }
                }

                if (opaque)
                {
                    continue;
                }

                alphaPlane = new byte[checked(w * h)];
                alphaPlane.AsSpan(0, y * w).Fill(255);
            }

            Span<byte> dst = alphaPlane.AsSpan(y * w, w * n);
            for (int i = 0; i < dst.Length; i++)
            {
                dst[i] = px[(i * 4) + 3];
            }
        }

        bool hasAlpha = alphaPlane != null;
        Task<byte[]>? alphaTask = hasAlpha && degree > 1 ? Task.Run(() => EncodeAlpha(alphaPlane!, w, h, options.Effort, degree, token), token) : null;
        byte[] data = enc.Finish();
        byte[]? alpha = null;
        if (hasAlpha)
        {
            alpha = alphaTask != null ? alphaTask.GetAwaiter().GetResult() : EncodeAlpha(alphaPlane!, w, h, options.Effort, degree, token);
        }

        return new EncodedFrame { Data = data, Alpha = alpha, Lossless = false, HasAlpha = hasAlpha };
    }

    // Loops that run for the whole call: compiled fully optimized on the first call, so no unoptimized-then-recompiled copies (and their resident memory) in the first calls.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static EncodedFrame EncodeSub(byte[] rgba, int w, int h, WebPEncoderOptions options, int degree, CancellationToken token)
    {
        if (options.Lossless)
        {
            // Packing the pixels also finds out whether any is translucent.
            uint[] argb = new uint[w * h];
            bool translucent = PackArgb(rgba, 4, argb, 0, argb.Length, options.Exact, degree);
            var lo = new VP8LEncodeOptions { Effort = options.Effort, Degree = degree };
            byte[] data = VP8LEncoder.EncodeArray(argb, w, h, translucent, lo, token);
            return new EncodedFrame { Data = data, Lossless = true, HasAlpha = translucent };
        }

        bool hasAlpha = false;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 255)
            {
                hasAlpha = true;
                break;
            }
        }

        return LossyEncode(rgba, w, h, hasAlpha, options, degree, token);
    }

    // Converts RGBA bytes to 0xAARRGGBB words (clearing the color of fully transparent pixels unless exact) and reports whether any alpha is below 255.
    // The pixels are bpp bytes each (3 = opaque RGB) in src, from its start; count of them land in argb from offset.
    private static bool PackArgb(byte[] src, int bpp, uint[] argb, int offset, int count, bool exact, int degree)
    {
        const int Chunk = 1 << 16;
        int chunks = (count + Chunk - 1) / Chunk;
        int translucent = 0;
        void Run(int c)
        {
            int end = Math.Min(count, (c + 1) * Chunk);
            bool any = false;
            for (int i = c * Chunk; i < end; i++)
            {
                uint r = src[i * bpp];
                uint g = src[(i * bpp) + 1];
                uint b = src[(i * bpp) + 2];
                uint a = bpp == 4 ? src[(i * 4) + 3] : 255u;
                any |= a != 255;
                if (a == 0 && !exact)
                {
                    r = g = b = 0;
                }

                argb[offset + i] = (a << 24) | (r << 16) | (g << 8) | b;
            }

            if (any)
            {
                Volatile.Write(ref translucent, 1);
            }
        }

        if (degree <= 1 || chunks < 4)
        {
            for (int c = 0; c < chunks; c++)
            {
                Run(c);
            }
        }
        else
        {
            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = degree }, Run);
        }

        return Volatile.Read(ref translucent) != 0;
    }

    private static EncodedFrame LossyEncode(byte[] rgba, int w, int h, bool hasAlpha, WebPEncoderOptions options, int degree, CancellationToken token)
    {
        // The alpha plane is coded on the side while the color planes are.
        Task<byte[]>? alphaTask = hasAlpha && degree > 1 ? Task.Run(() => EncodeAlpha(GatherAlpha(rgba), w, h, options.Effort, degree, token), token) : null;
        byte[] data = VP8Encoder.Encode(rgba, w, h, options.Quality, options.Effort, token, degree);
        byte[]? alpha = null;
        if (hasAlpha)
        {
            alpha = alphaTask != null ? alphaTask.GetAwaiter().GetResult() : EncodeAlpha(GatherAlpha(rgba), w, h, options.Effort, degree, token);
        }

        return new EncodedFrame { Data = data, Alpha = alpha, Lossless = false, HasAlpha = hasAlpha };
    }

    private static byte[] GatherAlpha(byte[] rgba)
    {
        byte[] plane = new byte[rgba.Length / 4];
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = rgba[(i * 4) + 3];
        }

        return plane;
    }

    // Alpha of a lossy image: a prediction-filtered plane coded with the lossless coder, or stored raw when that is smaller.
    private static byte[] EncodeAlpha(byte[] plane, int w, int h, int effort, int degree, CancellationToken token)
    {
        var lo = new VP8LEncodeOptions { Effort = Math.Min(effort, 4) };
        int[] filters = effort < 2 ? [0, 3] : [0, 1, 2, 3];
        byte[][] encoded = new byte[filters.Length][];
        void Candidate(int k)
        {
            byte[] filtered = new byte[plane.Length];
            ApplyAlphaFilter(filters[k], plane, filtered, w, h);
            encoded[k] = VP8LEncoder.EncodePlane(filtered, w, h, lo, token);
        }

        if (degree > 1)
        {
            Parallel.For(0, filters.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(degree, filters.Length), CancellationToken = token }, Candidate);
        }
        else
        {
            for (int k = 0; k < filters.Length; k++)
            {
                Candidate(k);
            }
        }

        // The smallest wins; on a tie the earlier filter does.
        byte[]? best = null;
        int bestFilter = 0;
        for (int k = 0; k < filters.Length; k++)
        {
            if (best == null || encoded[k].Length < best.Length)
            {
                best = encoded[k];
                bestFilter = filters[k];
            }
        }

        byte[] result;
        if (best!.Length >= plane.Length)
        {
            result = new byte[1 + plane.Length];
            plane.CopyTo(result, 1);
        }
        else
        {
            result = new byte[1 + best.Length];
            result[0] = (byte)(1 | (bestFilter << 2));
            best.CopyTo(result, 1);
        }

        return result;
    }

    private static void ApplyAlphaFilter(int filter, byte[] src, byte[] dst, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                int pred;
                if (filter == 0)
                {
                    pred = 0;
                }
                else if (y == 0)
                {
                    pred = x == 0 ? 0 : src[i - 1];
                }
                else if (x == 0)
                {
                    pred = src[i - w];
                }
                else if (filter == 1)
                {
                    pred = src[i - 1];
                }
                else if (filter == 2)
                {
                    pred = src[i - w];
                }
                else
                {
                    pred = Math.Clamp(src[i - 1] + src[i - w] - src[i - w - 1], 0, 255);
                }

                dst[i] = (byte)(src[i] - pred);
            }
        }
    }

    // Finds the smallest even-aligned rectangle covering the pixels that differ from the previous frame.
    private static void ChangedRect(byte[]? prev, byte[] cur, int w, int h, out int x, out int y, out int rw, out int rh)
    {
        if (prev == null)
        {
            x = 0;
            y = 0;
            rw = w;
            rh = h;
            return;
        }

        int minX = w;
        int minY = h;
        int maxX = -1;
        int maxY = -1;
        for (int yy = 0; yy < h; yy++)
        {
            ReadOnlySpan<byte> a = prev.AsSpan(yy * w * 4, w * 4);
            ReadOnlySpan<byte> b = cur.AsSpan(yy * w * 4, w * 4);
            if (a.SequenceEqual(b))
            {
                continue;
            }

            minY = Math.Min(minY, yy);
            maxY = yy;
            int first = a.CommonPrefixLength(b) / 4;
            int last = w - 1;
            while (last > first && a.Slice(last * 4, 4).SequenceEqual(b.Slice(last * 4, 4)))
            {
                last--;
            }

            minX = Math.Min(minX, first);
            maxX = Math.Max(maxX, last);
        }

        if (maxX < 0)
        {
            // Identical frame: a single pixel keeps the file valid.
            minX = 0;
            minY = 0;
            maxX = 0;
            maxY = 0;
        }

        x = minX & ~1;
        y = minY & ~1;
        rw = maxX - x + 1;
        rh = maxY - y + 1;
    }

    private static byte[] Crop(byte[] rgba, int stride, int x, int y, int w, int h)
    {
        if (x == 0 && y == 0 && w * 4 == stride * 4 && rgba.Length == w * h * 4)
        {
            return rgba;
        }

        byte[] r = new byte[w * h * 4];
        for (int yy = 0; yy < h; yy++)
        {
            rgba.AsSpan((((y + yy) * stride) + x) * 4, w * 4).CopyTo(r.AsSpan(yy * w * 4, w * 4));
        }

        return r;
    }
}
