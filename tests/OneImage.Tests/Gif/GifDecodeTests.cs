using ImageMagick;
using OneImage.Tests.Gif;

namespace OneImage.Tests.Gif;

public class GifDecodeTests
{
    private static readonly ImageConfiguration Config = new ImageConfiguration().Add(GifFormat.Instance);

    private static MagickCorpus.Part P(int x, int y, int w, int h, int seed, int colors, bool holes, GifDisposeMethod d, uint delay) =>
        new(x, y, w, h, MagickCorpus.Pattern(w, h, seed, colors, holes), d, delay);

    public static IEnumerable<object[]> Scenarios()
    {
        yield return ["static", 40, 30, new[] { P(0, 0, 40, 30, 1, 50, false, GifDisposeMethod.None, 0) }, null!, false];
        yield return ["interlaced", 40, 33, new[] { P(0, 0, 40, 33, 2, 200, false, GifDisposeMethod.None, 0) }, null!, true];
        yield return ["transparent", 40, 30, new[] { P(0, 0, 40, 30, 3, 30, true, GifDisposeMethod.None, 0) }, null!, false];
        yield return ["none", 50, 40, new[]
        {
            P(0, 0, 50, 40, 4, 20, false, GifDisposeMethod.None, 5),
            P(10, 5, 20, 20, 5, 15, true, GifDisposeMethod.None, 7),
            P(25, 15, 24, 24, 6, 10, true, GifDisposeMethod.None, 9),
        }, (uint?)3u, false];
        yield return ["background", 50, 40, new[]
        {
            P(0, 0, 50, 40, 7, 20, true, GifDisposeMethod.Background, 4),
            P(5, 5, 30, 20, 8, 12, true, GifDisposeMethod.Background, 6),
            P(20, 10, 30, 30, 9, 12, true, GifDisposeMethod.None, 8),
        }, (uint?)0u, false];
        yield return ["previous", 50, 40, new[]
        {
            P(0, 0, 50, 40, 10, 20, true, GifDisposeMethod.None, 3),
            P(8, 8, 30, 20, 11, 12, true, GifDisposeMethod.Previous, 3),
            P(15, 15, 30, 20, 12, 12, true, GifDisposeMethod.Previous, 3),
            P(0, 0, 10, 10, 13, 5, false, GifDisposeMethod.None, 3),
        }, (uint?)2u, false];
        yield return ["perframe-palettes", 32, 32, new[]
        {
            P(0, 0, 32, 32, 14, 200, false, GifDisposeMethod.None, 10),
            P(0, 0, 32, 32, 15, 220, false, GifDisposeMethod.None, 10),
            P(3, 3, 17, 9, 16, 4, false, GifDisposeMethod.Background, 10),
        }, (uint?)1u, false];
        yield return ["odd-offsets", 61, 47, new[]
        {
            P(0, 0, 61, 47, 17, 9, false, GifDisposeMethod.None, 2),
            P(60, 46, 1, 1, 18, 2, false, GifDisposeMethod.None, 2),
            P(1, 0, 59, 1, 19, 6, false, GifDisposeMethod.Previous, 2),
            P(0, 46, 1, 1, 20, 2, false, GifDisposeMethod.None, 2),
        }, (uint?)5u, true];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void MatchesMagickCoalescedFrames(string name, int cw, int ch, MagickCorpus.Part[] parts, uint? iterations, bool interlace)
    {
        byte[] gif = MagickCorpus.Write(cw, ch, parts, iterations, interlace);
        var (refFrames, refDelays, refIter, rw, rh) = MagickCorpus.Reference(gif);
        Image img = Image.Load(gif, new LoadOptions { Configuration = Config, Validate = true });
        Assert.Equal(refFrames.Count, img.FrameCount);
        Assert.Equal(rw, img.Width);
        Assert.Equal(rh, img.Height);
        if (iterations != null)
        {
            Assert.Equal((int)refIter, img.LoopCount);
        }

        for (int i = 0; i < refFrames.Count; i++)
        {
            Rgba32[] px = img.Frames[i].ToArray<Rgba32>();
            byte[] ours = new byte[px.Length * 4];
            for (int k = 0; k < px.Length; k++)
            {
                ours[k * 4] = px[k].R;
                ours[(k * 4) + 1] = px[k].G;
                ours[(k * 4) + 2] = px[k].B;
                ours[(k * 4) + 3] = px[k].A;
            }

            Assert.True(MagickCorpus.Normalize(refFrames[i]).AsSpan().SequenceEqual(MagickCorpus.Normalize(ours)), $"{name}: frame {i} differs from Magick.NET");
            Assert.Equal(refDelays[i] * 10.0, img.Frames[i].Delay.TotalMilliseconds);
        }
    }

    [Fact]
    public void MissingEndCodeAndSparseFramesMatchMagick()
    {
        byte[] pal = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
        byte[] ramp = Enumerable.Range(0, 12 * 9).Select(i => (byte)(i % 4)).ToArray();
        byte[] gif = GifBuilder.Build(12, 9, pal,
        [
            new GifBuilder.Frame(0, 0, 12, 9, ramp, EndCode: false, Delay: 4),
            new GifBuilder.Frame(3, 2, 5, 4, Enumerable.Repeat((byte)3, 20).ToArray(), Transparent: 3, Disposal: 1, Delay: 6),
            new GifBuilder.Frame(6, 5, 20, 20, Enumerable.Repeat((byte)1, 400).ToArray(), Disposal: 2, Delay: 8, Interlaced: true),
        ], loop: 0);
        var (refFrames, refDelays, _, _, _) = MagickCorpus.Reference(gif);
        Image img = Image.Load(gif, new LoadOptions { Configuration = Config, Validate = true });
        Assert.Equal(refFrames.Count, img.FrameCount);
        for (int i = 0; i < refFrames.Count; i++)
        {
            Rgba32[] px = img.Frames[i].ToArray<Rgba32>();
            byte[] ours = new byte[px.Length * 4];
            for (int k = 0; k < px.Length; k++)
            {
                (ours[k * 4], ours[(k * 4) + 1], ours[(k * 4) + 2], ours[(k * 4) + 3]) = (px[k].R, px[k].G, px[k].B, px[k].A);
            }

            Assert.True(MagickCorpus.Normalize(refFrames[i]).AsSpan().SequenceEqual(MagickCorpus.Normalize(ours)), $"frame {i}");
            Assert.Equal(refDelays[i] * 10.0, img.Frames[i].Delay.TotalMilliseconds);
        }
    }
}
