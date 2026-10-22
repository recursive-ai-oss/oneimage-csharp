# Release gate report

> final run, native AVX-512 (information only)

## Host

- Started 2026-10-22 01:15:32 CDT, finished 2026-10-22 01:59:57 CDT
- CPU: AMD Ryzen 9 7950X3D 16-Core Processor, 32 logical CPUs, 125 GiB RAM
- OS: Ubuntu 26.04 LTS, kernel 7.0.0-38-generic
- .NET SDK 10.0.400; Runtime .NET 10.0.11; Avx2.IsSupported=True Avx512F.IsSupported=True Vector256.IsHardwareAccelerated=True Vector512.IsHardwareAccelerated=True
- AVX-512: enabled, native run, information only
- Load before: ` 01:15:32 up 10:33,  1 user,  load average: 0.40, 0.50, 0.92`
- Load during (sampled each minute):
  - 01:15:32 load average: 0.40, 0.50, 0.92
  - 01:16:32 load average: 1.31, 0.78, 1.00
  - 01:17:32 load average: 1.69, 0.95, 1.04
  - 01:18:32 load average: 1.54, 1.03, 1.06
  - 01:19:32 load average: 1.20, 1.02, 1.05
  - 01:20:32 load average: 1.45, 1.16, 1.10
  - 01:21:32 load average: 1.20, 1.14, 1.09
  - 01:22:32 load average: 1.13, 1.13, 1.09
  - 01:23:32 load average: 1.18, 1.15, 1.10
  - 01:24:32 load average: 1.22, 1.16, 1.10
  - 01:25:32 load average: 1.39, 1.21, 1.12
  - 01:26:32 load average: 1.48, 1.27, 1.14
  - 01:27:32 load average: 1.65, 1.35, 1.18
  - 01:28:32 load average: 2.01, 1.49, 1.24
  - 01:29:32 load average: 1.44, 1.43, 1.23
  - 01:30:32 load average: 1.34, 1.40, 1.23
  - 01:31:32 load average: 1.45, 1.43, 1.26
  - 01:32:32 load average: 1.37, 1.41, 1.26
  - 01:33:32 load average: 1.60, 1.45, 1.28
  - 01:34:32 load average: 2.30, 1.63, 1.35
  - 01:35:32 load average: 1.73, 1.59, 1.36
  - 01:36:32 load average: 1.45, 1.53, 1.35
  - 01:37:32 load average: 1.38, 1.49, 1.35
  - 01:38:32 load average: 1.75, 1.56, 1.38
  - 01:39:32 load average: 1.47, 1.50, 1.37
  - 01:40:32 load average: 2.01, 1.68, 1.44
  - 01:41:32 load average: 1.66, 1.67, 1.46
  - 01:42:32 load average: 1.72, 1.67, 1.47
  - 01:43:32 load average: 1.97, 1.76, 1.51
  - 01:44:32 load average: 1.39, 1.63, 1.48
  - 01:45:32 load average: 1.54, 1.64, 1.49
  - 01:46:32 load average: 1.28, 1.55, 1.47
  - 01:47:33 load average: 1.23, 1.49, 1.45
  - 01:48:33 load average: 1.30, 1.47, 1.45
  - 01:49:33 load average: 1.27, 1.43, 1.44
  - 01:50:33 load average: 1.30, 1.41, 1.43
  - 01:51:33 load average: 1.30, 1.39, 1.42
  - 01:52:33 load average: 1.15, 1.33, 1.40
  - 01:53:33 load average: 1.05, 1.27, 1.37
  - 01:54:33 load average: 0.99, 1.22, 1.35
  - 01:55:33 load average: 1.10, 1.21, 1.33
  - 01:56:33 load average: 1.26, 1.22, 1.33
  - 01:57:33 load average: 1.34, 1.24, 1.33
  - 01:58:33 load average: 1.24, 1.24, 1.32
  - 01:59:33 load average: 3.45, 1.74, 1.48
- Load after: ` 01:59:57 up 11:17,  1 user,  load average: 2.68, 1.70, 1.47`

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
| thumb-jpeg | 43.0 | 47.2 | 0.91 win | magick 39.7 | 1.08 | 1.7 (±0.8) | 1.2 (±0.0) | 66.2 | 1.42 ✗ | 0.03 |
| thumb-webp | 45.9 | 65.6 | 0.70 win | magick 44.0 | 1.04 | 3.2 (±0.1) | 9.4 (±0.1) | 64.1 | 0.34 win | 0.05 |
| png-to-webp (Content=natural) | 76.7 | 1489.1 | 0.05 win | skia 149.9 | 0.51 | 43.2 (±4.3) | 87.5 (±0.5) | 127.1 | 0.49 win | 0.34 |
| png-to-webp (Content=photo) | 90.8 | 1041.1 | 0.09 win | magick 156.6 | 0.58 | 43.2 (±4.3) | 87.5 (±0.5) | 127.1 | 0.49 win | 0.34 |
| png-to-webp (Content=screenshot) | 99.4 | 251.0 | 0.40 win | magick 166.8 | 0.60 | 43.2 (±4.3) | 87.5 (±0.5) | 127.1 | 0.49 win | 0.34 |
| jpeg-decode | 74.9 | 76.9 | 0.97 tie | netvips 101.1 | 0.74 | 0.4 (±0.1) | 3.5 (±0.2) | 345.7 | 0.11 win | 0.00 |
| jpeg-encode | 144.8 | 102.7 | 1.41 ✗ | netvips 59.4 | 2.44 ✗ | 5.7 (±1.1) | 4.3 (±0.5) | 65.7 | 1.33 ✗ | 0.09 |
| png-decode (Content=natural) | 11.4 | 39.3 | 0.29 win | skia 19.5 | 0.59 | 0.3 (±0.0) | 0.5 (±0.2) | 87.2 | 0.30 win | 0.00 |
| png-decode (Content=photo) | 17.9 | 22.1 | 0.81 win | skia 21.6 | 0.83 | 0.3 (±0.0) | 0.5 (±0.2) | 87.2 | 0.30 win | 0.00 |
| png-decode (Content=screenshot) | 2.9 | 4.1 | 0.70 win | skia 4.2 | 0.68 | 0.3 (±0.0) | 0.5 (±0.2) | 87.2 | 0.30 win | 0.00 |
| png-encode (Content=natural) | 222.7 | 1104.4 | 0.20 win | netvips 97.4 | 2.29 ✗ | 11.8 (±0.6) | 33.9 (±0.4) | 20.5 | 0.35 win | 0.58 |
| png-encode (Content=photo) | 53.2 | 322.4 | 0.16 win | netvips 97.5 | 0.55 | 11.8 (±0.6) | 33.9 (±0.4) | 20.5 | 0.35 win | 0.58 |
| png-encode (Content=screenshot) | 52.7 | 96.5 | 0.55 win | netvips 39.9 | 1.32 | 11.8 (±0.6) | 33.9 (±0.4) | 20.5 | 0.35 win | 0.58 |
| webp-decode (Lossless=False) | 16.8 | 31.2 | 0.54 win | skia 17.7 | 0.95 | 2.1 (±0.2) | 2.8 (±0.0) | 171.7 | 0.75 win | 0.01 |
| webp-decode (Lossless=True) | 24.5 | 69.0 | 0.35 win | skia 35.1 | 0.70 | 2.1 (±0.2) | 2.8 (±0.0) | 171.7 | 0.75 win | 0.01 |
| webp-encode (Case=lossless-2000x1500) | 146.0 | 1095.1 | 0.13 win | skia 100.5 | 1.45 | 24.0 (±0.3) | 97.6 (±0.9) | 76.9 | 0.25 win | 0.31 |
| webp-encode (Case=lossy-2000x1500) | 103.0 | 581.8 | 0.18 win | magick 166.1 | 0.62 | 24.0 (±0.3) | 97.6 (±0.9) | 76.9 | 0.25 win | 0.31 |
| webp-encode (Case=lossy-400x300) | 5.4 | 23.9 | 0.23 win | magick 7.3 | 0.74 | 24.0 (±0.3) | 97.6 (±0.9) | 76.9 | 0.25 win | 0.31 |
| gif-decode | 2.2 | 6.2 | 0.36 win | skia 1.0 | 2.13 ✗ | 2.4 (±1.1) | 0.4 (±0.0) | - | 2.40 ✗ | - |
| gif-encode | 13.6 | 19.5 | 0.70 win | magick 70.2 | 0.19 | 1.2 (±0.0) | 2.3 (±0.0) | - | 0.52 win | - |
| gif-resize | 39.1 | 47.9 | 0.82 win | magick 799.6 | 0.05 | 3.4 (±0.0) | 3.2 (±1.3) | 80.9 | 1.06 ✗ | 0.04 |
| tiff-multipage-decode | 87.5 | 583.7 | 0.15 win | netvips 28.3 | 3.09 ✗ | 2.6 (±0.3) | 54.8 (±0.1) | 84.2 | 0.05 win | 0.03 |
| tiff-encode | 43.8 | 131.2 | 0.33 win | magick 69.0 | 0.63 | 28.3 (±0.6) | 32.3 (±1.3) | 39.5 | 0.88 win | 0.72 |
| bmp-decode | 0.4 | 0.7 | 0.53 win | skia 1.1 | 0.32 | 0.1 (±0.0) | 0.1 (±0.1) | - | 0.10 tie | - |
| bmp-encode | 2.7 | 4.8 | 0.57 win | magick 6.6 | 0.42 | 7.6 (±1.2) | 22.0 (±0.3) | - | 0.35 win | - |
| upscale-2x | 6.3 | 12.5 | 0.50 win | netvips 10.0 | 0.63 | 0.4 (±0.2) | 0.5 (±0.1) | 42.2 | 0.40 win | 0.01 |
| watermark | 4.9 | 5.7 | 0.86 win | netvips 62.2 | 0.08 | 0.3 (±0.1) | 0.4 (±0.0) | 182.2 | 0.30 win | 0.00 |
| cmyk-jpeg-to-srgb | 232.1 | 142.2 (skips ICC) | 1.63 | netvips 274.3 | 0.85 | 3.6 (±0.2) | 1.3 (±0.0) | 429.6 | 2.77 | 0.01 |
| jpeg-decode-rgba | 85.7 | 77.8 | 1.10 ✗ | skia 146.7 | 0.58 | - (±-) | - (±-) | - | - | - |

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
| thumb-jpeg | peak | miss | 1.7 MiB (spread 0.8) | 1.2 MiB (spread 0.0) | 1.42× | |
| jpeg-decode | time | tie | 74.9 ms (σ 0.8) | 76.9 ms (σ 0.7) | 0.97× | |
| jpeg-encode | time | miss | 144.8 ms (σ 1.2) | 102.7 ms (σ 1.2) | 1.41× | |
| jpeg-encode | peak | miss | 5.7 MiB (spread 1.1) | 4.3 MiB (spread 0.5) | 1.33× | |
| gif-decode | peak | miss | 2.4 MiB (spread 1.1) | 0.4 MiB (spread 0.0) | 2.40× | |
| gif-resize | peak | miss | 3.4 MiB (spread 0.0) | 3.2 MiB (spread 1.3) | 1.06× | |
| bmp-decode | peak | tie | 0.1 MiB (spread 0.0) | 0.1 MiB (spread 0.1) | 0.10× | |
| cmyk-jpeg-to-srgb | time | miss | 232.1 ms (σ 3.6) | 142.2 ms (σ 1.0) | 1.63× | information only: ImageSharp skips the ICC conversion |
| cmyk-jpeg-to-srgb | peak | miss | 3.6 MiB (spread 0.2) | 1.3 MiB (spread 0.0) | 2.77× | information only: ImageSharp skips the ICC conversion |
| jpeg-decode-rgba | time | miss | 85.7 ms (σ 0.5) | 77.8 ms (σ 0.4) | 1.10× | |

## Gaps

- **thumb-jpeg**: more peak working memory than ImageSharp, 2 MiB vs 1 MiB (1.42×).
- **jpeg-encode**: slower than ImageSharp (beyond a 5% tie), 144.8 ms vs 102.7 ms (1.41×).
- **jpeg-encode**: more peak working memory than ImageSharp, 6 MiB vs 4 MiB (1.33×).
- **jpeg-encode**: 2.44× the fastest native wrapper (netvips 59.4 ms vs ours 144.8 ms).
- **png-encode (Content=natural)**: 2.29× the fastest native wrapper (netvips 97.4 ms vs ours 222.7 ms).
- **gif-decode**: more peak working memory than ImageSharp, 2 MiB vs 0 MiB (2.40×).
- **gif-decode**: 2.13× the fastest native wrapper (skia 1.0 ms vs ours 2.2 ms).
- **gif-resize**: more peak working memory than ImageSharp, 3 MiB vs 3 MiB (1.06×).
- **tiff-multipage-decode**: 3.09× the fastest native wrapper (netvips 28.3 ms vs ours 87.5 ms).
- **jpeg-decode-rgba**: slower than ImageSharp (beyond a 5% tie), 85.7 ms vs 77.8 ms (1.10×).

## Rows

### thumb-jpeg

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET jpeg:size + Thumbnail + JPEG q90 | 39.7 | 0.1 | 1 ±0 (207) |
| skia | SkiaSharp scaled decode + resize + encode | 42.8 | 0.0 | 1 ±0 (206) |
| oneimage | OneImage Image.Thumbnail(bytes, 400) -> JPEG q90 | 43.0 | 0.1 | 2 ±1 (216) |
| imagesharp | ImageSharp load(TargetSize) + Resize + JPEG q90 | 47.2 | 0.1 | 1 ±0 (216) |
| netvips | NetVips thumbnail_buffer + jpegsave_buffer q90 | 49.6 | 0.0 | 66 ±2 (325) |

### thumb-webp

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET jpeg:size + Thumbnail + WebP q75 | 44.0 | 0.0 | 1 ±1 (208) |
| oneimage | OneImage Image.Thumbnail(bytes, 400) -> WebP q75 | 45.9 | 1.5 | 3 ±0 (216) |
| skia | SkiaSharp scaled decode + resize + WebP q75 | 48.0 | 0.0 | 0 ±0 (206) |
| netvips | NetVips thumbnail_buffer + webpsave_buffer q75 | 56.6 | 0.0 | 64 ±8 (329) |
| imagesharp | ImageSharp load(TargetSize) + Resize + WebP q75 | 65.6 | 6.0 | 9 ±0 (225) |

### png-to-webp (Content=natural)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Save WebP q75 | 76.7 | 42.5 | 43 ±4 (131) |
| skia | SkiaSharp decode + WebP q75 | 149.9 | 0.0 | 37 ±1 (114) |
| netvips | NetVips load + webpsave q75 | 160.2 | 0.1 | 127 ±6 (270) |
| magick | Magick.NET read + WebP q75 | 161.6 | 0.2 | 26 ±0 (104) |
| imagesharp | ImageSharp load + WebP q75 | 1489.1 | 40.5 | 88 ±0 (237) |

### png-to-webp (Content=photo)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Save WebP q75 | 90.8 | 42.6 | 43 ±4 (131) |
| magick | Magick.NET read + WebP q75 | 156.6 | 0.2 | 26 ±0 (104) |
| skia | SkiaSharp decode + WebP q75 | 173.5 | 0.0 | 37 ±1 (114) |
| netvips | NetVips load + webpsave q75 | 173.6 | 0.1 | 127 ±6 (270) |
| imagesharp | ImageSharp load + WebP q75 | 1041.1 | 35.8 | 88 ±0 (237) |

### png-to-webp (Content=screenshot)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Save WebP q75 | 99.4 | 45.2 | 43 ±4 (131) |
| magick | Magick.NET read + WebP q75 | 166.8 | 1.0 | 26 ±0 (104) |
| netvips | NetVips load + webpsave q75 | 180.5 | 0.4 | 127 ±6 (270) |
| skia | SkiaSharp decode + WebP q75 | 184.3 | 0.0 | 37 ±1 (114) |
| imagesharp | ImageSharp load + WebP q75 | 251.0 | 29.6 | 88 ±0 (237) |

### jpeg-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Image.Load + CopyPixelsTo reused Rgb24 buffer, 24 MP | 74.9 | 0.0 | 0 ±0 (276) |
| imagesharp | ImageSharp Image.Load<Rgb24>, 24 MP | 76.9 | 0.0 | 4 ±0 (280) |
| netvips | NetVips new_from_buffer + write_to_memory, 24 MP | 101.1 | 68.7 | 346 ±23 (700) |
| magick | Magick.NET read, 24 MP | 113.1 | 0.0 | 67 ±1 (269) |
| skia | SkiaSharp SKBitmap.Decode, 24 MP | 149.0 | 0.0 | 90 ±0 (291) |

### jpeg-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips q90 4:2:0 | 59.4 | 5.5 | 66 ±5 (690) |
| imagesharp | ImageSharp q90 4:2:0 | 102.7 | 8.0 | 4 ±0 (541) |
| oneimage | OneImage q90 4:2:0 optimized tables (default) | 144.8 | 8.3 | 6 ±1 (547) |
| magick | Magick.NET q90 4:2:0 | 166.1 | 16.0 | 81 ±1 (616) |
| skia | SkiaSharp q90 4:2:0 | 295.6 | 8.0 | 165 ±2 (696) |

### png-decode (Content=natural)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage | 11.4 | 0.0 | 0 ±0 (103) |
| skia | SkiaSharp | 19.5 | 0.0 | 0 ±0 (102) |
| netvips | NetVips | 29.4 | 8.6 | 87 ±3 (221) |
| imagesharp | ImageSharp | 39.3 | 0.0 | 0 ±0 (106) |
| magick | Magick.NET | 45.3 | 0.0 | 0 ±0 (101) |

### png-decode (Content=photo)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage | 17.9 | 0.0 | 0 ±0 (103) |
| skia | SkiaSharp | 21.6 | 0.0 | 0 ±0 (102) |
| imagesharp | ImageSharp | 22.1 | 0.1 | 0 ±0 (106) |
| netvips | NetVips | 29.3 | 8.6 | 87 ±3 (221) |
| magick | Magick.NET | 31.4 | 0.0 | 0 ±0 (101) |

### png-decode (Content=screenshot)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage | 2.9 | 0.0 | 0 ±0 (103) |
| imagesharp | ImageSharp | 4.1 | 0.0 | 0 ±0 (106) |
| skia | SkiaSharp | 4.2 | 0.0 | 0 ±0 (102) |
| netvips | NetVips | 11.9 | 8.6 | 87 ±3 (221) |
| magick | Magick.NET | 12.8 | 0.0 | 0 ±0 (101) |

### png-encode (Content=natural)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips | 97.4 | 7.0 | 20 ±6 (198) |
| oneimage | OneImage | 222.7 | 9.6 | 12 ±1 (166) |
| skia | SkiaSharp | 698.1 | 2.6 | 0 ±0 (160) |
| imagesharp | ImageSharp | 1104.4 | 20.7 | 34 ±0 (192) |
| magick | Magick.NET | 1614.0 | 8.1 | 8 ±2 (171) |

### png-encode (Content=photo)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage | 53.2 | 19.2 | 12 ±1 (166) |
| netvips | NetVips | 97.5 | 7.8 | 20 ±6 (198) |
| skia | SkiaSharp | 210.2 | 4.1 | 0 ±0 (160) |
| imagesharp | ImageSharp | 322.4 | 40.9 | 34 ±0 (192) |
| magick | Magick.NET | 669.5 | 16.2 | 8 ±2 (171) |

### png-encode (Content=screenshot)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips | 39.9 | 0.3 | 20 ±6 (198) |
| oneimage | OneImage | 52.7 | 1.4 | 12 ±1 (166) |
| skia | SkiaSharp | 84.0 | 0.3 | 0 ±0 (160) |
| imagesharp | ImageSharp | 96.5 | 2.6 | 34 ±0 (192) |
| magick | Magick.NET | 111.3 | 1.0 | 8 ±2 (171) |

### webp-decode (Lossless=False)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImageLib | 16.8 | 0.4 | 2 ±0 (225) |
| oneimage | OneImageNative | 17.3 | 8.9 |  |
| skia | SkiaSharp | 17.7 | 0.0 | 0 ±0 (222) |
| magick | Magick.NET | 20.8 | 0.0 | 0 ±0 (210) |
| imagesharp | ImageSharp | 31.2 | 0.5 | 3 ±0 (218) |
| imagesharp | ImageSharpRgb24 | 31.2 | 0.5 |  |
| netvips | NetVips | 43.9 | 8.6 | 172 ±20 (428) |

### webp-decode (Lossless=True)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImageLib | 24.5 | 0.2 | 2 ±0 (225) |
| oneimage | OneImageNative | 25.0 | 8.7 |  |
| skia | SkiaSharp | 35.1 | 0.0 | 0 ±0 (222) |
| magick | Magick.NET | 51.5 | 0.0 | 0 ±0 (210) |
| netvips | NetVips | 66.8 | 8.6 | 172 ±20 (428) |
| imagesharp | ImageSharp | 69.0 | 0.2 | 3 ±0 (218) |
| imagesharp | ImageSharpRgb24 | 70.3 | 0.2 |  |

### webp-encode (Case=lossless-2000x1500)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp | 100.5 | 13.1 | 1 ±0 (218) |
| oneimage | OneImageLib | 146.0 | 117.8 | 24 ±0 (245) |
| netvips | NetVips | 745.9 | 5.8 | 77 ±7 (336) |
| imagesharp | ImageSharp | 1095.1 | 100.8 | 98 ±1 (318) |
| magick | Magick.NET | 1552.8 | 8.0 | 1 ±0 (233) |

### webp-encode (Case=lossy-2000x1500)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImageLib | 103.0 | 32.1 | 24 ±0 (245) |
| magick | Magick.NET | 166.1 | 1.0 | 1 ±0 (233) |
| skia | SkiaSharp | 195.2 | 1.4 | 1 ±0 (218) |
| netvips | NetVips | 209.2 | 0.4 | 77 ±7 (336) |
| imagesharp | ImageSharp | 581.8 | 210.7 | 98 ±1 (318) |

### webp-encode (Case=lossy-400x300)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImageLib | 5.4 | 1.6 | 24 ±0 (245) |
| magick | Magick.NET | 7.3 | 0.1 | 1 ±0 (233) |
| skia | SkiaSharp | 8.6 | 0.0 | 1 ±0 (218) |
| netvips | NetVips | 9.4 | 0.0 | 77 ±7 (336) |
| imagesharp | ImageSharp | 23.9 | 6.7 | 98 ±1 (318) |

### gif-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp: decode all frames | 1.0 | 0.0 | 0 ±0 (64) |
| oneimage | oneimage: decode 50 frames 500x500 | 2.2 | 0.1 | 2 ±1 (66) |
| imagesharp | ImageSharp: decode | 6.2 | 0.2 | 0 ±0 (116) |
| magick | Magick.NET: decode + coalesce | 15.4 | 0.2 | 1 ±36 (96) |

### gif-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | oneimage: re-encode 50 frames | 13.6 | 0.8 | 1 ±0 (160) |
| imagesharp | ImageSharp: re-encode | 19.5 | 0.7 | 2 ±0 (161) |
| magick | Magick.NET: re-encode | 70.2 | 8.0 | 5 ±1 (193) |

### gif-resize

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Resize(200) + Save GIF | 39.1 | 2.3 | 3 ±0 (79) |
| imagesharp | ImageSharp Load + Resize(200) + Save GIF | 47.9 | 0.7 | 3 ±1 (133) |
| magick | Magick.NET coalesce + Resize(200) + Write GIF | 799.6 | 6.3 | 4 ±1 (101) |
| netvips | NetVips n=-1 + resize(0.4) + gifsave_buffer | 2692.9 | 1.7 | 81 ±2 (222) |

### tiff-multipage-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips | 28.3 | 85.9 | 84 ±1 (305) |
| oneimage-degree | oneimage, NOT DEFAULT: per-call maxDegree = all cores | 51.6 | 1.0 |  |
| oneimage-degree | oneimage, NOT DEFAULT: per-call maxDegree = 8 | 54.9 | 0.9 |  |
| oneimage | oneimage, pages into a reused buffer | 87.5 | 0.9 | 3 ±0 (62) |
| magick | Magick.NET | 212.7 | 0.0 | 138 ±1 (183) |
| imagesharp | ImageSharp | 583.7 | 990.5 | 55 ±0 (188) |

### tiff-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | oneimage (horizontal predictor) | 43.8 | 28.0 | 28 ±1 (86) |
| oneimage | oneimage (no predictor) | 56.3 | 44.1 |  |
| magick | Magick.NET | 69.0 | 16.0 | 24 ±1 (89) |
| netvips | NetVips | 85.3 | 15.8 | 40 ±1 (99) |
| imagesharp | ImageSharp | 131.2 | 32.1 | 32 ±1 (101) |

### bmp-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + CopyPixelsTo reused Rgb24 buffer | 0.4 | 0.0 | 0 ±0 (101) |
| imagesharp | ImageSharp Load<Rgb24> | 0.7 | 0.0 | 0 ±0 (109) |
| skia | SkiaSharp SKBitmap.Decode | 1.1 | 0.0 | 0 ±0 (114) |
| magick | Magick.NET | 5.9 | 0.0 | 9 ±0 (125) |

### bmp-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Save BMP | 2.7 | 8.6 | 8 ±1 (110) |
| imagesharp | ImageSharp Save BMP 24-bit | 4.8 | 23.6 | 22 ±0 (122) |
| magick | Magick.NET Write BMP3 | 6.6 | 32.2 | 31 ±1 (139) |

### upscale-2x

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | oneimage Catmull-Rom (linear) | 6.3 | 15.4 | 0 ±0 (108) |
| netvips | NetVips resize bicubic | 10.0 | 15.3 | 42 ±3 (162) |
| imagesharp | ImageSharp CatmullRom | 12.5 | 0.1 | 0 ±0 (118) |
| skia | SkiaSharp Catmull-Rom | 82.0 | 0.0 | 15 ±0 (119) |
| magick | Magick.NET Catrom | 154.0 | 0.0 | 23 ±0 (126) |

### watermark

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Composite + CopyPixelsTo | 4.9 | 0.0 | 0 ±0 (699) |
| imagesharp | ImageSharp Clone + DrawImage | 5.7 | 0.0 | 0 ±0 (771) |
| imagesharp | ImageSharp Clone + DrawImage + CopyPixelDataTo (packed RGB out) | 10.0 | 0.0 |  |
| netvips | NetVips composite2 + write_to_memory | 62.2 | 91.6 | 182 ±0 (900) |
| magick | Magick.NET Clone + Composite + packed RGB out | 88.6 | 68.7 | 204 ±0 (902) |
| skia | SkiaSharp copy + DrawBitmap | 159.0 | 0.0 | 182 ±2 (881) |

### cmyk-jpeg-to-srgb

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Load<Rgb24> (skips ICC conversion) | 142.2 | 0.4 | 1 ±0 (140) |
| oneimage | OneImage Load + ConvertColor(sRGB) + CopyPixelsTo reused Rgb24 buffer | 232.1 | 2.0 | 4 ±0 (163) |
| netvips | NetVips load + icc_transform(sRGB) | 274.3 | 68.7 | 430 ±47 (700) |
| skia | SkiaSharp SKBitmap.Decode to Rgba8888 | 552.5 | 0.0 | 90 ±1 (155) |
| magick | Magick.NET (LittleCMS) read + profile transform | 1007.2 | 0.3 | 159 ±1 (223) |

### jpeg-decode-rgba

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Image.Load<Rgba32>, 24 MP | 77.8 | 0.0 |  |
| oneimage | OneImage Image.Load + ToArray<Rgba32>, 24 MP | 85.7 | 91.6 |  |
| skia | SkiaSharp SKBitmap.Decode (Rgba8888), 24 MP | 146.7 | 0.0 |  |

