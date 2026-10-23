using OneImage.Layers;

namespace OneImage.Psd.Tests;

/// <summary>Posterize, curves and brightness/contrast layers read from Photoshop files against the composites Photoshop stored.</summary>
public class AdjustmentFidelityTests
{
    // The 'CgEd' descriptor Photoshop writes next to a zeroed 'brit' block: brightness 23, contrast 70, mean 127, Lab false, useLegacy false.
    private const string CurrentModeDescriptor =
        "00000010000000010000000000006E756C6C00000007000000005672736E6C6F6E670000000100000000427267686C6F6E670000001700000000436E74726C6F6E6700000046000000056D65616E736C6F6E670000007F000000004C616220626F6F6C00000000097573654C6567616379626F6F6C00000000004175746F626F6F6C0000";

    private static string Param(Adjustment a, string name) => a.Parameters.FirstOrDefault(p => p.Name == name).Value?.ToString() ?? "";

    [Fact]
    public void CurrentModeBrightnessContrastIsReadFromTheDescriptor()
    {
        PsdBlock[] siblings = [new PsdBlock("CgEd", Convert.FromHexString(CurrentModeDescriptor))];
        Adjustment a = AdjustmentCodec.Read("brit", new byte[8], PsdColorMode.Rgb, siblings)!;
        Assert.Equal("brightness-contrast", a.Name);
        Assert.Equal("photoshop-smooth", Param(a, "mode"));
        Assert.Equal((23 / 150.0).ToString(), Param(a, "brightness"));
        Assert.Equal(0.7.ToString(), Param(a, "contrast"));
    }

    [Fact]
    public void LegacyBlockWithoutDescriptorUsesTheLegacyFormula()
    {
        // 20, 57, mean 127, no Lab.
        Adjustment a = AdjustmentCodec.Read("brit", Convert.FromHexString("00140039007F0000"), PsdColorMode.Rgb)!;
        Assert.Equal("photoshop-legacy", Param(a, "mode"));
        Assert.Equal("0.57", Param(a, "contrast"));
    }

    [Fact]
    public void PosterizeAndCurvesUsePhotoshopRules()
    {
        Adjustment p = AdjustmentCodec.Read("post", [0, 7], PsdColorMode.Rgb)!;
        Assert.Equal("buckets", Param(p, "mode"));

        // A curve block: pad, version 1, mask for the master curve, three points as (output, input) pairs.
        byte[] curve = Convert.FromHexString("000001" + "00000001" + "0003" + "0000" + "0000" + "0080" + "0040" + "00FF" + "00FF");
        Adjustment c = AdjustmentCodec.Read("curv", curve, PsdColorMode.Rgb)!;
        Assert.Equal("natural", Param(c, "interpolation"));
    }

    [Fact]
    public void AdjustmentLayersOfTheCorpusRenderCloseToTheStoredComposite()
    {
        if (Util.CorpusDir == null)
        {
            return;
        }

        // Mean error in 8-bit levels against the stored composite, with the bound each layer type now reaches.
        (string File, double Bound)[] cases =
        [
            ("adjustments/posterize_rgb.psd", 0.05),
            ("adjustments/posterize_grayscale.psd", 0.05),
            ("adjustments/posterize_16bits_rgb.psd", 0.5),
            ("adjustments/posterize_16bits_grayscale.psd", 0.5),
            ("adjustments/curves_rgb.psd", 0.05),
            ("adjustments/curves_grayscale.psd", 0.05),
            ("adjustments/brightnesscontrast_legacy_rgb.psd", 0.05),
            ("adjustments/brightnesscontrast_legacy_grayscale.psd", 0.05),

            // The current brightness/contrast mode is an approximation fitted to these files.
            ("adjustments/brightnesscontrast_rgb.psd", 12),
            ("adjustments/brightnesscontrast_grayscale.psd", 12),
        ];
        foreach ((string file, double bound) in cases)
        {
            string path = System.IO.Path.Combine(Util.CorpusDir, file);
            if (!File.Exists(path))
            {
                continue;
            }

            using PsdFile f = PsdFile.Load(path);
            (double mean, _, _, _, _) = Util.Diff(Util.LoadComposite(path), f.Document.Render());
            Assert.True(mean <= bound, $"{file}: mean error {mean:F3} levels exceeds {bound}");
        }
    }
}
