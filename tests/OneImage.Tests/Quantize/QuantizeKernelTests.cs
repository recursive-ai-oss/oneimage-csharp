using OneImage.Kernels;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace OneImage.Tests.Quantize;

public class QuantizeKernelTests
{
    [Fact]
    public void HasAlphaBelowMatchesScalarOnEdgeSizes()
    {
        Console.WriteLine($"Vector256.IsHardwareAccelerated={Vector256.IsHardwareAccelerated} Avx2.IsSupported={Avx2.IsSupported}");
        var rng = new Random(9);
        foreach (int px in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 31, 33, 100, 257 })
        {
            for (int trial = 0; trial < 40; trial++)
            {
                byte[] data = new byte[px * 4];
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = trial % 2 == 0 ? (byte)255 : (byte)rng.Next(256);
                }

                if (trial % 4 == 2 && px > 0)
                {
                    data[(rng.Next(px) * 4) + 3] = 3; // a single transparent pixel somewhere
                }

                if (trial % 4 == 3 && px > 0)
                {
                    data[rng.Next(px) * 4] = 0; // low alpha-looking bytes outside the alpha lane must be ignored
                }

                foreach (byte t in new byte[] { 1, 128, 255 })
                {
                    Assert.Equal(QuantizeKernels.HasAlphaBelowScalar(data, t), QuantizeKernels.HasAlphaBelow(data, t));
                }
            }
        }
    }

    [Fact]
    public void NearestMatchesScalarIncludingTiesAndSkip()
    {
        Console.WriteLine($"Vector256.IsHardwareAccelerated={Vector256.IsHardwareAccelerated}");
        var rng = new Random(21);
        foreach (int count in new[] { 1, 2, 7, 8, 9, 16, 17, 33, 100, 255, 256 })
        {
            int padded = QuantizeKernels.PaddedLength(count);
            for (int trial = 0; trial < 60; trial++)
            {
                int[] r = Ops.Quantize.QuantizedPalette.PaddedTable(padded);
                int[] g = Ops.Quantize.QuantizedPalette.PaddedTable(padded);
                int[] b = Ops.Quantize.QuantizedPalette.PaddedTable(padded);
                int[] a = Ops.Quantize.QuantizedPalette.PaddedTable(padded);
                // Few distinct values make equal distances (ties) common.
                int range = trial % 3 == 0 ? 4 : 256;
                for (int i = 0; i < count; i++)
                {
                    r[i] = rng.Next(range);
                    g[i] = rng.Next(range);
                    b[i] = rng.Next(range);
                    a[i] = trial % 2 == 0 ? 255 : rng.Next(range);
                }

                for (int q = 0; q < 20; q++)
                {
                    int skip = q % 3 == 0 ? rng.Next(count) : -1;
                    int cr = rng.Next(range), cg = rng.Next(range), cb = rng.Next(range), ca = rng.Next(256);
                    Assert.Equal(
                        QuantizeKernels.NearestScalar(r, g, b, a, count, skip, cr, cg, cb, ca),
                        QuantizeKernels.Nearest(r, g, b, a, count, skip, cr, cg, cb, ca));
                }
            }
        }
    }
}
