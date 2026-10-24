using OneImage.Text.Fonts;

namespace OneImage.Text.Tests;

/// <summary>WOFF 2.0 files made by the reference compressor load to the same faces as their source fonts.</summary>
public class Woff2Tests
{
    public static TheoryData<string, string> Pairs => new()
    {
        { "NotoSans-Subset-Regular.ttf", "NotoSans-Subset-Regular.woff2" },
        { "NotoSans-Subset-Regular.ttf", "NotoSans-Subset-Regular-hmtx.woff2" },
        { "NotoSans-Shaping-Subset.ttf", "NotoSans-Shaping-Subset.woff2" },
        { "NotoSansCJK-Subset.otf", "NotoSansCJK-Subset.woff2" },
        { "NotoSans-Subset.ttc", "NotoSans-Subset.woff2" },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void EveryGlyphOutlineAdvanceAndKerningMatchesTheSource(string source, string woff2)
    {
        List<FontFace> a = FontFile.Parse(TestFonts.Bytes(source));
        List<FontFace> b = FontFile.Parse(TestFonts.Bytes(woff2));
        Assert.Equal(a.Count, b.Count);
        for (int f = 0; f < a.Count; f++)
        {
            FontFace x = a[f];
            FontFace y = b[f];
            Assert.Equal(x.NumGlyphs, y.NumGlyphs);
            Assert.Equal(x.FamilyName, y.FamilyName);
            Assert.Equal(x.UnitsPerEm, y.UnitsPerEm);
            Assert.Equal(x.Inner.IsCff, y.Inner.IsCff);
            for (int g = 0; g < x.NumGlyphs; g++)
            {
                Assert.Equal(x.GetAdvance(g), y.GetAdvance(g));
                Assert.Equal(x.Inner.GetLeftSideBearing(g), y.Inner.GetLeftSideBearing(g));
                Outline o = x.GetOutline(g);
                Outline p = y.GetOutline(g);
                Assert.Equal(o.Verbs, p.Verbs);
                Assert.Equal(o.Coords, p.Coords);
            }

            for (int cp = 0; cp < 0x3200; cp++)
            {
                Assert.Equal(x.GetGlyphIndex(cp), y.GetGlyphIndex(cp));
            }

            int[] some = [x.GetGlyphIndex('A'), x.GetGlyphIndex('V'), x.GetGlyphIndex('T'), x.GetGlyphIndex('o'), x.GetGlyphIndex('W')];
            foreach (int l in some)
            {
                foreach (int r in some)
                {
                    Assert.Equal(x.Inner.GetKerning(l, r), y.Inner.GetKerning(l, r));
                }
            }
        }
    }

    [Fact]
    public void ShapedTextMeasuresAndDrawsTheSameFromWoff2()
    {
        // Ligatures, kerning and mark positioning come out of GSUB/GPOS, which are untouched by the container.
        const string text = "Affine fi AV To Wa \u00c5 e\u0301 \u0416\u03a9";
        var ttf = new FontCollection();
        ttf.Add(TestFonts.PathOf("NotoSans-Shaping-Subset.ttf"));
        var w2 = new FontCollection();
        w2.Add(TestFonts.PathOf("NotoSans-Shaping-Subset.woff2"));
        Assert.Equal(ttf.Single().Name, w2.Single().Name);
        var o1 = new TextOptions(ttf.Single().CreateFont(40)) { Origin = new(10, 10) };
        var o2 = new TextOptions(w2.Single().CreateFont(40)) { Origin = new(10, 10) };
        TextMetrics m1 = TextMeasurer.Measure(text, o1);
        TextMetrics m2 = TextMeasurer.Measure(text, o2);
        Assert.Equal(m1.Advance, m2.Advance);
        Assert.Equal(m1.Bounds, m2.Bounds);

        var px = new Rgba32[500 * 80];
        Array.Fill(px, new Rgba32(255, 255, 255, 255));
        Image canvas = Image.FromPixels<Rgba32>(px, 500, 80);
        Rgba32[] i1 = canvas.DrawText(text, o1, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();
        Rgba32[] i2 = canvas.DrawText(text, o2, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();
        Assert.Equal(i1, i2);
        Assert.Contains(i1, p => p.R < 128);
    }

    [Fact]
    public void CollectionAddsEveryFace()
    {
        var c = new FontCollection();
        IReadOnlyList<FontFamily> families = c.AddCollection(TestFonts.PathOf("NotoSans-Subset.woff2"));
        Assert.Equal(2, families.Count);
        Assert.True(families[0].IsStyleAvailable(FontStyle.Regular));
        Assert.True(families[1].IsStyleAvailable(FontStyle.Bold));
    }

    [Fact]
    public void TruncatedAndCorruptedFilesFailTyped()
    {
        byte[] good = TestFonts.Bytes("NotoSans-Subset-Regular.woff2");
        for (int cut = 0; cut < good.Length; cut += 97)
        {
            Assert.Throws<FontFormatException>(() => new FontCollection().AddCollection(good.AsMemory(0, cut)));
        }

        var rng = new Random(11);
        for (int i = 0; i < 400; i++)
        {
            byte[] bad = (byte[])good.Clone();
            int at = i % 3 == 0 ? rng.Next(48, 200) : rng.Next(bad.Length);
            bad[at] ^= (byte)(1 << rng.Next(8));
            try
            {
                var c = new FontCollection();
                FontFamily fam = c.Add(bad);
                Font font = fam.CreateFont(20);
                _ = TextBuilder.GeneratePath("Agéy", new TextOptions(font));
            }
            catch (FontFormatException)
            {
            }
        }
    }

    [Fact]
    public void ADeclaredHugeTableBlockFromATinyFileFailsCheaply()
    {
        // Header for one 'glyf' table of 2^28 bytes with a 40 byte compressed block of nothing useful.
        var d = new List<byte>();
        void U32(uint v) => d.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        void U16(int v) => d.AddRange([(byte)(v >> 8), (byte)v]);
        U32(0x774F4632);
        U32(0x00010000);
        U32(0);
        U16(1);
        U16(0);
        U32(0);
        U32(40);
        U32(0);
        U32(0);
        U32(0);
        U32(0);
        U32(0);
        U32(0);
        d.Add(0x0A | 0xC0);         // glyf, null transform
        d.AddRange([0x81, 0x80, 0x80, 0x80, 0x00]);
        d.AddRange(new byte[40]);
        byte[] file = d.ToArray();
        file[8] = (byte)(file.Length >> 24);
        file[9] = (byte)(file.Length >> 16);
        file[10] = (byte)(file.Length >> 8);
        file[11] = (byte)file.Length;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<FontFormatException>(() => new FontCollection().Add(file));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 2 << 20);
    }
}
