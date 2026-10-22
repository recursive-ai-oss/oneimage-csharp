using System.Text;
using SixLabors.Fonts;

namespace OneImage.Text.Tests;

/// <summary>Layout and measurement against SixLabors.Fonts 2.1.3's TextMeasurer, through its public API only.</summary>
public class MeasureReferenceTests
{
    private const string Pangram = "The quick brown fox jumps over the lazy dog";

    private static readonly string[] Texts =
    [
        "A", "AV", "To", "Hello", "Hello World", "Hello World ", "a  b", " indented",
        Pangram, "Line one\nLine two\nLine three", "Para one\r\nPara two", "First\n\nThird", "ends with newline\n",
        "well-known multi-part self-made words", "Supercalifragilisticexpialidocious word", "aaa    bbb    ccc",
        "\u00c5ngstr\u00f6m caf\u00e9 \u0416\u0443\u0440\u043d\u0430\u043b \u0391\u03b8\u03ae\u03bd\u03b1",
        "Waves of AWAY Tom Va. (parens) 12,345.67 \u20ac \"quoted\"",
        "A fairly long paragraph of ordinary words that should wrap onto several lines when the wrapping length is small enough to force it.",
    ];

    [Fact]
    public void LineCountsAndAdvancesMatchTheReference()
    {
        var diffs = new List<string>();
        int cases = 0;
        foreach (float size in new[] { 12f, 20f, 33.3f })
        {
            foreach (float dpi in new[] { 72f, 96f })
            {
                foreach (float wrap in new[] { 0f, 60f, 130f, 300f })
                {
                    foreach (float spacing in new[] { 1f, 1.5f })
                    {
                        var mine = new TextOptions(TestFonts.Noto(size)) { Dpi = dpi, WrappingLength = wrap, LineSpacing = spacing };
                        var theirs = Reference.Options(Reference.Font(TestFonts.Regular, size), mine);
                        foreach (string text in Texts)
                        {
                            cases++;
                            TextMetrics m = TextMeasurer.Measure(text, mine);
                            int lines = SixLabors.Fonts.TextMeasurer.CountLines(text, theirs);
                            FontRectangle adv = SixLabors.Fonts.TextMeasurer.MeasureAdvance(text, theirs);
                            string tag = $"size={size} dpi={dpi} wrap={wrap} ls={spacing} '{text.Replace("\n", "\\n").Replace("\r", "\\r")}'";
                            if (m.LineCount != lines)
                            {
                                diffs.Add($"{tag}: lines {m.LineCount} vs {lines}");
                            }

                            if (Math.Abs(m.Advance.Width - adv.Width) > 0.01f || Math.Abs(m.Advance.Height - adv.Height) > 0.01f)
                            {
                                diffs.Add($"{tag}: advance {m.Advance.Width:F3}x{m.Advance.Height:F3} vs {adv.Width:F3}x{adv.Height:F3}");
                            }
                        }
                    }
                }
            }
        }

        Report(diffs, cases);
    }

    [Fact]
    public void InkBoundsMatchTheReference()
    {
        var diffs = new List<string>();
        int cases = 0;
        foreach (float size in new[] { 14f, 40f })
        {
            foreach (float wrap in new[] { 0f, 130f })
            {
                foreach (HorizontalAlignment ha in Enum.GetValues<HorizontalAlignment>())
                {
                    foreach (VerticalAlignment va in Enum.GetValues<VerticalAlignment>())
                    {
                        foreach (TextAlignment ta in Enum.GetValues<TextAlignment>())
                        {
                            var mine = new TextOptions(TestFonts.Noto(size))
                            {
                                WrappingLength = wrap,
                                Origin = new System.Numerics.Vector2(100.5f, 80.25f),
                                HorizontalAlignment = ha,
                                VerticalAlignment = va,
                                TextAlignment = ta,
                            };
                            var theirs = Reference.Options(Reference.Font(TestFonts.Regular, size), mine);
                            foreach (string text in new[] { "Hello World", Pangram, "Line one\nLine two longer\nIII", "AV To Va" })
                            {
                                cases++;
                                System.Drawing.RectangleF b = TextMeasurer.MeasureBounds(text, mine);
                                FontRectangle r = SixLabors.Fonts.TextMeasurer.MeasureBounds(text, theirs);
                                double d = Math.Max(Math.Max(Math.Abs(b.X - r.X), Math.Abs(b.Y - r.Y)), Math.Max(Math.Abs(b.Width - r.Width), Math.Abs(b.Height - r.Height)));
                                if (d > 0.05)
                                {
                                    diffs.Add($"size={size} wrap={wrap} {ha}/{va}/{ta} '{text.Replace("\n", "\\n")}': ({b.X:F3},{b.Y:F3},{b.Width:F3},{b.Height:F3}) vs ({r.X:F3},{r.Y:F3},{r.Width:F3},{r.Height:F3})");
                                }
                            }
                        }
                    }
                }
            }
        }

        Report(diffs, cases);
    }

    [Fact]
    public void KerningAdvancesMatchTheReferenceInFontUnits()
    {
        // One pixel per font unit (size = units per em at 72 dpi), so advances are font units. Covers GPOS kerning (regular), the
        // legacy kern table (bold) and CFF with GPOS (CJK face).
        var cases = new (string File, int Face, Func<float, Font> Make, string[] Pairs)[]
        {
            (TestFonts.Regular, 0, s => TestFonts.Noto(s), ["AV", "To", "Wa", "AW", "VA", "Ty", "r.", "P,", "Yo", "LT", "ff", "fi", "\u0410\u0412", "\u03a4\u03b1"]),
            (TestFonts.BoldKern, 1, s => TestFonts.Noto(s, FontStyle.Bold), ["AV", "AT", "VA", "To", "Wa", "AW", "xy"]),
            (TestFonts.Cff, 0, s => TestFonts.NotoCjk(s), ["AV", "To", "Wa", "Av", "\u3042\u3044"]),
        };
        int compared = 0;
        int kerned = 0;
        foreach ((string file, int faceIndex, Func<float, Font> make, string[] pairs) in cases)
        {
            Font font = make(1000);
            var refFont = Reference.Font(file == TestFonts.BoldKern ? TestFonts.Collection : file, 1000, faceIndex);
            foreach (string pair in pairs)
            {
                foreach (KerningMode mode in Enum.GetValues<KerningMode>())
                {
                    var mine = new TextOptions(font) { Kerning = mode };
                    float ours = TextMeasurer.MeasureAdvance(pair, mine).Width;
                    float theirs = SixLabors.Fonts.TextMeasurer.MeasureAdvance(pair, Reference.Options(refFont, mine)).Width;
                    Assert.True(Math.Abs(ours - theirs) < 0.01f, $"{file} '{pair}' {mode}: {ours} vs {theirs}");
                    compared++;
                    if (mode == KerningMode.Normal && Math.Abs(ours - TextMeasurer.MeasureAdvance(pair, new TextOptions(font) { Kerning = KerningMode.None }).Width) > 0.5f)
                    {
                        kerned++;
                    }
                }
            }
        }

        Assert.True(compared >= 40);
        Assert.True(kerned >= 8, $"only {kerned} pairs were actually kerned");
    }

    [Fact]
    public void CharacterBoxesAndLinePositionsMatchTheReference()
    {
        var diffs = new List<string>();
        int cases = 0;
        foreach (HorizontalAlignment ha in Enum.GetValues<HorizontalAlignment>())
        {
            foreach (TextAlignment ta in Enum.GetValues<TextAlignment>())
            {
                foreach (float wrap in new[] { 0f, 110f })
                {
                    var mine = new TextOptions(TestFonts.Noto(16)) { WrappingLength = wrap, HorizontalAlignment = ha, TextAlignment = ta, Origin = new System.Numerics.Vector2(150, 40), LineSpacing = 1.25f };
                    var theirs = Reference.Options(Reference.Font(TestFonts.Regular, 16), mine);
                    foreach (string text in new[] { "Alpha beta\ngamma delta epsilon\nzeta", "One two three four six seven eight nine" })
                    {
                        cases++;
                        IReadOnlyList<GlyphBounds> a = TextMeasurer.GetCharacterBounds(text, mine);
                        SixLabors.Fonts.TextMeasurer.TryMeasureCharacterBounds(text, theirs, out var b);
                        SixLabors.Fonts.GlyphBounds[] bb = b.ToArray();
                        string tag = $"{ha}/{ta}/wrap={wrap} '{text.Replace("\n", "\\n")}'";
                        int j = 0;
                        foreach (GlyphBounds g in a)
                        {
                            if (char.IsWhiteSpace((char)g.CodePoint))
                            {
                                continue;
                            }

                            while (j < bb.Length && char.IsWhiteSpace((char)bb[j].Codepoint.Value))
                            {
                                j++;
                            }

                            FontRectangle r = bb[j++].Bounds;
                            if (Math.Abs(g.Ink.X - r.X) > 0.02f || Math.Abs(g.Ink.Width - r.Width) > 0.02f || Math.Abs(g.Ink.Y - r.Y) > 0.02f || Math.Abs(g.Ink.Height - r.Height) > 0.02f)
                            {
                                diffs.Add($"{tag}: '{(char)g.CodePoint}' ({g.Ink.X:F2},{g.Ink.Y:F2},{g.Ink.Width:F2},{g.Ink.Height:F2}) vs ({r.X:F2},{r.Y:F2},{r.Width:F2},{r.Height:F2})");
                                break;
                            }
                        }
                    }
                }
            }
        }

        Report(diffs, cases);
    }

    private static void Report(List<string> diffs, int cases)
    {
        if (diffs.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{diffs.Count} of {cases} cases differ");
            foreach (string d in diffs.Take(25))
            {
                sb.AppendLine(d);
            }

            Assert.Fail(sb.ToString());
        }
    }
}
