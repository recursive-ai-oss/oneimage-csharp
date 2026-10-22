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

/// <summary>How the lines of a block line up inside it. Start and End follow the direction of each paragraph.</summary>
public enum TextAlignment
{
    /// <summary>Lines share the edge a paragraph starts at: the left edge of a left-to-right paragraph, the right edge of a right-to-left one (the default).</summary>
    Start,

    /// <summary>Lines are centered in the block.</summary>
    Center,

    /// <summary>Lines end at the edge a paragraph ends at: the right edge of a left-to-right paragraph, the left edge of a right-to-left one.</summary>
    End,
}

/// <summary>The base direction of a paragraph (Unicode Bidirectional Algorithm rules P2 and P3 choose it automatically).</summary>
public enum TextDirection
{
    /// <summary>The direction of the first strong character of each paragraph, left to right when there is none (the default).</summary>
    Auto,

    /// <summary>Every paragraph is left to right.</summary>
    LeftToRight,

    /// <summary>Every paragraph is right to left.</summary>
    RightToLeft,
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

/// <summary>How lines are oriented and in which direction they stack.</summary>
public enum LayoutMode
{
    /// <summary>Horizontal lines, the first at the top and later lines below it (the default).</summary>
    HorizontalTopBottom,

    /// <summary>Horizontal lines stacked upward: the first line is at the bottom of the block and later lines are above it. Each line still reads left to right.</summary>
    HorizontalBottomTop,

    /// <summary>
    /// Vertical columns, every character upright, reading top to bottom, the first column at the left and later columns to its
    /// right. The font's vertical metrics (<c>vhea</c>, <c>vmtx</c>, <c>VORG</c>) and its <c>vert</c>/<c>vrt2</c> forms are used.
    /// </summary>
    VerticalLeftRight,

    /// <summary>Like <see cref="VerticalLeftRight"/> with the first column at the right and later columns to its left, as in traditional East Asian typesetting.</summary>
    VerticalRightLeft,

    /// <summary>
    /// Vertical columns, the first at the left, with the orientation of each character taken from the Unicode Vertical_Orientation
    /// property (UAX #50): ideographs and kana stand upright, Latin letters, digits and most punctuation lie rotated a quarter turn clockwise.
    /// </summary>
    VerticalMixedLeftRight,

    /// <summary>Like <see cref="VerticalMixedLeftRight"/> with the first column at the right.</summary>
    VerticalMixedRightLeft,
}
