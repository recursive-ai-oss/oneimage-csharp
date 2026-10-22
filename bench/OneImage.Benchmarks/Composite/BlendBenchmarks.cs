using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Benchmarks.Jpeg;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpRgb = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Composite;

/// <summary>Watermark with a blend mode: a 600 x 200 RGBA overlay at (100, 100) on a 6000 x 4000 RGB picture; the result is written to an RGB buffer.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class BlendBenchmarks : IDisposable
{
    private const int W = JpegData.Width, H = JpegData.Height, OW = 600, OH = 200;
    private global::OneImage.Image _base = null!;
    private global::OneImage.Image _overlay = null!;
    private global::OneImage.Rgb24[] _out = null!;
    private SharpRgb _sharpBase = null!;
    private SharpRgba _sharpOverlay = null!;
    private byte[] _sharpOut = null!;

    [Params("Multiply", "Screen", "Overlay")]
    public string Mode { get; set; } = "Multiply";

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
                ov[i + 2] = (byte)y;
                ov[i + 3] = (byte)((x + y) % 7 == 0 ? 0 : 60 + ((x * 195) / OW));
            }
        }

        _overlay = global::OneImage.Image.FromPixels<global::OneImage.Rgba32>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, global::OneImage.Rgba32>(ov), OW, OH);
        _sharpBase = SharpImage.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgb24>(raw, W, H);
        _sharpOverlay = SharpImage.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(ov, OW, OH);
        _sharpOut = new byte[raw.Length];
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _sharpBase.Dispose();
        _sharpOverlay.Dispose();
        GC.SuppressFinalize(this);
    }

    private global::OneImage.BlendMode OurMode => Enum.Parse<global::OneImage.BlendMode>(Mode);

    private SixLabors.ImageSharp.PixelFormats.PixelColorBlendingMode TheirMode => Enum.Parse<SixLabors.ImageSharp.PixelFormats.PixelColorBlendingMode>(Mode);

    [Benchmark(Description = "OneImage Composite(blend) + CopyPixelsTo")]
    public int OneImage()
    {
        _base.Composite(_overlay, 100, 100, 1f, false, OurMode).CopyPixelsTo<global::OneImage.Rgb24>(_out, new ImageRect(0, 0, W, H));
        return _out[0].R;
    }

    [Benchmark(Baseline = true, Description = "ImageSharp Clone + DrawImage(mode) + CopyPixelDataTo")]
    public int ImageSharp()
    {
        SixLabors.ImageSharp.PixelFormats.PixelColorBlendingMode mode = TheirMode;
        using SharpRgb copy = _sharpBase.Clone(c => c.DrawImage(_sharpOverlay, new Point(100, 100), mode, 1f));
        copy.CopyPixelDataTo(_sharpOut);
        return _sharpOut[0];
    }
}
