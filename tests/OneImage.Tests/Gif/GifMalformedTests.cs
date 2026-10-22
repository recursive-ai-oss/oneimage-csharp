using System.Diagnostics;
using ImageMagick;
using OneImage.Tests.Gif;

namespace OneImage.Tests.Gif;

public class GifMalformedTests
{
    private static readonly byte[] Pal = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];

    private static byte[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 4)).ToArray();

    private static Image Load(byte[] gif, ImageLimits? limits = null) =>
        Image.Load(gif, new LoadOptions { Configuration = new ImageConfiguration().Add(GifFormat.Instance), Validate = true, Limits = limits });

    [Fact]
    public void MissingEndCodeStillDecodesEverything()
    {
        byte[] idx = Ramp(10 * 6);
        byte[] gif = GifBuilder.Build(10, 6, Pal, [new GifBuilder.Frame(0, 0, 10, 6, idx, EndCode: false)]);
        Rgba32[] px = GifTestUtil.Pixels(Load(gif));
        for (int i = 0; i < idx.Length; i++)
        {
            Assert.Equal(new Rgba32(Pal[idx[i] * 3], Pal[(idx[i] * 3) + 1], Pal[(idx[i] * 3) + 2], 255), px[i]);
        }
    }

    [Fact]
    public void DataThatEndsEarlyLeavesTheRestTransparent()
    {
        // 20 pixels of data for a 10x6 frame: two rows are painted, the rest stays as it was (transparent).
        byte[] gif = GifBuilder.Build(10, 6, Pal, [new GifBuilder.Frame(0, 0, 10, 6, Ramp(20), EndCode: false)]);
        Rgba32[] px = GifTestUtil.Pixels(Load(gif));
        Assert.True(px.Take(20).All(p => p.A == 255));
        Assert.True(px.Skip(20).All(p => p.A == 0));
    }

    [Fact]
    public void ReaderKeepsFramesBeforeAFileThatIsCutOff()
    {
        byte[] full = GifBuilder.Build(8, 8, Pal, [new GifBuilder.Frame(0, 0, 8, 8, Ramp(64)), new GifBuilder.Frame(0, 0, 8, 8, Ramp(64))], loop: 0);
        byte[] cut = full.AsSpan(0, full.Length - 12).ToArray();
        Image img = Load(cut);
        Assert.Equal(2, img.FrameCount);
        _ = GifTestUtil.Pixels(img, 1);
        Assert.Throws<ImageFormatException>(() => Load(full.AsSpan(0, 8).ToArray()));
        Assert.Throws<ImageFormatException>(() => Load(full.AsSpan(0, 20).ToArray()));
    }

    [Fact]
    public void InvalidLzwCodeIsAFormatError()
    {
        // Clear, then a code beyond the table.
        byte[] gif = GifBuilder.Build(4, 4, Pal, [new GifBuilder.Frame(0, 0, 4, 4, Ramp(16))]);
        int at = Array.IndexOf(gif, (byte)0x2C) + 10 + 1 + 1; // first data byte after min code size and length
        gif[at] = 0x3C; // clear code, then code 7 which no entry has yet
        Assert.Throws<ImageFormatException>(() => Load(gif));
    }

    [Fact]
    public void HugeLogicalScreenIsRefusedBeforeAllocation()
    {
        byte[] gif = GifBuilder.Build(65535, 65535, Pal, [new GifBuilder.Frame(0, 0, 2, 2, Ramp(4))]);
        long before = GC.GetTotalAllocatedBytes(true);
        Assert.Throws<ImageLimitException>(() => Load(gif));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 4_000_000);
    }

    [Fact]
    public void FrameCountLimitIsEnforced()
    {
        var frames = Enumerable.Range(0, 1001).Select(_ => new GifBuilder.Frame(0, 0, 1, 1, [1])).ToList();
        byte[] gif = GifBuilder.Build(2, 2, Pal, frames);
        Assert.Throws<ImageLimitException>(() => Load(gif));
        Assert.Equal(1001, Load(gif, new ImageLimits { MaxFrames = 2000 }).FrameCount);
    }

    [Fact]
    public void TotalPixelLimitCountsEveryFrame()
    {
        var frames = Enumerable.Range(0, 10).Select(_ => new GifBuilder.Frame(0, 0, 100, 100, new byte[10000])).ToList();
        byte[] gif = GifBuilder.Build(100, 100, Pal, frames);
        Assert.Throws<ImageLimitException>(() => Load(gif, new ImageLimits { MaxTotalPixels = 50_000 }));
    }

    [Fact]
    public void FrameRectangleOutsideTheScreenIsClipped()
    {
        byte[] idx = Enumerable.Repeat((byte)2, 20 * 20).ToArray();
        byte[] gif = GifBuilder.Build(10, 10, Pal, [new GifBuilder.Frame(5, 6, 20, 20, idx), new GifBuilder.Frame(40, 40, 3, 3, [1, 1, 1, 1, 1, 1, 1, 1, 1])]);
        Image img = Load(gif);
        Rgba32[] f0 = GifTestUtil.Pixels(img, 0);
        Assert.Equal(new Rgba32(0, 0, 255, 255), f0[(9 * 10) + 9]);
        Assert.Equal(0, f0[(5 * 10) + 9].A);
        Assert.Equal(0, f0[(6 * 10) + 4].A);
        Assert.Equal(f0, GifTestUtil.Pixels(img, 1)); // a frame wholly outside draws nothing
    }

    [Fact]
    public void HugeFrameRectangleWithLittleDataIsHarmless()
    {
        byte[] gif = GifBuilder.Build(16, 16, Pal, [new GifBuilder.Frame(0, 0, 65535, 65535, Ramp(300), EndCode: false)]);
        var sw = Stopwatch.StartNew();
        Image img = Load(gif);
        Assert.Equal(16, img.Width);
        Assert.True(sw.ElapsedMilliseconds < 3000);
    }

    [Fact]
    public void ZeroSizedScreenUsesTheFrameBounds()
    {
        byte[] gif = GifBuilder.Build(0, 0, Pal, [new GifBuilder.Frame(3, 2, 5, 4, Ramp(20))]);
        Image img = Load(gif);
        Assert.Equal(8, img.Width);
        Assert.Equal(6, img.Height);
    }

    [Fact]
    public void LoopCountAndInterlacingAndVersion87()
    {
        byte[] idx = Enumerable.Range(0, 7 * 19).Select(i => (byte)((i / 7) % 4)).ToArray();
        Assert.Equal(1, Load(GifBuilder.Build(7, 19, Pal, [new GifBuilder.Frame(0, 0, 7, 19, idx)])).LoopCount);
        Assert.Equal(0, Load(GifBuilder.Build(7, 19, Pal, [new GifBuilder.Frame(0, 0, 7, 19, idx)], loop: 0)).LoopCount);
        Assert.Equal(4, Load(GifBuilder.Build(7, 19, Pal, [new GifBuilder.Frame(0, 0, 7, 19, idx)], loop: 3)).LoopCount);
        byte[] flat = GifBuilder.Build(7, 19, Pal, [new GifBuilder.Frame(0, 0, 7, 19, idx)]);
        byte[] inter = GifBuilder.Build(7, 19, Pal, [new GifBuilder.Frame(0, 0, 7, 19, idx, Interlaced: true)]);
        Assert.Equal(GifTestUtil.Pixels(Load(flat)), GifTestUtil.Pixels(Load(inter)));
        flat[4] = (byte)'7';
        Assert.Equal(7, Load(flat).Width);
    }

    [Fact]
    public void TransparentIndexAndDisposalFollowTheSpec()
    {
        byte[] a = Enumerable.Repeat((byte)0, 16).ToArray();
        byte[] b = [3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3];
        byte[] c = [1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1];
        byte[] gif = GifBuilder.Build(4, 4, Pal, [
            new GifBuilder.Frame(0, 0, 4, 4, a, Delay: 10),
            new GifBuilder.Frame(0, 0, 4, 4, c, Transparent: 3, Disposal: 3, Delay: 20),
            new GifBuilder.Frame(0, 0, 4, 4, b, Transparent: 3, Delay: 30)]);
        Image img = Load(gif);
        Rgba32 red = new(255, 0, 0, 255), green = new(0, 255, 0, 255);
        Rgba32[] f1 = GifTestUtil.Pixels(img, 1);
        Assert.Equal(green, f1[0]);
        Assert.Equal(red, f1[1]);
        Assert.Equal(green, f1[15]);
        Assert.Equal(GifTestUtil.Pixels(img, 0), GifTestUtil.Pixels(img, 2)); // previous restored, nothing drawn
        Assert.Equal(300, img.Frames[2].Delay.TotalMilliseconds);
    }

    [Fact]
    public void XmpIsExposedAndDoesNotDisturbDecoding()
    {
        byte[] xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
        byte[] gif = GifBuilder.Build(4, 4, Pal, [new GifBuilder.Frame(0, 0, 4, 4, Ramp(16))], xmp: xmp);
        Image img = Load(gif);
        Assert.Equal(xmp, img.Metadata.Xmp.ToArray());
        using var m = new MagickImage(gif);
        Assert.Equal(4u, m.Width);
    }

    [Fact]
    public void RandomOrderFrameAccessMatchesSequential()
    {
        var parts = new List<MagickCorpus.Part>();
        for (int i = 0; i < 6; i++)
        {
            parts.Add(new MagickCorpus.Part(i * 3, i * 2, 20, 16, MagickCorpus.Pattern(20, 16, i, 12, true), i % 2 == 0 ? GifDisposeMethod.Background : GifDisposeMethod.Previous, 5));
        }

        Image img = Load(MagickCorpus.Write(40, 30, parts, 0));
        Rgba32[][] sequential = Enumerable.Range(0, 6).Select(i => GifTestUtil.Pixels(img, i)).ToArray();
        Image again = Load(MagickCorpus.Write(40, 30, parts, 0));
        foreach (int i in new[] { 4, 1, 5, 0, 3, 2, 2 })
        {
            Assert.Equal(sequential[i], GifTestUtil.Pixels(again, i));
        }
    }

    [Fact]
    public void FixedSeedMutationsOnlyRaiseTypedErrors()
    {
        var parts = new List<MagickCorpus.Part>
        {
            new(0, 0, 30, 24, MagickCorpus.Pattern(30, 24, 1, 40, false), GifDisposeMethod.None, 4),
            new(5, 4, 20, 16, MagickCorpus.Pattern(20, 16, 2, 30, true), GifDisposeMethod.Background, 4),
            new(2, 2, 25, 20, MagickCorpus.Pattern(25, 20, 3, 20, true), GifDisposeMethod.Previous, 4),
        };
        byte[] seed1 = MagickCorpus.Write(30, 24, parts, 2);
        byte[] seed2 = GifBuilder.Build(9, 9, Pal, [new GifBuilder.Frame(0, 0, 9, 9, Ramp(81), Interlaced: true), new GifBuilder.Frame(2, 2, 5, 5, Ramp(25), Transparent: 1, Disposal: 2)], loop: 0);
        var rng = new Random(12345);
        var limits = new ImageLimits { MaxPixelsPerFrame = 1 << 22, MaxTotalPixels = 1 << 24, MaxFrames = 200 };
        var sw = Stopwatch.StartNew();
        int ok = 0, typed = 0;
        for (int iter = 0; iter < 3000; iter++)
        {
            byte[] data = (byte[])(iter % 2 == 0 ? seed1 : seed2).Clone();
            int edits = 1 + rng.Next(4);
            for (int e = 0; e < edits; e++)
            {
                data[rng.Next(data.Length)] = (byte)rng.Next(256);
            }

            if (iter % 7 == 0)
            {
                data = data.AsSpan(0, rng.Next(data.Length)).ToArray();
            }

            try
            {
                Image img = Load(data, limits);
                for (int f = 0; f < img.FrameCount; f++)
                {
                    _ = GifTestUtil.Pixels(img, f);
                }

                ok++;
            }
            catch (Exception ex) when (ex is ImageFormatException or ImageLimitException)
            {
                typed++;
            }
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"{sw.Elapsed}");
        Assert.True(ok > 100 && typed > 100, $"ok {ok} typed {typed}");
    }

    [Fact]
    public void LargeSolidColorFrameIsNotMistakenForABomb()
    {
        // 16 million pixels compress to a few KB with LZW (a ratio well above 1100).
        const int N = 4000;
        byte[] gif = GifBuilder.Build(N, N, Pal, [new GifBuilder.Frame(0, 0, N, N, new byte[N * N])]);
        Assert.True(gif.Length * 1100L < (long)N * N, $"file {gif.Length} bytes does not exceed the old ratio");
        Image img = Load(gif);
        Rgba32[] corner = new Rgba32[4];
        img.CopyPixelsTo<Rgba32>(corner, new ImageRect(N - 2, N - 2, 2, 2));
        Assert.All(corner, p => Assert.Equal(new Rgba32(255, 0, 0, 255), p));
    }
}
