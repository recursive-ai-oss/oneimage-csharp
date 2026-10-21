using System;
using System.Buffers;
using System.IO;
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

Console.WriteLine("smoke ok: " + Library.Name);
return 0;
