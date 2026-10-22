using System.Text;
using OneImage.Svg;
using OneImage.Svg.Xml;

namespace OneImage.Svg.Tests;

public class XmlParserTests
{
    private static XmlDocument Parse(string xml, SvgLimits? limits = null) =>
        XmlParser.Parse(Encoding.UTF8.GetBytes(xml), limits ?? SvgLimits.Default);

    [Fact]
    public void ParsesElementsAttributesAndText()
    {
        XmlDocument doc = Parse("<?xml version=\"1.0\"?>\n<!-- c --><a x=\"1\" y='two &amp; &#x41;&#66;'><b/>t1<![CDATA[<raw>]]>t2<c>z</c></a>\n<!-- end -->");
        XmlElement a = doc.Root;
        Assert.Equal("a", a.LocalName);
        Assert.Equal("1", a.GetAttribute(null, "x"));
        Assert.Equal("two & AB", a.GetAttribute(null, "y"));
        Assert.Equal(3, a.Children.Count);
        Assert.Equal("b", ((XmlElement)a.Children[0]).LocalName);
        Assert.Equal("t1<raw>t2", ((XmlText)a.Children[1]).Value);
        Assert.Same(a, a.Children[0].Parent);
        Assert.Equal(3, doc.ElementCount);
    }

    [Fact]
    public void ResolvesNamespacesAndPrefixes()
    {
        XmlDocument doc = Parse("<s:svg xmlns:s='urn:s' xmlns='urn:d' xmlns:x='urn:x'><g x:href='#a' id='1'/><h xmlns=''/></s:svg>");
        Assert.Equal("urn:s", doc.Root.Namespace);
        var g = (XmlElement)doc.Root.Children[0];
        Assert.Equal("urn:d", g.Namespace);
        Assert.Equal("#a", g.GetAttribute("urn:x", "href"));
        Assert.Equal("1", g.GetAttribute(null, "id"));
        Assert.Null(((XmlElement)doc.Root.Children[1]).Namespace);
    }

    [Fact]
    public void NormalizesLineEndsAndAttributeWhitespace()
    {
        XmlDocument doc = Parse("<a v=\"x\ty\r\nz\">l1\r\nl2\rl3</a>");
        Assert.Equal("x y z", doc.Root.GetAttribute(null, "v"));
        Assert.Equal("l1\nl2\nl3", ((XmlText)doc.Root.Children[0]).Value);
    }

    [Fact]
    public void ReadsUtf16AndLatin1DeclaredDocuments()
    {
        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("<a>\u00e9</a>")];
        Assert.Equal("\u00e9", ((XmlText)XmlParser.Parse(utf16, SvgLimits.Default).Root.Children[0]).Value);
        byte[] latin = [.. Encoding.ASCII.GetBytes("<?xml version='1.0' encoding='ISO-8859-1'?><a>"), 0xE9, .. "</a>"u8.ToArray()];
        Assert.Equal("\u00e9", ((XmlText)XmlParser.Parse(latin, SvgLimits.Default).Root.Children[0]).Value);
    }

    [Theory]
    [InlineData("<?xml version='1.0' encoding='windows-1251'?><a/>")]
    [InlineData("<a>\0</a>")]
    [InlineData("<a>&#0;</a>")]
    [InlineData("<a>&#xD800;</a>")]
    [InlineData("<a>&#x110000;</a>")]
    public void RejectsUnsupportedEncodingsAndInvalidCharacters(string xml)
    {
        Assert.Throws<ImageFormatException>(() => Parse(xml));
    }

    [Fact]
    public void RejectsInvalidUtf8()
    {
        Assert.Throws<ImageFormatException>(() => XmlParser.Parse("<a>"u8.ToArray().Concat(new byte[] { 0xFF, 0xFE, 0x41 }).Concat("</a>"u8.ToArray()).ToArray(), SvgLimits.Default));
    }

    [Theory]
    [InlineData("<!DOCTYPE svg><svg/>")]
    [InlineData("<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\"><svg/>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY e \"x\">]><svg>&e;</svg>")]
    [InlineData("<svg><!DOCTYPE svg></svg>")]
    [InlineData("<svg><!ELEMENT a ANY></svg>")]
    public void RejectsDtds(string xml)
    {
        ImageFormatException ex = Assert.Throws<ImageFormatException>(() => Parse(xml));
        Assert.Contains("not allowed", ex.Message);
    }

    [Theory]
    [InlineData("<a>&e;</a>")]
    [InlineData("<a v='&nbsp;'/>")]
    [InlineData("<a>&amp</a>")]
    [InlineData("<a>&#;</a>")]
    [InlineData("<a>&#xZZ;</a>")]
    [InlineData("<a>&#-5;</a>")]
    public void RejectsEntityReferencesBeyondThePredefinedFive(string xml)
    {
        Assert.Throws<ImageFormatException>(() => Parse(xml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("text")]
    [InlineData("<a>")]
    [InlineData("<a></b>")]
    [InlineData("<a/><b/>")]
    [InlineData("<a/>junk")]
    [InlineData("<a x=1/>")]
    [InlineData("<a x='1' x='2'/>")]
    [InlineData("<a x='1'y='2'/>")]
    [InlineData("<a x='<'/>")]
    [InlineData("<p:a/>")]
    [InlineData("<a p:x='1'/>")]
    [InlineData("<a xmlns:p=''/>")]
    [InlineData("<a><!-- unterminated </a>")]
    [InlineData("<a><![CDATA[ unterminated </a>")]
    [InlineData("<a><?pi </a>")]
    [InlineData("<1a/>")]
    [InlineData("<a:b:c xmlns:a='u'/>")]
    public void RejectsMalformedDocuments(string xml)
    {
        Assert.Throws<ImageFormatException>(() => Parse(xml));
    }

    [Fact]
    public void DuplicateAttributesAreDetectedAcrossPrefixesThatNameTheSameNamespace()
    {
        Assert.Throws<ImageFormatException>(() => Parse("<a xmlns:p='u' xmlns:q='u' p:x='1' q:x='2'/>"));
        Parse("<a xmlns:p='u' xmlns:q='v' p:x='1' q:x='2'/>");
    }

    [Fact]
    public void DuplicateAttributesAreDetectedWithManyAttributes()
    {
        string many = string.Join(' ', Enumerable.Range(0, 20).Select(i => $"a{i}='{i}'"));
        Assert.Equal(20, Parse($"<a {many}/>").Root.Attributes.Length);
        Assert.Throws<ImageFormatException>(() => Parse($"<a {many} a3='x'/>"));
        Assert.Throws<ImageFormatException>(() => Parse($"<a {many} a19='x'/>"));
        Assert.Throws<ImageFormatException>(() => Parse("<a a0='' a1='' a2='' a3='' a4='' a5='' a6='' a7='' a0=''/>"));
    }

    [Fact]
    public void ElementLimitIsEnforced()
    {
        var limits = new SvgLimits { MaxElements = 10 };
        Parse("<a>" + string.Concat(Enumerable.Repeat("<b/>", 9)) + "</a>", limits);
        Assert.Throws<ImageLimitException>(() => Parse("<a>" + string.Concat(Enumerable.Repeat("<b/>", 10)) + "</a>", limits));
    }

    [Fact]
    public void DepthLimitIsEnforcedAndDeepInputDoesNotRecurse()
    {
        var limits = new SvgLimits { MaxDepth = 50 };
        Parse(Nested(50), limits);
        Assert.Throws<ImageLimitException>(() => Parse(Nested(51), limits));

        // The same code path with a huge allowance must still finish without exhausting the stack.
        var huge = new SvgLimits { MaxDepth = 400_000, MaxElements = 400_000 };
        XmlDocument doc = Parse(Nested(300_000), huge);
        Assert.Equal(300_000, doc.ElementCount);
    }

    private static string Nested(int depth) => string.Concat(Enumerable.Repeat("<g>", depth)) + string.Concat(Enumerable.Repeat("</g>", depth));

    [Fact]
    public void AttributeNamespaceAndNameLimitsAreEnforced()
    {
        var limits = new SvgLimits { MaxAttributesPerElement = 3, MaxNamespaceBindings = 2, MaxNameLength = 8 };
        Parse("<a a='1' b='2' c='3'/>", limits);
        Assert.Throws<ImageLimitException>(() => Parse("<a a='1' b='2' c='3' d='4'/>", limits));
        Parse("<a xmlns:p='1' xmlns:q='2'/>", limits);
        Assert.Throws<ImageLimitException>(() => Parse("<a xmlns:p='1' xmlns:q='2' xmlns:r='3'/>", limits));
        Assert.Throws<ImageLimitException>(() => Parse("<a xmlns:p='1'><b xmlns:q='2'><c xmlns:r='3'/></b></a>", limits));
        Parse("<a xmlns:p='1'><b xmlns:q='2'/><b xmlns:q='2'/><b xmlns:q='2'/></a>", limits);
        Assert.Throws<ImageLimitException>(() => Parse("<abcdefghi/>", limits));
    }

    [Fact]
    public void RootStartScanReadsOnlyTheStartTag()
    {
        XmlElement root = XmlParser.ParseRootStart("<svg xmlns='http://www.w3.org/2000/svg' width='5'><unclosed"u8, SvgLimits.Default);
        Assert.Equal("5", root.GetAttribute(null, "width"));
        Assert.Empty(root.Children);
        Assert.Throws<ImageFormatException>(() => XmlParser.ParseRootStart("<svg width='5'"u8, SvgLimits.Default));
    }
}
