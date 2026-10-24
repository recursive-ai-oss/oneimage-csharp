namespace OneImage.Text.Fonts;

/// <summary>Translates this package's shaping settings into OneFont's.</summary>
internal static class FontBridge
{
    /// <summary>
    /// The OneFont options for a set of settings. A vertical run has pair kerning off and the font's <c>vert</c> and <c>vrt2</c> forms on;
    /// the caller's own features come last, so they win.
    /// </summary>
    public static global::OneFont.ShapingOptions Options(string? language, KerningMode kerning, IList<FontFeature>? features, bool vertical)
    {
        var o = new global::OneFont.ShapingOptions
        {
            Language = language,
            Kerning = kerning == KerningMode.Normal ? global::OneFont.KerningMode.Normal : global::OneFont.KerningMode.None,
        };
        if (vertical)
        {
            o.Features.Add(global::OneFont.FontFeature.Off("kern"));
            o.Features.Add(global::OneFont.FontFeature.On("vert"));
            o.Features.Add(global::OneFont.FontFeature.On("vrt2"));
        }

        if (features is not null)
        {
            foreach (FontFeature f in features)
            {
                if (f.Tag is not null)
                {
                    o.Features.Add(new global::OneFont.FontFeature(f.Tag, f.Value));
                }
            }
        }

        return o;
    }
}
