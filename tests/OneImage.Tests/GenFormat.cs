using System.Buffers.Binary;

namespace OneImage.Tests;

/// <summary>
/// Test-only format "GEN1" (magic, width, height as int32 LE) whose pixels are a formula of the coordinates, produced row by row
/// by a sequential decoder, so huge images need no file. The encoder consumes rows through a sequential reader and verifies them
/// against the formula, shifted by <see cref="GenCheck.OffsetX"/>/<see cref="GenCheck.OffsetY"/> and optionally inverted.
/// </summary>
internal sealed class GenFormat : IImageFormat, IImageDecoder, IImageEncoder
{
    public static GenFormat Instance { get; } = new();

    public string Name => "gen";

    public IReadOnlyList<string> Extensions { get; } = [".gen"];

    public string MimeType => "application/x-gen-test";

    public int HeaderLength => 4;

    public IImageDecoder? Decoder => this;

    public IImageEncoder? Encoder => this;

    public DecoderCapabilities Capabilities => DecoderCapabilities.None;

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } = [PixelFormat.Rgba8];

    public bool SupportsMultipleFrames => false;

    public ImageConfiguration Configuration() => new ImageConfiguration().Add(this);

    public static byte[] Header(int width, int height)
    {
        byte[] h = new byte[12];
        "GEN1"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), height);
        return h;
    }

    public static Rgba32 Pixel(int x, int y) => new((byte)((x * 3) + y), (byte)(x + (y * 5)), (byte)(x ^ y), 255);

    public bool Matches(ReadOnlySpan<byte> header) => header.Length >= 4 && header[..4].SequenceEqual("GEN1"u8);

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        byte[] h = new byte[12];
        source.Position = 0;
        source.ReadExactly(h);
        int w = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(4));
        int ht = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8));
        limits.CheckFrame(w, ht);
        return new ImageInfo(this, [new FrameInfo(w, ht, PixelFormat.Rgba8)]);
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        int w = info.Width;
        int h = info.Height;
        const int Band = 16;
        Rgba32[] rows = new Rgba32[w * Band];
        for (int y = 0; y < h; y += Band)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Math.Min(Band, h - y);
            for (int r = 0; r < n; r++)
            {
                for (int x = 0; x < w; x++)
                {
                    rows[(r * w) + x] = Pixel(x, y + r);
                }
            }

            sink.WriteRows(y, OneImage.Kernels.SpanCast.AsBytes(rows.AsSpan(0, n * w)), n);
        }
    }

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        var check = (GenCheck)request.Options!;
        ImageFrameReader frame = request.Frames[0];
        const int Band = 16;
        byte[] buf = new byte[frame.Width * 4 * Band];
        for (int y = 0; y < frame.Height; y += Band)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Math.Min(Band, frame.Height - y);
            frame.ReadRows(y, n, buf);
            OneImage.Kernels.SpanCast.As<Rgba32>(buf.AsSpan(0, n * frame.Width * 4)).CopyTo(check.Scratch.AsSpan());
            for (int r = 0; r < n; r++)
            {
                for (int x = 0; x < frame.Width; x++)
                {
                    Rgba32 e = Pixel(x + check.OffsetX, y + r + check.OffsetY);
                    if (check.Inverted)
                    {
                        e = new Rgba32((byte)(255 - e.R), (byte)(255 - e.G), (byte)(255 - e.B), e.A);
                    }

                    if (check.Scratch[(r * frame.Width) + x] != e)
                    {
                        check.Mismatches++;
                    }
                }
            }

            check.RowsSeen += n;
        }
    }
}

internal sealed class GenCheck : ImageEncoderOptions
{
    public GenCheck(int width)
    {
        Scratch = new Rgba32[width * 16];
    }

    public int OffsetX { get; init; }

    public int OffsetY { get; init; }

    public bool Inverted { get; init; }

    public Rgba32[] Scratch { get; }

    public long Mismatches { get; set; }

    public long RowsSeen { get; set; }
}
