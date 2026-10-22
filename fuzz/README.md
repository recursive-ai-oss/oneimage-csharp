# Fuzzing

One executable (`fuzz/OneImage.Fuzz`) holds one target per input surface: `jpeg`, `inflate`, `lzw`, `icc`, `exif`, `tiff`.
Targets that read files go through the public `Image.Identify` / `Image.Load` / `CopyPixelsTo` path with tight `ImageLimits`; the compression,
ICC and EXIF targets drive the parsers directly. Only `ImageException` (and `OperationCanceledException`) may come out. Any other exception,
a hang (libFuzzer `-timeout`) or a memory blow-up (`-rss_limit_mb`) is a finding.

## Running

    export ONEIMAGE_FUZZ_TOOLS=/path/to/tools     # holds libfuzzer-dotnet and sharpfuzz/sharpfuzz
    export ONEIMAGE_FUZZ_WORK=/path/to/workdir    # builds, corpora, findings
    fuzz/run.sh tiff 600                          # fuzz the TIFF target for 10 minutes
    fuzz/run.sh replay tiff fuzz-input-file...    # reproduce saved inputs once, no libFuzzer

`run.sh` builds the harness in release mode, instruments `OneImage.dll` with SharpFuzz, seeds the corpus from `tests/testdata/<format>/`
(with the one mode byte some targets read in front), and runs libFuzzer. Findings land in `$ONEIMAGE_FUZZ_WORK/findings/<target>/`.
Per-input timeout and memory limit: `FUZZ_TIMEOUT` (10 s) and `FUZZ_RSS_MB` (2048).

The tools are two small programs:

* `libfuzzer-dotnet`: the C++ host from the SharpFuzz project (`libfuzzer-dotnet.cc`), compiled with `clang++ -fsanitize=fuzzer`.
* `sharpfuzz`: `dotnet tool install SharpFuzz.CommandLine --tool-path <dir>/sharpfuzz`.

## Adding a target

1. Add `Targets/FooTarget.cs` with `public static void Run(ReadOnlySpan<byte> data)` that wraps the work in `Targets.Guard(...)`.
2. Add one line to the dictionary in `Program.cs`.
3. Add one line to the `case` in `run.sh` naming its seed folder, mode-byte prefix, file patterns and maximum input length.

## Findings

A minimized reproducer goes to `tests/testdata/<format>/fuzz-*` with a regression test asserting a typed error.
