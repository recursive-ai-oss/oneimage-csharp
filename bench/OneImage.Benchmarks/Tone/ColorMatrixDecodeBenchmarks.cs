using System.IO;
using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Tone;

/// <summary>
/// A JPEG of 2048x1536 decoded and put through a color-matrix preset, ending as RGBA8 in memory: oneimage runs the matrix on rows as the
/// decoder delivers them and writes into a caller buffer; ImageSharp decodes to Rgba32 and mutates in place. Both use the default degree of parallelism.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class ColorMatrixDecodeBenchmarks
{
    private const int W = 2048;
    private const int H = 1536;
    private byte[] _jpeg = [];
    private OneImage.Rgba32[] _buffer = [];

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(7);
        var px = new OneImage.Rgb24[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                px[(y * W) + x] = new OneImage.Rgb24((byte)((x * 255 / W) + rnd.Next(12)), (byte)((y * 255 / H) + rnd.Next(12)), (byte)(((x + y) & 255) ^ rnd.Next(8)));
            }
        }

        using var ms = new MemoryStream();
        OneImage.Image.FromPixels<OneImage.Rgb24>(px, W, H).Save(ms, JpegFormat.Instance);
        _jpeg = ms.ToArray();
        _buffer = new OneImage.Rgba32[W * H];
    }

    [Benchmark(Description = "jpeg decode only: oneimage")]
    public int DecodeOurs()
    {
        OneImage.Image.Load(_jpeg).CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, W, H));
        return _buffer[12345].R;
    }

    [Benchmark(Description = "jpeg decode + sepia: oneimage")]
    public int SepiaOurs()
    {
        OneImage.Image.Load(_jpeg).Sepia().CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, W, H));
        return _buffer[12345].R;
    }

    [Benchmark(Description = "jpeg decode + sepia: ImageSharp")]
    public int SepiaSharp()
    {
        using IsImage img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(_jpeg);
        img.Mutate(c => c.Sepia());
        return img[1, 1].R;
    }
}
