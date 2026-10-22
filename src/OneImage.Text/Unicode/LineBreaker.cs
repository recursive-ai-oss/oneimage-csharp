using System.Buffers;
using System.Runtime.CompilerServices;

namespace OneImage.Text.Unicode;

/// <summary>
/// Unicode line breaking (UAX #14, default rules, no tailoring except that the small kana of class CJ are treated as NS, which
/// is the algorithm's "strict" reading). Produces, for a stretch of text, which positions allow a line to end and which force it.
/// </summary>
/// <remarks>
/// The rules are applied to groups: a base character together with the combining marks and zero width joiners that follow it
/// (rule LB9), so no rule needs to look through them. Everything else is the rule list of the annex in order, with the few
/// rules that need more than the two neighbouring characters (spaces in between, numbers, regional indicator pairs) driven by
/// small running state instead of backward scans, so the cost is linear in the text.
/// </remarks>
internal static class LineBreaker
{
    /// <summary>No break at this position.</summary>
    public const byte None = 0;

    /// <summary>A line may end before this position.</summary>
    public const byte Allowed = 1;

    /// <summary>A line must end before this position.</summary>
    public const byte Mandatory = 2;

    // Bits of a group's info word: the 10 bits of the property table value plus two flags found while grouping.
    private const int ClassMask = 63;
    private const int Wide = 1 << 6;
    private const int Initial = 1 << 7;
    private const int Final = 1 << 8;
    private const int PictographUnassigned = 1 << 9;
    private const int DottedCircle = 1 << 10;
    private const int EndsWithJoiner = 1 << 11;

    private const char DottedCircleChar = '\u25CC';

    /// <summary>Gets the line break class of a code point after the default resolution of ambiguous classes.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>The class.</returns>
    internal static LineBreakClass ClassOf(int codePoint) => (LineBreakClass)(UnicodeProps.LineBreakValue(codePoint) & ClassMask);

    /// <summary>Gets whether a code point has East_Asian_Width Fullwidth, Wide or Halfwidth.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>True for ideographs, kana, Hangul syllables, fullwidth forms and the like.</returns>
    internal static bool IsEastAsianWide(int codePoint) => (UnicodeProps.LineBreakValue(codePoint) & Wide) != 0;

    /// <summary>
    /// Fills <paramref name="breaks"/> so that entry <c>i</c> says whether a line may end before UTF-16 index <c>i</c>. Entry 0 is
    /// always <see cref="None"/>; entry <c>text.Length</c> is <see cref="Mandatory"/> when the text ends with a hard break and
    /// <see cref="Allowed"/> otherwise. Positions inside a surrogate pair or combining sequence are <see cref="None"/>.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="breaks">At least <c>text.Length + 1</c> entries.</param>
    public static void Fill(ReadOnlySpan<char> text, Span<byte> breaks)
    {
        breaks = breaks[..(text.Length + 1)];
        breaks.Clear();
        if (text.IsEmpty)
        {
            breaks[0] = Allowed;
            return;
        }

        int[] posBuf = ArrayPool<int>.Shared.Rent(text.Length);
        ushort[] infoBuf = ArrayPool<ushort>.Shared.Rent(text.Length);
        try
        {
            int groups = BuildGroups(text, posBuf, infoBuf);
            Scan(infoBuf.AsSpan(0, groups), posBuf.AsSpan(0, groups), breaks);
            int last = infoBuf[groups - 1] & ClassMask;
            breaks[text.Length] = last is (int)LineBreakClass.BK or (int)LineBreakClass.CR or (int)LineBreakClass.LF or (int)LineBreakClass.NL
                ? Mandatory
                : Allowed;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(posBuf);
            ArrayPool<ushort>.Shared.Return(infoBuf);
        }
    }

    // LB9 and LB10: a combining mark or zero width joiner joins the group before it, unless that group is a hard break, a space
    // or a zero width space; in those cases (and at the start of the text) it is an ordinary letter (class AL).
    private static int BuildGroups(ReadOnlySpan<char> text, int[] pos, ushort[] info)
    {
        int g = 0;
        int i = 0;
        while (i < text.Length)
        {
            int cp = text[i];
            int len = 1;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                len = 2;
            }

            int v = UnicodeProps.LineBreakValue(cp);
            int cls = v & ClassMask;
            bool joiner = cls == (int)LineBreakClass.ZWJ;
            if (cls == (int)LineBreakClass.CM || joiner)
            {
                if (g > 0)
                {
                    int prev = info[g - 1] & ClassMask;
                    if (prev is not ((int)LineBreakClass.BK or (int)LineBreakClass.CR or (int)LineBreakClass.LF or (int)LineBreakClass.NL
                        or (int)LineBreakClass.SP or (int)LineBreakClass.ZW))
                    {
                        info[g - 1] = (ushort)((info[g - 1] & ~EndsWithJoiner) | (joiner ? EndsWithJoiner : 0));
                        i += len;
                        continue;
                    }
                }

                // Stands alone: behaves as U+0041 (class AL, narrow, not a quotation or pictograph).
                pos[g] = i;
                info[g] = (ushort)((int)LineBreakClass.AL | (joiner ? EndsWithJoiner : 0));
            }
            else
            {
                pos[g] = i;
                info[g] = (ushort)(v | (cp == DottedCircleChar ? DottedCircle : 0));
            }

            g++;
            i += len;
        }

        return g;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LineBreakClass Cls(ReadOnlySpan<ushort> info, int g) => (LineBreakClass)(info[g] & ClassMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWide(ReadOnlySpan<ushort> info, int g) => (info[g] & Wide) != 0;

    private static bool IsAlOrHl(LineBreakClass c) => c is LineBreakClass.AL or LineBreakClass.HL;

    private static bool IsHangul(LineBreakClass c) => c is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3;

    private static void Scan(ReadOnlySpan<ushort> info, ReadOnlySpan<int> pos, Span<byte> breaks)
    {
        int count = info.Length;

        // State carried across positions.
        int ns = 0;                 // last group that is not a space (the one "spaces may follow" rules look at)
        bool nsInitialQuote = false; // rule LB15a: ns is an opening-position Pi quotation mark
        int numState = 0;           // 0 none, 1 after NU (SY|IS)*, 2 after NU (SY|IS)* (CL|CP)   (rule LB25)
        int riRun = 0;              // regional indicators immediately before the position

        // Group 0 only seeds the state.
        SeedState(info, 0, ref ns, ref nsInitialQuote, ref numState, ref riRun);

        for (int g = 1; g < count; g++)
        {
            byte decision = Decide(info, g, ns, nsInitialQuote, numState, riRun);
            breaks[pos[g]] = decision;
            SeedState(info, g, ref ns, ref nsInitialQuote, ref numState, ref riRun);
        }
    }

    private static void SeedState(ReadOnlySpan<ushort> info, int g, ref int ns, ref bool nsInitialQuote, ref int numState, ref int riRun)
    {
        LineBreakClass c = Cls(info, g);
        if (c != LineBreakClass.SP)
        {
            ns = g;
            if (c == LineBreakClass.QU && (info[g] & Initial) != 0)
            {
                // LB15a: the quotation mark must be at the start, or follow one of these classes.
                nsInitialQuote = g == 0 || Cls(info, g - 1) is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL
                    or LineBreakClass.OP or LineBreakClass.QU or LineBreakClass.GL or LineBreakClass.SP or LineBreakClass.ZW;
            }
            else
            {
                nsInitialQuote = false;
            }
        }

        numState = c switch
        {
            LineBreakClass.NU => 1,
            LineBreakClass.SY or LineBreakClass.IS when numState == 1 => 1,
            LineBreakClass.CL or LineBreakClass.CP when numState == 1 => 2,
            _ => 0,
        };
        riRun = c == LineBreakClass.RI ? riRun + 1 : 0;
    }

    // The decision for the position between groups g - 1 and g, following the rule list of UAX #14 section 6 in order.
    private static byte Decide(ReadOnlySpan<ushort> info, int g, int ns, bool nsInitialQuote, int numState, int riRun)
    {
        int count = info.Length;
        LineBreakClass c = Cls(info, g);
        LineBreakClass p = Cls(info, g - 1);

        // The common case, two letters in a row: no rule before LB28 can break between them unless a joiner or a dotted circle
        // is involved, so skip the rule list.
        if (p == LineBreakClass.AL && c == LineBreakClass.AL && ((info[g - 1] | info[g]) & (DottedCircle | EndsWithJoiner)) == 0)
        {
            return None;
        }

        LineBreakClass nsClass = Cls(info, ns);
        bool hasNext = g + 1 < count;
        LineBreakClass n1 = hasNext ? Cls(info, g + 1) : LineBreakClass.BK;

        // LB4, LB5: hard breaks, and CR LF stays together.
        if (p == LineBreakClass.BK)
        {
            return Mandatory;
        }

        if (p == LineBreakClass.CR)
        {
            return c == LineBreakClass.LF ? None : Mandatory;
        }

        if (p is LineBreakClass.LF or LineBreakClass.NL)
        {
            return Mandatory;
        }

        // LB6, LB7: no break before a hard break, a space or a zero width space.
        if (c is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW)
        {
            return None;
        }

        // LB8: break after a zero width space, even through spaces.
        if (nsClass == LineBreakClass.ZW)
        {
            return Allowed;
        }

        // LB8a: no break after a zero width joiner.
        if ((info[g - 1] & EndsWithJoiner) != 0)
        {
            return None;
        }

        // LB11, LB12: word joiner and glue.
        if (c == LineBreakClass.WJ || p == LineBreakClass.WJ || p == LineBreakClass.GL)
        {
            return None;
        }

        // LB12a: no break before glue unless after a space or a hyphen.
        if (c == LineBreakClass.GL && p is not (LineBreakClass.SP or LineBreakClass.BA or LineBreakClass.HY or LineBreakClass.HH))
        {
            return None;
        }

        // LB13: no break before closing punctuation, exclamations and slashes, even after spaces.
        if (c is LineBreakClass.CL or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.SY)
        {
            return None;
        }

        // LB14: no break after an opening bracket, even through spaces.
        if (nsClass == LineBreakClass.OP)
        {
            return None;
        }

        // LB15a: no break after an opening-position initial quotation mark, even through spaces.
        if (nsInitialQuote)
        {
            return None;
        }

        // LB15b: no break before a final quotation mark that is followed by one of these (or the end of the text).
        if (c == LineBreakClass.QU && (info[g] & Final) != 0 && (!hasNext || n1 is LineBreakClass.SP or LineBreakClass.GL or LineBreakClass.WJ
            or LineBreakClass.CL or LineBreakClass.QU or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.IS or LineBreakClass.SY
            or LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.ZW))
        {
            return None;
        }

        // LB15c: break before a decimal mark that follows a space ("subtract .5"). LB15d: otherwise none before IS.
        if (c == LineBreakClass.IS)
        {
            return p == LineBreakClass.SP && n1 == LineBreakClass.NU && hasNext ? Allowed : None;
        }

        // LB16, LB17: closing punctuation before a non-starter, and B2 B2, even through spaces.
        if (c == LineBreakClass.NS && nsClass is LineBreakClass.CL or LineBreakClass.CP)
        {
            return None;
        }

        if (c == LineBreakClass.B2 && nsClass == LineBreakClass.B2)
        {
            return None;
        }

        // LB18: break after spaces.
        if (p == LineBreakClass.SP)
        {
            return Allowed;
        }

        // LB19: no break around quotation marks that are not clearly final (before) or initial (after).
        if (c == LineBreakClass.QU && (info[g] & Initial) == 0)
        {
            return None;
        }

        if (p == LineBreakClass.QU && (info[g - 1] & Final) == 0)
        {
            return None;
        }

        // LB19a: unless East Asian characters surround it, no break on either side of a quotation mark.
        if (c == LineBreakClass.QU && (!IsWide(info, g - 1) || !hasNext || !IsWide(info, g + 1)))
        {
            return None;
        }

        if (p == LineBreakClass.QU && (!IsWide(info, g) || g == 1 || !IsWide(info, g - 2)))
        {
            return None;
        }

        // LB20: break around contingent break objects.
        if (c == LineBreakClass.CB || p == LineBreakClass.CB)
        {
            return Allowed;
        }

        // LB20a: no break after a hyphen at the start of a word.
        if (p is LineBreakClass.HY or LineBreakClass.HH && IsAlOrHl(c)
            && (g == 1 || Cls(info, g - 2) is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP
                or LineBreakClass.ZW or LineBreakClass.CB or LineBreakClass.GL))
        {
            return None;
        }

        // LB21: no break before hyphens, break-after characters and non-starters, nor after break-before characters.
        if (c is LineBreakClass.BA or LineBreakClass.HH or LineBreakClass.HY or LineBreakClass.NS || p == LineBreakClass.BB)
        {
            return None;
        }

        // LB21a, LB21b: Hebrew with hyphens and solidus.
        if (p is LineBreakClass.HY or LineBreakClass.HH && g >= 2 && Cls(info, g - 2) == LineBreakClass.HL && c != LineBreakClass.HL)
        {
            return None;
        }

        if (p == LineBreakClass.SY && c == LineBreakClass.HL)
        {
            return None;
        }

        // LB22: no break before ellipsis.
        if (c == LineBreakClass.IN)
        {
            return None;
        }

        // LB23, LB23a, LB24: letters, numbers, prefixes and postfixes.
        if ((IsAlOrHl(p) && c == LineBreakClass.NU) || (p == LineBreakClass.NU && IsAlOrHl(c)))
        {
            return None;
        }

        if ((p == LineBreakClass.PR && c is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM)
            || (p is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM && c == LineBreakClass.PO))
        {
            return None;
        }

        if ((p is LineBreakClass.PR or LineBreakClass.PO && IsAlOrHl(c)) || (IsAlOrHl(p) && c is LineBreakClass.PR or LineBreakClass.PO))
        {
            return None;
        }

        // LB25: numbers.
        if (numState != 0 && c is LineBreakClass.PO or LineBreakClass.PR)
        {
            return None;
        }

        if (numState == 1 && c == LineBreakClass.NU)
        {
            return None;
        }

        if (p is LineBreakClass.PO or LineBreakClass.PR)
        {
            if (c == LineBreakClass.NU)
            {
                return None;
            }

            if (c == LineBreakClass.OP && hasNext)
            {
                if (n1 == LineBreakClass.NU || (n1 == LineBreakClass.IS && g + 2 < count && Cls(info, g + 2) == LineBreakClass.NU))
                {
                    return None;
                }
            }
        }

        if (p is LineBreakClass.HY or LineBreakClass.IS && c == LineBreakClass.NU)
        {
            return None;
        }

        // LB26, LB27: Korean syllable blocks.
        if (IsHangul(c) || IsHangul(p))
        {
            if (p == LineBreakClass.JL && c is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.H2 or LineBreakClass.H3)
            {
                return None;
            }

            if (p is LineBreakClass.JV or LineBreakClass.H2 && c is LineBreakClass.JV or LineBreakClass.JT)
            {
                return None;
            }

            if (p is LineBreakClass.JT or LineBreakClass.H3 && c == LineBreakClass.JT)
            {
                return None;
            }

            if (IsHangul(p) && c == LineBreakClass.PO)
            {
                return None;
            }

            if (p == LineBreakClass.PR && IsHangul(c))
            {
                return None;
            }
        }

        // LB28: no break between letters.
        if (IsAlOrHl(p) && IsAlOrHl(c))
        {
            return None;
        }

        // LB28a: Brahmic orthographic syllables.
        if (IsAksara(info, g - 1) || p == LineBreakClass.AP || p == LineBreakClass.VI)
        {
            if (p == LineBreakClass.AP && IsAksara(info, g))
            {
                return None;
            }

            if (IsAksara(info, g - 1) && c is LineBreakClass.VF or LineBreakClass.VI)
            {
                return None;
            }

            if (p == LineBreakClass.VI && g >= 2 && IsAksara(info, g - 2) && IsAksaraOrCircle(info, g, includeStart: false))
            {
                return None;
            }

            if (IsAksara(info, g - 1) && IsAksara(info, g) && hasNext && n1 == LineBreakClass.VF)
            {
                return None;
            }
        }

        // LB29: no break between numeric punctuation and letters.
        if (p == LineBreakClass.IS && IsAlOrHl(c))
        {
            return None;
        }

        // LB30: no break between letters or numbers and a narrow opening or closing parenthesis.
        if ((IsAlOrHl(p) || p == LineBreakClass.NU) && c == LineBreakClass.OP && !IsWide(info, g))
        {
            return None;
        }

        if (p == LineBreakClass.CP && !IsWide(info, g - 1) && (IsAlOrHl(c) || c == LineBreakClass.NU))
        {
            return None;
        }

        // LB30a: regional indicators pair up from the start of a run.
        if (p == LineBreakClass.RI && c == LineBreakClass.RI && (riRun & 1) == 1)
        {
            return None;
        }

        // LB30b: emoji base (or a reserved pictograph) with a modifier.
        if (c == LineBreakClass.EM && (p == LineBreakClass.EB || (info[g - 1] & PictographUnassigned) != 0))
        {
            return None;
        }

        // LB31: break everywhere else.
        return Allowed;
    }

    // The class (AK | ◌ | AS) of rule LB28a.
    private static bool IsAksara(ReadOnlySpan<ushort> info, int g)
    {
        LineBreakClass c = Cls(info, g);
        return c is LineBreakClass.AK or LineBreakClass.AS || (c == LineBreakClass.AL && (info[g] & DottedCircle) != 0);
    }

    // (AK | ◌) when includeStart is false, (AK | ◌ | AS) otherwise.
    private static bool IsAksaraOrCircle(ReadOnlySpan<ushort> info, int g, bool includeStart)
    {
        LineBreakClass c = Cls(info, g);
        return c == LineBreakClass.AK || (includeStart && c == LineBreakClass.AS) || (c == LineBreakClass.AL && (info[g] & DottedCircle) != 0);
    }
}
