using System.Diagnostics;
using OneImage.Text.Fonts;
using OneImage.Text.Shaping;

namespace OneImage.Text.Tests;

/// <summary>Inputs found by fuzzing that are not crashes: they only show how much work a hostile font may cost.</summary>
public class FuzzReproTests
{
    private static readonly string[] Tags = ["smcp", "onum", "salt", "aalt", "calt", "ccmp", "locl", "kern", "mark", "mkmk", "curs"];

    [Fact]
    public void AMutatedFontWithCostlyLookupsStaysWithinTheShapingBudget()
    {
        // This font has lookups made of tens of thousands of subtables the engine cannot use. Skipping them used to be free, so
        // shaping 1200 glyph ids through every feature took 3.5 s (13 s unoptimized); skipped subtables now count against the
        // work budget and the run takes milliseconds. The bound is generous on purpose; it only has to tell milliseconds from seconds.
        FontFace face = FontFile.Parse(File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", "font-slow-shaping.ttf")))[0];
        int n = Math.Min(face.NumGlyphs, 600);
        int[] glyphs = new int[n * 2];
        for (int i = 0; i < glyphs.Length; i++)
        {
            glyphs[i] = i < n ? i : (i * 37) % n;
        }

        var features = FeatureSet.Create(Array.ConvertAll(Tags, t => (Be.Tag(t), 1)));
        var sw = Stopwatch.StartNew();
        var output = new List<ShapedRunGlyph>();
        TextShaper.ShapeGlyphs(face, glyphs, 0, features, output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
        Assert.True(output.Count <= (8 * glyphs.Length) + 256);
    }
}
