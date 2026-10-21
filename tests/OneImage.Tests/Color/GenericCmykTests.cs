using System.Globalization;

namespace OneImage.Tests;

public class GenericCmykTests
{
    [Fact]
    public void ProfileIsNamedFromAssemblyAndDoesNotClaimEndorsement()
    {
        ColorProfile p = ColorProfile.GenericCmyk;
        Assert.StartsWith(Library.Name + " Generic CMYK", p.Description);
        Assert.Contains("derived from FOGRA39 characterization data", p.Description);
        string all = System.Text.Encoding.Latin1.GetString(p.IccBytes.Span);
        Assert.Contains("no certification, approval or endorsement by Fogra", all);
        Assert.Equal(ColorModel.Cmyk, p.Model);
    }

    [Fact]
    public void GenerationIsByteIdentical()
    {
        byte[] a = GenericCmykBuilder.Build().IccBytes.ToArray();
        byte[] b = GenericCmykBuilder.Build().IccBytes.ToArray();
        Assert.True(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void ParsesInLittleCms()
    {
        var m = new ImageMagick.ColorProfile(ColorProfile.GenericCmyk.IccBytes.ToArray());
        Assert.Equal(ImageMagick.ColorSpace.CMYK, m.ColorSpace);
    }

    [Fact]
    public void A2BReproducesMeasurements()
    {
        var rows = new List<(float[] Ink, double[] Lab)>();
        bool data = false;
        var xyz = new List<double[]>();
        var inks = new List<float[]>();
        foreach (string raw in File.ReadLines(ColorTestUtil.TestDataPath("FOGRA39.txt")))
        {
            string l = raw.Trim();
            if (l == "BEGIN_DATA") { data = true; continue; }
            if (l == "END_DATA") { break; }
            if (!data || l.Length == 0) { continue; }
            string[] f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            inks.Add([float.Parse(f[1], CultureInfo.InvariantCulture) / 100, float.Parse(f[2], CultureInfo.InvariantCulture) / 100, float.Parse(f[3], CultureInfo.InvariantCulture) / 100, float.Parse(f[4], CultureInfo.InvariantCulture) / 100]);
            xyz.Add([double.Parse(f[5], CultureInfo.InvariantCulture), double.Parse(f[6], CultureInfo.InvariantCulture), double.Parse(f[7], CultureInfo.InvariantCulture)]);
        }

        double[] paper = xyz[0];
        Assert.True(inks[0].All(v => v == 0));
        ColorTransform t = ColorTransform.Get(ColorProfile.GenericCmyk, ColorProfile.LabD50, RenderingIntent.RelativeColorimetric, false);
        double sum = 0, max = 0;
        for (int i = 0; i < inks.Count; i++)
        {
            double x = xyz[i][0] / paper[0] * Pcs.D50X, y = xyz[i][1] / paper[1], z = xyz[i][2] / paper[2] * Pcs.D50Z;
            double fx = F(x / Pcs.D50X), fy = F(y), fz = F(z / Pcs.D50Z);
            double[] lab = [(116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz)];
            var o = new float[3];
            t.ConvertExact(inks[i], o, 1);
            double e = ColorTestUtil.DeltaE2000(lab[0], lab[1], lab[2], o[0], o[1], o[2]);
            sum += e;
            max = Math.Max(max, e);
        }

        string msg = $"FOGRA39 patches: {inks.Count}, mean dE00 {sum / inks.Count:F3}, max {max:F3}";
        ColorAccuracyTests.Report(msg, (max, sum / inks.Count, 0));
        Assert.True(sum / inks.Count <= 1.0, msg);
        Assert.True(max <= 2.0, msg);
    }

    private static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (((24389.0 / 27 * t) + 16) / 116);

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(0, false)]
    public void CmykToSrgbMatchesLcms(int intent, bool bpc)
    {
        var r = ColorAccuracyTests.Compare(ColorProfile.GenericCmyk, ColorProfile.Srgb, (RenderingIntent)intent, bpc);
        ColorAccuracyTests.Report($"cmyk->srgb {(RenderingIntent)intent} bpc={bpc}", r);

        // Relative colorimetric agrees to dE00 1.0. LittleCMS finds a slightly different black point for the perceptual intent
        // (mean dE00 about 0.4, worst case below 2), so that intent gets a looser bound.
        if (intent == 1)
        {
            Assert.True(r.Max <= 1.1 || r.MaxLsb <= 2, $"max dE00 {r.Max}, max LSB {r.MaxLsb}");
        }
        else
        {
            Assert.True(r.Max <= 2.0 && r.Mean <= 0.5, $"max dE00 {r.Max}, mean {r.Mean}");
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void SrgbToCmykMatchesLcms(int intent, bool bpc)
    {
        var r = ColorAccuracyTests.Compare(ColorProfile.Srgb, ColorProfile.GenericCmyk, (RenderingIntent)intent, bpc);
        ColorAccuracyTests.Report($"srgb->cmyk {(RenderingIntent)intent} bpc={bpc}", r);
        Assert.True(r.Mean <= 0.5, $"mean dE00 {r.Mean}");
    }
}
