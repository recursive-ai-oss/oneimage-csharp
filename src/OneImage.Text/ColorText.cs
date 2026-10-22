using System.Numerics;
using OneImage.Drawing;
using OneImage.Text.Fonts;
using OneImage.Text.Layout;
using Path = OneImage.Drawing.Path;

namespace OneImage.Text;

/// <summary>One fill of a color text run: a path painted with one palette color, or with the caller's brush.</summary>
internal sealed class ColorFill
{
    public required Path Path;
    public required Rgba32 Color;
    public required bool Foreground;
}

/// <summary>
/// Splits a laid-out text into its plain glyphs and the layers of its color glyphs. Layer 0 of every color glyph is painted
/// first, then layer 1, and so on, with layers of equal index and color merged into one path. Glyphs that overlap each other
/// in different layers may therefore stack differently from strict glyph-by-glyph painting.
/// </summary>
internal sealed class ColorText
{
    private ColorText(Path plain, List<ColorFill> fills)
    {
        Plain = plain;
        Fills = fills;
    }

    /// <summary>The glyphs that are not color glyphs, as one path.</summary>
    public Path Plain { get; }

    /// <summary>The layer fills in painting order.</summary>
    public List<ColorFill> Fills { get; }

    /// <summary>
    /// Lays the text out and splits it; null when color is off, the font has no color glyphs or none of them occurs in the text,
    /// in which case the caller draws the ordinary single path.
    /// </summary>
    public static ColorText? Create(string text, TextOptions options)
    {
        if ((options.ColorFontSupport & ColorFontSupport.MicrosoftColrFormat) == 0 || options.Font.Face.Data.Color is null)
        {
            return null;
        }

        TextLayout layout = TextLayout.Create(text, options);
        FontFace face = layout.Face;
        ColorGlyphs colors = face.Color!;
        float scale = layout.Scale;
        var plain = new PathBuilder();
        var layers = new List<List<(Rgba32 Color, bool Foreground, PathBuilder Builder)>>();
        bool any = false;
        for (int i = 0; i < layout.Lines.Count; i++)
        {
            LayoutLine l = layout.Lines[i];
            for (int k = l.GlyphStart; k < l.GlyphEnd; k++)
            {
                ShapedGlyph g = layout.Glyphs[k];
                if (g.IsSpace)
                {
                    continue;
                }

                var m = new Matrix3x2(scale, 0, 0, -scale, l.Left + ((layout.PenX[k] + g.XOffset) * scale), l.Baseline - (g.YOffset * scale));
                if (!colors.TryGetLayers(g.GlyphId, out int first, out int count))
                {
                    Outline o = face.GetOutline(g.GlyphId);
                    if (!o.IsEmpty)
                    {
                        GlyphPaths.Append(plain, o, m);
                    }

                    continue;
                }

                any = true;
                for (int j = 0; j < count; j++)
                {
                    (int layerGlyph, int paletteIndex) = colors.GetLayer(first + j);
                    bool foreground = paletteIndex == ColorGlyphs.ForegroundIndex;
                    Rgba32 color = default;
                    if (!foreground)
                    {
                        // A palette entry that does not exist leaves the layer out.
                        if (colors.GetColor(options.ColorPaletteIndex, paletteIndex) is not { } c)
                        {
                            continue;
                        }

                        color = c;
                    }

                    Outline lo = face.GetOutline(layerGlyph);
                    if (lo.IsEmpty)
                    {
                        continue;
                    }

                    while (layers.Count <= j)
                    {
                        layers.Add([]);
                    }

                    List<(Rgba32 Color, bool Foreground, PathBuilder Builder)> set = layers[j];
                    int at = set.FindIndex(e => e.Foreground == foreground && e.Color.Equals(color));
                    if (at < 0)
                    {
                        set.Add((color, foreground, new PathBuilder()));
                        at = set.Count - 1;
                    }

                    GlyphPaths.Append(set[at].Builder, lo, m);
                }
            }
        }

        if (!any)
        {
            return null;
        }

        var fills = new List<ColorFill>();
        foreach (var set in layers)
        {
            foreach ((Rgba32 color, bool foreground, PathBuilder builder) in set)
            {
                fills.Add(new ColorFill { Path = builder.Build(), Color = color, Foreground = foreground });
            }
        }

        return new ColorText(plain.Build(), fills);
    }
}
