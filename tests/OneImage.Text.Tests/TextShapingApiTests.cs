namespace OneImage.Text.Tests;

/// <summary>The public cluster-shaping API that callers with their own layout (SVG text) build on.</summary>
public class TextShapingApiTests
{
    [Fact]
    public void BidiLevelsMarkRightToLeftRunsAndTheParagraphLevel()
    {
        byte[] levels = TextShaping.GetBidiLevels("ab \u05D0\u05D1 cd", TextDirection.LeftToRight, out int para);
        Assert.Equal(0, para);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 1, 0, 0, 0 }, levels);
        byte[] rtl = TextShaping.GetBidiLevels("\u05D0\u05D1", TextDirection.RightToLeft, out int rpara);
        Assert.Equal(1, rpara);
        Assert.All(rtl, l => Assert.Equal(1, l));
    }

    [Fact]
    public void VisualOrderReversesRightToLeftRuns()
    {
        ReadOnlySpan<byte> ltr = [0, 0, 0];
        ReadOnlySpan<byte> mixed = [0, 1, 1, 0];
        Assert.Equal([0, 1, 2], TextShaping.GetVisualOrder(ltr));
        Assert.Equal([0, 2, 1, 3], TextShaping.GetVisualOrder(mixed));
        Assert.Empty(TextShaping.GetVisualOrder([]));
    }

    [Fact]
    public void ShapeRunReturnsOneClusterPerLetterWithAdvancesAndOutlines()
    {
        Font font = TestFonts.Noto(20);
        IReadOnlyList<ShapedCluster> clusters = TextShaping.ShapeRun("Hi!", font);
        Assert.Equal(3, clusters.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i, clusters[i].Start);
            Assert.Equal(1, clusters[i].Length);
            Assert.True(clusters[i].Advance > 0);
            Assert.False(clusters[i].HasMissingGlyph);
        }

        var bounds = clusters[0].GetOutline().Bounds;
        Assert.True(bounds.Width > 5 && bounds.Height > 10, "H has a body");
        Assert.True(bounds.Bottom <= 0.5f && bounds.Top < -10, "the pen is at the baseline with y down");
        Assert.Same(clusters[0].GetOutline(), clusters[0].GetOutline());
        Assert.InRange(clusters[0].Advance + clusters[1].Advance + clusters[2].Advance, 20, 40);
    }

    [Fact]
    public void KerningCanBeSwitchedOff()
    {
        Font font = TestFonts.Noto(40);
        float kerned = TextShaping.ShapeRun("AV", font)[0].Advance;
        float plain = TextShaping.ShapeRun("AV", font, options: new ShapingOptions { Kerning = KerningMode.None })[0].Advance;
        Assert.True(kerned < plain);
    }

    [Fact]
    public void AnUnknownCharacterIsReportedAsMissing()
    {
        Font font = TestFonts.Noto(20);
        IReadOnlyList<ShapedCluster> clusters = TextShaping.ShapeRun("\u4E2D", font);
        Assert.Single(clusters);
        Assert.True(clusters[0].HasMissingGlyph);
    }

    [Fact]
    public void FaceInfoReportsWeightAndSlant()
    {
        Assert.Equal(400, FontFaceInfo.GetWeight(TestFonts.Noto(12)));
        Assert.True(FontFaceInfo.GetWeight(TestFonts.Noto(12, FontStyle.Bold)) >= 700);
        Assert.False(FontFaceInfo.GetIsItalic(TestFonts.Noto(12)));
        Assert.True(FontFaceInfo.GetSuperscriptRaise(TestFonts.Noto(12)) >= 0);
        Assert.True(FontFaceInfo.GetSubscriptDrop(TestFonts.Noto(12)) >= 0);
    }

    [Fact]
    public void OrientationInVerticalTextFollowsVerticalOrientation()
    {
        Assert.True(TextShaping.IsUprightInVerticalText(0x65E5));   // an ideograph
        Assert.True(TextShaping.IsUprightInVerticalText(0x3042));   // kana
        Assert.False(TextShaping.IsUprightInVerticalText('A'));
        Assert.False(TextShaping.IsUprightInVerticalText('7'));
    }

    [Fact]
    public void AnUprightClusterStandsInItsColumnAndAdvancesByItsVerticalAdvance()
    {
        Font font = TestFonts.NotoCjk(40);
        ShapedCluster c = TextShaping.ShapeRun("\u65E5", font, options: new ShapingOptions { Vertical = true })[0];

        // The ink sits in the em square of the column (centered on x = 0) at the top of the cell.
        System.Drawing.RectangleF b = c.GetUprightOutline().Bounds;
        Assert.InRange(b.Left, -20, -2);
        Assert.InRange(b.Right, 2, 20);
        Assert.InRange(b.Top, 0, 8);
        Assert.InRange(b.Bottom, 28, c.VerticalAdvance + 1);
        Assert.InRange(c.VerticalAdvance, 30, 60);
        Assert.Same(c.GetUprightOutline(), c.GetUprightOutline());
    }

    [Fact]
    public void AFontWithoutVerticalMetricsAdvancesByItsLineHeightWhenUpright()
    {
        Font font = TestFonts.Noto(20);
        ShapedCluster c = TextShaping.ShapeRun("A", font, options: new ShapingOptions { Vertical = true })[0];
        Assert.Equal(font.Metrics.LineHeight * 20f / font.Metrics.UnitsPerEm, c.VerticalAdvance, 0.01);
    }

    private static Font Kannada(float size) => ComplexScriptReferenceTests.Load("NotoSansKannada-Subset-Regular.ttf").Collection.Get("Noto Sans Kannada").CreateFont(size);

    [Fact]
    public void ClustersCoverTheTextWithoutGapsWhenTheShaperDropsALeadingJoiner()
    {
        // The non-joiner before the letter is dropped by the shaper; the first cluster takes it, so the clusters start at 0 and meet.
        const string text = "\u200C\u0C95\u0CCD\u0C95";
        IReadOnlyList<ShapedCluster> clusters = TextShaping.ShapeRun(text, Kannada(30));
        Assert.NotEmpty(clusters);
        Assert.Equal(0, clusters[0].Start);
        for (int i = 1; i < clusters.Count; i++)
        {
            Assert.Equal(clusters[i - 1].Start + clusters[i - 1].Length, clusters[i].Start);
        }

        Assert.Equal(text.Length, clusters[^1].Start + clusters[^1].Length);
    }

    [Fact]
    public void TextOfOnlyJoinersGivesOneEmptyClusterThatCoversIt()
    {
        ShapedCluster only = Assert.Single(TextShaping.ShapeRun("\u200D\u200C", Kannada(30)));
        Assert.Equal(0, only.Start);
        Assert.Equal(2, only.Length);
        Assert.Equal(0, only.Advance);
        Assert.True(only.GetOutline().IsEmpty);
    }

    // A font of 5 glyphs whose character map sends 'A' to glyph 9000, which the font does not have.
    private static Font DamagedCharacterMapFont()
    {
        byte[] font = LayoutFont.Font([500, 600, 700, 800, 900]);
        int tables = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < tables; i++)
        {
            int e = 12 + (16 * i);
            if (System.Text.Encoding.ASCII.GetString(font, e, 4) == "cmap")
            {
                // Table header (12 bytes), then the format 6 header (5 values); the first entry is the glyph for 'A'.
                int at = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(e + 8)) + 12 + 10;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(at), 9000);
            }
        }

        return new FontCollection().Add(font).CreateFont(100);
    }

    [Fact]
    public void AGlyphIdPastTheGlyphCountIsTheMissingGlyph()
    {
        Font font = DamagedCharacterMapFont();
        ShapedCluster c = TextShaping.ShapeRun("A", font)[0];
        Assert.True(c.HasMissingGlyph);
        Assert.Equal(50, c.Advance, 0.01);   // glyph 0's advance of 500 units at 100 px per 1000-unit em, not the last glyph's 900
        _ = c.GetOutline();
        _ = c.GetUprightOutline();

        var o = new TextOptions(font);
        Assert.Equal(50, TextMeasurer.Measure("A", o).Advance.Width, 0.01);
        Assert.Equal(100, TextMeasurer.Measure("AA", o).Advance.Width, 0.01);
        _ = TextBuilder.GeneratePath("AB", o);
    }

    [Fact]
    public void ASubstitutionToAGlyphPastTheGlyphCountIsIgnored()
    {
        // The shaping engine refuses the substitution, so the letter keeps its own glyph and nothing reads out of range.
        byte[] gsub = LayoutFont.Table(false, [new FeatureSpec("ccmp", 0)], [new LookupSpec(1, 0, new Blob().U16(2).Ref(Blob.Cov(1)).U16(1, 8193).Build())]);
        Font font = new FontCollection().Add(LayoutFont.Font([500, 600, 700, 800, 900], null, gsub)).CreateFont(100);
        ShapedCluster c = TextShaping.ShapeRun("A", font)[0];
        Assert.False(c.HasMissingGlyph);
        Assert.Equal(60, c.Advance, 0.01);
        Assert.Equal(60, TextMeasurer.Measure("A", new TextOptions(font)).Advance.Width, 0.01);
    }
}
