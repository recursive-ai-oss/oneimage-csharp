using Xunit;
using static OneImage.Tests.Tone.ToneUtil;

namespace OneImage.Tests.Tone;

/// <summary>The Photoshop-style options of posterize, curves and brightness/contrast. The expected values come from renders Photoshop stored in sample files.</summary>
public class PhotoshopToneTests
{
    private static byte[] Ramp(Func<Image, Image> op)
    {
        var px = new Rgba32[256];
        for (int i = 0; i < 256; i++)
        {
            px[i] = new Rgba32((byte)i, (byte)i, (byte)i, 255);
        }

        return op(Image.FromPixels<Rgba32>(px, 256, 1)).ToArray<Rgba32>().Select(static p => p.R).ToArray();
    }

    [Theory]
    [InlineData(3, new[] { 0, 127, 255 }, new[] { 86, 171 })]
    [InlineData(7, new[] { 0, 42, 85, 127, 170, 212, 255 }, new[] { 37, 74, 110, 147, 183, 220 })]
    [InlineData(13, new[] { 0, 21, 42, 63, 85, 106, 127, 148, 170, 191, 212, 233, 255 }, new[] { 20, 40, 60, 79, 99, 119, 138, 158, 178, 197, 217, 237 })]
    public void PosterizeBucketsFollowPhotoshopOn8BitRamp(int levels, int[] outputs, int[] firstInputOfBucket)
    {
        byte[] r = Ramp(i => i.Posterize(levels, false, PosterizeMode.Buckets));
        for (int v = 0; v < 256; v++)
        {
            int bucket = 0;
            while (bucket < firstInputOfBucket.Length && v >= firstInputOfBucket[bucket])
            {
                bucket++;
            }

            Assert.Equal(outputs[bucket], r[v]);
        }
    }

    [Fact]
    public void PosterizeBucketsOnBinEdgesAndNearestIsUnchanged()
    {
        // 2 levels: the edge is exactly 128.
        byte[] two = Ramp(i => i.Posterize(2, false, PosterizeMode.Buckets));
        Assert.Equal(0, two[127]);
        Assert.Equal(255, two[128]);

        // The existing overload still rounds to the nearest level.
        byte[] near = Ramp(i => i.Posterize(3));
        Assert.Equal(0, near[63]);
        Assert.Equal(128, near[64]);
        Assert.Equal(near, Ramp(i => i.Posterize(3, false, PosterizeMode.Nearest)));
    }

    [Fact]
    public void NaturalCurveFollowsTheSplineAndTwoPointsAreALine()
    {
        // Three points: the natural spline has zero curvature at both ends; the reference is evaluated in double.
        CurvePoint[] pts = [new(0, 0), new(0.4, 0.7), new(1, 1)];
        byte[] r = Ramp(i => i.Curves(pts, ColorChannel.All, false, CurveInterpolation.Natural));
        double h0 = 0.4;
        double h1 = 0.6;

        // Second derivative at the middle point: 2(h0 + h1) m = 6((y2 - y1) / h1 - (y1 - y0) / h0).
        double m = 6 * (((1 - 0.7) / h1) - ((0.7 - 0) / h0)) / (2 * (h0 + h1));
        for (int v = 0; v < 256; v++)
        {
            double x = v / 255.0;
            double y;
            if (x < 0.4)
            {
                double b = x / h0;
                y = (b * 0.7) + ((((b * b * b) - b) * m) * h0 * h0 / 6);
            }
            else
            {
                double a = (1 - x) / h1;
                double b = (x - 0.4) / h1;
                y = (a * 0.7) + b + ((((a * a * a) - a) * m) * h1 * h1 / 6);
            }

            Assert.InRange(Math.Abs(r[v] - (Clamp01(y) * 255)), 0, 0.51);
        }

        byte[] line = Ramp(i => i.Curves([new(0, 0.2), new(1, 0.6)], ColorChannel.All, false, CurveInterpolation.Natural));
        for (int v = 0; v < 256; v++)
        {
            Assert.InRange(Math.Abs(line[v] - (255 * (0.2 + (0.4 * v / 255.0)))), 0, 0.51);
        }
    }

    [Fact]
    public void NaturalCurveCanBeNonMonotoneAndMonotoneDefaultIsUnchanged()
    {
        CurvePoint[] pts = [new(0, 0), new(0.3, 0.9), new(0.35, 0.1), new(1, 1)];
        byte[] nat = Ramp(i => i.Curves(pts, ColorChannel.All, false, CurveInterpolation.Natural));
        Assert.True(nat.Zip(nat.Skip(1), static (p, q) => q < p).Any(), "the points are out of order, so the curve is not monotone");
        byte[] mono = Ramp(i => i.Curves(pts));
        Assert.Equal(mono, Ramp(i => i.Curves(pts, ColorChannel.All, false, CurveInterpolation.Monotone)));
    }

    [Fact]
    public void LegacyBrightnessContrastMatchesStoredPhotoshopValues()
    {
        // Brightness 20, contrast 57 (positive: brightness first), values measured from a stored render.
        byte[] pos = Ramp(i => i.BrightnessContrast(20 / 150.0, 57 / 100.0, false, BrightnessContrastMode.PhotoshopLegacy));
        Assert.Equal(0, pos[40]);
        Assert.Equal(111, pos[100]);
        Assert.Equal(173, pos[127]);
        Assert.Equal(227, pos[150]);
        Assert.Equal(255, pos[163]);

        // Brightness -23, contrast -26 (negative: brightness after the slope).
        byte[] neg = Ramp(i => i.BrightnessContrast(-23 / 150.0, -26 / 100.0, false, BrightnessContrastMode.PhotoshopLegacy));
        Assert.Equal(12, neg[3]);
        Assert.Equal(104, neg[127]);
        Assert.Equal(199, neg[255]);
    }

    [Fact]
    public void NewModesAreIdentityAtZeroAndTheLinearModeMatchesTheOldOverload()
    {
        byte[] id = Ramp(static i => i);
        Assert.Equal(id, Ramp(i => i.BrightnessContrast(0, 0, false, BrightnessContrastMode.PhotoshopLegacy)));
        Assert.Equal(id, Ramp(i => i.BrightnessContrast(0, 0, false, BrightnessContrastMode.PhotoshopSmooth)));
        Assert.Equal(Ramp(i => i.BrightnessContrast(0.1, 0.3)), Ramp(i => i.BrightnessContrast(0.1, 0.3, false, BrightnessContrastMode.Linear)));
        byte[] smooth = Ramp(i => i.BrightnessContrast(0.5, 0, false, BrightnessContrastMode.PhotoshopSmooth));
        Assert.Equal(0, smooth[0]);
        Assert.Equal(255, smooth[255]);
        Assert.True(smooth[128] > 170, "positive brightness lifts the midtones strongly");
    }

    private static readonly Func<Image, Image>[] Chain =
    [
        i => i.Curves([new(0, 0), new(0.3, 0.45), new(0.8, 0.7), new(1, 1)], ColorChannel.All, false, CurveInterpolation.Natural),
        i => i.BrightnessContrast(0.1, 0.35, false, BrightnessContrastMode.PhotoshopLegacy),
        i => i.Posterize(7, false, PosterizeMode.Buckets),
        i => i.BrightnessContrast(-0.2, 0.3, false, BrightnessContrastMode.PhotoshopSmooth),
        i => i.Posterize(5, true, PosterizeMode.Buckets),
    ];

    [Fact]
    public void FusedPhotoshopStepsEqualOneByOne()
    {
        foreach (Func<Image> make in new Func<Image>[] { () => Make8(opaque: false), () => Make16(opaque: false), () => MakeFloat() })
        {
            Image fused = make();
            Image single = make();
            foreach (Func<Image, Image> op in Chain)
            {
                fused = op(fused);
                single = Barrier(op(single));
            }

            Assert.Equal(fused.Format, single.Format);
            if (fused.Format.Sample == SampleType.U8)
            {
                Assert.Equal(fused.ToArray<Rgba32>(), single.ToArray<Rgba32>());
            }
            else if (fused.Format.Sample == SampleType.U16)
            {
                Assert.Equal(fused.ToArray<Rgba64>(), single.ToArray<Rgba64>());
            }
            else
            {
                Assert.Equal(fused.ToArray<RgbaFloat>(), single.ToArray<RgbaFloat>());
            }
        }
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Image img = Make8();
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Posterize(1, false, PosterizeMode.Buckets));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Posterize(3, false, (PosterizeMode)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.Curves([new(0, 0), new(1, 1)], ColorChannel.All, false, (CurveInterpolation)5));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.BrightnessContrast(2, 0, false, BrightnessContrastMode.PhotoshopLegacy));
        Assert.Throws<ArgumentOutOfRangeException>(() => img.BrightnessContrast(0, 0, false, (BrightnessContrastMode)9));
    }
}
