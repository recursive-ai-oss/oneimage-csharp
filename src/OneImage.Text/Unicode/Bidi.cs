// This file is shared with the OnePDF library and must be kept in step with it; see docs/text.md.
namespace OneImage.Text.Unicode;

/// <summary>Bidi_Class values (UAX #9 table 4). The numeric values index <see cref="BidiData"/>.</summary>
internal enum BidiClass : byte
{
    L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON, LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI,
}

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9, Unicode 17): explicit levels (X1–X10), weak types (W1–W7), paired brackets (N0),
/// neutrals (N1–N2), implicit levels (I1–I2), line-end whitespace (L1) and reordering (L2). The whole input is one paragraph and
/// one line. Written from the specification; the character data is generated into <see cref="BidiData"/>.
/// </summary>
internal static class Bidi
{
    private const int MaxDepth = 125;
    private const int MaxBracketStack = 63;

    /// <summary>The level value reported for characters removed by rule X9.</summary>
    internal const byte Removed = 255;

    internal static BidiClass GetClass(int cp)
    {
        // ASCII fast path for the common letters.
        if (cp < 0x80 && ((cp >= 'a' && cp <= 'z') || (cp >= 'A' && cp <= 'Z')))
        {
            return BidiClass.L;
        }

        ReadOnlySpan<int> t = BidiData.ClassRanges;
        int lo = 0, hi = t.Length / 3 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int a = t[mid * 3];
            if (cp < a)
            {
                hi = mid - 1;
            }
            else if (cp > t[mid * 3 + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return (BidiClass)t[mid * 3 + 2];
            }
        }

        return BidiClass.L;
    }

    /// <summary>True for a strong right-to-left class (R or AL).</summary>
    internal static bool IsRtl(BidiClass c) => c is BidiClass.R or BidiClass.AL;

    private static int BracketType(int cp, out int paired)
    {
        ReadOnlySpan<int> t = BidiData.Brackets;
        int lo = 0, hi = t.Length / 3 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int a = t[mid * 3];
            if (cp < a)
            {
                hi = mid - 1;
            }
            else if (cp > a)
            {
                lo = mid + 1;
            }
            else
            {
                paired = t[mid * 3 + 1];
                return t[mid * 3 + 2];
            }
        }

        paired = 0;
        return 0;
    }

    private static int CanonicalBracket(int cp) => cp switch { 0x2329 => 0x3008, 0x232A => 0x3009, _ => cp };

    /// <summary>
    /// Resolves embedding levels for one paragraph. With <paramref name="lineEndRule"/> (the default) the paragraph is also one line
    /// and rule L1 is applied to it; a caller that breaks the paragraph into lines passes false and applies
    /// <see cref="ResetTrailingWhitespace"/> to each line.
    /// </summary>
    /// <param name="text">Code points in logical order.</param>
    /// <param name="requestedLevel">0 for left-to-right, 1 for right-to-left, anything else for automatic (rules P2–P3).</param>
    /// <param name="levels">Receives one level per code point; <see cref="Removed"/> for characters removed by X9.</param>
    /// <param name="lineEndRule">Whether to apply rule L1 to the whole paragraph as one line.</param>
    /// <returns>The paragraph embedding level.</returns>
    internal static int ResolveLevels(ReadOnlySpan<int> text, int requestedLevel, Span<byte> levels, bool lineEndRule = true)
    {
        int n = text.Length;
        if (levels.Length < n)
        {
            throw new ArgumentException("levels is too short.", nameof(levels));
        }

        if (n == 0)
        {
            return requestedLevel == 1 ? 1 : 0;
        }

        BidiClass[] orig = new BidiClass[n];
        for (int i = 0; i < n; i++)
        {
            orig[i] = GetClass(text[i]);
        }

        // BD9: matching isolate initiators and PDIs.
        int[] matchPdi = new int[n];
        int[] matchInit = new int[n];
        Array.Fill(matchPdi, -1);
        Array.Fill(matchInit, -1);
        {
            Stack<int> st = new();
            for (int i = 0; i < n; i++)
            {
                if (orig[i] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI)
                {
                    st.Push(i);
                }
                else if (orig[i] == BidiClass.PDI && st.Count > 0)
                {
                    int s = st.Pop();
                    matchPdi[s] = i;
                    matchInit[i] = s;
                }
            }
        }

        int para = requestedLevel is 0 or 1 ? requestedLevel : Math.Max(0, FirstStrong(orig, matchPdi, 0, n));

        BidiClass[] cls = (BidiClass[])orig.Clone();
        byte[] lev = new byte[n];
        bool[] removed = new bool[n];

        // X1–X8
        {
            Span<byte> stLevel = stackalloc byte[MaxDepth + 2];
            Span<byte> stOverride = stackalloc byte[MaxDepth + 2]; // 0 neutral, 1 L, 2 R
            Span<bool> stIsolate = stackalloc bool[MaxDepth + 2];
            int sp = 0;
            stLevel[0] = (byte)para;
            stOverride[0] = 0;
            stIsolate[0] = false;
            int overflowIsolate = 0, overflowEmbedding = 0, validIsolate = 0;

            for (int i = 0; i < n; i++)
            {
                BidiClass c = orig[i];
                switch (c)
                {
                    case BidiClass.RLE:
                    case BidiClass.LRE:
                    case BidiClass.RLO:
                    case BidiClass.LRO:
                    {
                        removed[i] = true;
                        lev[i] = stLevel[sp];
                        bool rtl = c is BidiClass.RLE or BidiClass.RLO;
                        int nl = NextLevel(stLevel[sp], rtl);
                        if (nl <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                        {
                            sp++;
                            stLevel[sp] = (byte)nl;
                            stOverride[sp] = c == BidiClass.RLO ? (byte)2 : c == BidiClass.LRO ? (byte)1 : (byte)0;
                            stIsolate[sp] = false;
                        }
                        else if (overflowIsolate == 0)
                        {
                            overflowEmbedding++;
                        }

                        break;
                    }

                    case BidiClass.RLI:
                    case BidiClass.LRI:
                    case BidiClass.FSI:
                    {
                        lev[i] = stLevel[sp];
                        if (stOverride[sp] != 0)
                        {
                            cls[i] = stOverride[sp] == 1 ? BidiClass.L : BidiClass.R;
                        }

                        bool rtl = c == BidiClass.RLI;
                        if (c == BidiClass.FSI)
                        {
                            int end = matchPdi[i] >= 0 ? matchPdi[i] : n;
                            rtl = FirstStrong(orig, matchPdi, i + 1, end) == 1;
                        }

                        int nl = NextLevel(stLevel[sp], rtl);
                        if (nl <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                        {
                            validIsolate++;
                            sp++;
                            stLevel[sp] = (byte)nl;
                            stOverride[sp] = 0;
                            stIsolate[sp] = true;
                        }
                        else
                        {
                            overflowIsolate++;
                        }

                        break;
                    }

                    case BidiClass.PDI:
                        if (overflowIsolate > 0)
                        {
                            overflowIsolate--;
                        }
                        else if (validIsolate > 0)
                        {
                            overflowEmbedding = 0;
                            while (!stIsolate[sp])
                            {
                                sp--;
                            }

                            sp--;
                            validIsolate--;
                        }

                        lev[i] = stLevel[sp];
                        if (stOverride[sp] != 0)
                        {
                            cls[i] = stOverride[sp] == 1 ? BidiClass.L : BidiClass.R;
                        }

                        break;

                    case BidiClass.PDF:
                        removed[i] = true;
                        lev[i] = stLevel[sp];
                        if (overflowIsolate > 0)
                        {
                            // nothing
                        }
                        else if (overflowEmbedding > 0)
                        {
                            overflowEmbedding--;
                        }
                        else if (!stIsolate[sp] && sp >= 1)
                        {
                            sp--;
                        }

                        break;

                    case BidiClass.B:
                        lev[i] = (byte)para;
                        break;

                    case BidiClass.BN:
                        removed[i] = true;
                        lev[i] = stLevel[sp];
                        break;

                    default:
                        lev[i] = stLevel[sp];
                        if (stOverride[sp] != 0)
                        {
                            cls[i] = stOverride[sp] == 1 ? BidiClass.L : BidiClass.R;
                        }

                        break;
                }
            }
        }

        // Explicit levels, kept apart from the resolved levels the sequences write into lev.
        byte[] emb = (byte[])lev.Clone();

        // X9: work on the characters that remain.
        int m = 0;
        int[] keep = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (!removed[i])
            {
                keep[m++] = i;
            }
        }

        // X10: level runs, then isolating run sequences (over positions in keep[]).
        int[] runStart = new int[m + 1];
        int runCount = 0;
        for (int k = 0; k < m; k++)
        {
            if (k == 0 || lev[keep[k]] != lev[keep[k - 1]])
            {
                runStart[runCount++] = k;
            }
        }

        runStart[runCount] = m;
        int[] runOfKeep = new int[m];
        for (int r = 0; r < runCount; r++)
        {
            for (int k = runStart[r]; k < runStart[r + 1]; k++)
            {
                runOfKeep[k] = r;
            }
        }

        int[] keepPosOf = new int[n]; // original index -> position in keep[], -1 if removed
        Array.Fill(keepPosOf, -1);
        for (int k = 0; k < m; k++)
        {
            keepPosOf[keep[k]] = k;
        }

        List<int> seq = new();
        for (int r = 0; r < runCount; r++)
        {
            int first = keep[runStart[r]];
            if (orig[first] == BidiClass.PDI && matchInit[first] >= 0 && keepPosOf[matchInit[first]] >= 0)
            {
                continue; // continuation of an earlier sequence
            }

            seq.Clear();
            int cr = r;
            while (true)
            {
                for (int k = runStart[cr]; k < runStart[cr + 1]; k++)
                {
                    seq.Add(keep[k]);
                }

                int last = keep[runStart[cr + 1] - 1];
                if (orig[last] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI && matchPdi[last] >= 0 && keepPosOf[matchPdi[last]] >= 0)
                {
                    cr = runOfKeep[keepPosOf[matchPdi[last]]];
                    continue;
                }

                break;
            }

            ResolveSequence(text, orig, cls, lev, emb, seq, keep, keepPosOf, m, para);
        }

        for (int i = 0; i < n; i++)
        {
            levels[i] = removed[i] ? Removed : lev[i];
        }

        if (lineEndRule)
        {
            ResetTrailingWhitespace(text, levels.Slice(0, n), para);
        }

        return para;
    }

    /// <summary>
    /// Rule L1 for one line: separators, and white space and isolate formatting characters before a separator or at the end of the
    /// line, go back to the paragraph level. Characters removed by X9 are transparent.
    /// </summary>
    /// <param name="text">The line's code points.</param>
    /// <param name="levels">The line's levels, as <see cref="ResolveLevels"/> wrote them; updated in place.</param>
    /// <param name="paragraphLevel">The paragraph embedding level.</param>
    internal static void ResetTrailingWhitespace(ReadOnlySpan<int> text, Span<byte> levels, int paragraphLevel)
    {
        bool trailing = true;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (levels[i] == Removed)
            {
                continue;
            }

            BidiClass c = GetClass(text[i]);
            if (c is BidiClass.B or BidiClass.S)
            {
                levels[i] = (byte)paragraphLevel;
                trailing = true;
            }
            else if (c is BidiClass.WS or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI)
            {
                if (trailing)
                {
                    levels[i] = (byte)paragraphLevel;
                }
            }
            else
            {
                trailing = false;
            }
        }
    }

    private static int NextLevel(int level, bool rtl) => rtl ? ((level + 1) | 1) : ((level + 2) & ~1);

    /// <summary>P2–P3 over [from, to): first strong character, skipping isolates. 1 for R/AL, 0 for L, -1 for none.</summary>
    private static int FirstStrong(BidiClass[] orig, int[] matchPdi, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            BidiClass c = orig[i];
            if (c == BidiClass.L)
            {
                return 0;
            }

            if (c is BidiClass.R or BidiClass.AL)
            {
                return 1;
            }

            if (c is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI)
            {
                if (matchPdi[i] < 0)
                {
                    return -1;
                }

                i = matchPdi[i];
            }
        }

        return -1;
    }

    private static void ResolveSequence(ReadOnlySpan<int> text, BidiClass[] orig, BidiClass[] cls, byte[] lev, byte[] emb, List<int> seq, int[] keep, int[] keepPosOf, int m, int para)
    {
        int len = seq.Count;
        int level = emb[seq[0]];

        int firstPos = keepPosOf[seq[0]];
        int prevLevel = firstPos > 0 ? emb[keep[firstPos - 1]] : para;
        BidiClass sos = (Math.Max(level, prevLevel) & 1) == 1 ? BidiClass.R : BidiClass.L;

        int lastIdx = seq[len - 1];
        int lastPos = keepPosOf[lastIdx];
        int nextLevel = para;
        if (!(orig[lastIdx] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI) && lastPos + 1 < m)
        {
            nextLevel = emb[keep[lastPos + 1]];
        }
        else if (orig[lastIdx] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI)
        {
            nextLevel = para;
        }

        BidiClass eos = (Math.Max(level, nextLevel) & 1) == 1 ? BidiClass.R : BidiClass.L;

        BidiClass[] t = new BidiClass[len];
        for (int i = 0; i < len; i++)
        {
            t[i] = cls[seq[i]];
        }

        // W1
        for (int i = 0; i < len; i++)
        {
            if (t[i] == BidiClass.NSM)
            {
                if (i == 0)
                {
                    t[i] = sos;
                }
                else
                {
                    BidiClass p = t[i - 1];
                    t[i] = p is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI ? BidiClass.ON : p;
                }
            }
        }

        // W2, W3
        {
            BidiClass lastStrong = sos;
            for (int i = 0; i < len; i++)
            {
                BidiClass c = t[i];
                if (c is BidiClass.L or BidiClass.R or BidiClass.AL)
                {
                    lastStrong = c;
                }
                else if (c == BidiClass.EN && lastStrong == BidiClass.AL)
                {
                    t[i] = BidiClass.AN;
                }
            }

            for (int i = 0; i < len; i++)
            {
                if (t[i] == BidiClass.AL)
                {
                    t[i] = BidiClass.R;
                }
            }
        }

        // W4
        for (int i = 1; i < len - 1; i++)
        {
            if (t[i] == BidiClass.ES && t[i - 1] == BidiClass.EN && t[i + 1] == BidiClass.EN)
            {
                t[i] = BidiClass.EN;
            }
            else if (t[i] == BidiClass.CS && t[i - 1] == t[i + 1] && t[i - 1] is BidiClass.EN or BidiClass.AN)
            {
                t[i] = t[i - 1];
            }
        }

        // W5
        for (int i = 0; i < len; i++)
        {
            if (t[i] == BidiClass.ET)
            {
                int j = i;
                while (j < len && t[j] == BidiClass.ET)
                {
                    j++;
                }

                bool adj = (i > 0 && t[i - 1] == BidiClass.EN) || (j < len && t[j] == BidiClass.EN);
                if (adj)
                {
                    for (int k = i; k < j; k++)
                    {
                        t[k] = BidiClass.EN;
                    }
                }

                i = j - 1;
            }
        }

        // W6
        for (int i = 0; i < len; i++)
        {
            if (t[i] is BidiClass.ES or BidiClass.ET or BidiClass.CS)
            {
                t[i] = BidiClass.ON;
            }
        }

        // W7
        {
            BidiClass lastStrong = sos;
            for (int i = 0; i < len; i++)
            {
                BidiClass c = t[i];
                if (c is BidiClass.L or BidiClass.R)
                {
                    lastStrong = c;
                }
                else if (c == BidiClass.EN && lastStrong == BidiClass.L)
                {
                    t[i] = BidiClass.L;
                }
            }
        }

        // N0
        BidiClass e = (level & 1) == 1 ? BidiClass.R : BidiClass.L;
        ResolveBrackets(text, orig, seq, t, sos, e);

        // N1, N2
        for (int i = 0; i < len; i++)
        {
            if (IsNeutral(t[i]))
            {
                int j = i;
                while (j < len && IsNeutral(t[j]))
                {
                    j++;
                }

                BidiClass before = i == 0 ? sos : StrongOf(t[i - 1]);
                BidiClass after = j == len ? eos : StrongOf(t[j]);
                BidiClass r = before == after ? before : e;
                for (int k = i; k < j; k++)
                {
                    t[k] = r;
                }

                i = j - 1;
            }
        }

        // I1, I2
        for (int i = 0; i < len; i++)
        {
            int l = level;
            BidiClass c = t[i];
            if ((level & 1) == 0)
            {
                if (c == BidiClass.R)
                {
                    l += 1;
                }
                else if (c is BidiClass.AN or BidiClass.EN)
                {
                    l += 2;
                }
            }
            else if (c is BidiClass.L or BidiClass.EN or BidiClass.AN)
            {
                l += 1;
            }

            lev[seq[i]] = (byte)l;
        }
    }

    private static bool IsNeutral(BidiClass c) =>
        c is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI;

    /// <summary>Strong direction used for neutral resolution: L for L, R for R, EN and AN.</summary>
    private static BidiClass StrongOf(BidiClass c) => c == BidiClass.L ? BidiClass.L : BidiClass.R;

    private static void ResolveBrackets(ReadOnlySpan<int> text, BidiClass[] orig, List<int> seq, BidiClass[] t, BidiClass sos, BidiClass e)
    {
        int len = seq.Count;
        List<(int Open, int Close)>? pairs = null;
        Span<int> stackCp = stackalloc int[MaxBracketStack];
        Span<int> stackPos = stackalloc int[MaxBracketStack];
        int sp = 0;
        for (int i = 0; i < len; i++)
        {
            if (t[i] != BidiClass.ON)
            {
                continue;
            }

            int cp = text[seq[i]];
            int type = BracketType(cp, out int paired);
            if (type == 1)
            {
                if (sp == MaxBracketStack)
                {
                    break;
                }

                stackCp[sp] = CanonicalBracket(paired);
                stackPos[sp] = i;
                sp++;
            }
            else if (type == 2)
            {
                int c = CanonicalBracket(cp);
                for (int k = sp - 1; k >= 0; k--)
                {
                    if (stackCp[k] == c)
                    {
                        (pairs ??= new()).Add((stackPos[k], i));
                        sp = k;
                        break;
                    }
                }
            }
        }

        if (pairs is null)
        {
            return;
        }

        pairs.Sort(static (a, b) => a.Open.CompareTo(b.Open));
        BidiClass opposite = e == BidiClass.L ? BidiClass.R : BidiClass.L;
        foreach ((int open, int close) in pairs)
        {
            bool foundE = false, foundO = false;
            for (int k = open + 1; k < close; k++)
            {
                BidiClass s = t[k] switch { BidiClass.L => BidiClass.L, BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R, _ => BidiClass.ON };
                if (s == e)
                {
                    foundE = true;
                    break;
                }

                if (s == opposite)
                {
                    foundO = true;
                }
            }

            BidiClass set;
            if (foundE)
            {
                set = e;
            }
            else if (foundO)
            {
                BidiClass ctx = sos;
                for (int k = open - 1; k >= 0; k--)
                {
                    BidiClass s = t[k] switch { BidiClass.L => BidiClass.L, BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R, _ => BidiClass.ON };
                    if (s != BidiClass.ON)
                    {
                        ctx = s;
                        break;
                    }
                }

                set = ctx == opposite ? opposite : e;
            }
            else
            {
                continue;
            }

            t[open] = set;
            t[close] = set;
            for (int k = open + 1; k < len && orig[seq[k]] == BidiClass.NSM; k++)
            {
                t[k] = set;
            }

            for (int k = close + 1; k < len && orig[seq[k]] == BidiClass.NSM; k++)
            {
                t[k] = set;
            }
        }
    }

    /// <summary>
    /// Rule L2 for one line: writes into <paramref name="order"/> the logical indices of the characters that are not removed, in visual
    /// (left-to-right) order, and returns how many there are.
    /// </summary>
    internal static int VisualOrder(ReadOnlySpan<byte> levels, Span<int> order)
    {
        int m = 0;
        int maxLevel = 0;
        int minOdd = int.MaxValue;
        for (int i = 0; i < levels.Length; i++)
        {
            byte l = levels[i];
            if (l == Removed)
            {
                continue;
            }

            order[m++] = i;
            maxLevel = Math.Max(maxLevel, l);
            if ((l & 1) == 1)
            {
                minOdd = Math.Min(minOdd, l);
            }
        }

        for (int lv = maxLevel; lv >= minOdd && lv > 0; lv--)
        {
            int i = 0;
            while (i < m)
            {
                if (levels[order[i]] >= lv)
                {
                    int j = i;
                    while (j < m && levels[order[j]] >= lv)
                    {
                        j++;
                    }

                    order.Slice(i, j - i).Reverse();
                    i = j;
                }
                else
                {
                    i++;
                }
            }
        }

        return m;
    }
}
