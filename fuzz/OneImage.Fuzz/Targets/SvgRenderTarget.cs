using System;
using OneImage;
using OneImage.Svg;

/// <summary>
/// The SVG renderer on arbitrary documents: parse, style, build the render tree (use, symbol, nested svg, limits) and draw.
/// Output sizes are capped small so every iteration is cheap: the whole image is drawn at 40 x 24 pixels through the
/// render-at-size path, and one 64 x 64 corner and one 32 x 32 window of the natural-size render are drawn as regions.
/// </summary>
internal static class SvgRenderTarget
{
    private static readonly SvgLimits Limits = new()
    {
        MaxDocumentBytes = 1 << 20,
        MaxElements = 20_000,
        MaxDepth = 64,
        MaxAttributesPerElement = 64,
        MaxNamespaceBindings = 32,
        MaxRootTagScanBytes = 1 << 16,
        MaxInstances = 20_000,
        MaxReferenceDepth = 8,
        MaxLayerDepth = 8,
        MaxPatternTilePixels = 1 << 18,
        MaxCssBytes = 1 << 16,
        MaxCssRules = 500,
        MaxCssMatchSteps = 1_000_000,
        MaxImageBytes = 1 << 16,
        MaxTotalImageBytes = 1 << 18,
        MaxImageDepth = 2,
        MaxFilterPixels = 1 << 20,
        MaxFilterPrimitives = 32,
        MaxTextCharacters = 20_000,
    };

    // The committed Noto subsets, so text elements lay out and draw instead of being left out for want of a font.
    private static SvgFontOptions Fonts()
    {
        var options = new SvgFontOptions { UseSystemFonts = false };
        if (Environment.GetEnvironmentVariable("ONEIMAGE_FUZZ_FONT_DIR") is { Length: > 0 } dir && System.IO.Directory.Exists(dir))
        {
            foreach (string file in System.IO.Directory.GetFiles(dir, "NotoSans*.ttf"))
            {
                try
                {
                    options.AddFont(file);
                }
                catch (OneImage.Text.FontFormatException)
                {
                }
            }

            options.SetGenericFamily("sans-serif", "Noto Sans");
        }

        return options;
    }

    private static readonly LoadOptions Options = new()
    {
        Configuration = new ImageConfiguration().Add(new SvgFormat(Limits, fonts: Fonts())),
        Limits = Targets.Limits,
    };

    public static void Run(ReadOnlySpan<byte> data)
    {
        byte[] input = data.ToArray();
        Targets.Guard(() =>
        {
            Image image = Image.Load(input, Options);
            _ = image.Resize(40, 24, Fit.Stretch).ToArray<Rgba32>();

            int w = Math.Min(image.Width, 64);
            int h = Math.Min(image.Height, 64);
            var corner = new Rgba32[w * h];
            image.CopyPixelsTo<Rgba32>(corner, new ImageRect(0, 0, w, h));

            int ww = Math.Min(image.Width, 32);
            int wh = Math.Min(image.Height, 32);
            var window = new Rgba32[ww * wh];
            image.CopyPixelsTo<Rgba32>(window, new ImageRect(image.Width - ww, image.Height - wh, ww, wh));
        });
    }
}
