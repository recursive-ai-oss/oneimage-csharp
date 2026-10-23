using System.Diagnostics;
using System.IO;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OneImage.Layers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace OneImage.Benchmarks.Layers;

internal static class LayerData
{
    public const int W = 6000, H = 4000;

    /// <summary>A deterministic semi-transparent pattern, different for every layer.</summary>
    public static void FillRows(Rgba32[] row, int y0, int rows, int seed)
    {
        for (int r = 0; r < rows; r++)
        {
            int y = y0 + r;
            for (int x = 0; x < W; x++)
            {
                uint h = (uint)((x * 73856093) ^ (y * 19349663) ^ (seed * 83492791));
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                row[(r * W) + x] = new Rgba32((byte)(h >> 3), (byte)(h >> 11), (byte)(h >> 19), (byte)(80 + (h & 0x3f)));
            }
        }
    }

    public static PixelLayer AddLayer(Document doc, int seed)
    {
        PixelLayer layer = doc.CreatePixelLayer("layer" + seed);
        var row = new Rgba32[W * Tile.Size];
        for (int y0 = 0; y0 < H; y0 += Tile.Size)
        {
            int rows = Math.Min(Tile.Size, H - y0);
            FillRows(row, y0, rows, seed);
            layer.Pixels.CopyFrom<Rgba32>(row.AsSpan(0, W * rows), new ImageRect(0, y0, W, rows));
        }

        layer.Opacity = 0.5f;
        layer.Flush();
        doc.Add(layer);
        return layer;
    }

    public static Document Build(int layers, DocumentOptions? options = null)
    {
        var doc = new Document(W, H, options ?? new DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
        for (int i = 0; i < layers; i++)
        {
            AddLayer(doc, i + 1);
        }

        return doc;
    }
}

/// <summary>Flattening a 24 MP document of N full-size half-transparent layers into a caller buffer, against ImageSharp's chain of <c>DrawImage</c> calls on a clone.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 5)]
public class FlattenBenchmarks : IDisposable
{
    private Document _doc = null!;
    private Rgba32[] _out = null!;
    private SharpRgba _sharpBase = null!;
    private SharpRgba _sharpLayer = null!;
    private byte[] _sharpOut = null!;
    private bool _flip;

    [Params(10, 50)]
    public int Layers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _doc = LayerData.Build(Layers);
        _out = new Rgba32[LayerData.W * LayerData.H];
        _sharpBase = new SharpRgba(LayerData.W, LayerData.H, new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 255, 255, 255));
        var row = new Rgba32[LayerData.W * LayerData.H];
        LayerData.FillRows(row, 0, LayerData.H, 1);
        _sharpLayer = SharpImage.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(System.Runtime.InteropServices.MemoryMarshal.AsBytes(row.AsSpan()), LayerData.W, LayerData.H);
        _sharpOut = new byte[LayerData.W * LayerData.H * 4];
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _doc.Dispose();
        _sharpBase.Dispose();
        _sharpLayer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "OneImage Document.Render (cold: every tile) + CopyPixelsTo")]
    public int OneImageFlatten()
    {
        // Changing the background invalidates every composited tile, so each run composites all layers again.
        _flip = !_flip;
        _doc.Background = _flip ? new Rgba32(255, 255, 255, 255) : new Rgba32(254, 255, 255, 255);
        _doc.Render().CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, LayerData.W, LayerData.H));
        return _out[0].R;
    }

    [Benchmark(Description = "OneImage Document.Render (cold) + CopyPixelsTo, maxDegree 32")]
    public int OneImageFlattenAllCores()
    {
        _flip = !_flip;
        _doc.Background = _flip ? new Rgba32(255, 255, 255, 255) : new Rgba32(254, 255, 255, 255);
        _doc.Render().CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, LayerData.W, LayerData.H), maxDegree: 32);
        return _out[0].R;
    }

    [Benchmark(Baseline = true, Description = "ImageSharp Clone + DrawImage x N + CopyPixelDataTo")]
    public int ImageSharpFlatten()
    {
        int n = Layers;
        using SharpRgba copy = _sharpBase.Clone(c =>
        {
            for (int i = 0; i < n; i++)
            {
                c.DrawImage(_sharpLayer, new SixLabors.ImageSharp.Point(0, 0), 0.5f);
            }
        });
        copy.CopyPixelDataTo(_sharpOut);
        return _sharpOut[0];
    }
}

/// <summary>Re-rendering after a small stroke on one layer of a 20-layer 24 MP document.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 2, iterationCount: 8)]
public class IncrementalBenchmarks : IDisposable
{
    private Document _doc = null!;
    private PixelLayer _layer = null!;
    private Rgba32[] _out = null!;
    private Rgba32[] _region = null!;
    private int _step;

    [GlobalSetup]
    public void Setup()
    {
        _doc = LayerData.Build(20);
        _layer = (PixelLayer)_doc.Root.Children[10];
        _out = new Rgba32[LayerData.W * LayerData.H];
        _region = new Rgba32[512 * 512];
        _doc.Render().CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, LayerData.W, LayerData.H));
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _doc.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Stroke()
    {
        _step++;
        float x = 1000 + ((_step * 37) % 3000);
        _layer.Pixels.Draw(new Rgba32(255, 0, 0, 255), 6f, OneImage.Drawing.Path.Polyline([new Vector2(x, 2000), new Vector2(x + 60, 2040), new Vector2(x + 90, 2010)]));
    }

    [Benchmark(Description = "Stroke on one layer + Render + whole-image CopyPixelsTo")]
    public int StrokeAndRenderWhole()
    {
        Stroke();
        _doc.Render().CopyPixelsTo<Rgba32>(_out, new ImageRect(0, 0, LayerData.W, LayerData.H));
        return _out[0].R;
    }

    [Benchmark(Description = "Stroke on one layer + Render + 512x512 CopyPixelsTo around it")]
    public int StrokeAndRenderViewport()
    {
        Stroke();
        _doc.Render().CopyPixelsTo<Rgba32>(_region, new ImageRect(1024 + (((_step * 37) % 3000) / 128 * 128), 1792, 512, 512));
        return _region[0].R;
    }

    [Benchmark(Description = "Snapshot only (compare 20 tile tables)")]
    public long SnapshotOnly() => _doc.Snapshot().Revision;
}

/// <summary>A round trip through the spill store: 1024 sealed 64 KiB tiles written to the file and read back.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 8)]
public class SpillBenchmarks
{
    private readonly string _dir = System.IO.Path.Combine(Environment.GetEnvironmentVariable("ONEIMAGE_SPILL_DIR") ?? System.IO.Path.GetTempPath(), "oneimage-spill-bench");

    [Benchmark(Description = "Spill 1024 tiles (64 MiB) to disk and read them all back")]
    public long RoundTrip()
    {
        using var store = new SpillTileStore(0, _dir, long.MaxValue);
        var tiles = new Tile[1024];
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = Tile.Rent(Tile.ByteSize(PixelFormat.Rgba8), clear: false, store);
            tiles[i].Span.Fill((byte)i);
            tiles[i].Seal();
        }

        store.EnforceBudget();
        long sum = 0;
        foreach (Tile t in tiles)
        {
            sum += t.Span[17];
            t.Release();
        }

        return sum + store.Statistics.TilesRead;
    }
}

/// <summary>Claim runs with printed evidence: <c>layers incremental</c> and <c>layers spill [layers] [budgetMiB]</c>.</summary>
internal static class LayerScenarios
{
    public static int Run(string[] args)
    {
        string which = args.Length > 1 ? args[1] : "incremental";
        return which switch
        {
            "incremental" => Incremental(),
            "rowcost" => RowCost(),
            "hw" => Hardware(),
            "overcost" => OverCost(),
            "spill" => Spill(args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 100, args.Length > 3 ? long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 256),
            "memory" => Spill(args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 100, 0),
            _ => 2,
        };
    }

    private static long PeakRssMiB()
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
            {
                return long.Parse(line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture) / 1024;
            }
        }

        return -1;
    }

    private static long RssMiB()
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                return long.Parse(line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture) / 1024;
            }
        }

        return -1;
    }

    private static int Hardware()
    {
        Console.WriteLine($"Vector128 {System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}, Vector256 {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}, Vector512 {System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated}, Avx2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}, Avx512F {System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
        return 0;
    }

    private static int OverCost()
    {
        int N = int.Parse(Environment.GetEnvironmentVariable("OVER_N") ?? "16384", System.Globalization.CultureInfo.InvariantCulture);
        var a = new Rgba32[N];
        var b = new Rgba32[N];
        var seedRow = new Rgba32[LayerData.W];
        LayerData.FillRows(seedRow, 0, 1, 3);
        for (int i = 0; i < N; i++) { a[i] = seedRow[i % 6000]; b[i] = new Rgba32(seedRow[(i * 7) % 6000].R, seedRow[i % 6000].G, 9, 255); }
        byte[] back = System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan()).ToArray();
        byte[] over = System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).ToArray();
        byte[] dst = new byte[N * 4];
        for (int round = 0; round < 8; round++)
        {
            int Reps = 2000 * 16384 / N;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Reps; i++) { OneImage.Kernels.CompositeKernel.OverRgba8(back, over, dst, N, 0.5f); }
            Console.WriteLine($"OverRgba8 over an opaque backdrop, cache-resident: {sw.Elapsed.TotalMilliseconds * 1e6 / Reps / N:F3} ns/px");
        }

        return 0;
    }

    private static int RowCost()
    {
        const int N = 6000;
        var rows = new Rgba32[N];
        LayerData.FillRows(rows, 0, 1, 3);
        byte[] src = System.Runtime.InteropServices.MemoryMarshal.AsBytes(rows.AsSpan()).ToArray();
        var work = new PixelFormat(ColorModel.Rgb, SampleType.F32, AlphaMode.Premultiplied);
        float[] fb = new float[N * 4], fo = new float[N * 4], fd = new float[N * 4];
        byte[] dst = new byte[N * 4];
        Span<byte> fbB = System.Runtime.InteropServices.MemoryMarshal.AsBytes(fb.AsSpan());
        Span<byte> foB = System.Runtime.InteropServices.MemoryMarshal.AsBytes(fo.AsSpan());
        Span<byte> fdB = System.Runtime.InteropServices.MemoryMarshal.AsBytes(fd.AsSpan());
        for (int round = 0; round < 3; round++)
        {
            const int Reps = 2000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Reps; i++) { OneImage.Kernels.ConvertKernel.ConvertRow(src, PixelFormat.Rgba8, fbB, work, N); }
            double toF = sw.Elapsed.TotalMilliseconds * 1e6 / Reps / N;
            sw.Restart();
            for (int i = 0; i < Reps; i++) { OneImage.Kernels.BlendKernel.Row(BlendMode.Normal, fb, fo, fd, 3, 0.5f, AlphaComposition.SrcOver); }
            double blend = sw.Elapsed.TotalMilliseconds * 1e6 / Reps / N;
            sw.Restart();
            for (int i = 0; i < Reps; i++) { OneImage.Kernels.ConvertKernel.ConvertRow(fdB, work, dst, PixelFormat.Rgba8, N); }
            double back = sw.Elapsed.TotalMilliseconds * 1e6 / Reps / N;
            Console.WriteLine($"ns/px: to float {toF:F2}, normal blend {blend:F2}, back to u8 {back:F2}");
        }

        _ = foB;
        return 0;
    }

    private static int Incremental()
    {
        var sw = Stopwatch.StartNew();
        using Document doc = LayerData.Build(20);
        Console.WriteLine($"build 20 layers x 24 MP: {sw.ElapsedMilliseconds} ms");
        var layer = (PixelLayer)doc.Root.Children[10];
        var buffer = new Rgba32[LayerData.W * LayerData.H];
        var whole = new ImageRect(0, 0, LayerData.W, LayerData.H);

        sw.Restart();
        doc.Render().CopyPixelsTo<Rgba32>(buffer, whole);
        long full = sw.ElapsedMilliseconds;
        Console.WriteLine($"first (full) render: {full} ms, tiles computed {doc.TilesComputed}");

        for (int round = 0; round < 3; round++)
        {
            layer.Pixels.Draw(new Rgba32(255, 0, 0, 255), 6f, OneImage.Drawing.Path.Polyline([new Vector2(2000 + (round * 300), 2000), new Vector2(2060 + (round * 300), 2040), new Vector2(2090 + (round * 300), 2010)]));
            long c0 = doc.TilesComputed;
            long r0 = doc.TilesReused;
            sw.Restart();
            DocumentSnapshot snap = doc.Snapshot();
            long snapMs = sw.ElapsedTicks * 1000 / Stopwatch.Frequency;
            double snapUs = sw.Elapsed.TotalMilliseconds * 1000;
            snap.Render().CopyPixelsTo<Rgba32>(buffer, whole);
            double total = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"after stroke {round}: snapshot {snapUs:F0} us, snapshot+render+copy-out {total:F1} ms; tiles recomputed {doc.TilesComputed - c0}, from cache {doc.TilesReused - r0} (full render: {full} ms, {LayerData.W * LayerData.H / (Tile.Size * Tile.Size) + 1} tiles)");
            _ = snapMs;
        }

        // Cache-hit floor: the same snapshot again.
        sw.Restart();
        doc.Render().CopyPixelsTo<Rgba32>(buffer, whole);
        Console.WriteLine($"unchanged re-render (all from cache): {sw.Elapsed.TotalMilliseconds:F1} ms");
        return 0;
    }

    private static int Spill(int layers, long budgetMiB)
    {
        string dir = args_dir();
        Directory.CreateDirectory(dir);
        var options = new DocumentOptions
        {
            Background = new Rgba32(255, 255, 255, 255),
            Spill = budgetMiB > 0 ? new SpillOptions { BudgetBytes = budgetMiB << 20, Directory = dir } : null,
        };
        Console.WriteLine($"{layers} layers x 24 MP Rgba8, {(budgetMiB > 0 ? "spill budget " + budgetMiB + " MiB" : "in memory (no spill)")}; rss at start {RssMiB()} MiB");
        var sw = Stopwatch.StartNew();
        using Document doc = LayerData.Build(layers, options);
        Console.WriteLine($"built in {sw.Elapsed.TotalSeconds:F1} s; rss {RssMiB()} MiB, peak {PeakRssMiB()} MiB");
        if (doc.SpillStatistics is { } built)
        {
            Console.WriteLine($"  spill after build: written {built.TilesWritten}, read {built.TilesRead}, resident {built.ResidentBytes >> 20} MiB, file {built.FileBytes >> 20} MiB");
        }

        sw.Restart();
        // Read the result in 256-row strips, as a streaming save would, so the output buffer is not part of the measurement.
        var buffer = new Rgba32[LayerData.W * 256];
        Image rendered = doc.Render();
        long sum = 0;
        for (int y0 = 0; y0 < LayerData.H; y0 += 256)
        {
            int rows = Math.Min(256, LayerData.H - y0);
            rendered.CopyPixelsTo<Rgba32>(buffer, new ImageRect(0, y0, LayerData.W, rows));
            sum += buffer[1234].R + buffer[(rows * LayerData.W) - 1].G;
        }

        Console.WriteLine($"rendered in {sw.Elapsed.TotalSeconds:F1} s; rss {RssMiB()} MiB, peak {PeakRssMiB()} MiB");
        if (doc.SpillStatistics is { } s)
        {
            Console.WriteLine($"  spill: written {s.TilesWritten}, read {s.TilesRead}, peak resident {s.PeakResidentBytes >> 20} MiB, file {s.FileBytes >> 20} MiB, budget {s.BudgetBytes >> 20} MiB");
        }

        Console.WriteLine($"  managed heap {GC.GetTotalMemory(false) >> 20} MiB, collections gen0/1/2 = {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, evicted-but-waiting {SpillEpochs.LimboBytes >> 20} MiB, idle reusable {SpillEpochs.FreeBytes >> 20} MiB, pool {PooledTileStore.Instance.PooledBytes >> 20} MiB");
        Console.WriteLine($"checksum {sum}");
        return 0;
    }

    private static string args_dir() => Environment.GetEnvironmentVariable("ONEIMAGE_SPILL_DIR") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oneimage-spill-run");
}
