# Release gate report

> DRY RUN on a shared, heavily loaded workstation: other builds and benchmarks were running throughout, so absolute times are noisy and pessimistic and ratios between rows carry several percent of jitter. This run finds gaps; it is not a published measurement.

## Host

- Started 2026-10-21 21:03:25 CDT, finished 2026-10-21 21:35:16 CDT
- CPU: AMD Ryzen 9 7950X3D 16-Core Processor, 32 logical CPUs, 125 GiB RAM
- OS: Ubuntu 26.04 LTS, kernel 7.0.0-38-generic
- .NET SDK 10.0.400; Runtime .NET 10.0.11; Avx2.IsSupported=True Avx512F.IsSupported=False Vector256.IsHardwareAccelerated=True Vector512.IsHardwareAccelerated=False
- AVX-512: disabled (DOTNET_EnableAVX512=0), the AVX2 gate configuration
- Load before: ` 21:03:25 up  6:21,  1 user,  load average: 7.52, 8.69, 9.51`
- Load during (sampled each minute):
  - 21:03:25 load average: 7.52, 8.69, 9.51
  - 21:04:25 load average: 7.95, 8.69, 9.46
  - 21:05:25 load average: 7.77, 8.49, 9.34
  - 21:06:25 load average: 8.81, 8.53, 9.30
  - 21:07:25 load average: 7.66, 8.24, 9.15
  - 21:08:25 load average: 6.94, 7.93, 8.98
  - 21:09:25 load average: 5.81, 7.44, 8.75
  - 21:10:25 load average: 6.01, 7.20, 8.58
  - 21:11:25 load average: 8.07, 7.52, 8.60
  - 21:12:25 load average: 7.14, 7.33, 8.47
  - 21:13:25 load average: 5.95, 6.88, 8.23
  - 21:14:25 load average: 6.29, 6.82, 8.13
  - 21:15:25 load average: 6.74, 6.91, 8.08
  - 21:16:25 load average: 6.54, 6.72, 7.93
  - 21:17:25 load average: 9.46, 7.42, 8.09
  - 21:18:25 load average: 8.91, 7.65, 8.13
  - 21:19:25 load average: 7.87, 7.60, 8.08
  - 21:20:25 load average: 5.73, 7.06, 7.86
  - 21:21:25 load average: 6.04, 6.83, 7.73
  - 21:22:25 load average: 9.21, 7.36, 7.84
  - 21:23:25 load average: 15.21, 9.06, 8.37
  - 21:24:25 load average: 8.98, 8.58, 8.26
  - 21:25:25 load average: 9.74, 8.58, 8.27
  - 21:26:25 load average: 13.26, 9.58, 8.62
  - 21:27:25 load average: 12.75, 10.48, 9.01
  - 21:28:25 load average: 8.23, 9.55, 8.78
  - 21:29:25 load average: 9.11, 9.82, 8.93
  - 21:30:25 load average: 6.09, 8.88, 8.67
  - 21:31:25 load average: 7.80, 8.87, 8.68
  - 21:32:25 load average: 6.56, 8.24, 8.47
  - 21:33:25 load average: 7.30, 8.22, 8.45
  - 21:34:25 load average: 12.75, 9.55, 8.89
- Load after: ` 21:35:16 up  6:52,  1 user,  load average: 9.69, 9.26, 8.83`
- Libraries: ImageSharp 3.1.12, SkiaSharp, NetVips, Magick.NET (Q8), through their .NET packages; this library built from the same checkout.

## Method

- Time: BenchmarkDotNet, `MemoryDiagnoser`, Throughput strategy with 2-3 warmup and 8-12 measured iterations per row (the job each benchmark class declares); Mean shown. Inputs are generated deterministically (`bench/OneImage.Benchmarks`); every task runs from and to memory.
- Peak memory: one process per task and library. After the inputs are built the process collects garbage, resets the kernel's resident-set high-water mark, runs the operation 4 times, and reports (peak − resident before) in MiB, i.e. the working memory of the operation, with the process total in brackets. Native allocations are included; they are what NetVips, SkiaSharp and Magick.NET use.
- Rows within a task pick each library's idiomatic one-call path to the same output (stated per row). Where output types differ (Rgb24 vs Rgba32) both rows exist.
- A library that cannot do a task has no row: SkiaSharp has no BMP, TIFF or animated GIF encoder and no TIFF decoder here; NetVips's native build has no BMP loader or saver.


## Gate summary

Ours = our fastest row of the task. ✗ marks a miss: slower than ImageSharp, more peak working memory than ImageSharp, more than 1.5× the fastest native wrapper (thumbnail and codec tasks), more than 2× NetVips's peak working memory (streaming tasks: thumbnails, PNG→WebP, GIF resize, CMYK). Peak columns are the growth of the process's resident set during the operation, in MiB (see Method).

| Task | Ours ms | ImageSharp ms | Ours / ImageSharp | Fastest native wrapper | Ours / fastest | Peak ours MiB | Peak ImageSharp MiB | Peak NetVips MiB | Peak ours / ImageSharp | Peak ours / NetVips |
|---|--:|--:|--:|---|--:|--:|--:|--:|--:|--:|
| thumb-jpeg | 47.8 | 50.6 | 0.94 | magick 46.4 | 1.03 | 15.3 | 15.0 | 118.3 | 1.02 | 0.13 |
| thumb-webp | 64.3 | 77.5 | 0.83 | magick 50.0 | 1.29 | 21.1 | 40.2 | 121.4 | 0.52 | 0.17 |
| png-to-webp (Content=photo) | 393.8 | 1088.8 | 0.36 | magick 167.6 | 2.35 ✗ | 183.5 | 166.4 | 160.1 | 1.10 ✗ | 1.15 |
| png-to-webp (Content=screenshot) | 369.7 | 257.1 | 1.44 ✗ | magick 177.4 | 2.08 ✗ | 183.5 | 166.4 | 160.1 | 1.10 ✗ | 1.15 |
| jpeg-decode | 87.9 | 90.3 | 0.97 | netvips 123.5 | 0.71 | 329.9 | 79.4 | 606.2 | 4.15 ✗ | 0.54 |
| jpeg-encode | 77.3 | 117.5 | 0.66 | netvips 55.3 | 1.40 | 108.7 | 27.6 | 183.2 | 3.94 ✗ | 0.59 |
| png-decode (Content=photo) | 28.6 | 24.7 | 1.16 ✗ | skia 25.0 | 1.14 | 48.0 | 5.6 | 132.7 | 8.57 ✗ | 0.36 |
| png-decode (Content=screenshot) | 6.6 | 4.9 | 1.34 ✗ | skia 4.6 | 1.42 | 48.0 | 5.6 | 132.7 | 8.57 ✗ | 0.36 |
| png-encode (Content=photo) | 786.7 | 337.5 | 2.33 ✗ | netvips 120.3 | 6.54 ✗ | 48.1 | 69.2 | 67.9 | 0.70 | 0.71 |
| png-encode (Content=screenshot) | 92.7 | 99.8 | 0.93 | netvips 42.6 | 2.18 ✗ | 48.1 | 69.2 | 67.9 | 0.70 | 0.71 |
| webp-decode (Lossless=False) | 31.9 | 33.5 | 0.95 | skia 18.4 | 1.73 ✗ | 60.7 | 8.7 | 184.1 | 6.98 ✗ | 0.33 |
| webp-decode (Lossless=True) | 94.8 | 75.4 | 1.26 ✗ | skia 38.0 | 2.50 ✗ | 60.7 | 8.7 | 184.1 | 6.98 ✗ | 0.33 |
| webp-encode (Case=lossless-2000x1500) | 474.7 | 1229.6 | 0.39 | skia 122.3 | 3.88 ✗ | 146.8 | 194.6 | 108.5 | 0.75 | 1.35 |
| webp-encode (Case=lossy-2000x1500) | 464.9 | 641.3 | 0.72 | magick 175.2 | 2.65 ✗ | 146.8 | 194.6 | 108.5 | 0.75 | 1.35 |
| webp-encode (Case=lossy-400x300) | 20.0 | 24.7 | 0.81 | magick 8.2 | 2.44 ✗ | 146.8 | 194.6 | 108.5 | 0.75 | 1.35 |
| gif-decode | 2.9 | 6.9 | 0.43 | skia 1.1 | 2.75 ✗ | 10.6 | 54.3 | - | 0.20 | - |
| gif-encode | 12.3 | 18.2 | 0.68 | magick 74.0 | 0.17 | 106.1 | 9.5 | - | 11.17 ✗ | - |
| gif-resize | 244.6 | 50.4 | 4.85 ✗ | magick 862.8 | 0.28 | 170.8 | 72.3 | 165.3 | 2.36 ✗ | 1.03 |
| tiff-multipage-decode | 212.4 | 776.7 | 0.27 | netvips 25.6 | 8.31 ✗ | 319.9 | 157.5 | 265.9 | 2.03 ✗ | 1.20 |
| tiff-encode | 47.9 | 154.3 | 0.31 | netvips 90.3 | 0.53 | 104.9 | 79.6 | 79.2 | 1.32 ✗ | 1.32 |
| bmp-decode | 5.2 | 0.9 | 6.03 ✗ | skia 1.8 | 2.89 ✗ | 74.0 | 9.2 | - | 8.04 ✗ | - |
| bmp-encode | 9.9 | 4.8 | 2.07 ✗ | magick 9.9 | 1.00 | 76.3 | 38.1 | - | 2.00 ✗ | - |
| upscale-2x | 11.4 | 13.4 | 0.85 | netvips 20.0 | 0.57 | 65.5 | 14.8 | 105.1 | 4.43 ✗ | 0.62 |
| watermark | 4.8 | 6.1 | 0.79 | netvips 87.3 | 0.06 | 2.2 | 74.5 | 479.1 | 0.03 | 0.00 |
| cmyk-jpeg-to-srgb | 296.7 | 161.2 | 1.84 ✗ | netvips 302.1 | 0.98 | 373.0 | 78.9 | 745.3 | 4.73 ✗ | 0.50 |
| jpeg-decode-rgba | 96.8 | 89.3 | 1.08 ✗ | skia 177.4 | 0.55 | - | - | - | - | - |

## Gaps

- **png-to-webp**: more peak working memory than ImageSharp, 184 MiB vs 166 MiB (1.10×).
- **png-to-webp (Content=photo)**: 2.35× the fastest native wrapper (magick 167.6 ms vs ours 393.8 ms).
- **png-to-webp (Content=screenshot)**: slower than ImageSharp (beyond a 5% tie), 369.7 ms vs 257.1 ms (1.44×).
- **png-to-webp (Content=screenshot)**: 2.08× the fastest native wrapper (magick 177.4 ms vs ours 369.7 ms).
- **jpeg-decode**: more peak working memory than ImageSharp, 330 MiB vs 79 MiB (4.15×).
- **jpeg-encode**: more peak working memory than ImageSharp, 109 MiB vs 28 MiB (3.94×).
- **png-decode (Content=photo)**: slower than ImageSharp (beyond a 5% tie), 28.6 ms vs 24.7 ms (1.16×).
- **png-decode**: more peak working memory than ImageSharp, 48 MiB vs 6 MiB (8.57×).
- **png-decode (Content=screenshot)**: slower than ImageSharp (beyond a 5% tie), 6.6 ms vs 4.9 ms (1.34×).
- **png-encode (Content=photo)**: slower than ImageSharp (beyond a 5% tie), 786.7 ms vs 337.5 ms (2.33×).
- **png-encode (Content=photo)**: 6.54× the fastest native wrapper (netvips 120.3 ms vs ours 786.7 ms).
- **png-encode (Content=screenshot)**: 2.18× the fastest native wrapper (netvips 42.6 ms vs ours 92.7 ms).
- **webp-decode**: more peak working memory than ImageSharp, 61 MiB vs 9 MiB (6.98×).
- **webp-decode (Lossless=False)**: 1.73× the fastest native wrapper (skia 18.4 ms vs ours 31.9 ms).
- **webp-decode (Lossless=True)**: slower than ImageSharp (beyond a 5% tie), 94.8 ms vs 75.4 ms (1.26×).
- **webp-decode (Lossless=True)**: 2.50× the fastest native wrapper (skia 38.0 ms vs ours 94.8 ms).
- **webp-encode (Case=lossless-2000x1500)**: 3.88× the fastest native wrapper (skia 122.3 ms vs ours 474.7 ms).
- **webp-encode (Case=lossy-2000x1500)**: 2.65× the fastest native wrapper (magick 175.2 ms vs ours 464.9 ms).
- **webp-encode (Case=lossy-400x300)**: 2.44× the fastest native wrapper (magick 8.2 ms vs ours 20.0 ms).
- **gif-decode**: 2.75× the fastest native wrapper (skia 1.1 ms vs ours 2.9 ms).
- **gif-encode**: more peak working memory than ImageSharp, 106 MiB vs 10 MiB (11.17×).
- **gif-resize**: slower than ImageSharp (beyond a 5% tie), 244.6 ms vs 50.4 ms (4.85×).
- **gif-resize**: more peak working memory than ImageSharp, 171 MiB vs 72 MiB (2.36×).
- **tiff-multipage-decode**: more peak working memory than ImageSharp, 320 MiB vs 158 MiB (2.03×).
- **tiff-multipage-decode**: 8.31× the fastest native wrapper (netvips 25.6 ms vs ours 212.4 ms).
- **tiff-encode**: more peak working memory than ImageSharp, 105 MiB vs 80 MiB (1.32×).
- **bmp-decode**: slower than ImageSharp (beyond a 5% tie), 5.2 ms vs 0.9 ms (6.03×).
- **bmp-decode**: more peak working memory than ImageSharp, 74 MiB vs 9 MiB (8.04×).
- **bmp-decode**: 2.89× the fastest native wrapper (skia 1.8 ms vs ours 5.2 ms).
- **bmp-encode**: slower than ImageSharp (beyond a 5% tie), 9.9 ms vs 4.8 ms (2.07×).
- **bmp-encode**: more peak working memory than ImageSharp, 76 MiB vs 38 MiB (2.00×).
- **upscale-2x**: more peak working memory than ImageSharp, 66 MiB vs 15 MiB (4.43×).
- **cmyk-jpeg-to-srgb**: slower than ImageSharp (beyond a 5% tie), 296.7 ms vs 161.2 ms (1.84×).
- **cmyk-jpeg-to-srgb**: more peak working memory than ImageSharp, 373 MiB vs 79 MiB (4.73×).
- **jpeg-decode-rgba**: slower than ImageSharp (beyond a 5% tie), 96.8 ms vs 89.3 ms (1.08×).

## Rows

### thumb-jpeg

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET jpeg:size + Thumbnail + JPEG q90 | 46.4 | 0.1 | 7 (207) |
| skia | SkiaSharp scaled decode + resize + encode | 47.3 | 0.0 | 6 (205) |
| oneimage | OneImage Image.Thumbnail(bytes, 400) -> JPEG q90 | 47.8 | 0.2 | 15 (215) |
| imagesharp | ImageSharp load(TargetSize) + Resize + JPEG q90 | 50.6 | 0.1 | 15 (215) |
| netvips | NetVips thumbnail_buffer + jpegsave_buffer q90 | 53.4 | 0.0 | 118 (318) |

### thumb-webp

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET jpeg:size + Thumbnail + WebP q75 | 50.0 | 0.0 | 8 (208) |
| skia | SkiaSharp scaled decode + resize + WebP q75 | 53.1 | 0.0 | 6 (206) |
| netvips | NetVips thumbnail_buffer + webpsave_buffer q75 | 59.1 | 0.0 | 121 (321) |
| oneimage | OneImage Image.Thumbnail(bytes, 400) -> WebP q75 | 64.3 | 1.5 | 21 (221) |
| imagesharp | ImageSharp load(TargetSize) + Resize + WebP q75 | 77.5 | 6.0 | 40 (240) |

### png-to-webp (Content=photo)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET read + WebP q75 | 167.6 | 0.3 | 28 (103) |
| netvips | NetVips load + webpsave q75 | 181.4 | 0.1 | 160 (235) |
| skia | SkiaSharp decode + WebP q75 | 181.8 | 0.0 | 74 (149) |
| oneimage | OneImage Load + Save WebP q75 | 393.8 | 48.3 | 184 (259) |
| imagesharp | ImageSharp load + WebP q75 | 1088.8 | 35.8 | 166 (242) |

### png-to-webp (Content=screenshot)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET read + WebP q75 | 177.4 | 1.0 | 28 (103) |
| netvips | NetVips load + webpsave q75 | 191.1 | 0.4 | 160 (235) |
| skia | SkiaSharp decode + WebP q75 | 193.7 | 0.0 | 74 (149) |
| imagesharp | ImageSharp load + WebP q75 | 257.1 | 29.6 | 166 (242) |
| oneimage | OneImage Load + Save WebP q75 | 369.7 | 51.9 | 184 (259) |

### jpeg-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Image.Load + ToArray<Rgb24>, 24 MP | 87.9 | 75.5 | 330 (530) |
| imagesharp | ImageSharp Image.Load<Rgb24>, 24 MP | 90.3 | 0.0 | 79 (280) |
| netvips | NetVips new_from_buffer + write_to_memory, 24 MP | 123.5 | 68.7 | 606 (806) |
| magick | Magick.NET read, 24 MP | 129.2 | 0.0 | 66 (266) |
| skia | SkiaSharp SKBitmap.Decode, 24 MP | 158.4 | 0.0 | 88 (288) |

### jpeg-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips q90 4:2:0 | 55.3 | 5.5 | 183 (713) |
| oneimage | OneImage q90 4:2:0 optimized tables (default) | 77.3 | 8.1 | 109 (639) |
| imagesharp | ImageSharp q90 4:2:0 | 117.5 | 8.0 | 28 (558) |
| magick | Magick.NET q90 4:2:0 | 180.0 | 16.0 | 123 (653) |
| skia | SkiaSharp q90 4:2:0 | 314.8 | 8.0 | 179 (709) |

### png-decode (Content=photo)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp | 24.7 | 0.1 | 6 (106) |
| skia | SkiaSharp | 25.0 | 0.0 | 1 (101) |
| oneimage | OneImage | 28.6 | 12.0 | 48 (148) |
| netvips | NetVips | 31.7 | 8.6 | 133 (233) |
| magick | Magick.NET | 37.5 | 0.0 | 1 (101) |

### png-decode (Content=screenshot)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp | 4.6 | 0.0 | 1 (101) |
| imagesharp | ImageSharp | 4.9 | 0.0 | 6 (106) |
| oneimage | OneImage | 6.6 | 12.0 | 48 (148) |
| netvips | NetVips | 12.2 | 8.6 | 133 (233) |
| magick | Magick.NET | 14.4 | 0.0 | 1 (101) |

### png-encode (Content=photo)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips | 120.3 | 7.8 | 68 (216) |
| skia | SkiaSharp | 217.4 | 4.1 | 22 (178) |
| imagesharp | ImageSharp | 337.5 | 40.9 | 69 (223) |
| magick | Magick.NET | 750.9 | 16.2 | 45 (192) |
| oneimage | OneImage | 786.7 | 19.2 | 48 (201) |

### png-encode (Content=screenshot)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips | 42.6 | 0.3 | 68 (216) |
| skia | SkiaSharp | 88.7 | 0.3 | 22 (178) |
| oneimage | OneImage | 92.7 | 1.4 | 48 (201) |
| imagesharp | ImageSharp | 99.8 | 2.6 | 69 (223) |
| magick | Magick.NET | 122.0 | 1.0 | 45 (192) |

### webp-decode (Lossless=False)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp | 18.4 | 0.0 | 12 (222) |
| magick | Magick.NET | 22.1 | 0.0 | 9 (219) |
| oneimage | OneImageLib | 31.9 | 12.4 | 61 (270) |
| oneimage | OneImageNative | 32.6 | 21.0 |  |
| imagesharp | ImageSharp | 33.5 | 0.5 | 9 (218) |
| imagesharp | ImageSharpRgb24 | 37.0 | 0.5 |  |
| netvips | NetVips | 46.5 | 8.6 | 184 (394) |

### webp-decode (Lossless=True)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp | 38.0 | 0.0 | 12 (222) |
| magick | Magick.NET | 56.2 | 0.0 | 9 (219) |
| imagesharp | ImageSharpRgb24 | 75.4 | 0.2 | 9 (218) |
| netvips | NetVips | 80.7 | 8.6 | 184 (394) |
| oneimage | OneImageLib | 94.8 | 12.2 | 61 (270) |
| oneimage | OneImageNative | 101.8 | 20.8 |  |
| imagesharp | ImageSharp | 102.2 | 0.2 |  |

### webp-encode (Case=lossless-2000x1500)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp | 122.3 | 13.1 | 1 (218) |
| oneimage | OneImageLib | 474.7 | 177.1 | 147 (364) |
| netvips | NetVips | 773.2 | 5.8 | 108 (326) |
| imagesharp | ImageSharp | 1229.6 | 100.8 | 195 (412) |
| magick | Magick.NET | 1672.5 | 8.0 | 16 (235) |

### webp-encode (Case=lossy-2000x1500)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET | 175.2 | 1.0 | 16 (235) |
| skia | SkiaSharp | 206.9 | 1.4 | 1 (218) |
| netvips | NetVips | 212.7 | 0.4 | 108 (326) |
| oneimage | OneImageLib | 464.9 | 38.0 | 147 (364) |
| imagesharp | ImageSharp | 641.3 | 210.0 | 195 (412) |

### webp-encode (Case=lossy-400x300)

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| magick | Magick.NET | 8.2 | 0.1 | 16 (235) |
| skia | SkiaSharp | 8.9 | 0.0 | 1 (218) |
| netvips | NetVips | 9.3 | 0.0 | 108 (326) |
| oneimage | OneImageLib | 20.0 | 1.7 | 147 (364) |
| imagesharp | ImageSharp | 24.7 | 6.7 | 195 (412) |

### gif-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| skia | SkiaSharp: decode all frames | 1.1 | 0.0 | 4 (64) |
| oneimage | oneimage: decode 50 frames 500x500 | 2.9 | 1.5 | 11 (72) |
| imagesharp | ImageSharp: decode | 6.9 | 0.2 | 54 (115) |
| magick | Magick.NET: decode + coalesce | 16.4 | 0.2 | 35 (96) |

### gif-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| oneimage | oneimage: re-encode 50 frames | 12.3 | 8.7 | 106 (259) |
| imagesharp | ImageSharp: re-encode | 18.2 | 0.7 | 10 (162) |
| magick | Magick.NET: re-encode | 74.0 | 8.0 | 62 (214) |

### gif-resize

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Load + Resize(200) + Save GIF | 50.4 | 0.7 | 72 (133) |
| oneimage | OneImage Load + Resize(200) + Save GIF | 244.6 | 194.1 | 171 (232) |
| magick | Magick.NET coalesce + Resize(200) + Write GIF | 862.8 | 6.3 | 43 (104) |
| netvips | NetVips n=-1 + resize(0.4) + gifsave_buffer | 3029.8 | 1.7 | 165 (226) |

### tiff-multipage-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| netvips | NetVips | 25.6 | 85.9 | 266 (296) |
| oneimage | oneimage | 212.4 | 175.4 | 320 (350) |
| magick | Magick.NET | 278.7 | 0.0 | 152 (182) |
| imagesharp | ImageSharp | 776.7 | 990.5 | 158 (187) |

### tiff-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| oneimage | oneimage (horizontal predictor) | 47.9 | 29.8 | 105 (152) |
| oneimage | oneimage (no predictor) | 61.1 | 45.9 |  |
| netvips | NetVips | 90.3 | 15.8 | 79 (126) |
| magick | Magick.NET | 94.0 | 16.0 | 84 (132) |
| imagesharp | ImageSharp | 154.3 | 32.1 | 80 (127) |

### bmp-decode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Load<Rgb24> | 0.9 | 0.0 | 9 (100) |
| skia | SkiaSharp SKBitmap.Decode | 1.8 | 0.0 | 14 (105) |
| oneimage | OneImage Load + ToArray<Rgb24> | 5.2 | 20.6 | 74 (165) |
| magick | Magick.NET | 7.1 | 0.0 | 26 (117) |

### bmp-encode

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Save BMP 24-bit | 4.8 | 23.6 | 38 (129) |
| magick | Magick.NET Write BMP3 | 9.9 | 32.2 | 84 (174) |
| oneimage | OneImage Save BMP | 9.9 | 30.9 | 76 (167) |

### upscale-2x

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| oneimage | oneimage Catmull-Rom (linear) | 11.4 | 15.4 | 66 (168) |
| imagesharp | ImageSharp CatmullRom | 13.4 | 0.1 | 15 (118) |
| netvips | NetVips resize bicubic | 20.0 | 15.3 | 105 (208) |
| skia | SkiaSharp Catmull-Rom | 84.2 | 0.0 | 16 (119) |
| magick | Magick.NET Catrom | 166.1 | 0.0 | 23 (126) |

### watermark

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| oneimage | OneImage Composite + CopyPixelsTo | 4.8 | 0.0 | 2 (699) |
| imagesharp | ImageSharp Clone + DrawImage | 6.1 | 0.0 | 74 (771) |
| imagesharp | ImageSharp Clone + DrawImage + CopyPixelDataTo (packed RGB out) | 12.2 | 0.0 |  |
| netvips | NetVips composite2 + write_to_memory | 87.3 | 91.6 | 479 (1176) |
| magick | Magick.NET Clone + Composite + packed RGB out | 100.2 | 68.7 | 410 (1107) |
| skia | SkiaSharp copy + DrawBitmap | 176.8 | 0.0 | 455 (1151) |

### cmyk-jpeg-to-srgb

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Load<Rgb24> (no ICC conversion) | 161.2 | 0.4 | 79 (141) |
| oneimage | OneImage Load + ConvertColor(sRGB) + ToArray<Rgb24> | 296.7 | 76.3 | 373 (435) |
| netvips | NetVips load + icc_transform(sRGB) | 302.1 | 68.7 | 745 (807) |
| skia | SkiaSharp SKBitmap.Decode to Rgba8888 | 603.9 | 0.0 | 92 (154) |
| magick | Magick.NET (LittleCMS) read + profile transform | 1153.2 | 0.3 | 160 (222) |

### jpeg-decode-rgba

| Library | Row | Mean ms | Managed alloc MiB | Peak working MiB (process total) |
|---|---|--:|--:|--:|
| imagesharp | ImageSharp Image.Load<Rgba32>, 24 MP | 89.3 | 0.0 |  |
| oneimage | OneImage Image.Load + ToArray<Rgba32>, 24 MP | 96.8 | 98.4 |  |
| skia | SkiaSharp SKBitmap.Decode (Rgba8888), 24 MP | 177.4 | 0.0 |  |

