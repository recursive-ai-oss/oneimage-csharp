namespace OneImage.Text.Unicode;

/// <summary>
/// Extended grapheme cluster boundaries (UAX #29 section 3, including the Indic conjunct rule GB9c). A cluster is what a reader
/// sees as one character: a base with its combining marks, an emoji sequence, a Hangul syllable block, a regional indicator
/// pair. Wrapping and truncation must never cut inside one.
/// </summary>
internal static class Graphemes
{
    /// <summary>Gets the length in UTF-16 units of the cluster that starts at the beginning of <paramref name="text"/>.</summary>
    /// <param name="text">Text that starts at a cluster boundary. Not empty.</param>
    /// <returns>A length of at least 1 (an unpaired surrogate is its own cluster).</returns>
    public static int NextLength(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return 0;
        }

        // Two characters below U+0300 (Latin letters, digits, punctuation) always split, except CR LF: nothing there extends,
        // joins or prepends, and controls break on both sides anyway.
        if (text.Length > 1 && text[0] < '\u0300' && text[1] < '\u0300' && !(text[0] == '\r' && text[1] == '\n'))
        {
            return 1;
        }

        int i = Decode(text, 0, out int cp);
        int v = UnicodeProps.GraphemeValue(cp);
        GraphemeClass prev = (GraphemeClass)(v & 15);

        // Running state of the rules that look back past the previous character.
        int pictograph = (v & 16) != 0 ? 1 : 0;  // GB11: 1 after ExtPict Extend*, 2 after that and a ZWJ
        int conjunct = IncbOf(v) == 1 ? 1 : 0;    // GB9c: 1 after a consonant, 2 once a linker was seen
        int regional = prev == GraphemeClass.RI ? 1 : 0;

        while (i < text.Length)
        {
            int next = Decode(text, i, out int cur);
            int cv = UnicodeProps.GraphemeValue(cur);
            var c = (GraphemeClass)(cv & 15);
            bool extPict = (cv & 16) != 0;
            int incb = IncbOf(cv);

            if (IsBoundary(prev, c, extPict, incb, pictograph, conjunct, regional))
            {
                return i;
            }

            pictograph = extPict ? 1 : (pictograph == 1 && c == GraphemeClass.Extend) ? 1 : (pictograph == 1 && c == GraphemeClass.ZWJ) ? 2 : 0;
            conjunct = incb switch
            {
                1 => 1,
                2 when conjunct != 0 => 2,
                3 when conjunct != 0 => conjunct,
                _ => 0,
            };
            regional = c == GraphemeClass.RI ? regional + 1 : 0;
            prev = c;
            i = next;
        }

        return text.Length;
    }

    /// <summary>
    /// Marks the start of every cluster: <c>starts[i]</c> is 1 when a cluster begins at UTF-16 index <c>i</c> (including index
    /// <c>text.Length</c>, the end) and 0 elsewhere.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="starts">At least <c>text.Length + 1</c> entries.</param>
    public static void Fill(ReadOnlySpan<char> text, Span<byte> starts)
    {
        starts = starts[..(text.Length + 1)];
        starts.Clear();
        int i = 0;
        while (i < text.Length)
        {
            starts[i] = 1;
            i += NextLength(text[i..]);
        }

        starts[text.Length] = 1;
    }

    private static int IncbOf(int value) => (value >> 5) & 3;

    private static int Decode(ReadOnlySpan<char> text, int index, out int cp)
    {
        char hi = text[index];
        if (char.IsHighSurrogate(hi) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            cp = char.ConvertToUtf32(hi, text[index + 1]);
            return index + 2;
        }

        cp = hi;
        return index + 1;
    }

    private static bool IsBoundary(GraphemeClass p, GraphemeClass c, bool curExtPict, int curIncb, int pictograph, int conjunct, int regional)
    {
        // GB3: CR LF; GB4, GB5: controls break on both sides.
        if (p == GraphemeClass.CR && c == GraphemeClass.LF)
        {
            return false;
        }

        if (p is GraphemeClass.Control or GraphemeClass.CR or GraphemeClass.LF || c is GraphemeClass.Control or GraphemeClass.CR or GraphemeClass.LF)
        {
            return true;
        }

        // GB6-GB8: Hangul syllable blocks.
        if (p == GraphemeClass.L && c is GraphemeClass.L or GraphemeClass.V or GraphemeClass.LV or GraphemeClass.LVT)
        {
            return false;
        }

        if (p is GraphemeClass.LV or GraphemeClass.V && c is GraphemeClass.V or GraphemeClass.T)
        {
            return false;
        }

        if (p is GraphemeClass.LVT or GraphemeClass.T && c == GraphemeClass.T)
        {
            return false;
        }

        // GB9, GB9a, GB9b: extenders, spacing marks, prepended characters.
        if (c is GraphemeClass.Extend or GraphemeClass.ZWJ or GraphemeClass.SpacingMark || p == GraphemeClass.Prepend)
        {
            return false;
        }

        // GB9c: consonant, linker(s) and extenders, then another consonant.
        if (conjunct == 2 && curIncb == 1)
        {
            return false;
        }

        // GB11: emoji ZWJ sequence.
        if (pictograph == 2 && curExtPict)
        {
            return false;
        }

        // GB12, GB13: regional indicators pair up from the start of a run.
        if (p == GraphemeClass.RI && c == GraphemeClass.RI && (regional & 1) == 1)
        {
            return false;
        }

        return true;
    }
}
