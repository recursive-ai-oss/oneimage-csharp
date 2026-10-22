using System.Text;
using OneImage.Text.Unicode;

namespace OneImage.Text.Tests;

/// <summary>Unicode line breaking, word breaking modes, justification and grapheme clusters through the layout.</summary>
public class LineBreakingTests
{
    // A wrapping length so small that every break opportunity is taken: the lines are exactly the unbreakable segments.
    private const float EveryOpportunity = 0.01f;

    private static TextOptions Latin(float size = 20) => new(TestFonts.Noto(size));

    private static string[] Lines(string text, TextOptions o) =>
        [.. TextMeasurer.Measure(text, o).Lines.Select(l => text.Substring(l.Start, l.Length))];

    private static float Width(string text, TextOptions o)
    {
        TextOptions plain = o.Clone();
        plain.WrappingLength = 0;
        return TextMeasurer.MeasureAdvance(text, plain).Width;
    }

    private static string[] Segments(string text, TextOptions o)
    {
        o.WrappingLength = EveryOpportunity;
        return Lines(text, o);
    }

    [Fact]
    public void SpacesAndHyphensStillBreak()
    {
        Assert.Equal(["well-", "known ", "words"], Segments("well-known words", Latin()));
        Assert.Equal(["a ", "b ", "c"], Segments("a b c", Latin()));
    }

    [Fact]
    public void SoftHyphenAndZeroWidthSpaceAreOpportunitiesAndDrawNothing()
    {
        Assert.Equal(["inter\u00AD", "national"], Segments("inter\u00ADnational", Latin()));
        Assert.Equal(["abc\u200B", "def"], Segments("abc\u200Bdef", Latin()));

        // Neither takes up room on the line.
        TextOptions o = Latin();
        Assert.Equal(Width("interna", o) + Width("tional", o), Width("inter\u00ADna\u200Btional", o) + 0f, 0.5f);
    }

    [Fact]
    public void NoBreakSpaceGluesWordsTogether()
    {
        Assert.Equal(["Dr.\u00A0Joseph ", "Becker"], Segments("Dr.\u00A0Joseph Becker", Latin()));
        Assert.Equal(["word\u2060joiner ", "next"], Segments("word\u2060joiner next", Latin()));
    }

    [Fact]
    public void QuotationMarksFollowTheCurrentLineBreakingRules()
    {
        // LB15a: no break after an opening quotation mark, even through the spaces that follow it.
        Assert.Equal(["word ", "\u201C quoted ", "text"], Segments("word \u201C quoted text", Latin()));

        // LB15b: no break before a closing quotation mark that is followed by a space.
        Assert.Equal(["said ", "hello \u201D ", "and"], Segments("said hello \u201D and", Latin()));

        // Brackets: none after an opening one, none before a closing one, however many spaces are between.
        Assert.Equal(["call ", "( spaced ) ", "now"], Segments("call ( spaced ) now", Latin()));
    }

    [Fact]
    public void NumbersAndPunctuationStayTogether()
    {
        Assert.Equal(["Total ", "$1,234.56 ", "(approx.) ", "-5.25 ", "12.5% ", "1/2"], Segments("Total $1,234.56 (approx.) -5.25 12.5% 1/2", Latin()));
        Assert.Equal(["Wait... ", "what? ", "Yes! ", "No:x"], Segments("Wait... what? Yes! No:x", Latin()));
    }

    [Fact]
    public void EastAsianTextBreaksBetweenCharactersButNotBeforeClosingPunctuation()
    {
        TextOptions o = new(TestFonts.NotoCjk(20));
        Assert.Equal(["\u65E5", "\u672C", "\u8A9E\u3002", "\u4E8C", "\u4E09\u3001", "\u4E00\u3041", "\u3042", "\u300C\u65E5", "\u672C", "\u8A9E\u300D"],
            Segments("\u65E5\u672C\u8A9E\u3002\u4E8C\u4E09\u3001\u4E00\u3041\u3042\u300C\u65E5\u672C\u8A9E\u300D", o));
    }

    [Fact]
    public void KeepAllStopsBreaksInsideEastAsianText()
    {
        TextOptions o = new(TestFonts.NotoCjk(20)) { WordBreaking = WordBreaking.KeepAll };
        string text = "\u65E5\u672C\u8A9E\u306E\u65E5\u672C\u8A9E \u4E8C\u4E09\u4E00";
        Assert.Equal(["\u65E5\u672C\u8A9E\u306E\u65E5\u672C\u8A9E ", "\u4E8C\u4E09\u4E00"], Segments(text, o));

        // Latin text is unaffected.
        TextOptions latin = Latin();
        latin.WordBreaking = WordBreaking.KeepAll;
        Assert.Equal(["well-", "known ", "words"], Segments("well-known words", latin));
    }

    [Fact]
    public void BreakAllFillsEveryLineToTheLength()
    {
        TextOptions o = Latin();
        o.WordBreaking = WordBreaking.BreakAll;
        string text = "Supercalifragilistic expialidocious";
        o.WrappingLength = Width("Supercal", o) + 1;
        string[] lines = Lines(text, o);
        Assert.True(lines.Length >= 4);
        Assert.Equal(text, string.Concat(lines));
        foreach (string line in lines[..^1])
        {
            Assert.True(Width(line.TrimEnd(), o) <= o.WrappingLength + 0.01f, line);
        }

        // The line has no room for another character: adding the next one would not fit.
        Assert.True(Width(lines[0] + lines[1][0], o) > o.WrappingLength);

        // White space is never pushed to the start of a line.
        Assert.DoesNotContain(lines, l => l.Length > 0 && l[0] == ' ');
    }

    [Fact]
    public void BreakWordSplitsOnlyWordsThatCannotFit()
    {
        TextOptions o = Latin();
        o.WordBreaking = WordBreaking.BreakWord;
        string text = "Hi Supercalifragilistic ok";
        o.WrappingLength = Width("Supercal", o) + 1;
        string[] lines = Lines(text, o);
        Assert.Equal(text, string.Concat(lines));
        Assert.Equal("Hi ", lines[0]);
        Assert.True(lines.Length >= 4);
        Assert.StartsWith("Supercal", lines[1], StringComparison.Ordinal);
        foreach (string line in lines)
        {
            Assert.True(Width(line.TrimEnd(), o) <= o.WrappingLength + 0.01f, line);
        }

        // Standard lets the same word overflow on its own line.
        o.WordBreaking = WordBreaking.Standard;
        Assert.Equal(["Hi ", "Supercalifragilistic ", "ok"], Lines(text, o));
    }

    [Fact]
    public void InterWordStretchesEveryLineThroughItsSpaces()
    {
        TextOptions o = Latin();
        o.WrappingLength = 200;
        o.TextJustification = TextJustification.InterWord;
        string text = "The quick brown fox jumps over the lazy dog and some more words follow here";
        TextMetrics m = TextMeasurer.Measure(text, o);
        Assert.True(m.LineCount >= 3);
        foreach (LineMetrics l in m.Lines)
        {
            Assert.Equal(200, l.Advance, 0.01f);
        }

        // The extra width is the same in every space of a line, and nothing else moves relative to its word.
        IReadOnlyList<GlyphBounds> chars = TextMeasurer.GetCharacterBounds(text, o);
        var gaps = new List<float>();
        foreach (var line in chars.GroupBy(c => c.Line))
        {
            GlyphBounds[] cs = [.. line];
            gaps.Add(cs.Where(c => c.CodePoint == ' ').Select(c => c.Bounds.Width).Distinct().Count());
        }

        Assert.All(gaps, g => Assert.True(g <= 2, "spaces of one line differ")); // the hanging last space may differ
        TextOptions none = o.Clone();
        none.TextJustification = TextJustification.None;
        Assert.True(TextMeasurer.Measure(text, none).Lines[0].Advance < 200);
    }

    [Fact]
    public void InterCharacterStretchesLinesWithoutSpacesToo()
    {
        TextOptions o = Latin();
        o.WrappingLength = 120;
        o.TextJustification = TextJustification.InterCharacter;
        TextMetrics m = TextMeasurer.Measure("Single", o);
        Assert.Equal(120, m.Lines[0].Advance, 0.01f);

        // A combining mark stays attached to its base: the gap goes after the pair.
        string text = "a\u0301b";
        IReadOnlyList<GlyphBounds> chars = TextMeasurer.GetCharacterBounds(text, o);
        Assert.Equal(2, chars.Count);
        Assert.Equal(0, chars[0].Index);
        Assert.Equal(2, chars[1].Index);
    }

    [Fact]
    public void JustificationNeedsAWrappingLength()
    {
        TextOptions o = Latin();
        o.TextJustification = TextJustification.InterWord;
        TextOptions plain = Latin();
        Assert.Equal(TextMeasurer.Measure("one two three", plain).Advance, TextMeasurer.Measure("one two three", o).Advance);
    }

    // Sequences that must never be cut: combining marks, an emoji ZWJ family, flags, Hangul jamo, a Devanagari conjunct, CRLF-free.
    private static readonly string[] ClusterTexts =
    [
        "e\u0301e\u0301e\u0301 a\u0308\u0323b ee\u0301e\u0301",
        "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466 \U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466\U0001F44D\U0001F3FD",
        "\U0001F1EF\U0001F1F5\U0001F1FA\U0001F1F8\U0001F1E9\U0001F1EA x\U0001F1EB",
        "\u1100\u1161\u11A8\u1100\u1161\u1112\u1161\u11AB \uAC01\uAC00\u11A8",
        "\u0915\u094D\u0937\u093F \u0928\u092E\u0938\u094D\u0924\u0947",
        "ok \u0E01\u0E33\u0E01\u0E33 x",
    ];

    [Fact]
    public void WrappingNeverSplitsAGraphemeCluster()
    {
        int cases = 0;
        foreach (string text in ClusterTexts)
        {
            var starts = new HashSet<int> { text.Length };
            for (int i = 0; i < text.Length; i += Graphemes.NextLength(text.AsSpan(i)))
            {
                starts.Add(i);
            }

            foreach (WordBreaking wb in Enum.GetValues<WordBreaking>())
            {
                for (float wrap = 8; wrap <= 90; wrap += 2.5f)
                {
                    TextOptions o = Latin(16);
                    o.WrappingLength = wrap;
                    o.WordBreaking = wb;
                    cases++;
                    TextMetrics m = TextMeasurer.Measure(text, o);
                    int expectedStart = 0;
                    foreach (LineMetrics l in m.Lines)
                    {
                        Assert.Contains(l.Start, starts);
                        Assert.Equal(expectedStart, l.Start);
                        expectedStart = l.Start + l.Length;
                    }

                    Assert.Equal(text.Length, expectedStart);
                }
            }
        }

        TestReport.Line($"CLUSTERS {cases} wrap cases, no line starts inside a grapheme cluster");
    }

    [Fact]
    public void CharacterBoxesAreOnePerGraphemeCluster()
    {
        foreach (string text in ClusterTexts)
        {
            int clusters = 0;
            for (int i = 0; i < text.Length; i += Graphemes.NextLength(text.AsSpan(i)))
            {
                clusters++;
            }

            TextOptions o = Latin(16);
            o.WrappingLength = 60;
            IReadOnlyList<GlyphBounds> boxes = TextMeasurer.GetCharacterBounds(text, o);
            Assert.Equal(clusters, boxes.Count);
            int expected = 0;
            foreach (GlyphBounds b in boxes)
            {
                Assert.Equal(expected, b.Index);
                expected += Graphemes.NextLength(text.AsSpan(expected));
            }
        }
    }

    [Fact]
    public void TruncationAtAnyBoxEdgeLeavesWholeClusters()
    {
        string text = ClusterTexts[1];
        TextOptions o = Latin(16);
        foreach (GlyphBounds b in TextMeasurer.GetCharacterBounds(text, o))
        {
            // The index of every box is a place a caret or a cut can go.
            Assert.True(b.Index == 0 || Graphemes.NextLength(text.AsSpan(0, b.Index)) > 0);
            Assert.Equal(b.Index, ClusterStartAtOrBefore(text, b.Index));
        }
    }

    private static int ClusterStartAtOrBefore(string text, int index)
    {
        int start = 0;
        while (start < text.Length)
        {
            int next = start + Graphemes.NextLength(text.AsSpan(start));
            if (next > index)
            {
                return start;
            }

            start = next;
        }

        return start;
    }

    [Fact]
    public void ALineStartsAtTheParagraphStartEvenAfterInvisibleCharacters()
    {
        // Found by fuzzing (tests/testdata/breaking/fuzz-leading-zwj-line-start.bin): the zero width joiner draws nothing, and the
        // first line used to start after it, inside the cluster it belongs to.
        foreach (string text in new[] { "\u200D\u1B44", "\u200B\u200Bab cd", "\u00ADab" })
        {
            TextOptions o = Latin();
            o.WrappingLength = 30;
            TextMetrics m = TextMeasurer.Measure(text, o);
            Assert.Equal(0, m.Lines[0].Start);
            Assert.Equal(text.Length, m.Lines.Sum(l => l.Length));
        }
    }

    [Fact]
    public void RandomTextKeepsTheLayoutInvariants()
    {
        string[] pool =
        [
            "a", "b", "word", " ", "  ", "-", "\u00AD", "\u200B", "\u2060", "\u00A0", "\u0301", "\u200D", "\U0001F468", "\U0001F3FD", "\U0001F1EF", "\u3042",
            "\u65E5", "\u3001", "\u300C", "\u300D", "\u201C", "\u201D", "(", ")", "1", ".", ",", "/", "$", "%", "\n", "\r\n", "\u1100", "\u1161", "\u11A8", "\ud800", "\udc00", "\t",
        ];
        var rng = new Random(12345);
        var sb = new StringBuilder();
        int cases = 0;
        for (int n = 0; n < 400; n++)
        {
            sb.Clear();
            int parts = rng.Next(1, 40);
            for (int i = 0; i < parts; i++)
            {
                sb.Append(pool[rng.Next(pool.Length)]);
            }

            string text = sb.ToString();
            var starts = new HashSet<int> { text.Length };
            for (int i = 0; i < text.Length; i += Graphemes.NextLength(text.AsSpan(i)))
            {
                starts.Add(i);
            }

            foreach (WordBreaking wb in Enum.GetValues<WordBreaking>())
            {
                foreach (TextJustification tj in Enum.GetValues<TextJustification>())
                {
                    TextOptions o = new(n % 2 == 0 ? TestFonts.Noto(14) : TestFonts.NotoCjk(14)) { WrappingLength = rng.Next(5, 120), WordBreaking = wb, TextJustification = tj };
                    cases++;
                    TextMetrics m = TextMeasurer.Measure(text, o);
                    Assert.True(m.LineCount >= 1);
                    foreach (LineMetrics l in m.Lines)
                    {
                        Assert.True(l.Start >= 0 && l.Length >= 0 && l.Start + l.Length <= text.Length);
                        Assert.Contains(l.Start, starts);
                        Assert.True(float.IsFinite(l.Advance) && float.IsFinite(l.Left));
                    }

                    _ = TextMeasurer.GetCharacterBounds(text, o);
                }
            }
        }

        TestReport.Line($"RANDOM layout invariants: {cases} cases");
    }

    [Fact]
    public void SegmentationAllocatesNothingInSteadyState()
    {
        string text = string.Concat(Enumerable.Repeat("The quick brown fox, jumps over the lazy dog (1,234.56) \u65E5\u672C\u8A9E e\u0301 \U0001F468\u200D\U0001F469. ", 200));
        byte[] breaks = new byte[text.Length + 1];
        byte[] clusters = new byte[text.Length + 1];
        for (int warm = 0; warm < 3; warm++)
        {
            LineBreaker.Fill(text, breaks);
            Graphemes.Fill(text, clusters);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            LineBreaker.Fill(text, breaks);
            Graphemes.Fill(text, clusters);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestReport.Line($"SEGMENT steady-state allocation over 20 passes of {text.Length} chars: {allocated} bytes");
        Assert.Equal(0, allocated);
    }
}
