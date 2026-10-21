using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using VipsImage = NetVips.Image;

namespace OneImage.Benchmarks.Tone;

/// <summary>
/// Color and tone operations on a 6000x4000 RGBA image held in memory, codecs excluded. Each library gets the source in its own
/// native form. oneimage builds a new lazy image per call (so nothing is served from its tile cache) and writes into a caller
/// buffer; ImageSharp mutates in place (repeated passes change the values but not the cost); NetVips writes to a managed array.
/// The chains differ by library because the libraries have different operations: oneimage runs levels, curves,
/// brightness/contrast, gamma and invert; ImageSharp runs brightness, contrast, invert, lightness and brightness (it has no
/// levels, curves or gamma); NetVips runs linear, gamma, invert, linear and gamma. Alpha handling differs: libvips applies gamma and invert to the alpha band as well.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(BenchmarkDotNet.Engines.RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class ToneBenchmarks : IDisposable
{
    private const int W = 6000;
    private const int H = 4000;
    private OneImage.Image _ours = null!;
    private IsImage _sharp = null!;
    private VipsImage _vips = null!;
    private OneImage.Rgba32[] _buffer = [];

    /// <summary>Degree of parallelism given to every library, so the comparison is per thread and not per core count.</summary>
    [Params(1, 4)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] raw = new byte[W * H * 4];
        var rnd = new Random(42);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = ((y * W) + x) * 4;
                raw[i] = (byte)((x * 255L / W) + rnd.Next(16));
                raw[i + 1] = (byte)((y * 255L / H) + rnd.Next(16));
                raw[i + 2] = (byte)(((x + y) & 255) + rnd.Next(8));
                raw[i + 3] = 255;
            }
        }

        _sharp = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(raw, W, H);
        var px = new OneImage.Rgba32[W * H];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, OneImage.Rgba32>(raw).CopyTo(px);
        _ours = OneImage.Image.FromPixels<OneImage.Rgba32>(px, W, H);
        _vips = VipsImage.NewFromMemory(raw, W, H, 4, NetVips.Enums.BandFormat.Uchar);
        _buffer = new OneImage.Rgba32[W * H];
        _sharp.Configuration.MaxDegreeOfParallelism = Threads;
        NetVips.NetVips.Concurrency = Threads;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _sharp?.Dispose();
        _vips?.Dispose();
        GC.SuppressFinalize(this);
    }

    private int Pull(OneImage.Image img)
    {
        img.CopyPixelsTo<OneImage.Rgba32>(_buffer, new ImageRect(0, 0, W, H), maxDegree: Threads);
        return _buffer[12345].R;
    }

    private static OneImage.Image Barrier(OneImage.Image img) => img.ToCanvas().Snapshot();

    // ---- grayscale ----

    [Benchmark(Description = "grayscale: oneimage (linear light, RGBA out)")]
    public int GrayOurs() => Pull(_ours.Grayscale());

    [Benchmark(Description = "grayscale: oneimage (encoded values, RGBA out)")]
    public int GrayOursEncoded() => Pull(_ours.Grayscale(linear: false));

    [Benchmark(Description = "grayscale: ImageSharp Bt709 (encoded values, RGBA, in place)")]
    public int GraySharp()
    {
        _sharp.Mutate(c => c.Grayscale(GrayscaleMode.Bt709));
        return _sharp[1, 1].R;
    }

    [Benchmark(Description = "grayscale: ImageSharp Bt709 (clone: separate output like the others)")]
    public int GraySharpClone()
    {
        using IsImage r = _sharp.Clone(c => c.Grayscale(GrayscaleMode.Bt709));
        return r[1, 1].R;
    }

    [Benchmark(Description = "grayscale: NetVips b-w (linear light, 1 band out)")]
    public int GrayVips()
    {
        using VipsImage r = _vips.Colourspace(NetVips.Enums.Interpretation.Bw);
        return r.WriteToMemory<byte>().Length;
    }

    // ---- brightness / contrast ----

    [Benchmark(Description = "brightness+contrast: oneimage (one op)")]
    public int BcOurs() => Pull(_ours.BrightnessContrast(0.05, 0.2));

    [Benchmark(Description = "brightness+contrast: ImageSharp (two ops, in place)")]
    public int BcSharp()
    {
        _sharp.Mutate(c => c.Brightness(1.05f).Contrast(1.2f));
        return _sharp[1, 1].R;
    }

    [Benchmark(Description = "brightness+contrast: ImageSharp (two ops, clone)")]
    public int BcSharpClone()
    {
        using IsImage r = _sharp.Clone(c => c.Brightness(1.05f).Contrast(1.2f));
        return r[1, 1].R;
    }

    [Benchmark(Description = "brightness+contrast: NetVips linear + cast")]
    public int BcVips()
    {
        using VipsImage r = _vips.Linear([1.2, 1.2, 1.2, 1.0], [10, 10, 10, 0]).Cast(NetVips.Enums.BandFormat.Uchar);
        return r.WriteToMemory<byte>().Length;
    }

    // ---- five-op chain ----

    private static readonly CurvePoint[] Curve = [new(0, 0), new(0.3, 0.2), new(0.7, 0.85), new(1, 1)];

    [Benchmark(Description = "5-op chain: oneimage fused (one pass)")]
    public int ChainFused() => Pull(_ours.Levels(0.05, 0.95, 1.2).Curves(Curve).BrightnessContrast(0.05, 0.2).Gamma(1.8).Invert());

    [Benchmark(Description = "5-op chain: oneimage unfused (each op materialized)")]
    public int ChainUnfused()
    {
        OneImage.Image i = Barrier(_ours.Levels(0.05, 0.95, 1.2));
        i = Barrier(i.Curves(Curve));
        i = Barrier(i.BrightnessContrast(0.05, 0.2));
        i = Barrier(i.Gamma(1.8));
        return Pull(i.Invert());
    }

    [Benchmark(Description = "5-op chain: ImageSharp (5 ops, in place)")]
    public int ChainSharp()
    {
        _sharp.Mutate(c => c.Brightness(1.05f).Contrast(1.1f).Invert().Lightness(0.95f).Brightness(1.02f));
        return _sharp[1, 1].R;
    }

    [Benchmark(Description = "5-op chain: ImageSharp (5 ops, clone)")]
    public int ChainSharpClone()
    {
        using IsImage r = _sharp.Clone(c => c.Brightness(1.05f).Contrast(1.1f).Invert().Lightness(0.95f).Brightness(1.02f));
        return r[1, 1].R;
    }

    [Benchmark(Description = "5-op chain: NetVips (5 ops, pipelined)")]
    public int ChainVips()
    {
        using VipsImage r = _vips.Linear([1.1, 1.1, 1.1, 1.0], [5, 5, 5, 0]).Cast(NetVips.Enums.BandFormat.Uchar)
            .Gamma(exponent: 1.8).Invert().Linear([0.95, 0.95, 0.95, 1.0], [3, 3, 3, 0]).Cast(NetVips.Enums.BandFormat.Uchar);
        return r.WriteToMemory<byte>().Length;
    }
}
