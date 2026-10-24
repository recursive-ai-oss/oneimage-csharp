using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace OneImage.Benchmarks;

/// <summary>A cheap synthetic source (constant tiles) so the benchmarks measure the engine, not the source.</summary>
internal sealed class FlatNode : Node
{
    public FlatNode(int width, int height)
        : base(width, height, PixelFormat.Rgba8, new OpDescriptor("flat"))
    {
    }

    protected override Tile ComputeTile(int tx, int ty, RenderContext context)
    {
        Tile t = Tile.Rent(Tile.ByteSize(Format), clear: false);
        t.Span.Fill((byte)(tx + ty));
        return t;
    }
}

[MemoryDiagnoser]
public class CanvasBenchmarks
{
    private const int W = 6000;
    private const int H = 4000;
    private Rgba32[] _source = null!;
    private Rgba32[] _target = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = new Rgba32[W * H];
        var rnd = new Random(1);
        for (int i = 0; i < _source.Length; i++)
        {
            _source[i] = new Rgba32((byte)rnd.Next(256), (byte)i, (byte)(i >> 8), 255);
        }

        _target = new Rgba32[W * H];
    }

    [Benchmark(Description = "Canvas fill + read-back, 24 MP Rgba32")]
    public Rgba32 FillAndReadBack()
    {
        using var canvas = new Canvas(W, H, PixelFormat.Rgba8);
        var all = new ImageRect(0, 0, W, H);
        canvas.CopyFrom<Rgba32>(_source, all);
        canvas.CopyTo<Rgba32>(_target, all);
        return _target[^1];
    }
}

[MemoryDiagnoser]
public class RegionBenchmarks
{
    private readonly int _size = 10000;

    [Benchmark(Description = "Region render 512x512 from lazy 10000x10000")]
    public Rgba32 Region512()
    {
        // A fresh node each call, so nothing is served from the shared cache.
        Image img = Image.FromNode(new FlatNode(_size, _size));
        var px = new Rgba32[512 * 512];
        img.CopyPixelsTo<Rgba32>(px, new ImageRect(3000, 3000, 512, 512));
        return px[0];
    }

    [Benchmark(Description = "Region render 512x512 through crop + invert")]
    public Rgba32 Region512ThroughOps()
    {
        Image img = Image.FromNode(new FlatNode(_size, _size)).Crop(17, 5, 9000, 9000).Invert();
        var px = new Rgba32[512 * 512];
        img.CopyPixelsTo<Rgba32>(px, new ImageRect(3000, 3000, 512, 512));
        return px[0];
    }
}

[MemoryDiagnoser]
public class SnapshotBenchmarks : IDisposable
{
    private Canvas _canvas = null!;

    [GlobalSetup]
    public void Setup()
    {
        _canvas = new Canvas(6000, 4000, PixelFormat.Rgba8);
        _canvas.CopyFrom<Rgba32>(new Rgba32[6000 * 4000], new ImageRect(0, 0, 6000, 4000));
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
            _canvas.Dispose();
        }
    }

    [Benchmark(Description = "Snapshot of a 24 MP canvas")]
    public Image Snapshot() => _canvas.Snapshot();

    [Benchmark(Description = "Snapshot + first write (copies one tile and the tile table)")]
    public Image SnapshotThenWrite()
    {
        Image s = _canvas.Snapshot();
        _canvas[1, 1] = new Rgba32(1, 2, 3, 4);
        return s;
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 2 && args[0] == "peak")
        {
            return Composite.PeakMemoryRunner.Run(args);
        }

        if (args.Length > 1 && args[0] == "gate-peak" && args[1] == "info")
        {
            Console.WriteLine($"Runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; Avx2.IsSupported={System.Runtime.Intrinsics.X86.Avx2.IsSupported} Avx512F.IsSupported={System.Runtime.Intrinsics.X86.Avx512F.IsSupported} Vector256.IsHardwareAccelerated={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated} Vector512.IsHardwareAccelerated={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated}");
            return 0;
        }

        if (args.Length > 2 && args[0] == "region-peak")
        {
            return Region.RegionPeak.Run(args);
        }

        if (args.Length > 0 && args[0] == "layers")
        {
            return Layers.LayerScenarios.Run(args);
        }

        if (args.Length > 0 && args[0] == "svg-hash")
        {
            return SvgHash.Run();
        }

        if (args.Length > 0 && args[0] == "gate-peak")
        {
            return Gate.GatePeak.Run(args);
        }

        if (args.Length > 0 && args[0] == "gate-deltae")
        {
            return Gate.GateDeltaE.Run();
        }

        if (args.Length > 0 && args[0] == "gate-report")
        {
            return Gate.GateReport.Run(args);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
