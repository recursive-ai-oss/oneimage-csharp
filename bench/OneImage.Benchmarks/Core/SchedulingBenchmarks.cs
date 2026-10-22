using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BenchmarkDotNet.Attributes;
using OneImage.Benchmarks.Jpeg;
using OneImage.Kernels;

namespace OneImage.Benchmarks.Core;

/// <summary>A sequential-only generated format: rows are a formula of the coordinates, so huge frames need no file.</summary>
internal sealed class SequentialGenFormat : IImageFormat, IImageDecoder
{
    public static SequentialGenFormat Instance { get; } = new();

    public string Name => "seqgen";

    public IReadOnlyList<string> Extensions { get; } = [".seqgen"];

    public string MimeType => "application/octet-stream";

    public int HeaderLength => 4;

    public IImageDecoder? Decoder => this;

    public IImageEncoder? Encoder => null;

    public DecoderCapabilities Capabilities => DecoderCapabilities.None;

    public static byte[] Header(int w, int h)
    {
        byte[] b = new byte[12];
        "SEQG"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), h);
        return b;
    }

    public bool Matches(ReadOnlySpan<byte> header) => header.Length >= 4 && header[..4].SequenceEqual("SEQG"u8);

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        byte[] b = new byte[12];
        source.Position = 0;
        source.ReadExactly(b);
        int w = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
        int h = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
        limits.CheckFrame(w, h);
        return new ImageInfo(this, [new FrameInfo(w, h, PixelFormat.Rgba8)]);
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        int w = info.Width;
        int h = info.Height;
        const int Band = 16;
        var rows = new OneImage.Rgba32[w * Band];
        for (int y = 0; y < h; y += Band)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Math.Min(Band, h - y);
            for (int r = 0; r < n; r++)
            {
                for (int x = 0; x < w; x++)
                {
                    rows[(r * w) + x] = new OneImage.Rgba32((byte)((x * 3) + y + r), (byte)(x + ((y + r) * 5)), (byte)(x ^ (y + r)), 255);
                }
            }

            sink.WriteRows(y, SpanCast.AsBytes<OneImage.Rgba32>(rows.AsSpan(0, n * w)), n);
        }
    }
}

/// <summary>Scheduling of the core: multi-page TIFF is in the Tiff benchmarks; here the streaming resize and the JPEG thumbnail.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SchedulingBenchmarks
{
    private byte[] _jpeg = null!;
    private Image _huge = null!;

    [GlobalSetup]
    public void Setup()
    {
        _jpeg = JpegData.SkiaJpeg(JpegData.Pixels(), 90);
        _huge = Image.Load(SequentialGenFormat.Header(8000, 30000), new LoadOptions { Configuration = new ImageConfiguration().Add(SequentialGenFormat.Instance) });
    }

    [Benchmark(Description = "streaming 8000x30000 sequential source -> Resize(400)")]
    public int StreamingResize() => _huge.Resize(width: 400).ToArray<OneImage.Rgba32>().Length;

    [Benchmark(Description = "decoder alone: 8000x30000 rows into a null sink")]
    public int DecodeOnly()
    {
        var sink = new NullSink(8000, 30000);
        SequentialGenFormat.Instance.DecodeFrame(Stream.Null, _huge.Frames[0].Node is { } ? SequentialGenFormat.Instance.ReadInfo(new MemoryStream(SequentialGenFormat.Header(8000, 30000)), ImageLimits.Unrestricted) : null!, 0, new DecodeRequest(new ImageRect(0, 0, 8000, 30000)), sink, ImageLimits.Unrestricted, CancellationToken.None);
        return sink.Rows;
    }

    [Benchmark(Description = "24 MP JPEG Load().Resize(400)")]
    public int JpegThumbnail() => Image.Load(_jpeg).Resize(width: 400).ToArray<OneImage.Rgb24>().Length;
}

internal sealed class NullSink(int width, int height) : IRowSink
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public PixelFormat Format => PixelFormat.Rgba8;

    public int Rows { get; private set; }

    public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount) => Rows += rowCount;
}
