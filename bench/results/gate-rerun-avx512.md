# Release gate report

> targeted re-run of the rows changed after the final gate run, native AVX-512, information only

## Host

- Started 2026-10-22 02:41:48 CDT, finished 2026-10-22 02:54:44 CDT
- CPU: AMD Ryzen 9 7950X3D 16-Core Processor, 32 logical CPUs, 125 GiB RAM
- OS: Ubuntu 26.04 LTS, kernel 7.0.0-38-generic
- .NET SDK 10.0.400; Runtime .NET 10.0.11; Avx2.IsSupported=True Avx512F.IsSupported=True Vector256.IsHardwareAccelerated=True Vector512.IsHardwareAccelerated=True
- AVX-512: enabled, native run, information only
- Load before: ` 02:41:48 up 11:59,  1 user,  load average: 1.10, 1.51, 1.97`
- Load during (sampled each minute):
  - 02:41:48 load average: 1.10, 1.51, 1.97
  - 02:42:48 load average: 1.45, 1.58, 1.97
  - 02:43:48 load average: 1.99, 1.72, 1.99
  - 02:44:48 load average: 1.59, 1.65, 1.95
  - 02:45:48 load average: 1.41, 1.60, 1.92
  - 02:46:48 load average: 1.57, 1.60, 1.90
  - 02:47:48 load average: 1.57, 1.60, 1.88
  - 02:48:48 load average: 1.87, 1.73, 1.91
  - 02:49:48 load average: 1.35, 1.61, 1.85
  - 02:50:48 load average: 1.20, 1.51, 1.80
  - 02:51:48 load average: 1.41, 1.51, 1.78
  - 02:52:48 load average: 1.26, 1.45, 1.74
  - 02:53:48 load average: 1.09, 1.36, 1.69
- Load after: ` 02:54:44 up 12:12,  1 user,  load average: 1.04, 1.30, 1.65`

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
| thumb-jpeg | 38.1 | 48.2 | 0.79 win | magick 40.3 | 0.94 | 2.2 (±0.6) | 1.2 (±0.0) | 68.1 | 1.83 ✗ | 0.03 |
| jpeg-decode | 41.7 | 76.5 | 0.54 win | netvips 98.2 | 0.42 | 0.5 (±0.0) | 3.5 (±0.1) | 347.4 | 0.14 win | 0.00 |
| jpeg-decode-rgba | 44.2 | 79.5 | 0.56 win | skia 148.3 | 0.30 | 0.4 (±0.1) | 0.7 (±0.0) | - | 0.40 win | - |
| jpeg-encode | 81.8 | 100.9 | 0.81 win | netvips 52.9 | 1.55 ✗ | 5.8 (±2.8) | 4.2 (±1.6) | 64.9 | 1.38 ✗ | 0.09 |
| gif-decode | 2.3 | 6.1 | 0.38 win | skia 1.0 | 2.27 ✗ | 0.3 (±0.1) | 0.4 (±0.0) | - | 0.30 win | - |
| gif-resize | 39.1 | 47.5 | 0.82 win | magick 798.5 | 0.05 | 2.4 (±0.1) | 2.6 (±0.1) | 82.0 | 0.92 win | 0.03 |
| cmyk-jpeg-to-srgb | 241.2 | 149.4 (skips ICC) | 1.61 | netvips 274.7 | 0.88 | 2.0 (±0.5) | 1.3 (±0.1) | 413.9 | 1.54 | 0.00 |

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
| thumb-jpeg | peak | miss | 2.2 MiB (spread 0.6) | 1.2 MiB (spread 0.0) | 1.83× | |
| jpeg-encode | peak | miss | 5.8 MiB (spread 2.8) | 4.2 MiB (spread 1.6) | 1.38× | |
| cmyk-jpeg-to-srgb | time | miss | 241.2 ms (σ 9.6) | 149.4 ms (σ 0.9) | 1.61× | information only: ImageSharp skips the ICC conversion |
| cmyk-jpeg-to-srgb | peak | miss | 2.0 MiB (spread 0.5) | 1.3 MiB (spread 0.1) | 1.54× | information only: ImageSharp skips the ICC conversion |

## Gaps

- **thumb-jpeg**: more peak working memory than ImageSharp, 2 MiB vs 1 MiB (1.83×).
- **jpeg-encode**: more peak working memory than ImageSharp, 6 MiB vs 4 MiB (1.38×).
- **jpeg-encode**: 1.55× the fastest native wrapper (netvips 52.9 ms vs ours 81.8 ms).
- **gif-decode**: 2.27× the fastest native wrapper (skia 1.0 ms vs ours 2.3 ms).

## Rows

### thumb-jpeg

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Image.Thumbnail(bytes, 400) -> JPEG q90 | 38.1 | 0.1 | 2 ±1 (217) |
| magick | Magick.NET jpeg:size + Thumbnail + JPEG q90 | 40.3 | 0.1 | 1 ±0 (207) |
| skia | SkiaSharp scaled decode + resize + encode | 43.9 | 0.0 | 0 ±0 (206) |
| imagesharp | ImageSharp load(TargetSize) + Resize + JPEG q90 | 48.2 | 0.1 | 1 ±0 (215) |
| netvips | NetVips thumbnail_buffer + jpegsave_buffer q90 | 50.6 | 0.0 | 68 ±6 (328) |

### jpeg-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Image.Load + CopyPixelsTo reused Rgb24 buffer, 24 MP | 41.7 | 0.0 | 0 ±0 (277) |
| imagesharp | ImageSharp Image.Load<Rgb24>, 24 MP | 76.5 | 0.0 | 4 ±0 (280) |
| netvips | NetVips new_from_buffer + write_to_memory, 24 MP | 98.2 | 68.7 | 347 ±6 (702) |
| magick | Magick.NET read, 24 MP | 116.8 | 0.0 | 68 ±1 (269) |
| skia | SkiaSharp SKBitmap.Decode, 24 MP | 151.9 | 0.0 | 90 ±1 (291) |

### jpeg-decode-rgba

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Image.Load + CopyPixelsTo reused Rgba32 buffer, 24 MP | 44.2 | 0.0 | 0 ±0 (295) |
| imagesharp | ImageSharp Image.Load<Rgba32>, 24 MP | 79.5 | 0.0 | 1 ±0 (298) |
| skia | SkiaSharp SKBitmap.Decode (Rgba8888), 24 MP | 148.3 | 0.0 | 90 ±0 (286) |

### jpeg-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips q90 4:2:0 | 52.9 | 5.5 | 65 ±25 (687) |
| oneimage | OneImage q90 4:2:0 optimized tables (default) | 81.8 | 8.4 | 6 ±3 (547) |
| imagesharp | ImageSharp q90 4:2:0 | 100.9 | 8.0 | 4 ±2 (541) |
| magick | Magick.NET q90 4:2:0 | 165.6 | 16.0 | 81 ±1 (615) |
| skia | SkiaSharp q90 4:2:0 | 292.8 | 8.0 | 164 ±1 (694) |

### gif-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp: decode all frames | 1.0 | 0.0 | 0 ±0 (65) |
| oneimage | oneimage: decode 50 frames 500x500 | 2.3 | 1.0 | 0 ±0 (64) |
| imagesharp | ImageSharp: decode | 6.1 | 0.2 | 0 ±0 (116) |
| magick | Magick.NET: decode + coalesce | 15.2 | 0.2 | 0 ±36 (96) |

### gif-resize

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Resize(200) + Save GIF | 39.1 | 2.1 | 2 ±0 (78) |
| imagesharp | ImageSharp Load + Resize(200) + Save GIF | 47.5 | 0.7 | 3 ±0 (133) |
| magick | Magick.NET coalesce + Resize(200) + Write GIF | 798.5 | 6.3 | 4 ±1 (101) |
| netvips | NetVips n=-1 + resize(0.4) + gifsave_buffer | 2711.8 | 1.7 | 82 ±4 (223) |

### cmyk-jpeg-to-srgb

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Load<Rgb24> (skips ICC conversion) | 149.4 | 0.4 | 1 ±0 (140) |
| oneimage | OneImage Load + ConvertColor(sRGB) + CopyPixelsTo reused Rgb24 buffer | 241.2 | 2.0 | 2 ±0 (162) |
| netvips | NetVips load + icc_transform(sRGB) | 274.7 | 68.7 | 414 ±45 (685) |
| skia | SkiaSharp SKBitmap.Decode to Rgba8888 | 555.4 | 0.0 | 91 ±0 (155) |
| magick | Magick.NET (LittleCMS) read + profile transform | 1017.1 | 0.3 | 159 ±1 (223) |

