# Release gate report

> final release gate run, AVX2 configuration

## Host

- Started 2026-10-22 00:19:22 CDT, finished 2026-10-22 01:04:52 CDT
- CPU: AMD Ryzen 9 7950X3D 16-Core Processor, 32 logical CPUs, 125 GiB RAM
- OS: Ubuntu 26.04 LTS, kernel 7.0.0-38-generic
- .NET SDK 10.0.400; Runtime .NET 10.0.11; Avx2.IsSupported=True Avx512F.IsSupported=False Vector256.IsHardwareAccelerated=True Vector512.IsHardwareAccelerated=False
- AVX-512: disabled (DOTNET_EnableAVX512=0), the AVX2 gate configuration
- Load before: ` 00:19:22 up  9:37,  1 user,  load average: 3.98, 2.61, 3.31`
- Load during (sampled each minute):
  - 00:19:22 load average: 3.98, 2.61, 3.31
  - 00:20:22 load average: 3.28, 2.82, 3.35
  - 00:21:22 load average: 2.62, 2.69, 3.27
  - 00:22:22 load average: 3.21, 2.80, 3.27
  - 00:23:22 load average: 2.13, 2.56, 3.16
  - 00:24:22 load average: 2.20, 2.51, 3.10
  - 00:25:22 load average: 2.10, 2.41, 3.03
  - 00:26:22 load average: 1.81, 2.27, 2.94
  - 00:27:22 load average: 1.69, 2.16, 2.86
  - 00:28:22 load average: 1.69, 2.08, 2.79
  - 00:29:22 load average: 1.67, 2.03, 2.72
  - 00:30:22 load average: 1.90, 2.03, 2.68
  - 00:31:22 load average: 1.52, 1.90, 2.60
  - 00:32:22 load average: 1.38, 1.80, 2.52
  - 00:33:22 load average: 1.38, 1.73, 2.44
  - 00:34:22 load average: 1.35, 1.65, 2.37
  - 00:35:22 load average: 1.69, 1.70, 2.34
  - 00:36:22 load average: 2.11, 1.79, 2.33
  - 00:37:22 load average: 1.86, 1.78, 2.29
  - 00:38:22 load average: 1.88, 1.81, 2.27
  - 00:39:22 load average: 1.40, 1.69, 2.20
  - 00:40:22 load average: 1.18, 1.57, 2.12
  - 00:41:22 load average: 1.19, 1.50, 2.06
  - 00:42:22 load average: 1.19, 1.44, 2.00
  - 00:43:22 load average: 1.29, 1.41, 1.95
  - 00:44:22 load average: 2.76, 1.89, 2.09
  - 00:45:22 load average: 3.22, 2.22, 2.19
  - 00:46:22 load average: 2.09, 2.09, 2.15
  - 00:47:22 load average: 1.76, 1.97, 2.10
  - 00:48:22 load average: 1.75, 1.95, 2.08
  - 00:49:22 load average: 1.27, 1.77, 2.01
  - 00:50:22 load average: 1.74, 1.81, 2.01
  - 00:51:22 load average: 1.86, 1.83, 2.00
  - 00:52:22 load average: 1.36, 1.69, 1.94
  - 00:53:22 load average: 1.41, 1.64, 1.90
  - 00:54:22 load average: 1.24, 1.55, 1.86
  - 00:55:22 load average: 1.24, 1.51, 1.82
  - 00:56:22 load average: 1.20, 1.46, 1.79
  - 00:57:22 load average: 1.30, 1.43, 1.76
  - 00:58:22 load average: 1.11, 1.35, 1.71
  - 00:59:22 load average: 1.21, 1.32, 1.67
  - 01:00:22 load average: 1.66, 1.43, 1.69
  - 01:01:22 load average: 1.34, 1.38, 1.65
  - 01:02:22 load average: 1.04, 1.29, 1.60
  - 01:03:22 load average: 1.22, 1.29, 1.58
  - 01:04:22 load average: 1.28, 1.31, 1.57
- Load after: ` 01:04:52 up 10:22,  1 user,  load average: 1.23, 1.29, 1.55`

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
| thumb-jpeg | 43.3 | 47.2 | 0.92 win | magick 40.8 | 1.06 | 1.6 (±0.7) | 1.2 (±0.1) | 64.0 | 1.33 ✗ | 0.03 |
| thumb-webp | 46.9 | 65.4 | 0.72 win | magick 46.0 | 1.02 | 3.0 (±0.1) | 9.5 (±0.1) | 64.2 | 0.32 win | 0.05 |
| png-to-webp (Content=natural) | 79.1 | 1511.5 | 0.05 win | netvips 155.1 | 0.51 | 41.9 (±4.0) | 86.8 (±1.4) | 121.6 | 0.48 win | 0.34 |
| png-to-webp (Content=photo) | 90.5 | 1025.2 | 0.09 win | magick 157.6 | 0.57 | 41.9 (±4.0) | 86.8 (±1.4) | 121.6 | 0.48 win | 0.34 |
| png-to-webp (Content=screenshot) | 95.8 | 236.5 | 0.41 win | magick 162.3 | 0.59 | 41.9 (±4.0) | 86.8 (±1.4) | 121.6 | 0.48 win | 0.34 |
| jpeg-decode | 74.8 | 79.4 | 0.94 win | netvips 101.0 | 0.74 | 0.4 (±0.0) | 3.6 (±0.1) | 347.9 | 0.11 win | 0.00 |
| jpeg-encode | 145.2 | 100.6 | 1.44 ✗ | netvips 53.4 | 2.72 ✗ | 6.4 (±0.9) | 4.3 (±2.8) | 78.4 | 1.49 ✗ | 0.08 |
| png-decode (Content=natural) | 11.4 | 39.1 | 0.29 win | skia 19.6 | 0.58 | 0.2 (±0.1) | 0.3 (±0.0) | 90.3 | 0.20 win | 0.00 |
| png-decode (Content=photo) | 18.3 | 22.2 | 0.82 win | skia 21.9 | 0.83 | 0.2 (±0.1) | 0.3 (±0.0) | 90.3 | 0.20 win | 0.00 |
| png-decode (Content=screenshot) | 2.9 | 4.2 | 0.70 win | skia 4.2 | 0.71 | 0.2 (±0.1) | 0.3 (±0.0) | 90.3 | 0.20 win | 0.00 |
| png-encode (Content=natural) | 219.6 | 1064.3 | 0.21 win | netvips 96.0 | 2.29 ✗ | 11.2 (±1.1) | 33.6 (±0.6) | 24.1 | 0.33 win | 0.46 |
| png-encode (Content=photo) | 52.3 | 318.6 | 0.16 win | netvips 96.7 | 0.54 | 11.2 (±1.1) | 33.6 (±0.6) | 24.1 | 0.33 win | 0.46 |
| png-encode (Content=screenshot) | 50.8 | 95.0 | 0.53 win | netvips 39.8 | 1.27 | 11.2 (±1.1) | 33.6 (±0.6) | 24.1 | 0.33 win | 0.46 |
| webp-decode (Lossless=False) | 16.9 | 31.0 | 0.54 win | skia 17.9 | 0.94 | 2.3 (±0.3) | 2.9 (±0.1) | 162.8 | 0.79 win | 0.01 |
| webp-decode (Lossless=True) | 24.9 | 68.2 | 0.37 win | skia 35.0 | 0.71 | 2.3 (±0.3) | 2.9 (±0.1) | 162.8 | 0.79 win | 0.01 |
| webp-encode (Case=lossless-2000x1500) | 146.8 | 1107.1 | 0.13 win | skia 101.4 | 1.45 | 24.2 (±0.5) | 97.1 (±0.7) | 73.1 | 0.25 win | 0.33 |
| webp-encode (Case=lossy-2000x1500) | 101.6 | 590.5 | 0.17 win | magick 162.3 | 0.63 | 24.2 (±0.5) | 97.1 (±0.7) | 73.1 | 0.25 win | 0.33 |
| webp-encode (Case=lossy-400x300) | 5.4 | 23.7 | 0.23 win | magick 7.0 | 0.76 | 24.2 (±0.5) | 97.1 (±0.7) | 73.1 | 0.25 win | 0.33 |
| gif-decode | 2.1 | 6.1 | 0.34 win | skia 1.0 | 2.09 ✗ | 1.4 (±1.1) | 0.4 (±0.0) | - | 1.40 ✗ | - |
| gif-encode | 13.3 | 16.8 | 0.79 win | magick 70.3 | 0.19 | 1.2 (±0.1) | 2.0 (±1.1) | - | 0.60 win | - |
| gif-resize | 42.6 | 49.0 | 0.87 win | magick 823.4 | 0.05 | 3.5 (±0.2) | 2.6 (±0.5) | 82.0 | 1.35 ✗ | 0.04 |
| tiff-multipage-decode | 84.3 | 586.7 | 0.14 win | netvips 28.1 | 3.00 ✗ | 2.5 (±0.2) | 54.7 (±0.1) | 85.1 | 0.05 win | 0.03 |
| tiff-encode | 44.4 | 134.6 | 0.33 win | magick 68.5 | 0.65 | 28.5 (±0.3) | 32.1 (±0.6) | 40.3 | 0.89 win | 0.71 |
| bmp-decode | 0.4 | 0.7 | 0.57 win | skia 1.2 | 0.32 | 0.1 (±0.0) | 0.1 (±0.0) | - | 0.10 tie | - |
| bmp-encode | 2.8 | 4.9 | 0.57 win | magick 7.1 | 0.39 | 7.0 (±0.4) | 21.6 (±0.8) | - | 0.32 win | - |
| upscale-2x | 6.3 | 12.3 | 0.51 win | netvips 9.7 | 0.64 | 0.5 (±0.1) | 0.5 (±0.1) | 40.7 | 0.50 tie | 0.01 |
| watermark | 4.9 | 4.9 | 0.99 tie | netvips 61.9 | 0.08 | 0.3 (±0.0) | 0.5 (±0.1) | 183.8 | 0.30 win | 0.00 |
| cmyk-jpeg-to-srgb | 241.5 | 148.7 (skips ICC) | 1.62 | netvips 268.4 | 0.90 | 3.4 (±0.1) | 1.3 (±0.0) | 437.8 | 2.62 | 0.01 |
| jpeg-decode-rgba | 87.6 | 81.3 | 1.08 ✗ | skia 150.1 | 0.58 | - (±-) | - (±-) | - | - | - |

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
| thumb-jpeg | peak | miss | 1.6 MiB (spread 0.7) | 1.2 MiB (spread 0.1) | 1.33× | |
| jpeg-encode | time | miss | 145.2 ms (σ 2.2) | 100.6 ms (σ 0.6) | 1.44× | |
| jpeg-encode | peak | miss | 6.4 MiB (spread 0.9) | 4.3 MiB (spread 2.8) | 1.49× | |
| gif-decode | peak | miss | 1.4 MiB (spread 1.1) | 0.4 MiB (spread 0.0) | 1.40× | |
| gif-resize | peak | miss | 3.5 MiB (spread 0.2) | 2.6 MiB (spread 0.5) | 1.35× | |
| bmp-decode | peak | tie | 0.1 MiB (spread 0.0) | 0.1 MiB (spread 0.0) | 0.10× | |
| upscale-2x | peak | tie | 0.5 MiB (spread 0.1) | 0.5 MiB (spread 0.1) | 0.50× | |
| watermark | time | tie | 4.9 ms (σ 0.1) | 4.9 ms (σ 0.0) | 0.99× | |
| cmyk-jpeg-to-srgb | time | miss | 241.5 ms (σ 3.4) | 148.7 ms (σ 1.9) | 1.62× | information only: ImageSharp skips the ICC conversion |
| cmyk-jpeg-to-srgb | peak | miss | 3.4 MiB (spread 0.1) | 1.3 MiB (spread 0.0) | 2.62× | information only: ImageSharp skips the ICC conversion |
| jpeg-decode-rgba | time | miss | 87.6 ms (σ 0.6) | 81.3 ms (σ 0.6) | 1.08× | |

## Gaps

- **thumb-jpeg**: more peak working memory than ImageSharp, 2 MiB vs 1 MiB (1.33×).
- **jpeg-encode**: slower than ImageSharp (beyond a 5% tie), 145.2 ms vs 100.6 ms (1.44×).
- **jpeg-encode**: more peak working memory than ImageSharp, 6 MiB vs 4 MiB (1.49×).
- **jpeg-encode**: 2.72× the fastest native wrapper (netvips 53.4 ms vs ours 145.2 ms).
- **png-encode (Content=natural)**: 2.29× the fastest native wrapper (netvips 96.0 ms vs ours 219.6 ms).
- **gif-decode**: more peak working memory than ImageSharp, 1 MiB vs 0 MiB (1.40×).
- **gif-decode**: 2.09× the fastest native wrapper (skia 1.0 ms vs ours 2.1 ms).
- **gif-resize**: more peak working memory than ImageSharp, 4 MiB vs 3 MiB (1.35×).
- **tiff-multipage-decode**: 3.00× the fastest native wrapper (netvips 28.1 ms vs ours 84.3 ms).
- **jpeg-decode-rgba**: slower than ImageSharp (beyond a 5% tie), 87.6 ms vs 81.3 ms (1.08×).

## Rows

### thumb-jpeg

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET jpeg:size + Thumbnail + JPEG q90 | 40.8 | 0.1 | 1 ±1 (207) |
| skia | SkiaSharp scaled decode + resize + encode | 43.1 | 0.0 | 0 ±0 (206) |
| oneimage | OneImage Image.Thumbnail(bytes, 400) -> JPEG q90 | 43.3 | 0.1 | 2 ±1 (216) |
| imagesharp | ImageSharp load(TargetSize) + Resize + JPEG q90 | 47.2 | 0.1 | 1 ±0 (216) |
| netvips | NetVips thumbnail_buffer + jpegsave_buffer q90 | 49.5 | 0.0 | 64 ±3 (328) |

### thumb-webp

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET jpeg:size + Thumbnail + WebP q75 | 46.0 | 0.0 | 1 ±1 (208) |
| oneimage | OneImage Image.Thumbnail(bytes, 400) -> WebP q75 | 46.9 | 1.5 | 3 ±0 (215) |
| skia | SkiaSharp scaled decode + resize + WebP q75 | 48.7 | 0.0 | 0 ±0 (206) |
| netvips | NetVips thumbnail_buffer + webpsave_buffer q75 | 55.1 | 0.0 | 64 ±12 (326) |
| imagesharp | ImageSharp load(TargetSize) + Resize + WebP q75 | 65.4 | 6.0 | 10 ±0 (225) |

### png-to-webp (Content=natural)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Save WebP q75 | 79.1 | 42.5 | 42 ±4 (130) |
| netvips | NetVips load + webpsave q75 | 155.1 | 0.1 | 122 ±10 (265) |
| skia | SkiaSharp decode + WebP q75 | 157.0 | 0.0 | 38 ±1 (114) |
| magick | Magick.NET read + WebP q75 | 159.4 | 0.2 | 25 ±1 (103) |
| imagesharp | ImageSharp load + WebP q75 | 1511.5 | 40.5 | 87 ±1 (236) |

### png-to-webp (Content=photo)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Save WebP q75 | 90.5 | 42.6 | 42 ±4 (130) |
| magick | Magick.NET read + WebP q75 | 157.6 | 0.2 | 25 ±1 (103) |
| skia | SkiaSharp decode + WebP q75 | 170.0 | 0.0 | 38 ±1 (114) |
| netvips | NetVips load + webpsave q75 | 172.9 | 0.1 | 122 ±10 (265) |
| imagesharp | ImageSharp load + WebP q75 | 1025.2 | 35.8 | 87 ±1 (236) |

### png-to-webp (Content=screenshot)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Save WebP q75 | 95.8 | 45.2 | 42 ±4 (130) |
| magick | Magick.NET read + WebP q75 | 162.3 | 1.0 | 25 ±1 (103) |
| netvips | NetVips load + webpsave q75 | 184.9 | 0.4 | 122 ±10 (265) |
| skia | SkiaSharp decode + WebP q75 | 185.3 | 0.0 | 38 ±1 (114) |
| imagesharp | ImageSharp load + WebP q75 | 236.5 | 29.6 | 87 ±1 (236) |

### jpeg-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Image.Load + CopyPixelsTo reused Rgb24 buffer, 24 MP | 74.8 | 0.0 | 0 ±0 (276) |
| imagesharp | ImageSharp Image.Load<Rgb24>, 24 MP | 79.4 | 0.0 | 4 ±0 (280) |
| netvips | NetVips new_from_buffer + write_to_memory, 24 MP | 101.0 | 68.7 | 348 ±32 (702) |
| magick | Magick.NET read, 24 MP | 116.2 | 0.0 | 68 ±0 (269) |
| skia | SkiaSharp SKBitmap.Decode, 24 MP | 148.5 | 0.0 | 91 ±1 (291) |

### jpeg-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips q90 4:2:0 | 53.4 | 5.5 | 78 ±21 (712) |
| imagesharp | ImageSharp q90 4:2:0 | 100.6 | 8.0 | 4 ±3 (541) |
| oneimage | OneImage q90 4:2:0 optimized tables (default) | 145.2 | 8.3 | 6 ±1 (548) |
| magick | Magick.NET q90 4:2:0 | 172.1 | 16.0 | 81 ±2 (615) |
| skia | SkiaSharp q90 4:2:0 | 301.5 | 8.0 | 164 ±1 (694) |

### png-decode (Content=natural)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage | 11.4 | 0.0 | 0 ±0 (103) |
| skia | SkiaSharp | 19.6 | 0.0 | 0 ±0 (102) |
| netvips | NetVips | 29.7 | 8.6 | 90 ±3 (225) |
| imagesharp | ImageSharp | 39.1 | 0.0 | 0 ±0 (106) |
| magick | Magick.NET | 46.7 | 0.0 | 0 ±0 (101) |

### png-decode (Content=photo)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage | 18.3 | 0.0 | 0 ±0 (103) |
| skia | SkiaSharp | 21.9 | 0.0 | 0 ±0 (102) |
| imagesharp | ImageSharp | 22.2 | 0.1 | 0 ±0 (106) |
| netvips | NetVips | 29.6 | 8.6 | 90 ±3 (225) |
| magick | Magick.NET | 31.8 | 0.0 | 0 ±0 (101) |

### png-decode (Content=screenshot)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage | 2.9 | 0.0 | 0 ±0 (103) |
| skia | SkiaSharp | 4.2 | 0.0 | 0 ±0 (102) |
| imagesharp | ImageSharp | 4.2 | 0.0 | 0 ±0 (106) |
| netvips | NetVips | 11.5 | 8.6 | 90 ±3 (225) |
| magick | Magick.NET | 13.2 | 0.0 | 0 ±0 (101) |

### png-encode (Content=natural)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips | 96.0 | 7.0 | 24 ±2 (201) |
| oneimage | OneImage | 219.6 | 9.6 | 11 ±1 (165) |
| skia | SkiaSharp | 683.1 | 2.6 | 0 ±1 (160) |
| imagesharp | ImageSharp | 1064.3 | 20.7 | 34 ±1 (192) |
| magick | Magick.NET | 1640.3 | 8.1 | 9 ±1 (173) |

### png-encode (Content=photo)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage | 52.3 | 19.2 | 11 ±1 (165) |
| netvips | NetVips | 96.7 | 7.8 | 24 ±2 (201) |
| skia | SkiaSharp | 207.4 | 4.1 | 0 ±1 (160) |
| imagesharp | ImageSharp | 318.6 | 40.9 | 34 ±1 (192) |
| magick | Magick.NET | 689.6 | 16.2 | 9 ±1 (173) |

### png-encode (Content=screenshot)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips | 39.8 | 0.3 | 24 ±2 (201) |
| oneimage | OneImage | 50.8 | 1.4 | 11 ±1 (165) |
| skia | SkiaSharp | 83.0 | 0.3 | 0 ±1 (160) |
| imagesharp | ImageSharp | 95.0 | 2.6 | 34 ±1 (192) |
| magick | Magick.NET | 114.5 | 1.0 | 9 ±1 (173) |

### webp-decode (Lossless=False)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImageLib | 16.9 | 0.4 | 2 ±0 (225) |
| oneimage | OneImageNative | 17.3 | 8.9 |  |
| skia | SkiaSharp | 17.9 | 0.0 | 0 ±0 (222) |
| magick | Magick.NET | 20.2 | 0.0 | 0 ±0 (219) |
| imagesharp | ImageSharp | 31.0 | 0.5 | 3 ±0 (218) |
| imagesharp | ImageSharpRgb24 | 32.4 | 0.5 |  |
| netvips | NetVips | 44.7 | 8.6 | 163 ±18 (419) |

### webp-decode (Lossless=True)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImageLib | 24.9 | 0.2 | 2 ±0 (225) |
| oneimage | OneImageNative | 25.2 | 8.7 |  |
| skia | SkiaSharp | 35.0 | 0.0 | 0 ±0 (222) |
| magick | Magick.NET | 49.0 | 0.0 | 0 ±0 (219) |
| netvips | NetVips | 67.0 | 8.6 | 163 ±18 (419) |
| imagesharp | ImageSharpRgb24 | 68.2 | 0.2 | 3 ±0 (218) |
| imagesharp | ImageSharp | 69.4 | 0.2 |  |

### webp-encode (Case=lossless-2000x1500)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp | 101.4 | 13.1 | 1 ±0 (218) |
| oneimage | OneImageLib | 146.8 | 117.8 | 24 ±0 (245) |
| netvips | NetVips | 740.0 | 5.8 | 73 ±7 (331) |
| imagesharp | ImageSharp | 1107.1 | 100.8 | 97 ±1 (317) |
| magick | Magick.NET | 1509.2 | 8.0 | 1 ±0 (233) |

### webp-encode (Case=lossy-2000x1500)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImageLib | 101.6 | 32.1 | 24 ±0 (245) |
| magick | Magick.NET | 162.3 | 1.0 | 1 ±0 (233) |
| skia | SkiaSharp | 196.5 | 1.4 | 1 ±0 (218) |
| netvips | NetVips | 198.1 | 0.4 | 73 ±7 (331) |
| imagesharp | ImageSharp | 590.5 | 210.0 | 97 ±1 (317) |

### webp-encode (Case=lossy-400x300)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImageLib | 5.4 | 1.6 | 24 ±0 (245) |
| magick | Magick.NET | 7.0 | 0.1 | 1 ±0 (233) |
| skia | SkiaSharp | 8.5 | 0.0 | 1 ±0 (218) |
| netvips | NetVips | 9.9 | 0.0 | 73 ±7 (331) |
| imagesharp | ImageSharp | 23.7 | 6.7 | 97 ±1 (317) |

### gif-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp: decode all frames | 1.0 | 0.0 | 0 ±0 (65) |
| oneimage | oneimage: decode 50 frames 500x500 | 2.1 | 0.1 | 1 ±1 (65) |
| imagesharp | ImageSharp: decode | 6.1 | 0.2 | 0 ±0 (116) |
| magick | Magick.NET: decode + coalesce | 15.1 | 0.2 | 0 ±1 (96) |

### gif-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | oneimage: re-encode 50 frames | 13.3 | 0.8 | 1 ±0 (159) |
| imagesharp | ImageSharp: re-encode | 16.8 | 0.7 | 2 ±1 (161) |
| magick | Magick.NET: re-encode | 70.3 | 8.0 | 5 ±1 (183) |

### gif-resize

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + Resize(200) + Save GIF | 42.6 | 2.3 | 4 ±0 (79) |
| imagesharp | ImageSharp Load + Resize(200) + Save GIF | 49.0 | 0.7 | 3 ±1 (133) |
| magick | Magick.NET coalesce + Resize(200) + Write GIF | 823.4 | 6.3 | 4 ±1 (101) |
| netvips | NetVips n=-1 + resize(0.4) + gifsave_buffer | 2791.5 | 1.7 | 82 ±2 (223) |

### tiff-multipage-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips | 28.1 | 85.9 | 85 ±1 (305) |
| oneimage-degree | oneimage, NOT DEFAULT: per-call maxDegree = all cores | 51.8 | 1.0 |  |
| oneimage-degree | oneimage, NOT DEFAULT: per-call maxDegree = 8 | 59.3 | 0.9 |  |
| oneimage | oneimage, pages into a reused buffer | 84.3 | 0.9 | 2 ±0 (62) |
| magick | Magick.NET | 224.1 | 0.0 | 140 ±9 (184) |
| imagesharp | ImageSharp | 586.7 | 990.5 | 55 ±0 (188) |

### tiff-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | oneimage (horizontal predictor) | 44.4 | 28.0 | 28 ±0 (87) |
| oneimage | oneimage (no predictor) | 55.3 | 44.1 |  |
| magick | Magick.NET | 68.5 | 16.0 | 24 ±1 (89) |
| netvips | NetVips | 81.8 | 15.8 | 40 ±2 (100) |
| imagesharp | ImageSharp | 134.6 | 32.1 | 32 ±1 (101) |

### bmp-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Load + CopyPixelsTo reused Rgb24 buffer | 0.4 | 0.0 | 0 ±0 (101) |
| imagesharp | ImageSharp Load<Rgb24> | 0.7 | 0.0 | 0 ±0 (109) |
| skia | SkiaSharp SKBitmap.Decode | 1.2 | 0.0 | 0 ±0 (114) |
| magick | Magick.NET | 6.0 | 0.0 | 9 ±9 (125) |

### bmp-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Save BMP | 2.8 | 8.6 | 7 ±0 (110) |
| imagesharp | ImageSharp Save BMP 24-bit | 4.9 | 23.6 | 22 ±1 (122) |
| magick | Magick.NET Write BMP3 | 7.1 | 32.2 | 31 ±1 (139) |

### upscale-2x

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | oneimage Catmull-Rom (linear) | 6.3 | 15.4 | 0 ±0 (108) |
| netvips | NetVips resize bicubic | 9.7 | 15.3 | 41 ±2 (161) |
| imagesharp | ImageSharp CatmullRom | 12.3 | 0.1 | 0 ±0 (118) |
| skia | SkiaSharp Catmull-Rom | 78.1 | 0.0 | 15 ±0 (119) |
| magick | Magick.NET Catrom | 149.7 | 0.0 | 23 ±0 (126) |

### watermark

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Composite + CopyPixelsTo | 4.9 | 0.0 | 0 ±0 (699) |
| imagesharp | ImageSharp Clone + DrawImage | 4.9 | 0.0 | 0 ±0 (771) |
| imagesharp | ImageSharp Clone + DrawImage + CopyPixelDataTo (packed RGB out) | 10.4 | 0.0 |  |
| netvips | NetVips composite2 + write_to_memory | 61.9 | 91.6 | 184 ±2 (900) |
| magick | Magick.NET Clone + Composite + packed RGB out | 91.2 | 68.7 | 204 ±1 (901) |
| skia | SkiaSharp copy + DrawBitmap | 161.8 | 0.0 | 181 ±1 (880) |

### cmyk-jpeg-to-srgb

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Load<Rgb24> (skips ICC conversion) | 148.7 | 0.4 | 1 ±0 (141) |
| oneimage | OneImage Load + ConvertColor(sRGB) + CopyPixelsTo reused Rgb24 buffer | 241.5 | 2.0 | 3 ±0 (163) |
| netvips | NetVips load + icc_transform(sRGB) | 268.4 | 68.7 | 438 ±43 (710) |
| skia | SkiaSharp SKBitmap.Decode to Rgba8888 | 562.7 | 0.0 | 90 ±1 (155) |
| magick | Magick.NET (LittleCMS) read + profile transform | 1030.1 | 0.3 | 159 ±0 (223) |

### jpeg-decode-rgba

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB, median ±spread (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Image.Load<Rgba32>, 24 MP | 81.3 | 0.0 |  |
| oneimage | OneImage Image.Load + ToArray<Rgba32>, 24 MP | 87.6 | 91.6 |  |
| skia | SkiaSharp SKBitmap.Decode (Rgba8888), 24 MP | 150.1 | 0.0 |  |

