using System.IO;
using OneImage.Kernels;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SkiaSharp;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpRgb = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>;
using VipsImage = NetVips.Image;

namespace OneImage.Benchmarks.Jpeg;

/// <summary>A 24 MP photograph-like frame and the same picture as a 4:2:0 baseline JPEG written by an independent encoder.</summary>
internal static class JpegData
{
    public const int Width = 6000;
    public const int Height = 4000;

    public static OneImage.Rgb24[] Pixels()
    {
        var px = new OneImage.Rgb24[Width * Height];
        var rnd = new Random(7);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                double r = 128 + (90 * Math.Sin((x / 137.0) + (y / 253.0)));
                double g = 128 + (80 * Math.Cos((y / 129.0) - (x / 371.0)));
                double b = 60 + (x * 140.0 / Width) + (y * 50.0 / Height);
                if (((x / 400) + (y / 300)) % 3 == 0)
                {
                    r = 230 - (r * 0.3);
                }

                double tex = rnd.Next(-7, 8) + (5 * Math.Sin(x * 0.9) * Math.Cos(y * 0.7));
                px[(y * Width) + x] = new OneImage.Rgb24(C(r + tex), C(g + tex), C(b + tex));
            }
        }

        return px;
    }

    private static byte C(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    public static byte[] SkiaJpeg(OneImage.Rgb24[] px, int quality)
    {
        byte[] rgba = new byte[px.Length * 4];
        for (int i = 0; i < px.Length; i++)
        {
            rgba[4 * i] = px[i].R;
            rgba[(4 * i) + 1] = px[i].G;
            rgba[(4 * i) + 2] = px[i].B;
            rgba[(4 * i) + 3] = 255;
        }

        using var data = SKData.CreateCopy(rgba);
        using SKImage image = SKImage.FromPixels(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque), data, Width * 4);
        using SKPixmap pm = image.PeekPixels();
        using var ms = new MemoryStream();
        using (var w = new SKManagedWStream(ms))
        {
            pm.Encode(w, new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
        }

        return ms.ToArray();
    }
}

/// <summary>Decode a 24 MP 4:2:0 baseline JPEG to RGB: full size and at 1/8 (shrink-on-load).</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class JpegDecodeBenchmarks
{
    private byte[] _jpeg = null!;
    private OneImage.Rgb24[] _decoded = [];

    [GlobalSetup]
    public void Setup()
    {
        _jpeg = JpegData.SkiaJpeg(JpegData.Pixels(), 90);
        _decoded = new OneImage.Rgb24[JpegData.Width * JpegData.Height];
    }

    // ImageSharp decodes into pooled image memory that is reused from one load to the next, so a decode that allocates a fresh
    // 72 MB array per call is charged for something that row does not pay. This row decodes into a buffer the caller keeps, as an
    // application decoding many files would; the ToArray row shows the allocating form.
    [BenchmarkCategory("Gate", "jpeg-decode")]
    [Benchmark(Description = "OneImage Image.Load + CopyPixelsTo reused Rgb24 buffer, 24 MP")]
    public int OneImageFull()
    {
        OneImage.Image img = OneImage.Image.Load(_jpeg);
        img.CopyPixelsTo<OneImage.Rgb24>(_decoded, new ImageRect(0, 0, img.Width, img.Height));
        return img.Width;
    }

    [BenchmarkCategory("jpeg-decode-alloc")]
    [Benchmark(Description = "OneImage Image.Load + ToArray<Rgb24> (allocates), 24 MP")]
    public int OneImageFullToArray() => OneImage.Image.Load(_jpeg).ToArray<OneImage.Rgb24>().Length;

    [Benchmark(Description = "OneImage decoder only (rows into a reused buffer), 24 MP")]
    public long OneImageCodecFull() => JpegBenchHelpers.DecodeInto(_jpeg, 1);

    [BenchmarkCategory("Gate", "jpeg-decode")]
    [Benchmark(Description = "SkiaSharp SKBitmap.Decode, 24 MP", Baseline = true)]
    public int SkiaFull()
    {
        using SKBitmap bmp = SKBitmap.Decode(_jpeg);
        return bmp.Width;
    }

    [BenchmarkCategory("Gate", "jpeg-decode")]
    [Benchmark(Description = "ImageSharp Image.Load<Rgb24>, 24 MP")]
    public int ImageSharpFull()
    {
        using SharpRgb img = SharpImage.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(_jpeg);
        return img.Width;
    }

    [BenchmarkCategory("Gate", "jpeg-decode")]
    [Benchmark(Description = "NetVips new_from_buffer + write_to_memory, 24 MP")]
    public int NetVipsFull()
    {
        using VipsImage img = VipsImage.NewFromBuffer(_jpeg);
        return img.WriteToMemory<byte>().Length;
    }

    [Benchmark(Description = "OneImage Image.Load + Resize(750) (shrink-on-load), 1/8")]
    public int OneImageEighth() => OneImage.Image.Load(_jpeg).Resize(width: 750).ToArray<OneImage.Rgb24>().Length;

    [Benchmark(Description = "OneImage decoder only at 1/8")]
    public long OneImageCodecEighth() => JpegBenchHelpers.DecodeInto(_jpeg, 8);

    [Benchmark(Description = "SkiaSharp SKCodec scaled decode, 1/8")]
    public int SkiaEighth()
    {
        using var codec = SKCodec.Create(new MemoryStream(_jpeg));
        SKSizeI size = codec.GetScaledDimensions(1f / 8);
        using var bmp = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        codec.GetPixels(bmp.Info, bmp.GetPixels());
        return bmp.Width;
    }

    [Benchmark(Description = "ImageSharp load with TargetSize (DCT scaling), 1/8")]
    public int ImageSharpEighth()
    {
        var options = new DecoderOptions { TargetSize = new Size(750, 500) };
        using SharpRgb img = SharpImage.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(options, _jpeg);
        return img.Width;
    }

    [Benchmark(Description = "NetVips new_from_buffer shrink=8 + write_to_memory, 1/8")]
    public int NetVipsEighth()
    {
        using VipsImage img = VipsImage.NewFromBuffer(_jpeg, "shrink=8");
        return img.WriteToMemory<byte>().Length;
    }
}

/// <summary>Encode a 24 MP RGB image at quality 90, 4:2:0.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class JpegEncodeBenchmarks : IDisposable
{
    private OneImage.Image _ours = null!;
    private SharpRgb _sharp = null!;
    private SKBitmap _skia = null!;
    private VipsImage _vips = null!;
    private SKData _skiaData = null!;

    [GlobalSetup]
    public void Setup()
    {
        OneImage.Rgb24[] px = JpegData.Pixels();
        _ours = OneImage.Image.FromPixels<OneImage.Rgb24>(px, JpegData.Width, JpegData.Height);
        byte[] raw = new byte[px.Length * 3];
        System.Runtime.InteropServices.MemoryMarshal.Cast<OneImage.Rgb24, byte>(px).CopyTo(raw);
        _sharp = SharpImage.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgb24>(raw, JpegData.Width, JpegData.Height);
        byte[] rgba = new byte[px.Length * 4];
        for (int i = 0; i < px.Length; i++)
        {
            rgba[4 * i] = px[i].R;
            rgba[(4 * i) + 1] = px[i].G;
            rgba[(4 * i) + 2] = px[i].B;
            rgba[(4 * i) + 3] = 255;
        }

        _skiaData = SKData.CreateCopy(rgba);
        _skia = new SKBitmap();
        _skia.InstallPixels(new SKImageInfo(JpegData.Width, JpegData.Height, SKColorType.Rgba8888, SKAlphaType.Opaque), _skiaData.Data, JpegData.Width * 4);
        _vips = VipsImage.NewFromMemory(raw, JpegData.Width, JpegData.Height, 3, global::NetVips.Enums.BandFormat.Uchar);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _sharp.Dispose();
        _skia.Dispose();
        _skiaData.Dispose();
        _vips.Dispose();
        GC.SuppressFinalize(this);
    }

    [BenchmarkCategory("Gate", "jpeg-encode")]
    [Benchmark(Description = "OneImage q90 4:2:0 optimized tables (default)")]
    public int OneImageOptimized() => Save(new JpegEncoderOptions { Quality = 90 });

    [Benchmark(Description = "OneImage q90 4:2:0 standard tables (streaming)")]
    public int OneImageStandard() => Save(new JpegEncoderOptions { Quality = 90, OptimizeHuffman = false });

    private int Save(JpegEncoderOptions o)
    {
        using var ms = new MemoryStream(8 << 20);
        _ours.Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = o });
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "jpeg-encode")]
    [Benchmark(Description = "SkiaSharp q90 4:2:0", Baseline = true)]
    public int Skia()
    {
        using var ms = new MemoryStream(8 << 20);
        using SKImage image = SKImage.FromBitmap(_skia);
        using SKPixmap pm = image.PeekPixels();
        using (var w = new SKManagedWStream(ms))
        {
            pm.Encode(w, new SKJpegEncoderOptions(90, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
        }

        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "jpeg-encode")]
    [Benchmark(Description = "ImageSharp q90 4:2:0")]
    public int ImageSharp()
    {
        using var ms = new MemoryStream(8 << 20);
        _sharp.Save(ms, new JpegEncoder { Quality = 90, ColorType = JpegEncodingColor.YCbCrRatio420 });
        return (int)ms.Length;
    }

    [BenchmarkCategory("Gate", "jpeg-encode")]
    [Benchmark(Description = "NetVips q90 4:2:0")]
    public int Vips() => _vips.JpegsaveBuffer(q: 90, subsampleMode: global::NetVips.Enums.ForeignSubsample.On).Length;
}

internal static class JpegBenchHelpers
{
    private sealed class ReuseSink : IRowSink
    {
        private readonly byte[] _buffer;
        private readonly int _rowBytes;

        public ReuseSink(int width, int height, PixelFormat format, byte[] buffer)
        {
            Width = width;
            Height = height;
            Format = format;
            _buffer = buffer;
            _rowBytes = width * format.BytesPerPixel;
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format { get; }

        public long Total { get; private set; }

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
            // Copy into a band-sized scratch so the cost of delivering rows is included but no full frame is kept.
            int n = Math.Min(rows.Length, _buffer.Length);
            rows[..n].CopyTo(_buffer);
            Total += rowCount * _rowBytes;
        }
    }

    [ThreadStatic]
    private static byte[]? t_scratch;

    public static long DecodeInto(byte[] jpeg, int scale)
    {
        using var ms = new MemoryStream(jpeg);
        ImageInfo info = JpegFormat.Instance.ReadInfo(ms, ImageLimits.Default);
        FrameInfo f = info.Frames[0];
        int w = (f.Width + scale - 1) / scale;
        int h = (f.Height + scale - 1) / scale;
        t_scratch ??= new byte[1 << 20];
        var sink = new ReuseSink(w, h, f.Format, t_scratch);
        JpegFormat.Instance.DecodeFrame(ms, info, 0, new DecodeRequest(new ImageRect(0, 0, w, h), scale), sink, ImageLimits.Default, CancellationToken.None);
        return sink.Total;
    }
}

/// <summary>The 8x8 transforms alone, 256 blocks per invocation (results are per invocation; divide by 256 for a block).</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 15)]
public class JpegTransformKernelBenchmarks
{
    private const int Blocks = 256;
    private short[] _coef = null!;
    private int[] _quant = null!;
    private byte[] _plane = null!;
    private byte[] _dst = null!;
    private uint[] _recip = null!;
    private short[] _out = new short[64];
    private int[] _fwd = new int[64];

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(5);
        _coef = new short[64 * Blocks];
        for (int b = 0; b < Blocks; b++)
        {
            // Photograph-like blocks: a DC term and a decaying handful of AC terms.
            _coef[b * 64] = (short)rnd.Next(-200, 200);
            for (int i = 1; i < 64; i++)
            {
                if (rnd.Next(64) < 24 - (i / 3))
                {
                    _coef[(b * 64) + i] = (short)rnd.Next(-12, 13);
                }
            }
        }

        _quant = new int[64];
        Array.Fill(_quant, 8);
        _recip = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(_quant, JpegFdct.Reciprocal));
        _plane = new byte[64 * 64];
        rnd.NextBytes(_plane);
        _dst = new byte[64 * 64];
    }

    [Benchmark(Description = "IDCT dispatch")]
    public void IdctDispatch()
    {
        for (int b = 0; b < Blocks; b++)
        {
            JpegIdct.Idct8(_coef.AsSpan(b * 64, 64), _quant, _dst.AsSpan(((b & 7) * 8 * 64) + ((b >> 3 & 7) * 8)), 64);
        }
    }

    [Benchmark(Description = "IDCT scalar")]
    public void IdctScalar()
    {
        for (int b = 0; b < Blocks; b++)
        {
            JpegIdct.Idct8Scalar(_coef.AsSpan(b * 64, 64), _quant, _dst.AsSpan(((b & 7) * 8 * 64) + ((b >> 3 & 7) * 8)), 64);
        }
    }

    [Benchmark(Description = "FDCT+quantize dispatch")]
    public void ForwardQuantizeDispatch()
    {
        for (int b = 0; b < Blocks; b++)
        {
            JpegFdct.ForwardQuantize(_plane.AsSpan(((b & 7) * 8 * 64) + ((b >> 3 & 7) * 8)), 64, _quant, _recip, OneImage.Codecs.Jpeg.JpegConstants.Zigzag, _out);
        }
    }

    [Benchmark(Description = "FDCT scalar")]
    public void ForwardScalar()
    {
        for (int b = 0; b < Blocks; b++)
        {
            JpegFdct.Forward(_plane.AsSpan(((b & 7) * 8 * 64) + ((b >> 3 & 7) * 8)), 64, _fwd);
        }
    }
}
