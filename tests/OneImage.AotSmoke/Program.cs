using System;
using System.Buffers;
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

Console.WriteLine("smoke ok: " + Library.Name);
return 0;
