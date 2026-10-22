using BenchmarkDotNet.Attributes;
using OneImage.Kernels;

namespace OneImage.Benchmarks.Png;

/// <summary>Unfilter and filter cost of one 2000-pixel row repeated 1500 times (the size of the gate image), per filter and pixel size.</summary>
[MemoryDiagnoser]
public class PngFilterBenchmarks
{
    private const int Rows = 1500;
    private const int Pixels = 2000;
    private byte[] _row = null!;
    private byte[] _work = null!;
    private byte[] _prev = null!;

    [Params(3, 4)]
    public int Bpp { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(5);
        _row = new byte[Pixels * Bpp];
        _prev = new byte[Pixels * Bpp];
        _work = new byte[Pixels * Bpp];
        rnd.NextBytes(_row);
        rnd.NextBytes(_prev);
    }

    private int Run(int filter)
    {
        for (int r = 0; r < Rows; r++)
        {
            _row.CopyTo(_work, 0);
            PngFilter.Unfilter(filter, _work, _prev, Bpp);
        }

        return _work[^1];
    }

    [Benchmark(Baseline = true, Description = "copy only")]
    public int CopyOnly() => Run(0);

    [Benchmark(Description = "unSub")]
    public int Sub() => Run(1);

    [Benchmark(Description = "unUp")]
    public int Up() => Run(2);

    [Benchmark(Description = "unAverage")]
    public int Average() => Run(3);

    [Benchmark(Description = "unPaeth")]
    public int Paeth() => Run(4);
}
