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

/// <summary>Lines drawn along text: any combination of the three.</summary>
[Flags]
public enum TextDecorations
{
    /// <summary>No decoration (the default).</summary>
    None = 0,

    /// <summary>A line below the baseline, at the underline position and thickness the font's <c>post</c> table gives.</summary>
    Underline = 1,

    /// <summary>A line through the letters, at the strikeout position and thickness of the font's <c>OS/2</c> table.</summary>
    Strikeout = 2,

    /// <summary>A line above the text, one em plus the underline distance over the baseline, as thick as the underline.</summary>
    Overline = 4,
}

/// <summary>A shift of a run off the baseline, at a reduced size.</summary>
public enum TextAttributes
{
    /// <summary>Normal text (the default).</summary>
    None,

    /// <summary>Raised and shrunk by the superscript size and offset the font's <c>OS/2</c> table recommends.</summary>
    Superscript,

    /// <summary>Lowered and shrunk by the subscript size and offset the font's <c>OS/2</c> table recommends.</summary>
    Subscript,
}
