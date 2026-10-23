using System.Runtime.InteropServices;
using OneImage.Codecs.WebP;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>
/// The encoders rent their large buffers (source planes, coded levels, output chunks, hash tables) from the shared array
/// pool, which hands out arrays holding old data. These check that nothing depends on a rented array being clean.
/// </summary>
public sealed class WebPPooledBuffersTests
{
    private static byte[] Noise(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        byte[] p = new byte[w * h * 4];
        rnd.NextBytes(p);
        for (int i = 3; i < p.Length; i += 4)
        {
            p[i] = 255;
        }

        return p;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void LossyOutputIsTheSameWhenThePoolHoldsOtherImages(int degree)
    {
        byte[] a = Noise(150, 90, 1);
        byte[] first = VP8Encoder.Encode(a, 150, 90, 75, 4, CancellationToken.None, degree);

        // Fill the pool with arrays holding other images: a bigger one and a smaller one.
        _ = VP8Encoder.Encode(Noise(400, 300, 2), 400, 300, 40, 6, CancellationToken.None, degree);
        _ = VP8Encoder.Encode(Noise(20, 20, 3), 20, 20, 90, 0, CancellationToken.None, degree);

        Assert.True(first.AsSpan().SequenceEqual(VP8Encoder.Encode(a, 150, 90, 75, 4, CancellationToken.None, degree)));
    }

    [Fact]
    public void CancelledEncodeDoesNotDisturbTheNextOne()
    {
        byte[] a = Noise(130, 70, 4);
        byte[] first = VP8Encoder.Encode(a, 130, 70, 75, 4, CancellationToken.None, 4);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => VP8Encoder.Encode(a, 130, 70, 75, 4, cts.Token, 4));

        Assert.True(first.AsSpan().SequenceEqual(VP8Encoder.Encode(a, 130, 70, 75, 4, CancellationToken.None, 4)));
    }

    // Repeating and flat images make the lossless encoder find matches in its predictor and color-transform maps, which are
    // small enough to use the compact hash map instead of the full table.
    [Theory]
    [InlineData(16, 16, 0)]
    [InlineData(100, 100, 0)]
    [InlineData(128, 128, 1)]
    [InlineData(300, 200, 2)]
    [InlineData(1000, 40, 0)]
    [InlineData(1, 1, 1)]
    public void LosslessSmallAndRepetitiveImagesRoundTrip(int w, int h, int kind)
    {
        var rnd = new Random(w + kind);
        byte[] tile = new byte[16 * 16 * 4];
        rnd.NextBytes(tile);
        byte[] p = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    p[(((y * w) + x) * 4) + c] = kind switch
                    {
                        0 => tile[((((y % 16) * 16) + (x % 16)) * 4) + c],
                        1 => (byte)((((x / 8) + (y / 8)) & 1) * 200),
                        _ => (byte)(((x * 3) + (y * 5) + (c * 40)) & 0xe0),
                    };
                }
            }
        }

        Image img = Image.FromPixels<Rgba32>(MemoryMarshal.Cast<byte, Rgba32>(p), w, h);
        foreach (int effort in new[] { 0, 4, 6 })
        {
            using var ms = new MemoryStream();
            img.Save(ms, WebPFormat.Instance, new SaveOptions { EncoderOptions = new WebPEncoderOptions { Lossless = true, Effort = effort } });
            Rgba32[] back = Image.Load(ms.ToArray()).ToArray<Rgba32>();
            Assert.True(MemoryMarshal.AsBytes<Rgba32>(back).SequenceEqual(p), $"effort {effort}");
        }
    }
}
