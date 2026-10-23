using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OursPath = OneImage.Drawing.Path;
using OursPen = OneImage.Drawing.Pen;

namespace OneImage.Benchmarks.Drawing;

/// <summary>How the canvas is snapshotted before each in-place draw.</summary>
public enum SnapshotState
{
    /// <summary>No snapshot is ever taken.</summary>
    None,

    /// <summary>A snapshot taken before each draw is kept until the next iteration replaces it (the undo pattern: snapshot, draw, snapshot, draw).</summary>
    Live,

    /// <summary>A snapshot is taken before each draw and dropped at once.</summary>
    Dropped,
}

/// <summary>
/// In-place drawing on a 2000 × 2000 Rgba32 canvas with and without snapshots: a circle fill, a circle stroke, a whole-canvas clear,
/// and small pixel writes. Allocation per operation shows whether tiles replaced by copy-on-write come back from the pool or are
/// left to the garbage collector.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 12)]
public class CanvasUndoBenchmarks : IDisposable
{
    private const int Size = 2000;
    private Canvas _canvas = null!;
    private Image? _held;
    private OursPath _circle = null!, _ring = null!;
    private OursPen _pen = null!;
    private readonly Rgba32 _red = new(220, 30, 30, 255), _blue = new(30, 30, 220, 255);
    private readonly Rgba32[] _patch = new Rgba32[200 * 200];

    /// <summary>Gets or sets the snapshot behavior.</summary>
    [Params(SnapshotState.None, SnapshotState.Live, SnapshotState.Dropped)]
    public SnapshotState Snapshots { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _canvas = new Canvas(Size, Size, PixelFormat.Rgba8);
        _canvas.Clear(new Rgba32(255, 255, 255, 255));
        _circle = OursPath.Circle(1000, 1000, 800);
        _ring = OursPath.Circle(1000, 1000, 900);
        _pen = new OursPen(OneImage.Drawing.Brush.Solid(_blue), 12, OneImage.Drawing.StrokeJoin.Round);
    }

    private void Before()
    {
        switch (Snapshots)
        {
            case SnapshotState.Live:
                _held = _canvas.Snapshot();
                break;
            case SnapshotState.Dropped:
                _ = _canvas.Snapshot();
                break;
        }
    }

    [Benchmark(Description = "Fill circle r800")]
    public void Fill()
    {
        Before();
        _canvas.Fill(_red, _circle);
    }

    [Benchmark(Description = "Fill circle r800 linear light")]
    public void FillLinear()
    {
        Before();
        _canvas.Fill(OneImage.Drawing.Brush.Solid(_red), _circle, linear: true);
    }

    [Benchmark(Description = "Stroke circle r900 w12")]
    public void Stroke()
    {
        Before();
        _canvas.Draw(_pen, _ring);
    }

    [Benchmark(Description = "Clear whole canvas")]
    public void Clear()
    {
        Before();
        _canvas.Clear(_red);
    }

    [Benchmark(Description = "Lock ReadWrite 200x200 patch")]
    public void LockWrite()
    {
        Before();
        using PixelLock<Rgba32> l = _canvas.Lock<Rgba32>(new ImageRect(900, 900, 200, 200), LockAccess.ReadWrite);
        for (int y = 0; y < 200; y++)
        {
            l.GetRow(y).Fill(_blue);
        }
    }

    [Benchmark(Description = "CopyFrom 200x200 patch")]
    public void CopyFromPatch()
    {
        Before();
        _canvas.CopyFrom<Rgba32>(_patch, new ImageRect(900, 900, 200, 200));
    }

    public void Dispose()
    {
        _canvas?.Dispose();
        GC.KeepAlive(_held);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// An undo loop on a 24 MP Rgba32 canvas: ten whole-frame draws, each after a <see cref="Canvas.Snapshot"/> that is dropped at once.
/// Allocation per loop shows whether the replaced tiles are recycled or left to the garbage collector.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 2, iterationCount: 8)]
public class CanvasUndoLoopBenchmarks : IDisposable
{
    private Canvas _canvas = null!;

    [GlobalSetup]
    public void Setup()
    {
        _canvas = new Canvas(6000, 4000, PixelFormat.Rgba8);
        _canvas.Clear(new Rgba32(255, 255, 255, 255));
    }

    [Benchmark(Description = "10 whole-frame draws on 24 MP, snapshot before each")]
    public void TenDraws()
    {
        for (int i = 0; i < 10; i++)
        {
            _ = _canvas.Snapshot();
            _canvas.Clear(new Rgba32((byte)i, 30, 30, 255));
        }
    }

    public void Dispose()
    {
        _canvas?.Dispose();
        GC.SuppressFinalize(this);
    }
}
