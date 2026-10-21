namespace OneImage.Tests;

/// <summary>Real-world profiles against LittleCMS: the ICC's own sRGB v2 and v4 profiles (checked in) and, when present on the host, the system's CMYK and RGB profiles.</summary>
public class RealProfileTests
{
    public static TheoryData<string, string, int, bool> Pairs()
    {
        var d = new TheoryData<string, string, int, bool>();
        foreach (int intent in new[] { 0, 1 })
        {
            foreach (bool bpc in new[] { false, true })
            {
                d.Add("srgb2014", "p3", intent, bpc);
                d.Add("p3", "srgb2014", intent, bpc);
                d.Add("srgb4", "p3", intent, bpc);
                d.Add("p3", "srgb4", intent, bpc);
                d.Add("srgb4", "srgb2014", intent, bpc);
                d.Add("srgb2014", "srgb4", intent, bpc);
                d.Add("srgb4", "wide", intent, bpc);
                d.Add("cmyk", "srgb4", intent, bpc);
                d.Add("cmyk", "srgb2014", intent, bpc);
                d.Add("srgb", "gray", intent, bpc);
                d.Add("gray", "srgb", intent, bpc);
                d.Add("gray", "cmyk", intent, bpc);
            }
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void MatchesLcms(string src, string dst, int intent, bool bpc)
    {
        var i = (RenderingIntent)intent;
        var r = ColorAccuracyTests.Compare(ColorAccuracyTests.Named(src), ColorAccuracyTests.Named(dst), i, bpc);
        ColorAccuracyTests.Report($"{src}->{dst} {i} bpc={bpc}", r);
        bool cmykSource = ColorAccuracyTests.Named(src).Model == ColorModel.Cmyk;
        if (cmykSource && i == RenderingIntent.Perceptual)
        {
            // LittleCMS settles on a slightly different black point for CMYK sources in the perceptual intent.
            Assert.True(r.Max <= 2.0 && r.Mean <= 0.5, $"max dE00 {r.Max}, mean {r.Mean}, max LSB {r.MaxLsb}");
        }
        else
        {
            Assert.True(r.Max <= 1.1 || r.MaxLsb <= 3, $"max dE00 {r.Max}, mean {r.Mean}, max LSB {r.MaxLsb}");
        }
    }

    public static TheoryData<string, string, int, bool> SystemPairs()
    {
        var d = new TheoryData<string, string, int, bool>();
        foreach (string name in new[] { "FOGRA27L_coated", "SWOP_TR005_coated_5", "FOGRA29L_uncoated" })
        {
            foreach (int intent in new[] { 0, 1 })
            {
                foreach (bool bpc in new[] { false, true })
                {
                    d.Add("/usr/share/color/icc/colord/" + name + ".icc", "srgb", intent, bpc);
                    d.Add("srgb", "/usr/share/color/icc/colord/" + name + ".icc", intent, bpc);
                }
            }
        }

        foreach (string name in new[] { "BruceRGB", "AppleRGB", "PAL-RGB" })
        {
            d.Add("/usr/share/color/icc/colord/" + name + ".icc", "srgb", 1, true);
            d.Add("srgb", "/usr/share/color/icc/colord/" + name + ".icc", 0, false);
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(SystemPairs))]
    public void SystemProfilesMatchLcms(string src, string dst, int intent, bool bpc)
    {
        if ((src.StartsWith('/') && !File.Exists(src)) || (dst.StartsWith('/') && !File.Exists(dst)))
        {
            return; // These profiles are not redistributable; the check runs only where the host has them.
        }

        var i = (RenderingIntent)intent;
        var r = ColorAccuracyTests.Compare(ColorAccuracyTests.Named(src), ColorAccuracyTests.Named(dst), i, bpc);
        ColorAccuracyTests.Report($"{Path.GetFileName(src)}->{Path.GetFileName(dst)} {i} bpc={bpc}", r);

        // Real CMYK profiles differ from LittleCMS by up to a few dE00 at the extremes (separation tables are interpolated
        // differently and the perceptual black point differs); the means are what matter.
        bool perceptual = i == RenderingIntent.Perceptual;
        Assert.True(r.Mean <= (perceptual ? 2.0 : 0.7), $"max dE00 {r.Max}, mean {r.Mean}, max LSB {r.MaxLsb}");
    }
}
