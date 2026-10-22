namespace OneImage.Text;

/// <summary>Where a line of wrapped text may end. Applies only when <see cref="TextOptions.WrappingLength"/> is set.</summary>
public enum WordBreaking
{
    /// <summary>
    /// Lines end only at the break opportunities of the Unicode line breaking algorithm (UAX #14): after spaces and hyphens, between
    /// ideographs, around punctuation as that annex prescribes. A word with no opportunity inside it that is longer than the
    /// wrapping length keeps its own line and overflows (the default).
    /// </summary>
    Standard,

    /// <summary>
    /// Lines may end between any two characters (grapheme clusters), inside words and numbers too, so every line is filled to the
    /// wrapping length. Spaces still stay with the word before them.
    /// </summary>
    BreakAll,

    /// <summary>
    /// Like <see cref="Standard"/>, but no break opportunity is used right after an East Asian wide or fullwidth character (Chinese,
    /// Japanese and Korean text stays together; it breaks only at spaces). Text without such characters wraps as in
    /// <see cref="Standard"/>.
    /// </summary>
    KeepAll,

    /// <summary>
    /// Like <see cref="Standard"/>, but a word that is longer than the wrapping length even on a line of its own is broken between
    /// characters so that it fills lines instead of overflowing.
    /// </summary>
    BreakWord,
}
