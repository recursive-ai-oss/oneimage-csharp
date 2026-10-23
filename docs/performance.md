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
| tiff-multipage-decode | 52.3 | 620.3 | 11.87× faster | netvips 26.6 | 1.96× | 2.5 (±0.2) | 54.7 (±0.1) | 85.1 | lower |
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
| tiff-multipage-decode, default degree | 52.3 ms | netvips 26.6 ms | 1.96× |
| png-encode (natural) | 219.6 ms | netvips 96.0 ms | 2.29× |
| gif-decode | 2.3 ms | skia 1.0 ms | 2.23× |
| jpeg-encode | 87.4 ms | netvips 55.3 ms | 1.58× |

Notes:

- **TIFF multi-page.** OneImage limits a single call to min(4, cores)
  threads by default, so a server does not oversubscribe its cores;
  NetVips uses every core. With the per-call degree raised the same read
  takes 51.4 ms at 8 threads and 46.5 ms on all cores (1.93× and 1.75×
  NetVips). The raised rows are shown for information; the default row is
  the one that counts. The 10 strips of a page are the units of parallel
  work (an LZW stream cannot be split), so four threads need three rounds
  per page and more than ten cannot help. Each strip costs about 1.3 ms of
  LZW decoding and 0.06 ms of predictor undo, and a copy-out of about 0.25
  ms per strip comes on top.
  The row and these figures were re-measured on the worker after the LZW
  and predictor work (only these rows; the peak columns are from the
  earlier full run). Same worker, same configuration, baseline and new
  build alternated three times under load 2 to 11 from other jobs: the
  gate row went from 60.3 to 54.6 ms (means; best runs 59.3 and 51.4 ms).
  On a local run of the same two builds (another job was running) the read went from 188 to
  160 ms at one thread and from 61 to 51 ms at four. The gap to NetVips is
  still about 2×; the floor is LZW decoding in safe code.
  **Direct whole-frame read.** A whole-frame read of a TIFF of 1 Mpixel or more
  (`CopyPixelsTo`, `ToArray`) no longer goes through 128×128 tiles: strips and
  tiles are handed out one at a time to the call's threads, each decoded into
  pooled memory and converted row by row straight into its own part of the
  caller's buffer, so the pixels are copied once instead of three times (block,
  band, tile, caller). Output is byte-identical (hash-compared over the test
  corpora at degrees 1, 4 and 16, with AVX2 and all intrinsics off). Same worker
  (Ryzen 9 7950X3D, 32 logical cores, .NET 10.0.13, AVX-512 off), before and
  after, one run each, other jobs on the host raised the load to 4-5 during the
  "after" run: the gate row 49.9 → 46.9 ms and allocation 965 → 92 KB; with the
  per-call degree raised to 8 42.7 → 32.0 ms, and to all cores 46.9 → 27.1 ms
  (NetVips 26.3 ms on all cores). At the default degree the 10 strips of a page
  still need three rounds, so the LZW decode, not the copy, sets the time.
  Reads under 1 Mpixel, region reads and frames whose strip is over 16 MiB of
  raw samples keep the tile path, which caches blocks for later reads.
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
  2.72× to 1.58× after the last fixes and is a goal, not a gate; the
  encoder pass described under "JPEG encode pass" closed it (0.96×).

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

## JPEG encode pass

Encoding got faster with byte-identical output (the same bytes at
degrees 1, 4 and 16, with AVX-512 off, AVX2 off and intrinsics off, for
quality 30/75/90/100 × 4:4:4/4:2:2/4:2:0/4:4:0/gray × optimized/standard
tables × baseline/progressive, at 0.1 MP and 4.6 MP).

- RGB to YCbCr is an SSSE3/SSE4.1 multiply-add kernel and the chroma
  averaging is portable 128-bit code, each with its scalar loop as the
  reference. Quantized blocks are put into zigzag order by placing only
  the non-zero values.
- Without restart markers, every worker takes the next MCU row and does
  all its work (convert, downsample, transform, count or code), so
  there is no barrier between stages. Symbol counts, including the DC
  differences across row boundaries, are gathered in the same pass that
  transforms. When coding, a row needs only the last DC of the row
  before it, which its owner publishes right after quantizing. Finished
  rows are appended to the output in order by whichever worker is free.
  Memory is the same as before: the per-worker row buffers and one tile
  row of pixels.

Measured on the worker (Ryzen 9 7950X3D, 32 logical CPUs, .NET 10.0.13,
AVX-512 off, default degree 4, load 1.0-5.0 before, during and after
both runs, no other benchmark running):

| Row | Before | After | ImageSharp 3.1.12 | NetVips |
|---|---|---|---|---|
| jpeg-encode 24 MP q90 4:2:0, optimized (BenchmarkDotNet) | 82.8 ms | 51.7 ms | 101.7 ms | 53.3 ms |
| same, standard tables | 46.4 ms | 30.7 ms | | |
| gate report jpeg-encode | 86.8 ms | 50.4 ms | 101.0 ms | 52.4 ms |
| gate peak working memory | 4.9 (±1.4) MiB | 3.1 (±0.9) MiB | 4.1 (±1.3) MiB | |
| 3 MP q90 4:2:0, optimized | 7.31 ms | 5.18 ms | 15.8 ms | 7.56 ms |

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

## JPEG restart intervals

Baseline JPEGs that carry restart markers (many cameras and phones write one interval per MCU row) are entropy-decoded on several threads. Each restart interval starts with fresh DC predictors and a flushed bit buffer, so whole intervals decode independently: the calling thread cuts the compressed scan into work units of whole intervals (about one MCU row each), workers decode them into a deeper coefficient ring, and finished units are handed on as complete MCU rows, in order. The worker count is the per-call degree (default min(4, cores)). Only sequential single-scan Huffman files of 1 MP or more with at least two work units take this path; files without restart markers, progressive and arithmetic files, and files whose intervals are few and huge decode as before, at the same speed.

The output is identical to the single-threaded decoder at every scale, degree and hardware path. A damaged file behaves the same too: a worker runs the same per-MCU loop over the same bytes, and when a unit fails the rows before the failing MCU are delivered first and the earliest failure is the one thrown, so a request that those rows complete still succeeds. Peak working memory grows by the deeper ring (about 3.5 MB for a 6000-pixel-wide 4:2:0 frame; the ring is capped at 16 MiB) plus the compressed bytes of the units in flight.

Measured on the shared worker (Ryzen 9 7950X3D, 32 logical CPUs, .NET 10.0.402, `DOTNET_EnableAVX512=0`, default degree 4; machine load 1-4 around the runs, no other benchmark running during the runs). A 24 MP 4:2:0 q90 file written by this library with restart interval 375 (one MCU row) and the same picture without restarts, BenchmarkDotNet mean (allocated):

| Row | no restarts, before | no restarts, after | restarts, before | restarts, after | ImageSharp 3.1.12 | NetVips |
|---|---|---|---|---|---|---|
| full size, caller's Rgb24 buffer | 31.8 ms (52 KB) | 32.4 ms (52 KB) | 32.3 ms | **25.5 ms** (54 KB) | 79.2 ms | 98.0 ms |
| 1/8 (`Resize(750)`) | 27.4 ms (1147 KB) | 27.3 ms | 27.0 ms | **9.0 ms** (1150 KB) | 46.6 ms (TargetSize) | 38.9 ms (shrink=8) |
| thumbnail 400 px | 27.8 ms (360 KB) | 27.6 ms | 27.1 ms | **9.0 ms** (376 KB) | | |

The gate rows are unchanged (their files have no restart markers): jpeg-decode 32.2 vs 32.7 ms, jpeg-decode-rgba 32.4 vs 32.0 ms, thumb-jpeg 27.6 vs 27.5 ms. At full size the decode is now bound by the transform and delivery threads (about 24 ms), not by entropy decoding, so scales 1/2 and 1/4, which are transform-bound, do not change. The "before" runs started at load 3.9 (falling to 1.6) and the "after" runs at load 1.0; the two share the same machine and settings but not the same minute.

## Progressive JPEG decoding

Progressive JPEGs (common on the web) used to decode several times slower than
baseline ones, because their refinement scans were read one coefficient at a
time. Two changes, both with output identical to before at every scale, degree
and hardware path (984 decodes hashed per setting: default, AVX2 off,
intrinsics off):

- Refinement scans keep a 64-bit mask of the nonzero positions of each block, so
  the correction bits between two symbols are read at once and applied without
  branches. The coefficient-by-coefficient reader stays as the reference.
- The scans of a progressive Huffman file of 1 MP or more decode side by side,
  each with its own reader, waiting only for the rows of earlier scans that touch
  the same coefficients; finished rows go to the transform while the last scan
  is still running. Errors are ranked by scan order, so a damaged file fails
  with the same exception as before, though it may deliver some rows first.

Measured on the worker (Ryzen 9 7950X3D, 32 logical CPUs, .NET 10.0.13,
`DOTNET_EnableAVX512=0`, default degree 4, load 0.4 to 1.6), 24 MP files:

| Row | Before | After | ImageSharp 3.1.12 | NetVips |
|---|--:|--:|--:|--:|
| noisy, full size | 389.0 ms | 68.5 ms | 432.0 ms | 404.2 ms |
| smooth, full size | 199.0 ms | 62.3 ms | 209.7 ms | 257.4 ms |
| noisy, 1/8 scale | 38.5 ms | 12.8 ms | 396.8 ms | 335.8 ms |
| smooth, 1/8 scale | 35.1 ms | 12.7 ms | 182.7 ms | 185.0 ms |

Baseline rows are unchanged (jpeg-decode 32.5 to 31.8 ms, thumb-jpeg 27.3 to
26.8 ms). The last luma refinement scan is now the critical path.

## WebP encode memory

The lossy encoder held a 4-bytes-per-pixel copy of the frame, whole source
and reconstructed YUV planes, and every macroblock's 400 quantized levels
(800 bytes) until the file was written, and joined its output through
several doubling buffers. It now keeps:

- the source YUV planes (1.5 bytes per pixel), filled strip by strip from
  the frame, so no RGBA copy exists (alpha, one byte per pixel, is gathered
  only if some pixel is translucent);
- for reconstruction, the last pixel line of each finished macroblock row
  plus each worker's current row, not whole planes;
- the levels in a compact per-row form (a count per block, then zigzag
  variable-length values), about a quarter of their size on a noisy photograph,
  released row by row while the tokens are written;
- partitions written in 64 KiB chunks and joined once into the frame, and the
  file written to the destination chunk by chunk.

The lossless encoder packs its 32-bit pixels band by band instead of via an
RGBA copy of the frame. Output bytes are unchanged: 498 SHA-256 hashes of
lossy (q50/75/90, effort 0/4/6, RGB, RGBA opaque and translucent, degrees
1/4/16, four odd sizes), lossless (exact on and off) and animated encodes
match the previous build on default hardware, `DOTNET_EnableAVX512=0`,
`DOTNET_EnableAVX2=0` and `DOTNET_EnableHWIntrinsic=0`, and a 2000x1500
set matches too.

Peak working memory (the method below: peak resident minus resident before,
warmed up, median of three processes, spread in parentheses, competitors
two to three processes; the runs were on the development host, because the
worker was held by other benchmarks, and resident memory does not depend on
load). The `webp-encode-24mp` and `webp-encode-lossless-24mp` tasks of
`gate-peak` use a 6000x4000 plasma-and-noise image (the `webp-encode` input
at 24 MP); all rows are quality 75, effort 4:

| Task | Before | After | SkiaSharp | Magick.NET | NetVips | ImageSharp |
|---|--:|--:|--:|--:|--:|--:|
| webp-encode-24mp (lossy) | 303.5 MB (±1.0) | 101.6 MB (±6.5) | 86.3 (±0.5) | 173.6 (±1.0) | 241.0 (±14.5) | 1450 (±1) |
| webp-encode-lossless-24mp | 1055.8 MB (±2.4) | 377.1 MB (±1.0) | 663.4 (±1.2) | 736.5 (±1.8) | | |
| webp-encode (lossy-2000x1500) | 22.2 MB (±2.9) | 4.6 MB (±0.2) | 0.7 | 1.3 | 107.9 | 98.1 |
| png-to-webp (photo) | 43.4 MB (±1.5) | 22.6 MB (±4.9) | 38.3 | 26.0 | 203.7 | 87.6 |
| thumb-webp | 4.7 MB (±0.6) | 4.6 MB (±0.3) | 0.7 | 1.9 | 83.2 | 9.9 |

SkiaSharp and Magick.NET encode straight from the caller's pixel buffer; the
lossy encoder converts to YUV first, which is the 36 MB of planes at 24 MP.

### Lossless encoder state

The lossless encoder used to hold, at 24 MP, the packed pixels plus one
full-size copy for each transform (subtract green, predictor, cross-color), a
12-byte token for every pixel, a 4-byte hash-chain entry for every pixel, and
a bit-writer array that doubled and was then copied. Now:

- the transforms run in place on the packed pixels (the encoder owns them; a
  first plan at effort 5 and 6 works on a copy). The predictor reads its
  neighbors' original values from a private copy of its tile row plus the row
  above, so tile rows still run in any order;
- there is no token array: the token stream is the pixels with the matches cut
  out, so state grows with the number of matches. The color cache is simulated
  as the tokens are visited, once for each cache-size estimate and once for
  each of the counting and writing passes;
- hash chains are built a few bands at a time and only the last 2^20 entries
  are kept (about 13 MB at degree 4, 38 MB at degree 16), with the same entries
  as one whole-image chain;
- the size of the main token stream follows from the symbol counts and code
  lengths, so the output is written into one array of exactly its final size
  and returned without a copy.

Output bytes are unchanged: 1011 SHA-256 hashes of lossless encodes (photo-like
RGB and RGBA at 703x499, 256x256, 17x33, 1x1, 3x2 and 1024x700, palette images
of 2, 3, 12 and 200 colors, a repeating image and a 33x16000 one; effort 0 to
6, degrees 1/4/16, Exact on and off) plus the animated lossless encodes match
the previous build on default hardware, `DOTNET_EnableAVX512=0`,
`DOTNET_EnableAVX2=0` and `DOTNET_EnableHWIntrinsic=0`.

Peak working memory, gate-peak method (median of three processes, spread in
parentheses, AVX-512 off, on the development host; resident memory does not
depend on load). The 2000x1500 row is the new `webp-encode-lossless` task:

| Task | Before | After | SkiaSharp | Magick.NET |
|---|--:|--:|--:|--:|
| webp-encode-lossless-24mp | 851.7 MB (±0.9) | 377.1 MB (±1.0) | 663.4 (±1.2) | 736.5 (±1.8) |
| webp-encode-lossless (2000x1500) | 106.7 MB (±5.4) | 46.0 MB (±1.5) | 62.4 (±1.4) | 81.9 (±1.1) |

What is left at 24 MP is the packed pixels (96 MB), the source rows the core
materializes while packing, the output (67 MB for this noisy image) and the
output stream the benchmark writes to.

Time and allocation, BenchmarkDotNet ShortRun, AVX-512 off, on the worker
(Ryzen 9 7950X3D, 32 logical CPUs, .NET SDK 10.0.402, load 2 to 3.2 before,
during and after), base and new alternating, two rounds, mean per call:

| Case | Before | After | Allocated before | Allocated after |
|---|--:|--:|--:|--:|
| lossy 400x300 | 5.40 / 5.23 ms | 5.35 / 5.32 ms | 1.65 MB | 0.92 MB |
| lossy 2000x1500 | 103.8 / 103.7 ms | 104.1 / 103.9 ms | 32.05 MB | 9.27 MB |
| lossy 6000x4000 | 777 / 781 ms | 784 / 788 ms | 252.4 MB | 66.5 MB |
| lossless 2000x1500 | 141.5 / 141.6 ms | 138.5 / 138.5 ms | 117.8 MB | 100.1 MB |
| lossless 6000x4000 | 1097 / 1107 ms | 1073 / 1071 ms | 1034 MB | 817 MB |

The lossless coder state change, same method (worker under the benchmark
lock: Ryzen 9 7950X3D, 32 logical CPUs, .NET SDK 10.0.402, load 0.1 before,
2 to 5 during, 3.5 after), base and new alternating, mean per call:

| Case | Before | After | Allocated before | Allocated after |
|---|--:|--:|--:|--:|
| lossless 2000x1500 | 132.8 / 135.6 ms | 131.2 / 129.7 / 127.7 ms | 100.1 MB | 53.7 MB |
| lossless 6000x4000 | 1072 / 1076 ms | 949 / 980 / 957 ms | 817 MB | 369 MB |
| lossy 2000x1500 (unchanged) | 104.5 / 104.7 ms | 104.9 / 104.4 / 106.0 ms | 9.3 MB | 9.3 MB |

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

## Whole-frame copy-out

Copying a computed 24 MP image (6000×4000, tiles in memory) into a caller's buffer, `CopyPixelsTo`, same worker as above (AVX-512 off, load 1-3 from
other jobs, `CoreHotPathBenchmarks`):

| Row | 1 thread | default (4) | 8 threads |
|---|--:|--:|--:|
| Rgba32 from tiles (96 MB) | 9.3 ms | 6.1 ms | 5.7 ms |
| Rgb24 from tiles (72 MB) | 7.5 ms | 4.4 ms | 4.2 ms |
| Same 96 MB as plain contiguous copies on the same threads (floor) | 5.0 ms | 3.1 ms | 2.9 ms |
| ImageSharp `CopyPixelDataTo` Rgba32 (single-threaded) | 6.1 ms | | |

The copy scales with threads and sits at 1.9× the contiguous floor at every
degree. The gap is the layout, not the engine: a tile row is 512 bytes of
Rgba32, so a frame is 192,000 separate 512-byte copies into rows 24 KB apart. A
stand-alone test of that loop (no engine) takes 10-11 ms against 6 ms for one
contiguous copy, and walking the destination in row-major order or in bands of
8 or 32 rows, or copying rows with 256-bit loads and stores, moved it by less
than 10%. Writing whole tiles to contiguous memory is not an option for a caller
buffer in row-major order. The extra copies that could be removed were in
decoders, and TIFF (above) was the one left; PNG, GIF, WebP and BMP already decode
straight into the caller's buffer on a whole-frame read.

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
| tiff-multipage-decode† | 84.3 | 87.5 | 583.7 | 6.67× faster |
| tiff-encode | 44.4 | 43.8 | 131.2 | 3.00× faster |
| bmp-decode | 0.4 | 0.4 | 0.7 | 1.89× faster |
| bmp-encode | 2.8 | 2.7 | 4.8 | 1.78× faster |
| upscale-2x | 6.3 | 6.3 | 12.5 | 1.98× faster |
| watermark | 4.9 | 4.9 | 5.7 | 1.16× faster |
| cmyk-jpeg-to-srgb | 243.4 | 241.2 | 149.4 | 1.61× slower (ImageSharp skips the ICC conversion) |
| jpeg-decode-rgba | 43.4 | 44.2 | 79.5 | 1.80× faster |

† Measured before the TIFF LZW and predictor work. The AVX2 gate figure is now 52.3 ms (table above); the AVX-512 columns were not re-run.

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

The reduced-size transforms used for DCT scaling (4x4 at 1/2, 2x2 at 1/4) also have a
128-bit path now, identical in output to the scalar one (hashes of the sample corpus and of
24 MP files at scales 1, 1/2, 1/4 and 1/8 match across AVX-512, AVX2, SSE-only and scalar
runs). Measured on the worker (Ryzen 9 7950X3D, 32 threads, .NET 10.0.13,
`DOTNET_EnableAVX512=0`, load 1.1 to 5 from other work, no other benchmark running), per block:
4x4 72 ns before and 26 ns after, 2x2 32 ns before and 20 ns after. A 24 MP 4:2:0 decode into
a reused buffer at 1/2 takes 38.9 ms before and 31.9 ms after, at 1/4 31.7 and 31.1 ms, at 1/8 26.4 and 26.0 ms
(full size 32.0 and 32.6 ms, unchanged within noise). The smooth test photograph is
entropy-bound at about 31 ms, so the transform no longer limits 1/2; noisier files with more
coefficients spend more time in the transform. The thumbnail row (24 MP to 400 px, shrink-on-load at 1/8, which does not
use these transforms) measured 27.3 ms before and 28.1 ms after, within the run spread.

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
