using System.Text;
using SixLabors.Fonts;

namespace OneImage.Text.Tests;

/// <summary>
/// Line breaking and wrapping compared with SixLabors.Fonts 2.1.3 through TextMeasurer only (public API, black-box). Where the
/// two differ on purpose the difference is pinned by a test of its own, with the reason.
/// </summary>
public class WrapReferenceTests
{
    // Text where every break opportunity is after a space or after punctuation that both libraries treat alike.
    private static readonly (string Name, string Text, string Font)[] Ordinary =
    [
        ("prose", "The quick brown fox jumps over the lazy dog, and then the dog wakes up and chases the fox across the whole meadow again.", TestFonts.Regular),
        ("quotes", "He said \u201CHello there\u201D and (maybe) left [quietly] and then \"quit\" 'now' okay, so (really) it was [done].", TestFonts.Regular),
        ("nbsp", "Dr.\u00A0Joseph Becker and Mr.\u00A0Smith went to the market today with Mrs.\u00A0Jones.", TestFonts.Regular),
        ("punct", "Wait... what? Yes! Really: no; maybe, perhaps. Done (almost) 12.5% of 3,000.00 EUR or 45 pcs.", TestFonts.Regular),
        ("greek", "\u0391\u03b8\u03ae\u03bd\u03b1 \u03ba\u03b1\u03b9 \u039a\u03bf\u03c1\u03b9\u03bd\u03b8\u03bf\u03c2 \u0416\u0443\u0440\u043d\u0430\u043b caf\u00e9 \u00c5ngstr\u00f6m na\u00efve", TestFonts.Regular),
    ];

    // Text with opportunities that are not after spaces (hyphens, slashes, soft hyphens, CJK, ...), where the two libraries part.
    private static readonly (string Name, string Text, string Font)[] Mixed =
    [
        ("hyphen", "a well-known self-made e-mail address ab-cd-ef-gh", TestFonts.Regular),
        ("url", "visit https://example.com/path/to/page?q=1 or www.example.org/a/b today", TestFonts.Regular),
        ("shy", "inter\u00ADnational\u00ADization of compre\u00ADhensible things", TestFonts.Regular),
        ("zwsp", "abc\u200Bdefghijkl\u200Bmnop qrs tuvwx\u200Byz", TestFonts.Regular),
        ("cjk", "\u65e5\u672c\u8a9e\u306e\u65e5\u672c\u8a9e\u4e8c\u4e09\u4e00\u3041\u3042\u65e5\u672c\u8a9e", TestFonts.Cff),
    ];

    private static TextOptions OptionsFor(string font, float size, float wrap, WordBreaking wb) =>
        new(font == TestFonts.Cff ? TestFonts.NotoCjk(size) : TestFonts.Noto(size)) { WrappingLength = wrap, WordBreaking = wb };

    internal static List<int> TheirLineStarts(string text, TextOptions mine, string fontFile)
    {
        var theirs = Reference.Options(Reference.Font(fontFile, mine.Font.Size), mine);
        var starts = new List<int>();
        if (!SixLabors.Fonts.TextMeasurer.TryMeasureCharacterBounds(text, theirs, out ReadOnlySpan<SixLabors.Fonts.GlyphBounds> gb))
        {
            return starts;
        }

        // The reference reports ink boxes, so a new line is where the pen moves back to the left.
        float lastX = float.NaN;
        foreach (SixLabors.Fonts.GlyphBounds g in gb)
        {
            int cp = g.Codepoint.Value;
            if (cp is 0xAD or 0x200B or 0x200D or 0x2060 or '\n' or '\r')
            {
                continue;
            }

            if (float.IsNaN(lastX) || g.Bounds.X < lastX - 0.05f)
            {
                starts.Add(g.StringIndex);
            }

            lastX = g.Bounds.X;
        }

        return starts;
    }

    internal static List<int> OurLineStarts(string text, TextOptions mine) =>
        [.. TextMeasurer.Measure(text, mine).Lines.Select(l => l.Start)];

    internal static string Show(string text, List<int> starts)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < starts.Count; i++)
        {
            int e = i + 1 < starts.Count ? starts[i + 1] : text.Length;
            if (i > 0)
            {
                sb.Append(" \u00A6 ");
            }

            sb.Append(text[starts[i]..e].Replace("\u00AD", "<SHY>").Replace("\u200B", "<ZWSP>").Replace("\u00A0", "<NBSP>").Replace("\n", "\\n"));
        }

        return sb.ToString();
    }

    private static (int Total, int Same, string FirstDifference) Sweep(string text, string font, WordBreaking wb)
    {
        int total = 0;
        int same = 0;
        string first = "";
        foreach (float size in new[] { 12f, 20f, 33.3f })
        {
            for (float wrap = 30; wrap <= 400; wrap += 7)
            {
                TextOptions o = OptionsFor(font, size, wrap, wb);
                total++;
                string m = Show(text, OurLineStarts(text, o));
                string t = Show(text, TheirLineStarts(text, o, font));
                if (m == t)
                {
                    same++;
                }
                else if (first.Length == 0)
                {
                    first = $"size={size} wrap={wrap}\n   ours  : {m}\n   theirs: {t}";
                }
            }
        }

        return (total, same, first);
    }

    [Fact]
    public void OrdinaryTextWrapsAtTheSamePlacesInStandardAndKeepAll()
    {
        int total = 0;
        var failures = new List<string>();
        foreach (WordBreaking wb in new[] { WordBreaking.Standard, WordBreaking.KeepAll })
        {
            foreach ((string name, string text, string font) in Ordinary)
            {
                (int t, int same, string first) = Sweep(text, font, wb);
                total += t;
                if (same != t)
                {
                    failures.Add($"{name}/{wb}: {same}/{t} identical. First difference: {first}");
                }
            }
        }

        TestReport.Line($"WRAP ordinary text, Standard and KeepAll: {total} (text x size x wrapping length) cases, {failures.Count} text/mode pairs with a difference");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void OtherModesAgreeOnMostOrdinaryCases()
    {
        // BreakAll and BreakWord fill lines character by character, which is where the reference's look-ahead (see below) shows most.
        // The guard is the rate seen when this was written, with a margin.
        var report = new StringBuilder();
        foreach (WordBreaking wb in new[] { WordBreaking.BreakAll, WordBreaking.BreakWord })
        {
            int total = 0;
            int same = 0;
            foreach ((string _, string text, string font) in Ordinary)
            {
                (int t, int s, string _) = Sweep(text, font, wb);
                total += t;
                same += s;
            }

            report.Append($" {wb} {same}/{total}");
            Assert.True(same >= total * 0.85, $"{wb}: only {same}/{total} identical");
        }

        TestReport.Line("WRAP ordinary text, other modes, identical line starts:" + report);
    }

    [Fact]
    public void MixedTextAgreesWhereBreaksFollowSpaces()
    {
        // For text with opportunities inside words the rates are lower; the reasons are the differences listed below.
        var report = new StringBuilder();
        foreach ((string name, string text, string font) in Mixed)
        {
            foreach (WordBreaking wb in Enum.GetValues<WordBreaking>())
            {
                (int t, int same, string _) = Sweep(text, font, wb);
                report.Append($" {name}/{wb} {same}/{t}");
            }
        }

        TestReport.Line("WRAP mixed text, identical line starts:" + report);
    }

    [Fact]
    public void TheReferenceKeepsRoomForTheNextCharacterAfterABreakThatIsNotAfterASpace()
    {
        // Difference 1 (explains most differences on the mixed texts). A word-by-word fill is exact in both libraries: a line is
        // accepted as soon as its text fits the length. After a break that is not preceded by a space, the reference also wants room
        // for the first character of the next piece, so it ends the line one opportunity early when that character would not fit.
        // We fill exactly: a line is accepted when its text fits.
        const string text = "mm-mm-mm-mm-mm-mm";
        double ourExtra = 0;
        double theirExtra = 0;
        for (int piece = 2; piece <= 4; piece++)
        {
            string line = string.Concat(Enumerable.Repeat("mm-", piece));
            TextOptions probe = new(TestFonts.Noto(20));
            float lineWidth = TextMeasurer.MeasureAdvance(line.TrimEnd(), probe).Width;
            float next = TextMeasurer.MeasureAdvance("m", probe).Width;
            int ours = 0;
            int theirs = 0;
            for (float wrap = lineWidth - 5; wrap < lineWidth + next + 5; wrap += 0.25f)
            {
                TextOptions o = new(TestFonts.Noto(20)) { WrappingLength = wrap };
                int oursFirst = OurLineStarts(text, o) is { Count: > 1 } a ? a[1] : text.Length;
                int theirsFirst = TheirLineStarts(text, o, TestFonts.Regular) is { Count: > 1 } b ? b[1] : text.Length;
                if (ours == 0 && oursFirst >= line.Length)
                {
                    ours = 1;
                    ourExtra = wrap - lineWidth;
                }

                if (theirs == 0 && theirsFirst >= line.Length)
                {
                    theirs = 1;
                    theirExtra = wrap - lineWidth;
                }
            }

            Assert.True(ourExtra < 0.5, $"ours needs {ourExtra} px more than the line's width");
            Assert.InRange(theirExtra, next - 1, next + 1);
        }

        TestReport.Line($"WRAP look-ahead: the reference needs the next character's width ({theirExtra:F2} px) of slack after a mid-word break, we need {ourExtra:F2}");
    }

    // A greedy fill over our break opportunities and our own character advances, with the reference's observed look-ahead as an
    // option: a segment that does not end in a space is accepted only if the first character after it fits too.
    private static List<int> Model(string text, TextOptions o, bool lookAhead)
    {
        TextOptions plain = o.Clone();
        plain.WrappingLength = 0;
        IReadOnlyList<GlyphBounds> boxes = TextMeasurer.GetCharacterBounds(text, plain);
        byte[] flags = new byte[text.Length + 1];
        OneImage.Text.Unicode.LineBreaker.Fill(text, flags);
        static bool Space(int cp) => cp is ' ' or '\t' or 0x1680 or (>= 0x2000 and <= 0x2006) or (>= 0x2008 and <= 0x200A) or 0x205F or 0x3000;
        var starts = new List<int> { 0 };
        float lineW = 0;
        int lineFirst = 0;
        int k = 0;
        while (k < boxes.Count)
        {
            int end = k + 1;
            while (end < boxes.Count && flags[boxes[end].Index] == 0)
            {
                end++;
            }

            float full = 0;
            for (int i = k; i < end; i++)
            {
                full += boxes[i].Bounds.Width;
            }

            float trimmed = full;
            int last = end - 1;
            while (last >= k && Space(boxes[last].CodePoint))
            {
                trimmed -= boxes[last].Bounds.Width;
                last--;
            }

            float need = trimmed;
            if (lookAhead && last == end - 1 && end < boxes.Count)
            {
                need += boxes[end].Bounds.Width;
            }

            if (k > lineFirst && lineW + need > o.WrappingLength)
            {
                starts.Add(boxes[k].Index);
                lineFirst = k;
                lineW = 0;
            }

            lineW += full;
            k = end;
        }

        return starts;
    }

    [Fact]
    public void TheLookAheadModelExplainsMostOfTheMidWordDifferences()
    {
        // Texts whose break opportunities UAX #14 and the reference agree on. The look-ahead is the main difference left; it is not
        // the whole story (the reference also treats soft hyphens and some hyphen cases its own way), so the bar is a share of cases.
        (string Name, string Text, string Font)[] texts =
        [
            ("hyphen", "a well-known self-made e-mail address ab-cd-ef-gh", TestFonts.Regular),
            ("shy", "inter\u00ADnational\u00ADization of compre\u00ADhensible things", TestFonts.Regular),
            ("zwsp", "abc\u200Bdefghijkl\u200Bmnop qrs tuvwx\u200Byz", TestFonts.Regular),
            ("cjk", "\u65e5\u672c\u8a9e\u306e\u65e5\u672c\u8a9e\u4e8c\u4e09\u4e00\u3041\u3042\u65e5\u672c\u8a9e", TestFonts.Cff),
        ];
        int total = 0;
        int plainSame = 0;
        int modelSame = 0;
        var firstBad = new List<string>();
        foreach ((string name, string text, string font) in texts)
        {
            foreach (float size in new[] { 12f, 20f, 33.3f })
            {
                for (float wrap = 30; wrap <= 400; wrap += 7)
                {
                    TextOptions o = OptionsFor(font, size, wrap, WordBreaking.Standard);
                    string theirs = Show(text, TheirLineStarts(text, o, font));
                    total++;
                    if (Show(text, Model(text, o, lookAhead: false)) == theirs)
                    {
                        plainSame++;
                    }

                    string model = Show(text, Model(text, o, lookAhead: true));
                    if (model == theirs)
                    {
                        modelSame++;
                    }
                    else if (firstBad.Count < 4)
                    {
                        firstBad.Add($"{name} size={size} wrap={wrap}\n   model : {model}\n   theirs: {theirs}");
                    }
                }
            }
        }

        TestReport.Line($"WRAP look-ahead model on hyphen/shy/zwsp/cjk text: exact fill matches the reference in {plainSame}/{total}, with the look-ahead {modelSame}/{total}");
        Assert.True(modelSame > plainSame, "the look-ahead explains nothing");
        Assert.True(modelSame >= total * 0.75, $"look-ahead model reproduces only {modelSame}/{total}\n" + string.Join('\n', firstBad));
    }

    [Fact]
    public void EastAsianTextFillsLinesExactly()
    {
        // Same cause as above: every ideograph is its own opportunity, so the reference holds one character fewer per line.
        TextOptions probe = new(TestFonts.NotoCjk(20));
        float one = TextMeasurer.MeasureAdvance("\u65E5", probe).Width;
        const string text = "\u65E5\u672C\u8A9E\u306E\u65E5\u672C\u8A9E\u4E8C\u4E09";
        TextOptions o = new(TestFonts.NotoCjk(20)) { WrappingLength = (one * 3) + (one / 2) };
        Assert.Equal(3, OurLineStarts(text, o)[1]);
        Assert.Equal(2, TheirLineStarts(text, o, TestFonts.Cff)[1]);
    }

    [Fact]
    public void UnicodeLineBreakingKeepsNumbersAndQuotesTogetherWhereTheReferenceSplitsThem()
    {
        // Difference 2. UAX #14 LB25 keeps a prefix, a sign and its number together ("$1,234.56", "-5.25"). The reference ends a
        // line between them when the number does not fit after it.
        const string numbers = "Total: $1,234.56 and then -5.25 end";
        TextOptions o = OptionsFor(TestFonts.Regular, 20, 130, WordBreaking.Standard);
        string ours = Show(numbers, OurLineStarts(numbers, o));
        string theirs = Show(numbers, TheirLineStarts(numbers, o, TestFonts.Regular));
        Assert.DoesNotContain("$ \u00A6", ours, StringComparison.Ordinal);
        Assert.Contains("$ \u00A6", theirs, StringComparison.Ordinal);

        // Difference 3. Rules LB15a-d (Unicode 15.1 and later) keep an opening quotation mark with the word after it, even across
        // spaces; the reference implements the older rule set and ends the line after the mark.
        const string quote = "word \u201C quoted text \u201D more words";
        TextOptions q = OptionsFor(TestFonts.Regular, 20, 70, WordBreaking.Standard);
        Assert.Equal("word  \u00A6 \u201C quoted  \u00A6 text \u201D  \u00A6 more  \u00A6 words", Show(quote, OurLineStarts(quote, q)));
        Assert.Equal("word \u201C  \u00A6 quoted  \u00A6 text \u201D  \u00A6 more  \u00A6 words", Show(quote, TheirLineStarts(quote, q, TestFonts.Regular)));
    }

    [Fact]
    public void WeBreakAfterASolidusAndTheReferenceDoesNot()
    {
        // Difference 4. UAX #14 gives a break opportunity after "/" (class SY) before a letter, which wraps URLs and paths. The
        // reference never breaks there, so a long URL overflows on a single line.
        const string text = "see https://example.com/path/to/page now";
        TextOptions o = OptionsFor(TestFonts.Regular, 20, 130, WordBreaking.Standard);
        Assert.True(OurLineStarts(text, o).Count > TheirLineStarts(text, o, TestFonts.Regular).Count);
        Assert.Contains("https:// \u00A6 example.com/", Show(text, OurLineStarts(text, o)), StringComparison.Ordinal);
    }

    [Fact]
    public void LongParagraphHasTheSameLineCount()
    {
        // The text of the wrapping benchmark: a long paragraph, so both libraries do comparable work there.
        const string Paragraph =
            "Typography is the craft of arranging type so that written language is legible, readable and appealing when displayed. " +
            "The arrangement of type involves selecting typefaces, point sizes, line lengths, line spacing and letter spacing, and " +
            "adjusting the space between pairs of letters. The term typography is also applied to the style, arrangement and appearance " +
            "of the letters, numbers and symbols created by the process. ";
        string text = string.Concat(Enumerable.Repeat(Paragraph, 28));
        TextOptions o = OptionsFor(TestFonts.Regular, 24, 600, WordBreaking.Standard);
        var theirs = Reference.Options(Reference.Font(TestFonts.Regular, 24), o);
        int ours = TextMeasurer.CountLines(text, o);
        int reference = SixLabors.Fonts.TextMeasurer.CountLines(text, theirs);
        TestReport.Line($"WRAP long paragraph ({text.Length} chars): {ours} lines, reference {reference}");
        Assert.Equal(reference, ours);
    }

    [Fact]
    public void JustificationMatchesTheReference()
    {
        string[] texts =
        [
            "The quick brown fox jumps over the lazy dog and some more words follow here",
            "One two\nthree four six seven eight nine ten eleven\nlast",
            "Aaa bbb  ccc ddd",
            "single",
        ];
        int cases = 0;
        double worst = 0;
        var diffs = new List<string>();
        foreach (TextJustification tj in new[] { TextJustification.InterWord, TextJustification.InterCharacter })
        {
            foreach (TextAlignment ta in Enum.GetValues<TextAlignment>())
            {
                foreach (string text in texts)
                {
                    TextOptions mine = new(TestFonts.Noto(16)) { WrappingLength = 200, TextJustification = tj, TextAlignment = ta };
                    var theirs = Reference.Options(Reference.Font(TestFonts.Regular, 16), mine);
                    cases++;
                    IReadOnlyList<GlyphBounds> a = TextMeasurer.GetCharacterBounds(text, mine);
                    SixLabors.Fonts.TextMeasurer.TryMeasureCharacterBounds(text, theirs, out var b);
                    SixLabors.Fonts.GlyphBounds[] bb = b.ToArray();
                    int j = 0;
                    foreach (GlyphBounds g in a)
                    {
                        if (char.IsWhiteSpace((char)g.CodePoint))
                        {
                            continue;
                        }

                        while (j < bb.Length && char.IsWhiteSpace((char)bb[j].Codepoint.Value))
                        {
                            j++;
                        }

                        SixLabors.Fonts.FontRectangle r = bb[j++].Bounds;
                        worst = Math.Max(worst, Math.Abs(g.Ink.X - r.X));
                        if (Math.Abs(g.Ink.X - r.X) > 0.05 || Math.Abs(g.Ink.Y - r.Y) > 0.05)
                        {
                            diffs.Add($"{tj}/{ta} '{text}': '{(char)g.CodePoint}' x {g.Ink.X:F2} vs {r.X:F2}, y {g.Ink.Y:F2} vs {r.Y:F2}");
                            break;
                        }
                    }
                }
            }
        }

        TestReport.Line($"JUSTIFY {cases} cases against SixLabors.Fonts: largest ink x difference {worst:F4} px");
        Assert.True(diffs.Count == 0, string.Join('\n', diffs.Take(10)));
    }
}
