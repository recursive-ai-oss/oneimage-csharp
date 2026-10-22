using System.Runtime.Intrinsics;
using OneImage.Kernels;

namespace OneImage.Tests.Bmp;

public class BmpPixelsKernelTests
{
    private static readonly int[] s_widths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 15, 16, 17, 31, 33, 47, 48, 49, 100, 257];

    [Fact]
    public void Swap3MatchesScalar()
    {
        Console.WriteLine($"Vector128.IsHardwareAccelerated={Vector128.IsHardwareAccelerated}");
        var rng = new Random(1);
        foreach (int w in s_widths)
        {
            byte[] src = new byte[w * 3];
            rng.NextBytes(src);
            byte[] expected = new byte[(w * 3) + 8];
            byte[] actual = new byte[(w * 3) + 8];
            Array.Fill(expected, (byte)0xEE);
            Array.Fill(actual, (byte)0xEE);
            BmpPixels.Swap3Scalar(src, expected, w);
            BmpPixels.Swap3(src, actual, w);
            Assert.Equal(expected, actual); // also proves nothing is written past the pixels
        }
    }

    [Fact]
    public void Narrow4MatchesScalar()
    {
        var rng = new Random(2);
        foreach (int w in s_widths)
        {
            byte[] src = new byte[w * 4];
            rng.NextBytes(src);
            byte[] expected = new byte[(w * 3) + 8];
            byte[] actual = new byte[(w * 3) + 8];
            Array.Fill(expected, (byte)0xEE);
            Array.Fill(actual, (byte)0xEE);
            BmpPixels.Narrow4Scalar(src, expected, w);
            BmpPixels.Narrow4(src, actual, w);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Swap4MatchesScalar()
    {
        var rng = new Random(3);
        foreach (int w in s_widths)
        {
            byte[] src = new byte[w * 4];
            rng.NextBytes(src);
            byte[] expected = new byte[(w * 4) + 8];
            byte[] actual = new byte[(w * 4) + 8];
            Array.Fill(expected, (byte)0xEE);
            Array.Fill(actual, (byte)0xEE);
            BmpPixels.Swap4Scalar(src, expected, w);
            BmpPixels.Swap4(src, actual, w);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(333)]
    public void RoundTripsOddWidthsThroughTheEncoderAndDecoder(int width)
    {
        // Rows are padded to four bytes, bottom-up, and decoded in blocks: odd widths and heights cover every edge of that.
        var rng = new Random(width);
        foreach (bool alpha in new[] { false, true })
        {
            var px = new Rgba32[width * 7];
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), alpha ? (byte)rng.Next(256) : (byte)255);
            }

            using var ms = new MemoryStream();
            Image.FromPixels<Rgba32>(px, width, 7).Save(ms, BmpFormat.Instance);
            Rgba32[] back = Image.Load(ms.ToArray(), new LoadOptions { Configuration = new ImageConfiguration().Add(BmpFormat.Instance) }).ToArray<Rgba32>();
            Assert.Equal(px, back);
        }
    }
}
