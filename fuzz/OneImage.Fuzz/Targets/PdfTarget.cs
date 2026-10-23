using System;
using OneImage;
using OneImage.Pdf;

/// <summary>
/// The PDF renderer on arbitrary files: parse, interpret and draw. Pages are drawn at 72 dpi with small limits, once through the
/// render-at-size path (a 40 x 40 resize), once as a corner region and once as an interior window, over up to two pages.
/// Only the typed image exceptions may leave the load and draw calls.
/// </summary>
internal static class PdfTarget
{
    private static readonly PdfRenderOptions RenderOptions = new()
    {
        Dpi = 72,
        PageCount = 2,
        Limits = new PdfRenderLimits
        {
            MaxFileBytes = 1 << 20,
            MaxImagePixels = 1 << 22,
            MaxNesting = 8,
            MaxClipDepth = 256,
            MaxPatternTileSide = 256,
            MaxPatternCopies = 16,
            MaxCachedPixels = 1 << 22,
        },
    };

    private static readonly LoadOptions Options = new()
    {
        Configuration = new ImageConfiguration().Add(new PdfFormat(RenderOptions)),
        Limits = Targets.Limits,
    };

    public static void Run(ReadOnlySpan<byte> data)
    {
        byte[] input = data.ToArray();
        Targets.Guard(() =>
        {
            Image image = Image.Load(input, Options);
            _ = image.Resize(40, 40, Fit.Stretch).ToArray<Rgba32>(maxDegree: 1);

            int w = Math.Min(image.Width, 96);
            int h = Math.Min(image.Height, 96);
            var corner = new Rgba32[w * h];
            image.CopyPixelsTo<Rgba32>(corner, new ImageRect(0, 0, w, h), maxDegree: 1);

            int ww = Math.Min(image.Width, 130);
            int wh = Math.Min(image.Height, 130);
            var window = new Rgba32[ww * wh];
            image.CopyPixelsTo<Rgba32>(window, new ImageRect((image.Width - ww) / 2, (image.Height - wh) / 2, ww, wh), maxDegree: 1);
        });
    }
}
