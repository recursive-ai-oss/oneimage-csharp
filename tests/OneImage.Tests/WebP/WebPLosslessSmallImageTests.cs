using System.Runtime.InteropServices;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>
/// Small images (and the predictor and color-transform maps of bigger ones) use a hash map sized to the image in the
/// lossless match search instead of the full table. Repeating and flat images make that search find matches.
/// </summary>
public sealed class WebPLosslessSmallImageTests
{
    [Theory]
    [InlineData(16, 16, 0)]
    [InlineData(100, 100, 0)]
    [InlineData(128, 128, 1)]
    [InlineData(300, 200, 2)]
    [InlineData(1000, 40, 0)]
    [InlineData(1, 1, 1)]
    public void RepetitiveImagesRoundTrip(int w, int h, int kind)
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
