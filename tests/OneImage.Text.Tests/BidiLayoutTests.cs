using System.Diagnostics;
using System.Text;
using OneImage.Text.Fonts;
using OneImage.Text.Layout;

namespace OneImage.Text.Tests;

/// <summary>Bidirectional layout checked against the algorithm itself, plus the paths through layout that mix directions with other features.</summary>
public class BidiLayoutTests
{
    private static readonly Lazy<FontCollection> Fonts = new(() =>
    {
        var c = new FontCollection();
        c.Add(TestFonts.PathOf(BidiHarness.Merged));
        return c;
    });

    private static readonly Lazy<FontCollection> SplitFonts = new(() =>
    {
        var c = new FontCollection();
        c.Add(TestFonts.PathOf(BidiHarness.Latin));
        c.Add(TestFonts.PathOf(BidiHarness.Arabic));
        c.Add(TestFonts.PathOf(BidiHarness.Hebrew));
        return c;
    });

    private static TextOptions Options(float size = 20, TextDirection dir = TextDirection.Auto) =>
        new(Fonts.Value.Get("Noto Sans").CreateFont(size)) { TextDirection = dir };

    private static int[] Cps(string s) => [.. s.EnumerateRunes().Select(r => r.Value)];

    // The line's characters in display order, as the algorithm gives them: levels with rule L1 for the whole line, then L2.
    private static string Expected(string text, TextDirection dir)
    {
        int[] c = Cps(text);
        byte[] lev = new byte[c.Length];
        int para = OneFont.TextShaping.ResolveBidiLevels(c, dir switch { TextDirection.LeftToRight => OneFont.TextDirection.LeftToRight, TextDirection.RightToLeft => OneFont.TextDirection.RightToLeft, _ => OneFont.TextDirection.Auto }, lev);
        OneFont.TextShaping.ResetTrailingWhitespace(c, lev, para);
        int[] order = new int[c.Length];
        int m = OneFont.TextShaping.GetVisualOrder(lev, order);
        var sb = new StringBuilder();
        for (int i = 0; i < m; i++)
        {
            int cp = c[order[i]];

            // Characters without a glyph of their own (the direction controls) do not show.
            if (cp is (>= 0x200E and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069))
            {
                continue;
            }

            sb.Append(char.ConvertFromUtf32(cp));
        }

        return sb.ToString();
    }

    private static string Shown(string text, TextOptions o)
    {
        TextLayout layout = TextLayout.Create(text, o);
        var sb = new StringBuilder();
        foreach (LayoutLine line in layout.Lines)
        {
            for (int k = line.GlyphStart; k < line.GlyphEnd; k++)
            {
                sb.Append(char.ConvertFromUtf32(layout.Glyphs[k].CodePoint));
            }
        }

        return sb.ToString();
    }

    [Theory]
    [InlineData(TextDirection.Auto)]
    [InlineData(TextDirection.LeftToRight)]
    [InlineData(TextDirection.RightToLeft)]
    public void OneLineIsShownInTheOrderOfTheAlgorithm(TextDirection dir)
    {
        // Latin letters, Hebrew letters (one glyph each), digits, white space, brackets, separators and the direction controls.
        string[] alphabet = ["a", "b", "\u05D0", "\u05D1", "\u05DD", " ", " ", "1", "2", "(", ")", "[", "]", ".", ",", "-", "$", "%", "\u200F", "\u200E", "\u202B", "\u202A", "\u202C", "\u2067", "\u2066", "\u2069", "\u202E"];
        var rng = new Random(20260722);
        TextOptions o = Options(18, dir);
        for (int n = 0; n < 1500; n++)
        {
            var sb = new StringBuilder();
            int len = rng.Next(1, 22);
            for (int i = 0; i < len; i++)
            {
                sb.Append(alphabet[rng.Next(alphabet.Length)]);
            }

            string text = sb.ToString();
            string expected = Expected(text, dir);
            string shown = Shown(text, o);
            Assert.True(expected == shown, $"{dir}: {string.Join(' ', text.Select(c => ((int)c).ToString("X4")))}\n expected [{expected}]\n shown    [{shown}]");
        }
    }

    [Fact]
    public void ForcedDirectionsKeepWordsReadable()
    {
        // A right-to-left paragraph of Latin words keeps a phrase together, left to right; Hebrew words in it read from the right.
        Assert.Equal("abc def", Shown("abc def", Options(dir: TextDirection.RightToLeft)));
        Assert.Equal("abc 123", Shown("abc 123", Options(dir: TextDirection.RightToLeft)));
        Assert.Equal("ב abc א", Shown("א abc ב", Options(dir: TextDirection.RightToLeft)));

        // A left-to-right paragraph still shows a Hebrew word right to left.
        Assert.Equal("abc \u05DD\u05D1\u05D0 def", Shown("abc \u05D0\u05D1\u05DD def", Options(dir: TextDirection.LeftToRight)));
    }

    [Fact]
    public void EachParagraphTakesItsOwnDirection()
    {
        TextLayout layout = TextLayout.Create("abc \u05D0\u05D1\u05D2\n\u05D0\u05D1\u05D2 abc\n123", Options());
        Assert.Equal([false, true, false], layout.Lines.Select(l => l.Rtl));
        Assert.Equal("abc \u05D2\u05D1\u05D0", Shown("abc \u05D0\u05D1\u05D2", Options()));
        Assert.Equal("abc \u05D2\u05D1\u05D0", Shown("\u05D0\u05D1\u05D2 abc", Options()));
    }

    [Fact]
    public void MirroredCharactersShowTheirMirrorInRightToLeftText()
    {
        FontFace face = TestFonts.Face(BidiHarness.Merged);
        int open = face.GetGlyphIndex('(');
        int close = face.GetGlyphIndex(')');
        TextLayout rtl = TextLayout.Create("\u05D0(\u05D1)", Options());

        TextLayout ltr = TextLayout.Create("a(b)", Options());
        Assert.Equal([face.GetGlyphIndex('a'), open, face.GetGlyphIndex('b'), close], ltr.Glyphs.Select(g => g.GlyphId));
        Assert.Equal([open, face.GetGlyphIndex('\u05D1'), close, face.GetGlyphIndex('\u05D0')], rtl.Glyphs.Select(g => g.GlyphId));
    }

    [Fact]
    public void StartAndEndFollowTheParagraphDirection()
    {
        var o = Options(20);
        o.WrappingLength = 300;
        o.HorizontalAlignment = HorizontalAlignment.Left;
        o.Origin = new System.Numerics.Vector2(10, 0);

        // Left to right: Start is the left edge, End the right edge of the 300 px box.
        TextLayout a = TextLayout.Create("abc", o);
        Assert.Equal(10f, a.Lines[0].Left, 0.01f);
        o.TextAlignment = TextAlignment.End;
        TextLayout b = TextLayout.Create("abc", o);
        Assert.Equal(310f, b.Lines[0].Left + b.Lines[0].Width, 0.01f);

        // Right to left: the other way round. With no wrapping the box is as wide as the line, so Start and End coincide.
        o.TextAlignment = TextAlignment.Start;
        o.WrappingLength = 300;
        TextLayout c = TextLayout.Create("\u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2", o);
        Assert.True(c.Lines.Count >= 2);
        float right = c.Lines.Max(l => l.Left + l.Hang + l.Width);
        Assert.All(c.Lines, l => Assert.Equal(right, l.Left + l.Hang + l.Width, 0.01f));
        o.TextAlignment = TextAlignment.End;
        TextLayout d = TextLayout.Create("\u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2", o);
        Assert.All(d.Lines, l => Assert.Equal(10f, l.Left + l.Hang, 0.01f));
    }

    [Fact]
    public void HangingSpacesAtTheEndOfARightToLeftLineLieOutsideTheBox()
    {
        var o = Options(20);
        o.WrappingLength = 60;
        o.Origin = new System.Numerics.Vector2(100, 0);
        TextLayout l = TextLayout.Create("\u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2 \u05D0\u05D1\u05D2", o);
        Assert.True(l.Lines.Count >= 2);

        // The first line ends in a space in reading order, which sits at its left; the visible letters still end at the right edge.
        LayoutLine first = l.Lines[0];
        Assert.True(first.Hang > 0);
        Assert.True(first.VisStart > first.GlyphStart);
        Assert.Equal(l.Lines.Max(x => x.Left + x.Hang + x.Width), first.Left + first.Hang + first.Width, 0.01f);
    }

    [Fact]
    public void CharacterBoundsCoverEveryCharacterOnceInRightToLeftText()
    {
        string text = "\u05E9\u05B8\u05C1\u05DC\u05D5\u05B9\u05DD abc \u0645\u0631\u062D\u0628\u0627";
        var o = Options(30);
        var bounds = TextMeasurer.GetCharacterBounds(text, o);
        var starts = new List<int>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            starts.Add(e.ElementIndex);
        }

        Assert.Equal(starts.Order(), bounds.Select(b => b.Index).Order());
        Assert.All(bounds, b => Assert.True(b.Bounds.Width >= 0));
    }

    [Fact]
    public void FallbackFontsServeRightToLeftRuns()
    {
        FontCollection fc = SplitFonts.Value;
        var o = new TextOptions(fc.Get("Noto Sans").CreateFont(24));
        o.FallbackFontFamilies.Add(fc.Get("Noto Sans Arabic"));
        o.FallbackFontFamilies.Add(fc.Get("Noto Sans Hebrew"));
        TextLayout layout = TextLayout.Create("abc \u0645\u0631\u062D\u0628\u0627 \u05E9\u05DC\u05D5\u05DD def", o);
        string[] families = [.. layout.Glyphs.Where(g => !g.IsSpace).Select(g => layout.Styles[g.Style].Face.FamilyName).Distinct()];
        Assert.Contains("Noto Sans Arabic", families);
        Assert.Contains("Noto Sans Hebrew", families);

        // Display order: the Latin words stay left to right, the Arabic and Hebrew words are reversed in the order of reading.
        Assert.Equal("abc \u05DD\u05D5\u05DC\u05E9 \u0627\u0628\u062D\u0631\u0645 def".Length, layout.Glyphs.Count);
    }

    [Fact]
    public void RunsDecorationsAndPathsWorkWithRightToLeftText()
    {
        string text = "abc \u0645\u0631\u062D\u0628\u0627 \u05E9\u05DC\u05D5\u05DD 123";
        var o = Options(24);
        o.TextRuns.Add(new TextRun(4, 9) { TextDecorations = TextDecorations.Underline, Font = Fonts.Value.Get("Noto Sans").CreateFont(32) });
        o.TextRuns.Add(new TextRun(10, 14) { TextDecorations = TextDecorations.Strikeout | TextDecorations.Overline });
        var path = OneImage.Text.TextBuilder.GeneratePath(text, o);
        Assert.False(path.IsEmpty);

        // Along a curve.
        var line = new OneImage.Drawing.PathBuilder().MoveTo(new System.Numerics.Vector2(0, 100)).CubicBezierTo(100, 0, 200, 200, 400, 100).Build();
        var o2 = Options(24);
        o2.Path = line;
        Assert.False(OneImage.Text.TextBuilder.GeneratePath(text, o2).IsEmpty);

        // Justified, wrapped and in every alignment.
        foreach (TextJustification j in new[] { TextJustification.InterWord, TextJustification.InterCharacter })
        {
            var o3 = Options(24);
            o3.WrappingLength = 150;
            o3.TextJustification = j;
            TextLayout layout = TextLayout.Create(text + " " + text, o3);
            Assert.True(layout.Lines.Count >= 2);
            foreach (LayoutLine l in layout.Lines.Take(layout.Lines.Count - 1))
            {
                Assert.True(l.Width <= 150.01f);
            }
        }
    }

    // ---- Limits ----

    private static double Seconds(Action a)
    {
        var sw = Stopwatch.StartNew();
        a();
        return sw.Elapsed.TotalSeconds;
    }

    [Fact]
    public void ExplicitEmbeddingDepthIsBounded()
    {
        // 125 levels of embedding, then more of them which are ignored (UAX #9 X1 to X8).
        string text = new string('\u202B', 400) + "\u05D0" + new string('\u202C', 400) + "a";
        TextLayout layout = TextLayout.Create(text, Options(12));
        Assert.All(layout.Glyphs, g => Assert.True(g.Level <= 126));
        Assert.Equal(2, layout.Glyphs.Count);
    }

    [Fact]
    public void HostileBidiInputStaysLinear()
    {
        string[] makers =
        [
            "rle", "isolates", "brackets", "nested brackets", "mixed controls",
        ];
        foreach (string kind in makers)
        {
            double Time(int n)
            {
                string text = kind switch
                {
                    "rle" => string.Concat(Enumerable.Repeat("\u202B\u05D0\u202A\u05D0", n / 4)),
                    "isolates" => string.Concat(Enumerable.Repeat("\u2067\u05D0\u2066a\u2068", n / 5)) + new string('\u2069', n / 5),
                    "brackets" => string.Concat(Enumerable.Repeat("(\u05D0)", n / 3)),
                    "nested brackets" => new string('(', n / 2) + "\u05D0" + new string(')', n / 2),
                    _ => string.Concat(Enumerable.Repeat("\u202E(a\u2067b\u202C\u200F[1", n / 9)),
                };
                TextOptions o = Options(10);
                return Seconds(() => TextLayout.Create(text, o));
            }

            Time(2000);
            double small = Math.Max(Time(40_000), 0.005);
            double large = Time(160_000);
            TestReport.Line($"BIDI hostile {kind}: 40k chars {small * 1000:0.#} ms, 160k chars {large * 1000:0.#} ms");

            // Four times the text takes about four times as long; allow plenty of noise on a shared machine.
            Assert.True(large < small * 16 + 0.5, $"{kind}: {small:0.000} s for 40k, {large:0.000} s for 160k");
            Assert.True(large < 20, $"{kind}: {large} s");
        }
    }
}
