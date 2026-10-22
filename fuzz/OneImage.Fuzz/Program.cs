using System;
using System.Collections.Generic;
using OneImage;
using SharpFuzz;

// One fuzz executable, one target per input surface. The target is chosen by the ONEIMAGE_FUZZ_TARGET environment
// variable because libFuzzer's .NET host starts the program without command-line arguments.
//
// To add a target: write a static class with `public static void Run(ReadOnlySpan<byte> data)` that lets only the
// library's typed exceptions (ImageException, OperationCanceledException) escape through Targets.Guard, then add one
// line to `Targets` below and a seed folder name to fuzz/run.sh.
internal static class Program
{
    private delegate void Target(ReadOnlySpan<byte> data);

    private static readonly Dictionary<string, Target> Targets = new(StringComparer.Ordinal)
    {
        ["jpeg"] = JpegTarget.Run,
        ["inflate"] = CompressionTargets.Inflate,
        ["lzw"] = CompressionTargets.Lzw,
        ["icc"] = MetadataTargets.Icc,
        ["exif"] = MetadataTargets.Exif,
        ["tiff"] = TiffTarget.Run,
        ["png"] = CodecTargets.Png,
        ["bmp"] = CodecTargets.Bmp,
        ["ico"] = CodecTargets.Ico,
        ["gif"] = CodecTargets.Gif,
        ["webp"] = CodecTargets.WebP,
        ["svg"] = DrawingTargets.Svg,
        ["svg-xml"] = SvgXmlTarget.Run,
        ["font"] = FontTarget.Run,
        ["breaking"] = BreakingTarget.Run,
        ["brotli"] = BrotliTarget.Run,
        ["richtext"] = RichTextTarget.Run,
        ["sysfont"] = SysFontTarget.Run,
    };

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "seeds-webp")
        {
            WebPSeeds.Write(args[1]);
            return 0;
        }

        if (args.Length == 3 && args[0] == "seeds-breaking")
        {
            BreakingTarget.WriteSeeds(args[1], args[2]);
            return 0;
        }

        string? name = Environment.GetEnvironmentVariable("ONEIMAGE_FUZZ_TARGET") ?? (args.Length > 0 ? args[0] : null);
        if (name is null || !Targets.TryGetValue(name, out Target? target))
        {
            Console.Error.WriteLine("Set ONEIMAGE_FUZZ_TARGET to one of: " + string.Join(", ", Targets.Keys));
            return 2;
        }

        if (args.Length > 1)
        {
            // Replay mode: run the target once per file, for reproducing a crash without libFuzzer.
            for (int i = 1; i < args.Length; i++)
            {
                target(System.IO.File.ReadAllBytes(args[i]));
            }

            // Managed heap still in use after a full collection: growth across repeated replays points at a leak.
            Console.WriteLine($"replayed {args.Length - 1} inputs; live heap {GC.GetTotalMemory(true) >> 10} KiB");

            return 0;
        }

        // Instrumented library code may only run once libFuzzer has attached, so configuration happens on the first input.
        bool configured = false;
        long count = 0;
        long heapLogEvery = long.TryParse(Environment.GetEnvironmentVariable("FUZZ_HEAP_LOG"), out long every) ? every : 0;
        Fuzzer.LibFuzzer.Run(span =>
        {
            if (!configured)
            {
                // Keep the shared tile cache small so the run's memory limit measures leaks, not the cache's default 256 MiB budget.
                ImageEngine.CacheBudgetBytes = 16L << 20;
                configured = true;
            }

            target(span);

            // Optional leak watch: FUZZ_HEAP_LOG=<every N inputs> prints the live managed heap after a full collection.
            if (heapLogEvery > 0 && ++count % heapLogEvery == 0)
            {
                Console.Error.WriteLine($"[heap] input {count}: live {GC.GetTotalMemory(true) >> 10} KiB, tiles rented-returned {Tile.RentedCount - Tile.ReturnedCount}, global cache {TileCache.Global.Bytes >> 10} KiB, streaming cache {TileCache.Streaming.Bytes >> 10} KiB");
            }
        });
        return 0;
    }
}
