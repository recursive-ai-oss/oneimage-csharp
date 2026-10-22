namespace OneImage.Text;

/// <summary>Which color glyph formats <see cref="TextDrawing"/> paints when it draws text.</summary>
[Flags]
public enum ColorFontSupport
{
    /// <summary>No color glyphs: every glyph is a plain outline filled with the brush.</summary>
    None = 0,

    /// <summary>
    /// The Microsoft COLR/CPAL layered format, version 0 (the default): a glyph is a stack of layer glyphs, each painted with a palette color or
    /// the text color. COLR version 1 data (gradients, transforms, compositing) is not painted; such a glyph draws as its plain
    /// outline, unless it also has version 0 layers.
    /// </summary>
    MicrosoftColrFormat = 1,
}
