using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Formats.Gif;
using ImageMagick;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using SkiaSharp;
using IS = SixLabors.ImageSharp;
using Rgba32Is = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace OneImage.Benchmarks.Gif;

[MemoryDiagnoser]
[ShortRunJob]
public class GifDecodeBenchmarks
{
    private byte[] _gif = null!;

    [GlobalSetup]
    public void Setup() => _gif = GifBenchData.Animation();

    [BenchmarkCategory("Gate", "gif-decode")]
    [Benchmark(Baseline = true, Description = "oneimage: decode 50 frames 500x500")]
    public long OneImageDecode()
    {
        // Validate decodes every frame up front into the image's tiles, like the other libraries' eager decode.
        Image img = Image.Load(_gif, new LoadOptions { Validate = true });
        return img.FrameCount;
    }

    [Benchmark(Description = "oneimage: decode + read every frame out")]
    public long OneImageDecodeAndRead()
    {
        Image img = Image.Load(_gif);
        long sum = 0;
        var buf = new Rgba32[img.Width * img.Height];
        for (int i = 0; i < img.FrameCount; i++)
        {
            img.Frames[i].CopyPixelsTo<Rgba32>(buf, new ImageRect(0, 0, img.Width, img.Height), maxDegree: 1);
            sum += buf[^1].R;
        }

        return sum;
    }

    [BenchmarkCategory("Gate", "gif-decode")]
    [Benchmark(Description = "ImageSharp: decode")]
    public long ImageSharpDecode()
    {
        using IS.Image<Rgba32Is> img = IS.Image.Load<Rgba32Is>(_gif);
        return img.Frames.Count;
    }

    [BenchmarkCategory("Gate", "gif-decode")]
    [Benchmark(Description = "SkiaSharp: decode all frames")]
    public long SkiaDecode()
    {
        using var codec = SKCodec.Create(new MemoryStream(_gif));
        SKImageInfo info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        long sum = 0;
        for (int i = 0; i < codec.FrameCount; i++)
        {
            codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(i, i - 1));
            sum += bmp.GetPixelSpan()[0];
        }

        return sum;
    }

    [BenchmarkCategory("Gate", "gif-decode")]
    [Benchmark(Description = "Magick.NET: decode + coalesce")]
    public long MagickDecode()
    {
        using var coll = new MagickImageCollection(_gif);
        coll.Coalesce();
        return coll.Count;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class GifEncodeBenchmarks : IDisposable
{
    private byte[] _gif = null!;
    private Image _ours = null!;
    private IS.Image<Rgba32Is> _sharp = null!;
    private MagickImageCollection _magick = null!;

    [GlobalSetup]
    public void Setup()
    {
        _gif = GifBenchData.Animation();
        _ours = Image.Load(_gif, new LoadOptions { Validate = true });
        _sharp = IS.Image.Load<Rgba32Is>(_gif);
        _magick = new MagickImageCollection(_gif);
        _magick.Coalesce();
    }

    [GlobalCleanup]
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sharp.Dispose();
            _magick.Dispose();
        }
    }

    [BenchmarkCategory("Gate", "gif-encode")]
    [Benchmark(Baseline = true, Description = "oneimage: re-encode 50 frames")]
    public long OneImageEncode()
    {
        using var ms = new MemoryStream();
        _ours.Save(ms, GifFormat.Instance);
        return ms.Length;
    }

    [BenchmarkCategory("Gate", "gif-encode")]
    [Benchmark(Description = "ImageSharp: re-encode")]
    public long ImageSharpEncode()
    {
        using var ms = new MemoryStream();
        _sharp.Save(ms, new GifEncoder());
        return ms.Length;
    }

    [BenchmarkCategory("Gate", "gif-encode")]
    [Benchmark(Description = "Magick.NET: re-encode")]
    public long MagickEncode()
    {
        using var ms = new MemoryStream();
        _magick.Write(ms, MagickFormat.Gif);
        return ms.Length;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class QuantizeBenchmarks
{
    private byte[] _rgba = null!;
    private int _w;
    private int _h;
    private Image _ours = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rgba = GifBenchData.PhotoRgba(out _w, out _h);
        var px = new Rgba32[_w * _h];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = new Rgba32(_rgba[i * 4], _rgba[(i * 4) + 1], _rgba[(i * 4) + 2], 255);
        }

        _ours = Image.FromPixels<Rgba32>(px, _w, _h);
    }

    [Benchmark(Baseline = true, Description = "oneimage: Quantize(256) 2000x1500")]
    public int OneImageQuantize()
    {
        Rgba32[] r = _ours.Quantize(256).ToArray<Rgba32>();
        return r[^1].R;
    }

    [Benchmark(Description = "ImageSharp: Wu 256, no dither")]
    public int ImageSharpWu()
    {
        using IS.Image<Rgba32Is> img = IS.Image.LoadPixelData<Rgba32Is>(_rgba, _w, _h);
        img.Mutate(x => x.Quantize(new WuQuantizer(new QuantizerOptions { MaxColors = 256, Dither = null })));
        return img[0, 0].R;
    }

    [Benchmark(Description = "Magick.NET: Quantize 256, no dither")]
    public int MagickQuantize()
    {
        using var img = new MagickImage(MagickColors.Black, (uint)_w, (uint)_h);
        img.ImportPixels(_rgba, new PixelImportSettings((uint)_w, (uint)_h, StorageType.Char, PixelMapping.RGBA));
        img.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.No });
        return img.Width > 0 ? 1 : 0;
    }
}
