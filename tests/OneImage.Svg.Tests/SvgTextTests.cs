using System.Text;
using OneImage.Svg;

namespace OneImage.Svg.Tests;

/// <summary>Text in SVG: positions, anchors, text on a path, decorations, clipping by text, fonts and limits.</summary>
[Collection(SvgSerial.Name)]
public class SvgTextTests
{
    private const string Ns = SvgRenderTests.Ns;

    private static string FontPath(string file) => Path.Combine(AppContext.BaseDirectory, "testdata", "fonts", file);

    private static ImageConfiguration Config(SvgLimits? limits = null)
    {
        var fonts = new SvgFontOptions { UseSystemFonts = false };
        fonts.AddFont(FontPath("NotoSans-Subset-Regular.ttf"));
        fonts.AddFont(FontPath("NotoSans-Subset-Bold-kern.ttf"));
        fonts.SetGenericFamily("sans-serif", "Noto Sans");
        return new ImageConfiguration().Add(new SvgFormat(limits, fonts: fonts));
    }

    private static Image Load(string svg, SvgLimits? limits = null) =>
        Image.Load(Encoding.UTF8.GetBytes(svg), new LoadOptions { Configuration = Config(limits) });

    // A 100 x 50 document drawn at 1:1.
    private static Rgba32[] Render(string body, SvgLimits? limits = null) => Load($"<svg {Ns} viewBox='0 0 100 50' width='100' height='50' font-family='Noto Sans'>{body}</svg>", limits).ToArray<Rgba32>();

    private static (int X0, int Y0, int X1, int Y1) Ink(Rgba32[] px, int minAlpha = 128)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < 50; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                if (px[(y * 100) + x].A >= minAlpha)
                {
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x);
                    y1 = Math.Max(y1, y);
                }
            }
        }

        return (x0, y0, x1, y1);
    }

    [Fact]
    public void TextIsDrawnWithItsFillAtTheBaseline()
    {
        Rgba32[] px = Render("<text x='10' y='30' font-size='20' fill='#00ff00'>Hi</text>");
        var ink = Ink(px);
        Assert.InRange(ink.X0, 10, 14);
        Assert.InRange(ink.Y1, 29, 30); // letters sit on the baseline
        Assert.InRange(ink.Y0, 12, 17); // the cap height of 20 px text is about 14 px
        Rgba32 any = px.First(p => p.A == 255);
        Assert.Equal(new Rgba32(0, 255, 0, 255), any);
    }

    [Fact]
    public void WithoutAnyFontTextIsLeftOutAndTheRestStillRenders()
    {
        var fonts = new SvgFontOptions { UseSystemFonts = false };
        var config = new ImageConfiguration().Add(new SvgFormat(fonts: fonts));
        Image img = Image.Load(Encoding.UTF8.GetBytes($"<svg {Ns} width='20' height='10'><rect width='5' height='5'/><text y='8'>Hi</text></svg>"), new LoadOptions { Configuration = config });
        Rgba32[] px = img.ToArray<Rgba32>();
        Assert.Equal(255, px[(2 * 20) + 2].A);
        Assert.Equal(0, px[(8 * 20) + 15].A);
    }

    [Fact]
    public void TextAnchorMiddleAndEndMoveTheTextByItsWidth()
    {
        var start = Ink(Render("<text x='50' y='30' font-size='20'>Wide</text>"));
        var middle = Ink(Render("<text x='50' y='30' font-size='20' text-anchor='middle'>Wide</text>"));
        var end = Ink(Render("<text x='50' y='30' font-size='20' text-anchor='end'>Wide</text>"));
        int width = start.X1 - start.X0;
        Assert.InRange((middle.X0 + middle.X1) / 2, 50 - 3, 50 + 3);
        Assert.InRange(end.X1, 50 - 3, 50 + 1);
        Assert.InRange(start.X0 - end.X0, width - 3, width + 6);
    }

    [Fact]
    public void DxAndPerCharacterXMoveCharacters()
    {
        var plain = Ink(Render("<text x='10' y='30' font-size='16'>ab</text>"));
        var moved = Ink(Render("<text x='10' y='30' font-size='16'>a<tspan dx='20'>b</tspan></text>"));
        Assert.Equal(plain.X0, moved.X0);
        Assert.InRange(moved.X1 - plain.X1, 19, 21);
        var listed = Ink(Render("<text x='10 40' y='30' font-size='16'>ab</text>"));
        Assert.InRange(listed.X1, 40, 60);
        Assert.True(listed.X1 > plain.X1 + 15);
    }

    [Fact]
    public void LetterSpacingAndWordSpacingWidenTheText()
    {
        var plain = Ink(Render("<text x='5' y='30' font-size='16'>a b</text>"));
        var letters = Ink(Render("<text x='5' y='30' font-size='16' letter-spacing='4'>a b</text>"));
        var words = Ink(Render("<text x='5' y='30' font-size='16' word-spacing='10'>a b</text>"));
        Assert.InRange(letters.X1 - plain.X1, 7, 9); // two gaps before the last letter
        Assert.InRange(words.X1 - plain.X1, 9, 11);
    }

    [Fact]
    public void PercentageSpacingIsRelativeToTheViewportWidth()
    {
        var plain = Ink(Render("<text x='5' y='30' font-size='10'>abc</text>"));
        var spaced = Ink(Render("<text x='5' y='30' font-size='10' letter-spacing='5%'>abc</text>"));
        Assert.InRange(spaced.X1 - plain.X1, 9, 11); // 5% of 100 is 5 per gap, two gaps before the last letter
    }

    [Fact]
    public void TheKerningAttributeSetToZeroTurnsKerningOff()
    {
        var kerned = Ink(Render("<text x='5' y='30' font-size='30'>AVA</text>"));
        var plain = Ink(Render("<text x='5' y='30' font-size='30' kerning='0'>AVA</text>"));
        Assert.True(plain.X1 > kerned.X1, "AVA is tighter with kerning");
    }

    [Fact]
    public void TheDefaultFontSizeIsSixteenAndNamedSizesStepByOnePointTwo()
    {
        var def = Ink(Render("<text x='5' y='30'>H</text>"));
        var sixteen = Ink(Render("<text x='5' y='30' font-size='16'>H</text>"));
        var large = Ink(Render("<text x='5' y='30' font-size='large'>H</text>"));
        var explicitLarge = Ink(Render("<text x='5' y='30' font-size='19.2'>H</text>"));
        Assert.Equal(sixteen, def);
        Assert.Equal(explicitLarge, large);
    }

    [Fact]
    public void TheDefaultFontSizeIsAnOptionThatMovesMediumAndTheNamedSizes()
    {
        var fonts = new SvgFontOptions { UseSystemFonts = false, DefaultFontSize = 12 };
        fonts.AddFont(FontPath("NotoSans-Subset-Regular.ttf"));
        var config = new ImageConfiguration().Add(new SvgFormat(fonts: fonts));
        Rgba32[] Draw(string body) => Image.Load(Encoding.UTF8.GetBytes($"<svg {Ns} viewBox='0 0 100 50' width='100' height='50' font-family='Noto Sans'>{body}</svg>"), new LoadOptions { Configuration = config }).ToArray<Rgba32>();
        var def = Ink(Draw("<text x='5' y='30'>H</text>"));
        Assert.Equal(Ink(Draw("<text x='5' y='30' font-size='12'>H</text>")), def);
        Assert.Equal(Ink(Draw("<text x='5' y='30' font-size='14.4'>H</text>")), Ink(Draw("<text x='5' y='30' font-size='large'>H</text>")));
        Assert.Equal(Ink(Draw("<text x='5' y='30' font-size='24'>H</text>")), Ink(Draw("<text x='5' y='30' font-size='200%'>H</text>")));
        Assert.Throws<ArgumentOutOfRangeException>(() => fonts.DefaultFontSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => fonts.DefaultFontSize = double.NaN);
    }

    [Fact]
    public void TextOnAPathFollowsTheCurve()
    {
        // A straight vertical path: the letters run down it, so the ink is tall and narrow.
        Rgba32[] px = Render("<defs><path id='p' d='M 20 5 L 20 45'/></defs><text font-size='10'><textPath href='#p'>abcdefg</textPath></text>");
        var ink = Ink(px);
        Assert.True(ink.Y1 - ink.Y0 > 25, "the text runs along the path");
        Assert.True(ink.X1 - ink.X0 < 14, "and stays narrow");
        Assert.InRange(ink.X0, 8, 22);
    }

    [Fact]
    public void TextOnAPathStartsAtStartOffsetAndDropsWhatDoesNotFit()
    {
        var a = Ink(Render("<defs><path id='p' d='M 5 30 L 95 30'/></defs><text font-size='10'><textPath href='#p'>ab</textPath></text>"));
        var b = Ink(Render("<defs><path id='p' d='M 5 30 L 95 30'/></defs><text font-size='10'><textPath href='#p' startOffset='50%'>ab</textPath></text>"));
        Assert.InRange(b.X0 - a.X0, 43, 47);
        var cut = Ink(Render("<defs><path id='p' d='M 5 30 L 30 30'/></defs><text font-size='10'><textPath href='#p'>abcdefghijkl</textPath></text>"));
        Assert.True(cut.X1 <= 31, "characters past the end of the path are not drawn");
    }

    [Fact]
    public void UnderlineIsABarBelowTheBaseline()
    {
        Rgba32[] px = Render("<text x='10' y='25' font-size='20' text-decoration='underline'>HH</text>");
        var ink = Ink(px);
        Assert.True(ink.Y1 > 25, "the bar is below the baseline");
        int row = ink.Y1;
        int filled = Enumerable.Range(0, 100).Count(x => px[(row * 100) + x].A >= 128);
        Assert.True(filled > 20, "and runs along the text");
    }

    [Fact]
    public void TextWorksAsAClipPath()
    {
        Rgba32[] px = Render("<clipPath id='c'><text x='5' y='40' font-size='40'>I</text></clipPath><rect width='100' height='50' fill='red' clip-path='url(#c)'/>");
        var ink = Ink(px);
        Assert.InRange(ink.X1 - ink.X0, 3, 12); // only the stem of the letter is left
        Assert.True(ink.Y1 - ink.Y0 > 20);
    }

    [Fact]
    public void WhitespaceCollapsesUnlessPreserved()
    {
        var collapsed = Ink(Render("<text x='5' y='30' font-size='12'>a     b</text>"));
        var single = Ink(Render("<text x='5' y='30' font-size='12'>a b</text>"));
        var preserved = Ink(Render("<text x='5' y='30' font-size='12' xml:space='preserve'>a     b</text>"));
        Assert.Equal(single, collapsed);
        Assert.True(preserved.X1 > single.X1 + 10);
    }

    [Fact]
    public void TrefTakesTheTextOfTheElementItPointsAt()
    {
        var direct = Ink(Render("<text x='5' y='30' font-size='12'>Hi</text>"));
        var viaTref = Ink(Render("<defs><rect id='r'>Hi</rect></defs><text x='5' y='30' font-size='12'><tref href='#r'/></text>"));
        Assert.Equal(direct, viaTref);
    }

    [Fact]
    public void KerningAcrossTspansOfDifferentWeightMatchesOneRun()
    {
        var one = Ink(Render("<text x='5' y='30' font-size='30'>AVA</text>"));
        var split = Ink(Render("<text x='5' y='30' font-size='30'>A<tspan font-weight='bold'>V</tspan>A</text>"));
        // The bold V is wider, but the A before it keeps its pair adjustment: the text stays within a few pixels of the single run.
        Assert.True(split.X1 - one.X1 < 8);
    }

    [Fact]
    public void TextCharactersAreLimited()
    {
        var limits = new SvgLimits { MaxTextCharacters = 10 };
        Assert.Throws<ImageLimitException>(() => Render("<text y='30'>This is more than ten characters</text>", limits));
        // Copies made by use count too.
        Assert.Throws<ImageLimitException>(() => Render("<defs><text id='t' y='30'>0123456</text></defs><use href='#t'/><use href='#t'/>", limits));
        Render("<text y='30'>short</text>", limits);
    }

    [Fact]
    public void TextRenderingTilesGiveTheSameBytesAsOneRender()
    {
        string svg = $"<svg {Ns} viewBox='0 0 300 200' font-family='Noto Sans'><text x='5' y='60' font-size='48' fill='blue'>Tiles across 128 px</text></svg>";
        Image img = Load(svg);
        Rgba32[] whole = img.ToArray<Rgba32>();
        var part = new Rgba32[100 * 100];
        img.CopyPixelsTo<Rgba32>(part, new ImageRect(130, 20, 100, 100));
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                Assert.Equal(whole[((20 + y) * 300) + 130 + x], part[(y * 100) + x]);
            }
        }
    }

    private static Rgba32[] RenderWith(string svg, params string[] fontFiles)
    {
        var fonts = new SvgFontOptions { UseSystemFonts = false };
        foreach (string f in fontFiles)
        {
            fonts.AddFont(FontPath(f));
        }

        var config = new ImageConfiguration().Add(new SvgFormat(fonts: fonts));
        return Image.Load(Encoding.UTF8.GetBytes(svg), new LoadOptions { Configuration = config }).ToArray<Rgba32>();
    }

    [Fact]
    public void VerticalTextRunsDownItsColumnWithLatinLettersLyingOnTheirSide()
    {
        var h = Ink(Render("<text x='10' y='40' font-size='16'>Hello</text>"));
        var v = Ink(Render("<text x='50' y='4' font-size='16' writing-mode='tb'>Hello</text>"));

        // The same ink turned a quarter: its length runs down the column and its height across it.
        Assert.InRange(v.Y1 - v.Y0, h.X1 - h.X0 - 3, h.X1 - h.X0 + 3);
        Assert.InRange(v.X1 - v.X0, h.Y1 - h.Y0 - 3, h.Y1 - h.Y0 + 3);
        Assert.InRange(v.Y0, 4, 8);
        Assert.InRange((v.X0 + v.X1) / 2, 44, 56);
    }

    [Fact]
    public void VerticalTextAnchorWorksAlongTheColumn()
    {
        var start = Ink(Render("<text x='50' y='25' font-size='12' writing-mode='tb'>Hello</text>"));
        var middle = Ink(Render("<text x='50' y='25' font-size='12' writing-mode='tb' text-anchor='middle'>Hello</text>"));
        var end = Ink(Render("<text x='50' y='25' font-size='12' writing-mode='tb' text-anchor='end'>Hello</text>"));
        Assert.InRange(start.Y0, 24, 28);
        Assert.InRange((middle.Y0 + middle.Y1) / 2, 23, 27);
        Assert.InRange(end.Y1, 23, 26);
    }

    [Fact]
    public void VerticalDxMovesAcrossTheColumnAndDyAlongIt()
    {
        var plain = Ink(Render("<text x='30' y='5' font-size='12' writing-mode='tb'>Hi</text>"));
        var moved = Ink(Render("<text x='30' y='5' font-size='12' writing-mode='tb'><tspan dx='20' dy='10'>Hi</tspan></text>"));
        Assert.InRange(moved.X0 - plain.X0, 19, 21);
        Assert.InRange(moved.Y0 - plain.Y0, 9, 11);
    }

    [Fact]
    public void WritingModeOnATspanDoesNotMakeTheTextVertical()
    {
        var ink = Ink(Render("<text x='10' y='30' font-size='14'><tspan writing-mode='tb'>Hello</tspan></text>"));
        Assert.True(ink.X1 - ink.X0 > ink.Y1 - ink.Y0);
    }

    [Fact]
    public void UprightIdeographsStandInTheColumnWhileLatinLiesDown()
    {
        // U+65E5 U+672C are Vertical_Orientation U: drawn as in horizontal text, one em apart down the column.
        string body = "<text x='50' y='2' font-size='20' writing-mode='tb' font-family='Noto Sans CJK JP, Noto Sans'>\u65E5\u672C</text>";
        Rgba32[] px = RenderWith($"<svg {Ns} viewBox='0 0 100 50' width='100' height='50'>{body}</svg>", "NotoSansCJK-Subset.otf", "NotoSans-Subset-Regular.ttf");
        var ink = Ink(px);
        Assert.InRange(ink.X1 - ink.X0, 14, 20);   // an upright ideograph is about as wide as it is tall
        Assert.InRange(ink.Y1 - ink.Y0, 30, 40);   // two of them, one em each
        Assert.InRange((ink.X0 + ink.X1) / 2, 46, 54);
    }

    [Fact]
    public void TextTakesItsStyleFromTheAncestorsOfTheUseElement()
    {
        string defs = "<defs><text id='t' y='30' font-size='16'>Wide</text></defs>";
        var plain = Ink(Render(defs + "<g text-anchor='middle'><use href='#t' x='50'/></g>"));
        var other = Ink(Render(defs + "<g><use href='#t' x='50'/></g>"));
        Assert.InRange((plain.X0 + plain.X1) / 2, 46, 54);
        Assert.InRange(other.X0, 50, 54);

        // The referenced element's own surroundings no longer count: a text-anchor on its parent in the defs does not apply.
        var inDefs = Ink(Render("<defs><g text-anchor='end'><text id='t' y='30' font-size='16'>Wide</text></g></defs><use href='#t' x='50'/>"));
        Assert.InRange(inDefs.X0, 50, 54);
    }

    [Fact]
    public void OpacityOnATspanFadesJustThatSpan()
    {
        Rgba32[] px = Render("<text x='5' y='30' font-size='24' fill='black'>I<tspan opacity='0.5'>I</tspan>I</text>");
        // Three stems along a row; the middle one is faded to half, the others are solid.
        var peaks = new List<int>();
        int peak = 0;
        for (int x = 0; x < 100; x++)
        {
            int a = px[(22 * 100) + x].A;
            if (a >= 64)
            {
                peak = Math.Max(peak, a);
            }
            else if (peak > 0)
            {
                peaks.Add(peak);
                peak = 0;
            }
        }

        Assert.Equal(3, peaks.Count);
        Assert.Equal(255, peaks[0]);
        Assert.InRange(peaks[1], 126, 130);
        Assert.Equal(255, peaks[2]);
    }

    [Fact]
    public void ACharacterWithItsOwnDxIsNotHeldBackByTheLigatureItWouldJoin()
    {
        // The font ties f and i into one glyph. Positioned apart they cannot share it, so the dx on the i takes effect.
        string Svg(string text) => $"<svg {Ns} viewBox='0 0 100 50' width='100' height='50'><text x='5' y='35' font-size='30' font-family='Noto Sans'>{text}</text></svg>";
        int Right(string text)
        {
            Rgba32[] px = RenderWith(Svg(text), "NotoSans-Shaping-Subset.ttf");
            int x1 = -1;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].A > 128)
                {
                    x1 = Math.Max(x1, i % 100);
                }
            }

            return x1;
        }

        int joined = Right("fi");
        Assert.InRange(Right("f<tspan dx='10'>i</tspan>") - joined, 9, 11);
        Assert.InRange(Right("<tspan rotate='0'>f</tspan><tspan rotate='5'>i</tspan>"), joined - 1, joined + 4);
    }

    [Fact]
    public void NestedTspanOpacityCountsAgainstTheLayerDepthLimit()
    {
        var limits = new SvgLimits { MaxLayerDepth = 3 };
        const string Nested = "<text y='30'><tspan opacity='.9'>a<tspan opacity='.9'>b<tspan opacity='.9'>c<tspan opacity='.9'>d<tspan opacity='.9'>e</tspan></tspan></tspan></tspan></tspan></text>";
        Assert.Throws<ImageLimitException>(() => Render(Nested, limits));
        Render(Nested);
    }
}
