using OneImage.Text.Shaping;

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
}
