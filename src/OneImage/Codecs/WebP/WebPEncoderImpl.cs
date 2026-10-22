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
            EncodedFrame f = EncodeFrame(first, 0, 0, first.Width, first.Height, null, options, cancellationToken);
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
                EncodedFrame f = EncodeSub(sub, w, h, options, cancellationToken);
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

        foreach ((uint fourCc, byte[] data) in chunks)
        {
            AppendChunk(output, fourCc, data);
        }

        if (needExtended)
        {
            if (exifBlob.Length > 0)
            {
                AppendChunk(output, Exif, exifBlob);
            }

            if (meta.Xmp.Length > 0)
            {
                AppendChunk(output, Xmp, meta.Xmp.ToArray());
            }
        }

        byte[] file = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        destination.Write(file);
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

    private static EncodedFrame EncodeFrame(ImageFrameReader fr, int x, int y, int w, int h, byte[]? unused, WebPEncoderOptions options, CancellationToken token)
    {
        _ = x;
        _ = y;
        _ = unused;
        byte[] rgba = ReadRgba(fr);
        return EncodeSub(rgba, w, h, options, token);
    }

    private static EncodedFrame EncodeSub(byte[] rgba, int w, int h, WebPEncoderOptions options, CancellationToken token)
    {
        bool hasAlpha = false;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 255)
            {
                hasAlpha = true;
                break;
            }
        }

        if (options.Lossless)
        {
            uint[] argb = new uint[w * h];
            for (int i = 0; i < argb.Length; i++)
            {
                uint r = rgba[i * 4];
                uint g = rgba[(i * 4) + 1];
                uint b = rgba[(i * 4) + 2];
                uint a = rgba[(i * 4) + 3];
                if (a == 0 && !options.Exact)
                {
                    r = g = b = 0;
                }

                argb[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }

            var lo = new VP8LEncodeOptions { Effort = options.Effort };
            byte[] data = VP8LEncoder.Encode(argb, w, h, hasAlpha, lo, token);
            return new EncodedFrame { Data = data, Lossless = true, HasAlpha = hasAlpha };
        }

        return LossyEncode(rgba, w, h, hasAlpha, options, token);
    }

    private static EncodedFrame LossyEncode(byte[] rgba, int w, int h, bool hasAlpha, WebPEncoderOptions options, CancellationToken token)
    {
        byte[] data = VP8Encoder.Encode(rgba, w, h, options.Quality, options.Effort, token);
        byte[]? alpha = null;
        if (hasAlpha)
        {
            alpha = EncodeAlpha(rgba, w, h, options.Effort, token);
        }

        return new EncodedFrame { Data = data, Alpha = alpha, Lossless = false, HasAlpha = hasAlpha };
    }

    // Alpha of a lossy image: a prediction-filtered plane coded with the lossless coder, or stored raw when that is smaller.
    private static byte[] EncodeAlpha(byte[] rgba, int w, int h, int effort, CancellationToken token)
    {
        byte[] plane = new byte[w * h];
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = rgba[(i * 4) + 3];
        }

        byte[]? best = null;
        int bestFilter = 0;
        byte[] filtered = new byte[plane.Length];
        var lo = new VP8LEncodeOptions { Effort = Math.Min(effort, 4) };
        for (int filter = 0; filter < 4; filter++)
        {
            if (effort < 2 && filter != 0 && filter != 3)
            {
                continue;
            }

            ApplyAlphaFilter(filter, plane, filtered, w, h);
            byte[] enc = VP8LEncoder.EncodePlane(filtered, w, h, lo, token);
            if (best == null || enc.Length < best.Length)
            {
                best = enc;
                bestFilter = filter;
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
