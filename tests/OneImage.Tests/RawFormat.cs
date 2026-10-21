using System.Buffers.Binary;

namespace OneImage.Tests;

/// <summary>
/// Trivial test-only format: "RAW1", format byte (0 = Gray8, 1 = Rgba8), width, height, frame count, loop count (int32 LE each),
/// then per frame: delay in milliseconds (int32) and the raw rows.
/// </summary>
internal sealed class RawFormat : IImageFormat, IImageDecoder, IImageEncoder
{
    private const int HeaderSize = 21;
    private readonly bool _checkLimitsInDecoder;
    private int _decodeCalls;
    private DecodeRequest _lastRequest;

    public RawFormat(DecoderCapabilities capabilities = DecoderCapabilities.None, bool checkLimitsInDecoder = true)
    {
        Capabilities = capabilities;
        _checkLimitsInDecoder = checkLimitsInDecoder;
    }

    public string Name => "raw";

    public IReadOnlyList<string> Extensions { get; } = [".raw"];

    public string MimeType => "application/x-raw-test";

    public int HeaderLength => 4;

    public IImageDecoder? Decoder => this;

    public IImageEncoder? Encoder => this;

    public DecoderCapabilities Capabilities { get; }

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } = [PixelFormat.Gray8, PixelFormat.Rgba8];

    public bool SupportsMultipleFrames => true;

    public int DecodeCalls => Volatile.Read(ref _decodeCalls);

    public DecodeRequest LastRequest => _lastRequest;

    public ImageConfiguration Configuration() => new ImageConfiguration().Add(this);

    public bool Matches(ReadOnlySpan<byte> header) => header.Length >= 4 && header[..4].SequenceEqual("RAW1"u8);

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        Span<byte> h = stackalloc byte[HeaderSize];
        source.Position = 0;
        source.ReadExactly(h);
        PixelFormat fmt = h[4] == 0 ? PixelFormat.Gray8 : PixelFormat.Rgba8;
        int w = BinaryPrimitives.ReadInt32LittleEndian(h[5..]);
        int ht = BinaryPrimitives.ReadInt32LittleEndian(h[9..]);
        int frames = BinaryPrimitives.ReadInt32LittleEndian(h[13..]);
        int loop = BinaryPrimitives.ReadInt32LittleEndian(h[17..]);
        if (_checkLimitsInDecoder)
        {
            limits.CheckFrame(w, ht);
            if (frames > limits.MaxFrames)
            {
                throw new ImageLimitException("too many frames");
            }
        }

        if (frames is < 1 or > 100_000)
        {
            throw new ImageFormatException("bad frame count");
        }

        var list = new List<FrameInfo>();
        for (int i = 0; i < frames; i++)
        {
            int delay = 0;
            long pos = HeaderSize + (i * (4L + ((long)w * ht * fmt.BytesPerPixel)));
            if (source.CanSeek && pos + 4 <= source.Length)
            {
                source.Position = pos;
                byte[] d = new byte[4];
                source.ReadExactly(d);
                delay = BinaryPrimitives.ReadInt32LittleEndian(d);
            }

            list.Add(new FrameInfo(w, ht, fmt, TimeSpan.FromMilliseconds(delay)));
        }

        return new ImageInfo(this, list, loop);
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _decodeCalls);
        _lastRequest = request;
        FrameInfo f = info.Frames[frameIndex];
        int bpp = f.Format.BytesPerPixel;
        long frameStart = HeaderSize + (frameIndex * (4L + ((long)f.Width * f.Height * bpp))) + 4;
        ImageRect r = request.Region;
        byte[] row = new byte[r.Width * bpp];
        for (int y = 0; y < r.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long pos = frameStart + ((((long)r.Y + y) * f.Width) + r.X) * bpp;
            if (pos + row.Length > source.Length)
            {
                throw new ImageFormatException("truncated raw data");
            }

            source.Position = pos;
            source.ReadExactly(row);
            sink.WriteRows(y, row, 1);
        }
    }

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ImageFrameReader first = request.Frames[0];
        Span<byte> h = stackalloc byte[HeaderSize];
        "RAW1"u8.CopyTo(h);
        h[4] = first.Format == PixelFormat.Gray8 ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt32LittleEndian(h[5..], first.Width);
        BinaryPrimitives.WriteInt32LittleEndian(h[9..], first.Height);
        BinaryPrimitives.WriteInt32LittleEndian(h[13..], request.Frames.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[17..], request.LoopCount);
        destination.Write(h);
        foreach (ImageFrameReader frame in request.Frames)
        {
            byte[] d = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(d, (int)frame.Delay.TotalMilliseconds);
            destination.Write(d);
            int bpp = frame.Format.BytesPerPixel;
            byte[] strip = new byte[frame.Width * bpp * 16];
            for (int y = 0; y < frame.Height; y += 16)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = Math.Min(16, frame.Height - y);
                frame.ReadRows(y, n, strip);
                destination.Write(strip, 0, n * frame.Width * bpp);
            }
        }
    }

    /// <summary>Builds a raw file with the given header values and no pixel data (for limit tests).</summary>
    public static byte[] HeaderOnly(int width, int height, int frames = 1)
    {
        byte[] h = new byte[HeaderSize];
        "RAW1"u8.CopyTo(h);
        h[4] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(5), width);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(9), height);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(13), frames);
        return h;
    }
}
