namespace OneImage.Text;

/// <summary>The weight and slant of a face within a family.</summary>
public enum FontStyle
{
    /// <summary>Upright, regular weight.</summary>
    Regular,

    /// <summary>Upright, bold.</summary>
    Bold,

    /// <summary>Slanted, regular weight.</summary>
    Italic,

    /// <summary>Slanted, bold.</summary>
    BoldItalic,
}

/// <summary>Where the text block sits horizontally relative to <see cref="TextOptions.Origin"/>.</summary>
public enum HorizontalAlignment
{
    /// <summary>The block starts at the origin (the default).</summary>
    Left,

    /// <summary>The block is centered on the origin.</summary>
    Center,

    /// <summary>The block ends at the origin.</summary>
    Right,
}

/// <summary>Where the text block sits vertically relative to <see cref="TextOptions.Origin"/>.</summary>
public enum VerticalAlignment
{
    /// <summary>The block starts at the origin (the default).</summary>
    Top,

    /// <summary>The block is centered on the origin.</summary>
    Center,

    /// <summary>The block ends at the origin.</summary>
    Bottom,
}

/// <summary>How the lines of a block line up inside it. Text runs left to right in this release.</summary>
public enum TextAlignment
{
    /// <summary>Lines keep the position horizontal alignment gave them, sharing a left edge (the default).</summary>
    Start,

    /// <summary>Lines are centered in the block.</summary>
    Center,

    /// <summary>Lines end at the block's end edge.</summary>
    End,
}

/// <summary>Whether pair kerning from the font is applied.</summary>
public enum KerningMode
{
    /// <summary>Apply the font's kerning: GPOS pair adjustment for the Latin script, or the legacy <c>kern</c> table when there is none (the default).</summary>
    Normal,

    /// <summary>Use plain advances.</summary>
    None,
}
