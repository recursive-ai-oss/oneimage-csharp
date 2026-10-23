# JPEG 2000 decoding

JPEG 2000 Part 1 (ITU-T T.800 / ISO 15444-1) is the wavelet codec PDF files use for `/JPXDecode` images. OneImage ships a
decode-only implementation inside the core package. It is internal: it is not in the default `ImageConfiguration` and not a
public format (JPEG 2000 stays out of the public format list). It exists so that a PDF renderer in the same library family
can reach it through core's internals, with core's limits, kernels and fuzz harness, and without adding a dependency.

Entry points (`OneImage.Codecs.Jpx.JpxCodec`):

- `Identify(data, options)`: reads the main header and the JP2 boxes; returns size, component count, bit depths, tile count,
  the number of reduction levels available, layers and colour space. No tile data is read.
- `Decode(data, options, cancellationToken)`: returns a `JpxImage` of 16-bit sample planes, one per channel, after the palette
  (`pclr`/`cmap`), channel definitions (`cdef`) and the sYCC to sRGB conversion.
- `DecodeComponents(...)`: the raw component planes at their own sampling, for conformance testing.

`JpxDecodeOptions` selects a reduction level (`ReductionLevels`, clamped to what the stream has: each level halves the
size and skips the finest wavelet level), a `Region` of the output (only the tiles it touches are decoded), `Strict`, and the
resource limits below.

## What is decoded

Raw codestreams and JP2 files; all five progression orders and progression order changes; tiles and tile-parts; precincts;
layers; packed packet headers (PPM, PPT); SOP/EPH markers; all code-block styles (bypass, reset, terminate-each-pass, vertically
causal, predictable termination, segmentation symbols); the reversible 5-3 and irreversible 9-7 wavelets; RCT and ICT;
region-of-interest max-shift; 1 to 16 bits per sample, signed or not; sub-sampled components (replicated to the output grid, no
interpolation); palettes, channel definitions, ICC and enumerated colour spaces. Part 2 extensions are ignored. More than 16 bits
per sample and more than 30 magnitude bit-planes are refused with `ImageFormatException`.

## Behavior

- Lossless (5-3) streams decode bit-exactly. Lossy (9-7) output is computed in single-precision float, one operation at a time
  (no fused multiply-add), and rounds half up, so it is identical on every hardware path; against OpenJPEG it differs by at
  most one level at 8 bits (two at 16 bits).
- A coefficient that was not fully decoded sits at the middle of its uncertainty interval (reconstruction parameter r = 1/2).
- Truncated or damaged tile data decodes as far as it goes and sets `JpxImage.Damaged`; with `Strict` it throws
  `ImageFormatException`. Missing bytes of a bypass segment read as 0xFF.
- The output grid is the reference grid divided by the finest component sampling, so a stream whose components all share a
  sampling yields its natural size.
- Output is deterministic: tiles are decoded in parallel (one tile per thread, up to `MaxDegreeOfParallelism`, default
  `min(4, cores)`) and write disjoint samples; a single large tile spreads its code-blocks and wavelet rows over the threads
  instead. The result never depends on the degree. When several tiles fail, the lowest-numbered tile's error is reported.

## Limits (checked before the allocation they guard)

| Limit | Default | Failure |
|---|---|---|
| Pixels per frame, dimension | `ImageLimits` (2²⁸, 65,535) | `ImageLimitException` |
| Components | 256 (`MaxComponents`) | `ImageLimitException` |
| Tiles | 65,535 (the standard's bound) | `ImageFormatException` |
| Code-blocks plus precincts per tile | 2²¹ (`MaxCodeBlocksPerTile`) | `ImageLimitException` |
| Output samples against the data size | `MaxExpansionRatio` × bytes, floor 64 Msamples | `ImageLimitException` |
| Working memory (planes and one tile's coefficients) | 1 GiB (`MaxWorkingBytes`) | `ImageLimitException` |
| Coding-pass work (sample visits) | 2²⁷ + 2048 × bytes | `ImageLimitException` |
| Progression order changes | 1,024 | `ImageLimitException` |
| Progression positions visited | 2²⁷ | `ImageLimitException` |
| Tile-parts | 1,000,000 | `ImageLimitException` |
| Magnitude bit-planes | 30 | `ImageFormatException` |

Output planes for the declared size are allocated before the first tile is read, so a small file can cost up to the
expansion floor (128 MiB of 16-bit samples) before its tile data is refused.

## Testing

- `tests/OneImage.Tests/Jpx`: the T.803 Class 1 conformance set (Profile 0 and 1, 52 component comparisons against the
  suite's reference images under its own tolerances), the committed generated corpus (`testdata/jpx/gen`), API behavior, one
  hostile stream per limit, prefixes and bit flips of every corpus file, and a determinism test that pins output hashes and
  compares degree 1 with degree 8. The conformance files are not in the repository: `tools/fetch-jpx-tests.sh <dir>` downloads
  them at a pinned commit and `ONEIMAGE_JPX_CORPUS=<dir>` enables the test.
- `testdata/jpx/gen` is made by `tools/gen-jpx-corpus.py`, using OpenJPEG as a black-box encoder and decoder on synthetic images.
- Fuzz target `jpx` (`fuzz/run.sh jpx 600`).
- Benchmark: `JpxBenchmarks` (files from `tools/gen-jpx-bench.py` and conformance stream p0_07).

## Speed

BenchmarkDotNet (`JpxBenchmarks`, medium job), Ryzen 9 7950X3D, .NET 10.0.400, Linux x64, default degree 4, wall time per decode
into 16-bit planes. Run on a shared host with a load average of about 5 from other work (not the quiet benchmark worker), so
read the ratios, not the absolute values. "OpenJPEG" is libopenjp2 through glymur with its default settings, best of 5, including
a copy into a numpy array; it is information only.

| File | Reduction | Before | After | Allocated before / after | OpenJPEG |
|---|---|---|---|---|---|
| lossy 2048x1536 RGB, one tile (471 KB) | 0 | 54.8 ms | 44.5 ms | 56.8 / 20.6 MB | 84.7 ms |
| same, 3 levels dropped | 3 | 3.25 ms | 2.69 ms | 1.3 / 0.6 MB | |
| lossy, 256-pixel tiles (465 KB) | 0 | 74.5 ms | 27.8 ms | 77.5 / 19.2 MB | 74.7 ms |
| lossless gray 3000x2000 (4.0 MB) | 0 | 161 ms | 156 ms | 36.0 / 12.8 MB | 366.6 ms |
| 12-bit 2048x2048x3, 256 tiles, 7.9 MB (conformance p0_07) | 0 | 539 ms | 319 ms | 141 / 28.6 MB | 805.6 ms |
| same, 3 levels dropped | 3 | 33.5 ms | 8.4 ms | 6.5 / 3.9 MB | |

Single-threaded the same decodes take 1.19 s (p0_07), 115 ms (lossy), 552 ms (lossless gray). The tier-1 entropy decoder is
the cost of dense files (about 95% of p0_07): in safe code it runs at roughly 1.5× OpenJPEG's single-thread time, and it
scales over code-blocks and tiles. What changed: tiles run in parallel (one per thread, so no fork-join per code-block batch or
wavelet level), the wavelet lifting steps work on the separated low- and high-pass runs with `Vector<T>` (the scalar path is the
reference; the output hashes are identical with vectors on or off), the MQ decoder state lives in locals, and the coefficient
buffers, wavelet scratch and per-thread block decoders are pooled.
