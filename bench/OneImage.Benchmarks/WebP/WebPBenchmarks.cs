using System.IO;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using ImageMagick;
using SixLabors.ImageSharp.Formats.Webp;
using SkiaSharp;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using VipsImage = NetVips.Image;

namespace OneImage.Benchmarks.WebP;

/// <summary>
/// Shared photo-like inputs: a fractal plasma with sensor-like noise, so entropy coders see realistic statistics. The lossy and
/// lossless files that the decode benchmarks read are produced once by libwebp (through SkiaSharp), the same bytes for all libraries.
/// </summary>
internal sealed class WebPInputs : IDisposable
{
    public WebPInputs(int w, int h)
    {
        Width = w;
        Height = h;
        MagickNET.SetRandomSeed(7);
        using var img = new MagickImage("plasma:fractal", (uint)w, (uint)h);
        img.AddNoise(NoiseType.Gaussian, 0.4);
        img.HasAlpha = false;
        using IPixelCollection<byte> px = img.GetPixels();
        byte[] rgb = px.ToByteArray(PixelMapping.RGB)!;
        Rgba = new byte[w * h * 4];
        for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4)
        {
            Rgba[j] = rgb[i];
            Rgba[j + 1] = rgb[i + 1];
            Rgba[j + 2] = rgb[i + 2];
            Rgba[j + 3] = 255;
        }

        Ours = OneImage.Image.FromPixels<OneImage.Rgba32>(MemoryMarshal.Cast<byte, OneImage.Rgba32>(Rgba), w, h);
        Sharp = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(Rgba, w, h);
        Skia = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(Rgba, 0, Skia.GetPixels(), Rgba.Length);
        Vips = VipsImage.NewFromMemory(Rgba, w, h, 4, NetVips.Enums.BandFormat.Uchar);
        Lossy = SkiaEncode(Skia, lossless: false);
        Lossless = SkiaEncode(Skia, lossless: true);
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Rgba { get; }

    public OneImage.Image Ours { get; }

    public IsImage Sharp { get; }

    public SKBitmap Skia { get; }

    public VipsImage Vips { get; }

    public byte[] Lossy { get; }

    public byte[] Lossless { get; }

    public static byte[] SkiaEncode(SKBitmap bmp, bool lossless)
    {
        using var ms = new MemoryStream();
        using var ws = new SKManagedWStream(ms);
        using SKPixmap pm = bmp.PeekPixels();
        pm.Encode(ws, new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, lossless ? 75 : 75));
        ws.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        Sharp.Dispose();
        Skia.Dispose();
        Vips.Dispose();
    }
}

/// <summary>Decoding 2000x1500 lossy and lossless files to RGBA bytes.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 6, iterationCount: 25, launchCount: 1)]
public class WebPDecodeBenchmarks : IDisposable
{
    private WebPInputs _in = null!;
    private OneImage.Rgba32[] _out = [];
    private SKBitmap _skiaBitmap = null!;

    [Params(false, true)]
    public bool Lossless { get; set; }

    private byte[] File => Lossless ? _in.Lossless : _in.Lossy;

    [GlobalSetup]
    public void Setup()
    {
        _in = new WebPInputs(2000, 1500);
        _out = new OneImage.Rgba32[2000 * 1500];
        _skiaBitmap = new SKBitmap(new SKImageInfo(2000, 1500, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _in.Dispose();
        _skiaBitmap.Dispose();
        GC.SuppressFinalize(this);
    }

    [BenchmarkCategory("Gate", "webp-decode")]
    [Benchmark(Baseline = true)]
    public OneImage.Rgba32 OneImageLib()
    {
        OneImage.Image img = OneImage.Image.Load(File);
        img.CopyPixelsTo<OneImage.Rgba32>(_out, new ImageRect(0, 0, img.Width, img.Height));
        return _out[0];
    }

    // The files are opaque, so their natural in-memory form is 3 bytes per pixel. These two rows compare both libraries in it;
    // the first row also pays this library's Rgb8 -> Rgba32 conversion in Image.CopyPixelsTo.
    [BenchmarkCategory("Gate", "webp-decode")]
    [Benchmark]
    public int OneImageNative()
    {
        OneImage.Image img = OneImage.Image.Load(File);
        return img.ToArray<OneImage.Rgb24>().Length;
    }

    [BenchmarkCategory("Gate", "webp-decode")]
    [Benchmark]
    public int ImageSharpRgb24()
    {
        using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(File);
        return img.Width;
    }

    [BenchmarkCategory("Gate", "webp-decode")]
    [Benchmark]
    public byte SkiaSharp()
    {
        using SKCodec codec = SKCodec.Create(new SKMemoryStream(File));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        codec.GetPixels(info, _skiaBitmap.GetPixels());
        return 0;
    }

    [BenchmarkCategory("Gate", "webp-decode")]
    [Benchmark]
    public int ImageSharp()
    {
        using IsImage img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(File);
        return img.Width;
    }

    [BenchmarkCategory("Gate", "webp-decode")]
    [Benchmark]
    public int NetVips()
    {
        using VipsImage img = VipsImage.NewFromBuffer(File);
        byte[] raw = img.WriteToMemory<byte>();
        return raw.Length;
    }
}

/// <summary>Encoding at libwebp's default quality 75 and effort (method 4).</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class WebPEncodeBenchmarks : IDisposable
{
    private WebPInputs _big = null!;
    private WebPInputs _small = null!;
    private WebPInputs? _huge;

    [GlobalSetup]
    public void Setup()
    {
        // The 24 MP input exists only for the peak-memory runs (gate-peak webp-encode-24mp), which set Case to it.
        if (Case.EndsWith("6000x4000", StringComparison.Ordinal))
        {
            _huge = new WebPInputs(6000, 4000);
            _big = _small = _huge;
            return;
        }

        _big = new WebPInputs(2000, 1500);
        _small = new WebPInputs(400, 300);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _big.Dispose();
        if (!ReferenceEquals(_small, _big))
        {
            _small.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    [Params("lossy-400x300", "lossy-2000x1500", "lossless-2000x1500")]
    public string Case { get; set; } = "";

    private WebPInputs Src => _huge ?? (Case.EndsWith("400x300", StringComparison.Ordinal) ? _small : _big);

    private bool Lossless => Case.StartsWith("lossless", StringComparison.Ordinal);

    [BenchmarkCategory("Gate", "webp-encode")]
    [Benchmark(Baseline = true)]
    public int OneImageLib()
    {
        using var ms = new MemoryStream();
        Src.Ours.Save(ms, WebPFormat.Instance, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = Lossless } });
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "webp-encode")]
    [Benchmark]
    public int SkiaSharp() => WebPInputs.SkiaEncode(Src.Skia, Lossless).Length;

    [BenchmarkCategory("Gate", "webp-encode")]
    [Benchmark]
    public int ImageSharp()
    {
        using var ms = new MemoryStream();
        Src.Sharp.Save(ms, new WebpEncoder { Quality = 75, FileFormat = Lossless ? WebpFileFormatType.Lossless : WebpFileFormatType.Lossy });
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "webp-encode")]
    [Benchmark]
    public int NetVips() => Src.Vips.WebpsaveBuffer(q: 75, lossless: Lossless).Length;
}

/// <summary>Encoding a 24 MP image at quality 75 and effort 4, lossy and lossless (time and allocation; peak memory is measured by the gate-peak runner).</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class WebPEncodeLargeBenchmarks : IDisposable
{
    private WebPInputs _in = null!;

    [GlobalSetup]
    public void Setup() => _in = new WebPInputs(6000, 4000);

    [GlobalCleanup]
    public void Dispose()
    {
        _in.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)]
    public int Lossy24Mp()
    {
        using var ms = new MemoryStream();
        _in.Ours.Save(ms, WebPFormat.Instance, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = false } });
        return (int)ms.Length;
    }

    [Benchmark]
    public int Lossless24Mp()
    {
        using var ms = new MemoryStream();
        _in.Ours.Save(ms, WebPFormat.Instance, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = true } });
        return (int)ms.Length;
    }
}
