using OneImage.Kernels;

namespace OneImage.Tests.Tiff;

public sealed class TiffPredictorKernelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RunningSumMatchesTheDefinition(int channels)
    {
        var rnd = new Random(channels);
        foreach (int length in new[] { 0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 33, 100, 1001 })
        {
            byte[] a = new byte[length];
            rnd.NextBytes(a);
            if (length == 100)
            {
                a.AsSpan().Fill(255); // wraps on every addition
            }

            byte[] want = (byte[])a.Clone();
            for (int i = channels; i < want.Length; i++)
            {
                want[i] = (byte)(want[i] + want[i - channels]);
            }

            TiffKernels.RunningSum8(a, channels);
            Assert.Equal(want, a);
        }
    }
}
