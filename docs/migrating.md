# Migrating from ImageSharp and System.Drawing

The OneImage side of every snippet below is compiled and run by `tests/OneImage.Tests/Docs/MigrationExamples.cs`. The ImageSharp side is compiled there too, against ImageSharp 3.1.12 (`ImageSharpSide.cs`). The System.Drawing side is **not** compiled in this repository (System.Drawing.Common is Windows-only and is not a dependency of the test project); it shows the usual idioms and is there for orientation.

## Concepts that differ

| | ImageSharp | System.Drawing | OneImage |
|---|---|---|---|
| Evaluation | Eager: each operation runs immediately on a decoded image | Eager | **Lazy**: operations are recorded; pixels are computed in tiles when you save or read |
| Mutation | `image.Mutate(x => ...)` changes the image in place; `Clone(...)` copies | Draw into a `Graphics`; `Bitmap` is mutable | **No `Mutate`.** Operations return a new immutable `Image`; chain them. Use a `Canvas` for in-place pixel edits |
| Pixel type | `Image<TPixel>` generic; `Image` is the non-generic base | `Bitmap` (GDI+ formats) | **`Image` is not generic.** It has a runtime `PixelFormat`; a pixel struct (`Rgba32`, ...) appears only where you read or write pixels |
| Disposal | `IDisposable` | `IDisposable` | `Image` has **no `Dispose`**. Only `Canvas` is disposable |
| Errors | Decode errors at `Load` | `ArgumentException`/`ExternalException` | Header errors at `Load`; **data errors may appear at `Save`**. Use `Validate = true` to fail at `Load` ([guide](guide.md#errors-can-surface-at-save)) |
| Orientation | Not applied unless you call `AutoOrient()` | Not applied | **Applied at load** by default; `LoadOptions.AutoOrient = false` to opt out |
| Color | Pixel values as stored | GDI+ may convert | Values kept as decoded, ICC profile carried through; resampling in linear light |
| Metadata | Preserved on save unless removed | Property items | Preserved; `Thumbnail` strips EXIF/XMP/IPTC by default |
| Limits | `DecoderOptions` (some) | none | `ImageLimits` on by default; checked from headers |
| Platform | Cross-platform | Windows-only on current .NET | Cross-platform, pure managed |

A consequence of laziness: `Image.Load(path)` does not read the pixels, so the file must stay unchanged and present until you are done with the image (see [sources and lifetime](guide.md#sources-and-lifetime)). To detach from the file, load from a `Stream` or `byte[]`.

Coordinates and rectangles are `int x, int y, int width, int height` (or `ImageRect`), zero-based, right and bottom edges exclusive.

## Side by side

### Load, resize, save

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Mutate(x => x.Resize(300, 0)); // 0 = keep aspect ratio
image.Save(output);
```

System.Drawing (not compiled here):

```csharp
using var src = new Bitmap(input);
using var dst = new Bitmap(300, src.Height * 300 / src.Width);
using (var g = Graphics.FromImage(dst))
{
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.DrawImage(src, 0, 0, dst.Width, dst.Height);
}
dst.Save(output, ImageFormat.Png);
```

OneImage:

```csharp
Image.Load(input).Resize(width: 300).Save(output);
```

`Resize` takes optional `width` and `height`; leave one out to keep the aspect ratio. The output format comes from the extension of `output`.

### Thumbnail that fills a box

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(200, 200), Mode = ResizeMode.Crop, Position = AnchorPositionMode.Center }));
image.Save(output);
```

OneImage:

```csharp
Image.Load(input).Resize(width: 200, height: 200, fit: Fit.Cover, anchor: Anchor.Center).Save(output);

// Or the one-liner: upright, never enlarged, sRGB, without EXIF/GPS.
Image.Thumbnail(input, 200, 200, fit: Fit.Cover).Save(output);
```

| ImageSharp `ResizeMode` | OneImage |
|---|---|
| `Max` | `Fit.Contain` (default), also spelled `Fit.Max` |
| `Crop` | `Fit.Cover`, also spelled `Fit.Crop` |
| `Stretch` | `Fit.Stretch` |
| `Pad` | `Fit.Pad` (with `background:`) |
| `BoxPad` | `Fit.BoxPad`: like `Pad`, but an image that already fits keeps its size |
| `Min` | `Fit.Min`: covers the box without cropping, never enlarges |
| `Manual` with `TargetRectangle` | `ResizeInto(canvasWidth, canvasHeight, new ImageRect(x, y, width, height))` |

Sizes and placement match ImageSharp 3.1.12 in the cases we compared (all of them within one pixel, nearly all identical). Three small differences remain: a derived side that lands exactly on .5 rounds up here and to even in ImageSharp, an odd leftover when centering goes to the bottom/right here and to the top/left there, and `Min` or `Max` with only one side given use that side exactly.

`Resize` enlarges unless you pass `upscale: false`; `Image.Thumbnail` never enlarges unless you pass `upscale: true`. ImageSharp's `KnownResamplers` map to `ResamplingFilter`: `Lanczos3`, `CatmullRom`, `Bicubic`≈`CatmullRom`, `Triangle` → `Bilinear`, `NearestNeighbor` → `Nearest`, and `Hermite`, `Lanczos5`, `Lanczos8`, `Robidoux`, `RobidouxSharp`, `Spline` and `Welch` have the same names (ResamplingFilter.Bicubic and .Triangle exist as aliases). Our resize output with these is within 0.3 of a level on average of ImageSharp's. The default `Auto` picks Lanczos3 when shrinking and Catmull-Rom when enlarging.

### Crop

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Mutate(x => x.Crop(new Rectangle(10, 10, 100, 100)));
image.Save(output);
```

System.Drawing (not compiled here):

```csharp
using var src = new Bitmap(input);
using var cropped = src.Clone(new Rectangle(10, 10, 100, 100), src.PixelFormat);
cropped.Save(output);
```

OneImage:

```csharp
Image.Load(input).Crop(10, 10, 100, 100).Save(output);
```

The rectangle must lie fully inside the image; `Pad` first if you want to extract beyond an edge. Tiles outside the rectangle are never decoded.

### Rotate and flip

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Mutate(x => x.Rotate(90).Flip(FlipMode.Horizontal));
image.Save(output);
```

System.Drawing (not compiled here):

```csharp
using var bmp = new Bitmap(input);
bmp.RotateFlip(RotateFlipType.Rotate90FlipX);
bmp.Save(output);
```

OneImage:

```csharp
Image.Load(input).Rotate(90).Flip(FlipAxis.Horizontal).Save(output);
```

Both libraries rotate clockwise for positive angles. Multiples of 90 are exact pixel moves. Other angles grow the canvas to the bounding box (`expand: true`); pass `background:` for the corners. System.Drawing's EXIF auto-rotation, which you did by hand, is the default at load here.

### Perspective transforms

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Mutate(x => x.Transform(new ProjectiveTransformBuilder().AppendTaper(TaperSide.Left, TaperCorner.Both, 0.6f)));
image.Save(output);
```

OneImage:

```csharp
Image.Load(input)
    .Perspective(new ProjectiveTransformBuilder().AppendTaper(TaperSide.Left, TaperCorner.Both, 0.6))
    .Save(output);

// Pin the four corners (top-left, top-right, bottom-right, bottom-left) to chosen points:
Image.Load(input).Perspective([(30, 10), (150, 40), (120, 130), (10, 100)]).Save(output);

// Or a 3x3 matrix directly (column vectors; the top two rows are the same as Affine's a, b, tx and c, d, ty):
Image.Load(input).Perspective(new ProjectiveMatrix(1, 0.1, 0, 0, 1, 0, 0.001, 0, 1)).Save(output);
```

`ProjectiveTransformBuilder` has the same steps as ImageSharp's (`AppendTaper`, `AppendRotationDegrees`, `AppendScale`, `AppendSkewDegrees`, `AppendTranslation`, `AppendMatrix` and the `Prepend` forms) and takes `double` rather than `float`. Tapers give the same corner positions as ImageSharp's, and the default Catmull-Rom output is within about 1.5 levels on average of ImageSharp's `Transform` on smooth images. Differences to know about:

- ImageSharp's matrices are `Matrix4x4` with row vectors; `ProjectiveMatrix` is 3×3 with column vectors. To convert: `M11 = m11, M12 = m21, M13 = m41, M21 = m12, M22 = m22, M23 = m42, M31 = m14, M32 = m24, M33 = m44`.
- The output is the bounding box of the transformed image with its top-left corner at the origin (`expand: true`, as in `Rotate` and `Affine`), so a translation does not shift the picture; ImageSharp keeps the offset. Pass `expand: false` to apply the matrix in the input frame.
- A matrix that sends part of the image through the horizon (where `w` is zero) throws `ImageOperationException` instead of producing a picture.
- Interpolation runs in linear light on premultiplied alpha by default; pass `linear: false` to filter stored values as ImageSharp does.

### Convert format and set quality

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Save(output, new JpegEncoder { Quality = 85 });
image.SaveAsWebp(output + ".webp", new WebpEncoder { Quality = 80 });
```

System.Drawing (not compiled here):

```csharp
using var bmp = new Bitmap(input);
var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
var p = new EncoderParameters(1);
p.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
bmp.Save(output, jpeg, p);
```

OneImage:

```csharp
Image image = Image.Load(input);
image.Save("out.jpg", new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = 85 } });
image.Save("out.webp", new SaveOptions { EncoderOptions = new WebPEncoderOptions { Quality = 80 } });
```

| Setting | OneImage |
|---|---|
| JPEG quality / subsampling / progressive | `JpegEncoderOptions.Quality`, `.Subsampling`, `.Progressive` |
| PNG compression / filter | `PngEncoderOptions.CompressionLevel`, `.Filter`, `.ColorMode` |
| WebP lossless / quality / effort | `WebPEncoderOptions.Lossless`, `.Quality`, `.Effort` |
| GIF palette / dither | `GifEncoderOptions.MaxColors`, `.Dither` |
| TIFF compression | `TiffEncoderOptions.Compression` |

Save to a stream with the format named: `image.Save(stream, JpegFormat.Instance, options)`. There is no format sniffing from the stream; there is from the file extension.

### Pixel access

ImageSharp:

```csharp
using Image<Rgba32> image = Image.Load<Rgba32>(input);
image.ProcessPixelRows(accessor =>
{
    Span<Rgba32> row = accessor.GetRowSpan(0);
    for (int x = 0; x < row.Length; x++)
    {
        row[x].G = 0;
    }
});
```

System.Drawing (not compiled here): `bmp.GetPixel`/`SetPixel`, or `LockBits` for speed.

OneImage:

```csharp
Image image = Image.Load(input);
Rgba32[] pixels = image.ToArray<Rgba32>();     // read: any pixel struct, converted from the image's format

using Canvas canvas = image.ToCanvas();        // edit
using (PixelLock<Rgb24> l = canvas.Lock<Rgb24>(new ImageRect(0, 0, canvas.Width, 1), LockAccess.ReadWrite))
{
    Span<Rgb24> row = l.GetRow(0);
    for (int x = 0; x < row.Length; x++)
    {
        row[x] = row[x] with { G = 0 };
    }
}
Image edited = canvas.Snapshot();
```

Two things to know. `Image.ToArray<T>()` and `CopyPixelsTo<T>()` convert to `T`, but `Canvas.Lock<T>` requires `T` to match the canvas format exactly (a decoded JPEG is `PixelFormat.Rgb8`, whose struct is `Rgb24`). Check `canvas.Format`, or build the canvas with `new Canvas(w, h, PixelFormat.Rgba8)`. And pixel structs are immutable records: assign a new value (`row[x] = row[x] with { G = 0 }`) rather than writing a field.

For a whole-image edit that is a per-pixel function, prefer the built-in ops (`Invert`, `Levels`, `ChannelMixer`, ...), which are vectorized and lazy.

### Draw an image on another (watermark)

ImageSharp:

```csharp
using Image image = Image.Load(input);
using Image overlay = Image.Load(mark);
image.Mutate(x => x.DrawImage(overlay, new Point(20, 20), 0.8f));
image.Save(output);
```

System.Drawing (not compiled here):

```csharp
using var bmp = new Bitmap(input);
using var overlay = new Bitmap(mark);
using (var g = Graphics.FromImage(bmp))
{
    var cm = new ColorMatrix { Matrix33 = 0.8f };
    var attrs = new ImageAttributes();
    attrs.SetColorMatrix(cm);
    g.DrawImage(overlay, new Rectangle(20, 20, overlay.Width, overlay.Height), 0, 0, overlay.Width, overlay.Height, GraphicsUnit.Pixel, attrs);
}
bmp.Save(output);
```

OneImage:

```csharp
Image.Load(input).Composite(Image.Load(mark), x: 20, y: 20, opacity: 0.8f).Save(output);
```

`Composite` is source-over with premultiplied alpha and color management by default. The overlay can be negative-offset or partly outside the base. The overlay is itself an `Image`, so resize or fade it first with the normal ops.

ImageSharp's `DrawImage(image, point, PixelColorBlendingMode, opacity)` and `GraphicsOptions.AlphaCompositionMode` map to the `blend:` and `alpha:` parameters of `Composite`:

```csharp
// ImageSharp: img.Mutate(c => c.DrawImage(mark, new Point(20, 20), PixelColorBlendingMode.Multiply, 0.8f));
Image.Load(input).Composite(Image.Load(mark), x: 20, y: 20, opacity: 0.8f, blend: BlendMode.Multiply).Save(output);
```

| ImageSharp | OneImage |
|---|---|
| `PixelColorBlendingMode` Normal, Multiply, Add, Subtract, Screen, Darken, Lighten, Overlay, HardLight | `BlendMode` with the same names. The values agree with ImageSharp within a few levels at 8 bits (ImageSharp rounds through 8-bit intermediates). |
| `PixelAlphaCompositionMode` SrcOver, Clear, Src, SrcAtop, SrcIn, SrcOut, Dest, DestAtop, DestIn, DestOut, DestOver, Xor | `AlphaComposition` with the same names. Like `DrawImage`, only the pixels under the overlay change. A base without alpha accepts only the operators that keep it opaque (SrcOver, SrcAtop, Dest, DestOver). |
| (not in ImageSharp) | Also ColorDodge, ColorBurn, SoftLight, Difference, Exclusion, LinearBurn, LinearDodge, VividLight, LinearLight, PinLight, HardMix, Divide, Hue, Saturation, Color and Luminosity. |

### Metadata

ImageSharp:

```csharp
using Image image = Image.Load(input);
string? make = null;
if (image.Metadata.ExifProfile != null && image.Metadata.ExifProfile.TryGetValue(ExifTag.Make, out IExifValue<string>? value))
{
    make = value.Value;
}
image.Metadata.ExifProfile = null; // strip EXIF
image.Save(output);
```

OneImage:

```csharp
Image image = Image.Load(input);
string? make = image.Metadata.ReadExif()?.Make;      // typed: Orientation, Make, Model, CaptureTime, Gps
image.StripMetadata().Save(output);                  // EXIF, XMP, IPTC and ICC removed; StripMetadata(keepIcc: true) keeps the profile
```

OneImage parses only the common EXIF tags. The raw `Exif`, `Xmp`, `Iptc` and `IccProfile` blobs are always available on `Image.Metadata` if you need others. Metadata is preserved by default and corrected for the saved size and orientation. System.Drawing exposes EXIF through `PropertyItems`; there is no direct equivalent API.

### Blur, sharpen, median, edges, histogram

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Mutate(x => x.GaussianBlur(2f));
image.Mutate(x => x.BoxBlur(3));
image.Mutate(x => x.GaussianSharpen(1.2f));
image.Mutate(x => x.MedianBlur(1, true));
image.Mutate(x => x.DetectEdges());
image.Save(output);

// Histogram: count it yourself.
var counts = new long[256];
image.ProcessPixelRows(accessor =>
{
    for (int y = 0; y < accessor.Height; y++)
    {
        foreach (Rgba32 p in accessor.GetRowSpan(y)) counts[p.R]++;
    }
});
```

OneImage:

```csharp
Image.Load(input)
    .GaussianBlur(2)
    .BoxBlur(3)
    .Sharpen(amount: 1, radius: 1.2)
    .Median(1)
    .Sobel()
    .Save(output);

long[] red = Image.Load(input).Histogram().Channel(0).ToArray();   // 256 bins; Statistics() gives min/max/mean/stddev
```

| ImageSharp | OneImage | Differences |
|---|---|---|
| `GaussianBlur(sigma)` | `GaussianBlur(sigma)` | Linear light by default (`linear: false` for stored values); edge mode defaults to clamp. |
| `BoxBlur(radius)` | `BoxBlur(radius)` | Same window `2r+1`. |
| `GaussianSharpen(sigma)` | `GaussianSharpen(sigma)` | The same kernel (see [Effects](#effects)). `Sharpen(amount, radius, threshold)` is a different operator, an unsharp mask with an `amount` and a `threshold`; its default is linear light, pass `linear: false` for the look most editors have. |
| `MedianBlur(radius, preserveAlpha)` | `Median(radius)` | Alpha is filtered like a color channel; there is no `preserveAlpha`. |
| `DetectEdges()` (Sobel by default) | `DetectEdges()` or `Sobel(scale)` | `DetectEdges` is ImageSharp's operator, unscaled (see [Effects](#effects)); `Sobel(scale)` divides by 4 so that a black-to-white step gives full scale, and always returns a gray image without alpha. |
| custom convolution | `Convolve(float[,] kernel, divisor, offset)` | Correlation (not flipped); divisor defaults to the sum of the weights. |
| none built in | `Histogram(bins)`, `Statistics()` | Per channel, whole frame 0, computed in parallel. |

### Effects

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Mutate(x => x.Pixelate(8));
image.Mutate(x => x.OilPaint(10, 15));
image.Mutate(x => x.Vignette(Color.Black));
image.Mutate(x => x.Glow(Color.White));
image.Mutate(x => x.BokehBlur(32, 2, 3f));
image.Mutate(x => x.GaussianSharpen(1.2f));
image.Mutate(x => x.DetectEdges(KnownEdgeDetectorKernels.Kirsch));
image.Mutate(x => x.EntropyCrop(0.5f));
image.Save(output);
```

OneImage:

```csharp
Image.Load(input)
    .Pixelate(8)
    .OilPaint(levels: 10, brushSize: 8)
    .Vignette(new Rgba32(0, 0, 0, 255))
    .Glow(new Rgba32(255, 255, 255, 255))
    .BokehBlur(radius: 32, components: 2, gamma: 3)
    .GaussianSharpen(1.2)
    .DetectEdges(EdgeDetector.Kirsch)
    .EntropyCrop(0.5f)
    .Save(output);
```

| ImageSharp | OneImage | Differences |
|---|---|---|
| `Pixelate(size)` | `Pixelate(size)` | Identical output: each block takes the color of the pixel at its center (not an average), in any pixel format. |
| `OilPaint(levels, brushSize)` | `OilPaint(levels, brushSize)` | ImageSharp's window reaches only up and left of the pixel and is `brushSize / 2 + 1` wide, which shifts the picture by about a quarter of the brush. OneImage centers a full `brushSize` window, so its strokes are about twice as wide for the same number and nothing shifts; use about half the brush size for a similar look. Where two intensity bins tie, ImageSharp keeps the one reached first while scanning and OneImage the brighter. |
| `Vignette(color, radiusX, radiusY, rectangle)` | `Vignette(color, radiusX, radiusY, area)` | Same falloff (within 1 level). Radii of 0 mean half the area's width and height, as in ImageSharp. Gray and RGB images only. |
| `Glow(color, radius, rectangle)` | `Glow(color, radius, area)` | Same falloff (within 1 level), and the same default color: black, which darkens the center. |
| `BokehBlur(radius, components, gamma)` | `BokehBlur(radius, components, gamma)` | Same complex-kernel family and defaults (32, 2, 3), computed on the stored premultiplied values like ImageSharp. With `gamma: 1` the results agree within 1 level; with a higher gamma, nearly black channels can differ by up to 20 levels, elsewhere 1 level. |
| `GaussianSharpen(sigma)` | `GaussianSharpen(sigma)` | Same kernel and defaults (sigma 3), within 2 levels. It filters stored values like ImageSharp; pass `linear: true` for linear light. |
| `DetectEdges(KnownEdgeDetectorKernels.X, grayscale)` | `DetectEdges(EdgeDetector.X, grayscale)` | Same kernels (Sobel, Prewitt, Scharr, RobertsCross, Kayyali, Laplacian3x3, Laplacian5x5, LaplacianOfGaussian, Kirsch, Robinson), same results to the level. With `grayscale: true` (the default) the result is a gray image without alpha, where ImageSharp returns an RGB image of equal channels. |
| `EntropyCrop(threshold)` | `EntropyCrop(threshold)` | Same rectangle. Eager, like `Trim`; an image with no detail is returned as it is. |

### Color matrix, preset looks, color blindness, opacity

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Mutate(x => x.Sepia(0.8f));
image.Mutate(x => x.Kodachrome());
image.Mutate(x => x.ColorBlindness(ColorBlindnessMode.Deuteranopia));
image.Mutate(x => x.Opacity(0.5f));
image.Mutate(x => x.Filter(new ColorMatrix(
    1f, 0f, 0f, 0f,
    0f, 1f, 0f, 0f,
    0f, 0f, 1f, 0f,
    0f, 0f, 0f, 1f,
    0.1f, 0f, 0f, 0f)));
image.Save(output);
```

OneImage:

```csharp
Image.Load(input)
    .Sepia(0.8f)
    .Kodachrome()
    .ColorBlindness(ColorBlindnessMode.Deuteranopia)
    .Opacity(0.5f)
    .ApplyColorMatrix(new ColorMatrix(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 0f, 0f, 1f,
        0.1f, 0f, 0f, 0f))
    .Save(output);
```

`ColorMatrix` has ImageSharp's layout (`M11`..`M54`, the pixel as a row vector, row 5 the offsets), so a matrix copies over value for value. The presets use ImageSharp's matrices, and the 8-bit results agree with ImageSharp to within one step (checked in the test suite).

| ImageSharp | OneImage | Differences |
|---|---|---|
| `Filter(ColorMatrix)` | `ApplyColorMatrix(ColorMatrix)` | Works on straight color on the stored values, like ImageSharp. Integer results clamp; float results are not clamped (alpha always is). Gray images become RGB. |
| `Sepia(amount)`, `BlackWhite()`, `Kodachrome()` | `Sepia(amount)`, `BlackWhite()`, `Kodachrome()` | Same matrices. `Sepia(0)` returns the image unchanged. `BlackWhite` is a hard, high-contrast matrix, not a luminance conversion; use `Grayscale` for that. |
| `Lomograph()`, `Polaroid()` | `Lomograph()`, `Polaroid()` | The full looks, within one level of ImageSharp at 8 bits: `Lomograph` is the matrix plus a dark-green `Vignette`; `Polaroid` is the matrix plus a dark-orange `Vignette` and an orange `Glow`. The matrix alone is `ApplyColorMatrix(ColorMatrix.Lomograph)` / `ColorMatrix.Polaroid`, which are the same values as ImageSharp's `KnownFilterMatrices` and have no vignette. |
| `ColorBlindness(mode)` | `ColorBlindness(mode)` | Same eight modes and matrices. |
| `Opacity(amount)` | `Opacity(amount)` | Multiplies alpha. An image without alpha gains an alpha channel. |
| `KnownFilterMatrices` | `ColorMatrix.Sepia(amount)`, `.BlackWhite`, `.Kodachrome`, `.Lomograph`, `.Polaroid`, `.ColorBlindness(mode)`, `.Opacity(amount)` | Matrices as values you can inspect, multiply (`a * b` applies `a`, then `b`) and pass to `ApplyColorMatrix`. |

These ops fuse with the other color ops (`Gamma`, `Levels`, `Invert`, ...) into one pass over each tile, so `Sepia().BrightnessContrast(...).Opacity(...)` reads the pixels once.

### Histograms, thresholds and dithering

ImageSharp:

```csharp
using Image image = Image.Load(input);
image.Mutate(x => x.HistogramEqualization(new HistogramEqualizationOptions { Method = HistogramEqualizationMethod.AutoLevel }));
image.Mutate(x => x.AdaptiveThreshold(Color.White, Color.Black, 0.85f));
image.Mutate(x => x.BinaryDither(KnownDitherings.Bayer4x4));
image.Mutate(x => x.Dither(KnownDitherings.Stucki, new[] { Color.Black, Color.White, Color.Red }));
image.Mutate(x => x.Quantize(KnownQuantizers.Octree));
image.Save(output);
```

OneImage:

```csharp
Image.Load(input)
    .AutoLevel()
    .AdaptiveThreshold(new Rgba32(255, 255, 255, 255), new Rgba32(0, 0, 0, 255), 0.85f)
    .BinaryDither(Dither.Bayer4x4)
    .Dither([new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255), new Rgba32(255, 0, 0, 255)], Dither.Stucki)
    .Quantize(256, quantizer: Quantizer.Octree)
    .Save(output);
```

| ImageSharp | OneImage | Differences |
|---|---|---|
| `HistogramEqualization` with `Method = AutoLevel`, `SyncChannels` | `AutoLevel(syncChannels)` | A method of its own rather than an option value. Per-channel (`syncChannels: false`) is within 1 level of ImageSharp, synchronized (the default) within 2. |
| `AdaptiveThreshold(upper, lower, limit)` | `AdaptiveThreshold(upper, lower, thresholdLimit)` | Same Bradley window and defaults (white over black, 0.85). Identical on gray input; on photos a pixel or two on the border can differ (2 of 31,871 in the test image). |
| `BinaryDither(dither, upper, lower)` | `BinaryDither(dither, upper, lower)` | Same dithers, black and white by default. |
| `Dither(dither, palette)` | `Dither(palette, dither, scale, fastMatch)` | All the `KnownDitherings` map to `Dither` members: Atkinson, Burkes, JarvisJudiceNinke, Sierra2, Sierra3, SierraLite, StevensonArce, Stucki, FloydSteinberg, Bayer2x2, Ordered3x3, Bayer4x4, Bayer8x8, Bayer16x16. See below for exactness. |

How exact the dithers are:

- **Error diffusion** (all nine kernels) is bit-identical to ImageSharp with the default `fastMatch: true`. ImageSharp finds the nearest palette color through a cache keyed by the top five bits of each channel and keeps the first answer for a cell, so its result depends on the order colors are met. Pass `fastMatch: false` to answer for the exact color of every pixel instead, which is more accurate but slower (about 1.1× ImageSharp's time with the web palette on one thread).
- **Ordered dithers** use the same matrices and the same amplitude rule as ImageSharp. With small palettes the output is identical; with the web-safe and Werner palettes up to about 2.5% of pixels differ, from the same cache effect.
- **Threads.** Ordered dithering runs on all tiles in parallel. Error diffusion is a single pass in row order, so it uses one thread whatever the setting, and its output never depends on the thread count.
- `Dither` takes a palette as a list of `Rgba32`; a palette with one transparent entry works with opaque photos.
- ImageSharp's `CalculateIntegralImage` has no public counterpart; `AdaptiveThreshold` uses a window sum internally.

## Behavior to expect after porting

- **Orientation is applied for you.** If your old code called `AutoOrient()` or rotated by the EXIF tag, remove that, or the picture will be turned twice.
- **Resize quality differs slightly.** Resampling is done in linear light on premultiplied alpha, so edges and gradients can look a little different from a gamma-space resize. Pass `linear: false` to compare.
- **Errors surface later.** Wrap the `Save`, not only the `Load`, in your `try` block, or load with `Validate = true`.
- **There is nothing to dispose** on an `Image`; delete `using` statements and let `Canvas` carry the `using`.
- **Operations are cheap to build and run once.** Don't save an intermediate just to reuse it: keep the `Image` and derive more images from it. Each save recomputes its own path, though shared tiles are cached under the engine's memory budget.
- **Limits are on.** Very large or suspicious files throw `ImageLimitException`; raise the limits or use `ImageLimits.Unrestricted` for trusted data.

## Not in OneImage yet

This is the one list of what ImageSharp 3.1.x has that OneImage 1.0 does not. It was built by going through the public API of ImageSharp 3.1.12 (the version this guide is checked against) and, for shapes, text and the web middleware, the public API and XML documentation of SixLabors.ImageSharp.Drawing 2.1.7, SixLabors.Fonts 2.1.3 and SixLabors.ImageSharp.Web 3.2.0. These are the last releases of those packages that build without a license key, matching the ImageSharp reference version; newer releases may offer more. "Coming soon" means the work is planned and next in line; it is not a promise of a date. "Not planned" means we are not working on it and are not promising it.

Where OneImage has part of a feature, the table says what it covers and what it does not.

### Drawing and text: coming soon

Drawing is SixLabors.ImageSharp.Drawing 2.1.7; text layout and fonts are SixLabors.Fonts 2.1.3. OneImage 1.0 has neither.

| ImageSharp | OneImage today | Closest workaround |
|---|---|---|
| `Fill(...)` (whole image, rectangle, path, polygon, path collection), `Draw(...)` (rectangle, path, path collection), `DrawLine`, `DrawPolygon`, `DrawBeziers`, `DrawPath`, `FillPolygon`, `Clear(...)` | No paths, shapes, fills or strokes. | Write pixels yourself through `Canvas.Lock`, or draw elsewhere and bring the result in with `Composite`. |
| Shapes and paths: `IPath`, `Path`, `PathBuilder` (lines, arcs, quadratic and cubic Béziers, SVG arc, figures, transforms), `Path.TryParseSvgPath`, `Polygon`, `ComplexPolygon`, `RectangularPolygon`, `RegularPolygon`, `Star`, `EllipsePolygon`, `PathCollection`; path `Translate`/`Scale`/`Rotate`/`Reverse`/`ComputeLength`; boolean `Clip` (`ClippingOperation`: union, intersection, difference, xor); `GenerateOutline` (stroke to outline) | None. | None. |
| Brushes: `SolidBrush`, `LinearGradientBrush`, `RadialGradientBrush`, `EllipticGradientBrush`, `PathGradientBrush` (all with `ColorStop`s and `GradientRepetitionMode` None/Repeat/Reflect/DontFill where applicable), `PatternBrush` and the `Brushes` hatch presets (horizontal, vertical, diagonals, 10%, 20%, min), `ImageBrush`, `RecolorBrush` | None. | A gradient can be generated into a `Canvas` and composited. |
| Pens: `Pen`, `SolidPen`, `PatternPen`, `Pens` (solid, dash, dot, dash-dot, dash-dot-dot), `PenOptions` (width, brush fill, dash pattern, `JointStyle` miter/round/square, `EndCapStyle` butt/round/square/joined/polygon) | None. | None. |
| Clipping and options: `Clip(path, action)` (apply any processors only inside a path), `DrawingOptions` (`GraphicsOptions`, `ShapeOptions`, `Transform`), `ShapeOptions.IntersectionRule` (non-zero, even-odd) and `ClippingOperation`, `GraphicsOptions` anti-aliasing and blending | None. | None. |
| `DrawText` (string or `RichTextOptions`, with a `Brush`, a `Pen` or both, also along a path with `RichTextOptions.Path`), `RichTextRun` (per-run brush, pen, underline, strikeout and overline pens), `TextBuilder.GenerateGlyphs` (text as a path) | No fonts and no text rendering. | Render the text with another tool and `Composite` the result. |
| Fonts: `Font`, `FontFamily`, `FontCollection`, `SystemFonts`; TrueType, OpenType with CFF outlines, TrueType collections and WOFF/WOFF2 files; `FontStyle` and `TextAttributes` (sub/superscript), `TextDecorations` | No font parsing. | None. |
| Text layout: `TextOptions` (origin, wrapping length, line spacing, tab width, horizontal and vertical alignment, `TextAlignment`, `TextJustification` inter-word/inter-character, `KerningMode`, `HintingMode`, `WordBreaking`, `Dpi`, `LayoutMode` horizontal and vertical modes, `FeatureTags`), `TextMeasurer`/`TextRenderer`/`TextLayout` | None. | None. |
| Complex text in SixLabors.Fonts 2.1.3: OpenType GSUB/GPOS shaping (ligatures, kerning, marks), Arabic and Indic shapers, the Unicode bidirectional algorithm (`TextDirection` auto/LTR/RTL) and line breaking, font fallback (`TextOptions.FallbackFontFamilies`), color fonts (`ColorFontSupport.MicrosoftColrFormat`: COLR/CPAL) | None. | None. |

Not in Fonts 2.1.3 either, so not part of the gap: CFF2 outlines (rejected with "CFF2 Fonts are not currently supported") and a public API for variable-font axes (the package lists the `rvrn` feature tag but exposes no axis settings). Also not covered by the colour-font option: SVG, `sbix`, CBDT and other bitmap colour glyphs.

### Effects and filters: coming soon

| ImageSharp | OneImage today | Closest workaround |
|---|---|---|
| `HistogramEqualization(options)` (global, adaptive tile, sliding window) | None. `Histogram` gives the data; `AutoLevel` covers the auto-level method. | `Levels` or `Curves` with values you compute from `Histogram`. |
| `CalculateIntegralImage()` | None as a public API (`AdaptiveThreshold` sums windows internally). | `Histogram` and `Statistics` cover the common uses. |

`BackgroundColor` is covered by `Flatten(color)`, `Skew` and `AffineTransformBuilder` by `Affine(a, b, c, d, tx, ty)`, and `ProjectiveTransformBuilder` by `Perspective` (see [Perspective transforms](#perspective-transforms)). `Rotate`, `Pad`, `Trim`, `Grayscale`, `Invert`, `GaussianBlur`, `BoxBlur`, `Median`, `Convolve`, brightness/contrast, hue/saturation/lightness and the rest of the list under [Side by side](#side-by-side) do exist.

### Web middleware

SixLabors.ImageSharp.Web 3.2.0 (it depends on ImageSharp 3.1.11) maps to the separate `RecursiveAI.OneImage.Web` package. [The web guide](web.md) has the details; the short version:

| ImageSharp.Web | OneImage.Web |
|---|---|
| `AddImageSharp()` / `UseImageSharp()` | `AddOneImage()` / `UseOneImage()`. The builder offers `AddProvider`, `ClearProviders`, `SetCache`, `SetCacheKey`, `SetCacheHash`, `Configure` and `ConfigureCache`. |
| Query commands `width`, `height`, `xy`, `rmode`, `ranchor`, `rsampler`, `color`, `bgcolor`, `orient`/`autoorient`, `compand`, `format`, `quality`; `PresetOnlyQueryCollectionRequestParser` | All accepted (alongside shorter names such as `w`, `h`, `fit`, `q`); `PresetsOnly = true`. `rmode=manual` and the samplers `welch`, `hermite`, `spline`, `robidoux`, `lanczos5`, `lanczos8` are rejected with 400. Call `options.UseImageSharpDefaults()` to get ImageSharp.Web's default of cropping and enlarging when a width and height are given. |
| `PhysicalFileSystemProvider`, `WebRootImageProvider`, `FileProviderImageProvider`, `IImageProvider`, `ProcessingBehavior` | `PhysicalFileImageProvider`, `WebRootImageProvider`, `FileProviderImageProvider`, `IImageProvider` (a simpler interface: `Match` and `GetAsync` returning a source with a modified time, a length and a stream), `ProcessingBehavior`. |
| `PhysicalFileSystemCache`, `IImageCache`, `ICacheKey`, `ICacheHash`, `CacheFolderDepth`, `CacheHashLength`, `BrowserMaxAge`, `CacheMaxAge`, conditional requests | Same names and meanings. The default cache folder is `oneimage-cache` in the content root rather than under `wwwroot`, the default hash length is 32, and ETags are known without reading the cache. There is no in-memory resolver cache. |
| `HMACSecretKey`, `HMACUtilities`, `ImageTagHelper`, `HmacTokenTagHelper` | `HmacSecretKey`, `HmacUtilities`, `ImageUrlSigner` and `ImageTagHelper` (`oi-width`, `oi-format` and so on; it adds the token itself). Tokens are not interchangeable with ImageSharp.Web's; regenerate signed URLs. A request that needs a token and lacks one gets 403. |
| `OnParseCommandsAsync`, `OnBeforeSaveAsync`, `OnProcessedAsync`, `OnPrepareResponseAsync`; custom `IImageWebProcessor` | The same four hooks (`OnBeforeLoadAsync` and `OnComputeHMAC` are not offered). A custom processor becomes work on the lazy `Image` in `OnBeforeSaveAsync`. |

Cloud storage providers (Azure Blob, AWS S3) and cache are not in the 3.2.0 package; they ship as separate ImageSharp.Web provider packages, which this comparison did not check.

### Formats: not planned

ImageSharp reads and writes more formats than OneImage. We are not working on these and are not promising them.

| ImageSharp | OneImage today |
|---|---|
| `TgaDecoder` / `TgaEncoder` (TGA) | Not supported. |
| `PbmDecoder` / `PbmEncoder` (PBM, PGM, PPM, PFM) | Not supported. |
| `QoiDecoder` / `QoiEncoder` (QOI) | Not supported. |

OneImage reads and writes PNG, JPEG, WebP, GIF, TIFF, BMP and ICO. ImageSharp 3.1.12 has no CUR support, so there is nothing to port there. Formats neither library has (AVIF, JPEG XL, HEIC, camera RAW, PSD, SVG, PDF) are listed on the [comparison page](comparison.md#not-in-oneimage-yet).

### Programming model

| ImageSharp | OneImage today | Closest workaround |
|---|---|---|
| `IImageProcessor` and `ApplyProcessors` plug-ins | No public processor interface. | Transform pixels after `ToArray`, or write into a `Canvas`. |
| `ProcessPixelRows`, `ProcessPixelRowsAsVector4`, `PixelRowOperation` | No row-callback API. | `Canvas.Lock` gives row spans. |
| Typed IPTC and XMP editing (`IptcProfile`, `XmpProfile`) | The blobs are kept and can be stripped; they cannot be edited tag by tag. | Edit the blob with another tool. |
| Pixel types beyond OneImage's list (`Bgra32`, `Argb32`, `Bgr24`, `NormalizedByte4`, `Short4`, `HalfVector4`, ...) | `Rgb24`, `Rgba32`, `Rgb48`, `Rgba64`, `RgbaHalf`, `RgbFloat`, `RgbaFloat`, `Gray8`, `Gray16`, `GrayFloat`, `GrayAlpha8`. | Convert at the edge with `CopyPixelsTo<T>`. |

### Not in either library

Layers, masks and groups with Photoshop blend modes, PSD, and vector formats are further out than the items above and are not in ImageSharp either.

## Drawing shapes

Filling shapes with a solid color maps closely to ImageSharp.Drawing; the names and the differences (default fill rule, non-anti-aliased edge rule, pixel-coordinate conventions) are in [drawing.md](drawing.md). Strokes, gradient and pattern brushes, clipping and text are not available yet.
