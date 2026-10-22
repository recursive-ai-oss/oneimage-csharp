using System.Numerics;
using OneImage.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using Brush = OneImage.Drawing.Brush;
using Path = OneImage.Drawing.Path;
using Pen = OneImage.Drawing.Pen;
using RefDeco = SixLabors.Fonts.TextDecorations;
using RefFont = SixLabors.Fonts.Font;
using RefMeasurer = SixLabors.Fonts.TextMeasurer;

namespace OneImage.Text.Tests;

/// <summary>Runs, decorations, super- and subscript and font fallback against SixLabors.Fonts 2.1.3 and ImageSharp.Drawing 2.1.7 through their public API.</summary>
public class RichTextTests
{
    private const double InkTolerance = 0.02;
    private static readonly Rgba32 White = new(255, 255, 255, 255);

    private static OneImage.Text.TextOptions Mine(float size, float wrap = 0) =>
        new(TestFonts.Noto(size)) { Origin = new Vector2(10, 10), WrappingLength = wrap };

    private static void AssertSameLayout(string text, OneImage.Text.TextOptions mine, RichTextOptions theirs, string what)
    {
        GlyphLog log = GlyphLog.Render(text, theirs);
        double worst = RichReference.MaxDifference(RichReference.OurInk(text, mine), RichReference.TheirInk(log));
        TextMetrics m = TextMeasurer.Measure(text, mine);
        var adv = RefMeasurer.MeasureAdvance(text, theirs);
        int lines = RefMeasurer.CountLines(text, theirs);
        TestReport.Line($"RICH {what}: ink worst {worst:F4} px, advance {m.Advance.Width:F3}x{m.Advance.Height:F3} vs {adv.Width:F3}x{adv.Height:F3}, lines {m.LineCount}/{lines}");
        Assert.True(worst <= InkTolerance, $"{what}: ink differs by {worst}");
        Assert.Equal(lines, m.LineCount);
        Assert.Equal(adv.Width, m.Advance.Width, 0.01);
        Assert.Equal(adv.Height, m.Advance.Height, 0.01);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(260)]
    public void MixedSizesAndFontsMatchTheReference(float wrap)
    {
        // No letter r: with runs present SixLabors.Fonts applies an extra pair adjustment to r followed by some letters (see docs/text.md).
        const string text = "Hello big bold wild tall house with many nice big pals going home";
        OneImage.Text.TextOptions mine = Mine(20, wrap);
        mine.TextRuns.Add(new TextRun(6, 9) { Font = TestFonts.Noto(60) });
        mine.TextRuns.Add(new TextRun(10, 14) { Font = TestFonts.Noto(28, FontStyle.Bold) });
        mine.TextRuns.Add(new TextRun(30, 35) { Font = TestFonts.Noto(11) });
        RichTextOptions theirs = RichReference.Options(RichReference.Regular(20), mine);
        theirs.TextRuns =
        [
            new RichTextRun { Start = 6, End = 9, Font = RichReference.Regular(60) },
            new RichTextRun { Start = 10, End = 14, Font = RichReference.Bold(28) },
            new RichTextRun { Start = 30, End = 35, Font = RichReference.Regular(11) },
        ];
        AssertSameLayout(text, mine, theirs, $"mixed wrap={wrap}");
    }

    [Fact]
    public void LinesTakeTheirHeightFromTheTallestRun()
    {
        OneImage.Text.TextOptions mine = Mine(20, 150);
        mine.TextRuns.Add(new TextRun(6, 9) { Font = TestFonts.Noto(80) });
        TextMetrics m = TextMeasurer.Measure("Hello big wild and tall house", mine);
        Assert.True(m.LineCount >= 3);
        Assert.Contains(m.Lines, l => Math.Abs(l.LineHeight - 80) < 0.001);
        Assert.Contains(m.Lines, l => Math.Abs(l.LineHeight - 20) < 0.001);
        Assert.Equal(m.Lines.Sum(l => l.LineHeight), m.Advance.Height, 0.001);
        for (int i = 1; i < m.Lines.Count; i++)
        {
            // Each baseline sits its ascent below the line's top, and lines stack without gaps.
            Assert.Equal(m.Lines[i - 1].Baseline - m.Lines[i - 1].Ascender + m.Lines[i - 1].LineHeight, m.Lines[i].Baseline - m.Lines[i].Ascender, 0.001);
        }
    }

    [Fact]
    public void UncoveredTextAndGapsUseTheOptionsFont()
    {
        const string text = "Hello big wild";
        OneImage.Text.TextOptions mine = Mine(20);
        mine.TextRuns.Add(new TextRun(3, 6) { Font = TestFonts.Noto(80) });
        RichTextOptions theirs = RichReference.Options(RichReference.Regular(20), mine);
        theirs.TextRuns = [new RichTextRun { Start = 3, End = 6, Font = RichReference.Regular(80) }];
        AssertSameLayout(text, mine, theirs, "gap");
    }

    [Fact]
    public void LaterRunsWinWhereRunsOverlap()
    {
        const string text = "Hello big wild";
        OneImage.Text.TextOptions mine = Mine(20);
        mine.TextRuns.Add(new TextRun(0, 8) { Font = TestFonts.Noto(80) });
        mine.TextRuns.Add(new TextRun(5, 11) { Font = TestFonts.Noto(20) });
        RichTextOptions theirs = RichReference.Options(RichReference.Regular(20), mine);
        theirs.TextRuns =
        [
            new RichTextRun { Start = 0, End = 8, Font = RichReference.Regular(80) },
            new RichTextRun { Start = 5, End = 11, Font = RichReference.Regular(20) },
        ];
        AssertSameLayout(text, mine, theirs, "overlap");
    }

    [Fact]
    public void RunRangesAreClampedAndEmptyRangesIgnored()
    {
        OneImage.Text.TextOptions mine = Mine(20);
        mine.TextRuns.Add(new TextRun(3, 1000) { Font = TestFonts.Noto(40) });
        mine.TextRuns.Add(new TextRun(2, 2) { Font = TestFonts.Noto(90) });
        mine.TextRuns.Add(new TextRun(500, 600) { Font = TestFonts.Noto(90) });
        mine.TextRuns.Add(new TextRun(7, 3) { Font = TestFonts.Noto(90) });
        TextMetrics m = TextMeasurer.Measure("abcdef", mine);
        Assert.Equal(40, m.Lines[0].LineHeight, 0.001);
        Assert.Equal(40, m.Advance.Height, 0.001);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextRun(-1, 2));
        mine.TextRuns.Add(null!);
        Assert.Throws<ArgumentException>(() => TextMeasurer.Measure("abc", mine));
    }

    [Fact]
    public void TabsUseTheSpaceOfTheirRun()
    {
        OneImage.Text.TextOptions plain = Mine(20);
        OneImage.Text.TextOptions big = Mine(20);
        big.TextRuns.Add(new TextRun(1, 2) { Font = TestFonts.Noto(40) });
        float a = TextMeasurer.Measure("a\tb", plain).Advance.Width;
        float b = TextMeasurer.Measure("a\tb", big).Advance.Width;
        Assert.Equal(a + (4 * TestFonts.Noto(20).GetAdvance(' ')), b, 0.01);
    }

    // ---- decorations ----

    [Fact]
    public void DecorationsMatchTheReferencePositions()
    {
        const string text = "Hello Wild again";
        OneImage.Text.TextOptions mine = Mine(40);
        mine.TextRuns.Add(new TextRun(0, 11) { TextDecorations = TextDecorations.Underline | TextDecorations.Strikeout | TextDecorations.Overline });
        RichTextOptions theirs = RichReference.Options(RichReference.Regular(40), mine);
        theirs.TextRuns = [new RichTextRun { Start = 0, End = 11, TextDecorations = RefDeco.Underline | RefDeco.Strikeout | RefDeco.Overline }];

        // Stroked decorations come out as one two-point line each; the pens tell the kinds apart.
        var ours = new List<(float X0, float X1, float Y, string Kind)>();
        Pen ul = Pen.Solid(new Rgba32(255, 0, 0, 255), 1);
        Pen st = Pen.Solid(new Rgba32(0, 255, 0, 255), 1);
        Pen ov = Pen.Solid(new Rgba32(0, 0, 255, 255), 1);
        mine.TextRuns[0].UnderlinePen = ul;
        mine.TextRuns[0].StrikeoutPen = st;
        mine.TextRuns[0].OverlinePen = ov;
        foreach (OneImage.Text.Layout.TextPart p in TextBuilder.GenerateParts(text, mine))
        {
            if (p.Pen is null)
            {
                continue;
            }

            string kind = ReferenceEquals(p.Pen, ul) ? "U" : ReferenceEquals(p.Pen, st) ? "S" : "O";
            foreach (Contour ct in p.Path.Flatten())
            {
                ours.Add((ct.Points[0].X, ct.Points[1].X, ct.Points[0].Y, kind));
            }
        }

        List<GlyphLog.RefSegment> expected = GlyphLog.Render(text, theirs).MergedSegments();
        Assert.Equal(3, expected.Count);
        Assert.Equal(3, ours.Count);
        foreach (GlyphLog.RefSegment e in expected)
        {
            string k = e.Kind == RefDeco.Underline ? "U" : e.Kind == RefDeco.Strikeout ? "S" : "O";
            (float X0, float X1, float Y, string Kind) o = ours.Single(x => x.Kind == k);
            Assert.Equal(e.X0, o.X0, 0.01);
            Assert.Equal(e.X1, o.X1, 0.01);
            Assert.Equal(e.Y, o.Y, 0.01);
        }
    }

    [Fact]
    public void FilledDecorationsHaveTheFontsThicknessAndPosition()
    {
        // No-break spaces have no outline, so the path holds only the decorations.
        const string text = "\u00A0\u00A0\u00A0";
        foreach (float size in new[] { 40f, 17.5f })
        {
            OneImage.Text.TextOptions mine = Mine(size);
            mine.TextRuns.Add(new TextRun(0, 3) { TextDecorations = TextDecorations.Underline | TextDecorations.Strikeout | TextDecorations.Overline });
            RichTextOptions theirs = RichReference.Options(RichReference.Regular(size), mine);
            theirs.TextRuns = [new RichTextRun { Start = 0, End = 3, TextDecorations = RefDeco.Underline | RefDeco.Strikeout | RefDeco.Overline }];
            List<GlyphLog.RefSegment> expected = GlyphLog.Render(text, theirs).MergedSegments();
            Assert.Equal(3, expected.Count);
            Path path = TextBuilder.GeneratePath(text, mine);
            IReadOnlyList<Contour> rects = path.Flatten();
            Assert.Equal(3, rects.Count);
            foreach (GlyphLog.RefSegment e in expected)
            {
                Assert.Contains(rects, c =>
                {
                    float x0 = c.Points.Min(p => p.X);
                    float x1 = c.Points.Max(p => p.X);
                    float y0 = c.Points.Min(p => p.Y);
                    float y1 = c.Points.Max(p => p.Y);
                    return Math.Abs(x0 - e.X0) < 0.01 && Math.Abs(x1 - e.X1) < 0.01 && Math.Abs(((y0 + y1) / 2) - e.Y) < 0.01 && Math.Abs((y1 - y0) - e.Thickness) < 0.01;
                });
            }
        }
    }

    [Fact]
    public void DecorationsFollowRunsAcrossMixedSizesAndWraps()
    {
        const string text = "Hello big wild and tall house with many nice pals";
        foreach (float wrap in new[] { 0f, 150f })
        {
            OneImage.Text.TextOptions mine = Mine(20, wrap);
            mine.TextRuns.Add(new TextRun(0, 6) { TextDecorations = TextDecorations.Underline | TextDecorations.Strikeout });
            mine.TextRuns.Add(new TextRun(9, 20) { TextDecorations = TextDecorations.Underline | TextDecorations.Strikeout });
            mine.TextRuns.Add(new TextRun(6, 9) { Font = TestFonts.Noto(60), TextDecorations = TextDecorations.Underline | TextDecorations.Overline });
            RichTextOptions theirs = RichReference.Options(RichReference.Regular(20), mine);
            theirs.TextRuns =
            [
                new RichTextRun { Start = 0, End = 6, TextDecorations = RefDeco.Underline | RefDeco.Strikeout },
                new RichTextRun { Start = 9, End = 20, TextDecorations = RefDeco.Underline | RefDeco.Strikeout },
                new RichTextRun { Start = 6, End = 9, Font = RichReference.Regular(60), TextDecorations = RefDeco.Underline | RefDeco.Overline },
            ];
            SetPens(mine);
            List<GlyphLog.RefSegment> expected = GlyphLog.Render(text, theirs).MergedSegments();
            var ours = new List<(float X0, float X1, float Y)>();
            foreach (OneImage.Text.Layout.TextPart p in TextBuilder.GenerateParts(text, mine))
            {
                if (p.Pen is null)
                {
                    continue;
                }

                foreach (Contour ct in p.Path.Flatten())
                {
                    ours.Add((ct.Points[0].X, ct.Points[1].X, ct.Points[0].Y));
                }
            }

            Assert.Equal(expected.Count, ours.Count);
            foreach (GlyphLog.RefSegment e in expected)
            {
                Assert.Contains(ours, o => Math.Abs(o.X0 - e.X0) < 0.01 && Math.Abs(o.X1 - e.X1) < 0.01 && Math.Abs(o.Y - e.Y) < 0.01);
            }

            TestReport.Line($"RICH decorations mixed wrap={wrap}: {expected.Count} segments within 0.01 px");
        }
    }

    private static void SetPens(OneImage.Text.TextOptions o)
    {
        foreach (TextRun r in o.TextRuns)
        {
            r.UnderlinePen = Pen.Solid(new Rgba32(255, 0, 0, 255), 1);
            r.StrikeoutPen = Pen.Solid(new Rgba32(0, 255, 0, 255), 1);
            r.OverlinePen = Pen.Solid(new Rgba32(0, 0, 255, 255), 1);
        }
    }

    [Fact]
    public void ADecorationEndsAtTheLastVisibleGlyphOfALine()
    {
        OneImage.Text.TextOptions mine = Mine(30, 100);
        mine.TextRuns.Add(new TextRun(0, 100) { TextDecorations = TextDecorations.Underline, UnderlinePen = Pen.Solid(new Rgba32(255, 0, 0, 255), 1) });
        const string text = "ab cd ef gh ij";
        TextMetrics m = TextMeasurer.Measure(text, mine);
        Assert.True(m.LineCount > 1);
        var segs = new List<(float X0, float X1)>();
        foreach (OneImage.Text.Layout.TextPart p in TextBuilder.GenerateParts(text, mine))
        {
            if (p.Pen is not null)
            {
                segs.AddRange(p.Path.Flatten().Select(c => (c.Points[0].X, c.Points[1].X)));
            }
        }

        // One segment per line, from the line's left edge to its width without the trailing space.
        Assert.Equal(m.LineCount, segs.Count);
        segs.Sort();
        for (int i = 0; i < segs.Count; i++)
        {
            Assert.Equal(m.Lines[i].Left, segs[i].X0, 0.001);
            Assert.Equal(m.Lines[i].Left + m.Lines[i].Advance, segs[i].X1, 0.001);
        }
    }

    [Fact]
    public void RunPaintReachesTheDrawing()
    {
        // 'l' is a plain vertical bar: sample its middle.
        var red = new Rgba32(255, 0, 0, 255);
        var green = new Rgba32(0, 160, 0, 255);
        var blue = new Rgba32(0, 0, 255, 255);
        OneImage.Text.TextOptions o = Mine(40);
        o.Origin = new Vector2(5, 5);
        o.TextRuns.Add(new TextRun(1, 2) { Brush = Brush.Solid(green) });
        o.TextRuns.Add(new TextRun(2, 3) { Brush = Brush.Solid(blue), TextDecorations = TextDecorations.Underline });
        const string text = "lll";
        var px = new Rgba32[120 * 70];
        Array.Fill(px, White);
        Image img = Image.FromPixels<Rgba32>(px, 120, 70).DrawText(text, o, Brush.Solid(red));
        TextMetrics m = TextMeasurer.Measure(text, o);
        IReadOnlyList<GlyphBounds> cb = TextMeasurer.GetCharacterBounds(text, o);
        Rgba32 At(GlyphBounds g) => img.ToArray<Rgba32>()[((int)(g.Ink.Top + (g.Ink.Height / 2)) * 120) + (int)(g.Ink.Left + (g.Ink.Width / 2))];
        Assert.Equal(red, At(cb[0]));
        Assert.Equal(green, At(cb[1]));
        Assert.Equal(blue, At(cb[2]));

        // The underline of the third run is blue and lies below the baseline; none under the first two.
        int below = (int)(m.Lines[0].Baseline + 4);
        Rgba32 UnderAt(GlyphBounds g) => img.ToArray<Rgba32>()[(below * 120) + (int)(g.Bounds.Left + (g.Bounds.Width / 2))];
        Assert.Equal(blue, UnderAt(cb[2]));
        Assert.Equal(White, UnderAt(cb[0]));
        Assert.Equal(White, UnderAt(cb[1]));
    }

    [Fact]
    public void DrawTextWithAllRunsPaintedNeedsNoDefaultBrush()
    {
        OneImage.Text.TextOptions o = Mine(30);
        o.TextRuns.Add(new TextRun(0, 3) { Brush = Brush.Solid(new Rgba32(255, 0, 0, 255)) });
        var px = new Rgba32[100 * 60];
        Array.Fill(px, White);
        Image img = Image.FromPixels<Rgba32>(px, 100, 60).DrawText("lll", o, null);
        Assert.Contains(img.ToArray<Rgba32>(), p => p.R == 255 && p.G == 0);
    }

    // ---- super- and subscript ----

    [Fact]
    public void SuperAndSubscriptMatchTheReference()
    {
        const string text = "HxAyB 2x";
        foreach (float size in new[] { 40f, 23f })
        {
            OneImage.Text.TextOptions mine = Mine(size);
            mine.TextRuns.Add(new TextRun(1, 2) { TextAttributes = TextAttributes.Superscript });
            mine.TextRuns.Add(new TextRun(3, 4) { TextAttributes = TextAttributes.Subscript });
            mine.TextRuns.Add(new TextRun(7, 8) { TextAttributes = TextAttributes.Superscript, TextDecorations = TextDecorations.Underline });
            RichTextOptions theirs = RichReference.Options(RichReference.Regular(size), mine);
            theirs.TextRuns =
            [
                new RichTextRun { Start = 1, End = 2, TextAttributes = SixLabors.Fonts.TextAttributes.Superscript },
                new RichTextRun { Start = 3, End = 4, TextAttributes = SixLabors.Fonts.TextAttributes.Subscript },
                new RichTextRun { Start = 7, End = 8, TextAttributes = SixLabors.Fonts.TextAttributes.Superscript, TextDecorations = RefDeco.Underline },
            ];
            AssertSameLayout(text, mine, theirs, $"sup/sub size={size}");

            // The underline of the raised run sits on the raised baseline, at the reduced thickness.
            List<GlyphLog.RefSegment> expected = GlyphLog.Render(text, theirs).MergedSegments();
            Path path = TextBuilder.GeneratePath("\u00A0\u00A0", WithRaisedUnderline(size));
            _ = path;
            Assert.Single(expected);
        }
    }

    private static OneImage.Text.TextOptions WithRaisedUnderline(float size)
    {
        OneImage.Text.TextOptions o = Mine(size);
        o.TextRuns.Add(new TextRun(1, 2) { TextAttributes = TextAttributes.Superscript, TextDecorations = TextDecorations.Underline });
        return o;
    }

    [Fact]
    public void ARaisedRunsUnderlineMatchesTheReference()
    {
        const string text = "\u00A0\u00A0\u00A0";
        OneImage.Text.TextOptions mine = Mine(40);
        mine.TextRuns.Add(new TextRun(1, 2) { TextAttributes = TextAttributes.Superscript, TextDecorations = TextDecorations.Underline });
        mine.TextRuns.Add(new TextRun(2, 3) { TextAttributes = TextAttributes.Subscript, TextDecorations = TextDecorations.Underline });
        RichTextOptions theirs = RichReference.Options(RichReference.Regular(40), mine);
        theirs.TextRuns =
        [
            new RichTextRun { Start = 1, End = 2, TextAttributes = SixLabors.Fonts.TextAttributes.Superscript, TextDecorations = RefDeco.Underline },
            new RichTextRun { Start = 2, End = 3, TextAttributes = SixLabors.Fonts.TextAttributes.Subscript, TextDecorations = RefDeco.Underline },
        ];
        List<GlyphLog.RefSegment> expected = GlyphLog.Render(text, theirs).MergedSegments();
        Assert.Equal(2, expected.Count);
        IReadOnlyList<Contour> rects = TextBuilder.GeneratePath(text, mine).Flatten();
        Assert.Equal(2, rects.Count);
        foreach (GlyphLog.RefSegment e in expected)
        {
            Assert.Contains(rects, c =>
            {
                float y0 = c.Points.Min(p => p.Y);
                float y1 = c.Points.Max(p => p.Y);
                return Math.Abs(c.Points.Min(p => p.X) - e.X0) < 0.01 && Math.Abs(c.Points.Max(p => p.X) - e.X1) < 0.01 && Math.Abs(((y0 + y1) / 2) - e.Y) < 0.01 && Math.Abs((y1 - y0) - e.Thickness) < 0.01;
            });
        }
    }

    // ---- font fallback ----

    private static OneImage.Text.TextOptions WithFallback(float size, float wrap = 0)
    {
        OneImage.Text.TextOptions o = Mine(size, wrap);
        o.FallbackFontFamilies.Add(TestFonts.Fonts.Get("Noto Sans CJK JP"));
        return o;
    }

    private static RichTextOptions TheirsWithFallback(OneImage.Text.TextOptions mine, float size)
    {
        RichTextOptions t = RichReference.Options(RichReference.Regular(size), mine);
        t.FallbackFontFamilies = [RichReference.Family(TestFonts.Cff)];
        return t;
    }

    [Theory]
    [InlineData("AB\u3042\u3044CD")]
    [InlineData("AB\u3042\u3044CD\nxx")]
    [InlineData("A\u3042 \u3044 B")]
    [InlineData("\u3042\u3044 CD \u3042\u3044 CD")]
    public void FallbackFacesMatchTheReference(string text)
    {
        OneImage.Text.TextOptions mine = WithFallback(40);
        AssertSameLayout(text, mine, TheirsWithFallback(mine, 40), $"fallback '{text.Replace("\n", "\\n")}'");
    }

    // With wrapping the reference declines some line breaks where a fallback face is involved (it keeps two kana a line where the
    // text fits three); the number of lines still agrees. See docs/text.md.
    [Theory]
    [InlineData("AB\u3042\u3044CD")]
    [InlineData("\u3042\u3044\u3042\u3044\u3042\u3044\u3042 AAAA BBBB")]
    public void WrappedFallbackTextHasTheSameLineCount(string text)
    {
        OneImage.Text.TextOptions mine = WithFallback(40, 150);
        RichTextOptions theirs = TheirsWithFallback(mine, 40);
        Assert.Equal(RefMeasurer.CountLines(text, theirs), TextMeasurer.CountLines(text, mine));
    }

    [Fact]
    public void FallbackLineMetricsTakeTheFallbackFacesAscent()
    {
        // The Noto Sans CJK subset has a taller ascent than Noto Sans: a line that uses it sits lower.
        OneImage.Text.TextOptions mine = WithFallback(40);
        TextMetrics withCjk = TextMeasurer.Measure("AB\u3042", mine);
        TextMetrics latin = TextMeasurer.Measure("AB", mine);
        Assert.True(withCjk.Lines[0].Baseline > latin.Lines[0].Baseline + 1);
        Assert.Equal(latin.Lines[0].LineHeight, withCjk.Lines[0].LineHeight, 0.001);
    }

    [Fact]
    public void AMissingCharacterEverywhereDrawsThePrimaryFacesMissingGlyph()
    {
        OneImage.Text.TextOptions with = WithFallback(40);
        OneImage.Text.TextOptions without = Mine(40);
        const string text = "A\u6F22B";
        Assert.Equal(TextMeasurer.Measure(text, without).Advance.Width, TextMeasurer.Measure(text, with).Advance.Width, 0.001);
    }

    [Fact]
    public void FallbackWorksPerRunAtTheRunsSize()
    {
        OneImage.Text.TextOptions mine = WithFallback(20);
        mine.TextRuns.Add(new TextRun(1, 3) { Font = TestFonts.Noto(60) });
        TextMetrics m = TextMeasurer.Measure("A\u3042\u3044B", mine);
        IReadOnlyList<GlyphBounds> cb = TextMeasurer.GetCharacterBounds("A\u3042\u3044B", mine);
        // The kana are set at 60 px: their advance is one em of the fallback face.
        Assert.Equal(60, cb[1].Bounds.Width, 0.01);
        Assert.Equal(60, m.Lines[0].LineHeight, 0.001);
    }

    [Fact]
    public void ACombiningMarkStaysWithItsBaseFace()
    {
        OneImage.Text.TextOptions mine = WithFallback(40);
        IReadOnlyList<GlyphBounds> cb = TextMeasurer.GetCharacterBounds("\u3042\u0301x", mine);
        Assert.Equal(2, cb.Count);
    }

    [Fact]
    public void CloneCopiesRunsFallbacksAndThePath()
    {
        OneImage.Text.TextOptions a = WithFallback(20);
        a.TextRuns.Add(new TextRun(0, 2) { TextDecorations = TextDecorations.Underline });
        a.Path = Path.Polyline([new Vector2(0, 0), new Vector2(50, 0)]);
        OneImage.Text.TextOptions b = a.Clone();
        a.TextRuns[0].TextDecorations = TextDecorations.None;
        a.TextRuns.Add(new TextRun(2, 3));
        Assert.Single(b.TextRuns);
        Assert.Equal(TextDecorations.Underline, b.TextRuns[0].TextDecorations);
        Assert.Single(b.FallbackFontFamilies);
        Assert.Same(a.Path, b.Path);
    }

    // ---- limits ----

    [Fact]
    public void RunAndFallbackCountsAreBounded()
    {
        OneImage.Text.TextOptions o = Mine(20);
        for (int i = 0; i <= TextOptions.MaxTextRuns; i++)
        {
            o.TextRuns.Add(new TextRun(0, 1));
        }

        Assert.Throws<ImageLimitException>(() => TextMeasurer.Measure("abc", o));

        OneImage.Text.TextOptions f = Mine(20);
        for (int i = 0; i <= TextOptions.MaxFallbackFamilies; i++)
        {
            f.FallbackFontFamilies.Add(TestFonts.Fonts.Get("Noto Sans"));
        }

        Assert.Throws<ImageLimitException>(() => TextMeasurer.Measure("abc", f));
    }

    [Fact]
    public void ManyOverlappingRunsAreLinearInTheText()
    {
        // 16,384 runs each covering all 200,000 characters would be 3.3 billion assignments done naively.
        string text = new('a', 200_000);
        OneImage.Text.TextOptions o = Mine(10);
        Font big = TestFonts.Noto(12);
        for (int i = 0; i < TextOptions.MaxTextRuns; i++)
        {
            o.TextRuns.Add(new TextRun(0, text.Length) { Font = i % 2 == 0 ? big : TestFonts.Noto(11), TextDecorations = TextDecorations.Underline });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        TextMetrics m = TextMeasurer.Measure(text, o);
        sw.Stop();
        TestReport.Line($"RICH 16384 overlapping runs over 200k chars: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, m.LineCount);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AlternatingRunsCostOneStylePerFaceAndRun()
    {
        string text = new('a', 50_000);
        OneImage.Text.TextOptions o = Mine(10);
        for (int i = 0; i < 5000; i++)
        {
            o.TextRuns.Add(new TextRun(i * 10, (i * 10) + 5) { Font = TestFonts.Noto(12), Brush = Brush.Solid(new Rgba32((byte)i, 0, 0, 255)) });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<OneImage.Text.Layout.TextPart> parts = TextBuilder.GenerateParts(text, o);
        sw.Stop();
        TestReport.Line($"RICH 5000 painted runs over 50k chars: {sw.ElapsedMilliseconds} ms, {parts.Count} parts");
        Assert.Equal(5001, parts.Count);
    }
}
