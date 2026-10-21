using System.Buffers.Binary;
using ImageMagick;
using SkiaSharp;
using OneImage.Codecs;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>Animated WebP: decode compositing against libwebp's animation decoder (through ImageMagick) and encode round trips.</summary>
public sealed class WebPAnimationTests
{
    private static readonly WebPFormat Format = new();

    internal static byte[] EncodeStill(byte[] rgba, int w, int h, bool lossless = true)
    {
        Image img = Image.FromPixels<Rgba32>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba32>(rgba), w, h);
        using var ms = new MemoryStream();
        img.Save(ms, Format, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = lossless } });
        return ms.ToArray();
    }

    /// <summary>Returns the VP8L (or VP8 plus ALPH) chunks of a still file, as a ready-to-embed byte run.</summary>
    private static byte[] ImageChunks(byte[] file)
    {
        int pos = 12;
        var o = new List<byte>();
        while (pos + 8 <= file.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
            int total = 8 + (int)size + (int)(size & 1);
            string cc = System.Text.Encoding.ASCII.GetString(file, pos, 4);
            if (cc is "VP8L" or "VP8 " or "ALPH")
            {
                o.AddRange(file.AsSpan(pos, total).ToArray());
            }

            pos += total;
        }

        return o.ToArray();
    }

    private static void U24(List<byte> l, int v)
    {
        l.Add((byte)v);
        l.Add((byte)(v >> 8));
        l.Add((byte)(v >> 16));
    }

    private static void Chunk(List<byte> l, string cc, byte[] data)
    {
        l.AddRange(System.Text.Encoding.ASCII.GetBytes(cc));
        l.AddRange(BitConverter.GetBytes((uint)data.Length));
        l.AddRange(data);
        if ((data.Length & 1) != 0)
        {
            l.Add(0);
        }
    }

    internal sealed record Anmf(int X, int Y, int W, int H, int DelayMs, bool Blend, bool DisposeBackground, byte[] Rgba);

    /// <summary>Builds an animated file by hand so that every blend/dispose combination is exercised exactly.</summary>
    internal static byte[] BuildAnimation(int cw, int ch, int loops, uint background, IReadOnlyList<Anmf> frames)
    {
        var body = new List<byte>();
        body.AddRange("WEBP"u8.ToArray());
        var vp8x = new List<byte> { 0x12, 0, 0, 0 };
        U24(vp8x, cw - 1);
        U24(vp8x, ch - 1);
        Chunk(body, "VP8X", vp8x.ToArray());
        var anim = new List<byte>(BitConverter.GetBytes(background));
        anim.Add((byte)loops);
        anim.Add((byte)(loops >> 8));
        Chunk(body, "ANIM", anim.ToArray());
        foreach (Anmf f in frames)
        {
            var p = new List<byte>();
            U24(p, f.X / 2);
            U24(p, f.Y / 2);
            U24(p, f.W - 1);
            U24(p, f.H - 1);
            U24(p, f.DelayMs);
            p.Add((byte)((f.Blend ? 0 : 2) | (f.DisposeBackground ? 1 : 0)));
            p.AddRange(ImageChunks(EncodeStill(f.Rgba, f.W, f.H)));
            Chunk(body, "ANMF", p.ToArray());
        }

        var file = new List<byte>();
        file.AddRange("RIFF"u8.ToArray());
        file.AddRange(BitConverter.GetBytes((uint)body.Count));
        file.AddRange(body);
        return file.ToArray();
    }

    private static byte[] Blob(int w, int h, int seed, int alphaMode)
    {
        var rnd = new Random(seed);
        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[(i * 4) + 0] = (byte)rnd.Next(256);
            px[(i * 4) + 1] = (byte)rnd.Next(256);
            px[(i * 4) + 2] = (byte)rnd.Next(256);
            px[(i * 4) + 3] = alphaMode switch { 0 => 255, 1 => (byte)rnd.Next(256), _ => (byte)(rnd.Next(3) == 0 ? 0 : 255) };
        }

        return px;
    }

    // libwebp through Skia: SkWebpCodec composites each frame onto the prior one with libwebp's own decoder.
    private static List<byte[]> SkiaFrames(byte[] file, out List<int> delaysMs, out int loops)
    {
        using SKCodec codec = SKCodec.Create(new MemoryStream(file));
        SKCodecFrameInfo[] infos = codec.FrameInfo;
        int rep = codec.RepetitionCount;
        loops = rep < 0 ? 0 : rep + 1;
        delaysMs = infos.Select(i => i.Duration).ToList();
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var frames = new List<byte[]>();
        for (int i = 0; i < infos.Length; i++)
        {
            using var bmp = new SKBitmap(info);
            int prior = infos[i].RequiredFrame;
            if (prior >= 0)
            {
                System.Runtime.InteropServices.Marshal.Copy(frames[prior], 0, bmp.GetPixels(), frames[prior].Length);
            }

            SKCodecResult r = codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(i, prior));
            Assert.Equal(SKCodecResult.Success, r);
            byte[] data = new byte[info.Width * info.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), data, 0, data.Length);
            frames.Add(data);
        }

        return frames;
    }

    private static void AssertSameAsLibwebp(byte[] file, int expectedFrames, int expectedLoops, int tolerance = 1)
    {
        Image ours = Image.Load(file);
        List<byte[]> theirs = SkiaFrames(file, out List<int> delays, out int loops);
        Assert.Equal(expectedFrames, ours.FrameCount);
        Assert.Equal(expectedFrames, theirs.Count);
        Assert.Equal(expectedLoops, ours.LoopCount);
        Assert.Equal(expectedLoops, loops);
        int count = theirs.Count;
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(delays[i], (int)ours.Frames[i].Delay.TotalMilliseconds);
            Rgba32[] px = ours.Frames[i].ToArray<Rgba32>();
            byte[] bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
            // Fully transparent pixels have no defined color; compare colors only where alpha is non-zero.
            int max = 0;
            for (int p = 0; p < bytes.Length; p += 4)
            {
                max = Math.Max(max, Math.Abs(bytes[p + 3] - theirs[i][p + 3]));
                if (bytes[p + 3] != 0)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        max = Math.Max(max, Math.Abs(bytes[p + c] - theirs[i][p + c]));
                    }
                }
            }

            if (max > tolerance)
            {
                for (int p = 0; p < bytes.Length; p += 4)
                {
                    if (Math.Abs(bytes[p + 3] - theirs[i][p + 3]) > tolerance || (bytes[p + 3] != 0 && Math.Abs(bytes[p] - theirs[i][p]) > tolerance))
                    {
                        Assert.Fail($"frame {i}: max diff {max}; first at pixel {p / 4}: ours {bytes[p]},{bytes[p + 1]},{bytes[p + 2]},{bytes[p + 3]} theirs {theirs[i][p]},{theirs[i][p + 1]},{theirs[i][p + 2]},{theirs[i][p + 3]}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void DecodeBlendAndDisposeMatchesLibwebp(bool blend, bool dispose) => BlendAndDispose(blend, dispose, blend ? 2 : 1, 1);

    // Partial alpha blended over partial alpha: Skia (the oracle) blends in a different integer form than libwebp's own
    // animation decoder, so only a loose bound is meaningful here.
    [Fact]
    public void DecodePartialAlphaBlendIsClose() => BlendAndDispose(true, false, 1, 8);

    private static void BlendAndDispose(bool blend, bool dispose, int alphaMode, int tolerance)
    {
        const int cw = 40, ch = 30;
        var frames = new List<Anmf>
        {
            new(0, 0, cw, ch, 100, blend, dispose, Blob(cw, ch, 1, alphaMode)),
            new(10, 6, 20, 16, 50, blend, dispose, Blob(20, 16, 2, alphaMode)),
            new(4, 2, 12, 20, 70, blend, !dispose, Blob(12, 20, 3, 2)),
            new(30, 20, 10, 10, 30, blend, dispose, Blob(10, 10, 4, 0)),
            new(0, 10, 40, 8, 120, blend, dispose, Blob(40, 8, 5, alphaMode)),
        };
        byte[] file = BuildAnimation(cw, ch, 3, 0xff808080u, frames);
        AssertSameAsLibwebp(file, 5, 3, tolerance);
    }

    [Fact]
    public void DecodeMagickWrittenAnimations()
    {
        foreach (bool lossless in new[] { true, false })
        {
            using var col = new MagickImageCollection();
            for (int i = 0; i < 6; i++)
            {
                var img = new MagickImage(MagickColors.Transparent, 64, 48);
                img.Draw(new ImageMagick.Drawing.DrawableFillColor(new MagickColor((byte)(i * 40), 150, 230, 255)), new ImageMagick.Drawing.DrawableCircle(10 + (i * 8), 20, 22 + (i * 8), 20));
                img.AnimationDelay = (uint)(5 + (i * 3));
                img.GifDisposeMethod = i % 2 == 0 ? GifDisposeMethod.Background : GifDisposeMethod.None;
                img.Settings.SetDefine(MagickFormat.WebP, "lossless", lossless ? "true" : "false");
                col.Add(img);
            }

            col[0].AnimationIterations = 4;
            using var ms = new MemoryStream();
            col.Write(ms, MagickFormat.WebP);
            byte[] file = ms.ToArray();
            Image ours = Image.Load(file);
            Assert.Equal(6, ours.FrameCount);
            Assert.Equal(4, ours.LoopCount);
            if (lossless)
            {
                AssertSameAsLibwebp(file, 6, 4);
            }
        }
    }

    [Fact]
    public void EncodedAnimationDecodesInLibwebpAndOurs()
    {
        const int w = 48, h = 36;
        var nodes = new List<Image>();
        var rgbaFrames = new List<byte[]>();
        for (int i = 0; i < 5; i++)
        {
            byte[] f = WebPOracle.MakeImage(w, h, 100 + i, alpha: i % 2 == 1, noisy: false);
            rgbaFrames.Add(f);
            nodes.Add(Image.FromPixels<Rgba32>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba32>(f), w, h));
        }

        Image anim = AnimationOf(nodes, [20, 40, 60, 80, 100], 7);
        foreach (bool lossless in new[] { true, false })
        {
            using var ms = new MemoryStream();
            anim.Save(ms, Format, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = lossless, Quality = 90 } });
            byte[] file = ms.ToArray();
            AssertSameAsLibwebp(file, 5, 7);
            if (lossless)
            {
                Image back = Image.Load(file);
                for (int i = 0; i < 5; i++)
                {
                    byte[] got = System.Runtime.InteropServices.MemoryMarshal.AsBytes(back.Frames[i].ToArray<Rgba32>().AsSpan()).ToArray();
                    Assert.Equal(rgbaFrames[i].Length, got.Length);
                    for (int p = 0; p < got.Length; p += 4)
                    {
                        Assert.Equal(rgbaFrames[i][p + 3], got[p + 3]);
                        if (got[p + 3] != 0)
                        {
                            Assert.True(rgbaFrames[i].AsSpan(p, 3).SequenceEqual(got.AsSpan(p, 3)));
                        }
                    }
                }
            }
        }
    }

    internal static Image AnimationOf(List<Image> frames, int[] delaysMs, int loops, ImageMetadata? metadata = null) =>
        new(frames.Select(f => f.Frames[0].Node).ToArray(), delaysMs.Select(d => TimeSpan.FromMilliseconds(d)).ToArray(), loops, metadata ?? ImageMetadata.Empty, null);

    [Fact]
    public void AnimationLimitsAreEnforced()
    {
        byte[] file = BuildAnimation(8, 8, 0, 0, Enumerable.Range(0, 5).Select(i => new Anmf(0, 0, 8, 8, 10, true, false, Blob(8, 8, i, 0))).ToList());
        Assert.Throws<ImageLimitException>(() => Image.Load(file, new LoadOptions { Limits = new ImageLimits { MaxFrames = 3 } }).Frames[0].ToArray<Rgba32>());
        Assert.Throws<ImageLimitException>(() => Image.Load(file, new LoadOptions { Limits = new ImageLimits { MaxTotalPixels = 8 * 8 * 4 } }).Frames[0].ToArray<Rgba32>());
    }
}
