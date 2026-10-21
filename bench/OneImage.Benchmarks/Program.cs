using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace OneImage.Benchmarks;

[MemoryDiagnoser]
public class PlaceholderBenchmarks
{
    [Benchmark]
    public static string LibraryName() => Library.Name;
}

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
