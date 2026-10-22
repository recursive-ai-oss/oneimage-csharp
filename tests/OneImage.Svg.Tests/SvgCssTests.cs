using System.Text;
using OneImage.Svg;

namespace OneImage.Svg.Tests;

/// <summary>Style sheets: selectors, the cascade (specificity, order, <c>!important</c>, the <c>style</c> attribute), inheritance keywords and the limits.</summary>
public class SvgCssTests
{
    private const string Ns = SvgRenderTests.Ns;

    // A row of ten-pixel cells; cell i holds a rect with id r{i}. Returns the center color of each cell.
    private static Rgba32[] Cells(string head, string body, int count, SvgLimits? limits = null)
    {
        string svg = $"<svg {Ns} width='{count * 10}' height='10'>{head}{body}</svg>";
        var options = limits is null ? SvgRenderTests.Options : new LoadOptions { Configuration = new ImageConfiguration().Add(new SvgFormat(limits)) };
        Rgba32[] px = Image.Load(Encoding.UTF8.GetBytes(svg), options).ToArray<Rgba32>();
        var result = new Rgba32[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = px[(5 * count * 10) + (i * 10) + 5];
        }

        return result;
    }

    private static string Rects(int count, string extra = "") =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"<rect id='r{i}' x='{i * 10}' width='10' height='10' {extra}/>"));

    private static readonly Rgba32 Green = new(0, 128, 0, 255);
    private static readonly Rgba32 Red = new(255, 0, 0, 255);
    private static readonly Rgba32 Blue = new(0, 0, 255, 255);
    private static readonly Rgba32 Black = new(0, 0, 0, 255);

    [Fact]
    public void TypeClassIdAndUniversalSelectorsMatch()
    {
        string css = "<style>rect { fill: red } .g { fill: green } #r2 { fill: blue } circle { fill: green }</style>";
        string body = "<rect id='r0' x='0' width='10' height='10'/><rect id='r1' class='x g' x='10' width='10' height='10'/><rect id='r2' class='g' x='20' width='10' height='10'/>";
        Assert.Equal([Red, Green, Blue], Cells(css, body, 3));
        Assert.Equal([Green, Green, Green], Cells("<style>* { fill: green }</style>", Rects(3), 3));
    }

    [Fact]
    public void AttributeSelectorsMatchRawAttributes()
    {
        string css = "<style>[data-k] { fill: red } [data-k=a] { fill: green } [data-k^=b] { fill: blue } [data-k$=z] { fill: black } [data-k~=w] { fill: green } [lang|=en] { fill: green } [data-k*=\"q q\"] { fill: red }</style>";
        string body =
            "<rect x='0' width='10' height='10' data-k='x'/>" +
            "<rect x='10' width='10' height='10' data-k='a'/>" +
            "<rect x='20' width='10' height='10' data-k='bcd'/>" +
            "<rect x='30' width='10' height='10' data-k='xyz'/>" +
            "<rect x='40' width='10' height='10' data-k='u w v'/>" +
            "<rect x='50' width='10' height='10' lang='en-GB'/>";
        Assert.Equal([Red, Green, Blue, Black, Green, Green], Cells(css, body, 6));

        // Attribute selectors on a sibling or an ancestor see that element's attributes as well.
        Assert.Equal(Green, Cells("<style>[data-k] ~ rect { fill: green }</style>", "<rect width='10' height='10' data-k='1'/><rect x='10' width='10' height='10'/>", 2)[1]);
        Assert.Equal([Green], Cells("<style>g[data-k] > rect { fill: green }</style>", "<g data-k='1'><rect width='10' height='10'/></g>", 1));

        // A presentation attribute is an attribute too: the selector sees it.
        Assert.Equal([Green], Cells("<style>[fill] { fill: green }</style>", "<rect width='10' height='10' fill='red'/>", 1));
    }

    [Fact]
    public void CombinatorsRelateElements()
    {
        string css = "<style>g > rect { fill: green } g rect.deep { fill: blue } rect + rect { fill: red } rect ~ circle { fill: black }</style>";
        string body =
            "<g><rect x='0' width='10' height='10'/></g>" +                         // child of g
            "<g><g><rect class='deep' x='10' width='10' height='10'/></g></g>" +    // descendant only (g > rect also matches; blue wins on specificity)
            "<rect x='20' width='10' height='10'/>" +                              // preceded by a g, not a rect
            "<rect x='30' width='10' height='10'/>" +                              // adjacent to a rect
            "<circle cx='45' cy='5' r='5'/>";                                      // after a rect somewhere before
        Rgba32[] c = Cells(css, body, 5);
        Assert.Equal(Green, c[0]);
        Assert.Equal(Blue, c[1]);
        Assert.Equal(Black, c[2]); // default fill (no rule matches the lone rect)
        Assert.Equal(Red, c[3]);
        Assert.Equal(Black, c[4]); // circle: general sibling rule gives black, same as the default
    }

    [Fact]
    public void StructuralPseudoClassesCount()
    {
        string css = "<style>rect:first-child { fill: green } rect:last-child { fill: blue } rect:nth-child(2n+2) { fill: red } rect:only-child { fill: black }</style>";
        // Five siblings: 1 first, 2 and 4 even, 5 last.
        Assert.Equal([Green, Red, Black, Red, Blue], Cells(string.Empty, "<g>" + Rects(5) + "</g>" + css, 5));
        Assert.Equal([Black], Cells(string.Empty, "<g>" + Rects(1) + "</g><style>rect:only-child { fill: black } rect { fill: red }</style>", 1));

        string types = "<style>rect:nth-of-type(odd) { fill: green } circle:first-of-type { fill: blue } rect:not(:first-of-type) { fill: red }</style>";
        string body = "<rect x='0' width='10' height='10'/><circle cx='15' cy='5' r='5'/><rect x='20' width='10' height='10'/><circle cx='35' cy='5' r='5'/>";
        Assert.Equal([Green, Blue, Red, Black], Cells(string.Empty, body + types, 4));
    }

    [Fact]
    public void SpecificityBeatsSourceOrderAndOrderBreaksTies()
    {
        Assert.Equal([Green], Cells("<style>#a { fill: green } rect.c { fill: red } rect { fill: red }</style>", "<rect id='a' class='c' width='10' height='10'/>", 1));
        Assert.Equal([Blue], Cells("<style>rect { fill: red } rect { fill: blue }</style>", Rects(1), 1));
        Assert.Equal([Green], Cells("<style>g#g1 * g * * rect { fill: green } g#g1 * * rect { fill: red }</style>", "<g id='g1'><g><g><g><g><rect width='10' height='10'/></g></g></g></g></g>", 1));
    }

    [Fact]
    public void CascadeOrdersAttributesRulesStyleAttributeAndImportant()
    {
        // presentation attribute < rule < style attribute
        Assert.Equal([Blue], Cells("<style>rect { fill: green }</style>", "<rect width='10' height='10' fill='red' style='fill: blue'/>", 1));
        Assert.Equal([Green], Cells("<style>rect { fill: green }</style>", "<rect width='10' height='10' fill='red'/>", 1));

        // !important in a rule beats the style attribute; !important in the attribute beats an important rule.
        Assert.Equal([Green], Cells("<style>rect { fill: green !important }</style>", "<rect width='10' height='10' style='fill: blue'/>", 1));
        Assert.Equal([Blue], Cells("<style>rect { fill: green !important }</style>", "<rect width='10' height='10' style='fill: blue !important'/>", 1));
        Assert.Equal([Green], Cells("<style>#r0 { fill: green !important } #r0 { fill: red }</style>", Rects(1), 1));
    }

    [Fact]
    public void InheritInitialAndUnsetKeywords()
    {
        string body = "<g fill='green'><rect x='0' width='10' height='10' style='fill: inherit'/><rect x='10' width='10' height='10' style='fill: initial'/><rect x='20' width='10' height='10' style='fill: unset'/>" +
                      "<g fill-opacity='0.5'><rect x='30' width='10' height='10' style='fill-opacity: initial'/></g></g>";
        Rgba32[] c = Cells(string.Empty, body, 4);
        Assert.Equal(Green, c[0]);
        Assert.Equal(Black, c[1]); // initial fill is black
        Assert.Equal(Green, c[2]); // fill is inherited, so unset means inherit
        Assert.Equal(255, c[3].A);

        // opacity is not inherited: unset means initial, and inherit takes the parent's own value.
        string op = "<g opacity='0.5'><rect width='10' height='10' style='opacity: inherit'/></g>";
        Assert.InRange(Cells(string.Empty, op, 1)[0].A, 62, 65); // 0.5 * 0.5
    }

    [Fact]
    public void RulesApplyBeforeUseAndInheritThroughIt()
    {
        string css = "<style>#t { fill: green }</style>";
        string body = "<defs><rect id='t' width='10' height='10'/></defs><g fill='red'><use xlink:href='#t'/></g>";
        Assert.Equal([Green], Cells(css, body, 1));
        // Without a rule the instance inherits from the use element.
        Assert.Equal([Red], Cells(string.Empty, body, 1));
    }

    [Fact]
    public void SheetsAreReadFromCdataAfterTheirUseAndSkipUnsupportedParts()
    {
        string body = Rects(1, "class='fil'");
        Assert.Equal([Green], Cells(string.Empty, body + "<style><![CDATA[ .fil { fill: green } ]]></style>", 1));
        Assert.Equal([Red], Cells(string.Empty, "<style type='text/x-other'>.fil { fill: green }</style>" + body.Replace("class='fil'", "class='fil' fill='red'"), 1));

        // At-rules are skipped whole, including their blocks; comments and rules with selectors we cannot honor are dropped.
        string css = "<style>@import url(other.css); @media print { rect { fill: red } } /* .fil { fill: red } */ rect::before { fill: red } rect:hover { fill: red } .fil { fill: green } @font-face { font-family: x }</style>";
        Assert.Equal([Green], Cells(css, body, 1));
    }

    [Fact]
    public void CssNeverFetchesAnything()
    {
        // @import and url() in a style sheet are ignored: no I/O, the rule after them still applies.
        string css = "<style>@import 'file:///etc/passwd'; rect { fill: green; background: url(http://example.com/x.png) }</style>";
        Assert.Equal([Green], Cells(css, Rects(1), 1));
    }

    [Fact]
    public void StyleSheetsCoverMoreThanPresentationAttributes()
    {
        // transform and transform-origin are properties too, so a rule can set them.
        string css = "<style>#r0 { transform: translate(10px, 0) }</style>";
        Rgba32[] c = Cells(css, Rects(2, "fill='green'"), 2);
        Assert.Equal(Green, c[1]); // r0 moved right over r1 (same color), cell 0 is empty
        Assert.Equal(0, c[0].A);
    }

    [Fact]
    public void TooMuchCssFailsClosed()
    {
        var limits = new SvgLimits { MaxCssBytes = 200 };
        string big = $"<style>{new string(' ', 300)} rect {{ fill: green }}</style>";
        Assert.Throws<ImageLimitException>(() => Cells(big, Rects(1), 1, limits));

        // style attributes count too
        Assert.Throws<ImageLimitException>(() => Cells(string.Empty, Rects(1, $"style='fill: green; {new string(' ', 300)}'"), 1, limits));

        var few = new SvgLimits { MaxCssRules = 3 };
        string rules = "<style>a{fill:red} b{fill:red} c{fill:red} d{fill:red}</style>";
        Assert.Throws<ImageLimitException>(() => Cells(rules, Rects(1), 1, few));
        // A selector list counts once per selector.
        Assert.Throws<ImageLimitException>(() => Cells("<style>a,b,c,d{fill:red}</style>", Rects(1), 1, few));
        Cells("<style>a,b,c{fill:red}</style>", Rects(1), 1, few);
    }

    [Fact]
    public void HostileSelectorsAreStoppedByTheStepBudget()
    {
        // 200 nested groups with 300 rects inside: a selector with many descendant steps that never completes would explore
        // combinations of ancestors without end; the budget stops it with a typed error, quickly.
        var sb = new StringBuilder();
        sb.Append($"<svg {Ns} width='10' height='10'><style>z g g g g g g g g g g g g g g g g rect {{ fill: red }}</style>");
        for (int i = 0; i < 200; i++)
        {
            sb.Append("<g>");
        }

        for (int i = 0; i < 300; i++)
        {
            sb.Append("<rect width='1' height='1'/>");
        }

        sb.Append(string.Concat(Enumerable.Repeat("</g>", 200))).Append("</svg>");
        var format = new SvgFormat(new SvgLimits { MaxCssMatchSteps = 5_000_000 });
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(format) };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<ImageLimitException>(() => Image.Load(Encoding.UTF8.GetBytes(sb.ToString()), options).ToArray<Rgba32>());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void IndexedRulesKeepHonestSheetsCheap()
    {
        // 2,000 id rules against 2,000 elements: with the index every element is tested against one rule, not 2,000.
        var sb = new StringBuilder();
        sb.Append($"<svg {Ns} width='10' height='10'><style>");
        for (int i = 0; i < 2000; i++)
        {
            sb.Append($"#e{i} {{ fill: green }}\n");
        }

        sb.Append("</style>");
        for (int i = 0; i < 2000; i++)
        {
            sb.Append($"<rect id='e{i}' width='1' height='1'/>");
        }

        sb.Append("</svg>");
        var format = new SvgFormat(new SvgLimits { MaxCssMatchSteps = 100_000 });
        var options = new LoadOptions { Configuration = new ImageConfiguration().Add(format) };
        _ = Image.Load(Encoding.UTF8.GetBytes(sb.ToString()), options).ToArray<Rgba32>();
    }
}
