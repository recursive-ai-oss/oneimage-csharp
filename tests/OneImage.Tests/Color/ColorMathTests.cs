namespace OneImage.Tests;

/// <summary>Checks matrix/TRC conversions between D65 spaces against straightforward double-precision math written from the primaries.</summary>
public class ColorMathTests
{
    private static double[] Matrix(double[] xy)
    {
        double[] w = [xy[6] / xy[7], 1, (1 - xy[6] - xy[7]) / xy[7]];
        double[,] p = { { xy[0] / xy[1], xy[2] / xy[3], xy[4] / xy[5] }, { 1, 1, 1 }, { (1 - xy[0] - xy[1]) / xy[1], (1 - xy[2] - xy[3]) / xy[3], (1 - xy[4] - xy[5]) / xy[5] } };
        double[] inv = IccData.Invert3([p[0, 0], p[0, 1], p[0, 2], p[1, 0], p[1, 1], p[1, 2], p[2, 0], p[2, 1], p[2, 2]]);
        double[] s = [(inv[0] * w[0]) + (inv[1] * w[1]) + (inv[2] * w[2]), (inv[3] * w[0]) + (inv[4] * w[1]) + (inv[5] * w[2]), (inv[6] * w[0]) + (inv[7] * w[1]) + (inv[8] * w[2])];
        var m = new double[9];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                m[(r * 3) + c] = p[r, c] * s[c];
            }
        }

        return m;
    }

    private static double SrgbDecode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double SrgbEncode(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;

    [Fact]
    public void SrgbToDisplayP3MatchesDoubleMath()
    {
        double[] ms = Matrix(Chromaticities.Srgb);
        double[] mpi = IccData.Invert3(Matrix(Chromaticities.DisplayP3));
        var rnd = new Random(5);
        int n = 20000;
        byte[] src = new byte[n * 3];
        rnd.NextBytes(src);
        byte[] dst = new byte[n * 3];
        ColorTransform.Get(ColorProfile.Srgb, ColorProfile.DisplayP3, RenderingIntent.RelativeColorimetric, false).ConvertRow(src, PixelFormat.Rgb8, dst, PixelFormat.Rgb8, n);
        int worst = 0;
        for (int i = 0; i < n; i++)
        {
            double[] lin = [SrgbDecode(src[i * 3] / 255.0), SrgbDecode(src[(i * 3) + 1] / 255.0), SrgbDecode(src[(i * 3) + 2] / 255.0)];
            double[] xyz = new double[3];
            for (int r = 0; r < 3; r++)
            {
                xyz[r] = (ms[r * 3] * lin[0]) + (ms[(r * 3) + 1] * lin[1]) + (ms[(r * 3) + 2] * lin[2]);
            }

            for (int r = 0; r < 3; r++)
            {
                double v = (mpi[r * 3] * xyz[0]) + (mpi[(r * 3) + 1] * xyz[1]) + (mpi[(r * 3) + 2] * xyz[2]);
                int expect = (int)Math.Clamp(Math.Round(SrgbEncode(Math.Clamp(v, 0, 1)) * 255), 0, 255);
                worst = Math.Max(worst, Math.Abs(expect - dst[(i * 3) + r]));
            }
        }

        Assert.True(worst <= 1, $"worst difference {worst} LSB");
    }

    [Fact]
    public void FastPathMatchesExactWithinOneLsb()
    {
        var rnd = new Random(9);
        ColorProfile[] ps = [ColorProfile.Srgb, ColorProfile.DisplayP3, ColorProfile.WideGamutRgb, ColorProfile.ProPhoto, ColorProfile.Rec2020, ColorProfile.LinearSrgb];
        foreach (ColorProfile a in ps)
        {
            foreach (ColorProfile b in ps)
            {
                int n = 4000;
                byte[] src = new byte[n * 3];
                rnd.NextBytes(src);
                byte[] fast = new byte[n * 3];
                ColorTransform t = ColorTransform.Get(a, b, RenderingIntent.RelativeColorimetric, true);
                t.ConvertRow(src, PixelFormat.Rgb8, fast, PixelFormat.Rgb8, n);
                float[] fin = src.Select(x => x / 255f).ToArray();
                float[] ex = new float[n * 3];
                t.ConvertExact(fin, ex, n);
                for (int i = 0; i < ex.Length; i++)
                {
                    int e = (int)Math.Clamp(Math.Round(ex[i] * 255), 0, 255);
                    Assert.True(Math.Abs(e - fast[i]) <= 1, $"{a}->{b}: {e} vs {fast[i]}");
                }
            }
        }
    }
}
