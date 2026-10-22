# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `Image.Composite(..., blend:, alpha:)`: all 24 Photoshop/W3C blend modes (`BlendMode`: Multiply, Screen, Overlay, Darken, Lighten, Color Dodge/Burn, Hard/Soft/Vivid/Linear/Pin Light, Hard Mix, Difference, Exclusion, Add, Subtract, Linear Burn, Divide, Hue, Saturation, Color, Luminosity) and the twelve Porter-Duff operators (`AlphaComposition`). Normal source-over output is unchanged.
- `Image.Perspective`: a projective (perspective) transform from a `ProjectiveMatrix`, a `ProjectiveTransformBuilder` (taper, rotation, scale, skew, translation, matrices, with `Append` and `Prepend` forms) or four corner points. `ProjectiveMatrix` also builds quad-to-quad maps.
- `Fit.BoxPad` and `Fit.Min` (ImageSharp's `ResizeMode.BoxPad` and `Min`), the aliases `Fit.Max` and `Fit.Crop`, and `Image.ResizeInto` for resizing into a rectangle of a canvas (`ResizeMode.Manual`).
- Resampling filters `Hermite`, `Lanczos5`, `Lanczos8`, `Robidoux`, `RobidouxSharp`, `Spline` and `Welch`, with the aliases `Bicubic` and `Triangle`, in `Resize` and in the rotate, affine and perspective samplers.

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
