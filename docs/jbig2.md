# JBIG2 decoding

JBIG2 (ITU-T T.88) is the bilevel codec scanned PDF pages use. OneImage ships a decode-only implementation inside
the core package. It is internal: it is not in the default `ImageConfiguration` and not a public format. It exists
so that a PDF renderer in the same library family can reach JBIG2 images through core's internals, with core's
limits, kernels and fuzz harness, and without adding a dependency.

Entry points (`OneImage.Codecs.Jbig2.Jbig2Codec`):

- `Decode(data, globals, limits, jbig2Limits, cancellationToken)`: an *embedded* stream as a PDF holds it (segments
  only, no file header), preceded by the segments of the optional `/JBIG2Globals` stream. Returns a `Jbig2Page`.
- `DecodeFile(file, pageNumber, ...)`: a standalone `.jb2`/`.jbig2` file, sequential or random-access organisation.
- `TryReadPageSize(data, out width, out height)`: reads the page information segment only.

`Jbig2Page` holds `Width`, `Height`, `Stride` and `Data`: one bit per pixel, most significant bit first, rows padded
to whole bytes with zero bits, **1 = black** (the sense the standard uses). A PDF consumer inverts this for
`DeviceGray`, and honours `/Decode` and `/ImageMask` itself.

## What is decoded

Generic regions (arithmetic with all four templates and any adaptive-pixel positions, typical prediction, and MMR),
generic refinement regions (both templates, typical prediction), symbol dictionaries (arithmetic and Huffman, with
refinement/aggregate symbols, collective bitmaps and export flags), text regions (arithmetic and Huffman, refinement,
all reference corners and transposition, the combination operators), pattern dictionaries and halftone regions
(gray-scale image decoding of annex C), custom Huffman tables (B.2) and the 15 standard ones (annex B), page make-up with
all combination operators and default pixel, striped pages and pages of unknown height, immediate/lossless/
intermediate segment variants, and both file organisations. Profile, extension and unknown segments are skipped.

Not decoded: the extended generic template (flag bits 4-7 of the generic region flags) is refused with a format
error. Colour JBIG2 extensions are not part of what PDF uses.

MMR reuses the CCITT G4 code the TIFF reader has (`TiffCcitt.DecodeMmr`, which also reports how many bytes it used).
The TIFF `Decode` path is untouched.

## Strictness

A stream is decoded as the standard describes. Where the data is damaged the decoder throws `ImageFormatException`
rather than rendering a partial page: a text region with more instances than it declares, a truncated segment, a
reference to a segment that does not exist. Some PDF viewers instead paint what they managed to decode; a caller that
wants that behaviour has to catch the exception and fall back.

## Limits

Everything that reads the stream is safe, bounds-checked C#; there is no `unsafe` code. Page size is bounded by
`ImageLimits` (pixels per frame, dimension). `Jbig2Limits` bounds the parts of a JBIG2 stream that can ask for far
more work than their size suggests; each is checked against header values before the allocation it guards:

| Limit | Default | Guards |
|---|---|---|
| `MaxSegments` | 131072 | segments in globals plus page data |
| `MaxReferredSegments` | 65536 | segments one segment refers to |
| `MaxSymbolsPerDictionary` | 1048576 | symbols a dictionary defines, exports or imports |
| `MaxSymbolPixels` | 16 Mi | pixels in one symbol or pattern |
| `MaxRetainedBytes` | 128 MiB | symbol and pattern bitmaps kept for later segments |
| `MaxInstances` | 32 Mi | symbol instances drawn in a stream |
| `MaxDecodedPixels` | 512 Mi | pixels allocated and decoded in total |
| `MaxHuffmanTableLines` | 65536 | lines of a custom Huffman table |
| `MaxPixelsPerCodedByte` | 1 Mi | declared region pixels per byte of coded data |
| `MaxPatterns` | 65536 | patterns in a dictionary |
| `MaxHalftoneCells` | 16 Mi | cells in a halftone grid |

Unknown-height pages grow stripe by stripe and are checked against the frame limits at every growth. Loops over
input-controlled counts (height classes, Huffman lines, instances) are bounded by these budgets, nothing recurses on
input depth, and cancellation is observed once per row of the larger regions.
`tests/OneImage.Tests/Jbig2/Jbig2LimitTests.cs` has one crafted stream per limit and checks the bytes allocated
before the refusal.

## Conformance

- The T.88 Annex H example (3 pages: Huffman and MMR coding, arithmetic coding, refinement and aggregation) decodes, and
  the pages the standard codes in different ways agree pixel for pixel (page 1 Huffman/MMR equals page 2 arithmetic;
  page 3 refinement/aggregation equals the text region of page 1).
- The MQ decoder reproduces the H.2 test sequence.
- A corpus of 2327 JBIG2 streams taken from public PDF files (US government documents) was decoded and compared with
  the images poppler's `pdfimages` writes from the same files. `tools/jbig2-corpus.sh` builds it from PDFs you
  supply; the test `Jbig2CorpusTests` runs it when `ONEIMAGE_JBIG2_CORPUS` names the directory. The extracted images
  are never committed.

| Class | Streams | Explanation |
|---|---|---|
| bits identical | 1925 | |
| exactly the inverse, every pixel | 402 | The PDF image dictionary says `/Decode [1 0]` (401 streams) or `/ImageMask true` (1 stream); `pdfimages` applies that polarity when it writes the bitmap, and our decoder returns the raw JBIG2 bits (1 = black) for the caller to interpret. Not a decoder defect. |
| anything else (size mismatch, partly different bits, error) | 0 | |

How the 402 were classified: the test accepts a stream as inverted only when *every* pixel is the exact complement of the
reference (padding bits ignored), so a decoder fault cannot hide in that class; a single wrong pixel would be reported as a
difference. The cause was then confirmed in the PDFs themselves: in each of the 23 documents with inverted streams, the
number of JBIG2 image dictionaries carrying `/Decode [1 0]` or `/ImageMask true` equals the number of inverted streams
from that document (for example 126845: 59 of 59, 882166: 108 of 108, 966625: 33 of 288 images, 33 inverted). No stream
of the corpus is a quirk of the reference beyond this polarity handling.

Four small streams are committed as `tests/testdata/jbig2/pdf-*.jb2e` (with `.jb2g` globals for three of them), excerpts
of public US government PDFs, with their page hashes in `Jbig2PdfStreamTests`. One of them (`966625-006`) is an inverted
class member; the others match without inversion. The full corpus stays outside the repository because of its size.

## Fuzzing

`fuzz/run.sh jbig2 <seconds>` drives the embedded decoder (with and without globals) and the file decoder under small
limits. Seeds are Annex H and the committed PDF streams.

## Speed

`bench/OneImage.Benchmarks/Jbig2` decodes the committed pages (BenchmarkDotNet ShortRun, .NET 10.0.402, AMD Ryzen 9
7950X3D, 32 logical cores; machine load 8 at the start of the run because the benchmark project had just been built
on it, 6 at the end, so treat the numbers as upper bounds):

| Page | Size | Mean | Allocated |
|---|---|---|---|
| 504344-001 (nearly blank, globals) | 3600x4576 | 3.8 ms | 4.0 MB |
| 714558-003 (text, symbol globals) | 2089x3152 | 5.6 ms | 1.9 MB |
| 714748-174 (text, symbol globals) | 2552x3304 | 3.7 ms | 2.3 MB |
| 966625-006 (generic region) | 1321x1814 | 7.1 ms | 0.65 MB |

For orientation only: `pdfimages -f P -l P` (poppler, a separate process on another, loaded machine) took 16-20 ms per run
for the pages holding the same images, which includes starting the process, parsing the PDF and writing a PBM file, so it
is not a like-for-like decoder timing.
