using OneImage.Text.Fonts;
using OneImage.Text.Unicode;

namespace OneImage.Text.Shaping;

/// <summary>
/// Shaping for the nine Indic scripts (Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam) following
/// Microsoft's OpenType script-development documents: syllables are found from character classes, split vowel signs are decomposed,
/// the base consonant is found with the font's own help (a consonant that forms a below-base or post-base form cannot be the base),
/// glyphs are put in reordering classes before the localized and basic forms run one feature at a time, and the pre-base matra, reph
/// and pre-base reordering Ra are moved to their final places before the presentation forms run on the whole syllable.
/// </summary>
/// <remarks>
/// Script tags: the "new" v2 tags (dev2, bng2, ...) are used when the font has them, then the v1 tags with the same processing. The
/// document's separate handling of v1 fonts (basic features applied to consonant + halant pairs only) is not reproduced.
/// A mark with no base gets a dotted circle (U+25CC) when the font has one. Syllables longer than <see cref="SyllableSupport.MaxSyllable"/>
/// glyphs are cut, which bounds every per-syllable step. Line breaking is not affected: it stays UAX #14 (see the text documentation).
/// </remarks>
internal sealed class IndicShaper : ScriptShaper
{
    // The feature stages, in application order. The localized and basic forms run one at a time.
    internal const int StLocl = 0, StNukt = 1, StAkhn = 2, StRphf = 3, StRkrf = 4, StPref = 5, StBlwf = 6, StAbvf = 7, StHalf = 8,
        StPstf = 9, StVatu = 10, StCjct = 11, StInit = 12;

    // Per-glyph feature bits: a feature with a bit only starts at glyphs carrying it.
    private const uint BitRphf = 1, BitPref = 2, BitBlwf = 4, BitAbvf = 8, BitHalf = 16, BitPstf = 32, BitInit = 64;

    private static readonly uint TagNukt = Fonts.Be.Tag("nukt");
    private static readonly uint TagAkhn = Fonts.Be.Tag("akhn");
    private static readonly uint TagRphf = Fonts.Be.Tag("rphf");
    private static readonly uint TagRkrf = Fonts.Be.Tag("rkrf");
    private static readonly uint TagPref = Fonts.Be.Tag("pref");
    private static readonly uint TagBlwf = Fonts.Be.Tag("blwf");
    private static readonly uint TagAbvf = Fonts.Be.Tag("abvf");
    private static readonly uint TagHalf = Fonts.Be.Tag("half");
    private static readonly uint TagPstf = Fonts.Be.Tag("pstf");
    private static readonly uint TagVatu = Fonts.Be.Tag("vatu");
    private static readonly uint TagCjct = Fonts.Be.Tag("cjct");
    private static readonly uint TagInit = Fonts.Be.Tag("init");
    private static readonly uint TagPres = Fonts.Be.Tag("pres");
    private static readonly uint TagAbvs = Fonts.Be.Tag("abvs");
    private static readonly uint TagBlws = Fonts.Be.Tag("blws");
    private static readonly uint TagPsts = Fonts.Be.Tag("psts");
    private static readonly uint TagHaln = Fonts.Be.Tag("haln");
    private static readonly uint TagDist = Fonts.Be.Tag("dist");
    private static readonly uint TagAbvm = Fonts.Be.Tag("abvm");
    private static readonly uint TagBlwm = Fonts.Be.Tag("blwm");

    private static readonly uint[][] Stages =
    [
        [Tag.Ccmp, Tag.Locl],
        [TagNukt],
        [TagAkhn],
        [TagRphf],
        [TagRkrf],
        [TagPref],
        [TagBlwf],
        [TagAbvf],
        [TagHalf],
        [TagPstf],
        [TagVatu],
        [TagCjct],
        [TagInit],
        [TagPres, TagAbvs, TagBlws, TagPsts, TagHaln, Tag.Calt, Tag.Clig],
    ];

    // Reordering classes inside a syllable, in the order the glyphs end up (the document's "reordering classes").
    private const byte PosStart = 0, PosReph = 1, PosPreM = 2, PosPreC = 3, PosBase = 4, PosAfterMain = 5, PosAboveC = 6,
        PosBeforeSub = 7, PosBelowC = 8, PosAfterSub = 9, PosBeforePost = 10, PosPostC = 11, PosAfterPost = 12, PosFinal = 13, PosSmVd = 14;

    // Character classes of the syllable grammar.
    private const byte CatOther = 0, CatC = 1, CatRa = 2, CatV = 3, CatN = 4, CatH = 5, CatZwnj = 6, CatZwj = 7, CatM = 8, CatSm = 9,
        CatVd = 10, CatGb = 11;

    private static readonly IndicShaper?[] Instances = new IndicShaper?[10];

    private readonly ShapedScript _script;
    private readonly int _block;
    private readonly byte _rephClass;
    private readonly bool _matrasAfterSub;
    private readonly bool _rephNeedsJoiner;
    private readonly uint _v2;
    private readonly uint _v1;

    private IndicShaper(ShapedScript script)
    {
        _script = script;
        int index = (int)script - 1;
        _block = 0x900 + (0x80 * index);
        (_rephClass, _matrasAfterSub, _rephNeedsJoiner) = script switch
        {
            ShapedScript.Devanagari => (PosBeforePost, true, false),
            ShapedScript.Gujarati => (PosBeforePost, true, false),
            ShapedScript.Bengali => (PosAfterSub, false, false),
            ShapedScript.Gurmukhi => (PosBeforeSub, false, false),
            ShapedScript.Oriya => (PosAfterMain, false, false),
            ShapedScript.Tamil => (PosBeforeSub, false, false),
            ShapedScript.Telugu => (PosAfterPost, false, false),
            ShapedScript.Kannada => (PosAfterPost, false, false),
            _ => (PosAfterMain, false, true), // Malayalam: the reph (chillu r) is requested with a joiner
        };
        (_v2, _v1) = script switch
        {
            ShapedScript.Devanagari => (Fonts.Be.Tag("dev2"), Fonts.Be.Tag("deva")),
            ShapedScript.Bengali => (Fonts.Be.Tag("bng2"), Fonts.Be.Tag("beng")),
            ShapedScript.Gurmukhi => (Fonts.Be.Tag("gur2"), Fonts.Be.Tag("guru")),
            ShapedScript.Gujarati => (Fonts.Be.Tag("gjr2"), Fonts.Be.Tag("gujr")),
            ShapedScript.Oriya => (Fonts.Be.Tag("ory2"), Fonts.Be.Tag("orya")),
            ShapedScript.Tamil => (Fonts.Be.Tag("tml2"), Fonts.Be.Tag("taml")),
            ShapedScript.Telugu => (Fonts.Be.Tag("tel2"), Fonts.Be.Tag("telu")),
            ShapedScript.Kannada => (Fonts.Be.Tag("knd2"), Fonts.Be.Tag("knda")),
            _ => (Fonts.Be.Tag("mlm2"), Fonts.Be.Tag("mlym")),
        };
    }

    /// <summary>The shaper for an Indic script, or null when <paramref name="script"/> is not one of the nine.</summary>
    public static IndicShaper? For(ShapedScript script)
    {
        if (script < ShapedScript.Devanagari || script > ShapedScript.Malayalam)
        {
            return null;
        }

        return Instances[(int)script] ??= new IndicShaper(script);
    }

    private int RaCp => _block + 0x30;

    private int ViramaCp => _block + 0x4D;

    public override uint[][] GsubStages => Stages;

    public override bool KeepsJoiners => true;

    public override bool Reorders => true;

    public override uint[] ScriptTags(uint script) => [_v2, _v1, Tag.Dflt, Tag.DfltLower, Tag.Latn];

    public override bool DefaultOn(uint tag) =>
        tag == Tag.Ccmp || tag == Tag.Locl || tag == TagNukt || tag == TagAkhn || tag == TagRphf || tag == TagRkrf || tag == TagPref
        || tag == TagBlwf || tag == TagAbvf || tag == TagHalf || tag == TagPstf || tag == TagVatu || tag == TagCjct || tag == TagInit
        || tag == TagPres || tag == TagAbvs || tag == TagBlws || tag == TagPsts || tag == TagHaln || tag == Tag.Calt || tag == Tag.Clig
        || tag == Tag.Kern || tag == TagDist || tag == TagAbvm || tag == TagBlwm || tag == Tag.Mark || tag == Tag.Mkmk || tag == Tag.Curs;

    public override uint FeatureMask(uint tag)
    {
        if (tag == TagRphf)
        {
            return BitRphf;
        }

        if (tag == TagPref)
        {
            return BitPref;
        }

        if (tag == TagBlwf)
        {
            return BitBlwf;
        }

        if (tag == TagAbvf)
        {
            return BitAbvf;
        }

        if (tag == TagHalf)
        {
            return BitHalf;
        }

        if (tag == TagPstf)
        {
            return BitPstf;
        }

        return tag == TagInit ? BitInit : 0;
    }

    // ---- Character classes ----

    private byte Classify(int cp, IndicSyllabicCategory isc)
    {
        if (cp == 0x200D)
        {
            return CatZwj;
        }

        if (cp == 0x200C)
        {
            return CatZwnj;
        }

        switch (isc)
        {
            case IndicSyllabicCategory.Consonant:
            case IndicSyllabicCategory.ConsonantDead:
            case IndicSyllabicCategory.ConsonantFinal:
            case IndicSyllabicCategory.ConsonantHeadLetter:
            case IndicSyllabicCategory.ConsonantInitialPostfixed:
            case IndicSyllabicCategory.ConsonantMedial:
            case IndicSyllabicCategory.ConsonantPrefixed:
            case IndicSyllabicCategory.ConsonantSubjoined:
            case IndicSyllabicCategory.ConsonantWithStacker:
            case IndicSyllabicCategory.ConsonantPrecedingRepha:
            case IndicSyllabicCategory.ConsonantSucceedingRepha:
                return cp == RaCp || cp == 0x9F0 ? CatRa : CatC;
            case IndicSyllabicCategory.Nukta:
                return CatN;
            case IndicSyllabicCategory.Virama:
            case IndicSyllabicCategory.PureKiller:
            case IndicSyllabicCategory.ConsonantKiller:
            case IndicSyllabicCategory.InvisibleStacker:
                return CatH;
            case IndicSyllabicCategory.Vowel:
            case IndicSyllabicCategory.VowelIndependent:
                return CatV;
            case IndicSyllabicCategory.VowelDependent:
                return CatM;
            case IndicSyllabicCategory.Bindu:
            case IndicSyllabicCategory.Visarga:
            case IndicSyllabicCategory.SyllableModifier:
            case IndicSyllabicCategory.ToneMark:
            case IndicSyllabicCategory.GeminationMark:
            case IndicSyllabicCategory.ModifyingLetter:
                return CatSm;
            case IndicSyllabicCategory.CantillationMark:
                return CatVd;
            case IndicSyllabicCategory.ConsonantPlaceholder:
                return CatGb;
            default:
                return CatOther;
        }
    }

    // The class a dependent vowel sign or modifier is sorted into; the Devanagari and Gujarati tables put all of them after the subjoined forms.
    private byte MatraClass(IndicPositionalCategory ipc)
    {
        switch (ipc)
        {
            case IndicPositionalCategory.Left:
            case IndicPositionalCategory.VisualOrderLeft:
            case IndicPositionalCategory.TopAndLeft:
            case IndicPositionalCategory.BottomAndLeft:
            case IndicPositionalCategory.LeftAndRight:
            case IndicPositionalCategory.TopAndLeftAndRight:
            case IndicPositionalCategory.TopAndBottomAndLeft:
                return PosPreM;
        }

        if (_matrasAfterSub)
        {
            return PosAfterSub;
        }

        // Signs keep their logical order after the consonants, so every sign sorts after the post-base consonants.
        return PosAfterPost;
    }

    // ---- Building the run: decomposition, syllables, dotted circles ----

    public override GlyphBuffer? BuildRun(FontFace face, ReadOnlySpan<int> codePoints, ReadOnlySpan<int> clusters)
    {
        int n = codePoints.Length;
        var cps = new int[n + (n >> 1) + 8];
        var cls = new int[cps.Length];
        var cat = new byte[cps.Length];
        var matra = new byte[cps.Length];
        var aboveSign = new bool[cps.Length];
        int m = 0;
        Span<int> stack = stackalloc int[8];
        for (int k = 0; k < n; k++)
        {
            int depth = 0;
            stack[depth++] = codePoints[k];
            while (depth > 0)
            {
                int cp = stack[--depth];
                IndicSyllabicCategory isc;
                IndicPositionalCategory ipc;
                IndicProps.Lookup(cp, out isc, out ipc);
                if (depth + 2 <= stack.Length && IndicProps.Decompose(cp, out int a, out int b)
                    && (isc == IndicSyllabicCategory.VowelDependent || face.GetGlyphIndex(cp) == 0))
                {
                    stack[depth++] = b;
                    stack[depth++] = a;
                    continue;
                }

                if (m == cps.Length)
                {
                    Array.Resize(ref cps, m * 2);
                    Array.Resize(ref cls, m * 2);
                    Array.Resize(ref cat, m * 2);
                    Array.Resize(ref matra, m * 2);
                    Array.Resize(ref aboveSign, m * 2);
                }

                cps[m] = cp;
                cls[m] = clusters[k];
                byte c = Classify(cp, isc);
                cat[m] = c;
                matra[m] = c == CatM ? MatraClass(ipc) : c is CatSm or CatVd ? PosSmVd : (byte)0;
                // Telugu's fonts ligate an above-base sign (and the length mark that follows it in the split ai and au) with the base itself,
                // so these signs sit next to the base, ahead of its below-base forms.
                aboveSign[m] = c == CatM && _script == ShapedScript.Telugu
                    && (ipc is IndicPositionalCategory.Top or IndicPositionalCategory.Overstruck || (m > 0 && aboveSign[m - 1] && cps[m - 1] != cp));
                m++;
            }
        }

        var buffer = new GlyphBuffer(m + 4);
        bool circle = face.GetGlyphIndex(SyllableSupport.DottedCircle) != 0;
        int syllable = 0;
        for (int i = 0; i < m;)
        {
            int end = MatchSyllable(cat, i, m, out bool broken);
            if (end - i > SyllableSupport.MaxSyllable)
            {
                end = i + SyllableSupport.MaxSyllable;
            }

            int first = cls[i];
            if (broken && circle)
            {
                GlyphInfo dc = SyllableSupport.MakeInfo(face, SyllableSupport.DottedCircle, first, CatGb);
                dc.Syllable = syllable;
                buffer.Add(dc);
            }

            for (int j = i; j < end; j++)
            {
                GlyphInfo g = SyllableSupport.MakeInfo(face, cps[j], first, cat[j]);
                g.Pos = matra[j];
                if (aboveSign[j])
                {
                    g.Flags |= GlyphFlags.Above;
                }
                g.Syllable = syllable;
                buffer.Add(g);
            }

            syllable++;
            i = end;
        }

        return buffer;
    }

    private static bool IsCons(byte c) => c == CatC || c == CatRa;

    private static bool IsJoiner(byte c) => c == CatZwj || c == CatZwnj;

    // Finds the end of the syllable starting at i (the document's consonant, vowel-based and stand-alone clusters, and runs of
    // marks without a base, which are "broken").
    private static int MatchSyllable(byte[] c, int i, int n, out bool broken)
    {
        broken = false;
        byte k = c[i];
        if (k == CatOther || IsJoiner(k))
        {
            return i + 1;
        }

        int p;
        if (k == CatRa && i + 2 < n && c[i + 1] == CatH && (c[i + 2] == CatV || c[i + 2] == CatGb))
        {
            // A vowel-based cluster may start with Ra + halant: [Ra H] V.
            p = i + 3;
        }
        else if (IsCons(k))
        {
            p = i;
            int len = 0;
            while (p < n && IsCons(c[p]) && len++ < 32)
            {
                p++;
                if (p < n && c[p] == CatN)
                {
                    p++;
                }

                // C [N] H [joiner] C continues the cluster; C [N] [joiner] H C as well.
                int q = p;
                if (q < n && IsJoiner(c[q]) && q + 1 < n && c[q + 1] == CatH)
                {
                    q++;
                }

                if (q < n && c[q] == CatH)
                {
                    q++;
                    if (q < n && IsJoiner(c[q]))
                    {
                        q++;
                    }

                    p = q;
                    if (p < n && IsCons(c[p]))
                    {
                        continue;
                    }
                }

                break;
            }

            return Tail(c, p, n);
        }
        else if (k == CatV || k == CatGb)
        {
            p = i + 1;
        }
        else
        {
            // Marks with no base.
            broken = true;
            return Tail(c, i, n, true);
        }

        if (p < n && c[p] == CatN)
        {
            p++;
        }

        int guard = 0;
        while (p < n && guard++ < 32)
        {
            int q = p;
            if (IsJoiner(c[q]) && q + 1 < n && c[q + 1] == CatH)
            {
                q++;
            }

            if (c[q] == CatH && q + 1 < n)
            {
                int r = q + 1;
                if (IsJoiner(c[r]))
                {
                    r++;
                }

                if (r < n && IsCons(c[r]))
                {
                    p = r + 1;
                    if (p < n && c[p] == CatN)
                    {
                        p++;
                    }

                    continue;
                }
            }
            else if (c[q] == CatZwj && q + 1 < n && IsCons(c[q + 1]))
            {
                p = q + 2;
                continue;
            }

            break;
        }

        return Tail(c, p, n);
    }

    // Matras, nuktas, trailing halants and modifier signs after the base.
    private static int Tail(byte[] c, int p, int n, bool atLeastOne = false)
    {
        int start = p;
        while (p < n && (c[p] is CatM or CatN or CatH or CatSm or CatVd || (IsJoiner(c[p]) && p > start && c[p - 1] == CatH)))
        {
            p++;
        }

        return atLeastOne && p == start ? p + 1 : p;
    }

    // ---- Initial reordering ----

    public override void Prepare(ShapeContext ctx)
    {
        GlyphBuffer buffer = ctx.Buffer;
        for (int s = 0; s < buffer.Count;)
        {
            int e = SyllableSupport.SyllableEnd(buffer, s);
            if (e - s >= 1 && buffer.Info[s].Cat != CatOther)
            {
                InitialReorder(ctx, s, e);
            }

            s = e;
        }
    }

    private int HalantGlyph(ShapeContext ctx, GlyphInfo[] info, int s, int e)
    {
        for (int i = s; i < e; i++)
        {
            if (info[i].Cat == CatH)
            {
                return info[i].Glyph;
            }
        }

        return ctx.Face.GetGlyphIndex(ViramaCp);
    }

    // True if the consonant at i, preceded directly by a halant (no joiner), forms the given stage's form with it.
    private static bool FormsWithHalant(ShapeContext ctx, GlyphInfo[] info, int s, int i, int stage, int halant)
    {
        if (i - 1 < s || info[i - 1].Cat != CatH)
        {
            return false;
        }

        return SyllableSupport.Would(ctx, stage, info[i - 1].Glyph == 0 ? halant : info[i - 1].Glyph, info[i].Glyph);
    }

    private void InitialReorder(ShapeContext ctx, int s, int e)
    {
        GlyphInfo[] info = ctx.Buffer.Info;
        int halant = 0;
        bool haveHalant = false;
        int firstHead = s;

        // A leading Ra + halant may become a reph.
        bool rephCandidate = false;
        if (info[s].Cat == CatRa && s + 1 < e && info[s + 1].Cat == CatH)
        {
            bool blocked = s + 2 < e && IsJoiner(info[s + 2].Cat) && !(_rephNeedsJoiner && info[s + 2].Cat == CatZwj);
            bool needsJoiner = _rephNeedsJoiner && !(s + 2 < e && info[s + 2].Cat == CatZwj);
            if (!blocked && !needsJoiner)
            {
                halant = HalantGlyph(ctx, info, s, e);
                haveHalant = true;
                rephCandidate = SyllableSupport.Would(ctx, StRphf, info[s].Glyph, info[s + 1].Glyph);
            }
        }

        int afterReph = rephCandidate ? s + (_rephNeedsJoiner ? 3 : 2) : s;
        if (afterReph > e)
        {
            afterReph = e;
        }

        // The base: for a syllable that starts with a vowel or placeholder it is that glyph; otherwise it is found among the consonants.
        int b = -1;
        int headIdx = -1;
        for (int i = afterReph; i < e; i++)
        {
            if (info[i].Cat is CatV or CatGb)
            {
                headIdx = i;
                break;
            }

            if (IsCons(info[i].Cat))
            {
                break;
            }
        }

        int consonants = 0;
        for (int i = s; i < e; i++)
        {
            if (IsCons(info[i].Cat))
            {
                consonants++;
            }
        }

        if (rephCandidate && consonants < 2 && headIdx < 0)
        {
            rephCandidate = false;
            afterReph = s;
        }

        if (headIdx >= 0)
        {
            b = headIdx;
        }
        else
        {
            if (!haveHalant)
            {
                halant = HalantGlyph(ctx, info, s, e);
            }

            int last = -1;
            for (int i = e - 1; i >= afterReph; i--)
            {
                if (IsCons(info[i].Cat))
                {
                    last = i;
                    break;
                }
            }

            if (last < 0)
            {
                // Marks only (a broken syllable with no circle available): nothing to reorder.
                return;
            }

            int firstCons = afterReph;
            while (firstCons < e && !IsCons(info[firstCons].Cat))
            {
                firstCons++;
            }

            b = last;
            bool seenBelow = false;
            for (int i = last; i > firstCons; i--)
            {
                if (!IsCons(info[i].Cat))
                {
                    continue;
                }

                if (FormsWithHalant(ctx, info, s, i, StPref, halant) && !seenBelow)
                {
                    continue;
                }

                if (FormsWithHalant(ctx, info, s, i, StBlwf, halant))
                {
                    seenBelow = true;
                    continue;
                }

                if (!seenBelow && FormsWithHalant(ctx, info, s, i, StPstf, halant))
                {
                    continue;
                }

                b = i;
                goto found;
            }

            b = firstCons;
        found:;
        }

        // Classes.
        info[b].Flags |= GlyphFlags.Base;
        bool word = s == 0 || info[s - 1].Cat == CatOther;
        byte lastPos = PosBase;
        for (int i = s; i < e; i++)
        {
            ref GlyphInfo g = ref info[i];
            uint mask = word ? BitInit : 0;
            if (rephCandidate && i < afterReph)
            {
                g.Pos = PosReph;
                mask |= i == s ? BitRphf | BitHalf : BitHalf;
            }
            else if (i < b)
            {
                switch (g.Cat)
                {
                    case CatM:
                    case CatSm:
                    case CatVd:
                        break; // already classed
                    default:
                        g.Pos = PosPreC;
                        // A non-joiner after the halant asks for the halant form, so the consonant gets no half form.
                        if (!(IsCons(g.Cat) && HalantThenNonJoiner(info, i, e)))
                        {
                            mask |= BitHalf;
                        }

                        break;
                }
            }
            else if (i == b)
            {
                g.Pos = PosBase;

                // Consonant + halant + joiner at the end of a syllable shows the half form.
                if (i + 2 < e + 0 && info[i + 1].Cat == CatH && info[i + 2].Cat == CatZwj)
                {
                    mask |= BitHalf;
                }
            }
            else
            {
                mask |= BitPref | BitBlwf | BitAbvf | BitPstf;
                switch (g.Cat)
                {
                    case CatC:
                    case CatRa:
                        if (FormsWithHalant(ctx, info, s, i, StAbvf, halant))
                        {
                            lastPos = PosAboveC;
                        }
                        else if (FormsWithHalant(ctx, info, s, i, StBlwf, halant))
                        {
                            lastPos = PosBelowC;
                        }
                        else
                        {
                            lastPos = PosPostC;
                        }

                        g.Pos = lastPos;
                        break;
                    case CatH:
                    case CatN:
                    case CatZwj:
                    case CatZwnj:
                        g.Pos = lastPos;
                        break;
                    default:
                        if (g.Pos != PosPreM)
                        {
                            lastPos = g.Pos;
                        }

                        break;
                }
            }

            g.Mask = mask;
        }

        // A nukta comes before a halant it follows.
        for (int i = s + 1; i < e; i++)
        {
            if (info[i].Cat == CatN && info[i - 1].Cat == CatH && !IsJoiner(info[i - 1].Cat))
            {
                (info[i - 1], info[i]) = (info[i], info[i - 1]);
            }
        }

        if (rephCandidate)
        {
            info[s].Flags |= GlyphFlags.Reph;
        }

        SyllableSupport.SortByPos(info, s, e);
    }

    private static bool HalantThenNonJoiner(GlyphInfo[] info, int i, int e)
    {
        int j = i + 1;
        if (j < e && info[j].Cat == CatN)
        {
            j++;
        }

        return j + 1 < e && info[j].Cat == CatH && info[j + 1].Cat == CatZwnj;
    }

    // ---- Between stages ----

    public override void AfterStage(int stage, ShapeContext ctx)
    {
        GlyphBuffer buffer = ctx.Buffer;
        GlyphInfo[] info = buffer.Info;
        if (stage == StAkhn || stage == StRkrf)
        {
            // Remember the glyphs so the next stage's effect (reph formed, pre-base Ra formed) can be seen.
            for (int i = 0; i < buffer.Count; i++)
            {
                info[i].Before = info[i].Glyph;
            }

            return;
        }

        if (stage == StRphf)
        {
            // Only a reph that really formed keeps the flag; otherwise the Ra is an ordinary consonant.
            for (int i = 0; i < buffer.Count; i++)
            {
                if ((info[i].Flags & GlyphFlags.Reph) != 0 && info[i].Glyph == info[i].Before)
                {
                    info[i].Flags = (byte)(info[i].Flags & ~GlyphFlags.Reph);
                }
            }

            return;
        }

        if (stage == StPref)
        {
            for (int i = 0; i < buffer.Count; i++)
            {
                if ((info[i].Flags & GlyphFlags.Base) == 0 && (info[i].Mask & BitPref) != 0 && info[i].Glyph != info[i].Before)
                {
                    info[i].Flags |= GlyphFlags.Pref;
                }
            }

            return;
        }

        if (stage == StCjct)
        {
            for (int s = 0; s < buffer.Count;)
            {
                int e = SyllableSupport.SyllableEnd(buffer, s);
                if (e - s > 1 && info[s].Cat != CatOther)
                {
                    FinalReorder(buffer, s, e);
                }

                s = e;
            }
        }
    }

    private static int FindBase(GlyphInfo[] info, int s, int e)
    {
        for (int i = s; i < e; i++)
        {
            if ((info[i].Flags & GlyphFlags.Base) != 0)
            {
                return i;
            }
        }

        return -1;
    }

    // The place a pre-base matra (or a pre-base Ra) goes: after the last halant that is still a glyph of its own between the start of
    // the syllable and the base (and after a joiner that follows it), or -1 when there is none.
    private static int AfterLastHalant(GlyphInfo[] info, int from, int b)
    {
        for (int i = b - 1; i >= from; i--)
        {
            if (info[i].Cat == CatH && (info[i].Flags & GlyphFlags.Pref) == 0 && (info[i].Flags & GlyphFlags.Base) == 0)
            {
                int t = i + 1;
                if (t < b && IsJoiner(info[t].Cat))
                {
                    t++;
                }

                return t;
            }
        }

        return -1;
    }

    private void FinalReorder(GlyphBuffer buffer, int s, int e)
    {
        GlyphInfo[] info = buffer.Info;
        int b = FindBase(info, s, e);
        if (b < 0)
        {
            return;
        }

        bool reph = (info[s].Flags & GlyphFlags.Reph) != 0;
        int initial = reph ? s + 1 : s;

        // Pre-base matras: initial reordering put them at the start; they now move to their place before the base.
        for (int i = initial; i < b; i++)
        {
            if (info[i].Cat == CatM && info[i].Pos == PosPreM)
            {
                int t = AfterLastHalant(info, initial, b);
                if (t < 0)
                {
                    t = initial;
                }

                if (t > i)
                {
                    SyllableSupport.MoveBefore(info, i, t);
                    b = FindBase(info, s, e);
                }

                break;
            }
        }

        // The pre-base reordering Ra.
        for (int i = b + 1; i < e; i++)
        {
            if ((info[i].Flags & GlyphFlags.Pref) != 0)
            {
                int t = AfterLastHalant(info, initial, b);
                if (t < 0)
                {
                    t = b;
                }

                SyllableSupport.MoveBefore(info, i, t);
                b = FindBase(info, s, e);
                break;
            }
        }

        // Signs above the base come right after it, ahead of its below-base forms.
        int after = b + 1;
        for (int i = b + 1; i < e; i++)
        {
            if ((info[i].Flags & GlyphFlags.Above) != 0)
            {
                SyllableSupport.MoveBefore(info, i, after);
                after++;
            }
        }

        // The reph goes to its script's place after the base.
        if (reph)
        {
            int t = e;
            for (int i = b + 1; i < e; i++)
            {
                if (info[i].Pos > _rephClass && (info[i].Flags & GlyphFlags.Pref) == 0)
                {
                    t = i;
                    break;
                }
            }

            info[s].Pos = _rephClass;
            SyllableSupport.MoveBefore(info, s, t);
        }
    }
}
