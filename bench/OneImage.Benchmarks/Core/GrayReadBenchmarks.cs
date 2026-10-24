using System.IO;
using BenchmarkDotNet.Attributes;
using OneImage.Benchmarks.Region;
using OneImage.Kernels;

namespace OneImage.Benchmarks.Core;

/// <summary>
/// Whole-frame reads of RGB images as Gray8 against the same read as Rgb24 (the decode cost without conversion), and the RGB-to-gray
/// row kernel alone. Inputs are written once under <c>ONEIMAGE_BENCH_DATA</c> (or the temp directory).
/// </summary>
[MemoryDiagnoser]
public class GrayReadBenchmarks
{
    private byte[] _a4Jpeg = null!;
    private byte[] _a4Png = null!;
    private byte[] _bigJpeg = null!;
    private Gray8[] _gray = null!;
    private Rgb24[] _rgb = null!;
    private byte[] _rgbaRows = null!;
    private byte[] _rgbRows = null!;
    private byte[] _grayRows = null!;

    [GlobalSetup]
    public void Setup()
    {
        string dir = Environment.GetEnvironmentVariable("ONEIMAGE_BENCH_DATA") ?? Path.Combine(Path.GetTempPath(), "oneimage-bench");
        _a4Jpeg = File.ReadAllBytes(RegionData.Ensure(dir, "a4-baseline.jpg"));
        _a4Png = File.ReadAllBytes(RegionData.Ensure(dir, "a4-png.png"));
        string big = Path.Combine(dir, "24mp.jpg");
        if (!File.Exists(big))
        {
            Image.FromPixels<Rgb24>(RegionData.Page(6000, 4000), 6000, 4000)
                .Save(big, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 85 } });
        }

        _bigJpeg = File.ReadAllBytes(big);
        _gray = new Gray8[6000 * 4000];
        _rgb = new Rgb24[6000 * 4000];
        var rnd = new Random(3);
        const int Pixels = 1 << 20;
        _rgbaRows = new byte[Pixels * 4];
        _rgbRows = new byte[Pixels * 3];
        _grayRows = new byte[Pixels];
        rnd.NextBytes(_rgbaRows);
        rnd.NextBytes(_rgbRows);
    }

    [Benchmark(Description = "A4 JPEG -> Rgb24")]
    public Rgb24 A4JpegRgb()
    {
        Image.Load(_a4Jpeg).CopyPixelsTo<Rgb24>(_rgb.AsSpan(0, 2480 * 3508), new ImageRect(0, 0, 2480, 3508));
        return _rgb[0];
    }

    [Benchmark(Description = "A4 JPEG -> Gray8")]
    public Gray8 A4JpegGray()
    {
        Image.Load(_a4Jpeg).CopyPixelsTo<Gray8>(_gray.AsSpan(0, 2480 * 3508), new ImageRect(0, 0, 2480, 3508));
        return _gray[0];
    }

    [Benchmark(Description = "A4 PNG -> Rgb24")]
    public Rgb24 A4PngRgb()
    {
        Image.Load(_a4Png).CopyPixelsTo<Rgb24>(_rgb.AsSpan(0, 2480 * 3508), new ImageRect(0, 0, 2480, 3508));
        return _rgb[0];
    }

    [Benchmark(Description = "A4 PNG -> Gray8")]
    public Gray8 A4PngGray()
    {
        Image.Load(_a4Png).CopyPixelsTo<Gray8>(_gray.AsSpan(0, 2480 * 3508), new ImageRect(0, 0, 2480, 3508));
        return _gray[0];
    }

    [Benchmark(Description = "24 MP JPEG -> Rgb24")]
    public Rgb24 BigJpegRgb()
    {
        Image.Load(_bigJpeg).CopyPixelsTo<Rgb24>(_rgb, new ImageRect(0, 0, 6000, 4000));
        return _rgb[0];
    }

    [Benchmark(Description = "24 MP JPEG -> Gray8")]
    public Gray8 BigJpegGray()
    {
        Image.Load(_bigJpeg).CopyPixelsTo<Gray8>(_gray, new ImageRect(0, 0, 6000, 4000));
        return _gray[0];
    }

    [Benchmark(Description = "kernel: 1 MP Rgb24 -> Gray8")]
    public byte KernelRgb24ToGray8()
    {
        ConvertKernel.ConvertRow(_rgbRows, PixelFormat.Rgb8, _grayRows, PixelFormat.Gray8, 1 << 20);
        return _grayRows[7];
    }

    [Benchmark(Description = "kernel: 1 MP Rgba32 -> Gray8")]
    public byte KernelRgba32ToGray8()
    {
        ConvertKernel.ConvertRow(_rgbaRows, PixelFormat.Rgba8, _grayRows, PixelFormat.Gray8, 1 << 20);
        return _grayRows[7];
    }
}
