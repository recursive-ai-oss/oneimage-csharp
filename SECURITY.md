# Security policy

## Supported versions

Security fixes go into the latest release. Older releases are not
patched; upgrade to the latest. OneImage targets `net10.0`.

## Reporting a vulnerability

Report it privately through GitHub: open the
[Security tab](https://github.com/recursive-ai-oss/oneimage-csharp/security)
of this repository and choose **Report a vulnerability**. Please do not
open a public issue or pull request for a vulnerability.

Include the file or code that shows the problem, the package version,
the .NET version and platform, and what you expected. A reproduction
that is a small image file or a few lines of C# is the most useful thing
you can send.

What counts: a crash, a hang, an exception other than the documented
ones, a read or write outside a buffer, or memory or time use out of
proportion to the input, when reading a crafted or damaged file. It is a
bug in this library and a valid report whether or not you can show it is
exploitable. So is a way around a limit you have set.

## What the library promises

- **Managed, bounds-checked parsing.** Container parsing, chunk and
  marker walking, entropy decoding, metadata and ICC profile parsing are
  safe C#: spans, explicit length checks and checked arithmetic on every
  size that comes from input. The only `unsafe` code is in the SIMD
  kernel layer (`src/OneImage/Kernels/`), which runs over buffers whose
  sizes were validated before the call and never parses. No native code
  and no dependency outside the .NET libraries.
- **Limits before allocation.** `ImageLimits` bounds pixels per frame,
  dimension, frame count, total pixels, metadata bytes and the
  inflate/LZW expansion ratio. They are on by default and are checked
  against header values before the allocation they guard. A tiny file
  that declares a huge image fails with an `ImageLimitException` without
  allocating the image. See [limits in the guide](docs/guide.md#limits).
- **Typed errors.** Bad input raises `ImageFormatException`,
  `ImageLimitException` or `ImageOperationException`; I/O exceptions and
  `OperationCanceledException` pass through. Any other exception from
  reading an image is a bug to report as above.
- **No recursion on input depth.** Nested structures are walked with
  explicit stacks, so deep input does not overflow the stack.
- **Fuzzed.** Every decoder and parser has a fuzz target: JPEG, PNG,
  GIF, WebP, TIFF, BMP, ICO, inflate, LZW, ICC and EXIF. See
  [fuzz/README.md](fuzz/README.md) for how to run them.

## What it does not promise

These are known limits, not bugs. Read the
[limits](docs/guide.md#limits) before you handle files from outside.

- `ImageLimits.Unrestricted` removes every bound you can set. Two engine
  caps remain (2^24 pixels per dimension and 2^22 tiles per frame).
  Never use it on input you do not trust.
- The default limits allow a frame of up to 2^28 pixels, which is
  hundreds of MiB once decoded. For uploads, set limits that match what
  your service accepts.
- Lazy loading means a damaged file can load successfully and fail when
  pixels are first read, for example at `Save`. Load with
  `new LoadOptions { Validate = true }` to fail up front ([errors](docs/guide.md#errors)).
- A path source is reopened when pixels are read and checked for
  changes by size and modification time. That detects a replaced file
  but is not a lock: keep untrusted files in storage nobody else writes.
- ICC profiles and image metadata are carried through and rewritten as
  blobs; the library parses only the fields it uses. Do not treat
  embedded metadata as trusted text.
- Third-party codecs you register through `ImageConfiguration` run
  outside these promises.
