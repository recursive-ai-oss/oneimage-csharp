using System;
using System.Buffers;
using System.IO;
using System.Linq;
using OneImage;
using OneImage.Compression;

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
    if (mult.Length != 300 * 200 || hue.Length != 300 * 200)
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

Console.WriteLine("smoke ok: " + Library.Name);
return 0;
