using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;
using OneImage.Benchmarks.Jpeg;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpRgb = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using VipsImage = NetVips.Image;

namespace OneImage.Benchmarks.Composite;

/// <summary>24 MP JPEG (4:2:0, q90) to a 400 px wide thumbnail saved as q90 JPEG, from bytes in memory to bytes in memory.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class ThumbnailBenchmarks
{
    private byte[] _jpeg = null!;

    [GlobalSetup]
    public void Setup() => _jpeg = JpegData.SkiaJpeg(JpegData.Pixels(), 90);

    [Benchmark(Description = "OneImage Image.Thumbnail(bytes, 400) -> JPEG q90")]
    public int OneImage()
    {
        using var ms = new MemoryStream(64 << 10);
        global::OneImage.Image.Thumbnail(_jpeg, 400).Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 90 } });
        return (int)ms.Length;
    }

    [Benchmark(Description = "SkiaSharp scaled decode + resize + encode", Baseline = true)]
    public int Skia()
    {
        using var codec = SKCodec.Create(new MemoryStream(_jpeg));
        SKSizeI size = codec.GetScaledDimensions(1f / 8);
        using var decoded = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        codec.GetPixels(decoded.Info, decoded.GetPixels());
        int h = (int)Math.Round(400.0 * codec.Info.Height / codec.Info.Width);
        using SKBitmap small = decoded.Resize(new SKImageInfo(400, h, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var image = SKImage.FromBitmap(small);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return (int)data.Size;
    }

    [Benchmark(Description = "ImageSharp load(TargetSize) + Resize + JPEG q90")]
    public int ImageSharp()
    {
        var options = new DecoderOptions { TargetSize = new Size(400, 267) };
        using SharpRgb img = SharpImage.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(options, _jpeg);
        img.Mutate(x => x.Resize(400, 0));
        using var ms = new MemoryStream(64 << 10);
        img.Save(ms, new JpegEncoder { Quality = 90 });
        return (int)ms.Length;
    }

    [Benchmark(Description = "NetVips thumbnail_buffer + jpegsave_buffer q90")]
    public int NetVips()
    {
        using VipsImage img = VipsImage.ThumbnailBuffer(_jpeg, 400);
        return img.JpegsaveBuffer(q: 90).Length;
    }
}

/// <summary>Watermark: a 600 x 200 RGBA overlay at (100, 100) on a 6000 x 4000 RGB picture held in memory; the result is written to an RGB buffer.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class WatermarkBenchmarks : IDisposable
{
    private const int W = JpegData.Width, H = JpegData.Height, OW = 600, OH = 200;
    private global::OneImage.Image _base = null!;
    private global::OneImage.Image _overlay = null!;
    private global::OneImage.Rgb24[] _out = null!;
    private SharpRgb _sharpBase = null!;
    private SharpRgba _sharpOverlay = null!;
    private SKBitmap _skiaBase = null!;
    private SKBitmap _skiaOverlay = null!;
    private VipsImage _vipsBase = null!;
    private VipsImage _vipsOverlay = null!;

    [GlobalSetup]
    public void Setup()
    {
        global::OneImage.Rgb24[] px = JpegData.Pixels();
        _base = global::OneImage.Image.FromPixels<global::OneImage.Rgb24>(px, W, H);
        _out = new global::OneImage.Rgb24[W * H];
        byte[] raw = new byte[px.Length * 3];
        System.Runtime.InteropServices.MemoryMarshal.Cast<global::OneImage.Rgb24, byte>(px).CopyTo(raw);
        byte[] ov = new byte[OW * OH * 4];
        for (int y = 0; y < OH; y++)
        {
            for (int x = 0; x < OW; x++)
            {
                int i = ((y * OW) + x) * 4;
                ov[i] = 255;
                ov[i + 1] = (byte)(255 - (x / 3));
                ov[i + 2] = (byte)(y);
                ov[i + 3] = (byte)((x + y) % 7 == 0 ? 0 : 60 + ((x * 195) / OW));
            }
        }

        _overlay = global::OneImage.Image.FromPixels<global::OneImage.Rgba32>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, global::OneImage.Rgba32>(ov), OW, OH);
        _sharpBase = SharpImage.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgb24>(raw, W, H);
        _sharpOverlay = SharpImage.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(ov, OW, OH);
        byte[] rgba = new byte[px.Length * 4];
        for (int i = 0; i < px.Length; i++)
        {
            rgba[4 * i] = px[i].R;
            rgba[(4 * i) + 1] = px[i].G;
            rgba[(4 * i) + 2] = px[i].B;
            rgba[(4 * i) + 3] = 255;
        }

        _skiaBase = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, _skiaBase.GetPixels(), rgba.Length);
        _skiaOverlay = new SKBitmap(new SKImageInfo(OW, OH, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(ov, 0, _skiaOverlay.GetPixels(), ov.Length);
        _vipsBase = VipsImage.NewFromMemory(raw, W, H, 3, global::NetVips.Enums.BandFormat.Uchar);
        _vipsOverlay = VipsImage.NewFromMemory(ov, OW, OH, 4, global::NetVips.Enums.BandFormat.Uchar);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _sharpBase.Dispose();
        _sharpOverlay.Dispose();
        _skiaBase.Dispose();
        _skiaOverlay.Dispose();
        _vipsBase.Dispose();
        _vipsOverlay.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "OneImage Composite + CopyPixelsTo")]
    public int OneImage()
    {
        _base.Composite(_overlay, 100, 100).CopyPixelsTo<global::OneImage.Rgb24>(_out, new ImageRect(0, 0, W, H));
        return _out[0].R;
    }

    [Benchmark(Description = "SkiaSharp copy + DrawBitmap", Baseline = true)]
    public int Skia()
    {
        using SKBitmap copy = _skiaBase.Copy();
        using (var canvas = new SKCanvas(copy))
        {
            using SKImage overlayImage = SKImage.FromBitmap(_skiaOverlay);
            canvas.DrawImage(overlayImage, 100f, 100f, new SKSamplingOptions(SKFilterMode.Nearest), null);
        }

        return copy.Width;
    }

    [Benchmark(Description = "ImageSharp Clone + DrawImage")]
    public int ImageSharp()
    {
        using SharpRgb copy = _sharpBase.Clone(c => c.DrawImage(_sharpOverlay, new Point(100, 100), 1f));
        return copy.Width;
    }

    [Benchmark(Description = "NetVips composite2 + write_to_memory")]
    public int NetVips()
    {
        using VipsImage r = _vipsBase.Composite2(_vipsOverlay, global::NetVips.Enums.BlendMode.Over, 100, 100);
        return r.WriteToMemory<byte>().Length;
    }
}
