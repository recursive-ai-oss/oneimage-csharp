using System.IO.Compression;
using System.Text;
using OneImage.Svg;

namespace OneImage.Svg.Tests;

public class SvgFormatTests
{
    private static readonly ImageConfiguration Config = new ImageConfiguration().Add(SvgFormat.Instance);
    private static readonly LoadOptions Options = new() { Configuration = Config };

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gz.Write(data);
        }

        return ms.ToArray();
    }

    private static ImageInfo Identify(string svg) => Image.Identify(Utf8(svg), Options);

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='20' height='10'/>")]
    [InlineData("<?xml version='1.0' encoding='UTF-8' standalone='no'?>\n<!-- hi -->\n<svg xmlns='http://www.w3.org/2000/svg' width='20' height='10'/>")]
    [InlineData("\uFEFF  <svg xmlns='http://www.w3.org/2000/svg' width='20' height='10'/>")]
    [InlineData("<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'x.dtd' [ <!ENTITY a '>'> ]><svg xmlns='http://www.w3.org/2000/svg' width='20' height='10'/>")]
    [InlineData("<s:svg xmlns:s='http://www.w3.org/2000/svg' width='20' height='10'/>")]
    [InlineData("<svg\nxmlns='http://www.w3.org/2000/svg' width='20' height='10'></svg>")]
    public void DetectsSvgAndReadsItsSize(string svg)
    {
        // The DOCTYPE case is detected, but the parser refuses it when the root element is read.
        if (svg.Contains("DOCTYPE", StringComparison.Ordinal))
        {
            Assert.Same(SvgFormat.Instance, Config.Detect(Utf8(svg)));
            Assert.Throws<ImageFormatException>(() => Identify(svg));
            return;
        }

        ImageInfo info = Identify(svg);
        Assert.Same(SvgFormat.Instance, info.Format);
        Assert.Equal((20, 10), (info.Width, info.Height));
        Assert.Equal(PixelFormat.Rgba8, info.Frames[0].Format);
    }

    [Fact]
    public void DetectsUtf16Documents()
    {
        byte[] data = [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='3' height='4'/>")];
        ImageInfo info = Image.Identify(data, Options);
        Assert.Equal((3, 4), (info.Width, info.Height));
    }

    [Theory]
    [InlineData("<html><svg/></html>")]
    [InlineData("<svgx/>")]
    [InlineData("<!-- <svg/> -->")]
    [InlineData("<a><svg/></a>")]
    [InlineData("hello <svg/>")]
    [InlineData("")]
    public void DoesNotClaimOtherData(string text)
    {
        Assert.Null(Config.Detect(Utf8(text)));
    }

    [Fact]
    public void DoesNotClaimBinaryFormatsOrOtherGzipData()
    {
        Assert.Null(Config.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]));
        Assert.Null(Config.Detect(Gzip(Utf8("just some text, not markup"))));
        Assert.Null(Config.Detect([0x1F, 0x8B, 0x08, 0xFF, 0, 0, 0, 0]));
    }

    [Fact]
    public void RegisteredNextToTheBuiltInFormatsItLeavesThemAlone()
    {
        var config = new ImageConfiguration().Add(SvgFormat.Instance);
        Assert.Equal("svg", config.FindByExtension("logo.svg")?.Name);
        Assert.Equal("svg", config.FindByExtension("logo.svgz")?.Name);
        Assert.Equal("image/svg+xml", SvgFormat.Instance.MimeType);
    }

    [Fact]
    public void ReadsGzipCompressedSvg()
    {
        byte[] gz = Gzip(Utf8("<?xml version='1.0'?><svg xmlns='http://www.w3.org/2000/svg' width='64' height='32'>" + new string(' ', 5000) + "</svg>"));
        ImageInfo info = Image.Identify(gz, Options);
        Assert.Equal((64, 32), (info.Width, info.Height));
    }

    [Fact]
    public void GzipExpansionBeyondTheLimitsIsRefused()
    {
        // 20 MiB of spaces compress to a few KiB, far past the 1 MiB document cap used here.
        byte[] gz = Gzip(Utf8("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>" + new string(' ', 20 << 20) + "</svg>"));
        var tight = new SvgFormat(new SvgLimits { MaxDocumentBytes = 1 << 20 });
        var config = new ImageConfiguration().Add(tight);
        var opts = new LoadOptions { Configuration = config };
        // Only the start tag is needed for the size, so identify still works...
        Assert.Equal(1, Image.Identify(gz, opts).Width);
        // ...but reading the whole document (what rendering does) hits the cap.
        Assert.Throws<ImageLimitException>(() => SvgSource.Read(new MemoryStream(gz), tight.Limits, ImageLimits.Default, 0, out _));
    }

    [Fact]
    public void OversizedPlainDocumentsAreRefusedBeforeParsing()
    {
        var tight = new SvgFormat(new SvgLimits { MaxDocumentBytes = 1000 });
        byte[] big = Utf8("<svg xmlns='http://www.w3.org/2000/svg'>" + new string(' ', 5000) + "</svg>");
        Assert.Throws<ImageLimitException>(() => SvgSource.Read(new MemoryStream(big), tight.Limits, ImageLimits.Default, 0, out _));
    }

    [Fact]
    public void CorruptGzipFailsWithAFormatError()
    {
        byte[] gz = Gzip(Utf8("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>"));
        gz[^6] ^= 0xFF; // CRC-32
        Assert.Throws<ImageFormatException>(() => SvgSource.Read(new MemoryStream(gz), SvgLimits.Default, ImageLimits.Default, 0, out _));
        Assert.Throws<ImageFormatException>(() => SvgSource.Read(new MemoryStream(gz[..^10]), SvgLimits.Default, ImageLimits.Default, 0, out _));
    }

    private const string Ns = "xmlns='http://www.w3.org/2000/svg'";

    [Theory]
    [InlineData("width='96' height='48'", 96, 48)]
    [InlineData("width='1in' height='0.5in'", 96, 48)]
    [InlineData("width='25.4mm' height='2.54cm'", 96, 96)]
    [InlineData("width='72pt' height='6pc'", 96, 96)]
    [InlineData("width='10.2' height='3.0001'", 11, 4)]
    [InlineData("width='1e2' height='2em'", 100, 32)]
    [InlineData("viewBox='0 0 300 150'", 300, 150)]
    [InlineData("viewBox='10,20,30.5,40'", 31, 40)]
    [InlineData("width='100%' height='100%' viewBox='0 0 50 60'", 50, 60)]
    [InlineData("width='200' viewBox='0 0 50 100'", 200, 400)]
    [InlineData("height='200' viewBox='0 0 50 100'", 100, 200)]
    [InlineData("width='80' height='40' viewBox='0 0 1 1'", 80, 40)]
    public void NaturalSize(string attributes, int width, int height)
    {
        ImageInfo info = Identify($"<svg {Ns} {attributes}/>");
        Assert.Equal((width, height), (info.Width, info.Height));
    }

    [Theory]
    [InlineData("width='0' height='5'")]
    [InlineData("width='-3' height='5'")]
    [InlineData("width='abc' height='5'")]
    [InlineData("width='5' height='5qq'")]
    [InlineData("width='5'")]
    [InlineData("")]
    [InlineData("viewBox='0 0 0 5'")]
    [InlineData("width='1e999' height='5'")]
    public void UnusableSizesFailWithAFormatError(string attributes)
    {
        Assert.Throws<ImageFormatException>(() => Identify($"<svg {Ns} {attributes}/>"));
    }

    [Fact]
    public void RootMustBeInTheSvgNamespace()
    {
        Assert.Throws<ImageFormatException>(() => Identify("<svg width='5' height='5'/>"));
        Assert.Throws<ImageFormatException>(() => Identify("<svg xmlns='urn:other' width='5' height='5'/>"));
    }

    [Fact]
    public void PixelLimitsApplyToTheNaturalSize()
    {
        string svg = $"<svg {Ns} width='100000' height='100000'/>";
        Assert.Throws<ImageLimitException>(() => Identify(svg));
        var opts = new LoadOptions { Configuration = Config, Limits = new ImageLimits { MaxPixelsPerFrame = 100, MaxDimension = 50 } };
        Assert.Throws<ImageLimitException>(() => Image.Identify(Utf8($"<svg {Ns} width='60' height='1'/>"), opts));
        Assert.Throws<ImageLimitException>(() => Identify($"<svg {Ns} width='1e12' height='1'/>"));
    }

    [Fact]
    public void SizeReadingDoesNotParseTheRestOfTheDocument()
    {
        // A broken body is reported when pixels are pulled, not at identify.
        Assert.Equal(7, Identify($"<svg {Ns} width='7' height='7'><g><oops></g></svg>").Width);
    }

    [Fact]
    public void RenderingIsNotAvailableYet()
    {
        Image img = Image.Load(Utf8($"<svg {Ns} width='7' height='7'/>"), Options);
        Assert.Equal((7, 7), (img.Width, img.Height));
        Assert.Throws<ImageOperationException>(() => img.ToArray<Rgba32>());
    }
}
