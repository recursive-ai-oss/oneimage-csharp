# Fuzzing

One executable (`fuzz/OneImage.Fuzz`) holds one target per input surface: `jpeg`, `inflate`, `lzw`, `icc`, `exif`, `tiff`, `png`, `bmp`, `ico`, `gif`, `webp`, `svg` (SVG path data into the drawing code), `font` (TrueType, OpenType/CFF, TTC, WOFF and WOFF2 files through parsing, glyph outlines, kerning, layout and drawing), `breaking` (arbitrary UTF-16 text, unpaired surrogates included, through line breaking, grapheme segmentation and layout in every word breaking and justification mode, checking that no line starts inside a grapheme cluster; seeded from the Unicode break test files), `bidi` (mixed Arabic, Hebrew and Latin text with direction controls through the bidirectional algorithm, joining and layout in every paragraph direction; seeded from BidiCharacterTest.txt), `brotli` (the text package's Brotli decoder, behind WOFF2), `richtext` (runs with fonts, sizes, attributes and decorations, a fallback family, wrapping and a path, all driven by the input bytes) `sysfont` (the installed-font scanner header reader) `svg-xml` (the SVG package's format sniffing including gzip, its strict XML parser and size reading; seeded from the committed resvg test files) and `svg-render` (whole documents through styles, `use`/`symbol`/nested `svg` instantiation under small limits and the renderer, drawn at a small target size and as small regions; same seeds).
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

The `font` target also seeds from the WOFF2 files in `tests/testdata/fonts`. Their table block is Brotli-compressed, so flipped bits mostly
break the compression; `tools/TextFonts/make-stored-woff2-seeds.py <fontsdir> <dir>` writes copies with the block stored uncompressed, and
copying those into `$ONEIMAGE_FUZZ_WORK/corpus/font/` lets mutations reach the glyph, `hmtx` and collection parsing.

## Adding a target

1. Add `Targets/FooTarget.cs` with `public static void Run(ReadOnlySpan<byte> data)` that wraps the work in `Targets.Guard(...)`.
2. Add one line to the dictionary in `Program.cs`.
3. Add one line to the `case` in `run.sh` naming its seed folder, mode-byte prefix, file patterns and maximum input length.

## Findings

A minimized reproducer goes to `tests/testdata/<format>/fuzz-*` with a regression test asserting a typed error.
