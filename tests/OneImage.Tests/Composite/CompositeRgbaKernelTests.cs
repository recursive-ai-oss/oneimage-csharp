using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using OneImage.Kernels;

namespace OneImage.Tests.Composite;

public class CompositeRgbaKernelTests
{
    private static readonly PixelFormat Work = new(ColorModel.Rgb, SampleType.F32, AlphaMode.Premultiplied);

    private static byte[] Random(int pixels, int seed)
    {
        var rng = new Random(seed);
        byte[] b = new byte[pixels * 4];
        rng.NextBytes(b);
        for (int i = 0; i < pixels; i++)
        {
            int pick = rng.Next(8);
            if (pick == 0)
            {
                b[(i * 4) + 3] = 0;
            }
            else if (pick == 1)
            {
                b[(i * 4) + 3] = 255;
            }
            else if (pick == 2)
            {
                b[(i * 4) + 3] = (byte)rng.Next(1, 4);
            }
        }

        return b;
    }

    // The general path the fast kernel replaces: premultiplied float, BlendKernel.Row, back to straight bytes.
    private static byte[] General(byte[] back, byte[] over, int pixels, float opacity)
    {
        float[] fb = new float[pixels * 4], fo = new float[pixels * 4], fd = new float[pixels * 4];
        ConvertKernel.ConvertRow(back, PixelFormat.Rgba8, MemoryMarshal.AsBytes(fb.AsSpan()), Work, pixels);
        ConvertKernel.ConvertRow(over, PixelFormat.Rgba8, MemoryMarshal.AsBytes(fo.AsSpan()), Work, pixels);
        BlendKernel.Row(BlendMode.Normal, fb, fo, fd, 3, opacity, AlphaComposition.SrcOver);
        byte[] result = new byte[pixels * 4];
        ConvertKernel.ConvertRow(MemoryMarshal.AsBytes(fd.AsSpan()), Work, result, PixelFormat.Rgba8, pixels);
        return result;
    }

    [Fact]
    public void VectorAndScalarEqualTheGeneralFloatPath()
    {
        Console.WriteLine($"Vector256 accelerated: {Vector256.IsHardwareAccelerated}, Vector128: {Vector128.IsHardwareAccelerated}, Vector512: {Vector512.IsHardwareAccelerated}");
        foreach (int width in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 33, 127, 128, 257, 6000 })
        {
            foreach (float opacity in new[] { 1f, 0.5f, 0.123456f, 0.999f, 0.001f, 0.75f })
            {
                byte[] back = Random(width, width + 1);
                byte[] over = Random(width, width + 2);
                byte[] expected = General(back, over, width, opacity);
                byte[] vec = new byte[width * 4];
                byte[] sca = new byte[width * 4];
                CompositeKernel.OverRgba8(back, over, vec, width, opacity);
                CompositeKernel.OverRgba8Scalar(back, over, sca, width, opacity);
                Assert.True(expected.AsSpan().SequenceEqual(sca), $"scalar differs: width {width} opacity {opacity}");
                Assert.True(expected.AsSpan().SequenceEqual(vec), $"vector differs: width {width} opacity {opacity}");
            }
        }
    }

    [Fact]
    public void EveryChannelValueAndAlphaCombinationMatches()
    {
        // The vector kernel replaces the division by 255 with a fused correction that is exact for whole numbers 0..255; this covers every channel value on both sides.
        const int N = 65536;
        foreach (byte ba in new byte[] { 0, 1, 77, 128, 254, 255 })
        {
            foreach (byte oa in new byte[] { 0, 1, 77, 128, 200, 254, 255 })
            {
                byte[] back = new byte[N * 4];
                byte[] over = new byte[N * 4];
                for (int i = 0; i < N; i++)
                {
                    back[i * 4] = (byte)i;
                    back[(i * 4) + 1] = (byte)(i >> 8);
                    back[(i * 4) + 2] = (byte)((i * 7) + (i >> 8));
                    back[(i * 4) + 3] = ba;
                    over[i * 4] = (byte)(i >> 8);
                    over[(i * 4) + 1] = (byte)i;
                    over[(i * 4) + 2] = (byte)((i * 13) ^ (i >> 5));
                    over[(i * 4) + 3] = oa;
                }

                foreach (float opacity in new[] { 1f, 0.5f, 0.3f, 0.123456f, 0.999f, 0.001f, 0f })
                {
                    byte[] expected = General(back, over, N, opacity);
                    byte[] vec = new byte[N * 4];
                    CompositeKernel.OverRgba8(back, over, vec, N, opacity);
                    Assert.True(expected.AsSpan().SequenceEqual(vec), $"vector differs: backdrop alpha {ba}, overlay alpha {oa}, opacity {opacity}");
                }
            }
        }
    }

    [Fact]
    public void OddOffsetsAndStrides()
    {
        // Slices that start at odd byte offsets still work.
        byte[] back = Random(70, 1);
        byte[] over = Random(70, 2);
        byte[] expected = General(back.AsSpan(4, 33 * 4).ToArray(), over.AsSpan(8, 33 * 4).ToArray(), 33, 0.6f);
        byte[] outBuffer = new byte[(70 * 4) + 3];
        CompositeKernel.OverRgba8(back.AsSpan(4, 33 * 4), over.AsSpan(8, 33 * 4), outBuffer.AsSpan(3, 33 * 4), 33, 0.6f);
        Assert.True(expected.AsSpan().SequenceEqual(outBuffer.AsSpan(3, 33 * 4)));
    }
}
