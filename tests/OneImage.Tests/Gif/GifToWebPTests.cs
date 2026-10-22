using OneImage.Codecs.WebP;

namespace OneImage.Tests.Gif;

public class GifToWebPTests
{
    // Load a GIF, resize it and save it as WebP: every frame, its delay and the loop count must survive.
    [Fact]
    public void AnimatedGifResizedToWebPKeepsFrames()
    {
        const int w = 120, h = 60, n = 5;
        var frames = new Rgba32[n][];
        for (int i = 0; i < n; i++)
        {
            frames[i] = new Rgba32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool inSquare = x >= i * 20 && x < (i * 20) + 20 && y >= 20 && y < 40;
                    frames[i][(y * w) + x] = inSquare ? new Rgba32(255, 0, 0, 255) : new Rgba32(0, 0, 255, 255);
                }
            }
        }

        byte[] gif = GifTestUtil.Encode(GifTestUtil.Animation(w, h, frames, 80, 2));

        Image resized = Image.Load(gif).Resize(width: 60);
        using var ms = new MemoryStream();
        resized.Save(ms, WebPFormat.Instance, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = true } });

        Image back = Image.Load(ms.ToArray());
        Assert.Equal(n, back.FrameCount);
        Assert.Equal(2, back.LoopCount);
        Assert.Equal(60, back.Width);
        Assert.Equal(30, back.Height);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(80 + (i * 10), back.Frames[i].Delay.TotalMilliseconds);

            // The red square moves 10 px per frame at half size; sample its center.
            Rgba32 c = back.Frames[i].ToArray<Rgba32>()[(15 * 60) + (i * 10) + 5];
            Assert.True(c.R > 200 && c.B < 60, $"frame {i}: {c.R},{c.G},{c.B}");
        }
    }
}
