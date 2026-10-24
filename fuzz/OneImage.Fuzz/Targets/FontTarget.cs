using System;
using System.Collections.Generic;
using System.Text;
using OneImage;
using OneImage.Text;
using OneImage.Text.Fonts;

/// <summary>
/// Font files of every supported container: parse, read the header tables, walk glyph outlines and kerning, then lay out, measure and
/// draw text with the result. A font that parses has to survive being used; only typed errors may come out.
/// </summary>
internal static class FontTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        Targets.Guard(() =>
        {
            List<FontFace> faces = FontFile.Parse(bytes);
            foreach (FontFace face in faces)
            {
                Walk(face);
            }

            var fonts = new FontCollection();
            foreach (FontFamily family in fonts.AddCollection(bytes))
            {
                Draw(family);
            }
        });
    }

    private static void Walk(FontFace face)
    {
        int glyphs = Math.Min(face.NumGlyphs, 400);
        for (int g = 0; g < glyphs; g++)
        {
            _ = face.GetAdvance(g);
            _ = face.Inner.GetLeftSideBearing(g);
            _ = face.GetOutline(g).TightBox();
            _ = face.Inner.GetKerning(g, (g * 7) % glyphs);

            // Grid fitting runs the font's own instructions: at two sizes, so a font program and a control value program
            // meet different scales, and the COLR version 1 paint graph is flattened for every glyph that has one.
            _ = face.GetHintedOutline(g, 9f).TightBox();
            if (g < 64)
            {
                _ = face.GetHintedOutline(g, 17.5f).TightBox();
            }

            _ = face.Color?.GetProgram(g);
        }

        ShapeGlyphIds(face);

        // Out-of-range ids are legal inputs for the callers (a cmap may point past the end).
        _ = face.GetOutline(face.NumGlyphs + 5);
        _ = face.GetOutline(-1);
        _ = face.GetAdvance(65535);
    }

    private static readonly string[] FeatureTags = ["smcp", "onum", "salt", "aalt", "ss01", "dlig", "frac", "calt", "ccmp", "locl", "kern", "mark", "mkmk", "curs"];

    // Shapes the characters the font covers through every feature it might have, so the lookups meet glyphs they cover. The shaping
    // itself is OneFont's; this checks that what comes back can be used by this package.
    private static void ShapeGlyphIds(FontFace face)
    {
        var text = new StringBuilder();
        for (int cp = 0x20; cp < 0x1F000 && text.Length < 600; cp += cp < 0x500 ? 1 : 11)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            if (face.GetGlyphIndex(cp) != 0)
            {
                text.Append(char.ConvertFromUtf32(cp));
            }
        }

        if (text.Length == 0)
        {
            return;
        }

        foreach (int value in new[] { 1, 2 })
        {
            var options = new OneFont.ShapingOptions();
            foreach (string tag in FeatureTags)
            {
                options.Features.Add(new OneFont.FontFeature(tag, value));
            }

            foreach (string language in new[] { "en", "ro", "sr" })
            {
                options.Language = language;
                try
                {
                    _ = OneFont.TextShaping.ShapeRun(text.ToString(), face.Inner, false, options);
                }
                catch (OneFont.FontFormatException)
                {
                    // A damaged font reports damage with a typed error.
                }
            }
        }
    }

    private static void Draw(FontFamily family)
    {
        Font font = family.CreateFont(18);
        var text = new StringBuilder();
        for (int cp = 0x20; cp < 0x3100 && text.Length < 48; cp += cp < 0x500 ? 1 : 37)
        {
            if (font.HasGlyph(cp))
            {
                text.Append((char)cp);
            }
        }

        for (int cp = 0xF0000; cp < 0xF1400 && text.Length < 80; cp += 7)
        {
            // The supplementary private use area, where the COLR version 1 test fonts keep their glyphs.
            if (font.HasGlyph(cp))
            {
                text.Append(char.ConvertFromUtf32(cp));
            }
        }

        text.Append("\nAV To 12 gy fi ffl e\u0301 a\u0308\u0301 \u03B1\u0301 \u0438\u0306");
        var options = new TextOptions(font) { WrappingLength = 120, Origin = new System.Numerics.Vector2(4, 4), Language = "ro" };
        options.Features.Add(FontFeature.On("smcp"));
        options.Features.Add(new FontFeature("aalt", 2));
        _ = TextMeasurer.Measure(text.ToString(), options);
        _ = TextMeasurer.GetCharacterBounds(text.ToString(), options);
        OneImage.Drawing.Path path = TextBuilder.GeneratePath(text.ToString(), options);
        var image = Image.FromPixels<Rgba32>(new Rgba32[96 * 64], 96, 64);
        _ = image.Fill(new Rgba32(255, 255, 255, 255), path).ToArray<Rgba32>(maxDegree: 1);
        _ = font.GetGlyphPath('A');

        // Hinted, vertical and color drawing of the same text.
        var hinted = new TextOptions(font) { WrappingLength = 120, Origin = new System.Numerics.Vector2(4, 4), HintingMode = HintingMode.Standard };
        _ = TextBuilder.GeneratePath(text.ToString(), hinted);
        var vertical = new TextOptions(font) { WrappingLength = 60, Origin = new System.Numerics.Vector2(40, 4), LayoutMode = LayoutMode.VerticalMixedRightLeft };
        _ = TextMeasurer.Measure(text.ToString(), vertical);
        _ = image.DrawText(text.ToString(), hinted, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>(maxDegree: 1);
        _ = image.DrawText(text.ToString(), vertical, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>(maxDegree: 1);
    }
}
