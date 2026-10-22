using OneImage.Text.Fonts;

namespace OneImage.Text.Tests;

public class LoadingTests
{
    [Fact]
    public void TrueTypeFontReportsFamilyStyleAndMetrics()
    {
        Font f = TestFonts.Noto(20);
        Assert.Equal("Noto Sans", f.Family.Name);
        Assert.Equal(FontStyle.Regular, f.Style);
        Assert.Equal(1000, f.Metrics.UnitsPerEm);
        Assert.Equal(1069, f.Metrics.Ascender);
        Assert.Equal(-293, f.Metrics.Descender);
        Assert.Equal(1362, f.Metrics.LineHeight);
        Assert.True(f.HasGlyph('A'));
        Assert.True(f.HasGlyph(0x416)); // Cyrillic
        Assert.False(f.HasGlyph(0x4E00));
        Assert.Equal(63.9f * 20f / 100f, f.GetAdvance('A'), 3);
    }

    [Fact]
    public void CffFontLoadsWithCidKeyedOutlines()
    {
        FontFace face = TestFonts.Face(TestFonts.Cff);
        Assert.True(face.IsCff);
        Assert.Equal(1000, face.UnitsPerEm);
        int ideograph = face.GetGlyphIndex(0x65E5);
        Assert.NotEqual(0, ideograph);
        Outline o = face.GetOutline(ideograph);
        Assert.False(o.IsEmpty);
        (float x0, float y0, float x1, float y1) = o.TightBox();
        Assert.InRange(x1 - x0, 400, 1000);
        Assert.InRange(y1 - y0, 400, 1000);
        Assert.Equal("Noto Sans CJK JP", TestFonts.NotoCjk(10).Family.Name);
    }

    [Fact]
    public void CollectionAddsEveryFaceToOneFamily()
    {
        var fonts = new FontCollection();
        IReadOnlyList<FontFamily> families = fonts.AddCollection(TestFonts.PathOf(TestFonts.Collection));
        Assert.Equal(2, families.Count);
        Assert.Same(families[0], families[1]);
        Assert.Single(fonts);
        Assert.Equal([FontStyle.Regular, FontStyle.Bold], families[0].GetAvailableStyles());

        // Face 1 is the bold face with the legacy kern table; face 0 has GPOS kerning only.
        Font regular = families[0].CreateFont(100);
        Font bold = families[0].CreateFont(100, FontStyle.Bold);
        Assert.Equal(FontStyle.Bold, bold.Style);
        Assert.Contains("Bold", bold.Name);
        Assert.NotEqual(regular.Face.Data, bold.Face.Data);
        Assert.NotEqual(regular.GetAdvance('H'), bold.GetAdvance('H'));
    }

    [Fact]
    public void MissingStyleFallsBackToTheClosestFace()
    {
        FontFamily family = new FontCollection().AddCollection(TestFonts.PathOf(TestFonts.Collection))[0];
        Assert.Equal(FontStyle.Regular, family.CreateFont(10, FontStyle.Italic).Style);
        Assert.Equal(FontStyle.Bold, family.CreateFont(10, FontStyle.BoldItalic).Style);
    }

    [Fact]
    public void AddAcceptsPathStreamAndMemory()
    {
        byte[] bytes = TestFonts.Bytes(TestFonts.Regular);
        var a = new FontCollection();
        var b = new FontCollection();
        var c = new FontCollection();
        using var ms = new MemoryStream(bytes);
        Assert.Equal("Noto Sans", a.Add(TestFonts.PathOf(TestFonts.Regular)).Name);
        Assert.Equal("Noto Sans", b.Add(ms).Name);
        Assert.Equal("Noto Sans", c.Add(bytes).Name);
        Assert.True(c.TryGet("noto sans", out _));
        Assert.False(c.TryGet("nope", out _));
        Assert.Throws<ArgumentException>(() => c.Get("nope"));
    }

    [Fact]
    public void WoffDecodesToTheSameGlyphsAsItsSource()
    {
        FontFace ttf = TestFonts.Face(TestFonts.Regular);
        FontFace woff = TestFonts.Face(TestFonts.Woff);
        Assert.Equal(ttf.NumGlyphs, woff.NumGlyphs);
        Assert.Equal(ttf.FamilyName, woff.FamilyName);
        int compared = 0;
        for (int cp = 0x20; cp < 0x2100; cp++)
        {
            int g = ttf.GetGlyphIndex(cp);
            Assert.Equal(g, woff.GetGlyphIndex(cp));
            if (g == 0)
            {
                continue;
            }

            Outline a = ttf.GetOutline(g);
            Outline b = woff.GetOutline(g);
            Assert.Equal(a.Verbs, b.Verbs);
            Assert.Equal(a.Coords, b.Coords);
            Assert.Equal(ttf.GetAdvance(g), woff.GetAdvance(g));
            compared++;
        }

        Assert.True(compared > 300);
    }

    [Fact]
    public void Woff2IsRefusedByName()
    {
        byte[] data = new byte[64];
        "wOF2"u8.CopyTo(data);
        FontFormatException e = Assert.Throws<FontFormatException>(() => new FontCollection().Add(data));
        Assert.Contains("WOFF2", e.Message);
    }

    [Fact]
    public void WoffDeclaringAHugeExpansionIsRejectedBeforeAllocating()
    {
        byte[] woff = TestFonts.Bytes(TestFonts.Woff);

        // Claim the first table is 100 MB when decompressed; its compressed size is a few KB at most.
        byte[] hostile = (byte[])woff.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hostile.AsSpan(44 + 12), 100_000_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<FontFormatException>(() => new FontCollection().Add(hostile));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 20_000_000);
    }

    [Fact]
    public void TruncatedAndCorruptedFontsOnlyThrowFontErrors()
    {
        var rng = new Random(7);
        foreach (string file in new[] { TestFonts.Regular, TestFonts.Cff, TestFonts.Collection, TestFonts.Woff, TestFonts.BoldKern })
        {
            byte[] good = TestFonts.Bytes(file);
            for (int i = 0; i < 400; i++)
            {
                byte[] bad = i % 2 == 0 ? good[..rng.Next(0, good.Length)] : (byte[])good.Clone();
                if (i % 2 == 1)
                {
                    for (int k = rng.Next(1, 12); k > 0; k--)
                    {
                        bad[rng.Next(bad.Length)] = (byte)rng.Next(256);
                    }
                }

                Exercise(bad);
            }
        }
    }

    internal static void Exercise(byte[] data)
    {
        try
        {
            foreach (FontFace face in FontFile.Parse(data))
            {
                var entry = new FaceEntry(face);
                _ = entry.Metrics.LineHeight;
                int prev = 0;
                foreach (char c in "AVa gHé日Ж")
                {
                    int g = face.GetGlyphIndex(c);
                    _ = face.GetAdvance(g);
                    _ = face.GetOutline(g).TightBox();
                    _ = face.GetKerning(prev, g);
                    prev = g;
                }

                for (int g = 0; g < Math.Min(face.NumGlyphs, 64); g++)
                {
                    _ = face.GetOutline(g);
                }
            }
        }
        catch (FontFormatException)
        {
        }
    }
}
