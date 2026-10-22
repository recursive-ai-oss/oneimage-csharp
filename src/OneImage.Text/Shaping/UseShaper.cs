using OneImage.Text.Fonts;
using OneImage.Text.Unicode;

namespace OneImage.Text.Shaping;

/// <summary>
/// Shaping for the scripts of Microsoft's Universal Shaping Engine specification, including Khmer, Myanmar, Sinhala, Tibetan,
/// Balinese and Javanese. Characters get a category from their Indic_Syllabic_Category and Indic_Positional_Category, clusters are
/// found as a base followed by stacked consonants and marks, the basic forms (localized forms, nukta, akhand, reph, pre-base
/// forms, below-, above- and post-base forms, half forms, conjuncts) run one feature at a time, and then the pre-base vowels and
/// medials, the reph (and the Myanmar kinzi) and the pre-base reordering consonant (the Khmer coeng + ro) move to their places
/// before the presentation forms run on the whole cluster.
/// </summary>
/// <remarks>
/// A mark with no base gets a dotted circle (U+25CC) when the font has one. Clusters are cut at <see cref="SyllableSupport.MaxSyllable"/>
/// glyphs, which bounds all per-cluster work. The Khmer vowel signs U+17BE, U+17BF, U+17C0, U+17C4 and U+17C5 are split into the
/// pre-base U+17C1 and the sign itself, as the Khmer script document describes. Sinhala's two-part vowel signs split by their
/// canonical decompositions. Line breaking is not affected: it stays UAX #14 without dictionaries (see the text documentation).
/// </remarks>
internal sealed class UseShaper : ScriptShaper
{
    internal const int StRphf = 3, StPref = 5, StReorder = 12;

    private const uint BitRphf = 1;

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
    private static readonly uint TagCfar = Fonts.Be.Tag("cfar");
    private static readonly uint TagAbvs = Fonts.Be.Tag("abvs");
    private static readonly uint TagBlws = Fonts.Be.Tag("blws");
    private static readonly uint TagHaln = Fonts.Be.Tag("haln");
    private static readonly uint TagPres = Fonts.Be.Tag("pres");
    private static readonly uint TagPsts = Fonts.Be.Tag("psts");
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
        [TagCfar],
        [TagAbvs, TagBlws, Tag.Calt, Tag.Clig, TagHaln, Tag.Liga, TagPres, TagPsts, Tag.Rclt, Tag.Rlig],
    ];

    // Cluster classes.
    private const byte CatOther = 0, CatBase = 1, CatGb = 2, CatCs = 3, CatH = 4, CatN = 5, CatSub = 6, CatMark = 7, CatPre = 8,
        CatZwj = 9, CatZwnj = 10, CatRepha = 11;

    // Reordering classes inside a cluster.
    private const byte PosRepha = 0, PosPre = 1, PosChain = 2, PosAfterChain = 3, PosMark = 4;

    private static readonly UseShaper?[] Instances = new UseShaper?[64];

    private readonly ShapedScript _script;
    private readonly uint _v2;
    private readonly uint _v1;

    private UseShaper(ShapedScript script)
    {
        _script = script;
        _v1 = Scripts.TagOf(script);
        _v2 = script == ShapedScript.Myanmar ? Fonts.Be.Tag("mym2") : 0;
    }

    /// <summary>The shaper for a script of the Universal Shaping Engine, or null when <paramref name="script"/> is not one of them.</summary>
    public static UseShaper? For(ShapedScript script)
    {
        if (script < ShapedScript.Khmer || script >= ShapedScript.Thai)
        {
            return null;
        }

        return Instances[(int)script] ??= new UseShaper(script);
    }

    public override uint[][] GsubStages => Stages;

    public override bool KeepsJoiners => true;

    public override bool Reorders => true;

    public override uint[] ScriptTags(uint script) =>
        _v2 != 0 ? [_v2, _v1, Tag.Dflt, Tag.DfltLower, Tag.Latn] : [_v1, Tag.Dflt, Tag.DfltLower, Tag.Latn];

    public override bool DefaultOn(uint tag) =>
        tag == Tag.Ccmp || tag == Tag.Locl || tag == TagNukt || tag == TagAkhn || tag == TagRphf || tag == TagRkrf || tag == TagPref
        || tag == TagBlwf || tag == TagAbvf || tag == TagHalf || tag == TagPstf || tag == TagVatu || tag == TagCjct || tag == TagCfar
        || tag == TagAbvs || tag == TagBlws || tag == TagHaln || tag == Tag.Calt || tag == Tag.Clig || tag == Tag.Liga || tag == TagPres
        || tag == TagPsts || tag == Tag.Rclt || tag == Tag.Rlig || tag == Tag.Kern || tag == TagDist || tag == TagAbvm || tag == TagBlwm
        || tag == Tag.Mark || tag == Tag.Mkmk || tag == Tag.Curs;

    public override uint FeatureMask(uint tag) => tag == TagRphf ? BitRphf : 0;

    private static bool HasLeft(IndicPositionalCategory ipc) =>
        ipc is IndicPositionalCategory.Left or IndicPositionalCategory.VisualOrderLeft or IndicPositionalCategory.TopAndLeft
            or IndicPositionalCategory.BottomAndLeft or IndicPositionalCategory.TopAndBottomAndLeft or IndicPositionalCategory.LeftAndRight
            or IndicPositionalCategory.TopAndLeftAndRight;

    private byte Classify(int cp, IndicSyllabicCategory isc, IndicPositionalCategory ipc)
    {
        if (cp == 0x200D)
        {
            return CatZwj;
        }

        if (cp == 0x200C)
        {
            return CatZwnj;
        }

        if (cp is >= 0xFE00 and <= 0xFE0F or 0x034F)
        {
            return CatMark;
        }

        switch (isc)
        {
            case IndicSyllabicCategory.Consonant:
            case IndicSyllabicCategory.ConsonantDead:
            case IndicSyllabicCategory.ConsonantHeadLetter:
            case IndicSyllabicCategory.ConsonantInitialPostfixed:
            case IndicSyllabicCategory.ConsonantPrefixed:
            case IndicSyllabicCategory.Vowel:
            case IndicSyllabicCategory.VowelIndependent:
            case IndicSyllabicCategory.ModifyingLetter:
            case IndicSyllabicCategory.ToneLetter:
                return CatBase;
            case IndicSyllabicCategory.ConsonantWithStacker:
                return CatCs;
            case IndicSyllabicCategory.ConsonantPlaceholder:
                return CatGb;
            case IndicSyllabicCategory.ConsonantPrecedingRepha:
                return CatRepha;
            case IndicSyllabicCategory.ConsonantSubjoined:
                return CatSub;
            case IndicSyllabicCategory.Virama:
            case IndicSyllabicCategory.InvisibleStacker:
                return CatH;
            case IndicSyllabicCategory.Nukta:
                return CatN;
            case IndicSyllabicCategory.ConsonantMedial:
            case IndicSyllabicCategory.VowelDependent:
            case IndicSyllabicCategory.Bindu:
            case IndicSyllabicCategory.Visarga:
            case IndicSyllabicCategory.SyllableModifier:
            case IndicSyllabicCategory.ToneMark:
            case IndicSyllabicCategory.GeminationMark:
            case IndicSyllabicCategory.CantillationMark:
            case IndicSyllabicCategory.RegisterShifter:
            case IndicSyllabicCategory.ConsonantFinal:
            case IndicSyllabicCategory.ConsonantKiller:
            case IndicSyllabicCategory.PureKiller:
            case IndicSyllabicCategory.ConsonantSucceedingRepha:
            case IndicSyllabicCategory.ReorderingKiller:
                return HasLeft(ipc) && !(_script == ShapedScript.Khmer && cp is >= 0x17BE and <= 0x17C0 or 0x17C4 or 0x17C5) ? CatPre : CatMark;
            default:
                return CatOther;
        }
    }

    public override GlyphBuffer? BuildRun(FontFace face, ReadOnlySpan<int> codePoints, ReadOnlySpan<int> clusters)
    {
        int n = codePoints.Length;
        var cps = new int[n + (n >> 1) + 8];
        var cls = new int[cps.Length];
        var cat = new byte[cps.Length];
        int m = 0;
        Span<int> stack = stackalloc int[8];
        for (int k = 0; k < n; k++)
        {
            int depth = 0;
            stack[depth++] = codePoints[k];
            while (depth > 0)
            {
                int cp = stack[--depth];
                IndicProps.Lookup(cp, out IndicSyllabicCategory isc, out IndicPositionalCategory ipc);
                if (depth + 2 <= stack.Length)
                {
                    if (_script == ShapedScript.Khmer && cp is 0x17BE or 0x17BF or 0x17C0 or 0x17C4 or 0x17C5)
                    {
                        // The pre-base e plus the sign itself; the sign keeps its code point (it is not decomposed again).
                        if (m + 2 > cps.Length)
                        {
                            GrowRun(ref cps, ref cls, ref cat);
                        }

                        cps[m] = 0x17C1;
                        cls[m] = clusters[k];
                        cat[m++] = CatPre;
                        cps[m] = cp;
                        cls[m] = clusters[k];
                        cat[m++] = Classify(cp, isc, ipc);
                        continue;
                    }

                    if (IndicProps.Decompose(cp, out int a, out int b) && isc == IndicSyllabicCategory.VowelDependent)
                    {
                        stack[depth++] = b;
                        stack[depth++] = a;
                        continue;
                    }
                }

                if (m == cps.Length)
                {
                    GrowRun(ref cps, ref cls, ref cat);
                }

                cps[m] = cp;
                cls[m] = clusters[k];
                cat[m++] = Classify(cp, isc, ipc);
            }
        }

        var buffer = new GlyphBuffer(m + 4);
        bool circle = face.GetGlyphIndex(SyllableSupport.DottedCircle) != 0;
        int cluster = 0;
        for (int i = 0; i < m;)
        {
            int end = MatchCluster(cat, i, m, out bool broken);
            if (end - i > SyllableSupport.MaxSyllable)
            {
                end = i + SyllableSupport.MaxSyllable;
            }

            int first = cls[i];
            if (broken && circle)
            {
                GlyphInfo dc = SyllableSupport.MakeInfo(face, SyllableSupport.DottedCircle, first, CatGb);
                dc.Syllable = cluster;
                buffer.Add(dc);
            }

            for (int j = i; j < end; j++)
            {
                GlyphInfo g = SyllableSupport.MakeInfo(face, cps[j], first, cat[j]);
                g.Syllable = cluster;
                buffer.Add(g);
            }

            cluster++;
            i = end;
        }

        return buffer;
    }

    private static void GrowRun(ref int[] cps, ref int[] cls, ref byte[] cat)
    {
        int size = cps.Length * 2;
        Array.Resize(ref cps, size);
        Array.Resize(ref cls, size);
        Array.Resize(ref cat, size);
    }

    private static bool IsHead(byte c) => c == CatBase || c == CatGb || c == CatCs;

    private static bool IsJoiner(byte c) => c == CatZwj || c == CatZwnj;

    // A cluster is an optional leading repha, a base, then stacked consonants (a virama or stacker followed by a base or subjoined
    // consonant, with an optional joiner), nuktas, subjoined consonants and marks. Marks with no base form a broken cluster.
    private static int MatchCluster(byte[] c, int i, int n, out bool broken)
    {
        broken = false;
        byte k = c[i];
        if (k == CatOther || IsJoiner(k))
        {
            return i + 1;
        }

        int p = i;
        if (k == CatRepha && i + 1 < n && IsHead(c[i + 1]))
        {
            p++;
        }
        else if (!IsHead(k) && k != CatRepha)
        {
            broken = true;
        }

        if (!broken)
        {
            p++;
        }

        int guard = 0;
        while (p < n && guard++ < SyllableSupport.MaxSyllable)
        {
            byte x = c[p];
            if (x == CatH)
            {
                int q = p + 1;
                if (q < n && IsJoiner(c[q]))
                {
                    q++;
                }

                if (q < n && (IsHead(c[q]) || c[q] == CatSub))
                {
                    p = q + 1;
                    continue;
                }

                p = q;
                continue;
            }

            if (x is CatSub or CatN or CatMark or CatPre || (IsJoiner(x) && p > i))
            {
                p++;
                continue;
            }

            break;
        }

        return Math.Max(p, i + 1);
    }

    // ---- Reordering ----

    public override void Prepare(ShapeContext ctx)
    {
        GlyphBuffer buffer = ctx.Buffer;
        GlyphInfo[] info = buffer.Info;
        for (int s = 0; s < buffer.Count;)
        {
            int e = SyllableSupport.SyllableEnd(buffer, s);
            if (info[s].Cat != CatOther && e - s > 1 || (e - s == 1 && info[s].Cat == CatRepha))
            {
                Classes(info, s, e);
            }

            s = e;
        }
    }

    private void Classes(GlyphInfo[] info, int s, int e)
    {
        int head = -1;
        int start = s;
        bool kinzi = _script == ShapedScript.Myanmar && e - s >= 4 && info[s].CodePoint == 0x1004 && info[s + 1].CodePoint == 0x103A
            && info[s + 2].CodePoint == 0x1039 && IsHead(info[s + 3].Cat);
        if (info[s].Cat == CatRepha)
        {
            info[s].Mask = BitRphf;
            info[s].Pos = PosRepha;
            start = s + 1;
        }
        else if (kinzi)
        {
            for (int i = s; i < s + 3; i++)
            {
                info[i].Mask = BitRphf;
                info[i].Pos = PosRepha;
            }

            start = s + 3;
        }

        for (int i = start; i < e; i++)
        {
            if (IsHead(info[i].Cat))
            {
                head = i;
                break;
            }
        }

        if (head >= 0)
        {
            info[head].Flags |= GlyphFlags.Base;
        }

        bool inChain = true;
        for (int i = start; i < e; i++)
        {
            ref GlyphInfo g = ref info[i];
            switch (g.Cat)
            {
                case CatPre:
                    g.Pos = PosPre;
                    break;
                case CatMark:
                    inChain = false;
                    g.Pos = PosMark;
                    break;
                case CatH:
                case CatSub:
                case CatN:
                case CatBase:
                case CatGb:
                case CatCs:
                    g.Pos = inChain ? PosChain : PosMark;
                    break;
                default:
                    g.Pos = i > start ? info[i - 1].Pos : PosChain;
                    break;
            }

            // A consonant after a stacker continues the chain even after marks (Myanmar medials come before stacked consonants).
            if (g.Cat is CatBase or CatSub && i > start && info[i - 1].Cat == CatH)
            {
                g.Pos = PosChain;
                inChain = true;
            }
        }

        // The glyphs stay in logical order while the basic features run (their patterns expect it); the reordering comes after them.
    }

    public override void AfterStage(int stage, ShapeContext ctx)
    {
        GlyphBuffer buffer = ctx.Buffer;
        GlyphInfo[] info = buffer.Info;
        if (stage == 2 || stage == 4)
        {
            for (int i = 0; i < buffer.Count; i++)
            {
                info[i].Before = info[i].Glyph;
            }

            return;
        }

        if (stage == StRphf)
        {
            for (int i = 0; i < buffer.Count; i++)
            {
                if ((info[i].Mask & BitRphf) != 0 && info[i].Pos == PosRepha && info[i].Glyph != info[i].Before)
                {
                    info[i].Flags |= GlyphFlags.Reph;
                }
            }

            return;
        }

        if (stage == StPref)
        {
            for (int i = 0; i < buffer.Count; i++)
            {
                if ((info[i].Flags & GlyphFlags.Base) == 0 && info[i].Pos == PosChain && info[i].Glyph != info[i].Before)
                {
                    info[i].Flags |= GlyphFlags.Pref;
                }
            }

            return;
        }

        if (stage == StReorder)
        {
            for (int s = 0; s < buffer.Count;)
            {
                int e = SyllableSupport.SyllableEnd(buffer, s);
                if (e - s > 1)
                {
                    FinalReorder(info, s, e);
                }

                s = e;
            }
        }
    }

    private static void FinalReorder(GlyphInfo[] info, int s, int e)
    {
        // The reph (or kinzi) goes after the base and its stacked consonants, ahead of the marks.
        if ((info[s].Flags & GlyphFlags.Reph) != 0)
        {
            int b = FindBase(info, s, e);
            int t = e;
            for (int i = Math.Max(b + 1, s + 1); i < e; i++)
            {
                if (info[i].Pos >= PosMark)
                {
                    t = i;
                    break;
                }
            }

            info[s].Pos = PosAfterChain;
            SyllableSupport.MoveBefore(info, s, t);
        }

        // The pre-base reordering consonant (Khmer coeng + ro) goes just before the base.
        for (int i = s + 1; i < e; i++)
        {
            if ((info[i].Flags & GlyphFlags.Pref) != 0)
            {
                int b = FindBase(info, s, e);
                if (b >= 0 && b < i)
                {
                    SyllableSupport.MoveBefore(info, i, b);
                }

                break;
            }
        }

        // Pre-base vowels and medials go to the start of the cluster, in their logical order.
        int dest = s;
        for (int i = s; i < e; i++)
        {
            if (info[i].Cat == CatPre)
            {
                SyllableSupport.MoveBefore(info, i, dest);
                dest++;
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
}
