using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using ImageMagick;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;
using OneImage.Benchmarks.Gif;
using OneImage.Benchmarks.Jpeg;
using OneImage.Benchmarks.Png;
using OneImage.Benchmarks.WebP;
using OI = global::OneImage;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpRgb = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using VipsImage = NetVips.Image;

// Gate rows that the per-area benchmark files do not already carry: the Magick.NET rows, the Rgba32 decode rows, and the tasks
// that had no benchmark yet (PNG to lossy WebP, BMP, CMYK JPEG to sRGB, animated GIF resize). Every method is tagged
// [BenchmarkCategory("Gate", "<task>")]; rows of one task are compared by the gate report whichever class they live in.
namespace OneImage.Benchmarks.Gate;

internal static class GateJob
{
    public const int Warmup = 2;
    public const int Iterations = 8;
}

/// <summary>Magick.NET and Rgba32 rows for the 24 MP JPEG tasks, plus the watermark row.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: GateJob.Warmup, iterationCount: GateJob.Iterations)]
public class JpegGateBenchmarks : IDisposable
{
    private const int W = JpegData.Width, H = JpegData.Height, OW = 600, OH = 200;
    private byte[] _jpeg = null!;
    private MagickImage _raw = null!;
    private MagickImage _wmBase = null!;
    private MagickImage _wmOverlay = null!;

    [GlobalSetup]
    public void Setup()
    {
        OI.Rgb24[] px = JpegData.Pixels();
        byte[] rgb = new byte[px.Length * 3];
        System.Runtime.InteropServices.MemoryMarshal.Cast<OI.Rgb24, byte>(px).CopyTo(rgb);
        _jpeg = JpegData.SkiaJpeg(px, 90);
        _raw = new MagickImage(rgb, new PixelReadSettings((uint)W, (uint)H, StorageType.Char, "RGB"));
        _wmBase = new MagickImage(rgb, new PixelReadSettings((uint)W, (uint)H, StorageType.Char, "RGB"));
        byte[] ov = new byte[OW * OH * 4];
        for (int y = 0; y < OH; y++)
        {
            for (int x = 0; x < OW; x++)
            {
                int i = ((y * OW) + x) * 4;
                ov[i] = 255;
                ov[i + 1] = (byte)(255 - (x / 3));
                ov[i + 2] = (byte)y;
                ov[i + 3] = (byte)((x + y) % 7 == 0 ? 0 : 60 + ((x * 195) / OW));
            }
        }

        _wmOverlay = new MagickImage(ov, new PixelReadSettings((uint)OW, (uint)OH, StorageType.Char, "RGBA"));
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _raw.Dispose();
        _wmBase.Dispose();
        _wmOverlay.Dispose();
        GC.SuppressFinalize(this);
    }

    [BenchmarkCategory("Gate", "jpeg-decode")]
    [Benchmark(Description = "Magick.NET read, 24 MP")]
    public int MagickDecode()
    {
        using var m = new MagickImage(_jpeg);
        return (int)m.Width;
    }

    [BenchmarkCategory("Gate", "jpeg-decode-rgba")]
    [Benchmark(Description = "OneImage Image.Load + ToArray<Rgba32>, 24 MP")]
    public int OneImageRgba() => OI.Image.Load(_jpeg).ToArray<OI.Rgba32>().Length;

    [BenchmarkCategory("Gate", "jpeg-decode-rgba")]
    [Benchmark(Description = "ImageSharp Image.Load<Rgba32>, 24 MP")]
    public int ImageSharpRgba()
    {
        using SharpRgba img = SharpImage.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(_jpeg);
        return img.Width;
    }

    [BenchmarkCategory("Gate", "jpeg-decode-rgba")]
    [Benchmark(Description = "SkiaSharp SKBitmap.Decode (Rgba8888), 24 MP")]
    public int SkiaRgba()
    {
        using SKBitmap bmp = SKBitmap.Decode(_jpeg);
        return bmp.Width;
    }

    [BenchmarkCategory("Gate", "jpeg-encode")]
    [Benchmark(Description = "Magick.NET q90 4:2:0")]
    public int MagickEncode()
    {
        using var ms = new MemoryStream();
        _raw.Quality = 90;
        _raw.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "2x2");
        _raw.Write(ms, MagickFormat.Jpeg);
        return (int)ms.Length;
    }

    private MagickImage MagickThumbSource()
    {
        // jpeg:size lets libjpeg scale the DCT on load, the same shortcut every other row takes.
        var s = new MagickReadSettings();
        s.SetDefine(MagickFormat.Jpeg, "size", "800x534");
        return new MagickImage(_jpeg, s);
    }

    [BenchmarkCategory("Gate", "thumb-jpeg")]
    [Benchmark(Description = "Magick.NET jpeg:size + Thumbnail + JPEG q90")]
    public int MagickThumbJpeg()
    {
        using MagickImage m = MagickThumbSource();
        m.Thumbnail(new MagickGeometry(400, 400));
        m.Quality = 90;
        using var ms = new MemoryStream();
        m.Write(ms, MagickFormat.Jpeg);
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "thumb-webp")]
    [Benchmark(Description = "Magick.NET jpeg:size + Thumbnail + WebP q75")]
    public int MagickThumbWebP()
    {
        using MagickImage m = MagickThumbSource();
        m.Thumbnail(new MagickGeometry(400, 400));
        m.Quality = 75;
        using var ms = new MemoryStream();
        m.Write(ms, MagickFormat.WebP);
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "watermark")]
    [Benchmark(Description = "Magick.NET Clone + Composite + packed RGB out")]
    public int MagickWatermark()
    {
        using var c = (MagickImage)_wmBase.Clone();
        c.Composite(_wmOverlay, 100, 100, CompositeOperator.Over);
        using IPixelCollection<byte> px = c.GetPixelsUnsafe();
        return px.ToByteArray(PixelMapping.RGB)!.Length;
    }
}

/// <summary>Magick.NET rows for PNG and 2x upscale, and PNG to lossy WebP.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: GateJob.Warmup, iterationCount: GateJob.Iterations)]
public class PngGateBenchmarks : IDisposable
{
    private byte[] _png = null!;
    private MagickImage _raw = null!;
    private OI.Image _ours = null!;

    [Params("photo", "screenshot")]
    public string Content { get; set; } = "photo";

    [GlobalSetup]
    public void Setup()
    {
        byte[] rgb = Content == "photo" ? PngBenchData.Photo() : PngBenchData.Screenshot();
        _png = PngBenchData.SkiaPng(rgb);
        _raw = new MagickImage(rgb, new PixelReadSettings(PngBenchData.W, PngBenchData.H, StorageType.Char, "RGB"));
        _ours = OI.Image.FromPixels<OI.Rgb24>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OI.Rgb24>(rgb), PngBenchData.W, PngBenchData.H);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _raw.Dispose();
        GC.SuppressFinalize(this);
    }

    [BenchmarkCategory("Gate", "png-decode")]
    [Benchmark(Description = "Magick.NET")]
    public int MagickDecode()
    {
        using var m = new MagickImage(_png);
        return (int)m.Width;
    }

    [BenchmarkCategory("Gate", "png-encode")]
    [Benchmark(Description = "Magick.NET")]
    public int MagickEncode()
    {
        using var ms = new MemoryStream();
        _raw.Write(ms, MagickFormat.Png);
        return (int)ms.Length;
    }

    // PNG file in, lossy WebP q75 out, both as bytes in memory.
    [BenchmarkCategory("Gate", "png-to-webp")]
    [Benchmark(Description = "OneImage Load + Save WebP q75")]
    public int PngToWebPOneImage()
    {
        using var ms = new MemoryStream();
        OI.Image.Load(_png).Save(ms, OI.WebPFormat.Instance, new OI.SaveOptions { EncoderOptions = new OI.WebPEncoderOptions { Quality = 75 } });
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "png-to-webp")]
    [Benchmark(Description = "SkiaSharp decode + WebP q75")]
    public int PngToWebPSkia()
    {
        using SKBitmap bmp = SKBitmap.Decode(_png);
        using SKData data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Webp, 75);
        return (int)data.Size;
    }

    [BenchmarkCategory("Gate", "png-to-webp")]
    [Benchmark(Description = "ImageSharp load + WebP q75")]
    public int PngToWebPImageSharp()
    {
        using SharpRgb img = SharpImage.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(_png);
        using var ms = new MemoryStream();
        img.Save(ms, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { Quality = 75 });
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "png-to-webp")]
    [Benchmark(Description = "NetVips load + webpsave q75")]
    public int PngToWebPNetVips()
    {
        using VipsImage img = VipsImage.NewFromBuffer(_png);
        return img.WebpsaveBuffer(q: 75).Length;
    }

    [BenchmarkCategory("Gate", "png-to-webp")]
    [Benchmark(Description = "Magick.NET read + WebP q75")]
    public int PngToWebPMagick()
    {
        using var m = new MagickImage(_png);
        m.Quality = 75;
        using var ms = new MemoryStream();
        m.Write(ms, MagickFormat.WebP);
        return (int)ms.Length;
    }
}

/// <summary>Magick.NET rows for WebP decode.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: GateJob.Warmup, iterationCount: GateJob.Iterations)]
public class WebPDecodeGateBenchmarks : IDisposable
{
    private WebPInputs _in = null!;

    [Params(false, true)]
    public bool Lossless { get; set; }

    [GlobalSetup]
    public void Setup() => _in = new WebPInputs(2000, 1500);

    [GlobalCleanup]
    public void Dispose()
    {
        _in.Dispose();
        GC.SuppressFinalize(this);
    }

    [BenchmarkCategory("Gate", "webp-decode")]
    [Benchmark(Description = "Magick.NET")]
    public int MagickDecode()
    {
        using var m = new MagickImage(Lossless ? _in.Lossless : _in.Lossy);
        return (int)m.Width;
    }
}

/// <summary>Magick.NET rows for WebP encode.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: GateJob.Warmup, iterationCount: GateJob.Iterations)]
public class WebPEncodeGateBenchmarks : IDisposable
{
    private MagickImage _big = null!;
    private MagickImage _small = null!;
    private WebPInputs _bigIn = null!;
    private WebPInputs _smallIn = null!;

    [Params("lossy-400x300", "lossy-2000x1500", "lossless-2000x1500")]
    public string Case { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _bigIn = new WebPInputs(2000, 1500);
        _smallIn = new WebPInputs(400, 300);
        _big = From(_bigIn);
        _small = From(_smallIn);
    }

    private static MagickImage From(WebPInputs i) => new(i.Rgba, new PixelReadSettings((uint)i.Width, (uint)i.Height, StorageType.Char, "RGBA"));

    [GlobalCleanup]
    public void Dispose()
    {
        _big.Dispose();
        _small.Dispose();
        _bigIn.Dispose();
        _smallIn.Dispose();
        GC.SuppressFinalize(this);
    }

    [BenchmarkCategory("Gate", "webp-encode")]
    [Benchmark(Description = "Magick.NET")]
    public int MagickEncode()
    {
        MagickImage src = Case.EndsWith("400x300", StringComparison.Ordinal) ? _small : _big;
        src.Quality = 75;
        src.Settings.SetDefine(MagickFormat.WebP, "lossless", Case.StartsWith("lossless", StringComparison.Ordinal) ? "true" : "false");
        using var ms = new MemoryStream();
        src.Write(ms, MagickFormat.WebP);
        return (int)ms.Length;
    }
}

/// <summary>Magick.NET row for the 2x upscale (1000x1000 to 2000x2000, Catmull-Rom).</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: GateJob.Warmup, iterationCount: GateJob.Iterations)]
public class UpscaleGateBenchmarks : IDisposable
{
    private MagickImage _src = null!;

    [GlobalSetup]
    public void Setup()
    {
        using var rs = new Resample.ResizeSources(1000, 1000);
        byte[] raw = System.Runtime.InteropServices.MemoryMarshal.AsBytes<OI.Rgba32>(rs.Ours.ToArray<OI.Rgba32>()).ToArray();
        _src = new MagickImage(raw, new PixelReadSettings(1000, 1000, StorageType.Char, "RGBA"));
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _src.Dispose();
        GC.SuppressFinalize(this);
    }

    [BenchmarkCategory("Gate", "upscale-2x")]
    [Benchmark(Description = "Magick.NET Catrom")]
    public int MagickUpscale()
    {
        using var c = (MagickImage)_src.Clone();
        c.FilterType = FilterType.Catrom;
        c.Resize(new MagickGeometry(2000, 2000) { IgnoreAspectRatio = true });
        return (int)c.Width;
    }
}

/// <summary>BMP decode and encode of a 2000x1500 24-bit picture. SkiaSharp has no BMP encoder; the NetVips native build has no BMP loader or saver.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: GateJob.Warmup, iterationCount: GateJob.Iterations)]
public class BmpGateBenchmarks : IDisposable
{
    private byte[] _bmp = null!;
    private OI.Image _ours = null!;
    private SharpRgb _sharp = null!;
    private MagickImage _magick = null!;

    [GlobalSetup]
    public void Setup()
    {
        byte[] rgb = PngBenchData.Photo();
        _magick = new MagickImage(rgb, new PixelReadSettings(PngBenchData.W, PngBenchData.H, StorageType.Char, "RGB"));
        using (var ms = new MemoryStream())
        {
            _magick.Write(ms, MagickFormat.Bmp3);
            _bmp = ms.ToArray();
        }

        _ours = OI.Image.FromPixels<OI.Rgb24>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OI.Rgb24>(rgb), PngBenchData.W, PngBenchData.H);
        _sharp = SharpImage.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgb24>(rgb, PngBenchData.W, PngBenchData.H);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sharp.Dispose();
        _magick.Dispose();
        GC.SuppressFinalize(this);
    }

    [BenchmarkCategory("Gate", "bmp-decode")]
    [Benchmark(Description = "OneImage Load + ToArray<Rgb24>")]
    public int OneImageDecode() => OI.Image.Load(_bmp).ToArray<OI.Rgb24>().Length;

    [BenchmarkCategory("Gate", "bmp-decode")]
    [Benchmark(Description = "ImageSharp Load<Rgb24>")]
    public int ImageSharpDecode()
    {
        using SharpRgb img = SharpImage.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(_bmp);
        return img.Width;
    }

    [BenchmarkCategory("Gate", "bmp-decode")]
    [Benchmark(Description = "SkiaSharp SKBitmap.Decode")]
    public int SkiaDecode()
    {
        using SKBitmap bmp = SKBitmap.Decode(_bmp);
        return bmp.Width;
    }

    [BenchmarkCategory("Gate", "bmp-decode")]
    [Benchmark(Description = "Magick.NET")]
    public int MagickDecode()
    {
        using var m = new MagickImage(_bmp);
        return (int)m.Width;
    }

    [BenchmarkCategory("Gate", "bmp-encode")]
    [Benchmark(Description = "OneImage Save BMP")]
    public int OneImageEncode()
    {
        using var ms = new MemoryStream();
        _ours.Save(ms, OI.BmpFormat.Instance);
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "bmp-encode")]
    [Benchmark(Description = "ImageSharp Save BMP 24-bit")]
    public int ImageSharpEncode()
    {
        using var ms = new MemoryStream();
        _sharp.Save(ms, new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder { BitsPerPixel = SixLabors.ImageSharp.Formats.Bmp.BmpBitsPerPixel.Pixel24 });
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "bmp-encode")]
    [Benchmark(Description = "Magick.NET Write BMP3")]
    public int MagickEncode()
    {
        using var ms = new MemoryStream();
        _magick.Write(ms, MagickFormat.Bmp3);
        return (int)ms.Length;
    }
}

/// <summary>
/// A 24 MP CMYK JPEG (Adobe marker, generic CMYK profile embedded) decoded and converted to 8-bit sRGB. Magick.NET and this
/// library convert through the embedded profile; the other libraries are included as they behave out of the box.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: GateJob.Warmup, iterationCount: GateJob.Iterations)]
public class CmykJpegGateBenchmarks
{
    private const int W = 6000, H = 4000;
    private byte[] _jpeg = null!;
    private ImageMagick.ColorProfile _srgb = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(3);
        byte[] cmyk = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int j = ((y * W) + x) * 4;
                cmyk[j] = (byte)((x * 200 / W) + rnd.Next(8));
                cmyk[j + 1] = (byte)((y * 200 / H) + rnd.Next(8));
                cmyk[j + 2] = (byte)rnd.Next(60);
                cmyk[j + 3] = (byte)((x + y) * 80 / (W + H));
            }
        }

        var profile = new ImageMagick.ColorProfile(OI.ColorProfile.GenericCmyk.IccBytes.ToArray());
        _srgb = new ImageMagick.ColorProfile(OI.ColorProfile.Srgb.IccBytes.ToArray());
        using var m = new MagickImage(cmyk, new PixelReadSettings(W, H, StorageType.Char, "CMYK")) { ColorSpace = ColorSpace.CMYK };
        m.SetProfile(profile);
        m.Quality = 90;
        using var ms = new MemoryStream();
        m.Write(ms, MagickFormat.Jpeg);
        _jpeg = ms.ToArray();
    }

    /// <summary>Gets the generated CMYK JPEG (deterministic: seeded generator, fixed Magick quality).</summary>
    public byte[] Jpeg => _jpeg;

    /// <summary>Gets the image width.</summary>
    public static int Width => W;

    /// <summary>Gets the image height.</summary>
    public static int Height => H;

    // The colour-accuracy check (gate-deltae) takes each library's sRGB bytes through these.
    public byte[] OneImageRgb(bool blackPointCompensation = true) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes<OI.Rgb24>(OI.Image.Load(_jpeg).ConvertColor(OI.ColorProfile.Srgb, blackPointCompensation: blackPointCompensation).ToArray<OI.Rgb24>()).ToArray();

    public byte[] ImageSharpRgb()
    {
        using SharpRgb img = SharpImage.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(_jpeg);
        byte[] rgb = new byte[W * H * 3];
        img.CopyPixelDataTo(rgb);
        return rgb;
    }

    public byte[]? SkiaRgb()
    {
        using SKBitmap? bmp = SKBitmap.Decode(_jpeg);
        if (bmp == null)
        {
            return null;
        }

        using SKBitmap rgba = bmp.Copy(SKColorType.Rgba8888);
        ReadOnlySpan<byte> px = rgba.GetPixelSpan();
        byte[] rgb = new byte[W * H * 3];
        for (int i = 0; i < W * H; i++)
        {
            rgb[3 * i] = px[4 * i];
            rgb[(3 * i) + 1] = px[(4 * i) + 1];
            rgb[(3 * i) + 2] = px[(4 * i) + 2];
        }

        return rgb;
    }

    public byte[] NetVipsRgb()
    {
        using VipsImage img = VipsImage.NewFromBuffer(_jpeg);
        using VipsImage rgb = img.IccTransform("srgb", embedded: true);
        return rgb.WriteToMemory<byte>();
    }

    public byte[] MagickRgb()
    {
        using var m = new MagickImage(_jpeg);
        m.RenderingIntent = ImageMagick.RenderingIntent.Relative;
        m.TransformColorSpace(m.GetColorProfile()!, _srgb);
        return m.GetPixels().ToByteArray(PixelMapping.RGB)!;
    }

    [BenchmarkCategory("Gate", "cmyk-jpeg-to-srgb")]
    [Benchmark(Description = "OneImage Load + ConvertColor(sRGB) + ToArray<Rgb24>")]
    public int OneImageCmyk() => OI.Image.Load(_jpeg).ConvertColor(OI.ColorProfile.Srgb).ToArray<OI.Rgb24>().Length;

    [BenchmarkCategory("Gate", "cmyk-jpeg-to-srgb")]
    [Benchmark(Description = "Magick.NET (LittleCMS) read + profile transform")]
    public int MagickCmyk()
    {
        using var m = new MagickImage(_jpeg);
        m.RenderingIntent = ImageMagick.RenderingIntent.Relative;
        m.TransformColorSpace(m.GetColorProfile()!, _srgb);
        return (int)m.Width;
    }

    [BenchmarkCategory("Gate", "cmyk-jpeg-to-srgb")]
    [Benchmark(Description = "ImageSharp Load<Rgb24> (skips ICC conversion)")]
    public int ImageSharpCmyk()
    {
        using SharpRgb img = SharpImage.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(_jpeg);
        return img.Width;
    }

    [BenchmarkCategory("Gate", "cmyk-jpeg-to-srgb")]
    [Benchmark(Description = "SkiaSharp SKBitmap.Decode to Rgba8888")]
    public int SkiaCmyk()
    {
        using SKBitmap? bmp = SKBitmap.Decode(_jpeg);
        return bmp?.Width ?? 0;
    }

    [BenchmarkCategory("Gate", "cmyk-jpeg-to-srgb")]
    [Benchmark(Description = "NetVips load + icc_transform(sRGB)")]
    public int NetVipsCmyk()
    {
        using VipsImage img = VipsImage.NewFromBuffer(_jpeg);
        using VipsImage rgb = img.IccTransform("srgb", embedded: true);
        return rgb.WriteToMemory<byte>().Length;
    }
}

/// <summary>Animated GIF (50 frames, 500x500) resized to 200 px wide and saved as GIF. SkiaSharp cannot write animated GIFs.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: GateJob.Warmup, iterationCount: GateJob.Iterations)]
public class GifResizeGateBenchmarks
{
    private byte[] _gif = null!;

    [GlobalSetup]
    public void Setup() => _gif = GifBenchData.Animation();

    [BenchmarkCategory("Gate", "gif-resize")]
    [Benchmark(Description = "OneImage Load + Resize(200) + Save GIF")]
    public long OneImageGifResize()
    {
        using var ms = new MemoryStream();
        OI.Image.Load(_gif).Resize(width: 200).Save(ms, OI.GifFormat.Instance);
        return ms.Length;
    }

    [BenchmarkCategory("Gate", "gif-resize")]
    [Benchmark(Description = "ImageSharp Load + Resize(200) + Save GIF")]
    public long ImageSharpGifResize()
    {
        using SharpImage img = SharpImage.Load(_gif);
        img.Mutate(x => x.Resize(200, 0));
        using var ms = new MemoryStream();
        img.Save(ms, new GifEncoder());
        return ms.Length;
    }

    [BenchmarkCategory("Gate", "gif-resize")]
    [Benchmark(Description = "Magick.NET coalesce + Resize(200) + Write GIF")]
    public long MagickGifResize()
    {
        using var coll = new MagickImageCollection(_gif);
        coll.Coalesce();
        foreach (IMagickImage<byte> im in coll)
        {
            im.Resize(200, 0);
        }

        using var ms = new MemoryStream();
        coll.Write(ms, MagickFormat.Gif);
        return ms.Length;
    }

    [BenchmarkCategory("Gate", "gif-resize")]
    [Benchmark(Description = "NetVips n=-1 + resize(0.4) + gifsave_buffer")]
    public long NetVipsGifResize()
    {
        using VipsImage img = VipsImage.NewFromBuffer(_gif, "n=-1");
        using VipsImage small = img.Resize(0.4);
        return small.GifsaveBuffer().Length;
    }
}
