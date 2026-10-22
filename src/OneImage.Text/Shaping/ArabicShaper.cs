using OneImage.Text.Unicode;

namespace OneImage.Text.Shaping;

/// <summary>Arabic-style joining shaping (Arabic, Syriac, N'Ko): works out each letter's joining form and limits the form features to it.</summary>
/// <remarks>
/// Follows the OpenType script-development notes for Arabic: the joining type of each character comes from ArabicShaping.txt
/// (transparent marks are skipped when looking for the neighbors), the isol/fina/medi/init lookups run on the glyphs of their form
/// only, and the features run in the documented order: ccmp, locl, the joining forms, rlig, calt, then liga and the rest, with
/// kern, mark, mkmk and curs for positioning. The second and third forms of Syriac (fin2, fin3, med2) are listed in the order but
/// no glyph selects them, as only the Syriac Alaph letters use them.
/// </remarks>
internal sealed class ArabicShaper : ScriptShaper
{
    public static readonly ArabicShaper Instance = new();

    private const uint Isol = 1;
    private const uint Fina = 2;
    private const uint Fin2 = 4;
    private const uint Fin3 = 8;
    private const uint Medi = 16;
    private const uint Med2 = 32;
    private const uint Init = 64;

    private static readonly uint TagIsol = Fonts.Be.Tag("isol");
    private static readonly uint TagFina = Fonts.Be.Tag("fina");
    private static readonly uint TagFin2 = Fonts.Be.Tag("fin2");
    private static readonly uint TagFin3 = Fonts.Be.Tag("fin3");
    private static readonly uint TagMedi = Fonts.Be.Tag("medi");
    private static readonly uint TagMed2 = Fonts.Be.Tag("med2");
    private static readonly uint TagInit = Fonts.Be.Tag("init");
    private static readonly uint TagMset = Fonts.Be.Tag("mset");

    private static readonly uint[][] Stages =
    [
        [Tag.Ccmp, Tag.Locl, Tag.Rtlm],
        [TagIsol, TagFina, TagFin2, TagFin3, TagMedi, TagMed2, TagInit],
        [Tag.Rlig],
        [Tag.Calt],
        [Tag.Liga, Tag.Clig, Tag.Rclt, TagMset],
    ];

    public override uint[][] GsubStages => Stages;

    public override bool DefaultOn(uint tag) =>
        tag == Tag.Ccmp || tag == Tag.Locl || tag == TagIsol || tag == TagFina || tag == TagFin2 || tag == TagFin3 || tag == TagMedi
        || tag == TagMed2 || tag == TagInit || tag == Tag.Rlig || tag == Tag.Calt || tag == Tag.Liga || tag == Tag.Clig
        || tag == Tag.Rclt || tag == TagMset || tag == Tag.Kern || tag == Tag.Mark || tag == Tag.Mkmk || tag == Tag.Curs;

    public override uint FeatureMask(uint tag)
    {
        if (tag == TagIsol)
        {
            return Isol;
        }

        if (tag == TagFina)
        {
            return Fina;
        }

        if (tag == TagFin2)
        {
            return Fin2;
        }

        if (tag == TagFin3)
        {
            return Fin3;
        }

        if (tag == TagMedi)
        {
            return Medi;
        }

        if (tag == TagMed2)
        {
            return Med2;
        }

        return tag == TagInit ? Init : 0;
    }

    public override uint[] ScriptTags(uint script) => [script, Tag.Arab, Tag.Dflt, Tag.DfltLower, Tag.Latn];

    /// <summary>Sets each glyph's joining form bit from the joining types of its character and its neighbors.</summary>
    public override void PrepareRun(GlyphBuffer buffer)
    {
        int n = buffer.Count;

        // The type of the nearest non-transparent character before each position, found in one pass; the next one in a second.
        JoiningType prev = JoiningType.None;
        Span<JoiningType> before = n <= 256 ? stackalloc JoiningType[n] : new JoiningType[n];
        for (int i = 0; i < n; i++)
        {
            before[i] = prev;
            JoiningType t = UnicodeBidi.GetJoiningType(buffer.Info[i].CodePoint);
            if (t != JoiningType.Transparent)
            {
                prev = t;
            }
        }

        JoiningType next = JoiningType.None;
        for (int i = n - 1; i >= 0; i--)
        {
            ref GlyphInfo info = ref buffer.Info[i];
            JoiningType t = UnicodeBidi.GetJoiningType(info.CodePoint);
            if (t == JoiningType.Transparent)
            {
                // Marks and other transparent characters have no form of their own.
                info.Mask = 0;
                continue;
            }

            // A character joins to the one before it if it can join backwards and that one can join forwards, and the same to the next.
            bool joinsBack = t is JoiningType.Right or JoiningType.Dual or JoiningType.Causing
                && before[i] is JoiningType.Left or JoiningType.Dual or JoiningType.Causing;
            bool joinsNext = t is JoiningType.Left or JoiningType.Dual or JoiningType.Causing
                && next is JoiningType.Right or JoiningType.Dual or JoiningType.Causing;
            info.Mask = t == JoiningType.None || t == JoiningType.Causing
                ? 0
                : joinsBack ? (joinsNext ? Medi : Fina) : (joinsNext ? Init : Isol);
            next = t;
        }
    }
}
