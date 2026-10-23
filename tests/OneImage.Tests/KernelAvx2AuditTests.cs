using System.Runtime.Intrinsics;
using OneImage.Kernels;

namespace OneImage.Tests;

/// <summary>Vector kernels whose conversions were reworked to avoid emulated unsigned operations, checked against one-pixel-at-a-time references.</summary>
public class KernelAvx2AuditTests
{
    [Fact]
    public void ColorMatrixRowKernelEqualsItsPerPixelPathAtEveryWidth()
    {
        Console.WriteLine($"Vector256 accelerated: {Vector256.IsHardwareAccelerated}, Vector128: {Vector128.IsHardwareAccelerated}");
        var rng = new Random(11);
        foreach (int inCh in new[] { 3, 4 })
        {
            foreach (int outCh in new[] { 3, 4 })
            {
                foreach (bool computeAlpha in new[] { false, true })
                {
                    if (outCh == 4 && !computeAlpha && inCh != 4)
                    {
                        continue;
                    }

                    for (int round = 0; round < 6; round++)
                    {
                        float[] m = new float[20];
                        for (int i = 0; i < 20; i++)
                        {
                            m[i] = round == 0 && i % 5 != 0 ? 0f : (float)((rng.NextDouble() * 2.4) - 0.7);
                        }

                        foreach (int width in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 15, 16, 17, 31, 33, 100 })
                        {
                            byte[] src = new byte[width * inCh];
                            rng.NextBytes(src);
                            byte[] got = new byte[width * outCh];
                            byte[] want = new byte[width * outCh];
                            ColorMatrixKernel.Apply8(src, got, width, inCh, outCh, computeAlpha, m);
                            for (int x = 0; x < width; x++)
                            {
                                ColorMatrixKernel.Apply8(src.AsSpan(x * inCh, inCh), want.AsSpan(x * outCh, outCh), 1, inCh, outCh, computeAlpha, m);
                            }

                            Assert.True(want.AsSpan().SequenceEqual(got), $"in {inCh} out {outCh} alpha {computeAlpha} width {width} round {round}");
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void ColorMatrixWithNonFiniteWeightsGivesZeroOrSaturatedBytesOnEveryPath()
    {
        float[] m = new float[20];
        for (int i = 0; i < 20; i++)
        {
            m[i] = i % 5 == 0 ? 1f : 0f;
        }

        m[0] = float.NaN;
        m[5] = float.PositiveInfinity;
        m[10] = float.NegativeInfinity;
        byte[] src = new byte[37 * 4];
        new Random(3).NextBytes(src);
        byte[] got = new byte[src.Length];
        byte[] want = new byte[src.Length];
        ColorMatrixKernel.Apply8(src, got, 37, 4, 4, true, m);
        for (int x = 0; x < 37; x++)
        {
            ColorMatrixKernel.Apply8(src.AsSpan(x * 4, 4), want.AsSpan(x * 4, 4), 1, 4, 4, true, m);
        }

        Assert.True(want.AsSpan().SequenceEqual(got));
    }

    [Fact]
    public void DitherPixelHelpersMatchTheScalarDefinition()
    {
        var rng = new Random(5);
        for (int n = 0; n < 20000; n++)
        {
            uint px = (uint)rng.NextInt64(0, 1L << 32);
            float[] e = new float[4];
            for (int i = 0; i < 4; i++)
            {
                e[i] = n % 4 == 0 ? (float)((rng.NextDouble() * 8) - 4) : (float)((rng.NextDouble() * 0.6) - 0.3);
            }

            uint got = DitherKernel.AddShare(px, Vector128.Create(e[0], e[1], e[2], e[3]));
            uint want = 0;
            for (int c = 0; c < 4; c++)
            {
                float b = ((px >> (8 * c)) & 0xFF) / 255f;
                float t = ((b + e[c]) * 255f) + 0.5f;
                t = Math.Min(Math.Max(t, 0f), 255f);
                want |= (uint)(byte)t << (8 * c);
            }

            Assert.Equal(want, got);
            Vector128<float> unit = DitherKernel.Unit(px);
            for (int c = 0; c < 4; c++)
            {
                Assert.Equal(((px >> (8 * c)) & 0xFF) / 255f, unit.GetElement(c));
            }
        }
    }
}
