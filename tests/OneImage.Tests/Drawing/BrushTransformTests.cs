using System.Numerics;
using OneImage.Drawing;
using Xunit;

namespace OneImage.Tests.Drawing;

/// <summary>The transformed linear gradient, the two-circle (focal) radial gradient and bilinear image sampling, against analytic references.</summary>
public class BrushTransformTests
{
    private static readonly ColorStop[] BlackWhite = [new(0f, new Rgba32(0, 0, 0, 255)), new(1f, new Rgba32(255, 255, 255, 255))];

    private static float[] Row(Brush brush, int x, int y, int n)
    {
        float[] row = new float[n * 4];
        brush.FillRow(x, y, row);
        return row;
    }

    // Level (0..255) of a gray ramp at position t with pad.
    private static double Level(double t) => Math.Clamp(t, 0, 1) * 255;

    [Fact]
    public void TransformedLinearGradientMatchesTheAnalyticProjection()
    {
        var m = Matrix3x2.CreateScale(1.7f, 0.6f) * Matrix3x2.CreateRotation(0.5f) * Matrix3x2.CreateTranslation(31.5f, -4.25f);
        Brush brush = Brush.LinearGradient(new Vector2(3, 2), new Vector2(40, 17), BlackWhite, m);
        Assert.True(Matrix3x2.Invert(m, out Matrix3x2 inv));
        double worst = 0;
        for (int y = 0; y < 60; y += 7)
        {
            float[] row = Row(brush, 5, y, 80);
            for (int i = 0; i < 80; i++)
            {
                // Pixel center back into gradient space (in double), then the projection on the start..end line.
                double px = 5 + i + 0.5, py = y + 0.5;
                double u = (px * inv.M11) + (py * inv.M21) + inv.M31, v = (px * inv.M12) + (py * inv.M22) + inv.M32;
                double dx = 37, dy = 15;
                double t = (((u - 3) * dx) + ((v - 2) * dy)) / ((dx * dx) + (dy * dy));
                worst = Math.Max(worst, Math.Abs((row[i * 4] * 255) - Level(t)));
                Assert.Equal(1f, row[(i * 4) + 3]);
            }
        }

        Assert.True(worst <= 1, $"worst error {worst} levels");
    }

    [Fact]
    public void IdentityTransformGivesTheUntransformedGradient()
    {
        Brush a = Brush.LinearGradient(new Vector2(3, 2), new Vector2(40, 17), BlackWhite, GradientRepetition.Reflect);
        Brush b = Brush.LinearGradient(new Vector2(3, 2), new Vector2(40, 17), BlackWhite, Matrix3x2.Identity, GradientRepetition.Reflect);
        float[] ra = Row(a, -20, 9, 120), rb = Row(b, -20, 9, 120);
        for (int i = 0; i < ra.Length; i++)
        {
            Assert.InRange(Math.Abs(ra[i] - rb[i]) * 255, 0, 0.5);
        }
    }

    // t of the circle that grows from (f, fr) to (c, r) and passes through p, found by bisection on the larger root with a non-negative radius.
    private static double? ReferenceT(Vector2 p, Vector2 f, double fr, Vector2 c, double r)
    {
        double G(double t)
        {
            double cx = f.X + (t * (c.X - f.X)), cy = f.Y + (t * (c.Y - f.Y)), rad = fr + (t * (r - fr));
            return Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy))) - rad;
        }

        // Scan from large t downwards for the first sign change among circles with non-negative radius.
        double prevT = 64, prev = G(prevT);
        for (double t = 64 - 0.01; t >= -64; t -= 0.01)
        {
            double g = G(t);
            double rad = fr + (t * (r - fr));
            double radPrev = fr + (prevT * (r - fr));
            if (rad >= 0 && radPrev >= 0 && (g == 0 || (g < 0) != (prev < 0)))
            {
                double lo = t, hi = prevT;
                for (int i = 0; i < 80; i++)
                {
                    double mid = (lo + hi) / 2;
                    if ((G(mid) < 0) == (G(lo) < 0))
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                return (lo + hi) / 2;
            }

            prevT = t;
            prev = g;
        }

        return null;
    }

    [Theory]
    [InlineData(100f, 100f, 60f, 100f, 100f, 0f)]   // concentric, no focal offset
    [InlineData(100f, 100f, 60f, 120f, 90f, 0f)]    // focal point inside the circle
    [InlineData(100f, 100f, 60f, 110f, 100f, 15f)]  // focal circle inside the circle
    [InlineData(100f, 100f, 60f, 100f, 100f, 80f)]  // focal circle larger than the circle (concentric)
    [InlineData(100f, 100f, 40f, 170f, 100f, 0f)]   // focal point outside: a cone
    public void FocalGradientMatchesTheTwoCircleDefinition(float cx, float cy, float r, float fx, float fy, float fr)
    {
        var transform = Matrix3x2.CreateScale(1.25f, 0.8f) * Matrix3x2.CreateTranslation(9f, -6f);
        Brush brush = Brush.RadialGradient(new Vector2(cx, cy), r, new Vector2(fx, fy), fr, BlackWhite, transform);
        Assert.True(Matrix3x2.Invert(transform, out Matrix3x2 inv));
        int checkedPixels = 0, unpainted = 0;
        double worst = 0;
        for (int y = 0; y < 200; y += 9)
        {
            float[] row = Row(brush, 0, y, 260);
            for (int i = 0; i < 260; i += 3)
            {
                double px = i + 0.5, py = y + 0.5;
                var p = new Vector2((float)((px * inv.M11) + (py * inv.M21) + inv.M31), (float)((px * inv.M12) + (py * inv.M22) + inv.M32));
                double? t = ReferenceT(p, new Vector2(fx, fy), fr, new Vector2(cx, cy), r);
                float alpha = row[(i * 4) + 3];
                if (t is null)
                {
                    Assert.Equal(0f, alpha);
                    unpainted++;
                    continue;
                }

                // Pixels right on the edge of the cone or the disc may fall either way between double and float arithmetic.
                if (alpha == 0f)
                {
                    continue;
                }

                worst = Math.Max(worst, Math.Abs((row[i * 4] * 255) - Level(t.Value)));
                checkedPixels++;
            }
        }

        Assert.True(checkedPixels > 100);
        Assert.True(worst <= 1.5, $"worst error {worst} levels over {checkedPixels} pixels");
        if (fx == 170f)
        {
            Assert.True(unpainted > 50, "a cone leaves most of the plane unpainted");
        }
    }

    [Fact]
    public void EqualConcentricCirclesPaintOnlyTheirDisc()
    {
        Brush brush = Brush.RadialGradient(new Vector2(50, 50), 20, new Vector2(50, 50), 20, [new(0f, new Rgba32(255, 0, 0, 255)), new(1f, new Rgba32(0, 0, 255, 255))]);
        float[] row = Row(brush, 0, 50, 100);
        Assert.Equal(1f, row[(50 * 4) + 3]);
        Assert.Equal(1f, row[(50 * 4)]);
        Assert.Equal(0f, row[(80 * 4) + 3]);
    }

    [Fact]
    public void RepetitionAppliesToFocalGradients()
    {
        Brush repeat = Brush.RadialGradient(new Vector2(0, 0), 10, new Vector2(0, 0), 0, BlackWhite, null, GradientRepetition.Repeat);
        float[] row = Row(repeat, 0, 0, 40);
        // Distance 15.5 from the center (pixel 15 at y=0.5 is about 15.5) is position 1.55, which wraps to 0.55.
        Assert.InRange(Math.Abs(row[15 * 4] - 0.55f), 0, 0.01f);
        Brush dontFill = Brush.RadialGradient(new Vector2(0, 0), 10, new Vector2(0, 0), 0, BlackWhite, null, GradientRepetition.DontFill);
        Assert.Equal(0f, Row(dontFill, 0, 0, 40)[15 * 4 + 3]);
    }

    [Fact]
    public void BadArgumentsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => Brush.LinearGradient(new Vector2(0, 0), new Vector2(1, 0), BlackWhite, new Matrix3x2(0, 0, 0, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => Brush.LinearGradient(new Vector2(0, 0), new Vector2(0, 0), BlackWhite, Matrix3x2.Identity));
        Assert.Throws<ArgumentException>(() => Brush.RadialGradient(new Vector2(0, 0), 5, new Vector2(0, 0), 0, BlackWhite, new Matrix3x2(float.NaN, 0, 0, 1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.RadialGradient(new Vector2(0, 0), 0, new Vector2(0, 0), 0, BlackWhite));
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.RadialGradient(new Vector2(0, 0), 5, new Vector2(0, 0), -1, BlackWhite));
        Assert.Throws<ArgumentException>(() => Brush.RadialGradient(new Vector2(0, 0), 5, new Vector2(0, 0), 0, []));
    }

    private static Image Ramp(int w, int h)
    {
        var px = new Rgba32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[(y * w) + x] = new Rgba32((byte)(x * 16), (byte)(y * 16), 7, 255);
            }
        }

        return Image.FromPixels<Rgba32>(px, w, h);
    }

    [Fact]
    public void BilinearSamplingInterpolatesBetweenSourcePixelCenters()
    {
        Image ramp = Ramp(12, 12);
        // Moves the source by (0.25, 0.75): a pixel center at px sits at source u = px + 0.5 - 0.25, between the centers at u - 0.5.
        Brush brush = Brush.FromImage(ramp, ImageSampling.Bilinear, transform: Matrix3x2.CreateTranslation(0.25f, 0.75f));
        double worst = 0;
        for (int y = 2; y < 9; y++)
        {
            float[] row = Row(brush, 2, y, 7);
            for (int i = 0; i < 7; i++)
            {
                double px = 2 + i;
                double expectedR = 16 * (px - 0.25);
                double expectedG = 16 * (y - 0.75);
                worst = Math.Max(worst, Math.Abs((row[i * 4] * 255) - expectedR));
                worst = Math.Max(worst, Math.Abs((row[(i * 4) + 1] * 255) - expectedG));
            }
        }

        Assert.True(worst <= 1, $"worst error {worst} levels");
    }

    [Fact]
    public void BilinearAtWholePixelOffsetsEqualsNearest()
    {
        Image ramp = Ramp(12, 9);
        Brush smooth = Brush.FromImage(ramp, ImageSampling.Bilinear, transform: Matrix3x2.CreateTranslation(3, 2));
        Brush plain = Brush.FromImage(ramp, transform: Matrix3x2.CreateTranslation(3, 2));
        Brush nearest = Brush.FromImage(ramp, ImageSampling.Nearest, transform: Matrix3x2.CreateTranslation(3, 2));
        for (int y = -3; y < 20; y += 4)
        {
            float[] a = Row(smooth, -5, y, 40), b = Row(plain, -5, y, 40), c = Row(nearest, -5, y, 40);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.InRange(Math.Abs(a[i] - b[i]) * 255, 0, 0.5);
                Assert.Equal(b[i], c[i]);
            }
        }
    }

    [Fact]
    public void BilinearTilesWrapWithoutASeamAndDoNotBleedTransparentColor()
    {
        // Two columns: transparent red, then opaque blue. Halfway between them the color is blue at half alpha, not purple.
        var px = new Rgba32[] { new(255, 0, 0, 0), new(0, 0, 255, 255) };
        Image tile = Image.FromPixels<Rgba32>(px, 2, 1);
        Brush brush = Brush.FromImage(tile, ImageSampling.Bilinear, transform: Matrix3x2.CreateTranslation(0.5f, 0));
        float[] row = Row(brush, 0, 0, 8);

        // Pixel 1 has its center at source u = 1.0 (source centers are at 0.5 and 1.5): half transparent red, half blue.
        Assert.InRange(row[(1 * 4) + 3], 0.49f, 0.51f);
        Assert.InRange(row[(1 * 4) + 2], 0.99f, 1f);
        Assert.InRange(row[(1 * 4)], 0f, 0.01f);

        // The pattern repeats every 2 pixels with no seam: pixel i and i + 2 are the same.
        for (int i = 0; i < 6; i++)
        {
            for (int k = 0; k < 4; k++)
            {
                Assert.Equal(row[(i * 4) + k], row[((i + 2) * 4) + k]);
            }
        }

        // Without tiling the edge fades out instead of wrapping.
        Brush once = Brush.FromImage(tile, ImageSampling.Bilinear, transform: Matrix3x2.CreateTranslation(0.5f, 0), tile: false);
        float[] r1 = Row(once, 0, 0, 8);
        Assert.True(r1[(7 * 4) + 3] == 0f);
    }

    [Fact]
    public void BilinearDescribesItselfAndRefusesUndefinedModes()
    {
        Image ramp = Ramp(4, 4);
        Assert.Contains(Brush.FromImage(ramp, ImageSampling.Bilinear).Describe(), p => p.Name == "sampling");
        Assert.DoesNotContain(Brush.FromImage(ramp).Describe(), p => p.Name == "sampling");
        Assert.Throws<ArgumentOutOfRangeException>(() => Brush.FromImage(ramp, (ImageSampling)7));
    }
}
