using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using static OneImage.Text.Tests.BidiReferenceTests;

namespace OneImage.Text.Tests;

/// <summary>Wrapping, measuring and alignment of right-to-left and mixed paragraphs against SixLabors.Fonts 2.1.3 (public API only).</summary>
public class BidiWrapTests
{
    private const string ArabicParagraph = "\u0645\u0631\u062D\u0628\u0627 \u0628\u0643\u0645 \u0641\u064A \u0647\u0630\u0627 \u0627\u0644\u0645\u0648\u0642\u0639 \u0627\u0644\u062C\u062F\u064A\u062F \u0627\u0644\u0630\u064A \u064A\u0642\u062F\u0645 \u0644\u0643\u0645 \u0623\u0641\u0636\u0644 \u0627\u0644\u062E\u062F\u0645\u0627\u062A \u0648\u0627\u0644\u0645\u0639\u0644\u0648\u0645\u0627\u062A";
    private const string HebrewParagraph = "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD \u05D6\u05D4\u05D5 \u05DE\u05E9\u05E4\u05D8 \u05D0\u05E8\u05D5\u05DA \u05E9\u05DE\u05EA\u05D7\u05DC\u05E7 \u05DC\u05DB\u05DE\u05D4 \u05E9\u05D5\u05E8\u05D5\u05EA \u05D1\u05E2\u05E8\u05DA";
    private const string MixedParagraph = "\u0627\u0644\u0633\u0639\u0631 is 123 USD \u0648\u0627\u0644\u0643\u0645\u064A\u0629 (45 pcs) \u0645\u0646 the Shop \u0641\u064A 2024, see www.example.com \u0644\u0644\u0645\u0632\u064A\u062F \u0645\u0646 \u0627\u0644\u0645\u0639\u0644\u0648\u0645\u0627\u062A";
    private const string LatinWithArabic = "The quick brown fox \u0627\u0644\u062B\u0639\u0644\u0628 \u0627\u0644\u0628\u0646\u064A jumps over \u0627\u0644\u0643\u0644\u0628 the lazy dog (\u05DB\u05DC\u05D1) again and again and again";

    public static TheoryData<string, string, float> Wrapped
    {
        get
        {
            var d = new TheoryData<string, string, float>();
            foreach (float w in new[] { 150f, 240f, 330f, 480f })
            {
                d.Add("arabic", ArabicParagraph, w);
                d.Add("hebrew", HebrewParagraph, w);
                d.Add("mixed", MixedParagraph, w);
                d.Add("latin with arabic", LatinWithArabic, w);
            }

            return d;
        }
    }

    [Theory]
    [MemberData(nameof(Wrapped))]
    public void WrappedLinesHaveTheSameGlyphsInTheSameOrder(string name, string text, float wrap)
    {
        // Glyph order per line is part of the comparison (the line number is in each id), positions to 0.5 px: the reference
        // widens the space after a Hebrew letter a little (see KerningDoesNotCrossDirectionRuns). In a right-to-left paragraph
        // that mixes in Latin words and numbers the reference left-aligns some lines that we align with the paragraph (see
        // RightToLeftParagraphsAlignEveryLineAtTheirStart), so the narrow settings of that paragraph are compared by glyphs only.
        bool glyphsOnly = name == "mixed" && wrap < 300;
        var (n, _, lines) = Compare($"wrap {wrap} {name}", text, 28, TextDirection.Auto, wrap, tolerance: 0.5, idsOnly: glyphsOnly);
        Assert.True(n > 0 && lines >= 1);
    }

    [Theory]
    [MemberData(nameof(Wrapped))]
    public void WrappedLineCountAndWidthMatchTheReference(string name, string text, float wrap)
    {
        var (ours, _, _) = BidiHarness.Run(text, 28, o => o.WrappingLength = wrap);
        var fc = new SixLabors.Fonts.FontCollection();
        fc.Add(TestFonts.PathOf(BidiHarness.Merged));
        SixLabors.Fonts.Font font = fc.Get("Noto Sans").CreateFont(28);
        var ro = new RichTextOptions(font) { WrappingLength = wrap };
        int theirLines = SixLabors.Fonts.TextMeasurer.CountLines(text, ro);
        FontRectangle theirAdvance = SixLabors.Fonts.TextMeasurer.MeasureAdvance(text, ro);

        OneImage.Text.TextMetrics ourMetrics = Measure(text, 28, wrap);
        TestReport.Line($"BIDI measure {name} wrap {wrap}: lines {ourMetrics.Lines.Count} vs {theirLines}, width {ourMetrics.Advance.Width:0.###} vs {theirAdvance.Width:0.###}");
        Assert.Equal(theirLines, ourMetrics.Lines.Count);
        Assert.True(Math.Abs(ourMetrics.Advance.Width - theirAdvance.Width) < 0.5, $"width {ourMetrics.Advance.Width} vs {theirAdvance.Width}");
        Assert.True(ours.Count > 0);
    }

    private static OneImage.Text.TextMetrics Measure(string text, float size, float wrap, OneImage.Text.TextAlignment align = OneImage.Text.TextAlignment.Start)
    {
        var fc = new OneImage.Text.FontCollection();
        fc.Add(TestFonts.PathOf(BidiHarness.Merged));
        var o = new OneImage.Text.TextOptions(fc.Get("Noto Sans").CreateFont(size)) { WrappingLength = wrap, TextAlignment = align };
        return OneImage.Text.TextMeasurer.Measure(text, o);
    }

    // Start and End follow the paragraph's direction. Compared with the reference through the glyph positions of a wrapped paragraph.
    public static TheoryData<string, string, TextAlignment, HorizontalAlignment> Alignments
    {
        get
        {
            var d = new TheoryData<string, string, TextAlignment, HorizontalAlignment>();
            foreach (TextAlignment a in new[] { TextAlignment.Start, TextAlignment.Center, TextAlignment.End })
            {
                foreach (HorizontalAlignment h in new[] { HorizontalAlignment.Left, HorizontalAlignment.Center, HorizontalAlignment.Right })
                {
                    d.Add("arabic", ArabicParagraph, a, h);
                    d.Add("latin with arabic", LatinWithArabic, a, h);
                }
            }

            return d;
        }
    }

    [Theory]
    [MemberData(nameof(Alignments))]
    public void AlignmentFollowsTheParagraphDirection(string name, string text, TextAlignment align, HorizontalAlignment h) =>
        Compare($"align {align}/{h} {name}", text, 28, TextDirection.Auto, 300, align, h, tolerance: 0.5);

    [Theory]
    [InlineData(150f)]
    [InlineData(240f)]
    [InlineData(480f)]
    public void RightToLeftParagraphsAlignEveryLineAtTheirStart(float wrap)
    {
        // Every line of a right-to-left paragraph ends at the same right edge: the box is as wide as the widest line.
        var (_, _, layout) = BidiHarness.Run(MixedParagraph, 28, o =>
        {
            o.WrappingLength = wrap;
            o.Origin = new System.Numerics.Vector2(10, 0);
        });
        float right = layout.Lines.Max(l => l.Left + l.Hang + l.Width);
        Assert.All(layout.Lines, l => Assert.Equal(right, l.Left + l.Hang + l.Width, 0.01f));
        Assert.Equal(10, layout.Lines.Min(l => l.Left + l.Hang), 0.01f);
    }
}
