# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Vector drawing foundation (`OneImage.Drawing`): `Path`, `PathBuilder` (lines, quadratic and cubic Béziers, SVG elliptical arcs), shape factories (rectangle, rounded rectangle, ellipse, circle, polygon, regular polygon, star, combine), transforms, reverse, length, bounds, flattening, and strict bounded SVG path-data parsing (`Path.TryParseSvg`). `Image.Fill`, `Image.Clear`, `Canvas.Fill` and `Canvas.Clear` fill paths with a solid `Brush`: exact-area anti-aliasing (or none), non-zero and even-odd rules, blend modes, Porter-Duff operators, opacity, a transform, and linear-light blending. `Image` fills are lazy and a run of fills computes in one pass; `Canvas` fills draw in place in parallel with identical bytes. See `docs/drawing.md`. A fuzz target `svg` covers the path parser.
- `Image.Composite(..., blend:, alpha:)`: all 24 Photoshop/W3C blend modes (`BlendMode`: Multiply, Screen, Overlay, Darken, Lighten, Color Dodge/Burn, Hard/Soft/Vivid/Linear/Pin Light, Hard Mix, Difference, Exclusion, Add, Subtract, Linear Burn, Divide, Hue, Saturation, Color, Luminosity) and the twelve Porter-Duff operators (`AlphaComposition`). Normal source-over output is unchanged.
- `Image.Perspective`: a projective (perspective) transform from a `ProjectiveMatrix`, a `ProjectiveTransformBuilder` (taper, rotation, scale, skew, translation, matrices, with `Append` and `Prepend` forms) or four corner points. `ProjectiveMatrix` also builds quad-to-quad maps.
- `Fit.BoxPad` and `Fit.Min` (ImageSharp's `ResizeMode.BoxPad` and `Min`), the aliases `Fit.Max` and `Fit.Crop`, and `Image.ResizeInto` for resizing into a rectangle of a canvas (`ResizeMode.Manual`).
- Resampling filters `Hermite`, `Lanczos5`, `Lanczos8`, `Robidoux`, `RobidouxSharp`, `Spline` and `Welch`, with the aliases `Bicubic` and `Triangle`, in `Resize` and in the rotate, affine and perspective samplers.
- Neighbourhood effects, matching ImageSharp 3.1.12: `Pixelate`, `OilPaint`, `Vignette`, `Glow`, `BokehBlur`, `GaussianSharpen`, `DetectEdges` (`EdgeDetector`: Sobel, Prewitt, Scharr, RobertsCross, Kayyali, Laplacian3x3/5x5, LaplacianOfGaussian, Kirsch, Robinson) and `EntropyCrop`. `OilPaint` centers its window, where ImageSharp's is shifted; see the migration guide.
- Color matrix: `Image.ApplyColorMatrix(ColorMatrix)` with a public `ColorMatrix` (ImageSharp's 5x4 layout), and the looks built on it: `Sepia`, `BlackWhite`, `Kodachrome`, `Lomograph`, `Polaroid`, `ColorBlindness(ColorBlindnessMode)` and `Opacity`. `Lomograph` and `Polaroid` include ImageSharp's vignette and glow; `ColorMatrix.Lomograph` and `.Polaroid` are the plain matrices. They fuse with the other color ops into one pass per tile, and a color chain directly on a decode now runs on rows as they are decoded when the whole frame is read at once.
- `RecursiveAI.OneImage.Web`, a separate package with ASP.NET Core middleware for on-the-fly image processing (`AddOneImage()` / `UseOneImage()`): URL commands for size, fit, anchor and focal point, filter, background, format, quality and orientation, with ImageSharp.Web query names accepted too; presets and a presets-only mode; web root, folder, `IFileProvider` and custom providers; a disk cache with configurable layout, ETag and Last-Modified validators with 304 answers, source-change invalidation and single processing of concurrent identical requests; HMAC-signed URLs with a signer and a Razor tag helper; request hooks; and size, source and concurrency limits. See `docs/web.md`.
- Histograms, thresholds and dithering, matching ImageSharp 3.1.12: `Image.AutoLevel`, `Image.AdaptiveThreshold`, and a public `Image.Dither(palette, dither, scale, fastMatch)` (`fastMatch` defaults to true: ImageSharp's order-dependent cell matching, bit-identical to its output; `false` matches each pixel exactly) and `BinaryDither`. The `Dither` enum gains Atkinson, Burkes, JarvisJudiceNinke, Sierra2, Sierra3, SierraLite, StevensonArce, Stucki, Bayer2x2, Ordered3x3, Bayer4x4, Bayer8x8 and Bayer16x16 (they also work in `Quantize` and the GIF encoder). `Quantizer` (Wu, Octree, WebSafe, Werner) selects the palette builder in `Quantize` and `GifEncoderOptions.Quantizer`, and `KnownPalettes.WebSafe` and `KnownPalettes.Werner` are public. Existing GIF and indexed-PNG output is unchanged.

## [1.0.0] - 2026-10-22

First release, published on NuGet as `RecursiveAI.OneImage` (namespace `OneImage`).

### Added

- Lazy, immutable `Image` and mutable tiled `Canvas` on one engine, with O(1) snapshots.
- Codecs: PNG (including APNG and indexed output), JPEG (baseline, progressive, arithmetic, 12-bit, CMYK, shrink-on-load), WebP (lossy, lossless, animation), GIF (animation, quantizing, dithering), TIFF (including BigTIFF, CCITT and multi-page), BMP and ICO.
- ICC color management (v2 and v4 profiles, built-in profiles, a generic CMYK profile), resampling in linear light, geometry, tone and filter operations, analysis, and normal-mode compositing.
- `Image.Thumbnail`, which applies EXIF orientation, converts to sRGB and strips EXIF, XMP and IPTC by default.
- Size limits on by default, checked from headers before allocation; typed exceptions; a fuzz target for every decoder and parser.
- Depends only on the .NET base class library; NativeAOT- and trim-compatible.
- Documentation: a guide, a migration guide from ImageSharp and System.Drawing, a comparison with other libraries, and a [performance page](https://github.com/recursive-ai-oss/oneimage-csharp/blob/v1.0.0/docs/performance.md) with the benchmark method and every loss.

[Unreleased]: https://github.com/recursive-ai-oss/oneimage-csharp/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/recursive-ai-oss/oneimage-csharp/releases/tag/v1.0.0
