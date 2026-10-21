using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>Hostile input: every failure must be a typed exception, quickly, without large allocations.</summary>
public sealed class WebPRobustnessTests
{
    private static readonly WebPFormat Format = new();

    private static byte[] Encode(byte[] rgba, int w, int h, bool lossless, int quality = 75)
    {
        Image img = Image.FromPixels<Rgba32>(MemoryMarshal.Cast<byte, Rgba32>(rgba), w, h);
        using var ms = new MemoryStream();
        img.Save(ms, Format, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = lossless, Quality = quality } });
        return ms.ToArray();
    }

    private static List<(string Name, byte[] Data)> Seeds()
    {
        var seeds = new List<(string, byte[])>
        {
            ("lossless", Encode(WebPOracle.MakeImage(37, 29, 1), 37, 29, true)),
            ("lossless-alpha", Encode(WebPOracle.MakeImage(33, 21, 2, alpha: true), 33, 21, true)),
            ("palette", Encode(WebPOracle.MakePalette(40, 30, 12, 3), 40, 30, true)),
            ("lossy", Encode(WebPOracle.MakeImage(45, 35, 4), 45, 35, false)),
            ("lossy-alpha", Encode(WebPOracle.MakeImage(31, 40, 5, alpha: true), 31, 40, false, 50)),
        };
        var frames = new List<Image>();
        for (int i = 0; i < 3; i++)
        {
            frames.Add(Image.FromPixels<Rgba32>(MemoryMarshal.Cast<byte, Rgba32>(WebPOracle.MakeImage(24, 20, 10 + i, alpha: true)), 24, 20));
        }

        using var ms = new MemoryStream();
        WebPAnimationTests.AnimationOf(frames, [10, 20, 30], 2).Save(ms, Format);
        seeds.Add(("anim", ms.ToArray()));
        return seeds;
    }

    /// <summary>Decodes everything; returns false on a typed failure. Anything else propagates and fails the test.</summary>
    private static bool TryDecodeAll(byte[] data)
    {
        try
        {
            Image img = Image.Load(data, new LoadOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 1 << 22, MaxTotalPixels = 1 << 24 } });
            foreach (ImageFrame f in img.Frames)
            {
                _ = f.ToArray<Rgba32>();
            }

            return true;
        }
        catch (ImageFormatException)
        {
            return false;
        }
        catch (ImageLimitException)
        {
            return false;
        }
    }

    [Fact]
    public void SeedsDecode()
    {
        foreach ((string name, byte[] data) in Seeds())
        {
            Assert.True(TryDecodeAll(data), name);
        }
    }

    [Fact]
    public void TruncationAtEveryScaleFailsCleanly()
    {
        foreach ((string name, byte[] data) in Seeds())
        {
            int step = Math.Max(1, data.Length / 150);
            for (int len = 0; len < data.Length; len += step)
            {
                // A cut file is either rejected or (when only trailing chunks are missing) still decodes; both are fine.
                _ = TryDecodeAll(data.AsSpan(0, len).ToArray());
            }

            Assert.False(TryDecodeAll(data.AsSpan(0, data.Length / 2).ToArray()) && name is "lossless" or "lossy", name);
        }
    }

    [Fact]
    public void SeededMutationsNeverEscapeTheTypedExceptions()
    {
        var rnd = new Random(20261021);
        long start = Environment.TickCount64;
        foreach ((string name, byte[] seed) in Seeds())
        {
            for (int iter = 0; iter < 400; iter++)
            {
                byte[] d = (byte[])seed.Clone();
                int edits = 1 + rnd.Next(4);
                for (int e = 0; e < edits; e++)
                {
                    switch (rnd.Next(5))
                    {
                        case 0:
                            d[rnd.Next(d.Length)] ^= (byte)(1 << rnd.Next(8));
                            break;
                        case 1:
                            d[rnd.Next(d.Length)] = (byte)rnd.Next(256);
                            break;
                        case 2:
                            int p = rnd.Next(d.Length - 4);
                            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(p), rnd.Next(2) == 0 ? 0xffffffffu : (uint)rnd.Next());
                            break;
                        case 3:
                            // Header-area edits are the most likely to produce absurd sizes and dimensions.
                            d[rnd.Next(Math.Min(48, d.Length))] = (byte)rnd.Next(256);
                            break;
                        default:
                            Array.Resize(ref d, rnd.Next(1, d.Length + 1));
                            break;
                    }

                    if (d.Length < 8)
                    {
                        break;
                    }
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                _ = TryDecodeAll(d);
                long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                // Limits above cap frames at 4 Mi pixels, so any decode needs well under a few hundred MB.
                Assert.True(alloc < 600L * 1024 * 1024, $"{name} iteration {iter} allocated {alloc} bytes");
            }
        }

        Assert.True(Environment.TickCount64 - start < 120_000, "mutation run took too long");
    }

    private static byte[] Riff(string fourCc, byte[] payload, uint? riffSize = null, uint? chunkSize = null)
    {
        byte[] f = new byte[20 + payload.Length];
        "RIFF"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), riffSize ?? (uint)(f.Length - 8));
        "WEBP"u8.CopyTo(f.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(f, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), chunkSize ?? (uint)payload.Length);
        payload.CopyTo(f.AsSpan(20));
        return f;
    }

    [Fact]
    public void ChunkAndRiffSizeLiesAreRejected()
    {
        byte[] good = Encode(WebPOracle.MakeImage(16, 16, 1), 16, 16, true);
        byte[] payload = good.AsSpan(20, good.Length - 20).ToArray();
        Assert.False(TryDecodeAll(Riff("VP8L", payload, chunkSize: 0x7fffffff)));
        Assert.False(TryDecodeAll(Riff("VP8L", payload, chunkSize: (uint)payload.Length + 100)));
        Assert.False(TryDecodeAll(Riff("VP8L", payload, riffSize: 0xfffffff0)));
        Assert.False(TryDecodeAll(Riff("VP8L", payload, riffSize: 3)));
        Assert.False(TryDecodeAll(Riff("VP8L", [])));
    }

    [Fact]
    public void AbsurdDimensionsAreLimitedBeforeAllocation()
    {
        // VP8L: signature 0x2f, then 14-bit width-1 and height-1 (all ones = 16384 x 16384), alpha bit, version.
        byte[] header = [0x2f, 0xff, 0xff, 0xff, 0x0f, 0, 0, 0, 0, 0];
        byte[] file = Riff("VP8L", header);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ImageLimitException>(() => Image.Load(file, new LoadOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 1 << 20 } }).Frames[0].ToArray<Rgba32>());
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024);

        // VP8: 3-byte frame tag (key frame), start code, 14-bit width/height = 16383.
        byte[] vp8 = [0x10, 0x02, 0x00, 0x9d, 0x01, 0x2a, 0xff, 0x3f, 0xff, 0x3f, 0, 0, 0, 0, 0, 0];
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<Exception>(() => Image.Load(Riff("VP8 ", vp8), new LoadOptions { Limits = new ImageLimits { MaxPixelsPerFrame = 1 << 20 } }).Frames[0].ToArray<Rgba32>());
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024);
    }

    [Fact]
    public void DefaultLimitsStillRejectTruncatedHugeLossless()
    {
        // 16383 x 16383 passes the default pixel limit (the pixel buffer is sized from the header, as in libwebp, because an
        // all-zero-bit code can legitimately expand a few bytes to a solid image), but the data is missing: decoding must fail typed.
        byte[] header = [0x2f, 0xfe, 0xff, 0xff, 0x0f, 0, 0, 0, 0, 0];
        byte[] file = Riff("VP8L", header);
        Assert.ThrowsAny<ImageFormatException>(() => Image.Load(file).Frames[0].ToArray<Rgba32>());
    }

    [Fact]
    public void HugeFrameCountIsLimited()
    {
        var body = new List<byte>("WEBP"u8.ToArray());
        void Chunk(string cc, byte[] data)
        {
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(cc));
            body.AddRange(BitConverter.GetBytes((uint)data.Length));
            body.AddRange(data);
            if ((data.Length & 1) != 0)
            {
                body.Add(0);
            }
        }

        Chunk("VP8X", [0x12, 0, 0, 0, 7, 0, 0, 7, 0, 0]);
        Chunk("ANIM", new byte[6]);
        byte[] frame = new byte[16 + 8 + 2];
        frame[12] = 1; // 2x2 sub-frame... payload content is irrelevant: the count must be rejected first
        for (int i = 0; i < 3000; i++)
        {
            Chunk("ANMF", frame);
        }

        byte[] file = new byte[8 + body.Count];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)body.Count);
        body.CopyTo(file, 8);
        Assert.ThrowsAny<Exception>(() => Image.Load(file)).GetType();
        Exception ex = Assert.ThrowsAny<Exception>(() => Image.Load(file));
        Assert.True(ex is ImageLimitException or ImageFormatException, ex.GetType().Name);
    }
}
