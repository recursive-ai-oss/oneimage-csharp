using System.IO;
using BenchmarkDotNet.Attributes;
using SharpRgb24 = SixLabors.ImageSharp.PixelFormats.Rgb24;

namespace OneImage.Benchmarks.Png;

/// <summary>Deterministic stand-ins for a photo and a screenshot (RGB8, about 2000x1500).</summary>
internal static class PngBenchData
{
    public const int W = 2000;
    public const int H = 1500;

    public static byte[] Photo()
    {
        var rnd = new Random(4);
        byte[] b = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double v = 110 + (60 * Math.Sin((x + (c * 40)) / 97.0)) + (50 * Math.Cos((y - (c * 25)) / 71.0)) + (20 * Math.Sin((x + y) / 13.0)) + rnd.Next(-4, 5);
                    b[(((y * W) + x) * 3) + c] = (byte)Math.Clamp((int)v, 0, 255);
                }
            }
        }

        return b;
    }

    public static byte[] Screenshot()
    {
        var rnd = new Random(6);
        byte[] b = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                bool panel = ((x / 300) + (y / 200)) % 2 == 0;
                byte bg = panel ? (byte)245 : (byte)32;
                int o = ((y * W) + x) * 3;
                b[o] = bg;
                b[o + 1] = bg;
                b[o + 2] = (byte)(bg ^ 8);
            }
        }

        for (int t = 0; t < W * H / 250; t++)
        {
            int x0 = rnd.Next(W - 8);
            int y0 = rnd.Next(H - 12);
            byte ink = rnd.Next(2) == 0 ? (byte)10 : (byte)230;
            for (int dy = 0; dy < 10; dy++)
            {
                for (int dx = 0; dx < 6; dx++)
                {
                    if (((dx * 7) + (dy * 3) + t) % 4 < 2)
                    {
                        int o = (((y0 + dy) * W) + x0 + dx) * 3;
                        b[o] = ink;
                        b[o + 1] = ink;
                        b[o + 2] = ink;
                    }
                }
            }
        }

        // Anti-aliased gradient strip, as in UI chrome.
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = ((y * W) + x) * 3;
                b[o] = (byte)(x * 255 / W);
                b[o + 1] = (byte)(y * 4);
                b[o + 2] = 128;
            }
        }

        return b;
    }

    /// <summary>Encodes with SkiaSharp (libpng) so every decoder reads the same neutral file.</summary>
    public static byte[] SkiaPng(byte[] rgb)
    {
        byte[] rgba = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            rgba[i * 4] = rgb[i * 3];
            rgba[(i * 4) + 1] = rgb[(i * 3) + 1];
            rgba[(i * 4) + 2] = rgb[(i * 3) + 2];
            rgba[(i * 4) + 3] = 255;
        }

        using var bmp = new SkiaSharp.SKBitmap(W, H, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque);
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        using SkiaSharp.SKData data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

[MemoryDiagnoser]
public class PngDecodeBenchmarks
{
    private byte[] _file = null!;
    private SharpRgb24[] _sink = null!;

    [Params("photo", "screenshot")]
    public string Content { get; set; } = "photo";

    [GlobalSetup]
    public void Setup()
    {
        _file = PngBenchData.SkiaPng(Content == "photo" ? PngBenchData.Photo() : PngBenchData.Screenshot());
        _sink = new SharpRgb24[PngBenchData.W * PngBenchData.H];
        Console.WriteLine($"{Content}: PNG file {_file.Length} bytes");
    }

    [Benchmark(Description = "OneImage")]
    public SharpRgb24 OneImageDecode()
    {
        Image img = Image.Load(_file);
        img.CopyPixelsTo<OneImage.Rgb24>(System.Runtime.InteropServices.MemoryMarshal.Cast<SharpRgb24, OneImage.Rgb24>(_sink.AsSpan()), new ImageRect(0, 0, img.Width, img.Height));
        return _sink[^1];
    }

    private byte[]? _zlib;
    private byte[]? _inflated;

    private sealed class Discard : IPngRowTarget
    {
        public int Rows;

        void IPngRowTarget.Rows(int y, ReadOnlySpan<byte> rows, int count) => Rows += count;
    }

    [Benchmark(Description = "OneImage codec only (no tiles)")]
    public int OneImageDecoderOnly()
    {
        using var ms = new MemoryStream(_file);
        ImageInfo info = PngDecoder.Instance.ReadInfo(ms, ImageLimits.Default);
        var target = new Discard();
        PngImageReader.Decode(ms, (PngInfo)info.DecoderState!, ((PngInfo)info.DecoderState!).Frames[0], target, int.MaxValue, ImageLimits.Default, default);
        return target.Rows;
    }

    [Benchmark(Description = "OneImage inflate only")]
    public int OneImageInflateOnly()
    {
        if (_zlib == null)
        {
            var z = new MemoryStream();
            int p = 8;
            while (p + 12 <= _file.Length)
            {
                int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(_file.AsSpan(p));
                if (_file[p + 4] == (byte)'I' && _file[p + 5] == (byte)'D')
                {
                    z.Write(_file, p + 8, len);
                }

                p += 12 + len;
            }

            _zlib = z.ToArray();
            _inflated = new byte[PngBenchData.H * ((PngBenchData.W * 3) + 1)];
        }

        using var inf = new OneImage.Compression.Inflater(true, _inflated!.Length + 1000, 0);
        inf.Inflate(_zlib, _inflated, out _, out int written);
        return written;
    }

    [Benchmark(Description = "ImageSharp")]
    public SharpRgb24 ImageSharpDecode()
    {
        using var img = SixLabors.ImageSharp.Image.Load<SharpRgb24>(_file);
        img.CopyPixelDataTo(_sink);
        return _sink[^1];
    }

    [Benchmark(Description = "SkiaSharp")]
    public byte SkiaDecode()
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(_file, new SkiaSharp.SKImageInfo(PngBenchData.W, PngBenchData.H, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque));
        return bmp.GetPixelSpan()[^1];
    }

    [Benchmark(Description = "NetVips")]
    public byte NetVipsDecode()
    {
        using var img = NetVips.Image.NewFromBuffer(_file);
        byte[] raw = img.WriteToMemory<byte>();
        return raw[^1];
    }
}

[MemoryDiagnoser]
public class PngEncodeBenchmarks : IDisposable
{
    private byte[] _rgb = null!;
    private Image _ours = null!;
    private SixLabors.ImageSharp.Image<SharpRgb24> _sharp = null!;
    private SkiaSharp.SKBitmap _skia = null!;
    private NetVips.Image _vips = null!;

    [Params("photo", "screenshot")]
    public string Content { get; set; } = "photo";

    [GlobalSetup]
    public void Setup()
    {
        _rgb = Content == "photo" ? PngBenchData.Photo() : PngBenchData.Screenshot();
        _ours = Image.FromPixels<OneImage.Rgb24>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OneImage.Rgb24>(_rgb), PngBenchData.W, PngBenchData.H);
        _sharp = SixLabors.ImageSharp.Image.LoadPixelData<SharpRgb24>(_rgb, PngBenchData.W, PngBenchData.H);
        byte[] rgba = new byte[PngBenchData.W * PngBenchData.H * 4];
        for (int i = 0; i < PngBenchData.W * PngBenchData.H; i++)
        {
            rgba[i * 4] = _rgb[i * 3];
            rgba[(i * 4) + 1] = _rgb[(i * 3) + 1];
            rgba[(i * 4) + 2] = _rgb[(i * 3) + 2];
            rgba[(i * 4) + 3] = 255;
        }

        _skia = new SkiaSharp.SKBitmap(PngBenchData.W, PngBenchData.H, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque);
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, _skia.GetPixels(), rgba.Length);
        _vips = NetVips.Image.NewFromMemory(_rgb, PngBenchData.W, PngBenchData.H, 3, NetVips.Enums.BandFormat.Uchar);
        Console.WriteLine($"{Content}: sizes ours {Ours().Length}, ImageSharp {Sharp().Length}, SkiaSharp {Skia().Length}, NetVips {Vips().Length}");
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sharp.Dispose();
        _skia.Dispose();
        _vips.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "OneImage")]
    public byte[] Ours()
    {
        using var ms = new MemoryStream(1 << 20);
        _ours.Save(ms, PngFormat.Instance);
        return ms.ToArray();
    }

    private byte[] OursWith(PngEncoderOptions options)
    {
        using var ms = new MemoryStream(1 << 20);
        _ours.Save(ms, PngFormat.Instance, new SaveOptions { EncoderOptions = options });
        return ms.ToArray();
    }

    [Benchmark(Description = "OneImage level 4")]
    public byte[] OursLevel4() => OursWith(new PngEncoderOptions { CompressionLevel = 4 });

    [Benchmark(Description = "OneImage level 5")]
    public byte[] OursLevel5() => OursWith(new PngEncoderOptions { CompressionLevel = 5 });

    [Benchmark(Description = "OneImage level 1")]
    public byte[] OursLevel1() => OursWith(new PngEncoderOptions { CompressionLevel = 1 });

    [Benchmark(Description = "OneImage filter none (deflate cost)")]
    public byte[] OursNoFilter() => OursWith(new PngEncoderOptions { Filter = PngFilterMode.None });

    [Benchmark(Description = "OneImage level 0 (filter cost)")]
    public byte[] OursStored() => OursWith(new PngEncoderOptions { CompressionLevel = 0 });

    [Benchmark(Description = "ImageSharp")]
    public byte[] Sharp()
    {
        using var ms = new MemoryStream(1 << 20);
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(_sharp, ms);
        return ms.ToArray();
    }

    [Benchmark(Description = "SkiaSharp")]
    public byte[] Skia()
    {
        using SkiaSharp.SKData data = _skia.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Benchmark(Description = "NetVips")]
    public byte[] Vips() => _vips.PngsaveBuffer();
}
