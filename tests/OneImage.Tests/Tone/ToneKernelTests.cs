using OneImage.Kernels;
using Xunit;
using static OneImage.Tests.Tone.ToneUtil;

namespace OneImage.Tests.Tone;

public class ToneKernelTests
{
    private static readonly int[] s_widths = [1, 2, 7, 8, 9, 15, 16, 17, 31, 33, 100, 128];

    public static IEnumerable<object[]> Widths() => s_widths.Select(w => new object[] { w });

    [Theory]
    [MemberData(nameof(Widths))]
    public void EncodedGrayscaleVectorMatchesScalarReference(int width)
    {
        var rnd = new Random(width);
        byte[] src = new byte[width * 4];
        rnd.NextBytes(src);
        byte[] got = new byte[src.Length];
        ToneKernel.GrayscaleRow8(src, got, width, 4, toGray: false, linear: false);
        for (int x = 0; x < width; x++)
        {
            byte y = (byte)((((src[x * 4] * 13933) + (src[(x * 4) + 1] * 46871) + (src[(x * 4) + 2] * 4732)) + 32768) >> 16);
            Assert.Equal(new[] { y, y, y, src[(x * 4) + 3] }, got.AsSpan(x * 4, 4).ToArray());
        }

        // Gray plus alpha output.
        byte[] ga = new byte[width * 2];
        ToneKernel.GrayscaleRow8(src, ga, width, 4, toGray: true, linear: false);
        for (int x = 0; x < width; x++)
        {
            Assert.Equal(got[x * 4], ga[x * 2]);
            Assert.Equal(src[(x * 4) + 3], ga[(x * 2) + 1]);
        }
    }

    [Theory]
    [MemberData(nameof(Widths))]
    public void LutKernelMatchesNaiveLookup(int width)
    {
        var rnd = new Random(width + 100);
        byte[][] tables = Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 256).Select(_ => (byte)rnd.Next(256)).ToArray()).ToArray();
        foreach ((int ch, int colors) in new[] { (4, 3), (3, 3), (1, 1), (2, 1) })
        {
            byte[] src = new byte[width * ch];
            rnd.NextBytes(src);
            byte[] got = new byte[src.Length];
            ToneKernel.ApplyLut8(src, got, width, ch, colors, tables);
            for (int x = 0; x < width; x++)
            {
                for (int k = 0; k < ch; k++)
                {
                    byte expect = k < colors ? tables[k][src[(x * ch) + k]] : src[(x * ch) + k];
                    Assert.Equal(expect, got[(x * ch) + k]);
                }
            }
        }
    }

    [Fact]
    public void LutKernelRejectsBadTables()
    {
        byte[] src = new byte[16];
        Assert.Throws<ArgumentException>(() => ToneKernel.ApplyLut8(src, new byte[16], 4, 4, 3, [new byte[255], new byte[256], new byte[256]]));
        Assert.Throws<ArgumentException>(() => ToneKernel.ApplyLut8(src, new byte[4], 4, 4, 3, [new byte[256], new byte[256], new byte[256]]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectGrayscaleKernelEqualsTheFloatPlanePath(bool linear)
    {
        // A fused Invert, Invert pair is the identity but forces the plane path; the lone-step path uses the row kernel.
        foreach (bool opaque in new[] { true, false })
        {
            Image img = Make8(333, 131, opaque);
            Rgba32[] direct = img.Grayscale(linear).ToArray<Rgba32>();
            Rgba32[] planes = img.Grayscale(linear).Invert().Invert().ToArray<Rgba32>();
            Assert.Equal(direct, planes);
            Rgb24[] rgb = img.RemoveAlpha().Grayscale(linear).ToArray<Rgb24>();
            Assert.Equal(rgb, img.RemoveAlpha().Grayscale(linear).Invert().Invert().ToArray<Rgb24>());
        }
    }

    [Fact]
    public void LinearLumaIsWithinTwentiethOfAStepOfTheExactCurve()
    {
        var rnd = new Random(1);
        double worst = 0;
        for (int i = 0; i < 200000; i++)
        {
            int r = rnd.Next(256), g = rnd.Next(256), b = rnd.Next(256);
            double y = Enc((0.2126 * Dec(r / 255.0)) + (0.7152 * Dec(g / 255.0)) + (0.0722 * Dec(b / 255.0))) * 255;
            int got = ToneKernel.Luma8(r, g, b);
            // Distance to the nearest rounding of the exact value, minus the half step that rounding itself allows.
            worst = Math.Max(worst, Math.Abs(got - y) - 0.5);
        }

        Assert.True(worst < 0.06, $"worst excess {worst}");
    }
}
