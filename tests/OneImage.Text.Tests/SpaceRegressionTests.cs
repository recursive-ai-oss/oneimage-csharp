namespace OneImage.Text.Tests;

/// <summary>White space between and after letters: it never draws, it ends cursive joining, and it keeps its own advance.</summary>
public class SpaceRegressionTests
{
    private static readonly Lazy<FontCollection> Fonts = new(() =>
    {
        var c = new FontCollection();
        c.Add(TestFonts.PathOf(BidiHarness.Merged));
        return c;
    });

    private static TextOptions Options(TextDirection dir = TextDirection.Auto, float wrap = 0) =>
        new(Fonts.Value.Get("Noto Sans").CreateFont(32)) { TextDirection = dir, WrappingLength = wrap, Origin = new(10, 10) };

    private static Rgba32[] Render(string text, TextOptions o)
    {
        var white = new Rgba32[400 * 80];
        Array.Fill(white, new Rgba32(255, 255, 255, 255));
        return Image.FromPixels<Rgba32>(white, 400, 80).DrawText(text, o, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();
    }

    [Theory]
    [InlineData("ab", TextDirection.LeftToRight)]
    [InlineData("\u0628\u0628", TextDirection.RightToLeft)]
    [InlineData("ab", TextDirection.RightToLeft)]
    public void TrailingSpacesDrawNothing(string word, TextDirection dir)
    {
        Assert.Equal(Render(word, Options(dir)), Render(word + "   ", Options(dir)));
        Assert.Equal(Render(word, Options(dir)), Render(word + " \t ", Options(dir)));
    }

    [Fact]
    public void ATrailingSpaceSitsAfterThePreviousGlyph()
    {
        foreach (TextDirection dir in new[] { TextDirection.LeftToRight, TextDirection.RightToLeft })
        {
            var b = TextMeasurer.GetCharacterBounds("ab ", Options(dir));
            Assert.Equal(3, b.Count);
            var prev = b[1].Bounds;
            var space = b[2].Bounds;
            Assert.True(space.Width > 0);
            if (dir == TextDirection.LeftToRight)
            {
                Assert.True(space.Left >= prev.Right - 0.01f, $"space {space} overlaps {prev}");
            }
            else
            {
                Assert.True(space.Right <= b[0].Bounds.Left + 0.01f || space.Left >= prev.Right - 0.01f, $"space {space} overlaps {prev}");
            }
        }
    }

    [Fact]
    public void ASpaceEndsArabicJoining()
    {
        // Each letter is an isolated form on its own, so with a space between them neither may take a joining form.
        var o = Options(TextDirection.RightToLeft);
        var alone = TextMeasurer.GetCharacterBounds("\u0628", o);
        var spaced = TextMeasurer.GetCharacterBounds("\u0628 \u0628", o);
        var joined = TextMeasurer.GetCharacterBounds("\u0628\u0628", o);
        Assert.Equal(3, spaced.Count);
        Assert.Equal(alone[0].Ink.Width, spaced[0].Ink.Width, 0.01f);
        Assert.Equal(alone[0].Ink.Width, spaced[2].Ink.Width, 0.01f);
        Assert.NotEqual(alone[0].Ink.Width, joined[0].Ink.Width, 0.01f);

        // Same through tabs, line breaks and a no-break space (all are non-joining characters).
        foreach (string gap in new[] { "\t", "\u00A0", "\u2003", " \u200C " })
        {
            var b = TextMeasurer.GetCharacterBounds("\u0628" + gap + "\u0628", o);
            Assert.Equal(alone[0].Ink.Width, b[0].Ink.Width, 0.01f);
            Assert.Equal(alone[0].Ink.Width, b[^1].Ink.Width, 0.01f);
        }
    }

    [Fact]
    public void SpacesAlsoEndJoiningWhenAFontChangeSplitsTheText()
    {
        // A space in a run of its own (a different font size) still separates the letters on either side.
        var o = Options(TextDirection.RightToLeft);
        var alone = TextMeasurer.GetCharacterBounds("\u0628", o);
        string text = "\u0628 \u0628";
        o.TextRuns.Add(new TextRun(1, 2) { Font = Fonts.Value.Get("Noto Sans").CreateFont(20) });
        var b = TextMeasurer.GetCharacterBounds(text, o);
        Assert.Equal(alone[0].Ink.Width, b[0].Ink.Width, 0.01f);
        Assert.Equal(alone[0].Ink.Width, b[2].Ink.Width, 0.01f);
    }
}
