using System;
using System.Buffers;
using System.IO;
using System.Linq;
using OneImage;
using OneImage.Compression;
using OneImage.Text;

// Exercises the library from a NativeAOT-published app; grows as the library does.
if (string.IsNullOrEmpty(Library.Name))
{
    Console.Error.WriteLine("library name missing");
    return 1;
}

using var canvas = new Canvas(300, 200, PixelFormat.Rgba8);
canvas[10, 20] = new Rgba32(1, 2, 3, 255);
using (PixelLock<Rgba32> l = canvas.Lock<Rgba32>(new ImageRect(100, 100, 200, 50), LockAccess.Write))
{
    for (int y = 0; y < l.Height; y++)
    {
        l.GetRow(y).Fill(new Rgba32(9, 8, 7, 255));
    }
}

Image snapshot = canvas.Snapshot();
canvas[10, 20] = new Rgba32(99, 99, 99, 255);
Image edited = snapshot.Crop(5, 15, 100, 100).Invert().ConvertTo(PixelFormat.Gray8);
Gray8[] gray = edited.ToArray<Gray8>();
Rgba32[] px = snapshot.Crop(5, 15, 100, 100).ToArray<Rgba32>();
Console.WriteLine(edited.DescribeGraph().TrimEnd());

bool ok = px[(5 * 100) + 5] == new Rgba32(1, 2, 3, 255) && gray.Length == 10000 && canvas[10, 20].R == 99
    && !Image.TryLoad(new byte[] { 1, 2, 3 }, out _);
if (!ok)
{
    Console.Error.WriteLine("smoke failed");
    return 1;
}

// Resize: shrink with the default filter, cover-crop with the smart anchor, pad.
Rgba32[] small = snapshot.Resize(width: 60).ToArray<Rgba32>();
Image padded = snapshot.Resize(100, 100, Fit.Pad, Anchor.TopLeft);
Image smart = snapshot.Resize(50, 50, Fit.Cover, Anchor.Smart);
if (small.Length != 60 * 40 || padded.Width != 100 || smart.ToArray<Rgba32>().Length != 2500)
{
    Console.Error.WriteLine("resize smoke failed");
    return 1;
}

// Geometry: exact turns, edge-mode padding, arbitrary rotation, trim.
Image geo = snapshot.Rotate(90).Flip(FlipAxis.Horizontal).Pad(3, 2, 1, 4, EdgeMode.Mirror);
Image turned = snapshot.Rotate(17, filter: ResamplingFilter.Lanczos3);
Image trimmed = snapshot.Pad(5, 5, 5, 5, EdgeMode.Constant, new Rgba32(255, 255, 255, 255)).Trim();
if (geo.Width != 204 || turned.ToArray<Rgba32>().Length != turned.Width * turned.Height || trimmed.Width < 1 || snapshot.Orient(6).Width != 200)
{
    Console.Error.WriteLine("geometry smoke failed");
    return 1;
}

// Perspective: a taper from the builder, pinned corners, the added resize modes and resamplers.
Image tapered = snapshot.Perspective(new ProjectiveTransformBuilder().AppendTaper(TaperSide.Left, TaperCorner.Both, 0.6).AppendRotationDegrees(5), filter: ResamplingFilter.Lanczos5);
Image pinned = snapshot.Perspective([(5, 2), (190, 10), (180, 120), (0, 100)]);
Image boxPadded = snapshot.Resize(300, 300, Fit.BoxPad, filter: ResamplingFilter.Robidoux);
Image manual = snapshot.ResizeInto(120, 90, new ImageRect(10, 10, 80, 60), ResamplingFilter.Welch);
if (tapered.ToArray<Rgba32>().Length != tapered.Width * tapered.Height || pinned.Width < 1 || boxPadded.Width != 300 || manual.ToArray<Rgba32>().Length != 120 * 90)
{
    Console.Error.WriteLine("perspective smoke failed");
    return 1;
}

// Filters and analysis: blur (exact and wide), sharpen, median, Sobel, convolution, histogram and statistics.
Image blurred = snapshot.GaussianBlur(2.5);
Image wide = snapshot.GaussianBlur(30);
Image filtered = snapshot.Sharpen(1, 1.5).Median(2).Convolve([1f, 2f, 1f, 2f, 4f, 2f, 1f, 2f, 1f], 3, 3);
ImageHistogram histogram = snapshot.Histogram();
ImageStatistics stats = snapshot.Statistics();
if (blurred.ToArray<Rgba32>().Length != 60000 || wide.ToArray<Rgba32>().Length != 60000 || filtered.ToArray<Rgba32>().Length != 60000
    || snapshot.Sobel().ToArray<Gray8>().Length != 60000 || histogram.PixelCount != 60000 || histogram.Channel(0).ToArray().Sum() != 60000
    || stats.Channels.Count != 4 || stats.Channels[0].Count != 60000)
{
    Console.Error.WriteLine("filter smoke failed");
    return 1;
}

// Neighbourhood effects: mosaic, oil paint, radial overlays, bokeh, Gaussian sharpening, edge detectors, entropy crop.
Image effects = snapshot.Pixelate(5).OilPaint(8, 5).Vignette().Glow(new Rgba32(255, 255, 255, 255), 40).BokehBlur(4, 2, 3).GaussianSharpen(1.5);
if (effects.ToArray<Rgba32>().Length != 60000 || snapshot.DetectEdges(EdgeDetector.Kirsch).ToArray<Gray8>().Length != 60000
    || snapshot.DetectEdges(EdgeDetector.Laplacian3x3, grayscale: false).ToArray<Rgba32>().Length != 60000 || snapshot.EntropyCrop().Width < 1)
{
    Console.Error.WriteLine("effects smoke failed");
    return 1;
}

// Histogram equalization: global, tile interpolation with clipping, sliding window.
Image equalized = snapshot.HistogramEqualization();
Image equalizedTiles = snapshot.HistogramEqualization(HistogramEqualizationMethod.AdaptiveTileInterpolation, clipHistogram: true);
Image equalizedWindow = snapshot.Crop(0, 0, 120, 100).HistogramEqualization(HistogramEqualizationMethod.AdaptiveSlidingWindow, clipHistogram: true, tiles: 4);
if (equalized.ToArray<Rgba32>().Length != 60000 || equalizedTiles.ToArray<Rgba32>().Length != 60000 || equalizedWindow.ToArray<Rgba32>().Length != 12000
    || !equalized.DescribeGraph().Contains("histogram_equalization", StringComparison.Ordinal))
{
    Console.Error.WriteLine("equalization smoke failed");
    return 1;
}

// Compression layer: deflate/inflate and LZW round trips.
byte[] sample = new byte[100_000];
for (int i = 0; i < sample.Length; i++)
{
    sample[i] = (byte)((i * 7 % 251) ^ (i >> 9));
}

byte[] zipped = Deflater.Compress(sample, 6);
using var inflater = new Inflater(true, sample.Length, 1100);
byte[] unzipped = new byte[sample.Length];
InflateStatus status = inflater.Inflate(zipped, unzipped, out _, out int produced);
byte[] lzw = LzwEncoder.Compress(sample, LzwVariant.Tiff);
byte[] unlzw = new byte[sample.Length];
InflateStatus lzwStatus = new LzwDecoder(LzwVariant.Tiff).Decode(lzw, unlzw, out _, out int lzwProduced);
if (status != InflateStatus.Done || produced != sample.Length || !unzipped.AsSpan().SequenceEqual(sample)
    || lzwStatus != InflateStatus.Done || lzwProduced != sample.Length || !unlzw.AsSpan().SequenceEqual(sample)
    || Crc32.Compute("123456789"u8) != 0xCBF43926u || Adler32.Update(Adler32.Initial, "Wikipedia"u8) != 0x11E60398u)
{
    Console.Error.WriteLine("compression smoke failed");
    return 1;
}

// Color and tone: a fused chain, grayscale, flatten, split/combine.
Image toned = snapshot.Crop(0, 0, 64, 64).Levels(0.1, 0.9, 1.2).Curves([new CurvePoint(0, 0), new CurvePoint(0.5, 0.6), new CurvePoint(1, 1)]).Gamma(1.5).Invert();
Image flat = snapshot.Crop(0, 0, 64, 64).Grayscale().HueSaturationLightness(20, 0.1).Flatten(new Rgba32(255, 255, 255, 255));
Image matrixed = snapshot.Crop(0, 0, 64, 64).Sepia(0.5f).ColorBlindness(ColorBlindnessMode.Tritanopia).Opacity(0.5f).ApplyColorMatrix(ColorMatrix.Kodachrome);
Image rejoined = Image.CombineChannels(snapshot.Crop(0, 0, 64, 64).SplitChannels());
bool toneOk = toned.ToArray<Rgba32>().Length == 4096 && flat.Format == PixelFormat.Rgb8 && flat.ToArray<Rgb24>().Length == 4096
    && rejoined.ToArray<Rgba32>().AsSpan().SequenceEqual(snapshot.Crop(0, 0, 64, 64).ToArray<Rgba32>()) && toned.DescribeGraph().Contains("fused", StringComparison.Ordinal)
    && matrixed.Format.HasAlpha && Math.Abs((matrixed.ToArray<Rgba32>()[0].A * 2) - snapshot.Crop(0, 0, 64, 64).ToArray<Rgba32>()[0].A) <= 1 && matrixed.DescribeGraph().Contains("color_matrix", StringComparison.Ordinal);
if (!toneOk)
{
    Console.Error.WriteLine("tone smoke failed");
    return 1;
}

// Color management and metadata: built-in profiles (including the embedded CMYK tables), conversions, EXIF.
Image p3 = snapshot.ConvertColor(ColorProfile.DisplayP3);
Rgba32[] p3px = p3.ToArray<Rgba32>();
Image cmykImage = snapshot.Crop(0, 0, 8, 8).ConvertTo(PixelFormat.Cmyk8);
Image lab = snapshot.Crop(0, 0, 8, 8).ConvertTo(PixelFormat.LabF32);
byte[] exif = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
ExifData e = ExifData.Parse(exif);
bool colorOk = p3px.Length == 300 * 200 && cmykImage.Format == PixelFormat.Cmyk8 && lab.ToArray<LabFloat>().Length == 64
    && e.Orientation == 6 && ColorProfile.GenericCmyk.Description.Contains("FOGRA39", StringComparison.Ordinal)
    && ColorProfile.Srgb.LinearVariant is { IsLinear: true } && snapshot.StripMetadata().Metadata.Exif.IsEmpty;
if (!colorOk)
{
    Console.Error.WriteLine("color smoke failed");
    return 1;
}

// TIFF: write with each compression (classic and BigTIFF), read back through the default configuration, and read a region.
Rgba32[] original = snapshot.Crop(0, 0, 300, 200).ToArray<Rgba32>();
foreach (TiffCompression compression in new[] { TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate, TiffCompression.PackBits })
{
    foreach (bool bigTiff in new[] { false, true })
    {
        using var tiff = new MemoryStream();
        snapshot.Save(tiff, TiffFormat.Instance, new SaveOptions { EncoderOptions = new TiffEncoderOptions { Compression = compression, BigTiff = bigTiff } });
        Image reread = Image.Load(tiff.ToArray());
        Rgba32[] again = reread.ToArray<Rgba32>();
        Rgba32[] region = reread.Crop(100, 100, 50, 40).ToArray<Rgba32>();
        if (reread.SourceFormat?.Name != "tiff" || !again.AsSpan().SequenceEqual(original) || region[0] != original[(100 * 300) + 100])
        {
            Console.Error.WriteLine("tiff smoke failed: " + compression + " " + bigTiff);
            return 1;
        }
    }
}

// JPEG: encode (optimized and progressive), decode through the default configuration, a 1/8 shrink-on-load resize, a crop,
// and JPEG inside TIFF.
foreach (JpegEncoderOptions jo in new[] { new JpegEncoderOptions { Quality = 85 }, new JpegEncoderOptions { Quality = 85, Progressive = true, Subsampling = JpegSubsampling.Yuv444 } })
{
    using var jpg = new MemoryStream();
    snapshot.ConvertTo(PixelFormat.Rgb8).Save(jpg, JpegFormat.Instance, new SaveOptions { EncoderOptions = jo });
    Image jr = Image.Load(jpg.ToArray());
    Rgb24[] jpx = jr.ToArray<Rgb24>();
    Rgb24[] jsmall = jr.Resize(width: 40).ToArray<Rgb24>();
    Rgb24[] jcrop = jr.Crop(16, 16, 32, 32).ToArray<Rgb24>();
    if (jr.SourceFormat?.Name != "jpeg" || jpx.Length != 300 * 200 || jsmall.Length != 40 * 27 || jcrop.Length != 32 * 32)
    {
        Console.Error.WriteLine("jpeg smoke failed");
        return 1;
    }
}

// PNG, BMP and ICO: encode with each registered codec, decode again, animate.
var rgbaPixels = new Rgba32[40 * 30];
for (int i = 0; i < rgbaPixels.Length; i++)
{
    rgbaPixels[i] = new Rgba32((byte)(i * 7), (byte)(i / 40 * 9), (byte)(i ^ 0x5A), (byte)(i % 5 == 0 ? 100 : 255));
}

Image source = Image.FromPixels<Rgba32>(rgbaPixels, 40, 30);
bool rasterOk = true;
foreach (IImageFormat format in new IImageFormat[] { PngFormat.Instance, BmpFormat.Instance, IcoFormat.Instance })
{
    using var encoded = new System.IO.MemoryStream();
    source.Save(encoded, format);
    Image back = Image.Load(encoded.ToArray());
    rasterOk &= back.SourceFormat == format && back.Width == 40 && back.Height == 30 && back.ToArray<Rgba32>().AsSpan().SequenceEqual(rgbaPixels);
}

// Indexed PNG for a few colors, and encoder options.
var solid = new Rgba32[20 * 20];
Array.Fill(solid, new Rgba32(10, 20, 30, 255));
using var indexed = new System.IO.MemoryStream();
Image.FromPixels<Rgba32>(solid, 20, 20).Save(indexed, PngFormat.Instance);
rasterOk &= Image.Load(indexed.ToArray()).ToArray<Rgb24>()[0] == new Rgb24(10, 20, 30);
using var tuned = new System.IO.MemoryStream();
source.Save(tuned, PngFormat.Instance, new SaveOptions { EncoderOptions = new PngEncoderOptions { CompressionLevel = 9, Filter = PngFilterMode.Paeth } });
rasterOk &= PngFormat.Instance.Matches(tuned.ToArray()) && Image.Load(tuned.ToArray()).ToArray<Rgba32>().AsSpan().SequenceEqual(rgbaPixels);
if (!rasterOk)
{
    Console.Error.WriteLine("png/bmp/ico smoke failed");
    return 1;
}

// GIF: encode a photo-like image (quantized, dithered), decode it, and decode a hand-assembled two-frame animation.
var photo = new Rgba32[64 * 48];
for (int i = 0; i < photo.Length; i++)
{
    photo[i] = new Rgba32((byte)(i * 3), (byte)((i / 64) * 5), (byte)(i ^ (i >> 3)), 255);
}

using var gifStream = new MemoryStream();
Image.FromPixels<Rgba32>(photo, 64, 48).Save(gifStream, GifFormat.Instance);
Image decodedGif = Image.Load(gifStream.ToArray());
Image quantized = Image.FromPixels<Rgba32>(photo, 64, 48).Quantize(16, Dither.FloydSteinberg);
Rgba32[] qpx = quantized.ToArray<Rgba32>();
var distinct = new System.Collections.Generic.HashSet<Rgba32>(qpx);

byte[] frameData = LzwEncoder.Compress(new byte[] { 0, 1, 2, 3 }, LzwVariant.Gif, 2);
var anim = new MemoryStream();
anim.Write("GIF89a"u8);
anim.Write(new byte[] { 2, 0, 2, 0, 0x81, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 });
for (int f = 0; f < 2; f++)
{
    anim.Write(new byte[] { 0x21, 0xF9, 4, 4, 5, 0, 0, 0, 0x2C, 0, 0, 0, 0, 2, 0, 2, 0, 0, 2, (byte)frameData.Length });
    anim.Write(frameData);
    anim.WriteByte(0);
}

anim.WriteByte(0x3B);
Image animGif = Image.Load(anim.ToArray());
if (decodedGif.Width != 64 || decodedGif.Height != 48 || distinct.Count > 16 || animGif.FrameCount != 2 || animGif.ToArray<Rgba32>()[3] != new Rgba32(255, 255, 255, 255))
{
    Console.Error.WriteLine("gif smoke failed");
    return 1;
}

// WebP: lossless (exact), lossy (close), alpha, and a two-frame animation with metadata, through the default configuration.
foreach (bool lossless in new[] { true, false })
{
    using var webp = new MemoryStream();
    snapshot.Save(webp, WebPFormat.Instance, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = lossless, Quality = 90 } });
    Image wr = Image.Load(webp.ToArray());
    Rgba32[] wpx = wr.ToArray<Rgba32>();
    bool wexact = !lossless || wpx.AsSpan().SequenceEqual(original);
    if (wr.SourceFormat?.Name != "webp" || wpx.Length != 300 * 200 || !wexact || wr.Crop(100, 100, 40, 40).ToArray<Rgba32>().Length != 1600)
    {
        Console.Error.WriteLine("webp smoke failed");
        return 1;
    }
}

// Composite and Thumbnail: a watermark over an RGB image (fast path), a linear-light blend (float path), and a thumbnail of encoded bytes.
{
    Image wm = Image.FromPixels<Rgba32>(new Rgba32[40 * 20], 40, 20);
    Image bg = snapshot.ConvertTo(PixelFormat.Rgb8);
    Rgb24[] marked = bg.Composite(wm.Invert(), 10, 10, 0.5f).ToArray<Rgb24>();
    Rgb24[] lin = bg.Composite(wm, 5, 5, 0.25f, linear: true).ToArray<Rgb24>();
    Rgb24[] mult = bg.Composite(wm.Invert(), 10, 10, 0.5f, blend: BlendMode.Multiply).ToArray<Rgb24>();
    Rgb24[] hue = bg.Composite(wm.Invert(), 10, 10, 1f, blend: BlendMode.Hue, alpha: AlphaComposition.SrcAtop).ToArray<Rgb24>();
    Rgb24[] plusMix = bg.Composite(wm.Invert(), 10, 10, 1f, alpha: AlphaComposition.Plus).ToArray<Rgb24>();
    if (mult.Length != 300 * 200 || hue.Length != 300 * 200 || plusMix.Length != 300 * 200)
    {
        Console.Error.WriteLine("blend smoke failed");
        return 1;
    }

    using var enc = new MemoryStream();
    bg.Save(enc, JpegFormat.Instance);
    Image thumb = Image.Thumbnail(enc.ToArray(), 100);
    using var webp = new MemoryStream();
    thumb.Save(webp, WebPFormat.Instance);
    if (marked.Length != 300 * 200 || lin.Length != 300 * 200 || thumb.Width != 100 || thumb.Height != 67 || webp.Length == 0 || thumb.Metadata.IccProfile.IsEmpty)
    {
        Console.Error.WriteLine("composite/thumbnail smoke failed");
        return 1;
    }
}

// Drawing: build and parse paths, fill an image and a canvas (anti-aliased, a blend mode, linear light) and check both agree.
{
    OneImage.Drawing.Path shape = OneImage.Drawing.Path.Combine(OneImage.Drawing.Path.Circle(100, 80, 60), OneImage.Drawing.Path.ParseSvg("M10 10 L 90 20 Q 60 90 20 60 Z"));
    Image filled = snapshot.Fill(new Rgba32(200, 40, 40, 200), shape, rule: OneImage.Drawing.IntersectionRule.EvenOdd);
    Image lit = snapshot.Fill(new Rgba32(200, 40, 40, 200), shape, blend: BlendMode.Multiply, linear: true);
    using var drawn = snapshot.ToCanvas();
    drawn.Fill(new Rgba32(200, 40, 40, 200), shape, rule: OneImage.Drawing.IntersectionRule.EvenOdd);
    Rgba32[] a = filled.ToArray<Rgba32>();
    var b = new Rgba32[a.Length];
    drawn.CopyTo<Rgba32>(b, new ImageRect(0, 0, 300, 200));
    if (!a.AsSpan().SequenceEqual(b) || lit.ToArray<Rgba32>().Length != a.Length || !OneImage.Drawing.Path.TryParseSvg(shape.ToSvg(), out _))
    {
        Console.Error.WriteLine("drawing smoke failed");
        return 1;
    }
}

// Histograms and dithering: auto-level, adaptive threshold, a palette dither and the octree quantizer.
{
    Image rgbPhoto = snapshot.ConvertTo(PixelFormat.Rgb8);
    int total = 0;
    total += rgbPhoto.AutoLevel().ToArray<Rgb24>().Length + rgbPhoto.AdaptiveThreshold().ToArray<Rgba32>().Length;
    total += rgbPhoto.Dither(KnownPalettes.WebSafe, Dither.Stucki).ToArray<Rgba32>().Length + rgbPhoto.BinaryDither(Dither.Bayer4x4).ToArray<Rgba32>().Length;
    total += rgbPhoto.Quantize(16, Dither.None, quantizer: Quantizer.Octree).ToArray<Rgba32>().Length;
    if (total != 5 * 300 * 200)
    {
        Console.Error.WriteLine("histogram/dither smoke failed");
        return 1;
    }
}

// Strokes: a dashed pen with round joins on an image and a canvas must agree, and the outline must come back as a path.
{
    OneImage.Drawing.Path line = OneImage.Drawing.Path.ParseSvg("M10 10 C 80 150 160 -40 280 120 L 40 180");
    var pen = new OneImage.Drawing.Pen(OneImage.Drawing.Brush.Solid(new Rgba32(20, 60, 220, 220)), 6, OneImage.Drawing.StrokeJoin.Round, OneImage.Drawing.StrokeCap.Round, dashPattern: [12, 5]);
    Rgba32[] viaImage = snapshot.Draw(pen, line).ToArray<Rgba32>();
    using var stroked = snapshot.ToCanvas();
    stroked.Draw(pen, line);
    var viaCanvas = new Rgba32[viaImage.Length];
    stroked.CopyTo<Rgba32>(viaCanvas, new ImageRect(0, 0, 300, 200));
    if (!viaImage.AsSpan().SequenceEqual(viaCanvas) || OneImage.Drawing.PathStrokeExtensions.GenerateOutline(line, pen).IsEmpty)
    {
        Console.Error.WriteLine("stroke smoke failed");
        return 1;
    }
}

// Boolean path operations and clipping: a union and a difference, a blur clipped to the union on an image and a canvas, and a clipped fill.
{
    OneImage.Drawing.Path circleA = OneImage.Drawing.Path.Circle(110, 90, 60), circleB = OneImage.Drawing.Path.Circle(170, 100, 50);
    OneImage.Drawing.Path united = OneImage.Drawing.PathBoolean.Union(circleA, circleB);
    OneImage.Drawing.Path cut = OneImage.Drawing.PathBoolean.Subtract(circleA, circleB);
    Rgba32[] viaImage = snapshot.Clip(united, i => i.GaussianBlur(4)).ToArray<Rgba32>();
    using var clipped = snapshot.ToCanvas();
    clipped.Clip(united, i => i.GaussianBlur(4));
    var viaCanvas = new Rgba32[viaImage.Length];
    clipped.CopyTo<Rgba32>(viaCanvas, new ImageRect(0, 0, 300, 200));
    Rgba32[] marked = snapshot.Fill(new Rgba32(200, 40, 40, 255), OneImage.Drawing.Path.Rectangle(0, 0, 300, 200), clip: cut).ToArray<Rgba32>();
    if (united.IsEmpty || cut.IsEmpty || !viaImage.AsSpan().SequenceEqual(viaCanvas) || marked.Length != viaImage.Length
        || OneImage.Drawing.PathBoolean.UnionAll([circleA, circleB]).IsEmpty || OneImage.Drawing.PathBoolean.Offset(circleA, 5).IsEmpty)
    {
        Console.Error.WriteLine("clip smoke failed");
        return 1;
    }
}

// Brushes: gradients (also in linear light), a hatch, an image brush, path gradient and recolor; Image and Canvas must agree.
{
    OneImage.Drawing.Path disc = OneImage.Drawing.Path.Circle(120, 90, 70);
    var red = new Rgba32(220, 30, 20, 255);
    var blue = new Rgba32(20, 40, 230, 255);
    var tile = Image.FromPixels<Rgba32>(new[] { red, blue, blue, red }, 2, 2);
    OneImage.Drawing.Brush[] brushes =
    [
        OneImage.Drawing.Brush.LinearGradient(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(300, 200), [new(0f, red), new(1f, blue)]),
        OneImage.Drawing.Brush.RadialGradient(new System.Numerics.Vector2(120, 90), 60, [new(0f, red), new(1f, new Rgba32(20, 40, 230, 0))], OneImage.Drawing.GradientRepetition.Reflect),
        OneImage.Drawing.Brush.EllipticGradient(new System.Numerics.Vector2(120, 90), new System.Numerics.Vector2(180, 90), 0.5f, [new(0f, red), new(1f, blue)]),
        OneImage.Drawing.Brush.PathGradient([new(40, 20), new(200, 30), new(180, 160), new(50, 150)], [red, blue, red, blue]),
        OneImage.Drawing.Brush.Hatch(OneImage.Drawing.HatchStyle.ForwardDiagonal, red, blue),
        OneImage.Drawing.Brush.FromImage(tile),
        OneImage.Drawing.Brush.Recolor(new Rgba32(10, 20, 30, 255), red, 0.3f),
    ];
    foreach (OneImage.Drawing.Brush brush in brushes)
    {
        Image viaImage = snapshot.Fill(brush, disc, linear: true);
        using var onCanvas = snapshot.ToCanvas();
        onCanvas.Fill(brush, disc, linear: true);
        Rgba32[] x = viaImage.ToArray<Rgba32>();
        var y = new Rgba32[x.Length];
        onCanvas.CopyTo<Rgba32>(y, new ImageRect(0, 0, 300, 200));
        if (!x.AsSpan().SequenceEqual(y) || !snapshot.Fill(brush, disc).ToArray<Rgba32>().AsSpan().SequenceEqual(snapshot.Fill(brush, disc).ToArray<Rgba32>()))
        {
            Console.Error.WriteLine("brush smoke failed");
            return 1;
        }
    }
}

// Text: fonts of each container, layout, measurement and drawing, through the separate text package.
{
    string dir = Path.Combine(AppContext.BaseDirectory, "fonts");
    var fonts = new OneImage.Text.FontCollection();
    OneImage.Text.FontFamily noto = fonts.Add(Path.Combine(dir, "NotoSans-Subset-Regular.woff"));
    fonts.AddCollection(Path.Combine(dir, "NotoSans-Subset.ttc"));
    OneImage.Text.FontFamily cjk = fonts.Add(Path.Combine(dir, "NotoSansCJK-Subset.otf"));
    var options = new OneImage.Text.TextOptions(noto.CreateFont(28)) { Origin = new System.Numerics.Vector2(8, 6), WrappingLength = 150 };
    const string text = "Hello AV world, kerned and wrapped.";
    OneImage.Text.TextMetrics metrics = OneImage.Text.TextMeasurer.Measure(text, options);
    var white = new Rgba32[300 * 200];
    Array.Fill(white, new Rgba32(255, 255, 255, 255));
    Image drawn = Image.FromPixels<Rgba32>(white, 300, 200)
        .DrawText(text, options, OneImage.Drawing.Brush.Solid(new Rgba32(0, 0, 0, 255)), OneImage.Drawing.Pen.Solid(new Rgba32(200, 0, 0, 255), 1));
    Rgba32[] pixels = drawn.ToArray<Rgba32>();
    var cjkOptions = new OneImage.Text.TextOptions(cjk.CreateFont(30));
    using var textCanvas = new Canvas(200, 60, PixelFormat.Rgba8);
    textCanvas.DrawText("\u65e5\u672c\u8a9e AV", cjkOptions, new Rgba32(255, 255, 255, 255));
    if (metrics.LineCount < 3 || !pixels.Any(p => p.R < 100 && p.G < 100) || !pixels.Any(p => p.R > 150 && p.G < 100) || textCanvas.Snapshot().ToArray<Rgba32>().All(p => p.A == 0))
    {
        Console.Error.WriteLine("text smoke failed");
        return 1;
    }
}

// Shaping: ligatures, composed marks, features and a language through the OpenType engine (GSUB and GPOS lookups).
{
    var fonts = new OneImage.Text.FontCollection();
    OneImage.Text.FontFamily shaped = fonts.Add(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Shaping-Subset.ttf"));
    var plain = new OneImage.Text.TextOptions(shaped.CreateFont(24));
    var noLigatures = new OneImage.Text.TextOptions(shaped.CreateFont(24)) { Language = "ro" };
    noLigatures.Features.Add(OneImage.Text.FontFeature.Off("liga"));
    noLigatures.Features.Add(OneImage.Text.FontFeature.On("smcp"));
    int ligated = OneImage.Text.TextMeasurer.GetCharacterBounds("afib", plain).Count;
    int separate = OneImage.Text.TextMeasurer.GetCharacterBounds("afib", noLigatures).Count;
    var marked = OneImage.Text.TextMeasurer.GetCharacterBounds("a\u0301 \u03B1\u0301 \u0438\u0306", plain);
    if (ligated != 3 || separate != 4 || marked.Count != 5)
    {
        Console.Error.WriteLine("shaping smoke failed");
        return 1;
    }
}

// WOFF 2.0 (our own Brotli decoder, transformed glyf, a collection) and COLR color glyphs.
{
    string dir = Path.Combine(AppContext.BaseDirectory, "fonts");
    var fonts = new OneImage.Text.FontCollection();
    OneImage.Text.FontFamily w2 = fonts.Add(Path.Combine(dir, "NotoSans-Subset-Regular.woff2"));
    int faces = new OneImage.Text.FontCollection().AddCollection(Path.Combine(dir, "NotoSans-Subset.woff2")).Count;
    OneImage.Text.FontFamily color = fonts.Add(Path.Combine(dir, "OneImageColorTest-V0.ttf"));
    var options = new OneImage.Text.TextOptions(color.CreateFont(60))
    {
        Origin = new System.Numerics.Vector2(4, 4),
        ColorFontSupport = OneImage.Text.ColorFontSupport.MicrosoftColrFormat,
    };
    var layerPx = new Rgba32[200 * 100];
    Array.Fill(layerPx, new Rgba32(255, 255, 255, 255));
    Rgba32[] drawn = Image.FromPixels<Rgba32>(layerPx, 200, 100).DrawText("\u2600\u2601", options, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();
    float width = OneImage.Text.TextMeasurer.MeasureAdvance("Hag", new OneImage.Text.TextOptions(w2.CreateFont(30))).Width;
    if (faces != 2 || width <= 0 || !drawn.Any(p => p.B > 200 && p.R < 60) || !drawn.Any(p => p.G > 130 && p.R < 60 && p.B < 80))
    {
        Console.Error.WriteLine("woff2 and color font smoke failed");
        return 1;
    }
}

// TrueType hinting (the font's own instructions run at the drawing size), COLR version 1 paint graphs and vertical layout.
{
    string dir = Path.Combine(AppContext.BaseDirectory, "fonts");
    var fonts = new OneImage.Text.FontCollection();
    OneImage.Text.FontFamily hintedFamily = fonts.Add(Path.Combine(dir, "NotoSans-Hinted-Subset.ttf"));
    var plainOptions = new OneImage.Text.TextOptions(hintedFamily.CreateFont(11)) { Origin = new System.Numerics.Vector2(2, 2) };
    var hintedOptions = new OneImage.Text.TextOptions(hintedFamily.CreateFont(11)) { Origin = new System.Numerics.Vector2(2, 2), HintingMode = OneImage.Text.HintingMode.Standard };
    var whitePx = new Rgba32[120 * 24];
    Array.Fill(whitePx, new Rgba32(255, 255, 255, 255));
    Rgba32[] unhinted = Image.FromPixels<Rgba32>(whitePx, 120, 24).DrawText("Hamburgefonstiv", plainOptions, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();
    Rgba32[] hinted = Image.FromPixels<Rgba32>(whitePx, 120, 24).DrawText("Hamburgefonstiv", hintedOptions, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();

    OneImage.Text.FontFamily colr1 = fonts.Add(Path.Combine(dir, "ColrV1-TestGlyphs.ttf"));
    var colorOptions = new OneImage.Text.TextOptions(colr1.CreateFont(64)) { Origin = new System.Numerics.Vector2(4, 4) };
    var colorPx = new Rgba32[100 * 100];
    Array.Fill(colorPx, new Rgba32(255, 255, 255, 255));
    Rgba32[] color = Image.FromPixels<Rgba32>(colorPx, 100, 100).DrawText(char.ConvertFromUtf32(0xF0B01), colorOptions, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();

    var vertical = new OneImage.Text.TextOptions(hintedFamily.CreateFont(20)) { LayoutMode = OneImage.Text.LayoutMode.VerticalMixedRightLeft };
    System.Drawing.SizeF column = OneImage.Text.TextMeasurer.Measure("Hag", vertical).Advance;
    if (!unhinted.Where((p, i) => p.R != hinted[i].R).Any() || !color.Any(p => Math.Abs(p.R - p.B) > 40) || !(column.Height > column.Width))
    {
        Console.Error.WriteLine("hinting, COLR v1 and vertical layout smoke failed");
        return 1;
    }
}

// Rich text: runs with decorations and attributes, font fallback, text along a path and the installed-font scan.
{
    string dir = Path.Combine(AppContext.BaseDirectory, "fonts");
    var fonts = new OneImage.Text.FontCollection();
    OneImage.Text.FontFamily noto = fonts.Add(Path.Combine(dir, "NotoSans-Subset-Regular.ttf"));
    OneImage.Text.FontFamily cjk = fonts.Add(Path.Combine(dir, "NotoSansCJK-Subset.otf"));
    var rich = new OneImage.Text.TextOptions(noto.CreateFont(24)) { Origin = new System.Numerics.Vector2(6, 4) };
    rich.FallbackFontFamilies.Add(cjk);
    rich.TextRuns.Add(new OneImage.Text.TextRun(0, 5) { Font = noto.CreateFont(40), TextDecorations = OneImage.Text.TextDecorations.Underline | OneImage.Text.TextDecorations.Strikeout, Brush = OneImage.Drawing.Brush.Solid(new Rgba32(200, 0, 0, 255)) });
    rich.TextRuns.Add(new OneImage.Text.TextRun(6, 7) { TextAttributes = OneImage.Text.TextAttributes.Superscript });
    const string richText = "Hello x2 \u3042\u3044 end";
    var canvasPixels = new Rgba32[300 * 100];
    Array.Fill(canvasPixels, new Rgba32(255, 255, 255, 255));
    Rgba32[] richPixels = Image.FromPixels<Rgba32>(canvasPixels, 300, 100)
        .DrawText(richText, rich, OneImage.Drawing.Brush.Solid(new Rgba32(0, 0, 0, 255))).ToArray<Rgba32>();
    var onPath = new OneImage.Text.TextOptions(noto.CreateFont(20)) { Path = OneImage.Drawing.Path.Circle(100, 100, 60) };
    var pathBounds = OneImage.Text.TextBuilder.GeneratePath("Round and round", onPath).Bounds;
    var installed = new OneImage.Text.SystemFontSet([dir]);
    if (!richPixels.Any(p => p.R > 150 && p.G < 100) || !richPixels.Any(p => p.R < 100 && p.G < 100) || pathBounds.Height < 30 || !installed.TryGet("Noto Sans", out _))
    {
        Console.Error.WriteLine("rich text smoke failed");
        return 1;
    }
}

// SVG: registration, detection (plain and gzip), the natural size and drawing at a requested size.
{
    var svgConfig = new ImageConfiguration().Add(OneImage.Svg.SvgFormat.Instance);
    byte[] svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='30mm' viewBox='0 0 40 20'/>");
    using var gzBytes = new MemoryStream();
    using (var gz = new System.IO.Compression.GZipStream(gzBytes, System.IO.Compression.CompressionLevel.Optimal, true))
    {
        gz.Write(svg);
    }

    ImageInfo plain = Image.Identify(svg, new LoadOptions { Configuration = svgConfig });
    ImageInfo svgZipped = Image.Identify(gzBytes.ToArray(), new LoadOptions { Configuration = svgConfig });
    if (plain.Width != 114 || plain.Height != 57 || svgZipped.Width != 114 || plain.Format.Name != "svg")
    {
        Console.Error.WriteLine("svg smoke failed");
        return 1;
    }

    byte[] drawing = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' viewBox='0 0 20 20' width='20' height='20'>"
        + "<defs><rect id='r' width='8' height='8' fill='#ff0000'/></defs><g opacity='0.5'><use xlink:href='#r' x='2' y='2'/></g>"
        + "<circle cx='14' cy='14' r='4' fill='none' stroke='blue' stroke-width='2' stroke-dasharray='3 1'/></svg>");
    Image sized = Image.Load(drawing, new LoadOptions { Configuration = svgConfig }).Resize(width: 80);
    Rgba32[] svgPixels = sized.ToArray<Rgba32>();
    if (sized.Width != 80 || sized.Height != 80 || svgPixels[(24 * 80) + 24].A is < 120 or > 135 || svgPixels[(24 * 80) + 24].R != 255 || svgPixels[(10 * 80) + 70].A != 0)
    {
        Console.Error.WriteLine("svg render smoke failed");
        return 1;
    }

    // Markers (orient, viewBox, context paint) and a blended group.
    byte[] marked = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 40' width='40' height='40'>"
        + "<marker id='m' viewBox='0 0 10 10' markerWidth='10' markerHeight='10' refX='5' refY='5' markerUnits='userSpaceOnUse' orient='auto'><rect width='10' height='10' fill='context-stroke'/></marker>"
        + "<polyline points='5,20 20,20 35,20' fill='none' stroke='#ff0000' marker-start='url(#m)' marker-end='url(#m)'/>"
        + "<rect x='0' y='30' width='40' height='10' fill='#808080'/><rect x='0' y='30' width='20' height='10' fill='#808080' style='mix-blend-mode:multiply'/></svg>");
    Rgba32[] markedPixels = Image.Load(marked, new LoadOptions { Configuration = svgConfig }).Resize(width: 40).ToArray<Rgba32>();
    if (markedPixels[(18 * 40) + 5].R != 255 || markedPixels[(18 * 40) + 5].G != 0 || markedPixels[(35 * 40) + 10].R is < 60 or > 70 || markedPixels[(35 * 40) + 30].R is < 125 or > 131)
    {
        Console.Error.WriteLine("svg marker smoke failed");
        return 1;
    }

    // plus-lighter adds to what is behind it: two 50% grays make 100% white.
    byte[] plus = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10' width='10' height='10'>"
        + "<rect width='10' height='10' fill='#808080'/><rect width='10' height='10' fill='#808080' style='mix-blend-mode:plus-lighter'/></svg>");
    Rgba32 plusPixel = Image.Load(plus, new LoadOptions { Configuration = svgConfig }).ToArray<Rgba32>()[55];
    if (plusPixel.R < 254 || plusPixel.A != 255)
    {
        Console.Error.WriteLine("svg plus-lighter smoke failed");
        return 1;
    }

    // Paint servers: a focal radial gradient with a transform, a linear gradient and a pattern tile.
    byte[] paints = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 20' width='40' height='20'>"
        + "<defs><radialGradient id='r' fx='0.8' gradientTransform='rotate(10)'><stop offset='0' stop-color='black'/><stop offset='1' stop-color='white'/></radialGradient>"
        + "<linearGradient id='l'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient>"
        + "<pattern id='p' patternUnits='userSpaceOnUse' width='10' height='10' patternTransform='rotate(20)'><rect width='5' height='5' fill='lime'/></pattern></defs>"
        + "<rect width='20' height='20' fill='url(#r)'/><rect x='20' width='10' height='20' fill='url(#l)'/><rect x='30' width='10' height='20' fill='url(#p)' paint-order='stroke'/></svg>");
    Rgba32[] paintPixels = Image.Load(paints, new LoadOptions { Configuration = svgConfig }).Resize(width: 160).ToArray<Rgba32>();
    int covered = 0;
    for (int y = 0; y < 80; y++)
    {
        for (int x = 120; x < 160; x++)
        {
            covered += paintPixels[(y * 160) + x].A != 0 ? 1 : 0;
        }
    }

    if (paintPixels[(40 * 160) + 85].A != 255 || paintPixels[(40 * 160) + 85].R <= paintPixels[(40 * 160) + 115].R || paintPixels[(40 * 160) + 40].A != 255 || covered is < 200 or > 3000)
    {
        Console.Error.WriteLine("svg paint server smoke failed");
        return 1;
    }

    // Clipping and masking: the clip keeps the left half, the luminance mask the top half, so only the top-left quarter is painted.
    byte[] masked = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20' width='20' height='20'>"
        + "<clipPath id='c'><rect width='10' height='20'/></clipPath>"
        + "<mask id='m' maskUnits='userSpaceOnUse' width='20' height='20'><rect width='20' height='10' fill='white'/></mask>"
        + "<g clip-path='url(#c)' mask='url(#m)'><rect width='20' height='20' fill='#00ff00'/></g></svg>");
    Rgba32[] maskedPixels = Image.Load(masked, new LoadOptions { Configuration = svgConfig }).Resize(width: 40).ToArray<Rgba32>();
    if (maskedPixels[(10 * 40) + 10].A != 255 || maskedPixels[(10 * 40) + 30].A != 0 || maskedPixels[(30 * 40) + 10].A != 0)
    {
        Console.Error.WriteLine("svg clip and mask smoke failed");
        return 1;
    }

    byte[] filterDoc = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20' width='20' height='20'>"
        + "<filter id='f' x='0' y='0' width='1' height='1'><feFlood flood-color='#ff0000'/><feGaussianBlur stdDeviation='1'/><feOffset dx='2'/><feComposite in='SourceGraphic' in2='SourceGraphic' operator='over'/></filter>"
        + "<rect x='4' y='4' width='12' height='12' fill='#0000ff' filter='url(#f)'/><rect x='0' y='0' width='3' height='3' fill='#00ff00' style='filter: grayscale(1) drop-shadow(1px 1px 0px black)'/></svg>");
    Rgba32[] filteredPixels = Image.Load(filterDoc, new LoadOptions { Configuration = svgConfig }).Resize(width: 40).ToArray<Rgba32>();
    if (filteredPixels[(20 * 40) + 20].A != 255 || filteredPixels[(20 * 40) + 20].B != 255 || filteredPixels[(2 * 40) + 2].A != 255 || filteredPixels[(35 * 40) + 35].A != 0)
    {
        Console.Error.WriteLine("svg filter smoke failed");
        return 1;
    }

    // Filters, part two: noise as a height map under a point light, morphology, tiling, displacement, component transfer, and a blur wide
    // enough to be computed in shared blocks.
    byte[] effectsDoc = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20' width='20' height='20'>"
        + "<filter id='lit' x='0' y='0' width='1' height='1'><feTurbulence type='fractalNoise' baseFrequency='0.3' numOctaves='2' result='n'/><feDiffuseLighting in='n' surfaceScale='3' result='d'><fePointLight x='5' y='5' z='6'/></feDiffuseLighting>"
        + "<feSpecularLighting in='n' specularExponent='8' result='s'><feSpotLight x='10' y='2' z='8' pointsAtX='8' pointsAtY='8' limitingConeAngle='50'/></feSpecularLighting><feMerge><feMergeNode in='d'/><feMergeNode in='s'/></feMerge></filter>"
        + "<filter id='mix' x='0' y='0' width='1' height='1'><feMorphology operator='dilate' radius='1'/><feComponentTransfer><feFuncR type='table' tableValues='1 0'/></feComponentTransfer><feFlood flood-color='navy' x='1' y='1' width='2' height='2' result='c'/><feTile in='c' result='t'/><feDisplacementMap in='SourceGraphic' in2='t' scale='2' xChannelSelector='R' yChannelSelector='B'/></filter>"
        + "<filter id='wide' x='-1' y='-1' width='3' height='3'><feGaussianBlur stdDeviation='8'/></filter>"
        + "<rect x='1' y='1' width='8' height='8' filter='url(#lit)'/><rect x='11' y='1' width='8' height='8' fill='tomato' filter='url(#mix)'/><rect x='6' y='12' width='8' height='6' fill='teal' filter='url(#wide)'/></svg>");
    Rgba32[] effectPixels = Image.Load(effectsDoc, new LoadOptions { Configuration = svgConfig }).Resize(width: 40).ToArray<Rgba32>();
    if (effectPixels[(10 * 40) + 10].A != 255 || effectPixels[(30 * 40) + 20].A == 0)
    {
        Console.Error.WriteLine("svg filter effects smoke failed");
        return 1;
    }
}

// SVG text: fonts added from files, a text element with tspans, text along a path and text as a clip path, through the Svg and Text packages.
{
    var fontOptions = new OneImage.Svg.SvgFontOptions { UseSystemFonts = false };
    fontOptions.AddFont(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Subset-Regular.ttf"));
    fontOptions.AddFont(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Subset-Bold-kern.ttf"));
    fontOptions.SetGenericFamily("sans-serif", "Noto Sans");
    var textConfig = new ImageConfiguration().Add(new OneImage.Svg.SvgFormat(fonts: fontOptions));
    byte[] textDoc = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' viewBox='0 0 100 50' width='100' height='50' font-family='Noto Sans'>"
        + "<defs><path id='p' d='M 5 45 L 95 45'/><clipPath id='c'><text x='60' y='25' font-size='30'>I</text></clipPath></defs>"
        + "<text x='5' y='20' font-size='16' fill='#0000ff' text-decoration='underline'>Hi <tspan font-weight='bold' dx='2' rotate='5'>there</tspan></text>"
        + "<text font-size='8'><textPath xlink:href='#p'>along</textPath></text>"
        + "<rect x='50' width='50' height='30' fill='#ff0000' clip-path='url(#c)'/></svg>");
    Rgba32[] textPixels = Image.Load(textDoc, new LoadOptions { Configuration = textConfig }).ToArray<Rgba32>();
    bool blue = false, red = false, path = false;
    for (int i = 0; i < textPixels.Length; i++)
    {
        int x = i % 100, y = i / 100;
        blue |= y < 25 && x < 50 && textPixels[i].B > 200 && textPixels[i].A > 200;
        red |= x >= 50 && y < 30 && textPixels[i].R > 200 && textPixels[i].A > 200;
        path |= y > 35 && textPixels[i].A > 200;
    }

    if (!blue || !red || !path)
    {
        Console.Error.WriteLine("svg text smoke failed");
        return 1;
    }

    // Vertical text through a use element, with a faded span: ink in a narrow column that runs down the image.
    byte[] verticalDoc = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 80' width='40' height='80' font-family='Noto Sans'>"
        + "<defs><text id='t' x='20' y='4' font-size='12' writing-mode='tb'>Ab<tspan opacity='0.5'>cd</tspan></text></defs><g text-anchor='start'><use href='#t'/></g></svg>");
    Rgba32[] verticalPixels = Image.Load(verticalDoc, new LoadOptions { Configuration = textConfig }).ToArray<Rgba32>();
    int top = 80, bottom = -1, left = 40, right = -1;
    for (int i = 0; i < verticalPixels.Length; i++)
    {
        if (verticalPixels[i].A > 64)
        {
            top = Math.Min(top, i / 40);
            bottom = Math.Max(bottom, i / 40);
            left = Math.Min(left, i % 40);
            right = Math.Max(right, i % 40);
        }
    }

    if (bottom - top < 25 || right - left > 16)
    {
        Console.Error.WriteLine("svg vertical text smoke failed");
        return 1;
    }
}

// Complex scripts: Devanagari (reph, pre-base matra) and Khmer (coeng, pre-base vowel) through the Indic and USE shapers.
{
    var fonts = new OneImage.Text.FontCollection();
    OneImage.Text.FontFamily deva = fonts.Add(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSansDevanagari-Subset-Regular.ttf"));
    OneImage.Text.FontFamily khmer = fonts.Add(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSansKhmer-Subset-Regular.ttf"));
    var devaOptions = new OneImage.Text.TextOptions(deva.CreateFont(32));
    var khmerOptions = new OneImage.Text.TextOptions(khmer.CreateFont(32));
    using var complexCanvas = new Canvas(200, 70, PixelFormat.Rgba8);
    complexCanvas.DrawText("\u0930\u094D\u0915\u093F \u0915\u094D\u0937", devaOptions, new Rgba32(255, 255, 255, 255));
    var khmerBounds = OneImage.Text.TextMeasurer.GetCharacterBounds("\u1780\u17D2\u179A\u17C1", khmerOptions);
    if (complexCanvas.Snapshot().ToArray<Rgba32>().All(p => p.A == 0) || khmerBounds.Count == 0 || OneImage.Text.TextMeasurer.MeasureAdvance("\u0915\u093F", devaOptions).Width <= 0)
    {
        Console.Error.WriteLine("complex script smoke failed");
        return 1;
    }
}

// SVG style sheets, switch with systemLanguage, and an image with a data: URI (a PNG made here, and a nested SVG).
{
    var svgConfig = new ImageConfiguration().Add(new OneImage.Svg.SvgFormat(languages: ["en"]));
    byte[] png;
    using (var pngStream = new MemoryStream())
    {
        Image.FromPixels<Rgba32>([new Rgba32(255, 0, 0, 255), new Rgba32(0, 255, 0, 255), new Rgba32(0, 0, 255, 255), new Rgba32(255, 255, 255, 255)], 2, 2).Save(pngStream, PngFormat.Instance);
        png = pngStream.ToArray();
    }

    string nested = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 4 4'><rect width='4' height='4' fill='#0000ff'/></svg>"));
    byte[] styled = System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 20' width='40' height='20'>"
        + "<style>.a > rect:first-child { fill: #00ff00 !important } rect { fill: #ff0000 }</style>"
        + "<g class='a'><rect width='10' height='10'/></g>"
        + "<switch><rect x='10' width='10' height='10' systemLanguage='de' fill='black'/><rect x='10' width='10' height='10' style='fill: #ffff00'/></switch>"
        + "<image x='20' width='10' height='10' image-rendering='pixelated' href='data:image/png;base64," + Convert.ToBase64String(png) + "'/>"
        + "<image x='30' width='10' height='10' href='data:image/svg+xml;base64," + nested + "'/></svg>");
    Rgba32[] sp = Image.Load(styled, new LoadOptions { Configuration = svgConfig }).ToArray<Rgba32>();
    if (sp[(5 * 40) + 5] != new Rgba32(0, 255, 0, 255) || sp[(5 * 40) + 15] != new Rgba32(255, 255, 0, 255) || sp[(2 * 40) + 22] != new Rgba32(255, 0, 0, 255) || sp[(5 * 40) + 35] != new Rgba32(0, 0, 255, 255))
    {
        Console.Error.WriteLine("svg style, switch and image smoke failed");
        return 1;
    }
}

// Right-to-left text: joined Arabic, Hebrew and a mixed-direction line through the bidirectional algorithm and the Arabic shaper.
{
    var fonts = new OneImage.Text.FontCollection();
    OneImage.Text.FontFamily bidi = fonts.Add(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Bidi-Subset.ttf"));
    var rtl = new OneImage.Text.TextOptions(bidi.CreateFont(28)) { Origin = new System.Numerics.Vector2(6, 4), WrappingLength = 180 };
    string mixed = "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645 abc 123 \u05E9\u05DC\u05D5\u05DD";
    var arabicPixels = new Rgba32[200 * 120];
    Array.Fill(arabicPixels, new Rgba32(255, 255, 255, 255));
    Rgba32[] drawnRtl = Image.FromPixels<Rgba32>(arabicPixels, 200, 120)
        .DrawText(mixed, rtl, OneImage.Drawing.Brush.Solid(new Rgba32(0, 0, 0, 255))).ToArray<Rgba32>();
    OneImage.Text.TextMetrics m = OneImage.Text.TextMeasurer.Measure(mixed, rtl);
    float joined = OneImage.Text.TextMeasurer.MeasureAdvance("\u0628\u0628\u0628", new OneImage.Text.TextOptions(bidi.CreateFont(28))).Width;
    float isolated = OneImage.Text.TextMeasurer.MeasureAdvance("\u0628 \u0628 \u0628", new OneImage.Text.TextOptions(bidi.CreateFont(28))).Width;
    if (!drawnRtl.Any(p => p.R < 100) || m.Lines.Count < 1 || joined <= 0 || joined >= isolated)
    {
        Console.Error.WriteLine("right-to-left text smoke failed");
        return 1;
    }
}

// SVG: the separate package registered through the public format API; style sheets, gradients, patterns, masks, filters, text and the limits.
{
    var svgFonts = new OneImage.Svg.SvgFontOptions { UseSystemFonts = false };
    svgFonts.AddFont(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Subset-Regular.ttf"));
    var svgConfig = new ImageConfiguration().Add(new OneImage.Svg.SvgFormat(fonts: svgFonts));
    var svgLoad = new LoadOptions { Configuration = svgConfig };
    const string Doc = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 40' font-family='Noto Sans'><style>.a{fill:url(#g)}</style>" +
        "<defs><linearGradient id='g'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient>" +
        "<pattern id='p' width='4' height='4' patternUnits='userSpaceOnUse'><rect width='2' height='2' fill='green'/></pattern>" +
        "<mask id='m'><rect width='20' height='40' fill='white'/></mask><filter id='f'><feGaussianBlur stdDeviation='1'/></filter></defs>" +
        "<rect class='a' width='40' height='20' mask='url(#m)'/><rect y='20' width='40' height='10' fill='url(#p)' filter='url(#f)'/>" +
        "<text x='2' y='38' font-size='10'>Hi</text></svg>";
    byte[] svgBytes = System.Text.Encoding.UTF8.GetBytes(Doc);
    Rgba32[] svgPixels = Image.Load(svgBytes, svgLoad).Resize(width: 80).ToArray<Rgba32>();
    ImageInfo svgInfo = Image.Identify(svgBytes, svgLoad);
    bool refused = false;
    try
    {
        var tight = new ImageConfiguration().Add(new OneImage.Svg.SvgFormat(new OneImage.Svg.SvgLimits { MaxElements = 3 }));
        Image.Load(svgBytes, new LoadOptions { Configuration = tight }).ToArray<Rgba32>();
    }
    catch (ImageLimitException)
    {
        refused = true;
    }

    if (svgInfo.Width != 40 || svgPixels.Length != 80 * 80 || svgPixels[(5 * 80) + 5].A == 0 || !refused)
    {
        Console.Error.WriteLine("svg smoke failed");
        return 1;
    }
}

// Layers: a small document with a spill store, groups, a mask, a clipped layer and an adjustment.
{
    string spillDir = Path.Combine(Path.GetTempPath(), "oneimage-aot-spill-" + Guid.NewGuid().ToString("N"));
    try
    {
        using var doc = new OneImage.Layers.Document(300, 200, new OneImage.Layers.DocumentOptions
        {
            Background = new Rgba32(255, 255, 255, 255),
            Spill = new OneImage.Layers.SpillOptions { BudgetBytes = 64 * 1024, Directory = spillDir },
        });
        OneImage.Layers.PixelLayer baseLayer = doc.Add(doc.CreatePixelLayer("base", 200, 150, 20, 10));
        baseLayer.Pixels.Clear(new Rgba32(200, 30, 30, 255));
        baseLayer.Flush();
        OneImage.Layers.LayerMask mask = baseLayer.AddMask(revealAll: false);
        mask.Pixels.Fill(new Rgba32(255, 255, 255, 255), OneImage.Drawing.Path.Rectangle(0, 0, 100, 150), antialias: false);
        mask.Feather = 3;
        OneImage.Layers.GroupLayer group = doc.Add(doc.CreateGroup("group", passThrough: false));
        OneImage.Layers.PixelLayer clipped = group.Add(doc.CreatePixelLayer("clipped", 300, 200));
        clipped.Pixels.Clear(new Rgba32(30, 200, 30, 255));
        clipped.BlendMode = BlendMode.Multiply;
        clipped.ClipToBelow = true;
        group.Opacity = 0.8f;
        doc.Add(doc.CreateAdjustmentLayer("levels", OneImage.Layers.Adjustment.Levels(inBlack: 0.1, inWhite: 0.9)));
        doc.Add(doc.CreateFillLayer("tint", new Rgba32(0, 0, 255, 255))).Opacity = 0.1f;
        Rgba32[] layered = doc.Render().ToArray<Rgba32>();
        string description = doc.Describe().Children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (layered.Length != 300 * 200 || layered[0].A != 255 || description != "4" || doc.SpillStatistics is null)
        {
            Console.Error.WriteLine("layers smoke failed");
            return 1;
        }
    }
    finally
    {
        if (Directory.Exists(spillDir))
        {
            Directory.Delete(spillDir, true);
        }
    }
}

// PSD: a layered document written, read back as layers and as a merged image, and written again identically.
{
    var psdDoc = new OneImage.Layers.Document(64, 48, new OneImage.Layers.DocumentOptions { Background = new Rgba32(255, 255, 255, 255) });
    OneImage.Layers.PixelLayer psdLayer = psdDoc.Add(psdDoc.CreatePixelLayer("layer", 40, 30, 5, 6));
    psdLayer.Pixels.Clear(new Rgba32(200, 30, 30, 255));
    psdLayer.Opacity = 0.5f;
    psdLayer.BlendMode = OneImage.BlendMode.Multiply;
    psdDoc.Add(psdDoc.CreateFillLayer("tint", new Rgba32(0, 0, 255, 255))).Opacity = 0.1f;
    // A new style and new gradient and pattern fills are written as effects and fill settings.
    psdLayer.Style = new OneImage.Layers.LayerStyle([new OneImage.Layers.DropShadowEffect { Distance = 4, Size = 3 }, new OneImage.Layers.StrokeEffect { Size = 2 }]);
    psdDoc.Add(psdDoc.CreateFillLayer("gradient", OneImage.Drawing.Brush.LinearGradient(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(60, 40), [new OneImage.Drawing.ColorStop(0, new Rgba32(255, 0, 0, 255)), new OneImage.Drawing.ColorStop(1, new Rgba32(0, 0, 255, 255))]))).Opacity = 0.2f;
    psdDoc.Add(psdDoc.CreateFillLayer("hatch", OneImage.Drawing.Brush.Hatch(OneImage.Drawing.HatchStyle.Percent20, new Rgba32(0, 0, 0, 255), new Rgba32(255, 255, 255, 255)))).Opacity = 0.1f;
    using var psdFile = OneImage.Psd.PsdFile.FromDocument(psdDoc);
    using var psdBytes = new MemoryStream();
    psdFile.Save(psdBytes);
    using var psdLoaded = OneImage.Psd.PsdFile.Load(psdBytes.ToArray());
    using var psdBytes2 = new MemoryStream();
    psdLoaded.Save(psdBytes2);
    using var psdLoaded2 = OneImage.Psd.PsdFile.Load(psdBytes2.ToArray());
    using var psdBytes3 = new MemoryStream();
    psdLoaded2.Save(psdBytes3);
    Image merged = Image.Load(psdBytes.ToArray(), new LoadOptions { Configuration = new ImageConfiguration().Add(OneImage.Psd.PsdFormat.Instance) });
    Rgba32[] mergedPixels = merged.ToArray<Rgba32>();
    Rgba32[] renderedPixels = psdDoc.Render().ToArray<Rgba32>();
    if (psdLoaded.Document.Root.Children.Count != 4 || psdLoaded.Document.Root.Children[0].Style?.Effects.Count != 2 || psdLoaded.GetInfo(psdLoaded.Document.Root.Children[2]).Kind != OneImage.Psd.PsdLayerKind.GradientOrPatternFill || !psdBytes2.ToArray().AsSpan().SequenceEqual(psdBytes3.ToArray()) || !mergedPixels.AsSpan().SequenceEqual(renderedPixels))
    {
        Console.Error.WriteLine("psd smoke failed");
        return 1;
    }
}

Console.WriteLine("smoke ok: " + Library.Name);
return 0;
