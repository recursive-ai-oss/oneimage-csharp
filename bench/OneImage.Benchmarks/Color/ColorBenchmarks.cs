using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using ImageMagick;

namespace OneImage.Benchmarks;

/// <summary>
/// 24 MP (6000x4000) profile conversions: 8-bit RGB sRGB to Display P3, and CMYK to sRGB. Magick.NET runs LittleCMS (8-bit quantum build).
/// ImageSharp 3.x has no ICC profile conversion for pixel buffers, so it has no row here. Each iteration builds a fresh lazy
/// conversion over the same source so no computed tiles are reused; Magick rows include cloning the source image.
/// </summary>
[MemoryDiagnoser]
[InProcess]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 5)]
public class ColorBenchmarks : IDisposable
{
    private const int W = 6000;
    private const int H = 4000;
    private Image _rgb = null!;
    private Image _cmyk = null!;
    private Rgb24[] _rgbOut = null!;
    private MagickImage _mRgb = null!;
    private MagickImage _mCmyk = null!;
    private ImageMagick.ColorProfile _srgb = null!;
    private ImageMagick.ColorProfile _p3 = null!;
    private ImageMagick.ColorProfile _cmykProfile = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(3);
        byte[] rgb = new byte[W * H * 3];
        byte[] cmyk = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 3;
                rgb[i] = (byte)((x * 255 / W) ^ rnd.Next(8));
                rgb[i + 1] = (byte)(y * 255 / H);
                rgb[i + 2] = (byte)(((x + y) * 255 / (W + H)) ^ rnd.Next(8));
                int j = ((y * W) + x) * 4;
                cmyk[j] = (byte)(x * 200 / W);
                cmyk[j + 1] = (byte)(y * 200 / H);
                cmyk[j + 2] = (byte)(rnd.Next(60));
                cmyk[j + 3] = (byte)((x + y) * 80 / (W + H));
            }
        }

        var rgbPixels = new Rgb24[W * H];
        var cmykPixels = new Cmyk32[W * H];
        for (int i = 0; i < W * H; i++)
        {
            rgbPixels[i] = new Rgb24(rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2]);
            cmykPixels[i] = new Cmyk32(cmyk[i * 4], cmyk[(i * 4) + 1], cmyk[(i * 4) + 2], cmyk[(i * 4) + 3]);
        }

        _rgb = Image.FromPixels<Rgb24>(rgbPixels, W, H);
        using (var c = new Canvas(W, H, PixelFormat.Cmyk8))
        {
            c.CopyFrom<Cmyk32>(cmykPixels, new ImageRect(0, 0, W, H));
            _cmyk = c.Snapshot();
        }

        _rgbOut = new Rgb24[W * H];
        _srgb = new ImageMagick.ColorProfile(ColorProfile.Srgb.IccBytes.ToArray());
        _p3 = new ImageMagick.ColorProfile(ColorProfile.DisplayP3.IccBytes.ToArray());
        _cmykProfile = new ImageMagick.ColorProfile(ColorProfile.GenericCmyk.IccBytes.ToArray());
        _mRgb = new MagickImage(rgb, new PixelReadSettings((uint)W, (uint)H, StorageType.Char, "RGB")) { ColorSpace = ColorSpace.sRGB };
        _mCmyk = new MagickImage(cmyk, new PixelReadSettings((uint)W, (uint)H, StorageType.Char, "CMYK")) { ColorSpace = ColorSpace.CMYK };
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _mRgb?.Dispose();
        _mCmyk?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "OneImage RGB sRGB->P3 24MP")]
    public void OursRgbToP3() =>
        _rgb.ConvertColor(ColorProfile.DisplayP3).CopyPixelsTo<Rgb24>(_rgbOut, new ImageRect(0, 0, W, H));

    [Benchmark(Description = "Magick.NET (LittleCMS) RGB sRGB->P3 24MP")]
    public void MagickRgbToP3()
    {
        using var c = (MagickImage)_mRgb.Clone();
        c.RenderingIntent = ImageMagick.RenderingIntent.Relative;
        c.TransformColorSpace(_srgb, _p3);
    }

    [Benchmark(Description = "OneImage CMYK->sRGB 24MP")]
    public void OursCmykToSrgb() =>
        _cmyk.ConvertColor(ColorProfile.Srgb).CopyPixelsTo<Rgb24>(_rgbOut, new ImageRect(0, 0, W, H));

    [Benchmark(Description = "Magick.NET (LittleCMS) CMYK->sRGB 24MP")]
    public void MagickCmykToSrgb()
    {
        using var c = (MagickImage)_mCmyk.Clone();
        c.RenderingIntent = ImageMagick.RenderingIntent.Relative;
        c.TransformColorSpace(_cmykProfile, _srgb);
    }
}
