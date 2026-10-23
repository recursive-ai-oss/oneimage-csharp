using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace OneImage.Benchmarks.Resample;

/// <summary>
/// Many small resizes in a loop: 2300x60 gray crops to height 32 (Lanczos3 in linear light, the defaults), the shape of an OCR
/// pipeline that resizes every text line. Rows report time and allocation per call. The "varying widths" rows cycle through
/// 128 source widths, which is what a stream of differently sized crops looks like.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Throughput, warmupCount: 5, iterationCount: 15)]
[BenchmarkCategory("resize-small")]
public partial class SmallResizeBenchmarks
{
    private const int Height = 32;
    // 128 distinct widths: more than the weight-table cache holds, so every call meets a new size.
    private static readonly int[] s_widths = MakeWidths();

    private static int[] MakeWidths()
    {
        var widths = new int[128];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = i == 0 ? 2300 : 2000 + (i * 5);
        }

        return widths;
    }

    private OneImage.Gray8[][] _pixels = [];
    private OneImage.Image[] _images = [];
    private OneImage.Gray8[] _out = [];
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _pixels = new OneImage.Gray8[s_widths.Length][];
        _images = new OneImage.Image[s_widths.Length];
        for (int i = 0; i < s_widths.Length; i++)
        {
            int w = s_widths[i];
            var px = new OneImage.Gray8[w * 60];
            var rnd = new Random(7 + i);
            for (int y = 0; y < 60; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    px[(y * w) + x] = new OneImage.Gray8((byte)(((x * 5) + (y * 3) + rnd.Next(40)) & 255));
                }
            }

            _pixels[i] = px;
            _images[i] = OneImage.Image.FromPixels<OneImage.Gray8>(px, w, 60);
        }

        _out = new OneImage.Gray8[4000 * Height];
        SetupSpan();
    }

    partial void SetupSpan();

    /// <summary>Image.Resize(height: 32) on the same image every call, then a copy into a reused buffer.</summary>
    [Benchmark(Description = "Image.Resize, 2300x60 -> h32")]
    public int ImageResize()
    {
        OneImage.Image r = _images[0].Resize(height: Height);
        r.CopyPixelsTo<OneImage.Gray8>(_out, new ImageRect(0, 0, r.Width, r.Height));
        return r.Width;
    }

    /// <summary>The same through the lazy pipeline, with a different source width each call.</summary>
    [Benchmark(Description = "Image.Resize, varying widths")]
    public int ImageResizeVarying()
    {
        OneImage.Image r = _images[_next++ & 127].Resize(height: Height);
        r.CopyPixelsTo<OneImage.Gray8>(_out, new ImageRect(0, 0, r.Width, r.Height));
        return r.Width;
    }
}
