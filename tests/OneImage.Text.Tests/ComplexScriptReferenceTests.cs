using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using OneImage.Text.Fonts;
using OneImage.Text.Layout;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;

namespace OneImage.Text.Tests;

/// <summary>Indic, Universal Shaping Engine, Thai and Lao shaping against SixLabors.Fonts 2.1.3, used only through its public API.</summary>
public partial class ComplexScriptReferenceTests
{
    internal static readonly (string Script, string Font)[] Scripts =
    [
        ("devanagari", "NotoSansDevanagari-Subset-Regular.ttf"),
        ("bengali", "NotoSansBengali-Subset-Regular.ttf"),
        ("gurmukhi", "NotoSansGurmukhi-Subset-Regular.ttf"),
        ("gujarati", "NotoSansGujarati-Subset-Regular.ttf"),
        ("oriya", "NotoSansOriya-Subset-Regular.ttf"),
        ("tamil", "NotoSansTamil-Subset-Regular.ttf"),
        ("telugu", "NotoSansTelugu-Subset-Regular.ttf"),
        ("kannada", "NotoSansKannada-Subset-Regular.ttf"),
        ("malayalam", "NotoSansMalayalam-Subset-Regular.ttf"),
        ("khmer", "NotoSansKhmer-Subset-Regular.ttf"),
        ("myanmar", "NotoSansMyanmar-Subset-Regular.ttf"),
        ("sinhala", "NotoSansSinhala-Subset-Regular.ttf"),
        ("tibetan", "NotoSerifTibetan-Subset-Regular.ttf"),
        ("thai", "NotoSansThai-Subset-Regular.ttf"),
        ("lao", "NotoSansLao-Subset-Regular.ttf"),
        ("balinese", "NotoSansBalinese-Subset-Regular.ttf"),
        ("javanese", "NotoSansJavanese-Subset-Regular.ttf"),
    ];

    private static readonly Dictionary<string, (FontCollection Collection, FontFace Face)> Loaded = [];

    internal static string Expand(string token) =>
        Regex.Replace(token, @"\\+u([0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    internal static List<string> CasesOf(string script)
    {
        var cases = new List<string>();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "testdata", "complex", script + ".txt")))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            cases.AddRange(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Expand));
        }

        return cases;
    }

    internal static (FontCollection Collection, FontFace Face) Load(string file)
    {
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(file, out var v))
            {
                var c = new FontCollection();
                c.Add(TestFonts.PathOf(file));
                v = (c, TestFonts.Face(file));
                Loaded[file] = v;
            }

            return v;
        }
    }

    internal static List<(int Glyph, Vector2 Origin, bool HasOutline)> Ours(string file, string text, out TextLayout layout)
    {
        var (collection, face) = Load(file);
        var font = collection.First().CreateFont(face.UnitsPerEm);
        layout = TextLayout.Create(text, new TextOptions(font));
        var ours = new List<(int, Vector2, bool)>();
        foreach (LayoutLine line in layout.Lines)
        {
            for (int k = line.GlyphStart; k < line.GlyphEnd; k++)
            {
                ShapedGlyph g = layout.Glyphs[k];
                if (g.IsSpace)
                {
                    continue;
                }

                Outline o = face.GetOutline(g.GlyphId);
                ours.Add((g.GlyphId, new Vector2(line.Left + layout.PenX[k] + g.XOffset, line.Baseline - g.YOffset), !o.IsEmpty));
            }
        }

        return ours;
    }

    internal static GlyphIdRecorder Theirs(string file, string text)
    {
        var face = Load(file).Face;
        var rec = new GlyphIdRecorder();
        TextRenderer.RenderTextTo(rec, text, new RichTextOptions(Reference.Font(file, face.UnitsPerEm)));
        return rec;
    }

    /// <summary>Compares visible glyph ids exactly and origins within 0.01 font unit; returns an empty string when they agree.</summary>
    internal static string Compare(string file, string text, out double worst)
    {
        var face = Load(file).Face;
        var ours = Ours(file, text, out _).Where(g => g.HasOutline).ToList();
        List<(int GlyphId, Vector2 Min, bool HasOutline)> theirs;
        worst = 0;
        try
        {
            theirs = Theirs(file, text).Glyphs.Where(g => g.HasOutline).ToList();
        }
        catch (Exception e) when (e is NullReferenceException or IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            return "REFERENCE-THROWS";
        }

        string a = string.Join(' ', ours.Select(g => g.Glyph));
        string b = string.Join(' ', theirs.Select(g => g.GlyphId));
        var theirOrigins = theirs.Select(t =>
        {
            Outline o = face.GetOutline(t.GlyphId);
            float minX = float.MaxValue, minY = float.MaxValue;
            for (int k = 0; k + 1 < o.Coords.Length; k += 2)
            {
                minX = Math.Min(minX, o.Coords[k]);
                minY = Math.Min(minY, -o.Coords[k + 1]);
            }

            return (t.GlyphId, Origin: t.Min - new Vector2(minX, minY));
        }).ToList();
        string detail = $"ours {string.Join(' ', ours.Select(g => $"{g.Glyph}@{g.Origin.X:0.#},{g.Origin.Y:0.#}"))} | theirs {string.Join(' ', theirOrigins.Select(g => $"{g.GlyphId}@{g.Origin.X:0.#},{g.Origin.Y:0.#}"))}";
        if (a != b)
        {
            // The same glyphs at the same places in a different drawing order (the reference draws a dotted circle after its mark).
            if (ours.Count == theirs.Count)
            {
                var x = ours.OrderBy(g => g.Glyph).ThenBy(g => g.Origin.X).Select(g => (g.Glyph, g.Origin)).ToList();
                var y = theirOrigins.OrderBy(g => g.GlyphId).ThenBy(g => g.Origin.X).Select(g => (g.GlyphId, g.Origin)).ToList();
                if (x.Zip(y).All(p => p.First.Glyph == p.Second.GlyphId && Math.Abs(p.First.Origin.X - p.Second.Origin.X) < 0.01 && Math.Abs(p.First.Origin.Y - p.Second.Origin.Y) < 0.01))
                {
                    return "ORDER-ONLY";
                }
            }

            return detail;
        }

        for (int i = 0; i < ours.Count; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(theirOrigins[i].Origin.X - ours[i].Origin.X), Math.Abs(theirOrigins[i].Origin.Y - ours[i].Origin.Y)));
        }

        return worst < 0.01 ? "" : "origin difference " + detail;
    }

    public static TheoryData<string> ScriptNames()
    {
        var d = new TheoryData<string>();
        foreach ((string s, _) in Scripts)
        {
            d.Add(s);
        }

        return d;
    }

    internal static Dictionary<string, string> KnownDifferences(string script)
    {
        var known = new Dictionary<string, string>();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "testdata", "complex", "known-differences.txt")))
        {
            string[] f = line.Split('\t');
            if (f.Length == 3 && f[0] == script)
            {
                known[f[1]] = f[2];
            }
        }

        return known;
    }

    internal static string Hex(string text) => string.Join(' ', text.Select(c => ((int)c).ToString("X4")));

    /// <summary>
    /// Every case either matches the reference (glyph ids exactly, origins within 0.01 font unit), or makes the reference throw, or is
    /// listed in known-differences.txt with the rule we follow instead (see the complex scripts section of docs/text.md).
    /// </summary>
    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void CasesMatchTheReferenceOrAreKnownDifferences(string script)
    {
        string file = Scripts.First(s => s.Script == script).Font;
        var known = KnownDifferences(script);
        var cases = CasesOf(script);
        var failures = new List<string>();
        int same = 0, orderOnly = 0, throws = 0;
        foreach (string text in cases)
        {
            string hex = Hex(text);
            string diff = Compare(file, text, out _);
            if (diff == "REFERENCE-THROWS")
            {
                throws++;
            }
            else if (diff.Length == 0 || diff == "ORDER-ONLY")
            {
                same++;
                orderOnly += diff.Length == 0 ? 0 : 1;
                if (known.ContainsKey(hex))
                {
                    failures.Add($"[{hex}] matches now but is listed as a known difference");
                }
            }
            else if (!known.ContainsKey(hex))
            {
                failures.Add($"[{hex}] {diff}");
            }
        }

        TestReport.Line($"COMPLEX {script}: {same}/{cases.Count} cases match ({orderOnly} differ only in drawing order), {throws} where the reference throws, {cases.Count - same - throws} known differences");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }
}
