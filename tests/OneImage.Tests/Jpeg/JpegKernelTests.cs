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
            $"Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} Avx2={Avx2.IsSupported} IdctVector={JpegIdct.UsesVectorPath}");

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
}
