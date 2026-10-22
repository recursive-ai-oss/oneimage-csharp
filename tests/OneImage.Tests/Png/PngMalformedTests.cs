using System.Buffers.Binary;
using System.Diagnostics;

namespace OneImage.Tests.Png;

[Collection(MemoryMeasurement.Name)]
public class PngMalformedTests
{
    private static readonly ImageLimits Small = new() { MaxPixelsPerFrame = 1 << 22, MaxMetadataBytes = 1 << 20 };

    private static byte[] Valid(int w = 40, int h = 30) => PngTestUtil.Encode(PngGen.Photo(w, h, PixelFormat.Rgb8, 1), w, h, PixelFormat.Rgb8);

    private static void AssertTyped(byte[] file, ImageLimits? limits = null)
    {
        try
        {
            PngTestUtil.Decode(file, 0, limits ?? Small);
        }
        catch (ImageException)
        {
        }
    }

    [Fact]
    public void EveryTruncationOfTheImageDataIsAFormatError()
    {
        byte[] file = Valid();
        int idatEnd = PngGen.Chunks(file).Where(c => c.Name == "IDAT").Max(c => c.Data + c.Length + 4);
        for (int len = 0; len < idatEnd; len += 5)
        {
            Assert.ThrowsAny<ImageFormatException>(() => PngTestUtil.Decode(file[..len]));
        }

        // Losing only IEND is fine: all pixels are there.
        Assert.NotNull(PngTestUtil.Decode(file[..^12]));
    }

    [Fact]
    public void BadCrcInCriticalChunksIsAnErrorAndInAncillaryChunksIsIgnored()
    {
        byte[] file = Valid();
        foreach (string critical in new[] { "IHDR", "IDAT" })
        {
            byte[] bad = (byte[])file.Clone();
            var c = PngGen.Chunks(bad).First(x => x.Name == critical);
            bad[c.Data + c.Length] ^= 0x55;
            Assert.ThrowsAny<ImageFormatException>(() => PngTestUtil.Decode(bad));
        }

        byte[] gama = MiniPng.Chunk("gAMA", [0, 0, 0xB1, 0x8F], badCrc: true);
        byte[] raw = MiniPng.RawRgba(MiniPng.Rgba(3, 3, 1, 2, 3, 4), 3, 3);
        byte[] png = MiniPng.Plain(3, 3, 8, 6, raw, gama);
        Decoded d = PngTestUtil.Decode(png);
        Assert.Equal(MiniPng.Rgba(3, 3, 1, 2, 3, 4), d.Data);
        Assert.Null(((PngInfo)PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(png), ImageLimits.Default).DecoderState!).Gamma);
    }

    [Fact]
    public void AbsurdDimensionsFailBeforeAnyAllocation()
    {
        foreach ((int w, int h) in new[] { (100_000, 100_000), (65_535, 65_535), (int.MaxValue, 1), (1, int.MaxValue) })
        {
            byte[] file = MiniPng.Concat(MiniPng.Signature, MiniPng.Chunk("IHDR", MiniPng.Ihdr(w, h)), MiniPng.Chunk("IDAT", [0x78, 1]), MiniPng.Chunk("IEND", []));
            long before = GC.GetTotalAllocatedBytes(true);
            Assert.ThrowsAny<ImageLimitException>(() => PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default));
            Assert.True(GC.GetTotalAllocatedBytes(true) - before < 1 << 20);
        }

        byte[] zero = MiniPng.Concat(MiniPng.Signature, MiniPng.Chunk("IHDR", MiniPng.Ihdr(0, 5)), MiniPng.Chunk("IEND", []));
        Assert.Throws<ImageFormatException>(() => PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(zero), ImageLimits.Default));
    }

    [Fact]
    public void HugeChunkLengthsAreRejectedWithoutAllocating()
    {
        foreach (string type in new[] { "eXIf", "iCCP", "tEXt", "PLTE", "tRNS", "IDAT", "zzZz", "fcTL" })
        {
            byte[] chunk = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(chunk, 0x7FFFFFF0);
            System.Text.Encoding.ASCII.GetBytes(type, chunk.AsSpan(4));
            byte[] file = MiniPng.Concat(MiniPng.Signature, MiniPng.Chunk("IHDR", MiniPng.Ihdr(4, 4)), chunk);
            long before = GC.GetTotalAllocatedBytes(true);
            Assert.ThrowsAny<ImageException>(() => PngTestUtil.Decode(file));
            Assert.True(GC.GetTotalAllocatedBytes(true) - before < 1 << 20, type);
        }
    }

    [Fact]
    public void ZlibBombInImageDataIsCappedByTheImageSizeAndTheRatioLimit()
    {
        // A 100x100 image whose data inflates to 64 MB: only the rows the header promises are ever produced.
        byte[] z = MiniPng.Zlib(new byte[64 << 20]);
        byte[] file = MiniPng.Concat(MiniPng.Signature, MiniPng.Chunk("IHDR", MiniPng.Ihdr(100, 100, 8, 0)), MiniPng.Chunk("IDAT", z), MiniPng.Chunk("IEND", []));
        long before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        Assert.Equal(new byte[100 * 100], PngTestUtil.Decode(file).Data);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 8 << 20);

        // The same bytes under a stricter ratio limit are refused.
        Assert.Throws<ImageLimitException>(() => PngTestUtil.Decode(file, 0, new ImageLimits { MaxExpansionRatio = 100 }));

        // A frame that claims far more data than the file holds is truncated, not a bomb.
        byte[] big = MiniPng.Concat(MiniPng.Signature, MiniPng.Chunk("IHDR", MiniPng.Ihdr(16000, 16000, 8, 0)), MiniPng.Chunk("IDAT", MiniPng.Zlib(new byte[16001 * 100])), MiniPng.Chunk("IEND", []));
        Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode(big));
    }

    [Fact]
    public void ZlibBombInIccpIsALimitErrorAndZtxtIsNeverInflated()
    {
        byte[] bomb = MiniPng.Zlib(new byte[8 << 20]);
        byte[] iccp = MiniPng.Chunk("iCCP", [.. "p\0\0"u8, .. bomb]);
        byte[] raw = MiniPng.RawRgba(MiniPng.Rgba(2, 2, 1, 2, 3, 4), 2, 2);
        byte[] withIccp = MiniPng.Plain(2, 2, 8, 6, raw, iccp);
        Assert.Throws<ImageLimitException>(() => PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(withIccp), Small));

        byte[] ztxt = MiniPng.Chunk("zTXt", [.. "Comment\0\0"u8, .. bomb]);
        byte[] withZtxt = MiniPng.Plain(2, 2, 8, 6, raw, ztxt);
        long before = GC.GetTotalAllocatedBytes(true);
        Assert.Equal(MiniPng.Rgba(2, 2, 1, 2, 3, 4), PngTestUtil.Decode(withZtxt, 0, Small).Data);
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 4 << 20);

        // A damaged (not oversized) profile is dropped without failing the image.
        byte[] junk = MiniPng.Chunk("iCCP", [.. "p\0\0"u8, 1, 2, 3, 4, 5, 6, 7, 8]);
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(MiniPng.Plain(2, 2, 8, 6, raw, junk)), Small);
        Assert.True(info.Metadata.IccProfile.IsEmpty);
    }

    [Fact]
    public void InvalidStructureIsAFormatError()
    {
        byte[] raw = MiniPng.RawRgba(MiniPng.Rgba(2, 2, 1, 2, 3, 4), 2, 2);
        byte[] badFilter = (byte[])raw.Clone();
        badFilter[0] = 5;
        Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode(MiniPng.Plain(2, 2, 8, 6, badFilter)));
        // palette image without PLTE
        Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode(MiniPng.Plain(2, 2, 8, 3, new byte[6])));
        // unknown critical chunk
        Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode(MiniPng.Plain(2, 2, 8, 6, raw, MiniPng.Chunk("ABCD", [1]))));
        // unknown ancillary chunk is fine
        Assert.NotNull(PngTestUtil.Decode(MiniPng.Plain(2, 2, 8, 6, raw, MiniPng.Chunk("abcd", [1]))));
        // no image data
        Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode(MiniPng.Concat(MiniPng.Signature, MiniPng.Chunk("IHDR", MiniPng.Ihdr(2, 2)), MiniPng.Chunk("IEND", []))));
        // wrong bit depth for the color type
        Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode(MiniPng.Plain(2, 2, 4, 6, raw)));
        // not a PNG at all
        Assert.Throws<ImageFormatException>(() => PngTestUtil.Decode("this is not a png file"u8.ToArray()));
        // palette index beyond the palette decodes (as black) instead of failing
        byte[] plte = MiniPng.Chunk("PLTE", [255, 0, 0]);
        Decoded d = PngTestUtil.Decode(MiniPng.Plain(2, 1, 8, 3, [0, 0, 7], plte));
        Assert.Equal([255, 0, 0, 0, 0, 0], d.Data);
    }

    [Fact]
    public void RegionDecodeMatchesCropOfFullDecode()
    {
        foreach (bool interlaced in new[] { false, true })
        {
            byte[] file = interlaced
                ? File.ReadAllBytes(Path.Combine(PngTestUtil.DataDir("png", "pngsuite"), "basi2c08.png"))
                : Valid(70, 50);
            using var ms = new MemoryStream(file);
            ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(ms, ImageLimits.Default);
            Decoded full = PngTestUtil.Decode(file);
            int bpp = full.Format.BytesPerPixel;
            var r = new ImageRect(3, 5, Math.Min(20, full.Width - 3), Math.Min(9, full.Height - 5));
            var sink = new CollectSink(r.Width, r.Height, info.Frames[0].Format);
            PngFormat.Instance.Decoder.DecodeFrame(ms, info, 0, new DecodeRequest(r), sink, ImageLimits.Default, CancellationToken.None);
            for (int y = 0; y < r.Height; y++)
            {
                Assert.Equal(
                    full.Data.AsSpan((((r.Y + y) * full.Width) + r.X) * bpp, r.Width * bpp).ToArray(),
                    sink.Data.AsSpan(y * r.Width * bpp, r.Width * bpp).ToArray());
            }
        }
    }

    [Fact]
    public void CancellationIsObserved()
    {
        byte[] file = Valid(200, 400);
        using var ms = new MemoryStream(file);
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(ms, ImageLimits.Default);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PngFormat.Instance.Decoder.DecodeFrame(ms, info, 0, new DecodeRequest(new ImageRect(0, 0, 200, 400)), new CollectSink(200, 400, info.Frames[0].Format), ImageLimits.Default, cts.Token));
    }

    [Fact]
    public void MutatedFilesNeverCrashOrHang()
    {
        var seeds = new List<byte[]>
        {
            Valid(33, 21),
            PngTestUtil.Encode(PngGen.Noise(19, 13, PixelFormat.Rgba16, 4), 19, 13, PixelFormat.Rgba16),
            PngTestUtil.Encode(PngGen.Screenshot(200, 100, 5), 200, 100, PixelFormat.Rgba8),
            MiniPng.Apng(8, 8,
            [
                new(0, 0, 8, 8, MiniPng.Rgba(8, 8, 9, 8, 7, 255)),
                new(2, 2, 3, 3, MiniPng.Rgba(3, 3, 1, 1, 1, 128), Dispose: 2, Blend: 1),
                new(1, 1, 4, 2, MiniPng.Rgba(4, 2, 5, 5, 5, 255), Dispose: 1),
            ]),
        };
        string dir = PngTestUtil.DataDir("png", "pngsuite");
        foreach (string name in new[] { "basi0g01", "basi3p04", "basi6a16", "basn4a08", "s09n3p02", "tbbn3p08", "tp0n2c08", "oi2n0g16", "bgwn6a08" })
        {
            seeds.Add(File.ReadAllBytes(Path.Combine(dir, name + ".png")));
        }

        var rnd = new Random(12345);
        var sw = Stopwatch.StartNew();
        int ok = 0, typed = 0;
        foreach (byte[] seed in seeds)
        {
            for (int i = 0; i < 1500; i++)
            {
                byte[] m = (byte[])seed.Clone();
                int edits = 1 + rnd.Next(4);
                for (int e = 0; e < edits; e++)
                {
                    m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
                }

                // Often fix the CRCs so the mutated content reaches the deeper parsing.
                if (rnd.Next(2) == 0)
                {
                    FixCrcs(m);
                }

                try
                {
                    ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(m), Small);
                    for (int f = 0; f < Math.Min(info.Frames.Count, 3); f++)
                    {
                        PngTestUtil.DecodeWith(PngFormat.Instance, m, f, Small);
                    }

                    ok++;
                }
                catch (ImageException)
                {
                    typed++;
                }
            }
        }

        TestContext.Current.SendDiagnosticMessage($"mutation: {ok} decoded, {typed} typed errors, {sw.ElapsedMilliseconds} ms");
        Assert.True(typed > 0 && ok > 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(120));
    }

    private static void FixCrcs(byte[] png)
    {
        int p = 8;
        while (p + 12 <= png.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            if (len > png.Length - p - 12)
            {
                return;
            }

            uint crc = System.IO.Hashing.Crc32.HashToUInt32(png.AsSpan(p + 4, 4 + (int)len));
            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(p + 8 + (int)len), crc);
            p += 12 + (int)len;
        }
    }
}
