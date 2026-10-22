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
}
