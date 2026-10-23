using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using OneImage.Text.Fonts;
using OneImage.Text.Layout;
using OneImage.Text.Shaping;
using OneImage.Text.Unicode;

namespace OneImage.Text.Tests;

/// <summary>Behavior of the Indic, Universal Shaping Engine and Thai/Lao shapers that does not need the reference: ordering rules, joiners, broken clusters, limits.</summary>
public class ComplexShapingTests
{
    private const string Deva = "NotoSansDevanagari-Subset-Regular.ttf";
    private const string Tamil = "NotoSansTamil-Subset-Regular.ttf";
    private const string Khmer = "NotoSansKhmer-Subset-Regular.ttf";
    private const string Myanmar = "NotoSansMyanmar-Subset-Regular.ttf";
    private const string Thai = "NotoSansThai-Subset-Regular.ttf";
    private const string Sinhala = "NotoSansSinhala-Subset-Regular.ttf";

    private static List<ShapedGlyph> Shape(string file, string text, TextOptions? configure = null)
    {
        var (collection, face) = ComplexScriptReferenceTests.Load(file);
        TextOptions o = configure ?? new TextOptions(collection.First().CreateFont(face.UnitsPerEm));
        TextLayout layout = TextLayout.Create(text, o);
        return [.. layout.Glyphs];
    }

    private static int Gid(string file, int cp) => ComplexScriptReferenceTests.Load(file).Face.GetGlyphIndex(cp);

    [Fact]
    public void TablesKnowTheCharacters()
    {
        Assert.Equal(ShapedScript.Devanagari, IndicProps.Lookup(0x93F, out IndicSyllabicCategory isc, out IndicPositionalCategory ipc));
        Assert.Equal(IndicSyllabicCategory.VowelDependent, isc);
        Assert.Equal(IndicPositionalCategory.Left, ipc);
        Assert.Equal(ShapedScript.Khmer, IndicProps.Lookup(0x17D2, out isc, out _));
        Assert.Equal(IndicSyllabicCategory.InvisibleStacker, isc);
        Assert.Equal(ShapedScript.None, IndicProps.Lookup('A', out _, out _));
        Assert.True(IndicProps.Decompose(0x9CB, out int a, out int b));
        Assert.Equal((0x9C7, 0x9BE), (a, b));
        Assert.Equal(Scripts.Of(0x915), Scripts.TagOf(ShapedScript.Devanagari));
        Assert.Equal(Scripts.Of(0xE01), Scripts.TagOf(ShapedScript.Thai));
    }

    [Fact]
    public void PreBaseMatraComesBeforeItsConsonant()
    {
        List<ShapedGlyph> g = Shape(Deva, "\u0915\u093F");
        Assert.Equal(2, g.Count);
        Assert.Equal(0x93F, g[0].CodePoint);
        Assert.Equal(0x915, g[1].CodePoint);
        Assert.Equal(g[0].Cluster, g[1].Cluster);
    }

    [Fact]
    public void RephMovesAfterItsBase()
    {
        List<ShapedGlyph> g = Shape(Deva, "\u0930\u094D\u0915");
        Assert.Equal(2, g.Count);
        Assert.Equal(0x915, g[0].CodePoint);
        Assert.NotEqual(Gid(Deva, 0x930), g[1].GlyphId); // the reph is a form of its own
    }

    [Fact]
    public void RephAndPreBaseMatraTogether()
    {
        // Ra + virama + ka + i: the matra stands in front of the base, the reph behind it.
        List<ShapedGlyph> g = Shape(Deva, "\u0930\u094D\u0915\u093F");
        Assert.Equal(3, g.Count);
        Assert.Equal(0x93F, g[0].CodePoint);
        Assert.Equal(0x915, g[1].CodePoint);
    }

    [Fact]
    public void ConjunctsFormAndAZeroWidthNonJoinerBlocksThem()
    {
        int conjunct = Shape(Deva, "\u0915\u094D\u0937").Count(g => g.GlyphId != 0);
        int blocked = Shape(Deva, "\u0915\u094D\u200C\u0937").Count(g => g.GlyphId != 0);
        Assert.Equal(1, conjunct);
        Assert.True(blocked >= 3, $"{blocked} glyphs");
    }

    [Fact]
    public void ZeroWidthJoinerAsksForTheHalfForm()
    {
        List<ShapedGlyph> half = Shape(Deva, "\u0915\u094D\u200D\u0937");
        Assert.Equal(2, half.Count);
        Assert.NotEqual(Gid(Deva, 0x915), half[0].GlyphId); // a half form, not the full ka
    }

    [Fact]
    public void MarksWithoutABaseGetADottedCircle()
    {
        List<ShapedGlyph> g = Shape(Deva, "\u0902");
        Assert.Equal(2, g.Count);
        Assert.Equal(Gid(Deva, 0x25CC), g[0].GlyphId);

        // A pre-base matra alone goes in front of the circle, like it would in front of a consonant.
        List<ShapedGlyph> i = Shape(Deva, "\u093F");
        Assert.Equal(0x93F, i[0].CodePoint);
        Assert.Equal(Gid(Deva, 0x25CC), i[1].GlyphId);

        // The same mark after a base does not get one.
        Assert.Equal(2, Shape(Deva, "\u0915\u0902").Count);
        Assert.DoesNotContain(Shape(Deva, "\u0915\u0902"), x => x.GlyphId == Gid(Deva, 0x25CC));
    }

    [Fact]
    public void SplitVowelSignsAreDecomposed()
    {
        // Tamil o (U+0BCA) = e (U+0BC6) + aa (U+0BBE): the e stands before the consonant, the aa after it.
        List<ShapedGlyph> g = Shape(Tamil, "\u0B95\u0BCA");
        Assert.Equal(3, g.Count);
        Assert.Equal(Gid(Tamil, 0xBC6), g[0].GlyphId);
        Assert.Equal(Gid(Tamil, 0xB95), g[1].GlyphId);
        Assert.Equal(Gid(Tamil, 0xBBE), g[2].GlyphId);
    }

    [Fact]
    public void SyllablesShareOneClusterAndStayInOrder()
    {
        string text = "\u0928\u092E\u0938\u094D\u0924\u0947 \u0915\u0930\u094D\u092E";
        List<ShapedGlyph> g = Shape(Deva, text);
        for (int i = 1; i < g.Count; i++)
        {
            Assert.True(g[i].Cluster >= g[i - 1].Cluster, $"glyph {i} cluster {g[i].Cluster} after {g[i - 1].Cluster}");
        }
    }

    [Fact]
    public void KhmerPreBaseVowelAndCoengRoComeFirst()
    {
        List<ShapedGlyph> e = Shape(Khmer, "\u1780\u17C1");
        Assert.Equal(0x17C1, e[0].CodePoint);

        // Coeng + ro is shown in front of the base consonant.
        List<ShapedGlyph> ro = Shape(Khmer, "\u1780\u17D2\u179A");
        Assert.NotEqual(Gid(Khmer, 0x1780), ro[0].GlyphId);
        Assert.Equal(0x1780, ro[^1].CodePoint);
    }

    [Fact]
    public void KhmerSplitVowelsGetTheirPreBasePart()
    {
        List<ShapedGlyph> g = Shape(Khmer, "\u1780\u17C4");
        Assert.True(g.Count >= 2);
        Assert.Equal(0x17C1, g[0].CodePoint);
    }

    [Fact]
    public void MyanmarMedialRaAndEVowelComeFirstAndKinziIsOneGlyph()
    {
        List<ShapedGlyph> e = Shape(Myanmar, "\u1000\u1031");
        Assert.Equal(0x1031, e[0].CodePoint);
        List<ShapedGlyph> ra = Shape(Myanmar, "\u1000\u103C");
        Assert.Equal(0x103C, ra[0].CodePoint);

        // The kinzi (nga + asat + virama) is one glyph drawn above the consonant that follows.
        List<ShapedGlyph> kinzi = Shape(Myanmar, "\u1004\u103A\u1039\u1000");
        Assert.Equal(2, kinzi.Count);
        Assert.Equal(0x1000, kinzi.Single(k => k.CodePoint == 0x1000).CodePoint);
    }

    [Fact]
    public void SinhalaVowelSignsReorderAndSplit()
    {
        List<ShapedGlyph> g = Shape(Sinhala, "\u0D9A\u0DDC"); // o = kombuva + aela-pilla
        Assert.Equal(3, g.Count);
        Assert.Equal(0xDD9, g[0].CodePoint);
        Assert.Equal(0xD9A, g[1].CodePoint);
    }

    [Fact]
    public void SaraAmSplitsAndTheNikhahitSitsUnderTheToneMark()
    {
        List<ShapedGlyph> g = Shape(Thai, "\u0E19\u0E49\u0E33");
        Assert.Equal(4, g.Count);
        Assert.Equal(0xE4D, g[1].CodePoint); // nikhahit before the tone mark
        Assert.Equal(0xE49, g[2].CodePoint);
        Assert.Equal(0xE32, g[3].CodePoint);
    }

    [Fact]
    public void WrappedParagraphsNeverStartALineWithAMark()
    {
        string text = string.Concat(Enumerable.Repeat("\u0915\u094D\u0937\u0924\u094D\u0930\u093F\u092F \u0935\u093F\u0926\u094D\u092F\u093E\u0930\u094D\u0925\u0940 \u0915\u0930\u094D\u092E ", 20));
        var (collection, face) = ComplexScriptReferenceTests.Load(Deva);
        var o = new TextOptions(collection.First().CreateFont(face.UnitsPerEm)) { WrappingLength = 6000 };
        TextLayout layout = TextLayout.Create(text, o);
        Assert.True(layout.Lines.Count > 3);
        foreach (LayoutLine line in layout.Lines)
        {
            UnicodeCategory c = CharUnicodeInfo.GetUnicodeCategory(text[line.TextStart]);
            Assert.True(c is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark), $"line starts with U+{(int)text[line.TextStart]:X4}");
        }
    }

    [Fact]
    public void LatinTextIsNotTouched()
    {
        // The Indic shaper's joiner handling must not leak into other scripts: a ZWNJ still ends the shaping run there.
        List<ShapedGlyph> g = Shape(TestFonts.Shaping, "f\u200Ci", new TextOptions(TestFonts.ShapingFonts.Get("Noto Sans").CreateFont(14)));
        Assert.Equal(2, g.Count(x => x.GlyphId != 0));
    }

    // ---- Limits ----

    [Theory]
    [InlineData("\u0915\u094D", 150_000)]            // endless conjunct chain
    [InlineData("\u093F\u0902\u094D", 100_000)]       // marks without a base: a dotted circle per cluster
    [InlineData("\u0915\u094D\u200D\u200C", 100_000)] // joiner spam
    [InlineData("\u0930\u094D", 150_000)]             // reph candidates everywhere
    public void HostileIndicInputStaysLinear(string unit, int repeat)
    {
        string text = string.Concat(Enumerable.Repeat(unit, repeat));
        var (collection, face) = ComplexScriptReferenceTests.Load(Deva);
        var o = new TextOptions(collection.First().CreateFont(face.UnitsPerEm));

        // Linearity is checked against a quarter-size run rather than a fixed wall-clock bound, so a slow or
        // busy machine does not fail the test: quadratic behavior would make the full run about 16 times slower,
        // linear about 4 times. The absolute cap only catches a hang.
        string quarter = string.Concat(Enumerable.Repeat(unit, repeat / 4));
        var sw = Stopwatch.StartNew();
        TextLayout.Create(quarter, o);
        TimeSpan small = sw.Elapsed;
        sw.Restart();
        TextLayout layout = TextLayout.Create(text, o);
        sw.Stop();
        TestReport.Line($"COMPLEX LIMITS {text.Length} chars of U+{(int)unit[0]:X4}...: {sw.ElapsedMilliseconds} ms (quarter {small.TotalMilliseconds:F0} ms), {layout.Glyphs.Count} glyphs");
        Assert.True(layout.Glyphs.Count <= (2 * text.Length) + 16, $"{layout.Glyphs.Count} glyphs");
        Assert.True(sw.Elapsed < (8 * small) + TimeSpan.FromSeconds(1), $"took {sw.Elapsed}, quarter size took {small}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
    }

    [Theory]
    [InlineData(Khmer, "\u1780\u17D2", 100_000)]
    [InlineData(Khmer, "\u17C1\u17C9", 100_000)]
    [InlineData(Myanmar, "\u1004\u103A\u1039", 100_000)]
    [InlineData(Myanmar, "\u103C\u1031", 100_000)]
    [InlineData(Sinhala, "\u0D9A\u0DCA\u200D", 100_000)]
    public void HostileUniversalShapingEngineInputStaysLinear(string font, string unit, int repeat)
    {
        string text = string.Concat(Enumerable.Repeat(unit, repeat));
        var sw = Stopwatch.StartNew();
        List<ShapedGlyph> g = Shape(font, text);
        sw.Stop();
        Assert.True(g.Count <= (2 * text.Length) + 16, $"{g.Count} glyphs");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    [Fact]
    public void LongSyllablesAreCutNotRejected()
    {
        // A conjunct with 500 consonants is cut into pieces of bounded length; every character still shows up.
        string text = string.Concat(Enumerable.Repeat("\u0915\u094D", 500)) + "\u0937\u093F";
        List<ShapedGlyph> g = Shape(Deva, text);
        Assert.True(g.Count > 100);
    }

    [Fact]
    public void DamagedComplexScriptFontsNeverThrowOrHang()
    {
        string[] fonts = [Deva, Khmer, Myanmar, Sinhala, Tamil];
        string[] samples =
        [
            "\u0915\u094D\u0937\u093F \u0930\u094D\u0915\u0940 \u0915\u094D\u200D\u0937 \u093F",
            "\u1780\u17D2\u179A\u17C1 \u1780\u17C4 \u17D2",
            "\u1004\u103A\u1039\u1000\u102D \u1000\u103C\u1031\u102C \u1031",
            "\u0D9A\u0DCA\u200D\u0DBB\u0DD9 \u0D9A\u0DDC \u0DD2",
            "\u0B95\u0BCA \u0B95\u0BCB \u0B95\u0BCD\u0BB7",
        ];
        var rnd = new Random(777);
        var sw = Stopwatch.StartNew();
        for (int n = 0; n < 400; n++)
        {
            int f = rnd.Next(fonts.Length);
            byte[] original = File.ReadAllBytes(TestFonts.PathOf(fonts[f]));
            byte[] font = (byte[])original.Clone();
            string tag = rnd.Next(2) == 0 ? "GSUB" : "GPOS";
            (int offset, int length) = FindTable(font, tag);
            for (int h = 0, hits = 1 + rnd.Next(8); h < hits; h++)
            {
                int at = offset + (rnd.Next(3) == 0 ? rnd.Next(Math.Min(length, 600)) : rnd.Next(length));
                font[at] = (byte)rnd.Next(256);
            }

            FontFamily family;
            try
            {
                family = new FontCollection().Add(font);
            }
            catch (FontFormatException)
            {
                continue;
            }

            foreach (string text in samples)
            {
                _ = TextLayout.Create(text, new TextOptions(family.CreateFont(16)));
            }
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(120), $"took {sw.Elapsed}");
    }

    private static (int Offset, int Length) FindTable(byte[] font, string tag)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int rec = 12 + (16 * i);
            if (System.Text.Encoding.ASCII.GetString(font, rec, 4) == tag)
            {
                return ((int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(rec + 8)), (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(rec + 12)));
            }
        }

        throw new InvalidOperationException(tag);
    }
}
