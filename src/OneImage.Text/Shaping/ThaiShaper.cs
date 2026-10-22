using OneImage.Text.Fonts;

namespace OneImage.Text.Shaping;

/// <summary>
/// Thai and Lao. The generic features apply as for Latin, with the script tags of their own. SARA AM (U+0E33, Lao U+0EB3) is split into
/// NIKHAHIT (U+0E4D, Lao U+0ECD) and SARA AA (U+0E32, Lao U+0EB2), and the nikhahit moves in front of any tone marks that precede
/// the sara am, so that the tone mark sits on top of it, as the Thai script document describes.
/// </summary>
internal sealed class ThaiShaper : ScriptShaper
{
    public static readonly ThaiShaper Thai = new(Fonts.Be.Tag("thai"), 0xE33, 0xE4D, 0xE32, 0xE48, 0xE4B);
    public static readonly ThaiShaper Lao = new(Fonts.Be.Tag("lao "), 0xEB3, 0xECD, 0xEB2, 0xEC8, 0xECB);

    private static readonly uint[][] Stages =
    [
        [Tag.Ccmp, Tag.Locl, Tag.Rtlm],
        [Tag.Rlig, Tag.Liga, Tag.Clig, Tag.Calt, Tag.Rclt],
    ];

    private readonly uint _tag;
    private readonly int _saraAm;
    private readonly int _nikhahit;
    private readonly int _saraAa;
    private readonly int _firstTone;
    private readonly int _lastTone;

    private ThaiShaper(uint tag, int saraAm, int nikhahit, int saraAa, int firstTone, int lastTone)
    {
        _tag = tag;
        _saraAm = saraAm;
        _nikhahit = nikhahit;
        _saraAa = saraAa;
        _firstTone = firstTone;
        _lastTone = lastTone;
    }

    public override uint[][] GsubStages => Stages;

    public override uint[] ScriptTags(uint script) => [_tag, Tag.Dflt, Tag.DfltLower, Tag.Latn];

    public override bool DefaultOn(uint tag) =>
        tag == Tag.Ccmp || tag == Tag.Locl || tag == Tag.Rlig || tag == Tag.Liga || tag == Tag.Clig || tag == Tag.Calt
        || tag == Tag.Rclt || tag == Tag.Kern || tag == Tag.Mark || tag == Tag.Mkmk || tag == Tag.Curs;

    public override GlyphBuffer? BuildRun(FontFace face, ReadOnlySpan<int> codePoints, ReadOnlySpan<int> clusters)
    {
        if (codePoints.IndexOf(_saraAm) < 0)
        {
            return null;
        }

        var buffer = new GlyphBuffer(codePoints.Length + 4);
        for (int k = 0; k < codePoints.Length; k++)
        {
            int cp = codePoints[k];
            if (cp != _saraAm)
            {
                buffer.Add(SyllableSupport.MakeInfo(face, cp, clusters[k], 0));
                continue;
            }

            // Tone marks directly before the sara am: the nikhahit goes in front of them.
            int at = buffer.Count;
            while (at > 0 && buffer.Info[at - 1].CodePoint >= _firstTone && buffer.Info[at - 1].CodePoint <= _lastTone)
            {
                at--;
            }

            GlyphInfo nik = SyllableSupport.MakeInfo(face, _nikhahit, at < buffer.Count ? buffer.Info[at].Cluster : clusters[k], 0);
            buffer.Insert(at, 1);
            buffer.Info[at] = nik;
            buffer.Add(SyllableSupport.MakeInfo(face, _saraAa, clusters[k], 0));
        }

        return buffer;
    }
}
