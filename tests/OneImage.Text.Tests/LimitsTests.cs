using OneImage.Text.Fonts;

namespace OneImage.Text.Tests;

/// <summary>Hostile font programs against the interpreter and glyph limits, with hand-built fonts.</summary>
public class LimitsTests
{
    private static byte[] N(int v) => MiniFont.Num(v);

    private static Outline Load(byte[] font, bool cff = true)
    {
        FontFace face = FontFile.Parse(font)[0];
        Assert.Equal(cff, face.IsCff);
        return face.GetOutline(face.GetGlyphIndex('A'));
    }

    [Fact]
    public void ASmallCharstringProducesItsOutline()
    {
        // 100 200 rmoveto 300 0 rlineto 0 300 rlineto endchar
        byte[] cs = [.. N(100), .. N(200), 21, .. N(300), .. N(0), 5, .. N(0), .. N(300), 5, 14];
        Outline o = Load(MiniFont.Cff(cs));
        Assert.Equal([Outline.Move, Outline.Line, Outline.Line, Outline.Close], o.Verbs);
        Assert.Equal([100f, 200f, 400f, 200f, 400f, 500f], o.Coords);
    }

    [Fact]
    public void WidthAndHintOperatorsAreSkipped()
    {
        // width 50, hstem 10 20, hintmask (1 stem, 1 mask byte), rmoveto, vlineto, endchar
        byte[] cs = [.. N(50), .. N(10), .. N(20), 1, 19, 0x80, .. N(5), .. N(6), 21, .. N(100), 7, 14];
        Outline o = Load(MiniFont.Cff(cs));
        Assert.Equal([5f, 6f, 5f, 106f], o.Coords);
    }

    [Fact]
    public void LocalAndGlobalSubroutinesRun()
    {
        // Subrs: 100 0 rlineto return. Bias is 107 for a single subroutine.
        byte[] sub = [.. N(100), .. N(0), 5, 11];
        byte[] cs = [.. N(0), .. N(0), 21, .. N(-107), 10, .. N(-107), 29, 14];
        Outline o = Load(MiniFont.Cff(cs, localSubr: sub, globalSubr: sub));
        Assert.Equal([0f, 0f, 100f, 0f, 200f, 0f], o.Coords);
    }

    [Fact]
    public void SubroutineRecursionIsCutOff()
    {
        // The subroutine calls itself forever.
        byte[] sub = [.. N(-107), 10, 11];
        byte[] cs = [.. N(-107), 10, 14];
        FontFormatException e = Assert.Throws<FontFormatException>(() => Load(MiniFont.Cff(cs, localSubr: sub)));
        Assert.Contains("nested too deeply", e.Message);
    }

    [Fact]
    public void OperandStackOverflowIsCutOff()
    {
        byte[] cs = [.. Enumerable.Range(0, 200).SelectMany(_ => N(1)), 14];
        Assert.Throws<FontFormatException>(() => Load(MiniFont.Cff(cs)));
    }

    [Fact]
    public void RunawayOperatorCountIsCutOff()
    {
        // 600,000 push-and-drop pairs: far more operators than any real glyph needs.
        var cs = new byte[1_200_001];
        for (int i = 0; i + 1 < 1_200_000; i += 2)
        {
            cs[i] = 139;
            cs[i + 1] = 18;
        }

        cs[^1] = 14;
        FontFormatException e = Assert.Throws<FontFormatException>(() => Load(MiniFont.Cff(cs)));
        Assert.Contains("runs too long", e.Message);
    }

    [Fact]
    public void NumbersThatOverflowAreRefused()
    {
        // 30000 * 30000 * 30000 * ... pushes coordinates past anything a font can hold.
        byte[] cs = [.. N(30000), 12, 27, 12, 24, 12, 27, 12, 24, 12, 27, 12, 24, 12, 27, 12, 24, 12, 27, 12, 24, 12, 27, 12, 24, 12, 27, 12, 24, 12, 27, 12, 24, 12, 27, 12, 24, 12, 27, 12, 24, 12, 27, 12, 24, 21, 14];
        Assert.Throws<FontFormatException>(() => Load(MiniFont.Cff(cs)));
    }

    [Fact]
    public void TruncatedCharstringsFail()
    {
        Assert.Throws<FontFormatException>(() => Load(MiniFont.Cff([28, 1])));
        Assert.Throws<FontFormatException>(() => Load(MiniFont.Cff([.. N(1), 21, 14])));
    }

    [Fact]
    public void ACompositeGlyphThatIncludesItselfIsCutOff()
    {
        // numberOfContours -1, bbox, then components: flags (words | xy | more), glyph 1 (itself), dx 0, dy 0 -- repeated.
        var data = new List<byte> { 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 40; i++)
        {
            data.AddRange([0x00, 0x23, 0x00, 0x01, 0, 0, 0, 0]);
        }

        FontFormatException e = Assert.Throws<FontFormatException>(() => Load(MiniFont.TrueType([.. data]), cff: false));
        Assert.Contains("nested too deeply", e.Message);
    }

    [Fact]
    public void ACompositeWithManyComponentsIsCutOff()
    {
        // Glyph 0 is empty, so 600 components that each reference it never recurse; the count alone must stop it.
        var data = new List<byte> { 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 600; i++)
        {
            data.AddRange([0x00, 0x23, 0x00, 0x00, 0, 0, 0, 0]);
        }

        // Glyph 0 has no outline so each component is skipped; the loop still has to terminate by count.
        FontFormatException e = Assert.Throws<FontFormatException>(() => Load(MiniFont.TrueType([.. data]), cff: false));
        Assert.Contains("too many components", e.Message);
    }

    [Fact]
    public void SimpleGlyphsWithImpossibleContoursFail()
    {
        // One contour ending at point 65535 with no point data behind it.
        byte[] data = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0];
        Assert.Throws<FontFormatException>(() => Load(MiniFont.TrueType(data), cff: false));
    }

    [Fact]
    public void ContoursThatDoNotIncreaseFail()
    {
        byte[] data = [0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 3, 0, 0, 1, 1, 1, 1, 1, 1];
        Assert.Throws<FontFormatException>(() => Load(MiniFont.TrueType(data), cff: false));
    }
}
