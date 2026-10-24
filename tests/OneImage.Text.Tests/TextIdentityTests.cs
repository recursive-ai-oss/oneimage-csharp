using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using OneImage.Drawing;
using Path = System.IO.Path;

namespace OneImage.Text.Tests;

/// <summary>
/// Hashes of what the text package produces (shaping, measurement, character bounds, outlines and drawn pixels) over the committed test
/// fonts, so a change underneath the package that must not alter its output can prove it. The reference file was captured from the
/// released 1.8.1 code; set <c>ONEIMAGE_CAPTURE_TEXT</c> to a path to write a new one instead of comparing, and
/// <c>ONEIMAGE_TEXT_DETAIL</c> to any value to key every line by single case (for finding which case differs).
/// </summary>
public class TextIdentityTests
{
    private static readonly string[] Latin =
    [
        "Hello, World!",
        "AVA To Wa fi ffi ffl fl",
        "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. How vexingly quick daft zebras jump!",
        "Tab\there\tand more\t.",
        "line one\nline two\r\nline three\u2028four\u2029five\u0085six",
        "a\u00ADb\u200Bc\u200Cd\u200De soft\u00ADhyphen zero\u200Bwidth",
        "e\u0301\u0323 a\u0308 o\u0302 n\u0303 \u0041\u030A",
        "H\u2082O x\u00B2 1234567890 12:30 3.14 1,000",
        "\u041F\u0440\u0438\u0432\u0435\u0442 \u043C\u0438\u0440 \u0391\u0392\u0393 \u03B1\u03B2\u03B3 \u0386\u03AF",
        "   leading and trailing   ",
        "",
        "x",
        "emoji \U0001F600 pair \U0001F1E9\U0001F1EA end",
        "a\uD800b\uDC00c",
        "non\u00A0breaking space and \u2003em space and \u3000ideographic",
        "Wrapping-needs a few long-hyphenated-words and averyveryveryverylongwordthatnevergetsbroken here.",
    ];

    private static readonly string[] Bidi =
    [
        "abc \u05D0\u05D1\u05D2 def",
        "\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645 123",
        "(\u05D0\u05D1) [x] \u0627\u0628 - 12.5 \u05D3",
        "\u202Aabc\u202C \u202B\u05D0\u05D1\u202C \u2067x\u2069 \u2066\u05D0\u2069",
        "\u0644\u0627 \u0644\u0644\u0647 \u0645\u062D\u0645\u062F\u200C\u0627\u0628 \u0643\u062A\u0627\u0628",
        "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD, hello world 2024! \u05D6\u05D4 \u05DE\u05D1\u05D7\u05DF \u05D0\u05E8\u05D5\u05DA \u05DE\u05D0\u05D5\u05D3 \u05E9\u05DE\u05D5\u05E4\u05E8\u05D3 \u05DC\u05E9\u05D5\u05E8\u05D5\u05EA",
        "\u0627\u0628\u062A\u062B 12 \u0663\u0664 \u0627\u0644\u0639\u0631\u0628\u064A\u0629\n\u05D0\u05D1\u05D2 text \u05D3\u05D4",
        "  \u05D0\u05D1  ",
    ];

    private static readonly string[] Cjk =
    [
        "\u65E5\u672C\u8A9E\u306E\u30C6\u30AD\u30B9\u30C8\u3001\u7E26\u66F8\u304D\u3002ABC 123",
        "\u65E5\u672C\u8A9E A\u65E5 (\u62EC\u5F27) \u300C\u5F15\u7528\u300D\u30FC\u3063\u3083",
        "\u3042\u3044\u3046\u3048\u304A\u304B\u304D\u304F\u3051\u3053\u3055\u3057\u3059\u305B\u305D \u7121 \u91CD\u8981",
        "Latin \u65E5\u672C and \u8A9E mixed, 2024\u5E74",
    ];

    private static readonly (string File, string[] Texts)[] LayoutFonts =
    [
        ("NotoSans-Subset-Regular.ttf", Latin),
        ("NotoSans-Subset-Bold-kern.ttf", Latin),
        ("NotoSans-Shaping-Subset.ttf", Latin),
        ("NotoSans-Subset-Regular.woff", Latin),
        ("NotoSans-Subset-Regular.woff2", Latin),
        ("NotoSans-Shaping-Subset.woff2", Latin),
        ("NotoSans-Subset.ttc", Latin),
        ("NotoSans-Hinted-Subset.ttf", Latin),
        ("NotoSans-Bidi-Subset.ttf", Bidi),
        ("NotoSansArabic-Subset-Regular.ttf", Bidi),
        ("NotoSansHebrew-Subset-Regular.ttf", Bidi),
        ("NotoSansCJK-Subset.otf", Cjk),
        ("NotoSansCJK-Subset.woff2", Cjk),
    ];

    private static readonly string[] ScriptsWithCases = ["devanagari", "bengali", "gurmukhi", "gujarati", "oriya", "tamil", "telugu", "kannada", "malayalam", "khmer", "myanmar", "sinhala", "tibetan", "thai", "lao", "balinese", "javanese"];

    private sealed class Hasher
    {
        private readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Add(int v) => _h.AppendData(BitConverter.GetBytes(v));

        public void Add(float v) => Add(BitConverter.SingleToInt32Bits(v));

        public void Add(bool v) => Add(v ? 1 : 0);

        public void Add(string v)
        {
            Add(v.Length);
            _h.AppendData(Encoding.UTF8.GetBytes(v));
        }

        public string Finish() => Convert.ToHexString(_h.GetHashAndReset());
    }

    private static FontFamily Family(string file) => Cache.GetOrAdd(file, f => new FontCollection().Add(TestFonts.PathOf(f)));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FontFamily> Cache = new();

    private static void Add(Hasher h, RectangleFLike r)
    {
        h.Add(r.X);
        h.Add(r.Y);
        h.Add(r.W);
        h.Add(r.H);
    }

    private readonly record struct RectangleFLike(float X, float Y, float W, float H)
    {
        public static implicit operator RectangleFLike(System.Drawing.RectangleF r) => new(r.X, r.Y, r.Width, r.Height);
    }

    private static void AddMetrics(Hasher h, string text, TextOptions o)
    {
        try
        {
            TextMetrics m = TextMeasurer.Measure(text, o);
            h.Add(m.Advance.Width);
            h.Add(m.Advance.Height);
            Add(h, m.Bounds);
            h.Add(m.LineCount);
            foreach (LineMetrics l in m.Lines)
            {
                h.Add(l.Start);
                h.Add(l.Length);
                h.Add(l.Left);
                h.Add(l.Baseline);
                h.Add(l.Advance);
                h.Add(l.Ascender);
                h.Add(l.Descender);
                h.Add(l.LineHeight);
            }

            foreach (GlyphBounds g in TextMeasurer.GetCharacterBounds(text, o))
            {
                h.Add(g.Index);
                h.Add(g.CodePoint);
                h.Add(g.Line);
                Add(h, g.Bounds);
                Add(h, g.Ink);
            }

            h.Add(TextBuilder.GeneratePath(text, o).ToSvg());
        }
        catch (FontFormatException ex)
        {
            h.Add("FontFormatException " + ex.Message);
        }
    }

    private static void AddClusters(Hasher h, string text, Font font, bool rtl, ShapingOptions? options)
    {
        try
        {
            IReadOnlyList<ShapedCluster> clusters = TextShaping.ShapeRun(text, font, rtl, options);
            h.Add(clusters.Count);
            foreach (ShapedCluster c in clusters)
            {
                h.Add(c.Start);
                h.Add(c.Length);
                h.Add(c.Advance);
                h.Add(c.VerticalAdvance);
                h.Add(c.HasMissingGlyph);
                h.Add(c.GetOutline().ToSvg());
                h.Add(c.GetUprightOutline().ToSvg());
            }
        }
        catch (FontFormatException ex)
        {
            h.Add("FontFormatException " + ex.Message);
        }
    }

    private static string Hex(string s) => string.Concat(s.Select(c => c < 128 && c > 32 ? c.ToString() : $"<{(int)c:X}>"));

    private static void Emit(SortedDictionary<string, string> result, bool detail, string key, Action<Hasher> body)
    {
        var h = new Hasher();
        body(h);
        result[key] = h.Finish();
    }

    private static List<string> Cases(string script) => ComplexScriptReferenceTests.CasesOf(script);

    internal static SortedDictionary<string, string> Compute(bool detail)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);

        // Shaping runs: whole case lists per script, a few option sets, both directions where it matters.
        foreach ((string script, string file) in ComplexScriptReferenceTests.Scripts)
        {
            Font font = Family(file).CreateFont(32);
            List<string> cases = Cases(script);
            for (int variant = 0; variant < 3; variant++)
            {
                ShapingOptions? options = variant switch
                {
                    1 => new ShapingOptions { Kerning = KerningMode.None },
                    2 => new ShapingOptions { Language = "en", Features = { FontFeature.Off("liga"), FontFeature.On("dlig") } },
                    _ => null,
                };

                if (detail)
                {
                    for (int i = 0; i < cases.Count; i++)
                    {
                        string text = cases[i];
                        Emit(result, true, $"shape|{script}|v{variant}|{i}|{Hex(text)}", h => AddClusters(h, text, font, false, options));
                    }
                }
                else
                {
                    Emit(result, false, $"shape|{script}|v{variant}", h =>
                    {
                        foreach (string t in cases)
                        {
                            AddClusters(h, t, font, false, options);
                        }
                    });
                }
            }
        }

        foreach ((string file, string[] texts) in LayoutFonts)
        {
            Font font = Family(file).CreateFont(28);
            for (int variant = 0; variant < 5; variant++)
            {
                ShapingOptions? options = variant switch
                {
                    1 => new ShapingOptions { Kerning = KerningMode.None },
                    2 => new ShapingOptions { Language = "tr", Features = { FontFeature.On("smcp"), FontFeature.Off("liga") } },
                    3 => new ShapingOptions { Vertical = true },
                    _ => null,
                };

                for (int dir = 0; dir < (variant < 2 ? 2 : 1); dir++)
                {
                    bool rtl = dir == 1;
                    if (detail)
                    {
                        for (int i = 0; i < texts.Length; i++)
                        {
                            string text = texts[i];
                            Emit(result, true, $"shape|{file}|v{variant}|{rtl}|{i}", h => AddClusters(h, text, font, rtl, options));
                        }
                    }
                    else
                    {
                        Emit(result, false, $"shape|{file}|v{variant}|{rtl}", h =>
                        {
                            foreach (string t in texts)
                            {
                                AddClusters(h, t, font, rtl, options);
                            }
                        });
                    }
                }
            }
        }

        // Layout: every font against a spread of option sets.
        var configs = new List<(string Name, Action<TextOptions> Apply)>
        {
            ("default", _ => { }),
            ("wrap90", o => o.WrappingLength = 90),
            ("wrap160-interword", o => { o.WrappingLength = 160; o.TextJustification = TextJustification.InterWord; }),
            ("wrap120-intercharacter", o => { o.WrappingLength = 120; o.TextJustification = TextJustification.InterCharacter; }),
            ("wrap140-center", o => { o.WrappingLength = 140; o.TextAlignment = TextAlignment.Center; o.HorizontalAlignment = HorizontalAlignment.Center; o.VerticalAlignment = VerticalAlignment.Center; }),
            ("wrap100-end-right", o => { o.WrappingLength = 100; o.TextAlignment = TextAlignment.End; o.HorizontalAlignment = HorizontalAlignment.Right; o.VerticalAlignment = VerticalAlignment.Bottom; }),
            ("rtl", o => o.TextDirection = TextDirection.RightToLeft),
            ("ltr", o => o.TextDirection = TextDirection.LeftToRight),
            ("rtl-wrap", o => { o.TextDirection = TextDirection.RightToLeft; o.WrappingLength = 110; }),
            ("auto-wrap", o => { o.TextDirection = TextDirection.Auto; o.WrappingLength = 110; o.TextAlignment = TextAlignment.End; }),
            ("spacing-tab", o => { o.LineSpacing = 1.5f; o.TabWidth = 2; o.Origin = new Vector2(7.5f, 3.25f); }),
            ("nokern", o => o.Kerning = KerningMode.None),
            ("features", o => { o.Features.Add(FontFeature.On("smcp")); o.Features.Add(FontFeature.Off("liga")); o.Features.Add(FontFeature.On("onum")); }),
            ("lang-tr", o => o.Language = "tr"),
            ("breakall", o => { o.WordBreaking = WordBreaking.BreakAll; o.WrappingLength = 70; }),
            ("keepall", o => { o.WordBreaking = WordBreaking.KeepAll; o.WrappingLength = 70; }),
            ("breakword", o => { o.WordBreaking = WordBreaking.BreakWord; o.WrappingLength = 60; }),
            ("vertical-lr", o => o.LayoutMode = LayoutMode.VerticalLeftRight),
            ("vertical-rl-wrap", o => { o.LayoutMode = LayoutMode.VerticalRightLeft; o.WrappingLength = 120; }),
            ("vertical-mixed-lr-wrap", o => { o.LayoutMode = LayoutMode.VerticalMixedLeftRight; o.WrappingLength = 150; }),
            ("vertical-mixed-rl", o => o.LayoutMode = LayoutMode.VerticalMixedRightLeft),
            ("bottom-top", o => { o.LayoutMode = LayoutMode.HorizontalBottomTop; o.WrappingLength = 100; }),
            ("hinted", o => o.HintingMode = HintingMode.Standard),
            ("dpi144", o => { o.Dpi = 144; o.WrappingLength = 200; }),
            ("size11", o => { o.Font = o.Font.WithSize(11); o.HintingMode = HintingMode.Standard; }),
            ("runs", o =>
            {
                o.WrappingLength = 180;
                o.TextRuns.Add(new TextRun(2, 6) { Font = o.Font.WithSize(44) });
                o.TextRuns.Add(new TextRun(8, 11) { TextAttributes = TextAttributes.Superscript, TextDecorations = TextDecorations.Underline });
                o.TextRuns.Add(new TextRun(11, 14) { TextAttributes = TextAttributes.Subscript, TextDecorations = TextDecorations.Strikeout | TextDecorations.Overline });
                o.TextRuns.Add(new TextRun(15, 20) { Font = o.Font.WithSize(16) });
            }),
            ("path-circle", o => o.Path = Drawing.Path.Circle(120, 100, 80)),
            ("path-line", o => o.Path = Drawing.Path.Polyline([new Vector2(0, 0), new Vector2(150, 40), new Vector2(300, 0), new Vector2(450, 60)])),
        };

        var fallback = Family("NotoSansCJK-Subset.otf");
        var fallbackLatin = Family("NotoSans-Subset-Regular.ttf");
        foreach ((string file, string[] texts) in LayoutFonts)
        {
            Font font = Family(file).CreateFont(28);
            foreach ((string name, Action<TextOptions> apply) in configs)
            {
                if (detail)
                {
                    for (int i = 0; i < texts.Length; i++)
                    {
                        string text = texts[i];
                        Emit(result, true, $"layout|{file}|{name}|{i}", h => AddMetrics(h, text, Make(font, apply)));
                    }
                }
                else
                {
                    Emit(result, false, $"layout|{file}|{name}", h =>
                    {
                        foreach (string t in texts)
                        {
                            AddMetrics(h, t, Make(font, apply));
                        }
                    });
                }
            }

            // Font fallback: the CJK face behind a Latin one and the other way round.
            FontFamily other = file.Contains("CJK", StringComparison.Ordinal) ? fallbackLatin : fallback;
            string[] mixed = [.. texts, "Latin \u65E5\u672C\u8A9E mix", "caf\u00E9 \u0391\u0392 \u65E5\u672C\u8A9E"];
            foreach (LayoutMode mode in new[] { LayoutMode.HorizontalTopBottom, LayoutMode.VerticalMixedLeftRight })
            {
                Emit(result, false, $"fallback|{file}|{mode}", h =>
                {
                    foreach (string t in mixed)
                    {
                        TextOptions o = Make(font, x => { x.LayoutMode = mode; x.WrappingLength = 150; });
                        o.FallbackFontFamilies.Add(other);
                        AddMetrics(h, t, o);
                    }
                });
            }
        }

        // Complex-script case lists through layout, one paragraph per script.
        foreach ((string script, string file) in ComplexScriptReferenceTests.Scripts)
        {
            Font font = Family(file).CreateFont(30);
            List<string> cases = Cases(script);
            foreach ((string name, Action<TextOptions> apply) in configs.Where(c => c.Name is "default" or "wrap160-interword" or "rtl" or "vertical-lr" or "hinted" or "features" or "runs"))
            {
                Emit(result, false, $"layout-script|{script}|{name}", h =>
                {
                    for (int i = 0; i < cases.Count; i += 4)
                    {
                        AddMetrics(h, string.Join(' ', cases.Skip(i).Take(4)), Make(font, apply));
                    }
                });
            }
        }

        // Pixels: hinted text, a CFF face, color glyphs in fonts, runs and fallbacks, and a stroked outline.
        var pixels = new (string Name, Func<Image> Render)[]
        {
            ("noto", () => Draw(Family("NotoSans-Subset-Regular.ttf").CreateFont(23), "Rendering: AVA fi 123", o => { })),
            ("hinted-11", () => Draw(Family("NotoSans-Hinted-Subset.ttf").CreateFont(11), "Hinted text sample, ggg HHH 0123", o => o.HintingMode = HintingMode.Standard)),
            ("hinted-17", () => Draw(Family("NotoSans-Hinted-Subset.ttf").CreateFont(17), "Hinted text sample, ggg HHH 0123", o => o.HintingMode = HintingMode.Standard)),
            ("cjk", () => Draw(Family("NotoSansCJK-Subset.otf").CreateFont(26), "\u65E5\u672C\u8A9E A\u3002", o => { })),
            ("cjk-vertical", () => Draw(Family("NotoSansCJK-Subset.otf").CreateFont(26), "\u65E5\u672C\u8A9E A\u3002", o => o.LayoutMode = LayoutMode.VerticalMixedRightLeft)),
            ("v0", () => Draw(Family("OneImageColorTest-V0.ttf").CreateFont(60), "A\u2600\u2601\u2602\u2603\u2604", o => { })),
            ("v0-palette1", () => Draw(Family("OneImageColorTest-V0.ttf").CreateFont(60), "\u2600\u2601", o => o.ColorPaletteIndex = 1)),
            ("v0-nocolor", () => Draw(Family("OneImageColorTest-V0.ttf").CreateFont(60), "\u2600\u2601", o => o.ColorFontSupport = ColorFontSupport.None)),
            ("v1", () => Draw(Family("OneImageColorTest-V1.ttf").CreateFont(60), "\u2610\u2600\u2601", o => { })),
            ("runs", () => Draw(Family("NotoSans-Subset-Regular.ttf").CreateFont(30), "Rich text runs here", o =>
            {
                o.TextRuns.Add(new TextRun(0, 4) { Brush = Brush.Solid(new Rgba32(255, 0, 0, 255)), Font = o.Font.WithSize(40) });
                o.TextRuns.Add(new TextRun(5, 9) { Pen = Pen.Solid(new Rgba32(0, 0, 255, 255), 1.5f), TextDecorations = TextDecorations.Underline });
            })),
            ("rtl", () => Draw(Family("NotoSansArabic-Subset-Regular.ttf").CreateFont(30), "\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645 123", o => o.TextDirection = TextDirection.RightToLeft)),
            ("devanagari", () => Draw(Family("NotoSansDevanagari-Subset-Regular.ttf").CreateFont(30), string.Join(' ', Cases("devanagari").Take(6)), o => o.WrappingLength = 300)),
        };
        foreach ((string name, Func<Image> render) in pixels)
        {
            Emit(result, detail, "pixels|" + name, h => h.Add(Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes<Rgba32>(render().ToArray<Rgba32>())))));
        }

        // Every glyph of the color test fonts that carries a paint graph.
        foreach (string file in new[] { "ColrV1-TestGlyphs.ttf", "OneImageColorTest-V0.ttf", "OneImageColorTest-V1.ttf" })
        {
            Emit(result, detail, "colr-all|" + file, h =>
            {
                Font font = Family(file).CreateFont(40);
                for (int cp = 0xF0000; cp < 0xF2000; cp++)
                {
                    if (font.HasGlyph(cp))
                    {
                        h.Add(cp);
                        h.Add(Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes<Rgba32>(Draw(font, char.ConvertFromUtf32(cp), o => { }, 70, 70).ToArray<Rgba32>()))));
                    }
                }

                for (int cp = 0x2600; cp < 0x2610; cp++)
                {
                    if (font.HasGlyph(cp))
                    {
                        h.Add(cp);
                        h.Add(Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes<Rgba32>(Draw(font, char.ConvertFromUtf32(cp), o => { }, 70, 70).ToArray<Rgba32>()))));
                    }
                }
            });
        }

        return result;
    }

    private static TextOptions Make(Font font, Action<TextOptions> apply)
    {
        var o = new TextOptions(font);
        apply(o);
        return o;
    }

    private static Image Draw(Font font, string text, Action<TextOptions> apply, int w = 420, int h = 150)
    {
        var px = new Rgba32[w * h];
        Array.Fill(px, new Rgba32(255, 255, 255, 255));
        TextOptions o = Make(font, apply);
        o.Origin = new Vector2(6, 6);
        return Image.FromPixels<Rgba32>(px, w, h).DrawText(text, o, Brush.Solid(new Rgba32(10, 20, 30, 255)));
    }

    [Fact]
    public void OutputIsIdenticalToTheReference()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "testdata", "text", "identity.txt");
        bool detail = Environment.GetEnvironmentVariable("ONEIMAGE_TEXT_DETAIL") != null;
        SortedDictionary<string, string> actual = Compute(detail);
        string? capture = Environment.GetEnvironmentVariable("ONEIMAGE_CAPTURE_TEXT");
        if (capture != null)
        {
            File.WriteAllLines(capture, actual.Select(p => p.Key + "\t" + p.Value));
            return;
        }

        var expected = File.ReadAllLines(path).Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        var diffs = new List<string>();
        foreach (KeyValuePair<string, string> p in actual)
        {
            if (!expected.TryGetValue(p.Key, out string? e))
            {
                diffs.Add("new: " + p.Key);
            }
            else if (e != p.Value)
            {
                diffs.Add("differs: " + p.Key);
            }
        }

        diffs.AddRange(expected.Keys.Where(k => !actual.ContainsKey(k)).Select(k => "missing: " + k));
        Assert.True(diffs.Count == 0, $"{diffs.Count} of {expected.Count} differ:\n" + string.Join('\n', diffs.Take(40)));
    }
}
