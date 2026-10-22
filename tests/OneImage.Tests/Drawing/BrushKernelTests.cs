using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OneImage.Kernels;
using Xunit;

namespace OneImage.Tests.Drawing;

public class BrushKernelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(128)]
    public void RgbaF32ToRgba8MatchesTheScalarReference(int pixels)
    {
        if (Environment.GetEnvironmentVariable("ONEIMAGE_PRINT_HW") != null)
        {
            Assert.Fail($"HW Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} Avx2={Avx2.IsSupported}");
        }

        var rnd = new Random(pixels);
        var src = new float[pixels * 4];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = (rnd.Next(8)) switch
            {
                0 => float.NaN,
                1 => -(float)rnd.NextDouble(),
                2 => 1f + (float)rnd.NextDouble(),
                3 => rnd.Next(256) / 255f,
                4 => (rnd.Next(255) + 0.5f) / 255f,
                5 => float.PositiveInfinity,
                _ => (float)rnd.NextDouble(),
            };
        }

        var vector = new byte[src.Length];
        var scalar = new byte[src.Length];
        BrushKernel.RgbaF32ToRgba8(src, vector);
        BrushKernel.RgbaF32ToRgba8Scalar(src, scalar);
        Assert.Equal(scalar, vector);
    }

    [Fact]
    public void ExactByteValuesRoundTrip()
    {
        var src = new float[256 * 4];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = (i / 4) / 255f;
        }

        var dst = new byte[src.Length];
        BrushKernel.RgbaF32ToRgba8(src, dst);
        for (int i = 0; i < dst.Length; i++)
        {
            Assert.Equal(i / 4, dst[i]);
        }
    }
}
