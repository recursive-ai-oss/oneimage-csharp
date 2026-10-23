using System.Runtime.ExceptionServices;
using OneImage.Svg.Dom;
using OneImage.Svg.Render;
using OneImage.Svg.Xml;

namespace OneImage.Svg;

internal sealed class SvgDecoder : IImageDecoder
{
    private readonly SvgFormat _format;

    public SvgDecoder(SvgFormat format) => _format = format;

    // Tiles are drawn on demand at any size; only the shapes under a region are drawn for it.
    public DecoderCapabilities Capabilities => DecoderCapabilities.RandomAccess | DecoderCapabilities.RenderAtSize;

    /// <summary>What a decoded image remembers between tiles: the render tree, built once from the first stream that asks for pixels.</summary>
    private sealed class State
    {
        private readonly object _gate = new();
        private SvgScene? _scene;
        private Exception? _failure;

        public State(SvgScene? scene) => _scene = scene;

        public SvgScene Scene(Func<SvgScene> build)
        {
            lock (_gate)
            {
                if (_scene is { } ready)
                {
                    return ready;
                }

                if (_failure is { } failed)
                {
                    ExceptionDispatchInfo.Capture(failed).Throw();
                }

                try
                {
                    return _scene = build();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A hostile or broken document costs its work once, not once per tile.
                    _failure = ex;
                    throw;
                }
            }
        }
    }

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        SvgLimits svg = _format.Limits;
        byte[] head = SvgSource.Read(source, svg, limits, svg.MaxRootTagScanBytes, out _);
        XmlElement root = XmlParser.ParseRootStart(head, svg);
        if (root.LocalName != "svg" || root.Namespace != SvgSize.SvgNamespace)
        {
            throw new ImageFormatException("The root element is not an SVG element.");
        }

        (double? w, double? h) = SvgSize.Partial(root.GetAttribute(null, "width"), root.GetAttribute(null, "height"), root.GetAttribute(null, "viewBox"));
        SvgScene? scene = null;
        if (w is null || h is null)
        {
            // Nothing in the root fixes the size: it is the size of the content, which needs the whole document.
            scene = BuildScene(source, limits, CancellationToken.None);
            if (!scene.HasContent)
            {
                throw new ImageFormatException("The SVG has no usable width and height or viewBox, and no content to measure.");
            }

            (w, h) = (w ?? scene.Width, h ?? scene.Height);
        }

        (int pw, int ph) = SvgSize.ToPixels(w.Value, h.Value, limits);
        return new ImageInfo(_format, [new FrameInfo(pw, ph, PixelFormat.Rgba8)], 0, null, new State(scene));
    }

    public void DecodeFrame(Stream source, ImageInfo info, int frameIndex, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken cancellationToken)
    {
        FrameInfo frame = info.Frames[frameIndex];
        int tw = request.TargetWidth > 0 ? request.TargetWidth : frame.Width;
        int th = request.TargetHeight > 0 ? request.TargetHeight : frame.Height;
        limits.CheckFrame(tw, th);
        ImageRect region = request.Region;
        if (region.X < 0 || region.Y < 0 || region.Right > tw || region.Bottom > th || sink.Width != region.Width || sink.Height != region.Height)
        {
            throw new ImageOperationException("The requested SVG region lies outside the target size.");
        }

        var state = (State)info.DecoderState!;
        SvgScene scene = state.Scene(() => BuildScene(source, limits, cancellationToken));
        SceneRenderer.Render(scene, tw, th, region, sink, _format.Limits, cancellationToken);
    }

    private SvgScene BuildScene(Stream source, ImageLimits limits, CancellationToken cancellationToken)
    {
        SvgLimits svg = _format.Limits;
        source.Position = 0;
        byte[] bytes = SvgSource.Read(source, svg, limits, 0, out _);
        XmlDocument xml = XmlParser.Parse(bytes, svg);
        SvgDocument doc = SvgDocument.Build(xml, svg);
        return SceneBuilder.Build(doc, svg, cancellationToken, limits, _format.Languages, _format.Fonts);
    }
}
