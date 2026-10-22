using System.Runtime.Intrinsics;
using OneImage.Kernels;

namespace OneImage.Tests;

public class ConvertFastTests
{
    public static TheoryData<int> Widths => new() { 1, 3, 4, 5, 7, 8, 15, 16, 17, 31, 33, 100, 129 };

    private static readonly (PixelFormat From, PixelFormat To, int Tolerance)[] Pairs =
    [
        (PixelFormat.Rgba8, PixelFormat.Rgb8, 0),
        (PixelFormat.Rgb8, PixelFormat.Rgba8, 0),
        (PixelFormat.Rgba8, PixelFormat.Rgba8Premultiplied, 0),
        (PixelFormat.Rgba8Premultiplied, PixelFormat.Rgba8, 1),
        (PixelFormat.Gray8, PixelFormat.Rgb8, 0),
        (PixelFormat.Gray8, PixelFormat.Rgba8, 0),
        (PixelFormat.GrayAlpha8, PixelFormat.Rgba8, 0),
        (PixelFormat.GrayAlpha8, PixelFormat.Rgb8, 0),
        (PixelFormat.Gray8, PixelFormat.GrayAlpha8, 0),
        (PixelFormat.GrayAlpha8, PixelFormat.Gray8, 0),
        (PixelFormat.Gray8, PixelFormat.Gray16, 0),
        (PixelFormat.Gray16, PixelFormat.Gray8, 0),
        (PixelFormat.Rgba8, PixelFormat.Rgba16, 0),
        (PixelFormat.Rgba16, PixelFormat.Rgba8, 0),
        (PixelFormat.Rgb8, PixelFormat.Rgb16, 0),
        (PixelFormat.Rgb16, PixelFormat.Rgb8, 0),
        (PixelFormat.Cmyk8, PixelFormat.Cmyk16, 0),
        (PixelFormat.Cmyk16, PixelFormat.Cmyk8, 0),
    ];

    private static readonly PixelFormat GrayAlpha16 = new(ColorModel.Gray, SampleType.U16, AlphaMode.Straight);

    private static PixelFormat[] LayoutFormats =>
    [
        PixelFormat.Gray8, PixelFormat.GrayAlpha8, PixelFormat.Rgb8, PixelFormat.Rgba8,
        PixelFormat.Gray16, GrayAlpha16, PixelFormat.Rgb16, PixelFormat.Rgba16,
    ];

    /// <summary>Every layout change between the straight-alpha gray/RGB 8- and 16-bit formats, except RGB to gray, equals the float path.</summary>
    [Fact]
    public void LayoutAndSampleChangesAreBitIdenticalToFloatPath()
    {
        const int Width = 65536 + 3;
        var rnd = new Random(5);
        int pairs = 0;
        foreach (PixelFormat from in LayoutFormats)
        {
            foreach (PixelFormat to in LayoutFormats)
            {
                if (from == to || (from.Model == ColorModel.Rgb && to.Model == ColorModel.Gray))
                {
                    continue;
                }

                byte[] src = new byte[Width * from.BytesPerPixel];
                if (from.Sample == SampleType.U8)
                {
                    // Each channel walks through all 256 values, offset per channel.
                    for (int i = 0; i < src.Length; i++)
                    {
                        src[i] = (byte)((i / from.ChannelCount) + ((i % from.ChannelCount) * 85));
                    }
                }
                else
                {
                    rnd.NextBytes(src);
                    Span<ushort> u = SpanCast.As<ushort>(src.AsSpan());
                    for (int i = 0; i < 6; i++)
                    {
                        u[i] = i % 2 == 0 ? (ushort)0 : (ushort)65535;
                    }
                }

                byte[] fast = new byte[Width * to.BytesPerPixel];
                byte[] reference = new byte[fast.Length];
                ConvertKernel.ConvertRow(src, from, fast, to, Width);
                ConvertKernel.ConvertViaFloat(src, from, reference, to, Width);
                Assert.True(fast.AsSpan().SequenceEqual(reference), $"{from} -> {to}");
                pairs++;
            }
        }

        Assert.Equal(40, pairs);
    }

    [Fact]
    public void ReportHardwarePaths() =>
        TestContext.Current.SendDiagnosticMessage($"Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}");

    [Theory]
    [MemberData(nameof(Widths))]
    public void FastPathsMatchFloatReference(int width)
    {
        var rnd = new Random(width);
        foreach ((PixelFormat from, PixelFormat to, int tol) in Pairs)
        {
            for (int round = 0; round < 8; round++)
            {
                byte[] src = new byte[width * from.BytesPerPixel];
                rnd.NextBytes(src);
                if (from.Alpha == AlphaMode.Premultiplied)
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

                byte[] fast = new byte[width * to.BytesPerPixel];
                byte[] reference = new byte[fast.Length];
                ConvertKernel.ConvertRow(src, from, fast, to, width);
                ConvertKernel.ConvertViaFloat(src, from, reference, to, width);
                AssertClose(reference, fast, tol, from.Sample == SampleType.U16 || to.Sample == SampleType.U16, $"{from} -> {to} width {width}");
            }
        }
    }

    [Fact]
    public void PremultiplyIsExactForEveryColorAndAlpha()
    {
        byte[] src = new byte[65536 * 4];
        for (int i = 0; i < 65536; i++)
        {
            src[i * 4] = (byte)(i >> 8);
            src[(i * 4) + 1] = (byte)i;
            src[(i * 4) + 2] = (byte)(i >> 8);
            src[(i * 4) + 3] = (byte)i;
        }

        byte[] fast = new byte[src.Length];
        byte[] reference = new byte[src.Length];
        ConvertKernel.ConvertRow(src, PixelFormat.Rgba8, fast, PixelFormat.Rgba8Premultiplied, 65536);
        ConvertKernel.ConvertViaFloat(src, PixelFormat.Rgba8, reference, PixelFormat.Rgba8Premultiplied, 65536);
        Assert.True(fast.AsSpan().SequenceEqual(reference));
    }

    [Fact]
    public void SixteenBitConversionsAreExactForEveryValue()
    {
        byte[] eight = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        byte[] w1 = new byte[512];
        byte[] w2 = new byte[512];
        ConvertKernel.ConvertRow(eight, PixelFormat.Gray8, w1, PixelFormat.Gray16, 256);
        ConvertKernel.ConvertViaFloat(eight, PixelFormat.Gray8, w2, PixelFormat.Gray16, 256);
        Assert.True(w1.AsSpan().SequenceEqual(w2));

        ushort[] all = Enumerable.Range(0, 65536).Select(i => (ushort)i).ToArray();
        byte[] bytes = SpanCast.AsBytes<ushort>(all).ToArray();
        byte[] n1 = new byte[65536];
        byte[] n2 = new byte[65536];
        ConvertKernel.ConvertRow(bytes, PixelFormat.Gray16, n1, PixelFormat.Gray8, 65536);
        ConvertKernel.ConvertViaFloat(bytes, PixelFormat.Gray16, n2, PixelFormat.Gray8, 65536);
        Assert.True(n1.AsSpan().SequenceEqual(n2));
    }

    private static void AssertClose(byte[] expected, byte[] actual, int tolerance, bool wide, string what)
    {
        if (expected.Length != actual.Length)
        {
            Assert.Fail(what);
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(expected[i] - actual[i]) > tolerance && !wide)
            {
                Assert.Fail($"{what}: byte {i} expected {expected[i]} got {actual[i]}");
            }
        }

        if (wide)
        {
            Assert.True(expected.AsSpan().SequenceEqual(actual), what);
        }
    }
}
