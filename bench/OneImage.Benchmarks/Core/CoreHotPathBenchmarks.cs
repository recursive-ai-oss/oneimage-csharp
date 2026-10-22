using System.Collections.Generic;
using System.IO;
using System.Threading;
using BenchmarkDotNet.Attributes;
using OneImage.Kernels;
using Sources = OneImage.Benchmarks.Resample.ResizeSources;

namespace OneImage.Benchmarks.Core;

/// <summary>Copy-out and format conversion of a 6000x4000 (24 MP) Rgba32 image already in memory.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CoreHotPathBenchmarks : IDisposable
{
    private Sources _src = null!;
    private OneImage.Rgba32[] _rgba = [];
    private OneImage.Rgb24[] _rgb = [];
    private byte[] _rowSrc = [];
    private byte[] _rowDst = [];
    private Image _asRgb = null!;
    private Image _small = null!;

    [GlobalSetup]
    public void Setup()
    {
        _src = new Sources(6000, 4000);
        _rgba = new OneImage.Rgba32[6000 * 4000];
        _rgb = new OneImage.Rgb24[6000 * 4000];
        _small = Image.FromPixels<OneImage.Rgba32>(new OneImage.Rgba32[2000 * 1500], 2000, 1500);
        _asRgb = _src.Ours.ConvertTo(PixelFormat.Rgb8);
        _rowSrc = new byte[6000 * 4];
        new Random(1).NextBytes(_rowSrc);
        _rowDst = new byte[6000 * 4];
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _src.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Call degree for the full copy-out: 0 is the default, 1 is serial.</summary>
    [Params(1, 0, 8)]
    public int Degree { get; set; }

    [Benchmark(Description = "copy out 6000x4000 Rgba32 to caller buffer")]
    public int CopyOut()
    {
        _src.Ours.CopyPixelsTo<OneImage.Rgba32>(_rgba, new ImageRect(0, 0, 6000, 4000), Degree);
        return _rgba.Length;
    }

    [Benchmark(Description = "copy out region 512x512 Rgba32")]
    public int CopyOutSmall()
    {
        _src.Ours.CopyPixelsTo<OneImage.Rgba32>(_rgba.AsSpan(0, 512 * 512), new ImageRect(1000, 1000, 512, 512));
        return _rgba.Length;
    }

    [Benchmark(Description = "encoder-style ReadRows, 2000x1500 Rgba32 in 16-row strips")]
    public int ReadRowsStrips()
    {
        var enc = new CountingEncoder();
        _small.Save(Stream.Null, new CountingFormat(enc));
        return enc.Rows;
    }

    [Benchmark(Description = "Rgba32 -> Rgb24 24 MP through the graph")]
    public int ConvertGraph()
    {
        _asRgb.CopyPixelsTo<OneImage.Rgb24>(_rgb, new ImageRect(0, 0, 6000, 4000));
        return _rgb.Length;
    }

    [Benchmark(Description = "Rgba32 -> Rgb24 kernel only, 4000 rows of 6000")]
    public int ConvertKernelOnly()
    {
        for (int y = 0; y < 4000; y++)
        {
            ConvertKernel.ConvertRow(_rowSrc, PixelFormat.Rgba8, _rowDst, PixelFormat.Rgb8, 6000);
        }

        return _rowDst[0];
    }
}

internal sealed class CountingEncoder : IImageEncoder
{
    public int Rows { get; private set; }

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } = [PixelFormat.Rgba8];

    public bool SupportsMultipleFrames => false;

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ImageFrameReader f = request.Frames[0];
        byte[] strip = new byte[f.Width * 4 * 16];
        for (int y = 0; y < f.Height; y += 16)
        {
            int n = Math.Min(16, f.Height - y);
            f.ReadRows(y, n, strip);
            Rows += n;
        }
    }
}

internal sealed class CountingFormat(IImageEncoder encoder) : IImageFormat
{
    public string Name => "counting";

    public IReadOnlyList<string> Extensions { get; } = [".cnt"];

    public string MimeType => "application/octet-stream";

    public int HeaderLength => 1;

    public IImageDecoder? Decoder => null;

    public IImageEncoder? Encoder => encoder;

    public bool Matches(ReadOnlySpan<byte> header) => false;
}
