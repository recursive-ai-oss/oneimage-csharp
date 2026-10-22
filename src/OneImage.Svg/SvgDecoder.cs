using OneImage.Svg.Xml;

namespace OneImage.Svg;

internal sealed class SvgDecoder : IImageDecoder
{
    private readonly SvgFormat _format;

    public SvgDecoder(SvgFormat format) => _format = format;

    public DecoderCapabilities Capabilities => DecoderCapabilities.None;

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        SvgLimits svg = _format.Limits;
        byte[] head = SvgSource.Read(source, svg, limits, svg.MaxRootTagScanBytes, out _);
        XmlElement root = XmlParser.ParseRootStart(head, svg);
        (double w, double h) = SvgSize.Natural(root);
        (int pw, int ph) = SvgSize.ToPixels(w, h, limits);
        return new ImageInfo(_format, [new FrameInfo(pw, ph, PixelFormat.Rgba8)]);
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken) =>
        throw new ImageOperationException("Rendering SVG is not available yet.");
}
