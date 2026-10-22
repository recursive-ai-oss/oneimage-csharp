using System.Buffers.Binary;
using System.Diagnostics;
using ImageMagick;
using OneImage.Tests.Png;

namespace OneImage.Tests.Bmp;

[Collection(MemoryMeasurement.Name)]
public class BmpEncodeAndMalformedTests
{
    private static byte[] Save(byte[] px, int w, int h, PixelFormat f, ImageMetadata? meta = null)
    {
        Image img = PngTestUtil.FromRaw(px, w, h, f);
        if (meta != null)
        {
            img = PngGen.Animation([img], [0], 0, meta);
        }

        using var ms = new MemoryStream();
        img.Save(ms, BmpFormat.Instance);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(5, 4)]
    [InlineData(33, 17)]
    public void WrittenFilesRoundTripAndMagickReadsThem(int w, int h)
    {
        // Opaque RGB.
        byte[] rgb = PngGen.Noise(w, h, PixelFormat.Rgb8, w);
        byte[] f1 = Save(rgb, w, h, PixelFormat.Rgb8);
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(f1.AsSpan(28)));
        Decoded d1 = BmpTestUtil.DecodeBmp(f1);
        Assert.Equal(PixelFormat.Rgb8, d1.Format);
        Assert.Equal(rgb, d1.Data);
        Assert.Equal(BmpTestUtil.ToRgba8(d1), BmpTestUtil.MagickRgba(f1, out _, out _));

        // RGBA with real alpha: 32 bit, alpha preserved.
        byte[] rgba = PngGen.Noise(w, h, PixelFormat.Rgba8, h);
        rgba[3] = 7;
        byte[] f2 = Save(rgba, w, h, PixelFormat.Rgba8);
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(f2.AsSpan(28)));
        Decoded d2 = BmpTestUtil.DecodeBmp(f2);
        Assert.Equal(PixelFormat.Rgba8, d2.Format);
        Assert.Equal(rgba, d2.Data);
        Assert.Equal(rgba, BmpTestUtil.MagickRgba(f2, out _, out _));

        // Fully opaque RGBA is written as 24 bit.
        for (int i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = 255;
        }

        byte[] f3 = Save(rgba, w, h, PixelFormat.Rgba8);
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(f3.AsSpan(28)));
        Assert.Equal(rgba, BmpTestUtil.ToRgba8(BmpTestUtil.DecodeBmp(f3)));

        // Gray expands to color.
        byte[] gray = PngGen.Noise(w, h, PixelFormat.Gray8, 3);
        Decoded d4 = BmpTestUtil.DecodeBmp(Save(gray, w, h, PixelFormat.Gray8));
        for (int i = 0; i < w * h; i++)
        {
            Assert.Equal(new[] { gray[i], gray[i], gray[i] }, d4.Data.AsSpan(i * 3, 3).ToArray());
        }

        // Output is deterministic.
        Assert.Equal(f1, Save(rgb, w, h, PixelFormat.Rgb8));
    }

    [Fact]
    public void SkiaReadsOurFiles()
    {
        const int W = 21, H = 13;
        byte[] rgba = PngGen.Noise(W, H, PixelFormat.Rgba8, 5);
        byte[] file = Save(rgba, W, H, PixelFormat.Rgba8);
        using var bmp = SkiaSharp.SKBitmap.Decode(file, new SkiaSharp.SKImageInfo(W, H, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        Assert.NotNull(bmp);
        byte[] got = new byte[W * H * 4];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), got, 0, got.Length);
        Assert.Equal(rgba, got);
    }

    [Fact]
    public void IccProfileUsesV5HeaderAndRoundTrips()
    {
        byte[] icc = Enumerable.Range(0, 300).Select(i => (byte)(i * 11)).ToArray();
        byte[] px = PngGen.Noise(6, 5, PixelFormat.Rgb8, 1);
        byte[] file = Save(px, 6, 5, PixelFormat.Rgb8, new ImageMetadata { IccProfile = icc });
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14)));
        ImageInfo info = BmpFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default);
        Assert.Equal(icc, info.Metadata.IccProfile.ToArray());
        Assert.Equal(px, BmpTestUtil.DecodeBmp(file).Data);
        Assert.Equal(BmpTestUtil.ToRgba8(BmpTestUtil.DecodeBmp(file)), BmpTestUtil.MagickRgba(file, out _, out _));
    }

    [Fact]
    public void MalformedFilesGiveTypedErrors()
    {
        byte[] px = PngGen.Noise(30, 20, PixelFormat.Rgb8, 9);
        byte[] good = Save(px, 30, 20, PixelFormat.Rgb8);

        // Every truncation inside the headers or pixels.
        for (int len = 0; len < good.Length; len += 37)
        {
            Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.DecodeBmp(good[..len]));
        }

        // Absurd sizes fail from the header, before any allocation.
        foreach ((int w, int h) in new[] { (100_000, 100_000), (int.MaxValue, 5), (5, int.MinValue + 1), (65_535, 65_535) })
        {
            byte[] file = BmpTestUtil.File(BmpTestUtil.InfoHeader(40, w, h, 24, 0), [], new byte[16]);
            long before = GC.GetTotalAllocatedBytes(true);
            Assert.ThrowsAny<ImageException>(() => BmpFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default));
            Assert.True(GC.GetTotalAllocatedBytes(true) - before < 1 << 20);
        }

        // Zero width, odd depth, unknown header size, JPEG/PNG compression, bad offset.
        Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 0, 5, 24, 0), [], new byte[16])));
        Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 4, 4, 7, 0), [], new byte[64])));
        Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(44, 4, 4, 24, 0), [], new byte[64])));
        Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 4, 4, 24, 4), [], new byte[64])));
        byte[] badOffset = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badOffset.AsSpan(10), 0x7FFFFFFF);
        Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.DecodeBmp(badOffset));

        // Bad RLE: delta outside the bitmap, stream that ends early, run past the row (clipped, not an error).
        (byte, byte, byte)[] pal = Enumerable.Range(0, 256).Select(i => ((byte)i, (byte)i, (byte)i)).ToArray();
        byte[] table = BmpTestUtil.PaletteBgrx(pal);
        Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 4, 4, 8, 1), table, [0, 2, 200, 0, 1, 1, 0, 1])));
        Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 4, 4, 8, 1), table, [4, 1, 0, 0])));
        Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 4, 4, 8, 1), table, [0, 3, 1, 2])));
        Decoded clipped = BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 4, 1, 8, 1), table, [200, 9, 0, 1]));
        Assert.All(clipped.Data, v => Assert.Equal(9, v));

        // RLE data cannot hide a bomb: the output is the declared size.
        byte[] longRle = [.. Enumerable.Range(0, 100_000).SelectMany(_ => new byte[] { 255, 1 }), 0, 1];
        Decoded bounded = BmpTestUtil.DecodeBmp(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 8, 8, 8, 1), table, longRle));
        Assert.Equal(8 * 8 * 3, bounded.Data.Length);
    }

    [Fact]
    public void MutatedFilesNeverCrashOrHang()
    {
        var limits = new ImageLimits { MaxPixelsPerFrame = 1 << 22, MaxMetadataBytes = 1 << 20 };
        var seeds = new List<byte[]>
        {
            Save(PngGen.Noise(17, 9, PixelFormat.Rgb8, 1), 17, 9, PixelFormat.Rgb8),
            Save(PngGen.Noise(11, 7, PixelFormat.Rgba8, 2), 11, 7, PixelFormat.Rgba8),
        };
        (byte, byte, byte)[] pal = Enumerable.Range(0, 16).Select(i => ((byte)(i * 16), (byte)i, (byte)(255 - i))).ToArray();
        seeds.Add(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 8, 4, 4, 2), BmpTestUtil.PaletteBgrx(pal), [7, 0x3A, 0, 0, 0, 5, 0x12, 0x34, 0x50, 0, 0, 0, 8, 0x77, 0, 0, 8, 1, 0, 1]));
        seeds.Add(BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 8, 4, 4, 0), BmpTestUtil.PaletteBgrx(pal), new byte[16]));
        var rnd = new Random(777);
        var sw = Stopwatch.StartNew();
        int ok = 0, typed = 0;
        foreach (byte[] seed in seeds)
        {
            for (int i = 0; i < 3000; i++)
            {
                byte[] m = (byte[])seed.Clone();
                for (int e = 1 + rnd.Next(4); e > 0; e--)
                {
                    m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
                }

                try
                {
                    BmpTestUtil.DecodeBmp(m, limits);
                    ok++;
                }
                catch (ImageException)
                {
                    typed++;
                }
            }
        }

        TestContext.Current.SendDiagnosticMessage($"bmp mutation: {ok} decoded, {typed} typed errors, {sw.ElapsedMilliseconds} ms");
        Assert.True(ok > 0 && typed > 0 && sw.Elapsed < TimeSpan.FromSeconds(60));
    }
}
