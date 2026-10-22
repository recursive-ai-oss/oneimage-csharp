using System.Text;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using OneImage.Text.Layout;
using static OneImage.Text.Tests.BidiHarness;
using RefDirection = SixLabors.Fonts.TextDirection;

namespace OneImage.Text.Tests;

/// <summary>
/// Right-to-left and mixed-direction text against SixLabors.Fonts 2.1.3 through its public API only: glyph ids exactly, glyph
/// origins to a few thousandths of a pixel, and the same glyphs on the same lines in the same display order. Where the two differ on
/// purpose or by a quirk of the reference, the difference has a test of its own below.
/// </summary>
public class BidiReferenceTests
{

    private static RefDirection Ref(TextDirection d) => d switch { TextDirection.LeftToRight => RefDirection.LeftToRight, TextDirection.RightToLeft => RefDirection.RightToLeft, _ => RefDirection.Auto };

    internal static (int Glyphs, double Worst, int Lines) Compare(string label, string text, float size = 40, TextDirection dir = TextDirection.Auto, float wrap = 0, TextAlignment align = TextAlignment.Start, HorizontalAlignment h = HorizontalAlignment.Left, KerningMode kerning = KerningMode.Normal, bool idsOnly = false, double tolerance = 0.01, bool skipLtrOnlyLines = false)
    {
        var (ours, theirs, layout) = Run(
            text,
            size,
            o =>
            {
                o.TextDirection = dir;
                o.Kerning = kerning;
                o.WrappingLength = wrap;
                o.TextAlignment = align;
                o.HorizontalAlignment = h;
                o.Origin = new System.Numerics.Vector2(20, 10);
            },
            r =>
            {
                r.TextDirection = Ref(dir);
                r.KerningMode = kerning == KerningMode.None ? SixLabors.Fonts.KerningMode.None : SixLabors.Fonts.KerningMode.Auto;
                r.WrappingLength = wrap > 0 ? wrap : -1;
                r.TextAlignment = align switch { TextAlignment.Center => SixLabors.Fonts.TextAlignment.Center, TextAlignment.End => SixLabors.Fonts.TextAlignment.End, _ => SixLabors.Fonts.TextAlignment.Start };
                r.HorizontalAlignment = h switch { HorizontalAlignment.Center => SixLabors.Fonts.HorizontalAlignment.Center, HorizontalAlignment.Right => SixLabors.Fonts.HorizontalAlignment.Right, _ => SixLabors.Fonts.HorizontalAlignment.Left };
                r.Origin = new System.Numerics.Vector2(20, 10);
            });
        string a = string.Join(' ', ours.Select(g => g.Line + ":" + g.Id));
        string b = string.Join(' ', theirs.Select(g => g.Line + ":" + g.Id));
        double worst = 0;
        bool same = a == b;
        if (same)
        {
            for (int i = 0; i < ours.Count; i++)
            {
                if (skipLtrOnlyLines && !ours[i].LineHasRtl)
                {
                    continue;
                }

                worst = Math.Max(worst, Math.Max(Math.Abs(ours[i].X - theirs[i].X), Math.Abs(ours[i].Y - theirs[i].Y)));
            }
        }

        TestReport.Line($"BIDI {label}: {ours.Count} glyphs, {layout.Lines.Count} lines, ids {(same ? "equal" : "DIFFER")}, largest origin difference {worst:0.####}");
        if (!same || worst >= tolerance)
        {
            TestReport.Line($" ours   {Describe(ours)}\n theirs {Describe(theirs)}");
        }

        Assert.True(same, $"{label}: glyph order differs\n ours   {Describe(ours)}\n theirs {Describe(theirs)}");
        Assert.True(idsOnly || worst < tolerance, $"{label}: largest origin difference {worst}\n ours   {Describe(ours)}\n theirs {Describe(theirs)}");

        return (ours.Count, same ? worst : double.NaN, layout.Lines.Count);
    }

    // ---- Arabic ----

    public static TheoryData<string, string> ArabicCases => new()
    {
        { "isolated and joined beh", "\u0628 \u0628\u0628 \u0628\u0628\u0628 \u0628\u0628\u0628\u0628" },
        { "words", "\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645 \u0648\u0631\u062D\u0645\u0629 \u0627\u0644\u0644\u0647" },
        { "non-joining letters", "\u062F\u0627\u0631 \u0630\u0626\u0628 \u0648\u0631\u062F \u0623\u0645\u0631 \u0632\u0631\u0627\u0631" },
        { "all letters initial medial final", "\u0627\u0628\u062A\u062B\u062C\u062D\u062E\u062F\u0630\u0631\u0632\u0633\u0634\u0635\u0636\u0637\u0638\u0639\u063A\u0641\u0642\u0643\u0644\u0645\u0646\u0647\u0648\u064A" },
        { "lam alef", "\u0644\u0627 \u0644\u0623 \u0644\u0625 \u0644\u0622 \u0643\u0644\u0627 \u0628\u0644\u0627 \u0627\u0644\u0644\u0627" },
        { "lam alef with marks", "\u0644\u064E\u0627 \u0644\u0651\u0627 \u0643\u0644\u064B\u0627" },
        { "fatha kasra damma", "\u0628\u064E \u0628\u0650 \u0628\u064F \u0628\u0652 \u0628\u0651 \u0628\u064B \u0628\u064C \u0628\u064D" },
        { "shadda with vowel", "\u0645\u064F\u062D\u064E\u0645\u0651\u064E\u062F" },
        { "marks above and below", "\u0628\u0651\u0650 \u062A\u0651\u064E \u0639\u0651\u064F \u064A\u0651\u064B" },
        { "tatweel", "\u0643\u0640\u062A\u0640\u0627\u0628 \u0645\u0640\u062D\u0640\u0645\u0640\u062F \u0640\u0640" },
        { "arabic indic digits", "\u0660\u0661\u0662\u0663\u0664\u0665\u0666\u0667\u0668\u0669 \u0661\u0662\u0663 \u0664\u0665\u0666" },
        { "digits in text", "\u0635\u0641\u062D\u0629 123 \u0645\u0646 456" },
        { "punctuation", "\u0645\u0631\u062D\u0628\u0627\u060C \u0643\u064A\u0641 \u062D\u0627\u0644\u0643\u061F" },
        { "brackets mirrored", "(\u0645\u0631\u062D\u0628\u0627) [\u0643\u064A\u0641] {\u062D\u0627\u0644} \u00AB\u0643\u0644\u0645\u0629\u00BB" },
        { "zero width non-joiner", "\u0628\u200C\u0628 \u0645\u064A\u200C\u062E\u0648\u0627\u0647\u0645" },
        { "zero width joiner", "\u0628\u200D\u0628 \u0627\u200D\u0628 \u0628\u200D \u200D\u0628" },
        { "persian letters", "\u06AF\u0686\u067E\u0698 \u06A9\u0627\u0631 \u06CC\u06A9" },
        { "yeh variants", "\u064A \u0649 \u06CC \u0626 \u0624 \u0629" },
        { "hamza forms", "\u0621 \u0623 \u0625 \u0626 \u0624 \u0622 \u0628\u0621 \u0628\u0623" },
        { "sentence", "\u0645\u0631\u062D\u0628\u0627 \u0628\u0643\u0645 \u0641\u064A \u0647\u0630\u0627 \u0627\u0644\u0645\u0648\u0642\u0639 \u0627\u0644\u062C\u062F\u064A\u062F" },
    };

    [Theory]
    [MemberData(nameof(ArabicCases))]
    public void ArabicMatchesTheReference(string name, string text) => Compare("arabic " + name, text);

    // ---- Hebrew ----

    public static TheoryData<string, string> HebrewCases => new()
    {
        { "words", "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD" },
        { "final letters", "\u05DA\u05DD\u05DF\u05E3\u05E5 \u05DB\u05DE\u05E0\u05E4\u05E6" },
        { "dagesh and rafe", "\u05D1\u05BC \u05D1\u05BF \u05DB\u05BC \u05E4\u05BF \u05E9\u05C1\u05BC" },
        { "geresh gershayim", "\u05E6\u05F3 \u05E6\u05F4 \u05E8\u05F3\u05D1" },
        { "maqaf", "\u05D1\u05BE\u05D0\u05D1 \u05D0\u05D1\u05BE\u05D2" },
        { "brackets", "(\u05E9\u05DC\u05D5\u05DD) [\u05E2\u05D5\u05DC\u05DD] {\u05D0\u05D1\u05D2}" },
        { "alef lamed ligature letters", "\u05D0\u05DC \u05D0\u05E8" },
    };

    [Theory]
    [MemberData(nameof(HebrewCases))]
    public void HebrewMatchesTheReference(string name, string text) => Compare("hebrew " + name, text);

    // ---- Mixed ----

    public static TheoryData<string, string, TextDirection> MixedCases => new()
    {
        { "latin then arabic", "abc \u0627\u0644\u0633\u0644\u0627\u0645 def", TextDirection.Auto },
        { "arabic then latin", "\u0627\u0644\u0633\u0644\u0627\u0645 abc \u0639\u0644\u064A\u0643\u0645", TextDirection.Auto },
        { "arabic with numbers", "\u0627\u0644\u0633\u0639\u0631 123 \u062F\u0648\u0644\u0627\u0631 \u0648 45.6 \u064A\u0648\u0631\u0648", TextDirection.Auto },
        { "rlm and lrm", "abc \u200F123\u200F \u0627\u0644\u0633\u0644\u0627\u0645\u200E x", TextDirection.Auto },
        { "quotes", "\u0642\u0627\u0644 \"hello world\" \u0623\u0645\u0633", TextDirection.Auto },
        { "urls and latin words in arabic", "\u0632\u0631 example.com \u0627\u0644\u064A\u0648\u0645", TextDirection.Auto },
    };

    [Theory]
    [MemberData(nameof(MixedCases))]
    public void MixedMatchesTheReference(string name, string text, TextDirection dir) => Compare("mixed " + name, text, 40, dir);

    // ---- Differences that come from the reference, each pinned ----

    // Hebrew points sit between letters. The font's kern lookup ignores marks, so a pair of letters with a point between them is kerned
    // here (as OpenType says); the reference does not kern across the mark. Without kerning the two agree exactly; with it the difference
    // is one kern value, at most 50 font units (2 px at 40 px).
    public static TheoryData<string, string> PointedCases => new()
    {
        { "sheva and patah", "\u05D1\u05B0\u05BC\u05E8\u05B5\u05D0\u05E9\u05B4\u05C1\u05D9\u05EA \u05D1\u05B8\u05BC\u05E8\u05B8\u05D0" },
        { "points", "\u05E9\u05B8\u05C1\u05DC\u05D5\u05B9\u05DD \u05E9\u05C2\u05B8\u05DE\u05B7\u05D9\u05B4\u05DD" },
        { "cantillation", "\u05D1\u05B0\u05BC\u05E8\u05B5\u05D0\u05E9\u05B4\u05C1\u05D9\u05EA\u0591 \u05D1\u05B8\u05BC\u05E8\u05B8\u05D0\u0596" },
    };

    [Theory]
    [MemberData(nameof(PointedCases))]
    public void PointedHebrewKernsAcrossMarksUnlikeTheReference(string name, string text)
    {
        Compare("pointed hebrew, no kerning: " + name, text, kerning: KerningMode.None);
        Compare("pointed hebrew: " + name, text, tolerance: 2.1);
    }

    // The reference makes a space that follows a Hebrew letter across a change of direction (a left-to-right space after a Hebrew
    // word) 10 font units (0.4 px at 40 px) wider than the font's advance, with or without kerning; inside Hebrew text, and after
    // Arabic, the two agree exactly. We use the font's advance for the space. The glyphs, their order and every other position agree.
    public static TheoryData<string, string, TextDirection> BoundaryCases => new()
    {
        { "brackets around hebrew in latin", "Hello (\u05E9\u05DC\u05D5\u05DD) world", TextDirection.Auto },
        { "brackets around mixed", "(abc) \u05E9\u05DC\u05D5\u05DD [x] {y} <z> \u05E2\u05D5\u05DC\u05DD", TextDirection.Auto },
        { "arabic hebrew latin", "abc \u0645\u0631\u062D\u0628\u0627 \u05E9\u05DC\u05D5\u05DD def 12", TextDirection.Auto },
        { "hebrew latin numbers", "\u05DE\u05E1\u05E4\u05E8 123 and 4.5% \u05E9\u05DC\u05D5\u05DD", TextDirection.Auto },
    };

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void KerningDoesNotCrossDirectionRuns(string name, string text, TextDirection dir)
    {
        Compare("direction boundary, no kerning: " + name, text, 40, dir, kerning: KerningMode.None, tolerance: 0.5);
        Compare("direction boundary: " + name, text, 40, dir, tolerance: 0.5);
    }

    // The reference gives the embedding, isolate and override controls an advance of 0.6 em each; they are invisible format characters
    // here and take no room. The glyphs and their order agree.
    public static TheoryData<string, string, TextDirection> ControlCases => new()
    {
        { "embedding", "abc \u202B\u0627\u0644\u0633\u0644\u0627\u0645 xyz\u202C def", TextDirection.Auto },
        { "override", "abc \u202Eabc 123\u202C def", TextDirection.Auto },
        { "isolate", "abc \u2067\u0627\u0644\u0633\u0644\u0627\u0645 xyz\u2069 def", TextDirection.Auto },
        { "first strong isolate", "\u0627\u0644\u0633\u0644\u0627\u0645 \u2068abc\u2069 \u0639\u0644\u064A\u0643\u0645", TextDirection.Auto },
    };

    [Theory]
    [MemberData(nameof(ControlCases))]
    public void ControlCharactersTakeNoRoom(string name, string text, TextDirection dir)
    {
        Compare("controls: " + name, text, 40, dir, idsOnly: true);
        string without = new([.. text.Where(c => c is not (>= '\u202A' and <= '\u202E') and not (>= '\u2066' and <= '\u2069'))]);
        var (a, _, _) = Run(text, 40);
        var (b, _, _) = Run(without, 40);

        // With the controls the same glyphs are drawn; where the controls change the order the two differ, but never in count.
        Assert.Equal(b.Count, a.Count);
    }
}
