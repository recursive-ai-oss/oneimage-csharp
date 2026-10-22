using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OneImage.Codecs.WebP;
using OneImage.Kernels;
using Xunit;

namespace OneImage.Tests.WebP;

/// <summary>
/// The lossy encoder's kernels (squared error, forward DCT, quantizer), its cost tables and sub-block predictors against plain
/// references, on random and extreme data. Run them again with DOTNET_EnableAVX2=0 and DOTNET_EnableHWIntrinsic=0 to cover the
/// other code paths.
/// </summary>
public sealed class WebPEncodeKernelTests
{
    [Fact]
    public void ReportsVectorFlags() =>
        Console.WriteLine($"Vector128.IsHardwareAccelerated={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Avx2={Avx2.IsSupported} Sse41={Sse41.IsSupported}");

    private static byte[] Bytes(Random rnd, int n, int mode)
    {
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = mode switch
            {
                0 => (byte)rnd.Next(256),
                1 => (byte)(rnd.Next(2) == 0 ? 0 : 255),
                _ => (byte)(120 + rnd.Next(8)),
            };
        }

        return b;
    }

    [Fact]
    public void SseMatchesScalar()
    {
        var rnd = new Random(1);
        foreach (int size in new[] { 4, 8, 16 })
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                foreach ((int sa, int sb) in new[] { (size, size), (16, 32), (32, 17), (size + 3, size + 1) })
                {
                    byte[] a = Bytes(rnd, (sa * size) + 20, mode);
                    byte[] b = Bytes(rnd, (sb * size) + 20, mode);
                    int ao = rnd.Next(10);
                    int bo = rnd.Next(10);
                    Assert.Equal(WebPSseKernel.Scalar(a, ao, sa, b, bo, sb, size), WebPSseKernel.Sse(a, ao, sa, b, bo, sb, size));
                }
            }
        }
    }

    [Fact]
    public void SseRejectsBlocksOutsideTheBuffers()
    {
        byte[] a = new byte[100];
        Assert.Throws<ArgumentException>(() => WebPSseKernel.Sse(a, 90, 16, a, 0, 16, 16));
        Assert.Throws<ArgumentException>(() => WebPSseKernel.Sse(a, 0, 3, a, 0, 16, 4));
        Assert.Throws<ArgumentException>(() => WebPSseKernel.Sse(a, 0, 16, a, 0, 16, 5));
    }

    [Fact]
    public void ForwardDctMatchesScalar()
    {
        var rnd = new Random(2);
        Span<short> v = stackalloc short[16];
        Span<short> s = stackalloc short[16];
        foreach (int mode in new[] { 0, 1, 2 })
        {
            for (int iter = 0; iter < 400; iter++)
            {
                int ss = 4 + rnd.Next(30);
                int ps = 4 + rnd.Next(30);
                byte[] src = Bytes(rnd, (ss * 4) + 8, mode);
                byte[] pred = Bytes(rnd, (ps * 4) + 8, mode);
                int so = rnd.Next(4);
                int po = rnd.Next(4);
                WebPFdctKernel.Forward(src, so, ss, pred, po, ps, v);
                WebPFdctKernel.Scalar(src, so, ss, pred, po, ps, s);
                Assert.True(v.SequenceEqual(s));
            }
        }
    }

    [Theory]
    [InlineData(4, 4, 0, false)]
    [InlineData(5, 7, 1, true)]
    [InlineData(20, 30, 1, true)]
    [InlineData(60, 100, 0, true)]
    [InlineData(157, 243, 1, false)]
    [InlineData(8, 8, 0, false)]
    public void QuantizerMatchesReference(int dcStep, int acStep, int first, bool sharpen)
    {
        var m = new VP8QuantMatrix(dcStep, acStep, 96, 110, sharpen);
        var rnd = new Random(dcStep + acStep);
        Span<short> c1 = stackalloc short[16];
        Span<short> c2 = stackalloc short[16];
        Span<short> c3 = stackalloc short[16];
        Span<short> l1 = stackalloc short[16];
        Span<short> l2 = stackalloc short[16];
        Span<short> l3 = stackalloc short[16];
        for (int iter = 0; iter < 3000; iter++)
        {
            int range = iter % 4 switch { 0 => 40, 1 => 600, 2 => 8000, _ => 32767 };
            for (int i = 0; i < 16; i++)
            {
                int v = rnd.Next(-range, range + 1);
                if (iter % 7 == 0 && i % 3 == 0)
                {
                    v = 0;
                }

                c1[i] = c2[i] = c3[i] = (short)v;
                l1[i] = l2[i] = l3[i] = 77;
            }

            int r1 = m.Quantize(c1, l1, first);
            int r2 = WebPQuantKernel.Scalar(c2, l2, first, m.Q, m.IQ, m.Bias, m.Sharpen);
            int r3 = OldQuantize(m, c3, l3, first);
            Assert.Equal(r3, r1);
            Assert.Equal(r3, r2);
            Assert.True(c1.SequenceEqual(c3));
            Assert.True(c2.SequenceEqual(c3));
            Assert.True(l1.SequenceEqual(l3));
            Assert.True(l2.SequenceEqual(l3));
        }
    }

    // The quantizer as first written, with its zero-threshold test.
    private static int OldQuantize(VP8QuantMatrix m, Span<short> coeffs, Span<short> levels, int first)
    {
        int last = -1;
        for (int n = first; n < 16; n++)
        {
            int j = VP8Fwd.Zigzag[n];
            int v = coeffs[j];
            bool neg = v < 0;
            int c = (neg ? -v : v) + m.Sharpen[j];
            int zthresh = ((1 << 17) - 1 - m.Bias[j]) / m.IQ[j];
            int level = 0;
            if (c > zthresh)
            {
                level = (int)((((long)c * m.IQ[j]) + m.Bias[j]) >> 17);
                if (level > 2047)
                {
                    level = 2047;
                }
            }

            if (level != 0)
            {
                last = n;
                coeffs[j] = (short)((neg ? -level : level) * m.Q[j]);
                levels[n] = (short)(neg ? -level : level);
            }
            else
            {
                coeffs[j] = 0;
                levels[n] = 0;
            }
        }

        return last;
    }

    [Fact]
    public void BlockCostMatchesTokenWalk()
    {
        var rnd = new Random(3);
        byte[] probs = VP8Tables.DefaultCoeffProbs.ToArray();
        short[] lv = new short[16];
        for (int iter = 0; iter < 20000; iter++)
        {
            int range = iter % 3 switch { 0 => 2, 1 => 12, _ => 2047 };
            int density = 1 + rnd.Next(16);
            for (int i = 0; i < 16; i++)
            {
                lv[i] = (short)(rnd.Next(16) < density ? rnd.Next(-range, range + 1) : 0);
            }

            int type = rnd.Next(4);
            int first = type == 0 ? 1 : 0;
            int ctx = rnd.Next(3);
            var sink = new CostSink(probs);
            VP8Tokens.EmitBlock(ref sink, lv, type, first, ctx);
            int last = -1;
            for (int i = 15; i >= first && last < 0; i--)
            {
                last = lv[i] != 0 ? i : -1;
            }

            Assert.Equal(sink.Cost, VP8Cost.BlockCost(lv, type, first, ctx, last));
        }
    }

    [Fact]
    public void AllSubBlockPredictionsMatchOneByOne()
    {
        var rnd = new Random(4);
        const int Bps = VP8Decoder.Bps;
        byte[] all = new byte[160];
        for (int iter = 0; iter < 200; iter++)
        {
            byte[] w = Bytes(rnd, Bps * 27, iter % 3);
            int off = VP8Decoder.YOff + (rnd.Next(4) * 4 * Bps) + (rnd.Next(3) * 4);
            Predict.AllSubBlocks(w, off, all);
            for (int m = 0; m < 10; m++)
            {
                byte[] copy = (byte[])w.Clone();
                Predict.SubBlock(copy, off, m);
                for (int r = 0; r < 4; r++)
                {
                    Assert.True(copy.AsSpan(off + (r * Bps), 4).SequenceEqual(all.AsSpan((m * 16) + (r * 4), 4)));
                }
            }
        }
    }

    [Fact]
    public void OutputDoesNotDependOnTheDegree()
    {
        // Odd size so that the last macroblock row and column are partial; noisy enough to use every mode.
        int w = 203, h = 171;
        var rnd = new Random(5);
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 4;
                int baseV = (int)(128 + (80 * Math.Sin(x / 9.0) * Math.Cos(y / 13.0)));
                rgba[o] = (byte)Math.Clamp(baseV + rnd.Next(-12, 13), 0, 255);
                rgba[o + 1] = (byte)Math.Clamp((x * 255 / w) + rnd.Next(-6, 7), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(y * 255 / h, 0, 255);
                rgba[o + 3] = 255;
            }
        }

        byte[] one = VP8Encoder.Encode(rgba, w, h, 75, 4, CancellationToken.None, 1);
        foreach (int degree in new[] { 2, 3, 8 })
        {
            Assert.True(one.AsSpan().SequenceEqual(VP8Encoder.Encode(rgba, w, h, 75, 4, CancellationToken.None, degree)), $"degree {degree}");
        }
    }

    [Fact]
    public void EncodingStopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        byte[] rgba = new byte[64 * 64 * 4];
        Assert.ThrowsAny<OperationCanceledException>(() => VP8Encoder.Encode(rgba, 64, 64, 75, 4, cts.Token, 4));
    }
}
