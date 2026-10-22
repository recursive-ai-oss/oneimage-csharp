# OneImage guide

Every snippet here is compiled and run by `tests/OneImage.Tests/Docs/DocsExamples.cs`. All types are in the `OneImage` namespace.

Contents: [the lazy model](#the-lazy-model) · [errors](#errors) · [sources and lifetime](#sources-and-lifetime) · [saving](#saving) · [operations](#operations) · [pixel access and Canvas](#pixel-access-and-canvas) · [metadata](#metadata) · [color](#color) · [animation](#animation-and-multi-page-files) · [limits](#limits) · [threading and memory](#threading-and-memory) · [AOT and trimming](#aot-and-trimming) · [formats](#formats)

## The lazy model

An `Image` is a description of a computation. `Image.Load` reads only the headers (size, format, metadata). Each operation returns a new `Image` that remembers what to do. Work happens when you pull pixels: `Save`, `ToArray<T>()`, `CopyPixelsTo<T>()`, or `ToCanvas()` followed by access.

```csharp
Image img = Image.Load("photo.jpg");          // headers only
Image small = img.Resize(width: 300);         // nothing decoded yet
small.Save("small.png");                      // decode, resize and encode, streaming over tiles
```

What follows from this:

- **Cheap pipelines.** Only what the output needs is computed. A crop never decodes the tiles outside it, and a JPEG thumbnail is decoded at 1/2, 1/4 or 1/8 scale when that is enough. Large images stream through bounded memory rather than being held whole.
- **Images are immutable and safe to share between threads.** An operation never changes the image it is called on.
- **Errors can appear late.** Corrupt pixel data is found when the pixels are pulled, which may be the `Save` at the end of the chain. See [Errors](#errors).
- **Order of operations is yours.** `Resize` then `Crop` is a different image from `Crop` then `Resize`; the engine only reorders what cannot change the result (for example pushing a crop down to the decoder).
- **Argument mistakes are eager.** A bad parameter (no width and no height, a negative size, an undefined enum) throws at the call that got it, not later.

`Image` is not `IDisposable`: it holds no unmanaged resources.

## Errors

Only these leave a public method: the library's own exceptions, `ArgumentException` and friends, I/O exceptions passed through unchanged, and `OperationCanceledException`.

| Exception | Meaning |
|---|---|
| `ImageFormatException` | The data is not a recognized image or is damaged. |
| `ImageLimitException` | The data is valid but exceeds an `ImageLimits` bound (see [Limits](#limits)). Raised from header values, before anything large is allocated. |
| `ImageOperationException` | The request cannot be done: no encoder for the format, an unknown output extension, an op on a pixel format it does not support (for example `Composite` on a CMYK image). |
| `ImageSourceChangedException` | A file loaded by path changed before its pixels were read (derives from `ImageOperationException`). |

All four derive from `ImageException`. A missing or unreadable file throws the ordinary `FileNotFoundException`/`IOException`.

```csharp
try
{
    Image.Load("upload.png").Resize(width: 800).Save("out.jpg");
}
catch (ImageException ex)    // format, limit, operation, source-changed
{
    Console.WriteLine(ex.Message);
}
```

### Errors can surface at `Save`

`Load` validates the container (signature, header, limits) but does not decode pixels. A file whose header is fine and whose compressed data is damaged loads successfully, and throws `ImageFormatException` at the first pixel pull, typically in `Save`. A failed `Save(path)` deletes the partial output file.

To find out up front, ask for validation:

```csharp
// Decodes every frame once; throws ImageFormatException here if anything is wrong.
Image img = Image.Load("upload.png", new LoadOptions { Validate = true });
```

`Validate` costs a full decode. Frames that decode sequentially are kept, so the work is not repeated by the later `Save`.

### Probing without exceptions

```csharp
if (Image.TryLoad("maybe.png", out Image? image))
{
    // use image
}

if (Image.TryIdentify(bytes, out ImageInfo? info))
{
    Console.WriteLine($"{info.Format.Name} {info.Width}x{info.Height}, {info.Frames.Count} frame(s)");
}
```

`TryLoad` and `TryIdentify` return `false` for `ImageFormatException` and `ImageLimitException`. I/O errors still throw. `Image.Identify` reads only the headers; it reports the *stored* size, before auto-orientation.

## Sources and lifetime

| Source | What `Load` does | What you may do afterwards |
|---|---|---|
| Path | Reads the headers and records the file's size and modification time. The file is **reopened** whenever pixels are pulled. | Don't change or delete the file while the image is in use. If its size or modification time differ at pull time, `ImageSourceChangedException` is thrown. |
| `Stream` | Copies the remaining bytes into pooled memory. | The stream is yours: close or dispose it right away. |
| `ReadOnlyMemory<byte>` | References the memory, no copy. | Don't modify the memory while the image, or anything derived from it, is in use. |

```csharp
Image fromPath   = Image.Load("a.jpg");
Image fromStream = Image.Load(File.OpenRead("a.jpg"));      // copied now
Image fromMemory = Image.Load(File.ReadAllBytes("a.jpg"));  // referenced
```

Async variants exist for I/O: `LoadAsync`, `IdentifyAsync`, `SaveAsync`. Everything that computes takes a `CancellationToken`.

`Canvas` is `IDisposable`: dispose it to return pooled tile memory. Forgetting is safe, because the garbage collector reclaims the buffers, but slower.

## Saving

`Save(path)` picks the format from the extension. A stream or `IBufferWriter<byte>` needs the format: `PngFormat.Instance`, `JpegFormat.Instance`, `GifFormat.Instance`, `BmpFormat.Instance`, `IcoFormat.Instance`, `TiffFormat.Instance`, or `WebPFormat.Instance`.

Codec settings go in `SaveOptions.EncoderOptions`:

```csharp
img.Save("q.jpg",  new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 85, Subsampling = JpegSubsampling.Yuv444 } });
img.Save("q.webp", new SaveOptions { EncoderOptions = new WebPEncoderOptions { Quality = 80 } });
img.Save("l.webp", new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = true } });
img.Save("q.png",  new SaveOptions { EncoderOptions = new PngEncoderOptions { CompressionLevel = 9 } });
img.Save("q.tif",  new SaveOptions { EncoderOptions = new TiffEncoderOptions { Compression = TiffCompression.Deflate } });
img.Save("p.gif",  new SaveOptions { EncoderOptions = new GifEncoderOptions { MaxColors = 64, Dither = Dither.Ordered } });
```

Defaults: JPEG quality 75 (the libjpeg scale), 4:2:0, optimized Huffman tables on, sequential. WebP lossy quality 75, effort 4. Each options class documents the rest.

Rules worth knowing:

- Formats that cannot hold animation (JPEG, BMP) write the first frame only. PNG (APNG), WebP, GIF and TIFF (pages) write all frames.
- JPEG has no alpha. An image with alpha is written with its stored color values; use `Flatten` to put it on a background first:

  ```csharp
  logo.Flatten(new Rgba32(255, 255, 255, 255)).Save("logo.jpg");
  ```
- The encoder converts the pixel format when the format cannot hold it (for example 16-bit to 8-bit for JPEG).
- Encoders produce identical bytes for identical input on a given platform, regardless of thread count.

## Operations

All are lazy and return a new `Image`; all apply to every frame of an animation. XML docs on each method state the defaults and edge behavior.

**Resize and thumbnails**

```csharp
img.Resize(width: 300);                                   // height follows the aspect ratio
img.Resize(width: 200, height: 200, fit: Fit.Cover, anchor: Anchor.Smart);   // fill the box, crop the overflow
img.Resize(width: 200, height: 200, fit: Fit.Pad, background: new Rgba32(255, 255, 255, 255));
img.Resize(width: 100, filter: ResamplingFilter.Mitchell);
```

- `Fit`: `Contain` (default; fits inside the box), `Cover` (fills the box, crops), `Stretch`, `Pad`, `BoxPad` (like `Pad`, but never enlarges) and `Min` (covers the box without cropping, never enlarges). `ResizeInto(canvasWidth, canvasHeight, target)` scales the image into a given rectangle of a canvas. `Anchor`: nine positions plus `Smart`, an entropy/edge heuristic that picks the crop window.
- Default filter `Auto`: Lanczos3 on an axis that shrinks, Catmull-Rom on one that grows. Others: `Nearest`, `Box`, `Bilinear`, `CatmullRom`, `Mitchell`, `Lanczos2`, `Lanczos3`, `Lanczos5`, `Lanczos8`, `Hermite`, `Robidoux`, `RobidouxSharp`, `Spline`, `Welch`.
- Resampling runs in linear light on premultiplied alpha and treats untagged RGB as sRGB. Pass `linear: false` to filter the stored values.
- **Upscale defaults differ on purpose.** `Resize` enlarges by default (`upscale: true`): you asked for that size. `Image.Thumbnail` never enlarges unless you pass `upscale: true`: a thumbnail is not bigger than the original. With `Resize(..., upscale: false)` Contain, Cover and Pad stop at scale 1.

`Image.Thumbnail(path, width, height?, fit, anchor, upscale, filter, keepMetadata, ...)` is `Resize` plus the right defaults for a web-ready copy, in this order: apply EXIF orientation; resize (shrink-on-load for JPEG, never larger than the original unless `upscale: true`); convert RGB to sRGB and embed the compact sRGB profile; strip EXIF/XMP/IPTC. It also accepts a `Stream`, `ReadOnlyMemory<byte>` or an existing `Image`.

**Geometry**

```csharp
img.Crop(10, 10, 100, 100);                   // x, y, width, height; must lie inside the image
img.Rotate(90);                               // clockwise; multiples of 90 are exact pixel moves
img.Rotate(15, background: new Rgba32(255, 255, 255, 255));   // expands to the bounding box by default
img.Flip(FlipAxis.Horizontal);
img.Pad(left: 10, top: 10, right: 10, bottom: 10, mode: EdgeMode.Mirror);
img.Trim();                                   // remove a uniform border (eager: it looks at the pixels)
```

`Orient(1..8)`, `AutoOrient()` and `Affine(...)` are also there. `Perspective(...)` applies a 3×3 projective transform, given as a `ProjectiveMatrix`, a `ProjectiveTransformBuilder` (tapers, rotation, scale, skew, translation) or four corner points:

```csharp
img.Perspective(new ProjectiveTransformBuilder().AppendTaper(TaperSide.Top, TaperCorner.Both, 0.7));
img.Perspective([(30, 10), (150, 40), (120, 130), (10, 100)]);   // corners: top-left, top-right, bottom-right, bottom-left
```

Like `Rotate` and `Affine` it grows the canvas to the transformed bounding box by default. `Trim` is the one geometry op that computes immediately, because it must scan for the border.

**Color and tone**: `Grayscale`, `BrightnessContrast`, `Exposure`, `Gamma`, `Levels`, `Curves`, `HueSaturationLightness`, `Invert`, `Threshold`, `Posterize`, `ChannelMixer`, `CombineChannels`, `AddAlpha`, `RemoveAlpha`, `Flatten`, `ConvertTo(PixelFormat)`. Adjacent tone ops fuse into one pass. Identity parameters return the input unchanged. Tone ops act on straight (not premultiplied) color and leave alpha alone; most work on encoded values, while `Grayscale` and `Exposure` default to linear light.

**Color matrix and presets**: `ApplyColorMatrix(ColorMatrix)` multiplies straight RGBA by a 5×4 matrix (ImageSharp's layout; `ColorMatrix` documents it), and the looks built on it are `Sepia(amount)`, `BlackWhite()`, `Kodachrome()`, `Lomograph()`, `Polaroid()`, `ColorBlindness(mode)` (the eight simulations) and `Opacity(amount)`. They work on stored values, fuse with the other color ops, and the identity (`Sepia(0)`, `Opacity(1)`) returns the input.

```csharp
Image warm = photo.Sepia(0.6f).BrightnessContrast(contrast: 0.1).Opacity(0.9f);
Image seen = photo.ColorBlindness(ColorBlindnessMode.Protanopia);
```

```csharp
img.Grayscale().BrightnessContrast(brightness: 0.1, contrast: 0.2).Gamma(1.1);
```

**Palettes**: `Quantize(colors, dither, alpha)` limits an image to a palette of at most 256 colors but keeps an ordinary RGB(A) format, so you can still process it or save it anywhere.

```csharp
img.Quantize(colors: 16, dither: Dither.FloydSteinberg);
```

**Compositing**: `Composite(overlay, x, y, opacity, linear)` lays one image over another (source-over, color-managed, premultiplied). It is the building block for watermarks:

```csharp
Image marked = photo.Composite(mark, x: photo.Width - 90, y: photo.Height - 40, opacity: 0.8f);
```

The overlay is converted to the base image's profile. The blend happens on stored (encoded) values unless `linear: true`.

`blend:` picks how colors combine (`BlendMode`: Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight, Difference, Exclusion, Add, Subtract, LinearBurn, LinearDodge, VividLight, LinearLight, PinLight, HardMix, Divide, Hue, Saturation, Color, Luminosity), following the W3C Compositing and Blending formulas. `alpha:` picks the Porter-Duff operator for coverage (`AlphaComposition`: SrcOver by default, then Clear, Src, Dest, DestOver, SrcIn, DestIn, SrcOut, DestOut, SrcAtop, DestAtop, Xor). Only pixels under the overlay change.

```csharp
Image shaded = photo.Composite(texture, opacity: 0.6f, blend: BlendMode.Multiply);
Image cutout = photo.AddAlpha().Composite(mask, alpha: AlphaComposition.DestIn); // the base needs an alpha channel for DestIn
```

**Filters**

```csharp
img.GaussianBlur(sigma: 2);                       // sigmaY: for a different vertical sigma
img.BoxBlur(radius: 3);
img.Sharpen(amount: 1.5, radius: 1.2, threshold: 0.02, linear: false);   // unsharp mask
img.Median(radius: 1);                            // 3x3 median, removes speckle noise
img.Sobel();                                      // edge strength as a gray image
img.Convolve(new float[,] { { 0, -1, 0 }, { -1, 5, -1 }, { 0, -1, 0 } });   // your own kernel
```

All are lazy, read only their tile plus a margin of the kernel radius (so they stream), and apply to every frame.

- **Working space.** `GaussianBlur`, `BoxBlur`, `Sharpen` and `Convolve` work in linear light on premultiplied alpha by default (`linear: true`), so blurring on transparency leaves no dark halos. `Sharpen` in linear light gives stronger halos on the dark side of edges; pass `linear: false` for the conventional look. `Median` and `Sobel` use the stored values and have no `linear` parameter.
- **Edges.** `edge:` is `EdgeMode.Clamp` (default, repeat the edge pixel), `Mirror`, or `Constant` (transparent black, so edges darken on opaque images). `Wrap` is not supported by filters.
- **Parameters.** `GaussianBlur(sigma)` has kernel radius `ceil(3 sigma)`; sigma 0 on an axis leaves it unchanged. `BoxBlur` radius is 1 to 4096 (window `2r+1`). `Sharpen`: `out = in + amount * (in - blur)`; `radius` is the Gaussian sigma; differences below `threshold` (0 to 1, fraction of full scale) are left alone; alpha is not sharpened. `Median` radius is 1 to 127. `Sobel(scale)` returns a gray image without alpha (luminance by Rec. 709 weights, divided so a black-to-white step gives full scale at `scale: 1`); it refuses CMYK and Lab.
- **`Convolve`** applies the kernel as written (correlation, not flipped), needs odd width and height, divides by the sum of the weights unless you pass `divisor:` (1 when the sum is 0), then adds `offset:` as a fraction of full scale. A rank-one kernel runs as two 1-D passes, with the same result.
- **Large blurs are approximated.** Gaussian sigma above 16 and box radius above 48 are computed on a reduced-resolution copy and enlarged, to keep streaming memory bounded. The result stays within a few 8-bit levels of the exact blur on photographs; see the XML docs for the measured difference. Below those limits the result is exact.
- Results do not depend on the thread count.

**Effects**

```csharp
img.Pixelate(size: 8);                                   // mosaic; each block takes its center pixel
img.OilPaint(levels: 10, brushSize: 15);                 // painterly flattening
img.Vignette(new Rgba32(0, 0, 0, 255));                  // darken towards the corners; radiusX:, radiusY:, area: are optional
img.Glow(new Rgba32(255, 255, 255, 255), radius: 200);   // a radial tint, full color at the center
img.BokehBlur(radius: 32, components: 2, gamma: 3);      // lens blur; bright points bloom into discs
img.GaussianSharpen(sigma: 3);
img.DetectEdges(EdgeDetector.Kirsch);                    // also Prewitt, Scharr, RobertsCross, Kayyali, Laplacian3x3/5x5, LaplacianOfGaussian, Robinson, Sobel
img.DetectEdges(EdgeDetector.Scharr, grayscale: false);  // per color channel, keeps format and alpha
img.EntropyCrop(threshold: 0.5f);                        // crop to the area with detail (eager, like Trim)
```

They are lazy (except `EntropyCrop`), read only their tile plus a margin (the radius, brush half-width or kernel size; none for `Pixelate`, `Vignette` and `Glow`), and apply to every frame.

- **Working space.** These work on stored values (not linear light), as ImageSharp does; `GaussianSharpen(linear: true)` is the one that can switch. `OilPaint`, `Vignette`, `Glow` and `DetectEdges` accept gray and RGB images and refuse CMYK and Lab; `Pixelate` takes any format.
- **Edges.** `BokehBlur`, `GaussianSharpen` and `DetectEdges` take `edge:` as the other filters do (clamp by default).
- **Limits.** `BokehBlur` radius is 1 to 255 with 1 to 6 components (cost grows with the radius, not its square); `GaussianSharpen` sigma up to 42; `OilPaint` brush up to 255 and up to 1024 levels.
- **`Vignette` and `Glow`** blend the color over the pixel (source-over, its alpha times the falloff). The vignette falloff is `0.9 * distance / sqrt(rx^2 + ry^2)`; the glow's is `1 - 0.95 * distance / radius`. Pass `area:` to confine the effect to a rectangle and center it there. The default glow color is black, as in ImageSharp.
- Results do not depend on the thread count.

**Analysis**

```csharp
ImageHistogram hist = img.Histogram();                    // 256 bins per channel
long darkRed = hist[0, 0];                                // channel 0 (R), bin 0
ReadOnlySpan<long> green = hist.Channel(1);

ImageStatistics stats = img.Statistics();
ChannelStatistics red = stats.Channels[0];                // Count, Min, Max, Mean, StdDev
```

Both compute the whole frame immediately, in parallel, and only read pixels (the image is not changed). They look at **frame 0**; use `img.Frames[i].Histogram()` / `.Statistics()` for another frame. Channels are in stored order with alpha last (gray; R, G, B; C, M, Y, K). Samples are the stored values: no color conversion, no un-premultiplying. `bins` is 1 to 65,536; 8-bit value `v` goes to bin `floor(v * bins / 256)`. `Statistics` values are in the channel's stored units (0..255, 0..65,535 or 0..1: see `FullScale`); `StdDev` is the population standard deviation. `skipTransparent: true` ignores pixels with alpha 0. Lab images are refused. Corrupt data throws `ImageFormatException` here, like any pixel pull.

## Pixel access and Canvas

Read pixels out of any `Image` as the struct you want. Formats are converted if they differ.

```csharp
Rgba32[] all = img.ToArray<Rgba32>();                           // frame 0
var block = new Gray8[16 * 16];
img.CopyPixelsTo(block, new ImageRect(100, 100, 16, 16));       // a rectangle
Image fromMemory = Image.FromPixels<Rgba32>(pixels, width, height);   // the other direction
```

Pixel structs: `Gray8`, `Gray16`, `GrayFloat`, `GrayAlpha8`, `Rgb24`, `Rgba32`, `Rgb48`, `Rgba64`, `RgbaHalf`, `RgbFloat`, `RgbaFloat`, `Cmyk32`, `LabFloat`. `Image.Format` is a runtime `PixelFormat` (`Rgb8`, `Rgba8`, `Gray8`, ...).

For editing, use a `Canvas`: a mutable image stored in 128×128 tiles created on first write.

```csharp
using Canvas canvas = img.ToCanvas();                 // tiles are computed on first touch
canvas[0, 0] = new Rgba32(255, 0, 0, 255);            // single pixel, converted to/from the canvas format

// Row access. T must match the canvas format exactly: a JPEG decodes to Rgb8, so T is Rgb24.
using (PixelLock<Rgb24> l = canvas.Lock<Rgb24>(new ImageRect(10, 10, 50, 50), LockAccess.ReadWrite))
{
    for (int y = 0; y < l.Height; y++)
    {
        l.GetRow(y).Fill(new Rgb24(0, 0, 0));
    }
}

Image before = canvas.Snapshot();                     // O(1), immutable
canvas[0, 0] = new Rgba32(0, 255, 0, 255);            // the snapshot does not change
```

- `Lock<T>` never converts. With a mismatched `T` it throws an `ArgumentException` that names the canvas format and the matching struct; to read in another pixel type use `canvas.CopyTo<T>(...)` or `canvas.Snapshot().ToArray<T>()`.
- `Lock` access modes: `Read`, `Write` (you overwrite every pixel in the rectangle) and `ReadWrite`. Dispose the lock to commit.
- `new Canvas(width, height, PixelFormat.Rgba8)` makes an empty (transparent black) canvas.
- A canvas has one writer. Hand `Snapshot()` images to other threads. Snapshots share tiles and a tile is copied only when the canvas next writes it, so keeping many snapshots for undo is cheap.
- A canvas holds a single frame. `ToCanvas()` uses frame 0.

## Metadata

`Image.Metadata` carries `IccProfile`, `Exif`, `Xmp` and `Iptc` as raw blobs, plus `Orientation` (1–8).

- **Preserved by default.** `Load` → ops → `Save` keeps the ICC profile and the EXIF/XMP/IPTC blobs for formats that can carry them. On save, the EXIF block's pixel-dimension and orientation tags are corrected to the saved image. A malformed EXIF block is dropped instead of written with stale values.
- **Typed EXIF.** `image.Metadata.ReadExif()` (or `TryReadExif`) parses the common tags: `Orientation`, `Make`, `Model`, `CaptureTime`, `Gps`.

  ```csharp
  ExifData? exif = img.Metadata.ReadExif();
  string? make = exif?.Make;
  ```
- **Auto-orient at load.** By default `Load` applies the stored EXIF orientation lazily: `Width`/`Height` are the upright size and `Metadata.Orientation` reads 1, so saving writes upright pixels and an orientation tag of 1. Opt out for the stored layout and the original tag:

  ```csharp
  Image stored = Image.Load("photo.jpg", new LoadOptions { AutoOrient = false });
  ```
- **Strip it.** `StripMetadata()` removes EXIF, XMP and IPTC and the ICC profile; `StripMetadata(keepIcc: true)` keeps the profile. It leaves the orientation value alone, since that describes how to display the pixels.
- **`Thumbnail` strips for privacy.** EXIF can contain GPS position and device details, so `Image.Thumbnail` removes EXIF/XMP/IPTC and keeps only the ICC profile (converted to sRGB) and the corrected orientation. `keepMetadata: true` carries the blobs over, GPS included; use it knowingly.

## Color

- **Default: source space kept.** Decoding does not change color values. The embedded ICC profile travels with the image and is written back on save. `ConvertColor` and `AssignProfile` are explicit.
- **`Thumbnail` converts to sRGB** (relative colorimetric, black point compensation), so wide-gamut and CMYK sources look right everywhere.
- **Explicit conversion.**

  ```csharp
  Image p3   = img.AssignProfile(ColorProfile.DisplayP3);      // label only: values are unchanged
  Image srgb = p3.ConvertColor(ColorProfile.Srgb);             // converts values and embeds the profile
  Image perceptual = p3.ConvertColor(ColorProfile.Srgb, RenderingIntent.Perceptual, blackPointCompensation: false);
  ```

  `ConvertColor` defaults to relative colorimetric with black point compensation on. Built-in profiles: `Srgb`, `DisplayP3`, `WideGamutRgb` (Adobe RGB (1998)-compatible), `ProPhoto`, `Rec2020`, linear variants, gray, `LabD50` and `GenericCmyk`; `ColorProfile.FromIcc(bytes)` reads any ICC v2/v4 matrix or LUT profile. DeviceLink, named-color and iccMAX profiles are not supported.
- **Untagged CMYK** (a CMYK JPEG or TIFF with no profile) is interpreted with the built-in generic CMYK profile, derived from published FOGRA39 characterization data, when converting to RGB. See `THIRD-PARTY-NOTICES.md`.
- **Linear light.** Resizing, rotation and compositing with `linear: true` convert to linear light first, treating untagged RGB and gray as sRGB; tone ops and blends use encoded values unless told otherwise. Each op documents its `linear` parameter.
- HDR transfer functions (PQ/HLG) are not interpreted; `cICP` and similar tags are not applied.

## Animation and multi-page files

An `Image` has `FrameCount`, `Frames`, `LoopCount` (0 = forever) and a `Delay` per frame. GIF, APNG, animated WebP and multi-page TIFF load as multiple frames. Operations apply to every frame, so animation survives a pipeline:

```csharp
Image anim = Image.Load("spin.gif");
Console.WriteLine($"{anim.FrameCount} frames, loop {anim.LoopCount}, first delay {anim.Frames[0].Delay}");

anim.Resize(width: 60).Save("spin.webp");                 // all frames, delays and loop count are kept
Image still = anim.Frames[1].ToImage();                   // one frame as its own image
anim.Save("first.jpg");                                   // JPEG cannot animate: frame 0 only
```

Animations are exposed as fully composited frames of the canvas size (disposal and blending already applied). Members of `Image` that read pixels (`ToArray`, `CopyPixelsTo`, `ToCanvas`, `Histogram`, `Statistics`) use frame 0 only and ignore the rest, without warning; use `Frames[i]` (an `ImageFrame` has `ToArray`, `CopyPixelsTo`, `Histogram`, `Statistics` and `ToImage()`) for the others. Each TIFF page is a frame with its own size.

## Limits

Everything read from untrusted bytes is checked against `ImageLimits` using header values, before the allocation they guard.

| Limit | Default |
|---|---|
| `MaxPixelsPerFrame` | 2²⁸ |
| `MaxDimension` | 65,535 |
| `MaxFrames` | 1,000 |
| `MaxTotalPixels` | 2³⁰ |
| `MaxMetadataBytes` | 16 MB |
| `MaxExpansionRatio` (inflate/LZW output vs. input) | 1,100 |

```csharp
// Per call.
Image.Load("a.jpg", new LoadOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 1_000_000, MaxDimension = 4096 } });
Image.Load("a.jpg", new LoadOptions { Limits = ImageLimits.Unrestricted });   // trusted input only

// Global default, set once at startup.
ImageConfiguration.Default.Limits = new ImageLimits { MaxPixelsPerFrame = 50_000_000 };
```

`ImageLimits.Unrestricted` removes the configurable bounds. Two engine caps remain: 2²⁴ pixels per dimension and 2²² tiles per frame. Exceeding a limit throws `ImageLimitException`. `Canvas` and `Save` take limits too.

## Threading and memory

- Rendering uses the .NET thread pool with 128×128 tiles as the unit of work. A single call uses at most `ImageEngine.DefaultCallDegree` threads (default `min(4, cores)`), so one request does not take a whole server. Override per call with `SaveOptions.MaxDegree` / `LoadOptions.MaxDegree` / the `maxDegree` parameter; `ImageEngine.MaxDegree` (default: core count) caps everything. Results never depend on the degree.
- Computed tiles are cached under a global byte budget, 256 MiB by default (`ImageEngine.CacheBudgetBytes`), least recently used first; an evicted tile is recomputed on demand. Canvas tiles are owned by the canvas and are not counted or evicted.

  ```csharp
  ImageEngine.DefaultCallDegree = 2;
  ImageEngine.CacheBudgetBytes = 64L * 1024 * 1024;
  ```
- Large images from sequential decoders (16 MiB of pixel data and up) stream through a window of tile rows instead of being held whole. An operation that needs rows out of order (rotate by 90° or 270°, a vertical flip) makes the engine decode the whole frame once.
- `Image` instances are safe for concurrent reads. A `Canvas` is not.

## AOT and trimming

The library has no reflection, no dynamic code and no dependencies, and is annotated trimmable and AOT-compatible. Codecs are registered explicitly, so an app that only handles two formats can leave the others out of the published binary: build your own `ImageConfiguration` instead of touching `ImageConfiguration.Default` (which references every codec).

```csharp
var config = new ImageConfiguration().Add(JpegFormat.Instance).Add(PngFormat.Instance);

Image img = Image.Load("a.jpg", new LoadOptions { Configuration = config });
img.Save("a.png", new SaveOptions { Configuration = config });   // extension lookup uses this configuration
// Image.Load("a.webp", new LoadOptions { Configuration = config }) throws ImageFormatException: WebP is not registered.
```

Format-less entry points (`Thumbnail`, `Load` without options) use `ImageConfiguration.Default`; pass `LoadOptions` with your configuration when trimming matters. You can add your own `IImageFormat` the same way.

## Formats

| Format | Read | Write |
|---|---|---|
| PNG | all color types, depths 1–16, Adam7, tRNS, APNG | 8/16-bit gray, gray+alpha, RGB, RGBA; automatic or required palette (`PngColorMode`); APNG; levels 0–9; filter choice |
| JPEG | baseline, extended and progressive, Huffman or arithmetic, 8- and 12-bit, gray/YCbCr/RGB/CMYK/YCCK, restart markers; DCT scaling 1/2, 1/4, 1/8. Not lossless or hierarchical | baseline or progressive; quality 1–100; 4:2:0/4:2:2/4:4:0/4:4:4; optimized tables; gray, RGB and CMYK |
| WebP | lossy (VP8), lossless (VP8L), alpha, animation | lossy, lossless, alpha, animation |
| GIF | GIF87a/89a, animation, transparency, disposal, loop count | global/local palettes, ≤256 colors, dithering (none, Floyd–Steinberg, ordered), frame differencing |
| TIFF | strips and tiles, BigTIFF, none/LZW/Deflate/PackBits/JPEG/CCITT G3 and G4, predictors, planar, 1–16-bit and 32-bit float; gray/RGB/palette/CMYK; multi-page. Signed and 64-bit samples, plain YCbCr and CIELab are rejected with an error | none/LZW/Deflate/PackBits, 8/16/32f, multi-page, BigTIFF |
| BMP | 1–32 bpp, RLE, V4/V5 headers | 24/32 bpp |
| ICO | yes (BMP and PNG entries; each size is a frame) | yes |

Detection is by content, not extension. `Image.Identify` and `Image.TryIdentify` report format, size, frame count and metadata without decoding pixels.
