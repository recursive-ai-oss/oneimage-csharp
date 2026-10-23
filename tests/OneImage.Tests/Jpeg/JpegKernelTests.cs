using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OneImage.Kernels;

namespace OneImage.Tests.Jpeg;

public class JpegKernelTests
{
    public static TheoryData<int> Widths => new() { 1, 2, 3, 7, 8, 9, 15, 16, 17, 31, 33, 100, 257 };

    [Fact]
    public void ReportHardwarePaths() =>
        TestContext.Current.SendDiagnosticMessage(
            $"Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} Avx2={Avx2.IsSupported} IdctVector={JpegIdct.UsesVectorPath} Idct128={JpegIdct.UsesVector128} Sse2={Sse2.IsSupported} AdvSimd={System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported}");

    private static int[] Quant(Random rnd, bool flat)
    {
        int[] q = new int[64];
        for (int i = 0; i < 64; i++)
        {
            q[i] = flat ? 1 : rnd.Next(1, 120);
        }

        return q;
    }

    [Fact]
    public void Idct8VectorMatchesScalar()
    {
        var rnd = new Random(1234);
        short[] coef = new short[64];
        byte[] a = new byte[8 * 11];
        byte[] b = new byte[8 * 11];
        for (int iter = 0; iter < 20000; iter++)
        {
            int kind = iter % 6;
            Array.Clear(coef);
            int[] q = Quant(rnd, kind == 5);
            switch (kind)
            {
                case 0: // dense small
                    for (int i = 0; i < 64; i++)
                    {
                        coef[i] = (short)rnd.Next(-40, 41);
                    }

                    break;
                case 1: // DC only
                    coef[0] = (short)rnd.Next(-1000, 1001);
                    break;
                case 2: // sparse
                    for (int i = 0; i < 6; i++)
                    {
                        coef[rnd.Next(64)] = (short)rnd.Next(-600, 601);
                    }

                    break;
                case 3: // large, saturating
                    for (int i = 0; i < 64; i++)
                    {
                        coef[i] = (short)rnd.Next(-2000, 2001);
                    }

                    break;
                case 4: // extremes
                    for (int i = 0; i < 64; i++)
                    {
                        coef[i] = rnd.Next(2) == 0 ? short.MinValue : short.MaxValue;
                    }

                    break;
                default:
                    for (int i = 0; i < 64; i++)
                    {
                        coef[i] = (short)rnd.Next(-300, 301);
                    }

                    break;
            }

            Array.Fill(a, (byte)7);
            Array.Fill(b, (byte)7);
            JpegIdct.Idct8Scalar(coef, q, a, 11 > 8 ? 11 : 8);
            JpegIdct.Idct8(coef, q, b, 11);
            Assert.True(a.AsSpan().SequenceEqual(b), $"iteration {iter} kind {kind}");
            if (JpegIdct.UsesVector128)
            {
                byte[] c = new byte[8 * 11];
                Array.Fill(c, (byte)7);
                JpegIdct.Idct8Vector128(coef, q, c, 11);
                Assert.True(a.AsSpan().SequenceEqual(c), $"iteration {iter} kind {kind} (128-bit)");
            }
        }
    }

    /// <summary>The 128-bit 4x4 and 2x2 transforms (byte and ushort output, via the dispatcher too) equal the scalar ones.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void ReducedIdctVectorMatchesScalar(int precision)
    {
        var rnd = new Random(77 + precision);
        short[] coef = new short[64];
        for (int iter = 0; iter < 30000; iter++)
        {
            int kind = iter % 6;
            int range = kind switch { 0 => 40, 1 => 0, 2 => 600, 3 => 2000, _ => 300 };
            Array.Clear(coef);
            int[] q = Quant(rnd, kind == 5);
            for (int i = 0; i < 64; i++)
            {
                if (kind == 4)
                {
                    coef[i] = rnd.Next(2) == 0 ? short.MinValue : short.MaxValue;
                }
                else if (kind == 2 ? rnd.Next(10) == 0 : i == 0 || kind != 1)
                {
                    coef[i] = (short)rnd.Next(-range, range + 1);
                }
            }

            int stride = 4 + (iter % 4);
            foreach (int size in new[] { 4, 2 })
            {
                ushort[] expected = new ushort[(size * stride) + 3];
                Array.Fill(expected, (ushort)9);
                if (size == 4)
                {
                    JpegIdct.Idct4(coef, q, expected, stride, precision);
                }
                else
                {
                    JpegIdct.Idct2(coef, q, expected, stride, precision);
                }

                ushort[] wide = new ushort[expected.Length];
                Array.Fill(wide, (ushort)9);
                JpegIdct.Block<ushort>(coef, q, wide, stride, size, precision);
                Assert.True(expected.AsSpan().SequenceEqual(wide), $"ushort size {size} iteration {iter}");
                if (JpegIdct.UsesVector128)
                {
                    ushort[] direct = new ushort[expected.Length];
                    Array.Fill(direct, (ushort)9);
                    if (size == 4)
                    {
                        JpegIdct.Idct4Vector128<ushort>(coef, q, direct, stride, precision);
                    }
                    else
                    {
                        JpegIdct.Idct2Vector128<ushort>(coef, q, direct, stride, precision);
                    }

                    Assert.True(expected.AsSpan().SequenceEqual(direct), $"ushort 128-bit size {size} iteration {iter}");
                }

                if (precision == 8)
                {
                    byte[] b = new byte[expected.Length];
                    Array.Fill(b, (byte)9);
                    JpegIdct.Block<byte>(coef, q, b, stride, size, precision);
                    Assert.True(expected.Select(v => (byte)v).SequenceEqual(b), $"byte size {size} iteration {iter}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void UpsamplersMatchScalarReference(int n)
    {
        var rnd = new Random(n);
        byte[] near = new byte[n + 20];
        byte[] far = new byte[n + 20];
        rnd.NextBytes(near);
        rnd.NextBytes(far);
        ushort[] nearW = near.Select(v => (ushort)v).ToArray();
        ushort[] farW = far.Select(v => (ushort)v).ToArray();

        byte[] d1 = new byte[(2 * n) + 20];
        ushort[] d2 = new ushort[(2 * n) + 20];
        JpegSampling.UpsampleH2V2<byte>(near, far, n, d1);
        JpegSampling.UpsampleH2V2<ushort>(nearW, farW, n, d2);
        Assert.Equal(d2.Take(2 * n).Select(v => (byte)v), d1.Take(2 * n));

        Array.Clear(d1);
        Array.Clear(d2);
        if (n >= 2)
        {
            JpegSampling.UpsampleH2V1<byte>(near, n, d1);
            JpegSampling.UpsampleH2V1<ushort>(nearW, n, d2);
            Assert.Equal(d2.Take(2 * n).Select(v => (byte)v), d1.Take(2 * n));
        }

        foreach (int bias in new[] { 1, 2 })
        {
            Array.Clear(d1);
            Array.Clear(d2);
            JpegSampling.UpsampleH1V2<byte>(near, far, n, bias, d1);
            JpegSampling.UpsampleH1V2<ushort>(nearW, farW, n, bias, d2);
            Assert.Equal(d2.Take(n).Select(v => (byte)v), d1.Take(n));
        }

        foreach (int factor in new[] { 2, 3, 4 })
        {
            byte[] r1 = new byte[(n * factor) + 20];
            ushort[] r2 = new ushort[(n * factor) + 20];
            JpegSampling.Replicate<byte>(near, n, factor, r1);
            JpegSampling.Replicate<ushort>(nearW, n, factor, r2);
            Assert.Equal(r2.Take(n * factor).Select(v => (byte)v), r1.Take(n * factor));
        }
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void ColorConversionMatchesScalarReference(int n)
    {
        var rnd = new Random(n + 99);
        byte[] y = new byte[n + 20];
        byte[] cb = new byte[n + 20];
        byte[] cr = new byte[n + 20];
        byte[] k = new byte[n + 20];
        rnd.NextBytes(y);
        rnd.NextBytes(cb);
        rnd.NextBytes(cr);
        rnd.NextBytes(k);
        ushort[] yw = y.Select(v => (ushort)v).ToArray();
        ushort[] cbw = cb.Select(v => (ushort)v).ToArray();
        ushort[] crw = cr.Select(v => (ushort)v).ToArray();
        ushort[] kw = k.Select(v => (ushort)v).ToArray();

        byte[] a = new byte[(3 * n) + 48];
        ushort[] b = new ushort[(3 * n) + 48];
        JpegSampling.YccToRgb<byte>(y, cb, cr, a, n, 255);
        JpegSampling.YccToRgb<ushort>(yw, cbw, crw, b, n, 255);
        Assert.Equal(b.Take(3 * n).Select(v => (byte)v), a.Take(3 * n));

        Array.Clear(a);
        Array.Clear(b);
        JpegSampling.InterleaveRgb<byte>(y, cb, cr, a, n);
        JpegSampling.InterleaveRgb<ushort>(yw, cbw, crw, b, n);
        Assert.Equal(b.Take(3 * n).Select(v => (byte)v), a.Take(3 * n));

        byte[] c1 = new byte[(4 * n) + 8];
        ushort[] c2 = new ushort[(4 * n) + 8];
        foreach (bool invert in new[] { false, true })
        {
            JpegSampling.YcckToCmyk<byte>(y, cb, cr, k, c1, n, 255, invert);
            JpegSampling.YcckToCmyk<ushort>(yw, cbw, crw, kw, c2, n, 255, invert);
            Assert.Equal(c2.Take(4 * n).Select(v => (byte)v), c1.Take(4 * n));
        }
    }

    [Fact]
    public void ReducedIdctOfFlatBlockIsExact()
    {
        short[] coef = new short[64];
        int[] q = Quant(new Random(1), true);
        coef[0] = 80;
        foreach (int size in new[] { 1, 2, 4, 8 })
        {
            byte[] dst = new byte[64];
            JpegIdct.Block<byte>(coef, q, dst, 8, size, 8);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Assert.Equal(138, dst[(y * 8) + x]);
                }
            }
        }
    }

    /// <summary>The vector transform and quantizer give exactly the scalar results, for random, flat and extreme blocks and odd strides.</summary>
    [Fact]
    public void ForwardQuantizeVectorEqualsScalar()
    {
        var rnd = new Random(11);
        for (int iter = 0; iter < 20000; iter++)
        {
            int stride = 8 + (iter % 5 * 3);
            byte[] plane = new byte[(8 * stride) + 8];
            switch (iter % 4)
            {
                case 0:
                    rnd.NextBytes(plane);
                    break;
                case 1:
                    Array.Fill(plane, (byte)rnd.Next(256));
                    break;
                case 2:
                    for (int i = 0; i < plane.Length; i++)
                    {
                        plane[i] = (byte)(((i + (i / stride)) & 1) * 255);
                    }

                    break;
                default:
                    for (int i = 0; i < plane.Length; i++)
                    {
                        plane[i] = (byte)Math.Clamp(128 + rnd.Next(-3, 4) + (i % stride * 9), 0, 255);
                    }

                    break;
            }

            int[] q = Quant(rnd, iter % 7 == 0);
            if (iter % 11 == 0)
            {
                Array.Fill(q, 255);
            }

            uint[] recip = q.Select(JpegFdct.Reciprocal).ToArray();
            short[] expected = new short[64];
            int[] coef = new int[64];
            JpegFdct.Forward(plane, stride, coef);
            JpegFdct.Quantize(coef, q, recip, OneImage.Codecs.Jpeg.JpegConstants.Zigzag, expected);
            short[] actual = new short[64];
            JpegFdct.ForwardQuantize(plane, stride, q, recip, OneImage.Codecs.Jpeg.JpegConstants.Zigzag, actual);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"iteration {iter}");
            if (JpegIdct.UsesVector128)
            {
                int[] v128 = new int[64];
                JpegFdct.ForwardVector128(plane, stride, v128);
                Assert.True(coef.AsSpan().SequenceEqual(v128), $"iteration {iter} (128-bit transform)");
                short[] nat = new short[64];
                short[] natRef = new short[64];
                JpegFdct.QuantizeVector128(coef, q, recip, nat);
                for (int k = 0; k < 64; k++)
                {
                    natRef[OneImage.Codecs.Jpeg.JpegConstants.Zigzag[k] & 63] = expected[k];
                }

                Assert.True(natRef.AsSpan().SequenceEqual(nat), $"iteration {iter} (128-bit quantizer)");
            }

            if (Avx2.IsSupported)
            {
                int[] vec = new int[64];
                JpegFdct.ForwardAvx2(plane, stride, vec);
                Assert.True(coef.AsSpan().SequenceEqual(vec), $"iteration {iter} (transform)");
            }
        }
    }
}
