using System.Buffers.Binary;

namespace OneImage.Tests.Resample;

/// <summary>
/// Test-only format "SCL1" + width + height (int32 LE): a virtual smooth gradient image. The decoder can advertise shrink-on-load;
/// when it does, a scaled decode samples the same gradient at the centers of the denominator-sized blocks.
/// </summary>
internal sealed class GradientFormat : IImageFormat, IImageDecoder
{
    public GradientFormat(DecoderCapabilities capabilities) => Capabilities = capabilities;

    public string Name => "grad";

    public IReadOnlyList<string> Extensions { get; } = [".grad"];

    public string MimeType => "application/x-grad-test";

    public int HeaderLength => 4;

    public IImageDecoder? Decoder => this;

    public IImageEncoder? Encoder => null;

    public DecoderCapabilities Capabilities { get; }

    public int LastScale { get; private set; } = -1;

    public static byte[] File(int width, int height)
    {
        byte[] b = new byte[12];
        "SCL1"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), height);
        return b;
    }

    public static Rgba32 Pixel(int x, int y, int w, int h) =>
        new((byte)(255 * x / Math.Max(1, w - 1)), (byte)(255 * y / Math.Max(1, h - 1)), 100, 255);

    public ImageConfiguration Configuration() => new ImageConfiguration().Add(this);

    public bool Matches(ReadOnlySpan<byte> header) => header.Length >= 4 && header[..4].SequenceEqual("SCL1"u8);

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        Span<byte> h = stackalloc byte[12];
        source.Position = 0;
        source.ReadExactly(h);
        int w = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
        int ht = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
        limits.CheckFrame(w, ht);
        return new ImageInfo(this, [new FrameInfo(w, ht, PixelFormat.Rgba8)]);
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        LastScale = request.ScaleDenominator;
        FrameInfo f = info.Frames[frameIndex];
        int d = request.ScaleDenominator;
        ImageRect r = request.Region;
        byte[] row = new byte[r.Width * 4];
        for (int y = 0; y < r.Height; y++)
        {
            int sy = Math.Min(f.Height - 1, ((r.Y + y) * d) + (d / 2));
            for (int x = 0; x < r.Width; x++)
            {
                int sx = Math.Min(f.Width - 1, ((r.X + x) * d) + (d / 2));
                Rgba32 p = Pixel(sx, sy, f.Width, f.Height);
                row[x * 4] = p.R;
                row[(x * 4) + 1] = p.G;
                row[(x * 4) + 2] = p.B;
                row[(x * 4) + 3] = p.A;
            }

            sink.WriteRows(y, row, 1);
        }
    }
}
