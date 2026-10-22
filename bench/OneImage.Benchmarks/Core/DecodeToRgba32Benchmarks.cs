using System.IO;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Tiff.Constants;
using SkiaSharp;
using IsRgba = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace OneImage.Benchmarks.Core;

/// <summary>
/// Decoding the way callers do: bytes in, Rgba32 pixels out. One 2000x1500 photo-like picture is encoded once per format by an
/// independent encoder, so both libraries read the same file. "reused buffer" rows copy into a caller-owned array;
/// "ToArray" rows pay the array allocation too. ImageSharp rows load straight to Rgba32 (its pooled pixel memory is not
/// counted as managed allocation).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class DecodeToRgba32Benchmarks
{
    private byte[] _file = null!;
    private OneImage.Rgba32[] _out = [];

    [Params("webp-lossy", "png", "jpeg", "tiff")]
    public string Format { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        int w = Png.PngBenchData.W;
        int h = Png.PngBenchData.H;
        byte[] rgb = Png.PngBenchData.Photo();
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = rgb[i * 3];
            rgba[(i * 4) + 1] = rgb[(i * 3) + 1];
            rgba[(i * 4) + 2] = rgb[(i * 3) + 2];
            rgba[(i * 4) + 3] = 255;
        }

        if (Format == "tiff")
        {
            using var img = SixLabors.ImageSharp.Image.LoadPixelData<IsRgba>(rgba, w, h);
            using var ms = new MemoryStream();
            img.Save(ms, new TiffEncoder { Compression = SixLabors.ImageSharp.Formats.Tiff.Constants.TiffCompression.Deflate, HorizontalPredictor = TiffPredictor.Horizontal, PhotometricInterpretation = TiffPhotometricInterpretation.Rgb, BitsPerPixel = TiffBitsPerPixel.Bit24 });
            _file = ms.ToArray();
        }
        else
        {
            using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
            SKEncodedImageFormat f = Format == "png" ? SKEncodedImageFormat.Png : Format == "jpeg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp;
            using SKData data = bmp.Encode(f, Format == "png" ? 100 : 80);
            _file = data.ToArray();
        }

        _out = new OneImage.Rgba32[w * h];
        Console.WriteLine($"{Format}: {_file.Length} bytes");
    }

    [Benchmark(Description = "OneImage Load + CopyPixelsTo (reused buffer)")]
    public OneImage.Rgba32 OneImageReused()
    {
        Image img = Image.Load(_file);
        img.CopyPixelsTo<OneImage.Rgba32>(_out, new ImageRect(0, 0, img.Width, img.Height));
        return _out[0];
    }

    [Benchmark(Description = "OneImage Load + ToArray")]
    public int OneImageToArray() => Image.Load(_file).ToArray<OneImage.Rgba32>().Length;

    [Benchmark(Description = "ImageSharp 3.1.12 Load<Rgba32>")]
    public int ImageSharp()
    {
        using SixLabors.ImageSharp.Image<IsRgba> img = SixLabors.ImageSharp.Image.Load<IsRgba>(_file);
        return img.Width;
    }
}
