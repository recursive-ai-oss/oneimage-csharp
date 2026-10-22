namespace OneImage.Tests.Png;

public class ApngTests
{
    private static byte[] Reference(int w, int h, MiniPng.Frame[] frames, int upTo)
    {
        byte[] canvas = new byte[w * h * 4];
        byte[]? snapshot = null;
        for (int i = 0; i <= upTo; i++)
        {
            if (i > 0)
            {
                MiniPng.Frame p = frames[i - 1];
                for (int y = p.Y; y < p.Y + p.H; y++)
                {
                    for (int x = p.X; x < p.X + p.W; x++)
                    {
                        int o = ((y * w) + x) * 4;
                        if (p.Dispose == 1 || (p.Dispose == 2 && snapshot == null))
                        {
                            canvas.AsSpan(o, 4).Clear();
                        }
                        else if (p.Dispose == 2)
                        {
                            snapshot.AsSpan(o, 4).CopyTo(canvas.AsSpan(o, 4));
                        }
                    }
                }
            }

            snapshot = (byte[])canvas.Clone();
            MiniPng.Frame f = frames[i];
            for (int y = 0; y < f.H; y++)
            {
                for (int x = 0; x < f.W; x++)
                {
                    int s = ((y * f.W) + x) * 4;
                    int o = (((f.Y + y) * w) + f.X + x) * 4;
                    if (f.Blend == 0)
                    {
                        f.Rgba.AsSpan(s, 4).CopyTo(canvas.AsSpan(o, 4));
                        continue;
                    }

                    double sa = f.Rgba[s + 3] / 255.0;
                    double da = canvas[o + 3] / 255.0;
                    double oa = sa + (da * (1 - sa));
                    if (oa == 0)
                    {
                        canvas.AsSpan(o, 4).Clear();
                        continue;
                    }

                    for (int c = 0; c < 3; c++)
                    {
                        canvas[o + c] = (byte)Math.Floor((((f.Rgba[s + c] * sa) + (canvas[o + c] * da * (1 - sa))) / oa) + 0.5);
                    }

                    canvas[o + 3] = (byte)Math.Floor((oa * 255) + 0.5);
                }
            }
        }

        return canvas;
    }

    private static void AssertClose(byte[] expected, byte[] actual, int tolerance = 1)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"byte {i}: expected {expected[i]} got {actual[i]}");
        }
    }

    [Fact]
    public void FramesDelaysLoopAndCompositingMatchTheSpec()
    {
        const int W = 12, H = 9;
        var frames = new MiniPng.Frame[]
        {
            new(0, 0, W, H, MiniPng.Rgba(W, H, 200, 10, 10, 255), 1, 10, Dispose: 0),
            new(2, 2, 5, 4, MiniPng.Rgba(5, 4, 0, 200, 0, 128), 3, 100, Dispose: 2, Blend: 1),   // over, then restore previous
            new(1, 1, 4, 4, MiniPng.Rgba(4, 4, 0, 0, 255, 255), 1, 0, Dispose: 1, Blend: 0),   // source, then clear to background
            new(6, 3, 5, 5, MiniPng.Rgba(5, 5, 255, 255, 0, 64), 2, 1000, Dispose: 0, Blend: 1),
            new(0, 0, W, H, MiniPng.Rgba(W, H, 9, 9, 9, 0), 1, 1, Dispose: 0, Blend: 1),
        };
        byte[] file = MiniPng.Apng(W, H, frames, plays: 3);
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default);
        Assert.Equal(5, info.Frames.Count);
        Assert.Equal(3, info.LoopCount);
        Assert.Equal(TimeSpan.FromSeconds(0.1), info.Frames[0].Delay);
        Assert.Equal(TimeSpan.FromSeconds(0.03), info.Frames[1].Delay);
        Assert.Equal(TimeSpan.FromSeconds(0.01), info.Frames[2].Delay); // denominator 0 means 100
        Assert.Equal(TimeSpan.FromSeconds(0.002), info.Frames[3].Delay);
        Assert.All(info.Frames, f => Assert.Equal((W, H, PixelFormat.Rgba8), (f.Width, f.Height, f.Format)));

        // Out of order and repeated requests exercise the cache and the restart path.
        foreach (int i in new[] { 0, 1, 2, 3, 4, 2, 0, 4, 4, 1 })
        {
            Decoded d = PngTestUtil.Decode(file, i);
            AssertClose(Reference(W, H, frames, i), d.Data);
        }
    }

    [Fact]
    public void HiddenDefaultImageIsNotAFrame()
    {
        const int W = 6, H = 6;
        var frames = new MiniPng.Frame[]
        {
            new(0, 0, W, H, MiniPng.Rgba(W, H, 1, 2, 3, 255)),
            new(1, 1, 2, 2, MiniPng.Rgba(2, 2, 50, 60, 70, 255)),
        };
        byte[] file = MiniPng.Apng(W, H, frames, hiddenDefault: MiniPng.Rgba(W, H, 255, 0, 255, 255));
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default);
        Assert.Equal(2, info.Frames.Count);
        AssertClose(Reference(W, H, frames, 1), PngTestUtil.Decode(file, 1).Data);
    }

    [Fact]
    public void RegionDecodeOfAnAnimationFrame()
    {
        const int W = 20, H = 10;
        var frames = new MiniPng.Frame[]
        {
            new(0, 0, W, H, MiniPng.Rgba(W, H, 1, 2, 3, 255)),
            new(5, 2, 10, 5, MiniPng.Rgba(10, 5, 50, 60, 70, 255)),
        };
        byte[] file = MiniPng.Apng(W, H, frames);
        using var ms = new MemoryStream(file);
        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(ms, ImageLimits.Default);
        var r = new ImageRect(4, 1, 8, 4);
        var sink = new CollectSink(8, 4, PixelFormat.Rgba8);
        PngFormat.Instance.Decoder.DecodeFrame(ms, info, 1, new DecodeRequest(r), sink, ImageLimits.Default, CancellationToken.None);
        byte[] full = Reference(W, H, frames, 1);
        for (int y = 0; y < 4; y++)
        {
            Assert.Equal(full.AsSpan((((r.Y + y) * W) + r.X) * 4, 32).ToArray(), sink.Data.AsSpan(y * 32, 32).ToArray());
        }
    }

    [Fact]
    public void EncodedAnimationRoundTripsAndLibpngSeesTheDefaultImage()
    {
        const int W = 40, H = 30;
        var rnd = new Random(8);
        var images = new List<Image>();
        byte[] px = PngGen.Photo(W, H, PixelFormat.Rgba8, 1);
        for (int i = 0; i < 5; i++)
        {
            // Each frame changes a moving rectangle.
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 11; x++)
                {
                    int o = (((i * 4 + y) * W) + (i * 6) + x) * 4;
                    px[o] = (byte)rnd.Next(256);
                    px[o + 3] = (byte)rnd.Next(256);
                }
            }

            images.Add(PngTestUtil.FromRaw((byte[])px.Clone(), W, H, PixelFormat.Rgba8));
        }

        int[] delays = [100, 40, 250, 7, 70000];
        Image anim = PngGen.Animation([.. images], delays, 4);
        using var ms = new MemoryStream();
        anim.Save(ms, PngFormat.Instance);
        byte[] file = ms.ToArray();
        Assert.Equal(file, EncodeAgain(anim));

        ImageInfo info = PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default);
        Assert.Equal(5, info.Frames.Count);
        Assert.Equal(4, info.LoopCount);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(7), TimeSpan.FromSeconds(70)], info.Frames.Select(f => f.Delay).ToArray());
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(images[i].ToArray<Rgba32>().SelectMany(p => new[] { p.R, p.G, p.B, p.A }).ToArray(), PngTestUtil.Decode(file, i).Data);
        }

        // libpng ignores the animation and shows the default image, which here is frame 0.
        Assert.Equal(Canon.ToRgba16(PixelFormat.Rgba8, PngTestUtil.Decode(file, 0).Data, W * H), PngTestUtil.NetVipsDecode(file).Rgba);
    }

    [Fact]
    public void ParallelFrameRequestsAgreeWithSequentialDecoding()
    {
        const int W = 30, H = 20;
        var rnd = new Random(5);
        var frames = Enumerable.Range(0, 12).Select(i =>
            new MiniPng.Frame(rnd.Next(10), rnd.Next(8), 10 + rnd.Next(10), 6 + rnd.Next(8), MiniPng.Rgba(20, 14, (byte)(i * 20), (byte)(i * 9), 7, (byte)(i % 3 == 0 ? 255 : 90)), 1, 10, Dispose: i % 3, Blend: i % 2)).ToArray();
        // Keep each rectangle inside the canvas.
        frames = frames.Select(f => f with { W = Math.Min(f.W, W - f.X), H = Math.Min(f.H, H - f.Y) }).Select(f => f with { Rgba = MiniPng.Rgba(f.W, f.H, f.Rgba[0], f.Rgba[1], f.Rgba[2], f.Rgba[3]) }).ToArray();
        byte[] file = MiniPng.Apng(W, H, frames);
        using var ms0 = new MemoryStream(file);
        ImageInfo shared = PngFormat.Instance.Decoder!.ReadInfo(ms0, ImageLimits.Default);
        byte[][] expected = Enumerable.Range(0, frames.Length).Select(i => Reference(W, H, frames, i)).ToArray();
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
        {
            int i = (n * 7) % frames.Length;
            using var ms = new MemoryStream(file);
            var sink = new CollectSink(W, H, PixelFormat.Rgba8);
            PngFormat.Instance.Decoder!.DecodeFrame(ms, shared, i, new DecodeRequest(new ImageRect(0, 0, W, H)), sink, ImageLimits.Default, CancellationToken.None);
            AssertClose(expected[i], sink.Data);
        });
    }

    private static byte[] EncodeAgain(Image anim)
    {
        using var ms = new MemoryStream();
        anim.Save(ms, PngFormat.Instance);
        return ms.ToArray();
    }

    [Fact]
    public void FrameCountLimitIsEnforcedFromHeaders()
    {
        const int W = 4, H = 4;
        var frames = Enumerable.Range(0, 6).Select(_ => new MiniPng.Frame(0, 0, W, H, MiniPng.Rgba(W, H, 1, 1, 1, 255))).ToArray();
        byte[] file = MiniPng.Apng(W, H, frames);
        Assert.Throws<ImageLimitException>(() => PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), new ImageLimits { MaxFrames = 5 }));
    }

    [Fact]
    public void FrameOutsideTheCanvasIsRejected()
    {
        var frames = new MiniPng.Frame[] { new(5, 5, 4, 4, MiniPng.Rgba(4, 4, 1, 1, 1, 255)) };
        byte[] file = MiniPng.Apng(8, 8, frames);
        Assert.Throws<ImageFormatException>(() => PngFormat.Instance.Decoder!.ReadInfo(new MemoryStream(file), ImageLimits.Default));
    }
}
