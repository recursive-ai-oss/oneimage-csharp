using System;
using System.IO;
using OneImage.Svg;
using OneImage.Svg.Xml;

/// <summary>
/// The SVG package's untrusted-input base: format sniffing (including gzip), the XML parser and the size reading.
/// Limits are small so that every iteration stays cheap and a limit error is an expected outcome.
/// </summary>
internal static class SvgXmlTarget
{
    private static readonly SvgLimits Limits = new()
    {
        MaxDocumentBytes = 1 << 20,
        MaxElements = 20_000,
        MaxDepth = 64,
        MaxAttributesPerElement = 64,
        MaxNamespaceBindings = 32,
        MaxRootTagScanBytes = 1 << 16,
    };

    private static readonly SvgFormat Format = new(Limits);

    public static void Run(ReadOnlySpan<byte> data)
    {
        byte[] input = data.ToArray();
        _ = Format.Matches(input.AsSpan(0, Math.Min(input.Length, Format.HeaderLength)));
        Targets.Guard(() =>
        {
            XmlDocument doc = XmlParser.Parse(input, Limits);
            if (doc.ElementCount > Limits.MaxElements)
            {
                throw new InvalidOperationException("element limit not enforced");
            }
        });
        Targets.Guard(() =>
        {
            using var ms = new MemoryStream(input);
            Format.Decoder!.ReadInfo(ms, Targets.Limits);
        });
    }
}
