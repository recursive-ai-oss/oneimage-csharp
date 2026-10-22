using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>
/// A tiny file whose header declares a huge frame must fail with a typed error after allocating only what it reached,
/// not a frame-sized buffer. These tests count allocations, so they run alone.
/// </summary>
[Collection(MemoryMeasurement.Name)]
public sealed class WebPMemoryTests
{
    private const long Budget = 16L * 1024 * 1024;
    private static readonly WebPFormat Format = WebPFormat.Instance;

    private sealed class NullSink(int w, int h, PixelFormat f) : IRowSink
    {
        public int Width => w;

        public int Height => h;

        public PixelFormat Format => f;

        public int Rows { get; private set; }

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount) => Rows += rowCount;
    }

    private static byte[] Encode(byte[] rgba, int w, int h, bool lossless)
    {
        Image img = Image.FromPixels<Rgba32>(MemoryMarshal.Cast<byte, Rgba32>(rgba), w, h);
        using var ms = new MemoryStream();
        img.Save(ms, Format, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = lossless } });
        return ms.ToArray();
    }

    private static byte[] Riff(IEnumerable<(string Id, byte[] Data)> chunks)
    {
        var body = new List<byte>("WEBP"u8.ToArray());
        foreach ((string id, byte[] data) in chunks)
        {
            body.AddRange(Encoding.ASCII.GetBytes(id));
            body.AddRange(BitConverter.GetBytes((uint)data.Length));
            body.AddRange(data);
            if ((data.Length & 1) != 0)
            {
                body.Add(0);
            }
        }

        var file = new List<byte>("RIFF"u8.ToArray());
        file.AddRange(BitConverter.GetBytes((uint)body.Count));
        file.AddRange(body);
        return file.ToArray();
    }

    private static byte[] Chunk(byte[] file, string id)
    {
        int pos = 12;
        while (pos + 8 <= file.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
            if (Encoding.ASCII.GetString(file, pos, 4) == id)
            {
                return file.AsSpan(pos + 8, (int)size).ToArray();
            }

            pos += 8 + (int)size + (int)(size & 1);
        }

        throw new InvalidOperationException(id);
    }

    private static byte[] Noise(int w, int h) => WebPOracle.MakeImage(w, h, 99);

    /// <summary>A lossless stream of a noisy image whose declared size is rewritten to 16383 x 16383.</summary>
    internal static byte[] HugeLossless()
    {
        byte[] vp8l = Chunk(Encode(Noise(24, 8), 24, 8, true), "VP8L");
        // Signature byte, then 14 bits of width - 1 and 14 bits of height - 1.
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(vp8l.AsSpan(1));
        bits = (bits & ~0x0fffffffu) | 16382u | (16382u << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(vp8l.AsSpan(1), bits);
        return Riff([("VP8L", vp8l)]);
    }

    private static byte[] WithHugeVp8Size(byte[] vp8)
    {
        byte[] d = (byte[])vp8.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(6), 16383);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), 16383);
        return d;
    }

    internal static byte[] HugeLossy()
    {
        byte[] vp8 = WithHugeVp8Size(Chunk(Encode(Noise(16, 16), 16, 16, false), "VP8 "));
        return Riff([("VP8 ", vp8)]);
    }

    internal static byte[] HugeLossyWithAlpha()
    {
        byte[] vp8 = WithHugeVp8Size(Chunk(Encode(Noise(16, 16), 16, 16, false), "VP8 "));
        // Alpha: lossless-compressed (method 1) noise that is not a valid stream for this size.
        byte[] alph = new byte[40];
        alph[0] = 1;
        new Random(5).NextBytes(alph.AsSpan(1));
        byte[] vp8x = [0x10, 0, 0, 0, 0xfe, 0x3f, 0, 0xfe, 0x3f, 0];
        return Riff([("VP8X", vp8x), ("ALPH", alph), ("VP8 ", vp8)]);
    }

    private static void AssertFailsSmall(byte[] file)
    {
        Assert.True(file.Length < 1024, "the file should be tiny");
        long before = GC.GetAllocatedBytesForCurrentThread();
        Exception? error = null;
        try
        {
            using var ms = new MemoryStream(file);
            ImageInfo info = Format.Decoder!.ReadInfo(ms, new ImageLimits());
            Assert.Equal(16383, info.Width);
            Assert.Equal(16383, info.Height);
            var sink = new NullSink(info.Width, info.Height, info.Frames[0].Format);
            Format.Decoder.DecodeFrame(ms, info, 0, new DecodeRequest(new ImageRect(0, 0, info.Width, info.Height)), sink, new ImageLimits(), CancellationToken.None);
        }
        catch (Exception e)
        {
            error = e;
        }

        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(error is ImageFormatException or ImageLimitException, "expected a typed error, got " + (error?.ToString() ?? "success"));
        Assert.True(alloc < Budget, $"allocated {alloc} bytes");
    }

    [Fact]
    public void TinyLosslessFileDeclaringHugeFrameAllocatesLittle() => AssertFailsSmall(HugeLossless());

    [Fact]
    public void TinyLossyFileDeclaringHugeFrameAllocatesLittle() => AssertFailsSmall(HugeLossy());

    [Fact]
    public void TinyLossyFileWithAlphaDeclaringHugeFrameAllocatesLittle() => AssertFailsSmall(HugeLossyWithAlpha());

    [Fact]
    public void CommittedHugeHeaderFilesFailSmall()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "webp");
        foreach (string name in new[] { "huge-lossless-truncated.webp", "huge-lossy-truncated.webp", "huge-lossy-alpha-truncated.webp" })
        {
            AssertFailsSmall(File.ReadAllBytes(Path.Combine(dir, name)));
        }
    }

    [Fact]
    public void LosslessDecodeOfLargeImageMatchesLibwebpAndStaysBounded()
    {
        // 1400 x 1000 is past the 2^20-pixel match window, so the sliding window and its reference reach are exercised.
        (_, byte[] rgba, int w, int h) = WebPCorpus.Photos(large: true).First(p => p.Name == "plasma-large");
        byte[] file = WebPOracle.SkiaEncode(rgba, w, h, lossless: true, quality: 75);
        byte[] theirs = WebPOracle.SkiaDecode(file, out _, out _);
        using var ms = new MemoryStream(file);
        ImageInfo info = Format.Decoder!.ReadInfo(ms, new ImageLimits());
        var all = new CollectSink(w, h, info.Frames[0].Format);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Format.Decoder.DecodeFrame(ms, info, 0, new DecodeRequest(new ImageRect(0, 0, w, h)), all, new ImageLimits(), CancellationToken.None);
        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(theirs, all.Data);
        // The output buffer itself is 12 MB; decoding adds a bounded window (~10 MB) rather than a second full frame.
        Assert.True(alloc < w * h * 4L + (20L << 20), $"allocated {alloc}");
    }

    private sealed class CollectSink : IRowSink
    {
        public CollectSink(int w, int h, PixelFormat f)
        {
            Width = w;
            Height = h;
            Format = f;
            Data = new byte[w * h * 4];
        }

        public int Width { get; }

        public int Height { get; }

        public PixelFormat Format { get; }

        public byte[] Data { get; }

        public void WriteRows(int y, ReadOnlySpan<byte> rows, int rowCount)
        {
            int bpp = Format.BytesPerPixel;
            for (int r = 0; r < rowCount; r++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int o = (((y + r) * Width) + x) * 4;
                    int s = ((r * Width) + x) * bpp;
                    Data[o] = rows[s];
                    Data[o + 1] = rows[s + 1];
                    Data[o + 2] = rows[s + 2];
                    Data[o + 3] = bpp == 4 ? rows[s + 3] : (byte)255;
                }
            }
        }
    }

    /// <summary>Regenerates the committed repro files: set ONEIMAGE_WEBP_WRITE_TESTDATA to the target directory.</summary>
    [Fact]
    public void WriteTestData()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_WEBP_WRITE_TESTDATA");
        Assert.SkipUnless(dir != null, "generator only");
        Directory.CreateDirectory(dir!);
        File.WriteAllBytes(Path.Combine(dir!, "huge-lossless-truncated.webp"), HugeLossless());
        File.WriteAllBytes(Path.Combine(dir!, "huge-lossy-truncated.webp"), HugeLossy());
        File.WriteAllBytes(Path.Combine(dir!, "huge-lossy-alpha-truncated.webp"), HugeLossyWithAlpha());
        File.WriteAllBytes(Path.Combine(dir!, "palette4-bundled.webp"), Encode(WebPOracle.MakePalette(21, 9, 4, 1), 21, 9, true));
        File.WriteAllBytes(Path.Combine(dir!, "lossless-alpha-13x7.webp"), Encode(WebPOracle.MakeImage(13, 7, 3, alpha: true), 13, 7, true));
        File.WriteAllBytes(Path.Combine(dir!, "lossy-alpha-17x19.webp"), Encode(WebPOracle.MakeImage(17, 19, 4, alpha: true, noisy: false), 17, 19, false));
        File.WriteAllBytes(Path.Combine(dir!, "lossy-1x1.webp"), Encode([10, 200, 30, 255], 1, 1, false));
        var frames = new List<WebPAnimationTests.Anmf>
        {
            new(0, 0, 16, 12, 40, true, false, WebPOracle.MakeImage(16, 12, 7, alpha: true, noisy: false)),
            new(4, 2, 8, 6, 80, true, true, WebPOracle.MakeImage(8, 6, 8, alpha: true, noisy: false)),
        };
        File.WriteAllBytes(Path.Combine(dir!, "anim-blend-dispose.webp"), WebPAnimationTests.BuildAnimation(16, 12, 2, 0xff808080u, frames));
    }
}

public sealed class WebPTestDataTests
{
    [Fact]
    public void CommittedFilesDecodeOrFailTyped()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "webp");
        foreach (string path in Directory.GetFiles(dir, "*.webp"))
        {
            if (Path.GetFileName(path).StartsWith("huge-", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] file = File.ReadAllBytes(path);
            Image img = Image.Load(file);
            foreach (ImageFrame f in img.Frames)
            {
                _ = f.ToArray<Rgba32>();
            }

            // libwebp agrees on what the file contains.
            byte[] theirs = WebPOracle.SkiaDecode(file, out int w, out int h);
            Assert.Equal((w, h), (img.Width, img.Height));
            if (img.FrameCount == 1)
            {
                byte[] ours = MemoryMarshal.AsBytes(img.ToArray<Rgba32>().AsSpan()).ToArray();
                for (int i = 0; i < ours.Length; i += 4)
                {
                    Assert.True(Math.Abs(ours[i + 3] - theirs[i + 3]) <= 1, path);
                    if (ours[i + 3] != 0)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            Assert.True(Math.Abs(ours[i + c] - theirs[i + c]) <= 1, path);
                        }
                    }
                }
            }
        }
    }
}
