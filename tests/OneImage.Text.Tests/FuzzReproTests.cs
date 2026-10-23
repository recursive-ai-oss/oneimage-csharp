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
        // Shaping 1200 glyph ids through every feature of this font took about 3.5 s in the first fuzz run (budget: 1024 subtable
        // attempts per glyph, each costly here). The cost is bounded and linear; the test pins the bound loosely.
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
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
        Assert.True(output.Count <= (8 * glyphs.Length) + 256);
    }
}
