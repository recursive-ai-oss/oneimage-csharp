using OneImage.Text.Layout;

namespace OneImage.Text.Tests;

/// <summary>Wrapping and justification on shaped glyphs: ligatures, combining marks and the soft break characters.</summary>
public class ShapedWrappingTests
{
    // Words with fi, ffl, ffi and fl ligatures, and letters with combining marks (base + U+0301, U+0308).
    private const string Prose =
        "The office staff shuffled affluent files and baffled the chef while a fluffy waffle flew; "
        + "cafe\u0301 o\u0308ffice\u0301 fi\u0301le re\u0301flechi ba\u0308ffle stuff.";

    private static TextOptions Options(float size = 18, float wrap = 0, WordBreaking breaking = WordBreaking.Standard, TextJustification justify = TextJustification.None) =>
        new(TestFonts.ShapingFonts.Get("Noto Sans").CreateFont(size)) { WrappingLength = wrap, WordBreaking = breaking, TextJustification = justify };

    private static float Advance(string text, TextOptions o)
    {
        TextOptions plain = o.Clone();
        plain.WrappingLength = 0;
        plain.TextJustification = TextJustification.None;
        return TextMeasurer.MeasureAdvance(text, plain).Width;
    }

    [Fact]
    public void ProseReallyContainsLigatures()
    {
        TextLayout layout = TextLayout.Create(Prose, Options());
        Assert.True(layout.Glyphs.Count < Prose.Length - 8, $"{layout.Glyphs.Count} glyphs for {Prose.Length} characters");
    }

    [Theory]
    [InlineData(WordBreaking.Standard)]
    [InlineData(WordBreaking.BreakAll)]
    [InlineData(WordBreaking.BreakWord)]
    [InlineData(WordBreaking.KeepAll)]
    public void WrappedShapedTextNeverSplitsALigatureOrACluster(WordBreaking mode)
    {
        TextLayout flat = TextLayout.Create(Prose, Options());
        var glyphStarts = new HashSet<int>(flat.Glyphs.Select(g => g.Cluster)) { 0, Prose.Length };
        var clusters = new byte[Prose.Length + 1];
        OneFont.TextSegmentation.FillGraphemeStarts(Prose, clusters);

        int cases = 0;
        for (float wrap = 40; wrap <= 400; wrap += 7)
        {
            TextOptions o = Options(wrap: wrap, breaking: mode);
            TextMetrics m = TextMeasurer.Measure(Prose, o);
            Assert.Equal(Prose.Length, m.Lines.Sum(l => l.Length));
            int expectedStart = 0;
            foreach (LineMetrics l in m.Lines)
            {
                Assert.Equal(expectedStart, l.Start);
                Assert.True(glyphStarts.Contains(l.Start), $"wrap {wrap}: line starts inside a ligature at {l.Start}");
                Assert.True(l.Start == 0 || clusters[l.Start] != 0, $"wrap {wrap}: line starts inside a cluster at {l.Start}");
                expectedStart += l.Length;
                cases++;
            }

            // Lines are narrower than the length unless they hold one unbreakable word (or, in BreakAll, one cluster).
            foreach (LineMetrics l in m.Lines)
            {
                string text = Prose.Substring(l.Start, l.Length).TrimEnd();
                if (l.Advance > wrap + 0.01f)
                {
                    Assert.True(!text.Contains(' ') || mode == WordBreaking.KeepAll, $"wrap {wrap}: \"{text}\" is {l.Advance} px");
                }
            }

            TextLayout wrapped = TextLayout.Create(Prose, o);
            Assert.True(wrapped.Lines.Count >= 1);
        }

        Assert.True(cases > 100);
    }

    [Fact]
    public void ALineMeasuresAsTheShapedAdvanceOfItsOwnText()
    {
        // Shaping happens before the lines are cut, yet no ligature spans a cut, so each line is as wide as its text shaped alone.
        foreach (float wrap in new[] { 90f, 130f, 170f, 230f })
        {
            TextOptions o = Options(wrap: wrap);
            foreach (LineMetrics l in TextMeasurer.Measure(Prose, o).Lines)
            {
                string text = Prose.Substring(l.Start, l.Length).TrimEnd();
                Assert.Equal(Advance(text, o), l.Advance, 0.5);
            }
        }
    }

    [Fact]
    public void LigaturesFitTheLineByTheirOwnAdvance()
    {
        // A line holding ligatures is measured with the shaped glyphs: it fits at exactly its advance and not a little less.
        foreach (string pair in new[] { "fi fi", "ffl ffl", "fl ff", "office staff" })
        {
            Assert.True(TextLayout.Create(pair, Options()).Glyphs.Count < pair.Length, pair);
            float shaped = Advance(pair, Options());
            Assert.Single(TextMeasurer.Measure(pair, Options(wrap: shaped + 0.01f)).Lines);
            Assert.Equal(2, TextMeasurer.Measure(pair, Options(wrap: shaped - 0.5f)).Lines.Count);
        }
    }

    [Fact]
    public void ZeroWidthSpaceAndSoftHyphenStopLigaturesAndStayBreakOpportunities()
    {
        TextOptions o = Options();
        Assert.Single(TextLayout.Create("fi", o).Glyphs);
        Assert.Equal(2, TextLayout.Create("f\u200Bi", o).Glyphs.Count);
        Assert.Equal(2, TextLayout.Create("f\u00ADi", o).Glyphs.Count);

        // Wide enough for "f" but not "fi": the line can end at the zero width space.
        float f = Advance("f", o);
        TextOptions narrow = Options(wrap: f + 1);
        string[] lines = TextMeasurer.Measure("f\u200Bi", narrow).Lines.Select(l => "f\u200Bi".Substring(l.Start, l.Length)).ToArray();
        Assert.Equal(["f\u200B", "i"], lines);
    }

    [Fact]
    public void MarksAndLigaturesGiveOneBoxPerCluster()
    {
        const string text = "fi\u0301 ffl a\u0308";
        IReadOnlyList<GlyphBounds> boxes = TextMeasurer.GetCharacterBounds(text, Options(30));
        // "f", "i" + acute (a mark ends the ligature chance and stays in its cluster), a space, "ffl", a space, "a" + diaeresis.
        Assert.Equal([0, 1, 3, 4, 7, 8], boxes.Select(b => b.Index).ToArray());
        float total = TextMeasurer.MeasureAdvance(text, Options(30)).Width;
        Assert.Equal(total, boxes[^1].Bounds.Right, 0.01);
    }

    [Theory]
    [InlineData(TextJustification.InterWord)]
    [InlineData(TextJustification.InterCharacter)]
    public void JustifiedShapedLinesFillTheWrappingLength(TextJustification mode)
    {
        foreach (float wrap in new[] { 150f, 200f, 260f, 333f })
        {
            TextOptions o = Options(wrap: wrap, justify: mode);
            TextMetrics m = TextMeasurer.Measure(Prose, o);
            Assert.True(m.Lines.Count > 2);
            foreach (LineMetrics l in m.Lines)
            {
                string text = Prose.Substring(l.Start, l.Length).TrimEnd();
                // A line holding one unbreakable word is too long or has no spaces to stretch.
                float natural = Advance(text, o);
                if (natural >= wrap || (mode == TextJustification.InterWord && !text.Contains(' ')))
                {
                    continue;
                }

                Assert.Equal(wrap, l.Advance, 0.02);
            }

            // The stretched line is placed from the shaped advances: the last box ends where the line ends.
            IReadOnlyList<GlyphBounds> boxes = TextMeasurer.GetCharacterBounds(Prose, o);
            foreach (IGrouping<int, GlyphBounds> line in boxes.GroupBy(b => b.Line))
            {
                GlyphBounds last = line.Last(b => b.CodePoint != ' ');
                Assert.True(last.Bounds.Right <= m.Lines[line.Key].Left + wrap + 0.05f, $"line {line.Key} ends at {last.Bounds.Right}");
            }
        }
    }

    [Fact]
    public void JustificationKeepsMarksOnTheirBase()
    {
        // The accent sits on its letter at every stretch: it is not separated from the base by the added space.
        const string text = "ee\u0301 e\u0301e e\u0301 ee";
        TextOptions o = Options(24, wrap: 300, justify: TextJustification.InterCharacter);
        IReadOnlyList<GlyphBounds> boxes = TextMeasurer.GetCharacterBounds(text, o);
        TextOptions none = Options(24, wrap: 300);
        IReadOnlyList<GlyphBounds> base0 = TextMeasurer.GetCharacterBounds(text, none);
        for (int i = 0; i < boxes.Count; i++)
        {
            // Within a cluster the ink keeps its place relative to the letter: the box width is unchanged except for the added gap.
            Assert.Equal(base0[i].Ink.Width, boxes[i].Ink.Width, 0.01);
            Assert.Equal(base0[i].Ink.Height, boxes[i].Ink.Height, 0.01);
        }
    }
}
