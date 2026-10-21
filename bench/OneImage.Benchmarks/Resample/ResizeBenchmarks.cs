using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using VipsImage = NetVips.Image;

namespace OneImage.Benchmarks.Resample;

/// <summary>
/// In-memory resize of raw RGBA pixels, codecs excluded. Every library gets its source in its own native in-memory form during
/// setup. Rows named "(encoded space)" resample without linear-light conversion, which is what the competitors do by default;
/// "(linear)" is this library's default.
/// </summary>
internal sealed class ResizeSources : IDisposable
{
    public ResizeSources(int w, int h)
    {
        Width = w;
        byte[] raw = new byte[w * h * 4];
        var rnd = new Random(42);
        // Smooth gradients plus noise: closer to a photo than pure noise, and the same bytes for every library.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                raw[i] = (byte)((x * 255L / w) + rnd.Next(16));
                raw[i + 1] = (byte)((y * 255L / h) + rnd.Next(16));
                raw[i + 2] = (byte)(((x + y) & 255) + rnd.Next(8));
                raw[i + 3] = 255;
            }
        }

        Sharp = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(raw, w, h);
        var ours = new OneImage.Rgba32[w * h];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OneImage.Rgba32>(raw).CopyTo(ours);
        Ours = OneImage.Image.FromPixels<OneImage.Rgba32>(ours, w, h);
        Skia = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(raw, 0, Skia.GetPixels(), raw.Length);
        Vips = VipsImage.NewFromMemory(raw, w, h, 4, NetVips.Enums.BandFormat.Uchar);
    }

    public int Width { get; }

    public OneImage.Image Ours { get; }

    public IsImage Sharp { get; }

    public SKBitmap Skia { get; }

    public VipsImage Vips { get; }

    public void Dispose()
    {
        Sharp.Dispose();
        Skia.Dispose();
        Vips.Dispose();
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class DownscaleBenchmarks : IDisposable
{
    private ResizeSources _src = null!;
    private const int TargetWidth = 400;
    private OneImage.Rgba32[] _buffer = [];

    [GlobalSetup]
    public void Setup()
    {
        _src = new ResizeSources(6000, 4000);
        _buffer = new OneImage.Rgba32[TargetWidth * 267];
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _src.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "oneimage Lanczos3 (linear)")]
    public OneImage.Rgba32[] OursLinear() =>
        _src.Ours.Resize(width: TargetWidth, filter: ResamplingFilter.Lanczos3).ToArray<OneImage.Rgba32>();

    [Benchmark(Description = "oneimage Lanczos3 (linear) into caller buffer")]
    public int OursLinearIntoBuffer()
    {
        OneImage.Image r = _src.Ours.Resize(width: TargetWidth, filter: ResamplingFilter.Lanczos3);
        r.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, r.Width, r.Height));
        return r.Width;
    }

    [Benchmark(Description = "oneimage Lanczos3 (encoded space)")]
    public OneImage.Rgba32[] OursEncoded() =>
        _src.Ours.Resize(width: TargetWidth, filter: ResamplingFilter.Lanczos3, linear: false).ToArray<OneImage.Rgba32>();

    [Benchmark(Description = "ImageSharp Lanczos3")]
    public int ImageSharp()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Resize(TargetWidth, 0, KnownResamplers.Lanczos3));
        return r.Width;
    }

    [Benchmark(Description = "SkiaSharp linear+mipmap")]
    public int SkiaSharp()
    {
        using SKBitmap? r = _src.Skia.Resize(new SKImageInfo(TargetWidth, 267, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return r!.Width;
    }

    [Benchmark(Description = "NetVips resize Lanczos3")]
    public int NetVipsResize()
    {
        using VipsImage r = _src.Vips.Resize((double)TargetWidth / _src.Width, kernel: NetVips.Enums.Kernel.Lanczos3);
        return r.WriteToMemory().Length;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class UpscaleBenchmarks : IDisposable
{
    private OneImage.Rgba32[] _buffer = [];
    private ResizeSources _src = null!;
    [GlobalSetup]
    public void Setup()
    {
        _src = new ResizeSources(1000, 1000);
        _buffer = new OneImage.Rgba32[2000 * 2000];
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _src.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "oneimage Catmull-Rom (linear)")]
    public OneImage.Rgba32[] OursLinear() =>
        _src.Ours.Resize(2000, 2000, Fit.Stretch, filter: ResamplingFilter.CatmullRom).ToArray<OneImage.Rgba32>();

    [Benchmark(Description = "oneimage Catmull-Rom (linear) into caller buffer")]
    public int OursLinearIntoBuffer()
    {
        OneImage.Image r = _src.Ours.Resize(2000, 2000, Fit.Stretch, filter: ResamplingFilter.CatmullRom);
        r.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, 2000, 2000));
        return r.Width;
    }

    [Benchmark(Description = "oneimage Catmull-Rom (encoded space)")]
    public OneImage.Rgba32[] OursEncoded() =>
        _src.Ours.Resize(2000, 2000, Fit.Stretch, filter: ResamplingFilter.CatmullRom, linear: false).ToArray<OneImage.Rgba32>();

    [Benchmark(Description = "ImageSharp CatmullRom")]
    public int ImageSharp()
    {
        using IsImage r = _src.Sharp.Clone(c => c.Resize(2000, 2000, KnownResamplers.CatmullRom));
        return r.Width;
    }

    [Benchmark(Description = "SkiaSharp Catmull-Rom")]
    public int SkiaSharp()
    {
        using SKBitmap? r = _src.Skia.Resize(new SKImageInfo(2000, 2000, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKCubicResampler.CatmullRom));
        return r!.Width;
    }

    [Benchmark(Description = "NetVips resize bicubic")]
    public int NetVipsResize()
    {
        // libvips has no Catmull-Rom kernel name; its cubic kernel is the Catmull-Rom spline.
        using VipsImage r = _src.Vips.Resize(2.0, kernel: NetVips.Enums.Kernel.Cubic);
        return r.WriteToMemory().Length;
    }
}
