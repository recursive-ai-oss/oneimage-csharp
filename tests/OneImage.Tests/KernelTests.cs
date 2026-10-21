using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OneImage.Kernels;

namespace OneImage.Tests;

public class KernelTests
{
    public static TheoryData<int> Widths => new() { 1, 7, 8, 15, 16, 17, 31, 33, 100, 128 };

    public static IEnumerable<PixelFormat> Formats() =>
    [
        PixelFormat.Gray8, PixelFormat.Rgb8, PixelFormat.Rgba8, PixelFormat.Rgba8Premultiplied, PixelFormat.Gray16, PixelFormat.Rgba16,
        PixelFormat.RgbF32, PixelFormat.RgbaF32, PixelFormat.RgbaF32Premultiplied, PixelFormat.RgbaF16, PixelFormat.Cmyk8,
    ];

    [Fact]
    public void ReportHardwarePaths() =>
        TestContext.Current.SendDiagnosticMessage($"Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} Avx2={Avx2.IsSupported} Avx512F={Avx512F.IsSupported}");

    [Theory]
    [MemberData(nameof(Widths))]
    public void InvertVectorMatchesScalar(int width)
    {
        var rnd = new Random(width);
        foreach (PixelFormat f in Formats())
        {
            byte[] src = new byte[width * f.BytesPerPixel];
            rnd.NextBytes(src);
            if (f.Sample == SampleType.F32)
            {
                // Keep floats finite and in a normal range so comparison is meaningful.
                Span<float> fl = SpanCast.As<float>(src.AsSpan());
                for (int i = 0; i < fl.Length; i++)
                {
                    fl[i] = rnd.NextSingle();
                }
            }
            else if (f.Sample == SampleType.F16)
            {
                Span<Half> h = SpanCast.As<Half>(src.AsSpan());
                for (int i = 0; i < h.Length; i++)
                {
                    h[i] = (Half)rnd.NextSingle();
                }
            }

            if (f.Alpha == AlphaMode.Premultiplied && f.Sample == SampleType.U8)
            {
                for (int i = 0; i < width; i++)
                {
                    int a = src[(i * 4) + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        src[(i * 4) + c] = (byte)Math.Min(src[(i * 4) + c], a);
                    }
                }
            }

            byte[] vec = new byte[src.Length];
            byte[] sca = new byte[src.Length];
            InvertKernel.Invert(src, vec, f);
            InvertKernel.InvertScalar(src, sca, f);
            Assert.True(vec.AsSpan().SequenceEqual(sca), $"{f} width {width}");
        }
    }

    [Fact]
    public void InvertHasDocumentedSemantics()
    {
        byte[] src = [10, 20, 30, 200];
        byte[] dst = new byte[4];
        InvertKernel.Invert(src, dst, PixelFormat.Rgba8);
        Assert.Equal(new byte[] { 245, 235, 225, 200 }, dst);
        byte[] pre = [10, 20, 30, 200];
        InvertKernel.Invert(pre, dst, PixelFormat.Rgba8Premultiplied);
        Assert.Equal(new byte[] { 190, 180, 170, 200 }, dst);
    }

    [Fact]
    public void IntegerWideningAndNarrowingRoundTrip()
    {
        byte[] all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        byte[] wide = new byte[512];
        ConvertKernel.ConvertRow(all, PixelFormat.Gray8, wide, PixelFormat.Gray16, 256);
        ushort[] w16 = SpanCast.As<ushort>(wide).ToArray();
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(i * 257, w16[i]);
        }

        byte[] back = new byte[256];
        ConvertKernel.ConvertRow(wide, PixelFormat.Gray16, back, PixelFormat.Gray8, 256);
        Assert.Equal(all, back);
    }

    [Fact]
    public void ConvertAlphaAndModelRules()
    {
        byte[] straight = [200, 100, 50, 128];
        byte[] pre = new byte[4];
        ConvertKernel.ConvertRow(straight, PixelFormat.Rgba8, pre, PixelFormat.Rgba8Premultiplied, 1);
        Assert.Equal(new byte[] { 100, 50, 25, 128 }, pre);

        byte[] rgb = new byte[3];
        ConvertKernel.ConvertRow(straight, PixelFormat.Rgba8, rgb, PixelFormat.Rgb8, 1);
        Assert.Equal(new byte[] { 200, 100, 50 }, rgb);

        byte[] opaque = new byte[4];
        ConvertKernel.ConvertRow(rgb, PixelFormat.Rgb8, opaque, PixelFormat.Rgba8, 1);
        Assert.Equal(new byte[] { 200, 100, 50, 255 }, opaque);

        byte[] gray = new byte[1];
        ConvertKernel.ConvertRow([255, 255, 255], PixelFormat.Rgb8, gray, PixelFormat.Gray8, 1);
        Assert.Equal(255, gray[0]);
        byte[] g3 = new byte[3];
        ConvertKernel.ConvertRow([77], PixelFormat.Gray8, g3, PixelFormat.Rgb8, 1);
        Assert.Equal(new byte[] { 77, 77, 77 }, g3);

        // CMYK crosses to RGB through the default ICC profiles: no ink is paper white.
        byte[] white = new byte[3];
        ConvertKernel.ConvertRow([0, 0, 0, 0], PixelFormat.Cmyk8, white, PixelFormat.Rgb8, 1);
        Assert.Equal(new byte[] { 255, 255, 255 }, white);
    }

    [Fact]
    public void FloatPackClampsAndHandlesNaN()
    {
        float[] v = [-1f, 0.5f, 2f, float.NaN];
        byte[] b = new byte[4];
        ConvertKernel.Pack(v, SampleType.U8, b);
        Assert.Equal(new byte[] { 0, 128, 255, 0 }, b);
    }
}
