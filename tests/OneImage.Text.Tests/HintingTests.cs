using System.Diagnostics;
using OneImage.Text.Fonts;

namespace OneImage.Text.Tests;

public class HintingTests
{
    private static byte[] Maxp(int storage = 16, int functions = 16, int stack = 64, int twilight = 16)
    {
        var m = new byte[32];
        m[16] = (byte)(twilight >> 8);
        m[17] = (byte)twilight;
        m[18] = (byte)(storage >> 8);
        m[19] = (byte)storage;
        m[20] = (byte)(functions >> 8);
        m[21] = (byte)functions;
        m[24] = (byte)(stack >> 8);
        m[25] = (byte)stack;
        return m;
    }

    private static TrueTypeHinter? Hinter(byte[] fpgm, byte[] prep) => TrueTypeHinter.Create(1000, 12, fpgm, prep, new byte[8], Maxp());

    // A square: four on-curve points, one contour.
    private static bool Hint(TrueTypeHinter h, byte[] program, out float[] y)
    {
        float[] x = [100, 600, 600, 100];
        y = [0, 0, 700, 700];
        return h.TryHint(x, y, [true, true, true, true], 0, 4, [3], program);
    }

    private static void MustFailQuickly(byte[] fpgm, byte[] prep, byte[] glyph, bool setupFails)
    {
        var sw = Stopwatch.StartNew();
        TrueTypeHinter? h = Hinter(fpgm, prep);
        if (setupFails)
        {
            Assert.Null(h);
        }
        else
        {
            Assert.NotNull(h);
            float[] before = [0, 0, 700, 700];
            Assert.False(Hint(h, glyph, out float[] y));
            Assert.Equal(before, y);
        }

        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    private static readonly byte[] Nop = [0x00];

    // SVTCA[y]; PUSHW[0] -4; JMPR: jumps back to the start forever without growing the stack.
    private static readonly byte[] Loop = [0x00, 0xB8, 0xFF, 0xFC, 0x1C];

    [Fact]
    public void AnInfiniteLoopInTheControlValueProgramLeavesTheFontUnhinted() => MustFailQuickly([], Loop, Nop, true);

    [Fact]
    public void AnInfiniteLoopInTheFontProgramLeavesTheFontUnhinted() => MustFailQuickly(Loop, Nop, Nop, true);

    [Fact]
    public void AnInfiniteLoopInAGlyphProgramLeavesTheGlyphUnhinted() => MustFailQuickly([], Nop, Loop, false);

    [Fact]
    public void UnboundedRecursionIsCutAtTheCallDepth()
    {
        // FDEF 0: PUSHB 0, CALL, ENDF  and then a program that calls it.
        byte[] fpgm = [0xB0, 0x00, 0x2C, 0xB0, 0x00, 0x2B, 0x2D];
        byte[] call = [0xB0, 0x00, 0x2B];
        MustFailQuickly(fpgm, call, Nop, true);
        MustFailQuickly(fpgm, Nop, call, false);
    }

    [Fact]
    public void StackOverflowAndUnderflowAbort()
    {
        // DUP forever overflows the stack: PUSHB 1; (DUP; PUSHW -2; JMPR)
        byte[] grow = [0xB0, 0x01, 0x20, 0xB8, 0xFF, 0xFD, 0x1C];
        MustFailQuickly([], Nop, grow, false);
        MustFailQuickly([], Nop, [0x21], false);
        MustFailQuickly([], [0x60], Nop, true);
    }

    [Fact]
    public void BadIndexesAndArithmeticAbort()
    {
        MustFailQuickly([], Nop, [0xB1, 0xFF, 0x01, 0x42], false);   // WS to storage slot 255 of 16
        MustFailQuickly([], Nop, [0xB0, 0x09, 0x43, 0x21, 0xB0, 0xFF, 0x43], false); // RS out of range
        MustFailQuickly([], Nop, [0xB1, 0x05, 0x00, 0x62], false);   // DIV by zero
        MustFailQuickly([], Nop, [0xB0, 0x63, 0x2E], false);         // MDAP on point 99
        MustFailQuickly([], Nop, [0xB0, 0x63, 0x45], false);         // RCVT index 99
        MustFailQuickly([], Nop, [0xB0, 0x07, 0x2A], false);         // CALL an undefined function
        MustFailQuickly([], Nop, [0xB8, 0xFF, 0xFF, 0x17, 0x39], false); // SLOOP -1
        MustFailQuickly([], Nop, [0xB0, 0x00, 0x1C], false);         // JMPR 0
        MustFailQuickly([], Nop, [0xB0, 0x70, 0x1C], false);         // JMPR far past the program
        MustFailQuickly([], Nop, [0xB0, 0x00, 0x58], false);         // IF without EIF
        MustFailQuickly([], Nop, [0x40, 0x09, 0x01], false);         // NPUSHB past the end
        MustFailQuickly([], Nop, [0xB0, 0x00, 0x2C, 0x2D], false);   // FDEF in a glyph program
    }

    [Fact]
    public void ABigLoopCountIsPaidForInInstructions()
    {
        // LOOPCALL a function that itself loops 65535 times: total work stays under the budget or aborts quickly.
        byte[] fpgm = [0xB0, 0x00, 0x2C, 0xB0, 0x01, 0x20, 0xB8, 0xFF, 0xFF, 0x17, 0x21, 0x2D];
        var sw = Stopwatch.StartNew();
        TrueTypeHinter? h = Hinter(fpgm, [0xB8, 0xFF, 0xFF, 0xB0, 0x00, 0x2A]);
        Assert.True(sw.ElapsedMilliseconds < 3000);
        _ = h;
    }

    [Fact]
    public void AValidProgramMovesPoints()
    {
        // SVTCA[y]; PUSHB 3; MDAP[rnd] on points 0..: rounds point 3 (y 700 units = 8.4 px) to 8 px.
        TrueTypeHinter? h = Hinter([], Nop);
        Assert.NotNull(h);
        Assert.True(Hint(h, [0x00, 0xB0, 0x03, 0x2F, 0xB0, 0x02, 0x2F, 0x31], out float[] y));
        Assert.Equal(666.6667f, y[3], 0.01f);
        Assert.Equal(666.6667f, y[2], 0.01f);
    }

    [Fact]
    public void HintingMovesOnlyYAndKeepsTheStructure()
    {
        FontFace face = TestFonts.Face(HintingReferenceTests.HintedFile);
        int moved = 0;
        for (int g = 0; g < face.NumGlyphs; g++)
        {
            Outline plain = face.GetOutline(g);
            Outline hinted = face.GetHintedOutline(g, 11f);
            Assert.Equal(plain.Verbs, hinted.Verbs);
            for (int i = 0; i < plain.Coords.Length; i += 2)
            {
                Assert.Equal(plain.Coords[i], hinted.Coords[i], 0.001f);
                if (Math.Abs(plain.Coords[i + 1] - hinted.Coords[i + 1]) > 0.001f)
                {
                    moved++;
                }
            }
        }

        Assert.True(moved > 500, $"only {moved} coordinates moved");
    }

    [Fact]
    public void HintedLettersLandOnTheGrid()
    {
        // ttfautohint aligns the cap height and x-height of Noto Sans to whole pixels.
        FontFace face = TestFonts.Face(HintingReferenceTests.HintedFile);
        float scale = 11f / face.UnitsPerEm;
        (_, _, _, float top) = face.GetHintedOutline(face.GetGlyphIndex('H'), 11f).TightBox();
        Assert.Equal(Math.Round(top * scale), top * scale, 0.02);
        (_, _, _, float x) = face.GetHintedOutline(face.GetGlyphIndex('x'), 11f).TightBox();
        Assert.Equal(Math.Round(x * scale), x * scale, 0.02);
    }

    [Fact]
    public void ProgramsFromHostileTablesFallBackToUnhintedOutlines()
    {
        byte[] data = TestFonts.Bytes(HintingReferenceTests.HintedFile);
        FontFace good = FontFile.Parse(data)[0];
        foreach (string table in new[] { "prep", "fpgm" })
        {
            var tables = new Dictionary<uint, ReadOnlyMemory<byte>>();
            foreach (uint tag in AllTags(data))
            {
                tables[tag] = TableOf(data, tag);
            }

            tables[Be.Tag(table)] = Loop;
            FontFace bad = FontFace.Create(tables);
            var sw = Stopwatch.StartNew();
            for (int g = 0; g < 100; g++)
            {
                Assert.Equal(good.GetOutline(g).Coords, bad.GetHintedOutline(g, 12f).Coords);
            }

            Assert.True(sw.ElapsedMilliseconds < 3000, $"{table}: {sw.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void HintingIsSafeFromManyThreads()
    {
        FontFace face = TestFonts.Face(HintingReferenceTests.HintedFile);
        var expected = new float[face.NumGlyphs][];
        for (int g = 0; g < face.NumGlyphs; g++)
        {
            expected[g] = [.. face.GetHintedOutline(g, 13f).Coords];
        }

        FontFace fresh = TestFonts.Face(HintingReferenceTests.HintedFile);
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            for (int g = 0; g < fresh.NumGlyphs; g++)
            {
                int gg = (g + (t * 7)) % fresh.NumGlyphs;
                Assert.Equal(expected[gg], fresh.GetHintedOutline(gg, 13f).Coords);
            }
        });
    }

    [Fact]
    public void DefaultAndUnsupportedCasesDrawLikeUnhinted()
    {
        // None is the default; CFF fonts, rotated path text and absurd sizes are drawn unhinted even with Standard.
        Assert.Equal(HintingMode.None, new TextOptions(TestFonts.Noto(12)).HintingMode);
        var plain = new TextOptions(TestFonts.NotoCjk(12)) { Origin = new(2, 2) };
        var hinted = new TextOptions(TestFonts.NotoCjk(12)) { Origin = new(2, 2), HintingMode = HintingMode.Standard };
        Assert.Equal(Render("\u65e5\u672c", plain), Render("\u65e5\u672c", hinted));
        var noProgram = new TextOptions(TestFonts.Noto(12)) { Origin = new(2, 2) };
        var noProgramHinted = new TextOptions(TestFonts.Noto(12)) { Origin = new(2, 2), HintingMode = HintingMode.Standard };
        Assert.Equal(Render("Hello", noProgram), Render("Hello", noProgramHinted));
        Assert.Equal(HintingMode.Standard, noProgramHinted.Clone().HintingMode);
    }

    private static Rgba32[] Render(string text, TextOptions o)
    {
        var white = new Rgba32[100 * 30];
        Array.Fill(white, new Rgba32(255, 255, 255, 255));
        return Image.FromPixels<Rgba32>(white, 100, 30).DrawText(text, o, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();
    }

    private static IEnumerable<uint> AllTags(byte[] d)
    {
        int n = (d[4] << 8) | d[5];
        for (int i = 0; i < n; i++)
        {
            yield return Be.U32(d, 12 + (16 * i));
        }
    }

    private static ReadOnlyMemory<byte> TableOf(byte[] d, uint tag)
    {
        int n = (d[4] << 8) | d[5];
        for (int i = 0; i < n; i++)
        {
            if (Be.U32(d, 12 + (16 * i)) == tag)
            {
                return d.AsMemory((int)Be.U32(d, 20 + (16 * i)), (int)Be.U32(d, 24 + (16 * i)));
            }
        }

        return default;
    }
}
