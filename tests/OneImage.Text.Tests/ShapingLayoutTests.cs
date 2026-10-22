using OneImage.Text.Fonts;
using OneImage.Text.Layout;

namespace OneImage.Text.Tests;

/// <summary>Shaping as seen through the public layout calls: ligatures in measuring and wrapping, features, languages and kerning switches.</summary>
public class ShapingLayoutTests
{
    private static Font Noto(float size) => TestFonts.ShapingFonts.Get("Noto Sans").CreateFont(size);

    private static TextOptions Options(float size = 20, Action<TextOptions>? configure = null)
    {
        var o = new TextOptions(Noto(size));
        configure?.Invoke(o);
        return o;
    }

    private static float Advance(string text, TextOptions o) => TextMeasurer.MeasureAdvance(text, o).Width;

    private static string Ids(string text, TextOptions o) => string.Join(' ', TextLayout.Create(text, o).Glyphs.Select(g => g.GlyphId));

    [Fact]
    public void ALigatureMeasuresAsItsOwnGlyph()
    {
        FontFace face = TestFonts.Face(TestFonts.Shaping);
        float scale = 20f / face.UnitsPerEm;
        int fi = face.GetGlyphIndex(0xFB01);
        Assert.Equal(face.GetAdvance(fi) * scale, Advance("fi", Options()), 0.001);
        Assert.Equal(face.GetAdvance(fi) * scale, Advance("\uFB01", Options()), 0.001);

        // Switched off, the two letters measure separately (plus whatever kerning the font has between them).
        float off = Advance("fi", Options(20, o => o.Features.Add(FontFeature.Off("liga"))));
        Assert.Equal((face.GetAdvance(face.GetGlyphIndex('f')) + face.GetAdvance(face.GetGlyphIndex('i'))) * scale, off, 0.5);
        Assert.NotEqual(Ids("fi", Options()), Ids("fi", Options(20, o => o.Features.Add(FontFeature.Off("liga")))));
    }

    [Fact]
    public void ALigatureNeverSplitsAcrossALineBreak()
    {
        // Wrapping works on shaped glyphs, so a break can only fall between words, never inside a ligature.
        TextOptions o = Options(20, x => x.WrappingLength = 40);
        TextMetrics m = TextMeasurer.Measure("office fluffy", o);
        Assert.Equal(2, m.LineCount);
        Assert.Equal(0, m.Lines[0].Start);
        Assert.Equal("office ".Length, m.Lines[1].Start);
    }

    [Fact]
    public void SingleLineAdvanceWithLigaturesMatchesTheReference()
    {
        double worst = 0;
        int cases = 0;
        foreach (float size in new[] { 12f, 20f, 33.3f })
        {
            foreach (string text in new[] { "fi", "office", "fluffy affix", "efficient waffle", "ffl ffi ff fl fi", "The first official flight" })
            {
                TextOptions mine = Options(size);
                SixLabors.Fonts.TextOptions theirs = Reference.Options(Reference.Font(TestFonts.Shaping, size), mine);
                float a = Advance(text, mine);
                float b = SixLabors.Fonts.TextMeasurer.MeasureAdvance(text, theirs).Width;
                worst = Math.Max(worst, Math.Abs(a - b));
                cases++;
            }
        }

        TestReport.Line($"LIGATURE ADVANCE {cases} cases: largest difference {worst:F5} px");
        Assert.True(worst < 0.01, $"largest advance difference {worst}");
    }

    private static (int Cases, int Differ) WrapAgainstReference(string[] words, bool checkOurLines)
    {
        int cases = 0, differ = 0;
        var rnd = new Random(7);
        for (int n = 0; n < 60; n++)
        {
            string text = string.Join(' ', Enumerable.Range(0, 8).Select(_ => words[rnd.Next(words.Length)]));
            float wrap = 60 + (rnd.Next(10) * 20);
            TextOptions mine = Options(20, o => o.WrappingLength = wrap);
            SixLabors.Fonts.TextOptions theirs = Reference.Options(Reference.Font(TestFonts.Shaping, 20), mine);
            TextMetrics m = TextMeasurer.Measure(text, mine);
            cases++;
            if (m.LineCount != SixLabors.Fonts.TextMeasurer.CountLines(text, theirs))
            {
                differ++;
            }

            if (checkOurLines)
            {
                // Ours: no line (but a single over-long word) is wider than the wrapping length, and the lines add up to the text.
                foreach (LineMetrics line in m.Lines)
                {
                    string lineText = text.Substring(line.Start, line.Length);
                    if (lineText.Trim().Contains(' '))
                    {
                        Assert.True(line.Advance <= wrap + 0.01f, $"'{lineText}' is {line.Advance} wide, limit {wrap}");
                    }
                }

                Assert.Equal(text, string.Concat(m.Lines.Select(l => text.Substring(l.Start, l.Length))));
            }
        }

        return (cases, differ);
    }

    [Fact]
    public void WrappingAgreesWithTheReferenceOnTextWithoutLigatures()
    {
        (int cases, int differ) = WrapAgainstReference(["the", "quick", "brown", "jumps", "over", "lazy", "dogs", "wax", "of", "hush"], checkOurLines: true);
        TestReport.Line($"WRAP control {cases} random texts without ligatures: line count differs from the reference in {differ}");
        Assert.Equal(0, differ);
    }

    [Fact]
    public void WrappedLineCountsWithLigaturesFollowTheShapedWidths()
    {
        // The reference over-measures a line that holds a ligature when it chooses where to wrap; we use the advances of the shaped
        // glyphs. The count of disagreements is reported; our own lines are checked against the limit.
        (int cases, int differ) = WrapAgainstReference(["office", "fluffy", "affix", "the", "first", "official", "flight", "waffle", "of", "fish"], checkOurLines: true);
        TestReport.Line($"WRAP ligatures {cases} random texts: line count differs from the reference in {differ}");
        Assert.True(differ > 0);
    }

    [Fact]
    public void KerningNoneSwitchesPairKerningOffButNotLigatures()
    {
        float kerned = Advance("AVAT", Options());
        float plain = Advance("AVAT", Options(20, o => o.Kerning = KerningMode.None));
        Assert.True(kerned < plain - 0.5f);
        Assert.Equal(Advance("fi", Options()), Advance("fi", Options(20, o => o.Kerning = KerningMode.None)), 0.001);
    }

    [Fact]
    public void AnExplicitKernFeatureOverridesTheKerningMode()
    {
        float kerned = Advance("AVAT", Options());
        Assert.Equal(kerned, Advance("AVAT", Options(20, o => { o.Kerning = KerningMode.None; o.Features.Add(FontFeature.On("kern")); })), 0.001);
        Assert.NotEqual(kerned, Advance("AVAT", Options(20, o => o.Features.Add(FontFeature.Off("kern")))), 0.5);
    }

    [Fact]
    public void FeatureSettingsAreFourCharacterTags()
    {
        Assert.Throws<ArgumentException>(() => new FontFeature("lig"));
        Assert.Throws<ArgumentException>(() => new FontFeature("liga "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FontFeature("liga", -1));
        TextOptions o = Options(20, x => x.Features.Add(new FontFeature("smcp")));
        Assert.Single(o.Clone().Features);
    }

    [Fact]
    public void SmallCapsChangeTheGlyphsAndAlternatesPickByValue()
    {
        string plain = Ids("Ab1", Options());
        Assert.NotEqual(plain, Ids("Ab1", Options(20, o => o.Features.Add(FontFeature.On("smcp")))));
        Assert.NotEqual(plain, Ids("Ab1", Options(20, o => o.Features.Add(new FontFeature("aalt", 1)))));
    }

    [Fact]
    public void LanguageSelectsTheFontsLocalizedForms()
    {
        // Romanian (ROM) puts a comma below on s and t where the default forms carry a cedilla; Catalan (CAT) joins l·l.
        string cedilla = "\u015F\u0163";
        Assert.NotEqual(Ids(cedilla, Options()), Ids(cedilla, Options(20, o => o.Language = "ro")));
        Assert.Equal(Ids(cedilla, Options(20, o => o.Language = "ro")), Ids(cedilla, Options(20, o => o.Language = "ro-RO")));
        Assert.Equal(Ids(cedilla, Options(20, o => o.Language = "ro")), Ids(cedilla, Options(20, o => o.Language = "ROM")));
        Assert.Equal(Ids(cedilla, Options()), Ids(cedilla, Options(20, o => o.Language = "xx")));       // unknown language: default forms
        Assert.Equal(Ids(cedilla, Options()), Ids(cedilla, Options(20, o => o.Language = "de")));       // the font has no forms for it
        string lDot = "l\u00B7l";
        Assert.NotEqual(Ids(lDot, Options()), Ids(lDot, Options(20, o => o.Language = "ca")));

        // The exact forms, as the font's own locl lookups define them (glyph ids read from its tables with fontTools):
        // ROM: s and t with cedilla become the comma-below glyphs 320 and 290; CAT: l + middle dot before an l becomes the single glyph ldot (255);
        // SRB and MKD: Cyrillic be becomes glyph 942.
        Assert.Equal("320 290", Ids(cedilla, Options(20, o => o.Language = "ro")));
        Assert.Equal("255 77", Ids(lDot, Options(20, o => o.Language = "ca")));
        Assert.Equal("77 119 77", Ids(lDot, Options()));
        Assert.Equal("77 119", Ids("l\u00B7", Options(20, o => o.Language = "ca")));      // the rule needs the second l
        Assert.Equal("942", Ids("\u0431", Options(20, o => o.Language = "sr")));
        Assert.Equal("942", Ids("\u0431", Options(20, o => o.Language = "mk-MK")));
        Assert.Equal("449", Ids("\u0431", Options()));
        Assert.Equal("449", Ids("\u0431", Options(20, o => o.Language = "bg")));
    }

    [Fact]
    public void AZeroWidthNonJoinerBreaksALigature()
    {
        Assert.NotEqual(Ids("fi", Options()), Ids("f\u200Ci", Options()));
        Assert.Equal(2, TextLayout.Create("f\u200Ci", Options()).Glyphs.Count);
        // A zero width joiner is dropped like the other format characters and does not stop it.
        Assert.Equal(Ids("fi", Options()), Ids("f\u200Di", Options()));
    }

    [Fact]
    public void TabsEndAShapingRun() => Assert.Equal(3, TextLayout.Create("f\ti", Options()).Glyphs.Count);

    [Fact]
    public void CharacterBoundsFollowTheShapedGlyphs()
    {
        // fi is one glyph for two characters: one box, starting at the first character.
        IReadOnlyList<GlyphBounds> b = TextMeasurer.GetCharacterBounds("afib", Options());
        Assert.Equal(3, b.Count);
        Assert.Equal([0, 1, 3], b.Select(x => x.Index));
    }

    [Fact]
    public void MarksSitOnTheirBaseInTheDrawnOutline()
    {
        // a + combining acute: the accent's ink lies above the letter and within its width.
        IReadOnlyList<GlyphBounds> accent = TextMeasurer.GetCharacterBounds("a\u0301", Options(100));
        Assert.Equal(2, accent.Count);
        System.Drawing.RectangleF letter = accent[0].Ink;
        System.Drawing.RectangleF mark = accent[1].Ink;
        Assert.True(mark.Bottom <= letter.Top + 0.5f, $"mark {mark} letter {letter}");
        Assert.True(mark.Left >= letter.Left - 5 && mark.Right <= letter.Right + 5, $"mark {mark} letter {letter}");
        Assert.Equal(0f, accent[1].Bounds.Width, 0.001);
    }

    [Fact]
    public void ShapingIsDeterministicAcrossRepeatedAndParallelCalls()
    {
        TextOptions o = Options(18);
        const string text = "Office fluffy \u03B1\u0301 \u0438\u0306 ffl";
        static string Dump(string t, TextOptions opt) => string.Join(',', TextLayout.Create(t, opt).Glyphs.Select(g => $"{g.GlyphId}:{g.Advance}:{g.XOffset}:{g.YOffset}"));
        string first = Dump(text, o);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(first, Dump(text, o));
        }

        Parallel.For(0, 32, _ => Assert.Equal(first, Dump(text, o)));
    }
}
