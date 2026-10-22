# Release gate report

> targeted re-run of the rows changed after the final gate run, AVX2 configuration

## Host

- Started 2026-10-22 02:28:20 CDT, finished 2026-10-22 02:41:20 CDT
- CPU: AMD Ryzen 9 7950X3D 16-Core Processor, 32 logical CPUs, 125 GiB RAM
- OS: Ubuntu 26.04 LTS, kernel 7.0.0-38-generic
- .NET SDK 10.0.400; Runtime .NET 10.0.11; Avx2.IsSupported=True Avx512F.IsSupported=False Vector256.IsHardwareAccelerated=True Vector512.IsHardwareAccelerated=False
- AVX-512: disabled (DOTNET_EnableAVX512=0), the AVX2 gate configuration
- Load before: ` 02:28:20 up 11:46,  1 user,  load average: 1.01, 1.87, 2.72`
- Load during (sampled each minute):
  - 02:28:20 load average: 1.01, 1.87, 2.72
  - 02:29:20 load average: 1.13, 1.75, 2.62
  - 02:30:20 load average: 1.38, 1.73, 2.56
  - 02:31:20 load average: 1.18, 1.61, 2.46
  - 02:32:20 load average: 1.15, 1.53, 2.38
  - 02:33:20 load average: 1.20, 1.46, 2.30
  - 02:34:20 load average: 1.52, 1.50, 2.26
  - 02:35:20 load average: 1.51, 1.49, 2.21
  - 02:36:20 load average: 1.31, 1.43, 2.14
  - 02:37:20 load average: 1.49, 1.46, 2.10
  - 02:38:20 load average: 2.15, 1.65, 2.12
  - 02:39:20 load average: 1.72, 1.62, 2.08
  - 02:40:20 load average: 1.81, 1.66, 2.06
- Load after: ` 02:41:20 up 11:59,  1 user,  load average: 1.59, 1.63, 2.03`

## Method

- Measured on a shared workstation: other work may have been running, and the load samples above say how much. Absolute times carry that noise.
- Libraries: ImageSharp 3.1.12 is the reference (the last release that builds without a license key); SkiaSharp, NetVips and Magick.NET run through their .NET packages.
- Time: BenchmarkDotNet, `MemoryDiagnoser`, Throughput strategy, 2-3 warm-up and 8-12 measured iterations per row; Mean shown. Inputs are generated deterministically; every task runs from and to memory. A ratio within 5% of 1 is a tie, printed as "tie", never as a win.
- Peak memory: one process per task and library, repeated 3 times; the report shows the median and the spread (max - min). Each process runs the operation once to warm up (code generation, thread-pool start-up, native library loading), collects garbage, resets the kernel's resident-set high-water mark, runs the operation 4 times (collecting garbage after each, outside the timed part), and reports peak minus resident-before: the working memory of the operation. Native allocations are included.
- Peak tie rule against ImageSharp: a tie only when the difference is within max(2 MiB, the spread of either library) and within 5%; otherwise a win (ours lower) or a miss.
- Rows pick each library's idiomatic one-call path to the same output (stated per row). Where a competitor writes into reused or pooled storage, our row writes into a caller-supplied buffer too; rows that allocate a fresh result are kept beside them. Where output types differ (Rgb24 vs Rgba32) both rows exist.
- CMYK JPEG to sRGB: the bar is the libraries that apply the embedded ICC profile (Magick.NET, NetVips). ImageSharp does not convert, so its row is information only; colour accuracy (CIEDE2000) is reported for each library.
- Rows marked NOT DEFAULT use a raised per-call degree of parallelism (all cores, or 8) against the default of min(4, cores). The default row is the gate row; the raised rows are shown for information.
- A library that cannot do a task has no row: SkiaSharp has no BMP, TIFF or animated GIF encoder and no TIFF decoder here; NetVips's native build has no BMP loader or saver.


## Gate summary

Ours = our fastest row of the task. ✗ marks a miss: slower than ImageSharp, more peak working memory than ImageSharp (beyond 5% and 2 MiB), more than 1.5× the fastest native wrapper (thumbnail and codec tasks), more than 2× NetVips's peak working memory (streaming tasks: thumbnails, PNG→WebP, GIF resize, CMYK). Peak columns are the growth of the process's resident set during the operation, in MiB (see Method).

| Task | Ours ms | ImageSharp ms | Ours / ImageSharp | Fastest native wrapper | Ours / fastest | Peak ours MiB (spread) | Peak ImageSharp MiB (spread) | Peak NetVips MiB | Peak ours / ImageSharp | Peak ours / NetVips |
|---|--:|--:|--:|---|--:|--:|--:|--:|--:|--:|
| thumb-jpeg | 37.5 | 47.0 | 0.80 win | magick 40.3 | 0.93 | 2.2 (±0.2) | 1.2 (±0.1) | 64.4 | 1.83 ✗ | 0.03 |
| jpeg-decode | 41.2 | 77.2 | 0.53 win | netvips 102.3 | 0.40 | 0.5 (±0.0) | 3.5 (±0.1) | 360.6 | 0.14 win | 0.00 |
| jpeg-decode-rgba | 43.4 | 79.4 | 0.55 win | skia 146.4 | 0.30 | 0.5 (±0.0) | 0.7 (±0.1) | - | 0.50 win | - |
| jpeg-encode | 87.4 | 100.6 | 0.87 win | netvips 55.3 | 1.58 ✗ | 5.3 (±3.2) | 4.1 (±0.8) | 67.3 | 1.29 ✗ | 0.08 |
| gif-decode | 2.3 | 6.1 | 0.38 win | skia 1.0 | 2.23 ✗ | 0.3 (±0.0) | 0.4 (±0.0) | - | 0.30 win | - |
| gif-resize | 40.4 | 47.1 | 0.86 win | magick 802.4 | 0.05 | 2.4 (±0.3) | 2.6 (±0.0) | 81.6 | 0.92 win | 0.03 |
| cmyk-jpeg-to-srgb | 243.4 | 148.3 (skips ICC) | 1.64 | netvips 252.0 | 0.97 | 2.2 (±0.4) | 1.3 (±0.1) | 441.1 | 1.69 | 0.00 |

## CMYK JPEG to sRGB: colour accuracy

The bar for this task is Magick.NET and NetVips, which apply the embedded ICC profile, not ImageSharp, which skips the conversion (its row is information only). CIEDE2000 of each library's sRGB output against an ICC-correct reference: the CMYK samples from our decoder converted through the embedded profile with this library's exact-math (floating point, no lookup grid) conversion, relative colorimetric with black point compensation. Computed on a deterministic sample of the pixels (every 11th column of every 7th row).

Magick.NET (relative colorimetric) and NetVips do not apply black point compensation unless asked; this library does by default, so its default output sits about 0.9 ΔE00 from theirs. The `oneimage-nobpc` row is this library with `blackPointCompensation: false` and matches them.

The same output compared with the LittleCMS result (Magick.NET) as the reference is in the last two columns.

| Library | Mean ΔE00 | Max ΔE00 | Pixels | Mean ΔE00 vs LittleCMS | Max ΔE00 vs LittleCMS |
|---|--:|--:|--:|--:|--:|
| oneimage | 0.190 | 0.773 | 312312 | 0.935 | 2.764 |
| oneimage-nobpc | 0.882 | 2.685 | 312312 | 0.062 | 1.443 |
| imagesharp (skips ICC conversion) | 11.329 | 27.193 | 312312 | 11.614 | 26.813 |
| skia | 0.909 | 2.629 | 312312 | 0.560 | 1.788 |
| netvips | 0.885 | 2.685 | 312312 | 0.060 | 1.249 |
| magick | 0.889 | 2.698 | 312312 | 0.000 | 0.000 |

## Gate exceptions

Every task and measure where this library ties or misses ImageSharp (the gate is a win on both time and peak memory). Time: tie within 5%; σ is the standard deviation of the benchmark iterations. Peak: tie within max(2 MiB, either library's run-to-run spread) and within 5%; spread is max − min over the repeated runs.

| Task | Measure | Result | Ours | ImageSharp | Ratio | Note |
|---|---|---|---|---|--:|---|
| thumb-jpeg | peak | miss | 2.2 MiB (spread 0.2) | 1.2 MiB (spread 0.1) | 1.83× | |
| jpeg-encode | peak | miss | 5.3 MiB (spread 3.2) | 4.1 MiB (spread 0.8) | 1.29× | |
| cmyk-jpeg-to-srgb | time | miss | 243.4 ms (σ 5.5) | 148.3 ms (σ 0.7) | 1.64× | information only: ImageSharp skips the ICC conversion |
| cmyk-jpeg-to-srgb | peak | miss | 2.2 MiB (spread 0.4) | 1.3 MiB (spread 0.1) | 1.69× | information only: ImageSharp skips the ICC conversion |

## Gaps

- **thumb-jpeg**: more peak working memory than ImageSharp, 2 MiB vs 1 MiB (1.83×).
- **jpeg-encode**: more peak working memory than ImageSharp, 5 MiB vs 4 MiB (1.29×).
- **jpeg-encode**: 1.58× the fastest native wrapper (netvips 55.3 ms vs ours 87.4 ms).
- **gif-decode**: 2.23× the fastest native wrapper (skia 1.0 ms vs ours 2.3 ms).

## Rows

### thumb-jpeg

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Image.Thumbnail(bytes, 400) -> JPEG q90 | 37.5 | 0.1 | 2 ±0 (216) |
| magick | Magick.NET jpeg:size + Thumbnail + JPEG q90 | 40.3 | 0.1 | 1 ±1 (207) |
| skia | SkiaSharp scaled decode + resize + encode | 43.0 | 0.0 | 0 ±0 (206) |
| imagesharp | ImageSharp load(TargetSize) + Resize + JPEG q90 | 47.0 | 0.1 | 1 ±0 (215) |
| netvips | NetVips thumbnail_buffer + jpegsave_buffer q90 | 49.5 | 0.0 | 64 ±4 (323) |

### jpeg-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Image.Load + CopyPixelsTo reused Rgb24 buffer, 24 MP | 41.2 | 0.0 | 0 ±0 (277) |
| imagesharp | ImageSharp Image.Load<Rgb24>, 24 MP | 77.2 | 0.0 | 4 ±0 (280) |
| netvips | NetVips new_from_buffer + write_to_memory, 24 MP | 102.3 | 68.7 | 361 ±18 (719) |
| magick | Magick.NET read, 24 MP | 113.9 | 0.0 | 67 ±0 (269) |
| skia | SkiaSharp SKBitmap.Decode, 24 MP | 148.1 | 0.0 | 90 ±2 (290) |

### jpeg-decode-rgba

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Image.Load + CopyPixelsTo reused Rgba32 buffer, 24 MP | 43.4 | 0.0 | 0 ±0 (294) |
| imagesharp | ImageSharp Image.Load<Rgba32>, 24 MP | 79.4 | 0.0 | 1 ±0 (298) |
| skia | SkiaSharp SKBitmap.Decode (Rgba8888), 24 MP | 146.4 | 0.0 | 90 ±2 (285) |

### jpeg-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips q90 4:2:0 | 55.3 | 5.5 | 67 ±13 (687) |
| oneimage | OneImage q90 4:2:0 optimized tables (default) | 87.4 | 8.4 | 5 ±3 (546) |
| imagesharp | ImageSharp q90 4:2:0 | 100.6 | 8.0 | 4 ±1 (540) |
| magick | Magick.NET q90 4:2:0 | 166.3 | 16.0 | 80 ±2 (614) |
| skia | SkiaSharp q90 4:2:0 | 291.7 | 8.0 | 164 ±1 (694) |

### gif-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp: decode all frames | 1.0 | 0.0 | 0 ±0 (65) |
| oneimage | oneimage: decode 50 frames 500x500 | 2.3 | 1.0 | 0 ±0 (64) |
| imagesharp | ImageSharp: decode | 6.1 | 0.2 | 0 ±0 (116) |
| magick | Magick.NET: decode + coalesce | 15.3 | 0.2 | 0 ±35 (96) |

### gif-resize

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Resize(200) + Save GIF | 40.4 | 2.1 | 2 ±0 (78) |
| imagesharp | ImageSharp Load + Resize(200) + Save GIF | 47.1 | 0.7 | 3 ±0 (133) |
| magick | Magick.NET coalesce + Resize(200) + Write GIF | 802.4 | 6.3 | 4 ±31 (100) |
| netvips | NetVips n=-1 + resize(0.4) + gifsave_buffer | 2708.8 | 1.7 | 82 ±4 (222) |

### cmyk-jpeg-to-srgb

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Load<Rgb24> (skips ICC conversion) | 148.3 | 0.4 | 1 ±0 (140) |
| oneimage | OneImage Load + ConvertColor(sRGB) + CopyPixelsTo reused Rgb24 buffer | 243.4 | 2.0 | 2 ±0 (162) |
| netvips | NetVips load + icc_transform(sRGB) | 252.0 | 68.7 | 441 ±12 (710) |
| skia | SkiaSharp SKBitmap.Decode to Rgba8888 | 554.7 | 0.0 | 90 ±1 (154) |
| magick | Magick.NET (LittleCMS) read + profile transform | 1015.9 | 0.3 | 159 ±0 (223) |


## Note on thumb-jpeg peak

This run re-measures the rows touched by the JPEG encoder, JPEG decoder and GIF changes made after `gate-final.md`; every other row stands as published there. thumb-jpeg peak was also measured with 5 repeats on the commit before those changes (da48354): median working memory 2.2 MiB (range 1.5–2.5) before and after, against 1.2 MiB for ImageSharp. The gap of about 1 MiB is not caused by these changes; the 1.6 MiB in `gate-final.md` was a low draw from the same range.
