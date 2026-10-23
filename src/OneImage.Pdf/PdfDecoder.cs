using OneImage.Pdf.Render;
using OnePdf;

namespace OneImage.Pdf;

internal sealed class PdfDecoder : IImageDecoder
{
    private readonly PdfFormat _format;

    public PdfDecoder(PdfFormat format) => _format = format;

    // Cells are drawn on demand at any size; only the part of the page under a cell is replayed for it.
    public DecoderCapabilities Capabilities => DecoderCapabilities.RandomAccess | DecoderCapabilities.RenderAtSize;

    /// <summary>What a loaded document remembers between pulls: the parsed document and, per page, the shared render state.</summary>
    private sealed class State(PdfDocument document, PageRender[] pages)
    {
        public PdfDocument Document { get; } = document;

        public PageRender[] Pages { get; } = pages;
    }

    public ImageInfo ReadInfo(Stream source, ImageLimits limits)
    {
        PdfRenderOptions options = _format.Options;
        byte[] bytes = ReadAll(source, options.Limits.MaxFileBytes);
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(bytes, new PdfOpenOptions { Limits = options.DocumentLimits ?? PdfLimits.Default, Password = options.Password });
        }
        catch (PdfPasswordException ex)
        {
            throw new ImageFormatException("The PDF is encrypted and the password is missing or wrong.", ex);
        }
        catch (PdfException ex)
        {
            throw PdfErrors.Map(ex);
        }

        try
        {
            int total = doc.PageCount;
            if (options.FirstPage >= total)
            {
                throw new ImageFormatException($"The PDF has {total} page(s); page index {options.FirstPage} does not exist.");
            }

            int count = (int)Math.Min(options.PageCount, (long)total - options.FirstPage);
            if (count < 1)
            {
                throw new ImageFormatException("The page selection is empty.");
            }

            if (count > limits.MaxFrames)
            {
                throw new ImageLimitException($"The PDF selection has {count} pages, more than the {limits.MaxFrames} frames allowed; choose a range with PdfRenderOptions.FirstPage and PageCount.");
            }

            var frames = new FrameInfo[count];
            var pages = new PageRender[count];
            for (int i = 0; i < count; i++)
            {
                PdfPage page = doc.Pages[options.FirstPage + i];
                (double wPts, double hPts) = PageGeometry.Size(page, options);
                int w = PageGeometry.ToPixels(wPts, options.Dpi), h = PageGeometry.ToPixels(hPts, options.Dpi);
                limits.CheckFrame(w, h);
                frames[i] = new FrameInfo(w, h, PixelFormat.Rgba8);
                pages[i] = new PageRender(page, options);
            }

            return new ImageInfo(_format, frames, 0, null, new State(doc, pages));
        }
        catch (PdfException ex)
        {
            doc.Dispose();
            throw PdfErrors.Map(ex);
        }
        catch
        {
            doc.Dispose();
            throw;
        }
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
            throw new ImageOperationException("The requested PDF region lies outside the target size.");
        }

        var state = (State)info.DecoderState!;
        CellRenderer.Render(state.Pages[frameIndex], tw, th, region, sink, cancellationToken);
    }

    private static byte[] ReadAll(Stream source, long max)
    {
        source.Position = 0;
        long length = source.Length;
        if (length > max)
        {
            throw new ImageLimitException($"The PDF file is larger than the limit of {max} bytes.");
        }

        var bytes = new byte[length];
        source.ReadExactly(bytes);
        return bytes;
    }
}

/// <summary>The size of a page the way OnePDF reports it: page box limited to the media box, rotation and user unit applied.</summary>
internal static class PageGeometry
{
    public static (double Width, double Height) Size(PdfPage page, PdfRenderOptions o)
    {
        PdfRectangle r = o.Box switch
        {
            PdfPageBox.MediaBox => page.MediaBox,
            PdfPageBox.BleedBox => page.BleedBox,
            PdfPageBox.TrimBox => page.TrimBox,
            PdfPageBox.ArtBox => page.ArtBox,
            _ => page.CropBox,
        };
        PdfRectangle box = r.Intersect(page.MediaBox) ?? page.MediaBox;
        if (box.IsEmpty)
        {
            box = new PdfRectangle(0, 0, 612, 792);
        }

        double unit = page.UserUnit;
        double w = box.Width * unit, h = box.Height * unit;
        return page.Rotate is 90 or 270 ? (h, w) : (w, h);
    }

    public static int ToPixels(double points, double dpi)
    {
        double px = Math.Round(points * dpi / 72.0, MidpointRounding.AwayFromZero);
        return px < 1 ? 1 : px > int.MaxValue / 2 ? int.MaxValue / 2 : (int)px;
    }
}
