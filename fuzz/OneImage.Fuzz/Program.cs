using System;
using System.Collections.Generic;
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
    };

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "seeds-webp")
        {
            WebPSeeds.Write(args[1]);
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

            return 0;
        }

        Fuzzer.LibFuzzer.Run(span => target(span));
        return 0;
    }
}
