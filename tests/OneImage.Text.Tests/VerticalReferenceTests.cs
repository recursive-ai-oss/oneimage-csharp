using System.Text;
using SixLabors.Fonts;
using RefM = SixLabors.Fonts.TextMeasurer;
using RefO = SixLabors.Fonts.TextOptions;
using RLM = SixLabors.Fonts.LayoutMode;

namespace OneImage.Text.Tests;

/// <summary>Vertical and bottom-to-top layout against SixLabors.Fonts 2.1.3, through its public API only.</summary>
public class VerticalReferenceTests
{
    private static readonly string[] Texts =
    [
        "A", "ABC", "\u65e5", "\u65e5\u672c\u8a9e", "A\u65e5\u672c\u8a9eB", "\u3042\u3044\u3046\u3048\u304a\u304b\u304d", "\u65e5\u672c\n\u8a9e", "A\nB\u65e5",
        "AB CD EF", "\u65e5\u672c\u8a9e\u65e5\u672c\u8a9e\u65e5\u672c\u8a9e", "Hello, world 123", "\u65e5\u672c\u8a9e (2024) \u2014 ok",
    ];

    private static (RLM Ref, LayoutMode Mine)[] Modes =>
    [
        (RLM.HorizontalBottomTop, LayoutMode.HorizontalBottomTop),
        (RLM.VerticalLeftRight, LayoutMode.VerticalLeftRight),
        (RLM.VerticalRightLeft, LayoutMode.VerticalRightLeft),
        (RLM.VerticalMixedLeftRight, LayoutMode.VerticalMixedLeftRight),
        (RLM.VerticalMixedRightLeft, LayoutMode.VerticalMixedRightLeft),
    ];

    private static (string Name, string File)[] Fonts =>
    [
        ("cjk", "NotoSansCJK-Subset.otf"),
        ("latin", "NotoSans-Subset-Regular.ttf"),
    ];

    [Fact]
    public void MeasurementAndCharacterBoxesMatchTheReference()
    {
        var sb = new StringBuilder();
        int cases = 0, exact = 0, differing = 0;
        foreach ((string fname, string file) in Fonts)
        {
            foreach ((RLM refMode, LayoutMode mode) in Modes)
            {
                foreach (string text in Texts)
                {
                    foreach (float wrap in new[] { 0f, 100f })
                    {
                        // Documented difference: the reference ends a line one opportunity early after an ideograph.
                        if (wrap > 0 && text.Any(c => c >= 0x3000 && c != 0x2014))
                        {
                            continue;
                        }

                        foreach (float size in new[] { 40f, 24f })
                        {
                            // Documented difference: the reference widens a mixed-mode column that holds both upright and
                            // rotated characters by 0.282 em in this font; we keep every column one line pitch wide.
                            bool bothKinds = text.Any(c => c >= 0x3000 && c != 0x2014) && text.Any(c => c < 0x3000 && !char.IsWhiteSpace(c) || c == 0x2014);
                            if (bothKinds && mode is LayoutMode.VerticalMixedLeftRight or LayoutMode.VerticalMixedRightLeft)
                            {
                                differing++;
                                continue;
                            }

                            cases++;
                            var mine = new TextOptions(fname == "cjk" ? TestFonts.NotoCjk(size) : TestFonts.Noto(size)) { LayoutMode = mode, Origin = new(100, 100), WrappingLength = wrap };
                            var theirs = Reference.Options(Reference.Font(file, size), mine);
                            theirs.LayoutMode = refMode;
                            string tag = $"{fname} {mode} wrap={wrap} size={size} '{text.Replace("\n", "\\n")}'";
                            TextMetrics m = TextMeasurer.Measure(text, mine);
                            var adv = RefM.MeasureAdvance(text, theirs);
                            var problems = new List<string>();
                            if (Math.Abs(m.Advance.Width - adv.Width) > 0.02f || Math.Abs(m.Advance.Height - adv.Height) > 0.02f)
                            {
                                problems.Add($"advance {m.Advance.Width:F2}x{m.Advance.Height:F2} vs {adv.Width:F2}x{adv.Height:F2}");
                            }

                            RefM.TryMeasureCharacterBounds(text, theirs, out var cb);
                            var mineBounds = TextMeasurer.GetCharacterBounds(text, mine);
                            foreach (var g in cb.ToArray())
                            {
                                var mb = mineBounds.FirstOrDefault(b => b.Index == g.StringIndex);
                                if (char.IsWhiteSpace(text[g.StringIndex]))
                                {
                                    continue;
                                }

                                if (mb.Ink.IsEmpty && g.Bounds.Width > 0)
                                {
                                    problems.Add($"char {g.StringIndex} missing");
                                    continue;
                                }

                                float d = Math.Max(Math.Max(Math.Abs(mb.Ink.X - g.Bounds.X), Math.Abs(mb.Ink.Y - g.Bounds.Y)), Math.Max(Math.Abs(mb.Ink.Width - g.Bounds.Width), Math.Abs(mb.Ink.Height - g.Bounds.Height)));
                                if (d > 0.03f)
                                {
                                    problems.Add($"char {g.StringIndex} ink {mb.Ink.X:F2},{mb.Ink.Y:F2},{mb.Ink.Width:F2},{mb.Ink.Height:F2} vs {g.Bounds.X:F2},{g.Bounds.Y:F2},{g.Bounds.Width:F2},{g.Bounds.Height:F2}");
                                }
                            }

                            if (problems.Count == 0)
                            {
                                exact++;
                            }
                            else
                            {
                                sb.AppendLine($"{tag}: {string.Join("; ", problems.Take(3))}");
                            }
                        }
                    }
                }
            }
        }

        TestReport.Line($"VERTICAL {cases} cases compared, {exact} equal; {differing} mixed-column cases are a documented difference");
        Assert.True(cases > 200, "too few cases ran");
        Assert.True(exact == cases, sb.ToString());
    }

    [Fact]
    public void AlignmentsMatchTheReference()
    {
        var sb = new StringBuilder();
        int cases = 0, bad = 0;
        foreach ((RLM refMode, LayoutMode mode) in Modes.Skip(1))
        {
            foreach (string text in new[] { "ABC\nDEFGH\nI", "\u65e5\u672c\u8a9e\n\u65e5" })
            {
                foreach (HorizontalAlignment ha in Enum.GetValues<HorizontalAlignment>())
                {
                    foreach (VerticalAlignment va in Enum.GetValues<VerticalAlignment>())
                    {
                        foreach (TextAlignment ta in Enum.GetValues<TextAlignment>())
                        {
                            foreach (float wrap in new[] { 0f, 220f })
                            {
                                cases++;
                                var mine = new TextOptions(TestFonts.NotoCjk(30)) { LayoutMode = mode, Origin = new(150, 150), HorizontalAlignment = ha, VerticalAlignment = va, TextAlignment = ta, WrappingLength = wrap };
                                var theirs = Reference.Options(Reference.Font("NotoSansCJK-Subset.otf", 30), mine);
                                theirs.LayoutMode = refMode;
                                string t = text.Replace("\\n", "\n");
                                RefM.TryMeasureCharacterBounds(t, theirs, out var cb);
                                var mb = TextMeasurer.GetCharacterBounds(t, mine);
                                var diffs = new List<string>();
                                foreach (var g in cb.ToArray())
                                {
                                    if (char.IsWhiteSpace(t[g.StringIndex]))
                                    {
                                        continue;
                                    }

                                    var m = mb.First(b => b.Index == g.StringIndex).Ink;
                                    if (Math.Abs(m.X - g.Bounds.X) > 0.03f || Math.Abs(m.Y - g.Bounds.Y) > 0.03f)
                                    {
                                        diffs.Add($"{g.StringIndex}: {m.X:F2},{m.Y:F2} vs {g.Bounds.X:F2},{g.Bounds.Y:F2}");
                                    }
                                }

                                if (diffs.Count > 0)
                                {
                                    bad++;
                                    sb.AppendLine($"{mode} {ha} {va} {ta} wrap={wrap} '{t.Replace("\n", "\\n")}': {string.Join("; ", diffs.Take(2))}");
                                }
                            }
                        }
                    }
                }
            }
        }

        TestReport.Line($"VERTICAL-ALIGN {cases} cases, {cases - bad} equal");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vertical-align-diffs.txt"), sb.ToString());
        Assert.True(bad == 0, sb.ToString());
    }
}
