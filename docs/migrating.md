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

| ImageSharp `ResizeMode` | OneImage `Fit` |
|---|---|
| `Max` | `Contain` (default) |
| `Crop` | `Cover` |
| `Stretch` | `Stretch` |
| `Pad`, `BoxPad` | `Pad` (with `background:`) |
| `Min` | no direct equivalent; `Cover` with `anchor` comes closest |

`Resize` enlarges unless you pass `upscale: false`; `Image.Thumbnail` never enlarges unless you pass `upscale: true`. ImageSharp's `KnownResamplers` map to `ResamplingFilter`: `Lanczos3`, `CatmullRom`, `Bicubic`≈`CatmullRom`, `Triangle` → `Bilinear`, `NearestNeighbor` → `Nearest`. The default `Auto` picks Lanczos3 when shrinking and Catmull-Rom when enlarging.

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

`Composite` is source-over with premultiplied alpha and color management; there are no other blend modes yet. The overlay can be negative-offset or partly outside the base. The overlay is itself an `Image`, so resize or fade it first with the normal ops.

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
| `GaussianSharpen(sigma)` | `Sharpen(amount, radius, threshold)` | An unsharp mask with a Gaussian of sigma `radius`, so there is an `amount` and a `threshold`. The default is linear light; pass `linear: false` for the look most editors have. |
| `MedianBlur(radius, preserveAlpha)` | `Median(radius)` | Alpha is filtered like a color channel; there is no `preserveAlpha`. |
| `DetectEdges()` (Sobel by default) | `Sobel(scale)` | Returns a gray image without alpha. Other edge kernels: use `Convolve`. |
| custom convolution | `Convolve(float[,] kernel, divisor, offset)` | Correlation (not flipped); divisor defaults to the sum of the weights. |
| none built in | `Histogram(bins)`, `Statistics()` | Per channel, whole frame 0, computed in parallel. |

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

| ImageSharp | As of 1.0 | Closest workaround |
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

### Blending: coming soon

| ImageSharp | As of 1.0 | Closest workaround |
|---|---|---|
| `DrawImage(image, point, PixelColorBlendingMode, opacity)` with `Multiply`, `Add`, `Subtract`, `Screen`, `Darken`, `Lighten`, `Overlay`, `HardLight` | `Composite(overlay, x, y, opacity)` does normal source-over only. | Do the blend per pixel with `Canvas.Lock` on both images. |
| `PixelAlphaCompositionMode` (`SrcAtop`, `SrcIn`, `SrcOut`, `Dest`, `DestOver`, `DestIn`, `DestOut`, `DestAtop`, `Clear`, `Xor`; `SrcOver` is the default) | Source-over only. | As above. |
| `Opacity(amount)` (multiply every pixel's alpha) | No single call. `AddAlpha(alpha)` sets a constant alpha on an opaque image, and `Composite` takes an `opacity`. | `Composite` the image onto a transparent `Canvas` with the opacity you want. |

### Effects and filters: coming soon

| ImageSharp | As of 1.0 | Closest workaround |
|---|---|---|
| `Pixelate(size)` | None. | `Resize` down with `filter: ResamplingFilter.Box`, then back up with `ResamplingFilter.Nearest`. |
| `OilPaint(levels, brushSize)` | None. | None; per-pixel code on a `Canvas`. |
| `Vignette(...)`, `Glow(...)` (overlays) | None. | Draw the radial falloff into a `Canvas` and `Composite` it. |
| `BokehBlur(radius, components, gamma)` | None. `GaussianBlur` and `BoxBlur` exist. | `Convolve` with a disc-shaped kernel gives a plain (non-bokeh) lens blur. |
| `Sepia`, `Kodachrome`, `Lomograph`, `Polaroid`, `BlackWhite` preset filters | None. | `ChannelMixer` can apply the sepia matrix; `Grayscale` followed by `Threshold` approximates `BlackWhite`. |
| `ColorBlindness(mode)` (Protanopia, Deuteranopia, Tritanopia, Achromatopsia and the "-omaly" modes) | None. | `ChannelMixer` with the simulation matrix you want. |
| `Filter(ColorMatrix)` and `KnownFilterMatrices` | No general 4×4/5×4 color matrix. | `ChannelMixer` takes a 3×3 or 3×4 RGB matrix; there is no alpha row. |
| `HistogramEqualization(options)` (global, adaptive tile, sliding window, `AutoLevel`) | None. `Histogram` gives the data. | `Levels` or `Curves` with values you compute from `Histogram`. |
| `AdaptiveThreshold(...)` | None. `Threshold(level)` is a global threshold. | None. |
| `BinaryDither(...)` and `Dither(..., palette)` with `KnownDitherings` (Atkinson, Burkes, JarvisJudiceNinke, Sierra2/3/Lite, Stucki, StevensonArce, Bayer2x2/4x4/8x8/16x16, Ordered3x3) | `Quantize(colors, dither)` offers none, Floyd–Steinberg and an 8×8 ordered dither, and picks its own palette. | Quantize to 2 colors for a bilevel result. A caller-supplied palette and the other matrices are not available. |
| `Quantize(KnownQuantizers.Octree / WebSafe / Werner, ...)` | One built-in quantizer; no `PaletteQuantizer`, web-safe or Werner palettes. | `Quantize(colors, ...)`. |
| `GaussianSharpen(sigma)` | `Sharpen` is an unsharp mask, not the same kernel. | `Sharpen(...)`. |
| `DetectEdges(...)` with Kayyali, Kirsch, Laplacian3x3/5x5, LaplacianOfGaussian, Prewitt, RobertsCross, Robinson, Scharr (and the compass variants) | `Sobel` only. | `Convolve` with the kernel you need. |
| `EntropyCrop(threshold)` | None. `Trim` removes a uniform border, and `Anchor.Smart` picks a crop by detail when resizing. | `Trim` for borders, `Resize(..., anchor: Anchor.Smart)` for content-aware crops. |
| `CalculateIntegralImage()` | None. | `Histogram` and `Statistics` cover the common uses. |

`BackgroundColor` is covered by `Flatten(color)`, `Skew` and `AffineTransformBuilder` by `Affine(a, b, c, d, tx, ty)`. `Rotate`, `Pad`, `Trim`, `Grayscale`, `Invert`, `GaussianBlur`, `BoxBlur`, `Median`, `Convolve`, brightness/contrast, hue/saturation/lightness and the rest of the list under [Side by side](#side-by-side) do exist.

### Transforms: coming soon

| ImageSharp | As of 1.0 | Closest workaround |
|---|---|---|
| `Transform(ProjectiveTransformBuilder)`: perspective, `AppendTaperMatrix`, arbitrary 3×3 projective matrices | None. `Affine` covers rotate, scale, skew and translate. | None for true perspective. |
| Resize extras: `ResizeMode.BoxPad` and `Min`/`Max`, `ResizeOptions.Sampler` choices `Hermite`, `Lanczos5`, `Lanczos8`, `Robidoux`, `RobidouxSharp`, `Spline`, `Welch` | `Fit` offers Contain, Cover, Stretch and Pad. Filters are nearest, box, bilinear, Catmull-Rom, Mitchell, Lanczos2 and Lanczos3. | Pick the closest filter; compute Min/Max sizes yourself and pass them. |

### Web middleware: coming soon

SixLabors.ImageSharp.Web 3.2.0 (it depends on ImageSharp 3.1.11).

| ImageSharp | As of 1.0 | Closest workaround |
|---|---|---|
| ASP.NET Core middleware: `AddImageSharp()` / `UseImageSharp()`, URL query commands handled by processors for resize (`width`, `height`, focal point, resize mode, anchor, sampler, pad color, orient, compand), `format`, `quality`, `bgcolor` and `autoorient` (`ResizeWebProcessor`, `FormatWebProcessor`, `QualityWebProcessor`, `BackgroundColorWebProcessor`, `AutoOrientWebProcessor`); custom processors, converters and request parsers; a preset-only parser (`PresetOnlyQueryCollectionRequestParser`) that accepts named presets instead of free commands | No ASP.NET Core package. | Call `Image.Thumbnail` or `Resize` in your own endpoint and cache the result. |
| Image providers: `PhysicalFileSystemProvider`, `WebRootImageProvider`, `FileProviderImageProvider` (any `IFileProvider`), custom `IImageProvider`, `ProcessingBehavior` (`CommandOnly` or `All`) | None. | None. |
| Caching: `PhysicalFileSystemCache` (on-disk, with a cache folder and folder depth), `IImageCache`, `ICacheKey` and `ICacheHash` (SHA-256), in-memory resolver and metadata caching, browser and cache max-age options, conditional requests | None. | Cache the saved output yourself. |
| Security and helpers: HMAC-signed URLs (a token command checked with `HMACSecretKey`; SHA-256, SHA-384 and SHA-512 helpers in `HMACUtilities`), `ImageTagHelper` (`<img>` Razor tag helper with width, height, format, quality and so on) and `HmacTokenTagHelper` (adds the token), request and response hooks (`OnParseCommandsAsync`, `OnBeforeLoadAsync`, `OnBeforeSaveAsync`, `OnProcessedAsync`, `OnPrepareResponseAsync`, `OnComputeHMAC`) | None. | None. |

Cloud storage providers (Azure Blob, AWS S3) and cache are not in the 3.2.0 package; they ship as separate ImageSharp.Web provider packages, which this comparison did not check.

### Formats: not planned

ImageSharp reads and writes more formats than OneImage. We are not working on these and are not promising them.

| ImageSharp | As of 1.0 |
|---|---|
| `TgaDecoder` / `TgaEncoder` (TGA) | Not supported. |
| `PbmDecoder` / `PbmEncoder` (PBM, PGM, PPM, PFM) | Not supported. |
| `QoiDecoder` / `QoiEncoder` (QOI) | Not supported. |

OneImage reads and writes PNG, JPEG, WebP, GIF, TIFF, BMP and ICO. ImageSharp 3.1.12 has no CUR support, so there is nothing to port there. Formats neither library has (AVIF, JPEG XL, HEIC, camera RAW, PSD, SVG, PDF) are listed on the [comparison page](comparison.md#not-in-oneimage-yet).

### Programming model

| ImageSharp | As of 1.0 | Closest workaround |
|---|---|---|
| `IImageProcessor` and `ApplyProcessors` plug-ins | No public processor interface. | Transform pixels after `ToArray`, or write into a `Canvas`. |
| `ProcessPixelRows`, `ProcessPixelRowsAsVector4`, `PixelRowOperation` | No row-callback API. | `Canvas.Lock` gives row spans. |
| Typed IPTC and XMP editing (`IptcProfile`, `XmpProfile`) | The blobs are kept and can be stripped; they cannot be edited tag by tag. | Edit the blob with another tool. |
| Pixel types beyond OneImage's list (`Bgra32`, `Argb32`, `Bgr24`, `NormalizedByte4`, `Short4`, `HalfVector4`, ...) | `Rgb24`, `Rgba32`, `Rgb48`, `Rgba64`, `RgbaHalf`, `RgbFloat`, `RgbaFloat`, `Gray8`, `Gray16`, `GrayFloat`, `GrayAlpha8`. | Convert at the edge with `CopyPixelsTo<T>`. |

### Not in either library

Layers, masks and groups with Photoshop blend modes, PSD, and vector formats are further out than the items above and are not in ImageSharp either.
