# Performance

OneImage is faster than ImageSharp 3.1.12 on every task in the benchmark
set except one that is a tie, and one where ImageSharp does less work
(CMYK, below). It is not faster than every native wrapper, and on peak
memory it has one exception and several ties. Drawing and clipping
against ImageSharp.Drawing have one exception of their own
([below](#drawing-and-clipping-exceptions)), and flattening a layered
document at the default thread limit has another
([below](#layer-flatten-exception)). This page prints all of
it: the table, the ties, the losses and the method.

Every number here comes from the reports in
[`bench/results/`](https://github.com/recursive-ai-oss/oneimage-csharp/tree/main/bench/results),
measured on a shared workstation (see [Method](#method)). Treat them as
local measurements, not as promises for your hardware.

## Results at a glance

Time is the BenchmarkDotNet mean for one operation, in milliseconds.
Peak is the growth of the process's resident set during the operation,
in MiB, as the median of three runs with the run-to-run spread (max −
min) in brackets. A time within 5% of ImageSharp's is a tie. Native
wrappers are SkiaSharp (`skia`), NetVips (`netvips`) and Magick.NET
(`magick`) through their .NET packages; the column shows the fastest of
the three.

One OneImage row per task is shown: its fastest idiomatic one-call path
to the stated output, writing into a caller buffer where the competitors
reuse pooled storage. Every row of every library is in the raw reports.

![Bar chart: how many times faster OneImage is than ImageSharp on each task, log scale, higher is better; ties and slower tasks are marked.](https://raw.githubusercontent.com/recursive-ai-oss/oneimage-csharp/main/docs/img/oneimage-hero-light.svg)

The chart is drawn from the same table by
[`tools/make-hero-chart.py`](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/tools/make-hero-chart.py).
Ratios there come from the unrounded report values, so they can differ
in the last digit from a division of the rounded milliseconds below.

| Task | OneImage ms | ImageSharp ms | Time vs ImageSharp | Fastest native wrapper | OneImage time ÷ fastest native | Peak MiB: OneImage | Peak: ImageSharp | Peak: NetVips | Peak vs ImageSharp |
|---|--:|--:|---|---|--:|--:|--:|--:|---|
| thumb-jpeg | 37.5 | 47.0 | 1.25× faster | magick 40.3 | 0.93× | 2.2 (±0.2) | 1.2 (±0.1) | 64.4 | **higher** (exception) |
| thumb-webp | 46.9 | 65.4 | 1.39× faster | magick 46.0 | 1.02× | 3.0 (±0.1) | 9.5 (±0.1) | 64.2 | lower |
| png-to-webp (natural) | 79.1 | 1511.5 | 19.11× faster | netvips 155.1 | 0.51× | 41.9 (±4.0) | 86.8 (±1.4) | 121.6 | lower |
| png-to-webp (photo) | 90.5 | 1025.2 | 11.33× faster | magick 157.6 | 0.57× | 41.9 (±4.0) | 86.8 (±1.4) | 121.6 | lower |
| png-to-webp (screenshot) | 95.8 | 236.5 | 2.47× faster | magick 162.3 | 0.59× | 41.9 (±4.0) | 86.8 (±1.4) | 121.6 | lower |
| jpeg-decode | 41.2 | 77.2 | 1.87× faster | netvips 102.3 | 0.40× | 0.5 (±0.0) | 3.5 (±0.1) | 360.6 | lower |
| jpeg-encode | 87.4 | 100.6 | 1.15× faster | netvips 55.3 | 1.58× | 5.3 (±3.2) | 4.1 (±0.8) | 67.3 | tie (within the run-to-run spread) |
| png-decode (natural) | 11.4 | 39.1 | 3.43× faster | skia 19.6 | 0.58× | 0.2 (±0.1) | 0.3 (±0.0) | 90.3 | lower |
| png-decode (photo) | 18.3 | 22.2 | 1.21× faster | skia 21.9 | 0.83× | 0.2 (±0.1) | 0.3 (±0.0) | 90.3 | lower |
| png-decode (screenshot) | 2.9 | 4.2 | 1.45× faster | skia 4.2 | 0.71× | 0.2 (±0.1) | 0.3 (±0.0) | 90.3 | lower |
| png-encode (natural) | 219.6 | 1064.3 | 4.85× faster | netvips 96.0 | 2.29× | 11.2 (±1.1) | 33.6 (±0.6) | 24.1 | lower |
| png-encode (photo) | 52.3 | 318.6 | 6.09× faster | netvips 96.7 | 0.54× | 11.2 (±1.1) | 33.6 (±0.6) | 24.1 | lower |
| png-encode (screenshot) | 50.8 | 95.0 | 1.87× faster | netvips 39.8 | 1.27× | 11.2 (±1.1) | 33.6 (±0.6) | 24.1 | lower |
| webp-decode (lossy) | 16.9 | 31.0 | 1.83× faster | skia 17.9 | 0.94× | 2.3 (±0.3) | 2.9 (±0.1) | 162.8 | lower |
| webp-decode (lossless) | 24.9 | 68.2 | 2.74× faster | skia 35.0 | 0.71× | 2.3 (±0.3) | 2.9 (±0.1) | 162.8 | lower |
| webp-encode (lossless-2000x1500) | 146.8 | 1107.1 | 7.54× faster | skia 101.4 | 1.45× | 24.2 (±0.5) | 97.1 (±0.7) | 73.1 | lower |
| webp-encode (lossy-2000x1500) | 101.6 | 590.5 | 5.81× faster | magick 162.3 | 0.63× | 24.2 (±0.5) | 97.1 (±0.7) | 73.1 | lower |
| webp-encode (lossy-400x300) | 5.4 | 23.7 | 4.39× faster | magick 7.0 | 0.76× | 24.2 (±0.5) | 97.1 (±0.7) | 73.1 | lower |
| gif-decode | 2.3 | 6.1 | 2.65× faster | skia 1.0 | 2.23× | 0.3 (±0.0) | 0.4 (±0.0) | - | lower |
| gif-encode | 13.3 | 16.8 | 1.26× faster | magick 70.3 | 0.19× | 1.2 (±0.1) | 2.0 (±1.1) | - | lower |
| gif-resize | 40.4 | 47.1 | 1.17× faster | magick 802.4 | 0.05× | 2.4 (±0.3) | 2.6 (±0.0) | 81.6 | lower |
| tiff-multipage-decode | 84.3 | 586.7 | 6.96× faster | netvips 28.1 | 3.00× | 2.5 (±0.2) | 54.7 (±0.1) | 85.1 | lower |
| tiff-encode | 44.4 | 134.6 | 3.03× faster | magick 68.5 | 0.65× | 28.5 (±0.3) | 32.1 (±0.6) | 40.3 | lower |
| bmp-decode | 0.4 | 0.7 | 1.75× faster | skia 1.2 | 0.32× | 0.1 (±0.0) | 0.1 (±0.0) | - | tie |
| bmp-encode | 2.8 | 4.9 | 1.75× faster | magick 7.1 | 0.39× | 7.0 (±0.4) | 21.6 (±0.8) | - | lower |
| upscale-2x | 6.3 | 12.3 | 1.95× faster | netvips 9.7 | 0.64× | 0.5 (±0.1) | 0.5 (±0.1) | 40.7 | tie |
| watermark | 4.9 | 4.9 | tie | netvips 61.9 | 0.08× | 0.3 (±0.0) | 0.5 (±0.1) | 183.8 | lower |
| cmyk-jpeg-to-srgb | 243.4 | 148.3 | 1.64× slower (ImageSharp skips the ICC conversion) | netvips 252.0 | 0.97× | 2.2 (±0.4) | 1.3 (±0.1) | 441.1 | higher (ImageSharp skips the ICC conversion) |
| jpeg-decode-rgba | 43.4 | 79.4 | 1.83× faster | skia 146.4 | 0.30× | 0.5 (±0.0) | 0.7 (±0.1) | - | lower |

### Reading the table

- **Ties are ties.** bmp-decode and upscale-2x are ties on peak memory
  (0.1 vs 0.1 MiB and 0.5 vs 0.5 MiB, both within their spreads).
  watermark is a tie on time (4.9 ms against 4.9 ms). Neither is a win.
- **jpeg-encode peak is a tie within its spread.** OneImage 5.3 MiB
  (spread ±3.2) against ImageSharp 4.1 MiB (spread ±0.8): the 1.2 MiB
  difference is smaller than OneImage's own run-to-run spread. The
  gate report's stricter 5% rule lists it as a miss; we do not call it a
  win, and we do not claim it is lower. Time is a win, 87.4 against
  100.6 ms.
- **The one exception: thumb-jpeg uses 1.0 MiB more peak working memory
  than ImageSharp, 2.2 vs 1.2 MiB.** The time is a win. The gap is about
  1 MiB; five repeats before the last changes gave the same 1.5–2.5 MiB
  range, so it is not new. Both are tiny next to NetVips's 64 MiB for the
  same task.
- **CMYK JPEG to sRGB is slower than ImageSharp, and that comparison is
  not like for like.** ImageSharp does not apply the embedded ICC
  profile; OneImage, NetVips and Magick.NET do. The fair bar is the
  libraries that convert: OneImage (243.4 ms, 2.2 MiB peak) is faster
  than NetVips (252.0 ms, 441.1 MiB) and Magick.NET (1015.9 ms,
  159 MiB). The colour error shows what skipping the conversion costs:
  mean CIEDE2000 against an ICC-correct reference is 0.190 for
  OneImage, 0.885 for NetVips, 0.889 for Magick.NET, 0.909 for
  SkiaSharp and 11.329 for ImageSharp (max 27.193).

  OneImage's number is lower than the others' because it applies black
  point compensation by default, as Photoshop does; the others do not
  unless asked. Against the LittleCMS result (Magick.NET) it sits 0.935
  mean / 2.764 max ΔE00 away by default and 0.062 / 1.443 with
  `blackPointCompensation: false`.
- **Peak memory** is the table's last column. OneImage is lower than
  ImageSharp on every task except the ones named above (ties,
  jpeg-encode, thumb-jpeg and the CMYK row), and far below NetVips's on
  every streaming task.

## Where OneImage is slower than a native wrapper

The gate asks for thumbnail and codec tasks within 1.5× of the fastest
native wrapper. These are the rows that are not:

| Task | OneImage | Fastest | Ratio |
|---|--:|---|--:|
| tiff-multipage-decode, default degree | 84.3 ms | netvips 28.1 ms | 3.00× |
| png-encode (natural) | 219.6 ms | netvips 96.0 ms | 2.29× |
| gif-decode | 2.3 ms | skia 1.0 ms | 2.23× |
| jpeg-encode | 87.4 ms | netvips 55.3 ms | 1.58× |

Notes:

- **TIFF multi-page.** OneImage limits a single call to min(4, cores)
  threads by default, so a server does not oversubscribe its cores;
  NetVips uses every core. With the per-call degree raised the same read
  takes 59.3 ms at 8 threads and 51.8 ms on all cores (2.11× and 1.84×
  NetVips). The raised rows are shown for information; the default row is
  the one that counts. The floor is LZW decoding in safe code.
- **Resize.** The thumbnail rows are where resize cost shows: thumb-jpeg
  is 37.5 ms against NetVips's 49.5 ms and thumb-webp 46.9 against 55.1,
  and 2× upscale is 6.3 ms against 9.7 ms. Those rows are decode-bound (the
  resize runs on a 1/8-size decode). A downscale of an already decoded
  24 MP RGBA image is where NetVips's dedicated resize is ahead. Measured on
  the worker at the default degree of 4 (AVX2, minimum of 40 runs, load 4-7;
  base and new alternated three times): 24 MP to 400 px went from 11.5 to
  8.8 ms, a 3× reduction (6000 to 2000) from 30.0 to 25.3 ms and a 2×
  upscale from 5.35 to 4.85 ms. NetVips measured 6.2-7.4 ms and 17.5-18.1 ms
  on the first two with every core, so the gap that remains is 1.2-1.5×;
  it comes from the per-call degree, not from CPU time (a local count shows
  NetVips using about as much CPU as we do on the first row and 2.5× as much
  on the second). The `resize` benchmark category
  (`--anyCategories resize`) runs these rows.
- **png-encode.** Only the natural-content input is beyond 1.5×; the
  photo and screenshot inputs take 0.54× and 1.27× of NetVips's time.
- **gif-decode** is 1.3 ms behind SkiaSharp on a 50-frame 500×500 file.
- **jpeg-encode** uses optimized Huffman tables by default (smaller
  files), which costs extra work. The gap to NetVips shrank from
  2.72× to 1.58× after the last fixes and is a goal, not a gate.

## JPEG entropy decoding

The thumbnail row is bound by Huffman decoding, which cannot be spread
over cores without restart markers. The decoder resolves most AC symbols
(code and magnitude bits) with one lookup in a 12-bit table, refills its
bit buffer once per symbol without branching on the fill level, and skips
the AC data of any component that only contributes its DC coefficient at
the requested scale (at 1/8 of a 4:2:0 image that is the luma, two thirds
of the blocks). Output is byte-identical to the earlier decoder.

Same code, same worker (Ryzen 9 7950X3D, 32 logical CPUs, .NET 10.0.402,
AVX-512 off, shared with other jobs: load ranged from 1.4 to 12 over
the four runs, so read the differences, not the digits).
Gate harness rows, mean ms:

| Task | Before | After | ImageSharp (same run) | Peak MiB before → after (ImageSharp) |
|---|--:|--:|--:|--:|
| thumb-jpeg | 38.5 / 40.0 | 28.2 / 27.3 | 47.4-49.5 | 2.3 → 2.4 (1.3) |
| jpeg-decode | 56.6* / 44.8 | 58.7* / 42.2 | 80.1-119.5 | 0.5 → 0.7 (3.6) |
| jpeg-decode-rgba | 44.9 / 47.0 | 98.2* / 44.5 | 86.7-112.4 | 0.5 → 0.6 (0.7) |
| cmyk-jpeg-to-srgb | 235.8 / 275.8 | 243.9 / 303.0 | 149.7-200.0 (skips ICC) | 2.0 → 2.1 (1.3) |
| tiff-multipage-decode | 138.8* / 73.6 | 71.5 / 87.3 | 591.6-637.9 | 1.8 → 2.0 (54.8) |

Each cell shows two runs; * marks a run during a load spike that moved
ImageSharp's row by the same factor. The rows other than thumb-jpeg do
not change beyond that spread: full-size 4:2:0 decoding was bound by the
transform thread (about 38 ms for 24 MP), not by Huffman decoding (see
the next section). The
thumb-jpeg peak stays the published exception (about 1 MiB over
ImageSharp). Decoder only on a desktop host (one core for entropy decoding plus
the transform thread, best of 30, a 6000x4000 4:2:0 file of 5.8 MB): 1/8 scale 38.6 to 28.3 ms, and the
scan decoder is the whole of that time in both (37.9 and 28.4 ms inside
the decoder); 24 MP CMYK file of 16.5 MB, full size 111 to 90 ms.
Progressive full-size decoding stays dominated by refinement scans
(about 205 ms for a noisy 24 MP file against 218 before).

## JPEG full-size transform split

Full-size decoding of a sequential 4:2:0 file was bound by one thread
doing inverse DCT, chroma upsampling, color conversion and sink writes
(about 38 ms of 24 MP) while entropy decoding took 31. The transform is
now two stages, each on its own thread beside the entropy decoder, with
four plane-ring slots so a row is transformed while its predecessor is
emitted. Per-call thread use rises from two to three (one more with
three or more cores); on two cores the entropy thread does the inverse
DCT. Output is byte-identical at every stage count, scale, region and
hardware level (SHA-256 over the test corpus plus generated 4:2:0, 4:2:2,
4:4:0, 4:4:4 and progressive files at 1, 1/2, 1/4, 1/8, with and without
hardware intrinsics).

Desktop host, 6000x4000 file, best of 20 into a null sink, AVX2 only:

| File | One thread | Entropy + transform+emit | Three threads |
|---|--:|--:|--:|
| 4:2:0 q90, 8.9 MB | 78 ms | 64 ms | 43 ms (was 43) |
| 4:4:4 q90, 9.4 MB | 94 ms | 82 ms | 55 ms (was 53) |
| 4:2:0 q90, 2.1 MB (smooth) | 50 ms | 35 ms | 20 ms (was 34) |
| 4:4:4, 2.1 MB (smooth) | 62 ms | 55 ms | 31 ms (was 39) |

Noisy photographs are entropy-bound (43 ms is the Huffman time), so the
split helps where the transform was the longer stage: images with fewer
bits per pixel, which includes most web photographs. Progressive files
are bound by their coefficient scans (110-280 ms) and move by under 5%.

Worker rows (Ryzen 9 7950X3D, 32 logical CPUs, .NET 10.0.402 SDK with
runtime 10.0.13, AVX-512 off, shared: load 1.2 to 5), mean ms, two paired
runs of before / after, ImageSharp alongside:

| Task | Before | After | ImageSharp |
|---|--:|--:|--:|
| jpeg-decode | 55.2 / 43.8 | 31.8 / 31.7 | 81.7-97.1 |
| jpeg-decode-rgba | 55.8 / 44.9 | 31.3 / 33.2 | 86.9-97.8 |
| thumb-jpeg | 34.8 / 27.7 | 27.6 / 29.3 | 47.8-50.1 |
| cmyk-jpeg-to-srgb | 419* / 257 / 237 / 274 | 262* / 291 / 252 / 255 | 150-157 (skips ICC) |
| tiff-multipage-decode | 93.8 / 60.2 | 78 / 118 / 61.5 / 60.1 | 599-735 |

* a run under load above 6 that moved ImageSharp by the same factor.
The thumb-jpeg, CMYK and TIFF rows do not use the split path or do not
change beyond their spread; peaks are unchanged (jpeg-decode 1 MiB,
cmyk 2 MiB, thumb-jpeg 2 MiB).

## Drawing and clipping exceptions

The drawing operations are compared with ImageSharp.Drawing 2.1.7 in
`bench/OneImage.Benchmarks/Drawing` (BenchmarkDotNet with the memory
diagnoser, 2000 x 2000 RGBA, .NET 10.0.11, X64 AVX-512). These were
local smoke runs on a shared workstation with a load of 4.7 at the
start, rising to about 10, so treat them as rough. Fills, strokes and
brushes are faster with less allocation on every row; see
[drawing.md](drawing.md). Clipping has one exception:

| Row | OneImage | ImageSharp.Drawing | Allocated (OneImage vs ImageSharp) | |
|---|--:|--:|---|---|
| union of 200 circles | 7.2 ms | 11.1 ms | 3.2 MB vs 23 MB | faster, less |
| star minus a 1000-point polygon | 158 µs | 134 µs | 33 KB vs 205 KB | **slower** (exception) |
| blur clipped to a circle, lazy `Image` | 12.3 ms | 56 ms | 52 KB vs 423 KB | faster, less |
| blur clipped to a circle, in place on a `Canvas` | 12.9 ms | 56 ms | 88 KB vs 423 KB | faster, less |

- **Star minus a 1000-point polygon is 1.18× slower.** The time is
  split evenly between finding crossings and the sweep. The boolean
  engine works in exact integer arithmetic on a 1/256-pixel grid, and
  for an input this small the candidate-pair search costs more than it
  saves. It allocates a sixth as much.
- **`Canvas.Clip` allocates 88 KB** (the `Image` path 52 KB). It used to
  allocate 9.6 MB: it read the canvas through a public snapshot, so every
  rewritten tile was copied and the replaced tiles, together with the
  tiles the clipped graph cached, went to the garbage collector. It now
  reads a private copy of the tile table, swaps fresh pooled tiles in and
  returns the replaced and cached tiles to the pool afterwards. Time is
  unchanged (12.9 ms against 13.4 ms before).

## Layer flatten exception

Flattening a layered document (`OneImage.Layers`, see [layers.md](layers.md))
is compared with ImageSharp 3.1.12 cloning a 24 MP image and chaining
`DrawImage` calls. Full-size 24 MP Rgba8 layers at 50% opacity, Normal blend,
every tile recomputed; Ryzen 9 7950X3D (16 cores, 32 threads), .NET 10.0.13,
on a shared machine with other jobs running (load 4-12 during the runs).

| Row | OneImage, default (4 threads) | OneImage, degree raised to 32 | ImageSharp, all cores | Allocated (OneImage) |
|---|--:|--:|--:|---|
| flatten 10 layers | 77 ms | 34 ms | 72 ms | 0.25 MB |
| flatten 50 layers | 361 ms | 129 ms | 297 ms | 0.25 MB |
| flatten 10 layers, AVX2 only | 115 ms | 40 ms | 91 ms | |
| flatten 50 layers, AVX2 only | 544 ms | 157 ms | 340 ms | |

- **Flatten of 10 layers at 24 MP is slower than ImageSharp at the default
  thread limit: 77 ms against 72 ms (1.07×), an exception.** It is not
  published as a tie. The 50-layer and AVX2-only rows are slower in the same
  way.
- **The comparison is not on equal cores.** OneImage's default per-call degree
  is 4 threads, so a server is not oversubscribed by one call; ImageSharp's
  `DrawImage` chain uses every core. With the degree raised to 32 for the
  call, OneImage takes 34 ms, 2.1× faster than ImageSharp's 72 ms. The
  default stays at 4.
- A cold flatten allocates 0.25 MB. The raw figures and the method (the
  benchmark is in `bench/OneImage.Benchmarks`) are in
  [layers.md](layers.md#measured).

## AVX-512 (information)

The gate runs with AVX-512 disabled so that its numbers describe the
AVX2 path every x64 server has. The same suite with native AVX-512
enabled on this CPU gave the times below (OneImage ms; the AVX2 gate
column repeats the table above). They change little. The raw reports are
[gate-final-avx512.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/bench/results/gate-final-avx512.md)
and
[gate-rerun-avx512.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/bench/results/gate-rerun-avx512.md).

| Task | AVX2 gate: OneImage ms | AVX-512: OneImage ms | AVX-512: ImageSharp ms | AVX-512 time vs ImageSharp |
|---|--:|--:|--:|---|
| thumb-jpeg | 37.5 | 38.1 | 48.2 | 1.27× faster |
| thumb-webp | 46.9 | 45.9 | 65.6 | 1.43× faster |
| png-to-webp (natural) | 79.1 | 76.7 | 1489.1 | 19.41× faster |
| png-to-webp (photo) | 90.5 | 90.8 | 1041.1 | 11.47× faster |
| png-to-webp (screenshot) | 95.8 | 99.4 | 251.0 | 2.53× faster |
| jpeg-decode | 41.2 | 41.7 | 76.5 | 1.83× faster |
| jpeg-encode | 87.4 | 81.8 | 100.9 | 1.23× faster |
| png-decode (natural) | 11.4 | 11.4 | 39.3 | 3.45× faster |
| png-decode (photo) | 18.3 | 17.9 | 22.1 | 1.23× faster |
| png-decode (screenshot) | 2.9 | 2.9 | 4.1 | 1.41× faster |
| png-encode (natural) | 219.6 | 222.7 | 1104.4 | 4.96× faster |
| png-encode (photo) | 52.3 | 53.2 | 322.4 | 6.06× faster |
| png-encode (screenshot) | 50.8 | 52.7 | 96.5 | 1.83× faster |
| webp-decode (lossy) | 16.9 | 16.8 | 31.2 | 1.86× faster |
| webp-decode (lossless) | 24.9 | 24.5 | 69.0 | 2.82× faster |
| webp-encode (lossless-2000x1500) | 146.8 | 146.0 | 1095.1 | 7.50× faster |
| webp-encode (lossy-2000x1500) | 101.6 | 103.0 | 581.8 | 5.65× faster |
| webp-encode (lossy-400x300) | 5.4 | 5.4 | 23.9 | 4.43× faster |
| gif-decode | 2.3 | 2.3 | 6.1 | 2.65× faster |
| gif-encode | 13.3 | 13.6 | 19.5 | 1.43× faster |
| gif-resize | 40.4 | 39.1 | 47.5 | 1.21× faster |
| tiff-multipage-decode | 84.3 | 87.5 | 583.7 | 6.67× faster |
| tiff-encode | 44.4 | 43.8 | 131.2 | 3.00× faster |
| bmp-decode | 0.4 | 0.4 | 0.7 | 1.89× faster |
| bmp-encode | 2.8 | 2.7 | 4.8 | 1.78× faster |
| upscale-2x | 6.3 | 6.3 | 12.5 | 1.98× faster |
| watermark | 4.9 | 4.9 | 5.7 | 1.16× faster |
| cmyk-jpeg-to-srgb | 243.4 | 241.2 | 149.4 | 1.61× slower (ImageSharp skips the ICC conversion) |
| jpeg-decode-rgba | 43.4 | 44.2 | 79.5 | 1.80× faster |

## Vector kernels on AVX2 and SSE

The .NET vector API compiles to one instruction only where the CPU has it.
Unsigned 32-bit lanes converted to float (and back), unsigned compares,
64-bit lane multiplies and saturating float-to-int conversions are native
with AVX-512 but expanded into several instructions on AVX2 and SSE, so
benchmarks on an AVX-512 desktop hide them while the AVX2 gate pays for
them. Every kernel in `Kernels/` was checked for these (by reading the
code and by scanning the generated assembly of every kernel the test
suite and a sweep of about fifty operations and codecs reach, with AVX-512 off and AVX2 off). Four
needed work, and their output did not change by a bit.

One thread, 1024 x 1024 pixels, minimum of 12 runs, nanoseconds per pixel
(AMD Ryzen 9 7950X3D, .NET 10.0.11; the host was shared, load average
about 1.5 to 2.5, so differences under 10% are noise):

| Kernel or operation | AVX-512 before | AVX-512 after | AVX2 before | AVX2 after | SSE only before | SSE only after |
|---|--:|--:|--:|--:|--:|--:|
| Error diffusion, Floyd-Steinberg, fast match | 19.6 | 18.8 | 60.8 | 18.5 | 60.8 | 18.8 |
| Error diffusion, Floyd-Steinberg, exact match | 34.5 | 33.7 | 68.2 | 33.5 | 64.7 | 33.5 |
| Error diffusion, Atkinson, exact match | 39.1 | 39.0 | 93.0 | 38.8 | 92.5 | 38.4 |
| Equalization luma kernel (multiply form) | 0.21 | 0.18 | 0.35 | 0.18 | 0.87 | 0.34 |
| Color matrix kernel, RGBA to RGBA | 1.36 | 1.25 | 2.64 | 1.53 | 3.72 | 3.07 |
| Color matrix kernel, RGB to RGB | 1.23 | 1.21 | 1.64 | 1.39 | 3.79 | 2.49 |
| Float-path RGBA composite kernel | 1.66 | 1.07 | 2.15 | 1.12 | 4.27 | 2.25 |
| `Composite` operation, 8-bit RGBA | 2.43 | 1.85 | 2.99 | 1.89 | 4.88 | 2.70 |

The other kernels and the codecs measured within 10% of their AVX-512 time
on AVX2 (resize, blur, JPEG, PNG, WebP, GIF, TIFF, geometry, tone and the
remaining filters). The JPEG transforms have an AVX2 path and a 128-bit path (SSE2/SSE4.1 on
x64, AdvSimd on ARM64); all three give identical bits. With AVX2 turned off
(`DOTNET_EnableAVX2=0`, same host, load 1.6 to 10) a 24 MP decode took 47 ms
(79 ms before the 128-bit path; 32 ms with AVX2) and a q90 4:2:0 encode
179 ms (225 ms before; 86 ms with AVX2). Per 8x8 block the inverse DCT takes
40 ns (scalar 145, AVX2 19) and the forward DCT with quantization 71 ns
(scalar 131, AVX2 47). The AVX2 times did not change. The rest of the
SSE-only gap is the other JPEG stages (entropy coding, color conversion,
upsampling), not the transforms.

## Method

- **Libraries.** OneImage, measured against
  ImageSharp 3.1.12 (the last release that builds without a license key,
  so it is the reference; newer versions are not measured here),
  SkiaSharp, NetVips and Magick.NET, each through its .NET package.
- **Rows.** Each library runs its idiomatic one-call path to the same
  output, from and to memory, on inputs generated deterministically.
  Where a competitor writes into reused or pooled storage, OneImage
  writes into a caller buffer too; rows that allocate a fresh result are
  kept beside them in the raw reports. A library that cannot do a task
  has no row: SkiaSharp has no BMP, TIFF or animated GIF encoder and no
  TIFF decoder here, and NetVips's native build has no BMP loader.
- **Time.** BenchmarkDotNet with `MemoryDiagnoser`, throughput strategy,
  2–3 warm-up and 8–12 measured iterations per row; the mean is shown.
- **Peak memory.** One process per task and library, repeated three
  times. Each process runs the operation once to warm up, collects
  garbage, resets the resident-set high-water mark, runs the operation
  four times, and reports the peak minus the resident size before:
  the working memory of the operation, native allocations included.
  The median and the spread are shown. A peak difference counts as a tie
  when it is within the larger of 2 MiB and either library's spread,
  and within 5%; see the exceptions above for the rows read as ties.
- **Parallelism.** A single OneImage call uses at most min(4, cores)
  threads by default. Rows with a raised degree are marked as such and
  are information only.
- **Hardware.** AMD Ryzen 9 7950X3D (16 cores, 32 logical), 125 GiB RAM,
  Ubuntu 26.04, .NET SDK 10.0.400, runtime 10.0.11. The gate run has
  AVX-512 turned off (`DOTNET_EnableAVX512=0`, so `Avx2.IsSupported` is
  true and `Avx512F.IsSupported` false); the information run has it on.
- **A shared workstation.** The machine was not idle. The load average
  was 3.98 at the start of the full gate run, between 1.0 and 3.3 during
  it and 1.2 at the end; the re-run of changed rows saw 1.0 to 2.2. Every
  minute's sample is in the raw reports. Absolute times carry that noise;
  ratios on one machine in one window are steadier.
- **Two runs.** The full gate ran on 2026-10-22. The rows for JPEG
  encode and decode, thumb-jpeg, the CMYK conversion and GIF decode and
  resize were measured again later the same day after changes to them;
  the table uses the later numbers for those rows and the first run for
  all others. Both reports are kept:
  [gate-final.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/bench/results/gate-final.md),
  [gate-rerun.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/bench/results/gate-rerun.md).
  ARM64 is checked for correctness only; no ARM64 timings are published.

## Raw reports

Each report lists every library's row, with allocated memory, and the
load samples.

- [gate-final.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/bench/results/gate-final.md): full run, AVX2 gate.
- [gate-rerun.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/bench/results/gate-rerun.md): changed rows, AVX2 gate.
- [gate-final-avx512.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/bench/results/gate-final-avx512.md): full run, native AVX-512.
- [gate-rerun-avx512.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/bench/results/gate-rerun-avx512.md): changed rows, native AVX-512.

To run it yourself: `bench/gate.sh` (see [CONTRIBUTING.md](https://github.com/recursive-ai-oss/oneimage-csharp/blob/main/CONTRIBUTING.md#benchmarks)).
The libvips-based competitors need their native packages for your platform.
