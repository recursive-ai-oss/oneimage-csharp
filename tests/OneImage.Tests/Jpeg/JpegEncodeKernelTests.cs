using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OneImage.Kernels;

namespace OneImage.Tests.Jpeg;

public class JpegEncodeKernelTests
{
    public static TheoryData<int> Counts => new() { 1, 2, 7, 8, 15, 16, 17, 31, 32, 33, 47, 100, 257 };

    [Fact]
    public void ReportHardwarePaths() =>
        TestContext.Current.SendDiagnosticMessage(
            $"Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Avx2={Avx2.IsSupported} Ssse3={Ssse3.IsSupported} Sse41={Sse41.IsSupported}");

    private static byte[] Samples(Random rnd, int length, int mode)
    {
        byte[] data = new byte[length];
        switch (mode)
        {
            case 0:
                rnd.NextBytes(data);
                break;
            case 1:
                Array.Fill(data, (byte)255);
                break;
            case 2:
                break;
            default:
                for (int i = 0; i < length; i++)
                {
                    data[i] = (byte)((i & 1) * 255);
                }

                break;
        }

        return data;
    }

    /// <summary>The vector color conversion gives exactly the scalar bytes, including for saturated and alternating pixels.</summary>
    [Theory]
    [MemberData(nameof(Counts))]
    public void RgbToYCbCrEqualsScalar(int count)
    {
        var rnd = new Random(count);
        for (int mode = 0; mode < 4 * 3; mode++)
        {
            byte[] rgb = Samples(rnd, (3 * count) + 5, mode % 4);
            if (mode >= 4)
            {
                // Primary colors and their mixes hit the extremes of every coefficient.
                for (int i = 0; i + 2 < rgb.Length; i += 3)
                {
                    int v = rnd.Next(8);
                    rgb[i] = (byte)((v & 1) * 255);
                    rgb[i + 1] = (byte)(((v >> 1) & 1) * 255);
                    rgb[i + 2] = (byte)(((v >> 2) & 1) * 255);
                }
            }

            byte[] y = new byte[count + 3], cb = new byte[count + 3], cr = new byte[count + 3];
            byte[] ey = new byte[count + 3], ecb = new byte[count + 3], ecr = new byte[count + 3];
            JpegEncodeKernels.RgbToYCbCr(rgb, y, cb, cr, count);
            JpegEncodeKernels.RgbToYCbCrScalar(rgb, ey, ecb, ecr, 0, count);
            Assert.Equal(ey, y);
            Assert.Equal(ecb, cb);
            Assert.Equal(ecr, cr);
        }
    }

    /// <summary>Every value of each channel against random values of the other two.</summary>
    [Fact]
    public void RgbToYCbCrEveryChannelValue()
    {
        var rnd = new Random(5);
        const int count = 256 * 3 * 16;
        byte[] rgb = new byte[3 * count];
        for (int round = 0; round < 40; round++)
        {
            for (int i = 0; i < count; i++)
            {
                int channel = i / (256 * 16);
                int value = i % 256;
                for (int c = 0; c < 3; c++)
                {
                    rgb[(3 * i) + c] = (byte)(c == channel ? value : rnd.Next(256));
                }
            }

            byte[] y = new byte[count], cb = new byte[count], cr = new byte[count];
            byte[] ey = new byte[count], ecb = new byte[count], ecr = new byte[count];
            JpegEncodeKernels.RgbToYCbCr(rgb, y, cb, cr, count);
            JpegEncodeKernels.RgbToYCbCrScalar(rgb, ey, ecb, ecr, 0, count);
            Assert.True(ey.AsSpan().SequenceEqual(y) && ecb.AsSpan().SequenceEqual(cb) && ecr.AsSpan().SequenceEqual(cr));
        }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void ChromaAveragesEqualScalar(int count)
    {
        var rnd = new Random(count + 100);
        for (int mode = 0; mode < 4; mode++)
        {
            byte[] a = Samples(rnd, (2 * count) + 4, mode);
            byte[] b = Samples(rnd, (2 * count) + 4, 3 - mode);
            byte[] d = new byte[count + 2];

            JpegEncodeKernels.Average2x2(a, b, d, count);
            for (int x = 0; x < count; x++)
            {
                Assert.Equal((a[2 * x] + a[(2 * x) + 1] + b[2 * x] + b[(2 * x) + 1] + (x % 2 == 0 ? 1 : 2)) >> 2, d[x]);
            }

            JpegEncodeKernels.Average2x1(a, d, count);
            for (int x = 0; x < count; x++)
            {
                Assert.Equal((a[2 * x] + a[(2 * x) + 1] + (x & 1)) >> 1, d[x]);
            }

            JpegEncodeKernels.Average1x2(a, b, d, count);
            for (int x = 0; x < count; x++)
            {
                Assert.Equal((a[x] + b[x] + 1) >> 1, d[x]);
            }
        }
    }
}
