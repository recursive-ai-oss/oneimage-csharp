using System.Buffers.Binary;
using System.Diagnostics;
using ImageMagick;
using OneImage.Tests.Png;

namespace OneImage.Tests.Bmp;

public class IcoTests
{
    private static byte[] Sprite(int size, int seed, bool alpha)
    {
        byte[] b = PngGen.Noise(size, size, PixelFormat.Rgba8, seed);
        for (int i = 0; i < size * size; i++)
        {
            int x = i % size, y = i / size;
            bool inside = ((x - (size / 2)) * (x - (size / 2))) + ((y - (size / 2)) * (y - (size / 2))) < size * size / 5;
            b[(i * 4) + 3] = alpha ? (inside ? (byte)(128 + (b[(i * 4) + 3] / 2)) : (byte)0) : (byte)255;
        }

        return b;
    }

    private static byte[] OurIco(params (byte[] Px, int Size)[] frames)
    {
        Image[] images = frames.Select(f => PngTestUtil.FromRaw(f.Px, f.Size, f.Size, PixelFormat.Rgba8)).ToArray();
        Image anim = PngGen.Animation(images, new int[frames.Length], 0);
        using var ms = new MemoryStream();
        anim.Save(ms, IcoFormat.Instance);
        return ms.ToArray();
    }

    [Fact]
    public void OurFilesRoundTripWithBmpAndPngEntries()
    {
        var frames = new[] { (Sprite(16, 1, true), 16), (Sprite(48, 2, true), 48), (Sprite(256, 3, true), 256), (Sprite(32, 4, false), 32) };
        byte[] file = OurIco(frames);
        ImageInfo info = IcoFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default);
        Assert.Equal([16, 48, 256, 32], info.Frames.Select(f => f.Width).ToArray()); // file order, as written
        Assert.All(info.Frames, f => Assert.Equal(PixelFormat.Rgba8, f.Format));
        for (int i = 0; i < frames.Length; i++)
        {
            Assert.Equal(frames[i].Item1, BmpTestUtil.Decode(IcoFormat.Instance, file, i).Data);
        }

        // Entry 2 (256x256) is PNG, the others BMP DIBs.
        int dirOffset(int i) => (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(6 + (i * 16) + 12));
        Assert.True(PngFormat.Instance.Matches(file.AsSpan(dirOffset(2))));
        Assert.False(PngFormat.Instance.Matches(file.AsSpan(dirOffset(0))));
        Assert.Equal(file, OurIco(frames));

        // ImageMagick agrees on every frame.
        using var coll = new MagickImageCollection(file, MagickFormat.Ico);
        Assert.Equal(4, coll.Count);
        for (int i = 0; i < 4; i++)
        {
            using var px = coll[i].GetPixels();
            Assert.Equal(frames[i].Item1, px.ToByteArray(PixelMapping.RGBA));
        }
    }

    [Fact]
    public void MagickWrittenIconsDecodeLikeMagick()
    {
        using var coll = new MagickImageCollection();
        foreach (int size in new[] { 16, 24, 64, 256 })
        {
            coll.Add(new MagickImage(Sprite(size, size, true), new PixelReadSettings((uint)size, (uint)size, StorageType.Char, PixelMapping.RGBA)));
        }

        byte[] file = coll.ToByteArray(MagickFormat.Ico);
        if (Environment.GetEnvironmentVariable("ONEIMAGE_WRITE_CORPUS") is { } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "magick-4-sizes.ico"), file);
        }

        ImageInfo info = IcoFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default);
        using var back = new MagickImageCollection(file, MagickFormat.Ico);
        Assert.Equal(back.Count, info.Frames.Count);
        for (int i = 0; i < back.Count; i++)
        {
            using var px = back[i].GetPixels();
            byte[] reference = px.ToByteArray(PixelMapping.RGBA)!;
            Decoded ours = BmpTestUtil.Decode(IcoFormat.Instance, file, i);
            Assert.Equal((int)back[i].Width, ours.Width);
            Assert.Equal(reference, ours.Data);
        }
    }

    [Fact]
    public void LowDepthEntryUsesTheAndMask()
    {
        // 4x4 24-bit DIB: pixels red, mask makes the left half transparent.
        const int W = 4, H = 4;
        byte[] dib = BmpTestUtil.InfoHeader(40, W, H * 2, 24, 0);
        byte[] pixels = new byte[12 * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                pixels[(y * 12) + (x * 3) + 2] = 200; // red in BGR order
            }
        }

        byte[] mask = new byte[4 * H];
        for (int y = 0; y < H; y++)
        {
            mask[y * 4] = 0b1100_0000;
        }

        byte[] entry = [.. dib, .. pixels, .. mask];
        byte[] file = new byte[6 + 16 + entry.Length];
        file[2] = 1;
        file[4] = 1;
        file[6] = W;
        file[7] = H;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14), (uint)entry.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(18), 22);
        entry.CopyTo(file, 22);
        Decoded d = BmpTestUtil.Decode(IcoFormat.Instance, file);
        for (int x = 0; x < W; x++)
        {
            Assert.Equal(x < 2 ? 0 : 255, d.Data[(x * 4) + 3]);
            Assert.Equal(200, d.Data[x * 4]);
        }
    }

    [Fact]
    public void MalformedIconsGiveTypedErrors()
    {
        byte[] good = OurIco((Sprite(16, 1, true), 16), (Sprite(32, 2, true), 32));
        // Anything cut before the last entry's data is an error; a missing tail of the last AND mask is tolerated.
        int lastEntry = (int)BinaryPrimitives.ReadUInt32LittleEndian(good.AsSpan(6 + 16 + 12));
        for (int len = 0; len < lastEntry; len += 29)
        {
            try
            {
                BmpTestUtil.Decode(IcoFormat.Instance, good[..len]);
                Assert.Fail($"length {len} decoded");
            }
            catch (ImageFormatException)
            {
            }
        }

        // Entry count beyond the limit, directory past the end, offset outside the file.
        byte[] many = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(many.AsSpan(4), 60000);
        Assert.ThrowsAny<ImageException>(() => BmpTestUtil.Decode(IcoFormat.Instance, many));
        byte[] badOffset = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badOffset.AsSpan(18), 0x7FFFFFFF);
        Assert.ThrowsAny<ImageFormatException>(() => BmpTestUtil.Decode(IcoFormat.Instance, badOffset));

        // A PNG entry claiming a huge image is a limit error from its header.
        byte[] hugePng = MiniPng.Concat(MiniPng.Signature, MiniPng.Chunk("IHDR", MiniPng.Ihdr(60000, 60000)), MiniPng.Chunk("IEND", []));
        byte[] ico = new byte[22 + hugePng.Length];
        ico[2] = 1;
        ico[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), (uint)hugePng.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
        hugePng.CopyTo(ico, 22);
        Assert.ThrowsAny<ImageLimitException>(() => BmpTestUtil.Decode(IcoFormat.Instance, ico));

        Assert.Throws<ImageOperationException>(() => OurIcoTooLarge());
    }

    private static void OurIcoTooLarge() => OurIco((new byte[300 * 300 * 4], 300));

    [Fact]
    public void MutatedFilesNeverCrashOrHang()
    {
        var limits = new ImageLimits { MaxPixelsPerFrame = 1 << 22, MaxMetadataBytes = 1 << 20 };
        byte[] seed = OurIco((Sprite(16, 1, true), 16), (Sprite(20, 2, false), 20));
        var rnd = new Random(4242);
        var sw = Stopwatch.StartNew();
        int ok = 0, typed = 0;
        for (int i = 0; i < 6000; i++)
        {
            byte[] m = (byte[])seed.Clone();
            for (int e = 1 + rnd.Next(4); e > 0; e--)
            {
                m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
            }

            try
            {
                ImageInfo info = IcoFormat.Instance.Decoder!.ReadInfo(new MemoryStream(m), limits);
                for (int f = 0; f < info.Frames.Count; f++)
                {
                    BmpTestUtil.Decode(IcoFormat.Instance, m, f, limits);
                }

                ok++;
            }
            catch (ImageException)
            {
                typed++;
            }
        }

        TestContext.Current.SendDiagnosticMessage($"ico mutation: {ok} decoded, {typed} typed errors, {sw.ElapsedMilliseconds} ms");
        Assert.True(ok > 0 && typed > 0 && sw.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void FormatDetectionDistinguishesTheThreeFormats()
    {
        var config = ImageConfiguration.Default;
        byte[] ico = OurIco((Sprite(16, 1, true), 16));
        byte[] bmp = BmpTestUtil.File(BmpTestUtil.InfoHeader(40, 1, 1, 24, 0), [], new byte[4]);
        byte[] png = PngTestUtil.Encode(new byte[12], 2, 2, PixelFormat.Rgb8);
        Assert.Same(IcoFormat.Instance, config.Detect(ico));
        Assert.Same(BmpFormat.Instance, config.Detect(bmp));
        Assert.Same(PngFormat.Instance, config.Detect(png));
        Assert.Null(config.Detect("BMxxxxxxxxxxxxxxxxxxxx"u8));
        Assert.Same(PngFormat.Instance, config.FindByExtension("x.PNG"));
        Assert.Same(IcoFormat.Instance, config.FindByExtension("favicon.ico"));
        // End to end through the public API.
        using var ms = new MemoryStream(ico);
        Image img = Image.Load(ms);
        Assert.Equal((16, 16, 1), (img.Width, img.Height, img.FrameCount));
    }
}
