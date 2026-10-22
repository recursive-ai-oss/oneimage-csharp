using BenchmarkDotNet.Attributes;
using IsRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using VipsImage = NetVips.Image;

namespace OneImage.Benchmarks.Filter;

/// <summary>
/// Neighbourhood filters and the histogram on 6000x4000 RGBA pixels already in memory. Every library gets the same bytes in its
/// own native form during setup and produces its whole result in memory (ours into a caller buffer). Kernel conventions differ
/// and are noted in the row names: this library blurs in linear light by default ("encoded" rows pass linear: false, which is
/// what the competitors do), uses radius ceil(3 sigma), and the competitors' sharpen operators are not the same operator.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FilterBenchmarks : IDisposable
{
    private const int W = 6000;
    private const int H = 4000;
    private OneImage.Image _ours = null!;
    private IsImage _sharp = null!;
    private VipsImage _vips = null!;
    private OneImage.Rgba32[] _buffer = [];

    [GlobalSetup]
    public void Setup()
    {
        NetVips.Cache.Max = 0;
        byte[] raw = new byte[W * H * 4];
        var rnd = new Random(42);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 4;
                raw[i] = (byte)((x * 255L / W) + rnd.Next(16));
                raw[i + 1] = (byte)((y * 255L / H) + rnd.Next(16));
                raw[i + 2] = (byte)(((x + y) & 255) + rnd.Next(8));
                raw[i + 3] = 255;
            }
        }

        _sharp = SixLabors.ImageSharp.Image.LoadPixelData<IsRgba32>(raw, W, H);
        var ours = new OneImage.Rgba32[W * H];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OneImage.Rgba32>(raw).CopyTo(ours);
        _ours = OneImage.Image.FromPixels<OneImage.Rgba32>(ours, W, H);
        _vips = VipsImage.NewFromMemory(raw, W, H, 4, NetVips.Enums.BandFormat.Uchar);
        _buffer = new OneImage.Rgba32[W * H];
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sharp.Dispose();
        _vips.Dispose();
        GC.SuppressFinalize(this);
    }

    private int Run(OneImage.Image image)
    {
        image.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, W, H));
        return _buffer[0].R;
    }

    private static int Run(IsImage source, Action<IImageProcessingContext> op)
    {
        using IsImage r = source.Clone(op);
        return r.Width;
    }

    private static int Run(VipsImage image) => image.WriteToMemory<byte>().Length;

    [Benchmark(Description = "gaussian s2: oneimage linear")]
    public int Gauss2Ours() => Run(_ours.GaussianBlur(2));

    [Benchmark(Description = "gaussian s2: oneimage encoded")]
    public int Gauss2OursEncoded() => Run(_ours.GaussianBlur(2, linear: false));

    [Benchmark(Description = "gaussian s2: ImageSharp")]
    public int Gauss2Sharp() => Run(_sharp, c => c.GaussianBlur(2f));

    [Benchmark(Description = "gaussian s2: NetVips")]
    public int Gauss2Vips() => Run(_vips.Gaussblur(2));

    [Benchmark(Description = "gaussian s10: oneimage linear")]
    public int Gauss10Ours() => Run(_ours.GaussianBlur(10));

    [Benchmark(Description = "gaussian s10: oneimage encoded")]
    public int Gauss10OursEncoded() => Run(_ours.GaussianBlur(10, linear: false));

    [Benchmark(Description = "gaussian s10: ImageSharp")]
    public int Gauss10Sharp() => Run(_sharp, c => c.GaussianBlur(10f));

    [Benchmark(Description = "gaussian s10: NetVips")]
    public int Gauss10Vips() => Run(_vips.Gaussblur(10));

    [Benchmark(Description = "unsharp s1.5: oneimage linear")]
    public int SharpenOurs() => Run(_ours.Sharpen(1, 1.5));

    [Benchmark(Description = "unsharp s1.5: oneimage encoded")]
    public int SharpenOursEncoded() => Run(_ours.Sharpen(1, 1.5, linear: false));

    [Benchmark(Description = "unsharp s1.5: ImageSharp GaussianSharpen")]
    public int SharpenSharp() => Run(_sharp, c => c.GaussianSharpen(1.5f));

    [Benchmark(Description = "sharpen s1.5: NetVips (CIELAB L, other operator)")]
    public int SharpenVips() => Run(_vips.ExtractBand(0, 3).Sharpen(sigma: 1.5));

    [Benchmark(Description = "median r2: oneimage")]
    public int MedianOurs() => Run(_ours.Median(2));

    [Benchmark(Description = "median r2: ImageSharp")]
    public int MedianSharp() => Run(_sharp, c => c.MedianBlur(2, true));

    [Benchmark(Description = "median 5x5: NetVips")]
    public int MedianVips() => Run(_vips.Median(5));

    [Benchmark(Description = "sobel: oneimage")]
    public int SobelOurs() => Run(_ours.Sobel().ConvertTo(OneImage.PixelFormat.Rgba8));

    [Benchmark(Description = "histogram (4 channels, 256 bins): oneimage")]
    public long HistogramOurs() => _ours.Histogram().PixelCount;

    [Benchmark(Description = "histogram: ImageSharp (manual loop)")]
    public long HistogramSharp()
    {
        long[] counts = new long[4 * 256];
        _sharp.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                foreach (IsRgba32 p in accessor.GetRowSpan(y))
                {
                    counts[p.R]++;
                    counts[256 + p.G]++;
                    counts[512 + p.B]++;
                    counts[768 + p.A]++;
                }
            }
        });
        return counts[0];
    }

    [Benchmark(Description = "histogram: NetVips hist_find per band")]
    public long HistogramVips() => _vips.HistFind().WriteToMemory<uint>().Length;
}
