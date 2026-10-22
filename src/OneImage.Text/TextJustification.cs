namespace OneImage.Text;

/// <summary>How lines are stretched to the full wrapping length. Applies only when <see cref="TextOptions.WrappingLength"/> is set.</summary>
public enum TextJustification
{
    /// <summary>Lines keep their natural width (the default).</summary>
    None,

    /// <summary>
    /// Each line is stretched to the wrapping length by adding the same extra width to every space between its words. A line
    /// without spaces, and a line that is already longer than the wrapping length, is left as it is. As in ImageSharp, the last
    /// line and lines that end at a line break are stretched too.
    /// </summary>
    InterWord,

    /// <summary>
    /// Each line is stretched to the wrapping length by adding the same extra width after every character (grapheme cluster) but
    /// the last, spaces included. A line that is already longer than the wrapping length is left as it is.
    /// </summary>
    InterCharacter,
}
