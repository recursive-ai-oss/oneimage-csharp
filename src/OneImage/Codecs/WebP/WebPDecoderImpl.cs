using System.Buffers;

namespace OneImage.Codecs.WebP;

/// <summary>Decoder for WebP: lossy (VP8), lossless (VP8L), alpha and animation.</summary>
internal sealed class WebPDecoderImpl : IImageDecoder
{
    private readonly IImageFormat _format;

    public WebPDecoderImpl(IImageFormat format) => _format = format;

    public DecoderCapabilities Capabilities => DecoderCapabilities.None;

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        WebPFileInfo file = WebPContainer.Parse(source, limits);
        var frames = new List<FrameInfo>(file.Frames.Count);
        if (file.Animated)
        {
            foreach (WebPFrameRecord f in file.Frames)
            {
                frames.Add(new FrameInfo(file.CanvasWidth, file.CanvasHeight, PixelFormat.Rgba8, TimeSpan.FromMilliseconds(f.DurationMs)));
            }
        }
        else
        {
            frames.Add(new FrameInfo(file.CanvasWidth, file.CanvasHeight, file.HasAlpha ? PixelFormat.Rgba8 : PixelFormat.Rgb8));
        }

        var state = new DecoderState(file);
        var info = new ImageInfo(_format, frames, file.LoopCount, file.Metadata, state);
        limits.Check(info);
        return info;
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        var state = (DecoderState)info.DecoderState!;
        WebPFileInfo file = state.File;
        if ((uint)frameIndex >= (uint)file.Frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        if (!file.Animated)
        {
            DecodeFrameImage(source, file.Frames[0], sink, limits, cancellationToken);
            return;
        }

        lock (state)
        {
            AnimationCanvas c = state.Canvas ??= new AnimationCanvas(file.CanvasWidth, file.CanvasHeight);
            if (frameIndex < c.Last)
            {
                c.Reset();
            }

            while (c.Last < frameIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int i = c.Last + 1;
                if (i > 0 && file.Frames[i - 1].DisposeToBackground)
                {
                    c.ClearRect(file.Frames[i - 1]);
                }

                WebPFrameRecord f = file.Frames[i];
                byte[] tmp = Pools<byte>.Pool.Rent(checked(f.Width * f.Height * 4));
                try
                {
                    var buf = new BufferSink(f.Width, f.Height, PixelFormat.Rgba8, tmp);
                    DecodeFrameImage(source, f, buf, limits, cancellationToken);
                    c.Draw(f, tmp);
                }
                finally
                {
                    Pools<byte>.Pool.Return(tmp);
                }

                c.Last = i;
            }

            int rowBytes = file.CanvasWidth * 4;
            for (int y = 0; y < file.CanvasHeight; y += 16)
            {
                int n = Math.Min(16, file.CanvasHeight - y);
                sink.WriteRows(y, c.Pixels.AsSpan(y * rowBytes, n * rowBytes), n);
            }
        }
    }

    // Decodes the bitstream of one frame into the sink, in the sink's pixel format (Rgb8 or Rgba8).
    private static void DecodeFrameImage(Stream source, WebPFrameRecord f, IRowSink sink, ImageLimits limits, CancellationToken token)
    {
        limits.CheckFrame(f.Width, f.Height);
        if (sink.Width != f.Width || sink.Height != f.Height)
        {
            throw new ImageOperationException("The sink size does not match the frame.");
        }

        byte[] data = Pools<byte>.Pool.Rent(f.DataLength);
        byte[]? alpha = null;
        try
        {
            source.Position = f.DataOffset;
            WebPContainer.ReadExactly(source, data.AsSpan(0, f.DataLength));
            if (f.Lossless)
            {
                using var dec = new VP8LDecoder(data, 0, f.DataLength, limits, token);
                var adapter = new ArgbToRowSink(sink);
                dec.DecodeWithHeader(adapter, out _, out _);
                adapter.Flush();
                return;
            }

            if (f.AlphaLength > 0)
            {
                alpha = Pools<byte>.Pool.Rent(f.AlphaLength);
                source.Position = f.AlphaOffset;
                WebPContainer.ReadExactly(source, alpha.AsSpan(0, f.AlphaLength));
            }

            VP8Decoder.Decode(data, f.DataLength, alpha, f.AlphaLength, sink, limits, token);
        }
        finally
        {
            Pools<byte>.Pool.Return(data);
            if (alpha != null)
            {
                Pools<byte>.Pool.Return(alpha);
            }
        }
    }

    private sealed class DecoderState
    {
        public DecoderState(WebPFileInfo file) => File = file;

        public WebPFileInfo File { get; }

        public AnimationCanvas? Canvas { get; set; }
    }

    // The canvas an animation is composed on; the decoder keeps the state after the last composited frame so that
    // reading frames in order costs one decode per frame.
    private sealed class AnimationCanvas
    {
        private readonly int _width;
        private readonly int _height;

        public AnimationCanvas(int width, int height)
        {
            _width = width;
            _height = height;
            Pixels = new byte[checked(width * height * 4)];
        }

        public byte[] Pixels { get; }

        public int Last { get; set; } = -1;

        public void Reset()
        {
            Array.Clear(Pixels);
            Last = -1;
        }

        public void ClearRect(WebPFrameRecord f)
        {
            for (int y = 0; y < f.Height; y++)
            {
                Pixels.AsSpan((((f.Y + y) * _width) + f.X) * 4, f.Width * 4).Clear();
            }
        }

        public void Draw(WebPFrameRecord f, byte[] src)
        {
            bool blend = f.Blend && f.HasAlpha;
            for (int y = 0; y < f.Height; y++)
            {
                Span<byte> dst = Pixels.AsSpan((((f.Y + y) * _width) + f.X) * 4, f.Width * 4);
                ReadOnlySpan<byte> row = src.AsSpan(y * f.Width * 4, f.Width * 4);
                if (!blend)
                {
                    row.CopyTo(dst);
                    continue;
                }

                for (int x = 0; x < f.Width * 4; x += 4)
                {
                    BlendPixel(row.Slice(x, 4), dst.Slice(x, 4));
                }
            }
        }

        // Non-premultiplied source-over in 8-bit integer arithmetic (sRGB values, no linearization).
        private static void BlendPixel(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int sa = src[3];
            if (sa == 255 || dst[3] == 0)
            {
                src.CopyTo(dst);
                return;
            }

            if (sa == 0)
            {
                return;
            }

            int da = dst[3];
            int dstFactor = (da * (256 - sa)) >> 8;
            int blendA = sa + dstFactor;
            uint scale = (1u << 24) / (uint)blendA;
            for (int c = 0; c < 3; c++)
            {
                uint v = (uint)((src[c] * sa) + (dst[c] * dstFactor));
                dst[c] = (byte)((v * scale) >> 24);
            }

            dst[3] = (byte)blendA;
        }
    }

    // Collects rows into a caller-owned buffer.
    private sealed class BufferSink : IRowSink
    {
        private readonly byte[] _buffer;

        public BufferSink(int width, int height, PixelFormat format, byte[] buffer)
        {
            Width = width;
            Height = height;
            Format = format;
            _buffer = buffer;
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format { get; }

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount) =>
            rows[..(rowCount * Width * Format.BytesPerPixel)].CopyTo(_buffer.AsSpan(y * Width * Format.BytesPerPixel));
    }

    // Converts decoded ARGB rows to the sink's byte layout in batches.
    private sealed class ArgbToRowSink : IArgbRowSink
    {
        private const int Batch = 16;
        private readonly IRowSink _sink;
        private readonly int _bpp;
        private readonly byte[] _buffer;
        private int _start;
        private int _count;

        public ArgbToRowSink(IRowSink sink)
        {
            _sink = sink;
            _bpp = sink.Format.BytesPerPixel;
            _buffer = new byte[checked(sink.Width * _bpp * Batch)];
        }

        public void WriteRow(int y, ReadOnlySpan<uint> argb)
        {
            if (_count == 0)
            {
                _start = y;
            }

            Span<byte> dst = _buffer.AsSpan(_count * _sink.Width * _bpp, _sink.Width * _bpp);
            if (_bpp == 4)
            {
                for (int x = 0; x < argb.Length; x++)
                {
                    uint p = argb[x];
                    dst[x * 4] = (byte)(p >> 16);
                    dst[(x * 4) + 1] = (byte)(p >> 8);
                    dst[(x * 4) + 2] = (byte)p;
                    dst[(x * 4) + 3] = (byte)(p >> 24);
                }
            }
            else
            {
                for (int x = 0; x < argb.Length; x++)
                {
                    uint p = argb[x];
                    dst[x * 3] = (byte)(p >> 16);
                    dst[(x * 3) + 1] = (byte)(p >> 8);
                    dst[(x * 3) + 2] = (byte)p;
                }
            }

            if (++_count == Batch)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (_count > 0)
            {
                _sink.WriteRows(_start, _buffer.AsSpan(0, _count * _sink.Width * _bpp), _count);
                _count = 0;
            }
        }
    }
}
