using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OneImage.Codecs.WebP;
using OneImage.Kernels;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>
/// The lossy output stage (chroma upsampling, YUV to RGB) and the lossless predictor helpers against plain per-channel
/// references, on random and extreme data and at widths around the vector block sizes.
/// </summary>
public sealed class WebPYuvKernelTests
{
    private static readonly int[] Widths = [1, 2, 3, 7, 8, 9, 15, 16, 17, 18, 31, 32, 33, 47, 48, 63, 64, 65, 100, 129];

    private static byte[] Random(int n, int seed, int mode)
    {
        var rnd = new Random(seed);
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = mode switch
            {
                0 => (byte)rnd.Next(256),
                1 => (byte)(rnd.Next(2) == 0 ? 0 : 255),
                _ => (byte)Math.Clamp(16 + (i % 7) + rnd.Next(0, 3), 0, 255),
            };
        }

        return b;
    }

    [Fact]
    public void ReportsVectorFlags() =>
        Console.WriteLine($"Vector128.IsHardwareAccelerated={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Avx2={Avx2.IsSupported} Ssse3={Ssse3.IsSupported}");

    [Fact]
    public void YuvRowMatchesScalar()
    {
        foreach (int width in Widths)
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                foreach (int bpp in new[] { 3, 4 })
                {
                    foreach (bool alpha in new[] { false, true })
                    {
                        byte[] y = Random(width + 16, width, mode);
                        byte[] u = Random(width + 16, width + 1, mode);
                        byte[] v = Random(width + 16, width + 2, mode);
                        byte[] a = Random(width + 16, width + 3, 0);
                        // Guard bytes after the row catch writes past the end.
                        byte[] expected = new byte[(width * bpp) + 32];
                        byte[] actual = new byte[(width * bpp) + 32];
                        Array.Fill(expected, (byte)0x5a);
                        Array.Fill(actual, (byte)0x5a);
                        ReadOnlySpan<byte> alphaSpan = alpha ? a : default;
                        WebPYuvKernel.YuvRowScalar(y, u, v, alphaSpan, expected, width, bpp);
                        WebPYuvKernel.YuvRow(y, u, v, alphaSpan, actual, width, bpp);
                        Assert.True(expected.AsSpan().SequenceEqual(actual), $"width {width} mode {mode} bpp {bpp} alpha {alpha}");
                    }
                }
            }
        }
    }

    [Fact]
    public void YuvRowCoversWholeRangeOfEachPlane()
    {
        // Every value of one plane against a few fixed values of the others.
        byte[] ramp = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            ramp[i] = (byte)i;
        }

        foreach (int fixedValue in new[] { 0, 16, 128, 235, 240, 255 })
        {
            byte[] c = new byte[256];
            Array.Fill(c, (byte)fixedValue);
            for (int plane = 0; plane < 3; plane++)
            {
                byte[] y = plane == 0 ? ramp : c;
                byte[] u = plane == 1 ? ramp : c;
                byte[] v = plane == 2 ? ramp : c;
                byte[] expected = new byte[256 * 3];
                byte[] actual = new byte[256 * 3];
                WebPYuvKernel.YuvRowScalar(y, u, v, default, expected, 256, 3);
                WebPYuvKernel.YuvRow(y, u, v, default, actual, 256, 3);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"fixed {fixedValue} plane {plane}");
            }
        }
    }

    [Fact]
    public void ChromaMatchesScalar()
    {
        foreach (int width in Widths)
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                foreach (bool bottom in new[] { false, true })
                {
                    int cw = ((width - 1) >> 1) + 1;
                    byte[] t = Random(cw + 20, width, mode);
                    byte[] c = Random(cw + 20, width + 5, mode);
                    byte[] topE = new byte[width + 32];
                    byte[] botE = new byte[width + 32];
                    byte[] topA = new byte[width + 32];
                    byte[] botA = new byte[width + 32];
                    foreach (byte[] arr in new[] { topE, botE, topA, botA })
                    {
                        Array.Fill(arr, (byte)0x77);
                    }

                    WebPYuvKernel.ChromaScalar(t, c, width, topE, botE, bottom);
                    WebPYuvKernel.Chroma(t, c, width, topA, botA, bottom);
                    Assert.True(topE.AsSpan().SequenceEqual(topA), $"top width {width} mode {mode} bottom {bottom}");
                    Assert.True(botE.AsSpan().SequenceEqual(botA), $"bottom width {width} mode {mode} bottom {bottom}");
                }
            }
        }
    }

    [Fact]
    public void ShortBuffersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => WebPYuvKernel.YuvRow(new byte[8], new byte[8], new byte[8], default, new byte[23], 8, 3));
        Assert.Throws<ArgumentException>(() => WebPYuvKernel.YuvRow(new byte[8], new byte[8], new byte[8], new byte[4], new byte[32], 8, 4));
        Assert.Throws<ArgumentException>(() => WebPYuvKernel.Chroma(new byte[3], new byte[3], 8, new byte[8], new byte[8], false));
        Assert.Throws<ArgumentException>(() => WebPYuvKernel.Chroma(new byte[4], new byte[4], 8, new byte[8], new byte[7], true));
    }

    // Plain per-channel versions of the lossless predictors (RFC 9649 section 4.1).
    private static uint Ch(uint v, int i) => (v >> (8 * i)) & 0xff;

    [Fact]
    public void LosslessPredictorHelpersMatchPerChannelDefinitions()
    {
        var rnd = new Random(11);
        for (int iter = 0; iter < 200000; iter++)
        {
            uint a = (uint)rnd.NextInt64(0, 1L << 32);
            uint b = (uint)rnd.NextInt64(0, 1L << 32);
            uint c = (uint)rnd.NextInt64(0, 1L << 32);
            if (iter % 5 == 0)
            {
                // Near-equal pixels hit the tie cases.
                b = a ^ (uint)(1 << rnd.Next(32));
                c = a;
            }

            // Select: Manhattan distances of the gradient estimate to left and to top.
            int pL = 0;
            int pT = 0;
            uint grad = 0;
            uint halfRef = 0;
            uint avg = VP8LDecoder.Average2(a, b);
            for (int i = 0; i < 4; i++)
            {
                int l = (int)Ch(a, i);
                int t = (int)Ch(b, i);
                int tl = (int)Ch(c, i);
                int est = l + t - tl;
                pL += Math.Abs(est - l);
                pT += Math.Abs(est - t);
                grad |= (uint)Math.Clamp(est, 0, 255) << (8 * i);
                int av = (int)Ch(avg, i);
                int h = av + ((av - tl) / 2);
                halfRef |= (uint)Math.Clamp(h, 0, 255) << (8 * i);
            }

            uint select = pL < pT ? a : b;
            Assert.Equal(select, VP8LDecoder.Select(a, b, c));
            Assert.Equal(grad, VP8LDecoder.ClampAddSubtractFull(a, b, c));
            Assert.Equal(halfRef, VP8LDecoder.ClampAddSubtractHalf(avg, c));
        }
    }
}
