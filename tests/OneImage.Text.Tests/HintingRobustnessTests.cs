using System.Diagnostics;
using System.Buffers.Binary;
using OneImage.Text.Fonts;

namespace OneImage.Text.Tests;

/// <summary>
/// Hostile and unusual hinting programs: arithmetic on extreme values, the twilight zone across programs, and the instruction
/// budget shared by the components of one glyph. The fonts are also the seeds of the font fuzz target (tests/testdata/fonts/hint-*.ttf).
/// </summary>
public class HintingRobustnessTests
{
    private static readonly byte[] Nop = [0x00];
    private static readonly byte[] Loop = [0x00, 0xB8, 0xFF, 0xFC, 0x1C];

    private const byte SvtcaY = 0x00, SvtcaX = 0x01, Szps = 0x16, Wcvtp = 0x44, Miap1 = 0x3F, Miap0 = 0x3E;

    private static byte[] Maxp()
    {
        var m = new byte[32];
        m[17] = 16;
        m[19] = 16;
        m[21] = 16;
        m[25] = 64;
        return m;
    }

    private static TrueTypeHinter Hinter(byte[] prep) =>
        TrueTypeHinter.Create(1000, 12, Array.Empty<byte>(), prep, new byte[16], Maxp())!;

    private static bool Hint(TrueTypeHinter h, byte[] program, out float[] y)
    {
        float[] x = [100, 600, 600, 100];
        y = [0, 0, 700, 700];
        return h.TryHint(x, y, [true, true, true, true], 0, 4, [3], program, new TrueTypeHinter.HintBudget());
    }

    // PUSHB / PUSHW of a small constant.
    private static byte[] Push(int v) => v is >= 0 and < 256 ? [0xB0, (byte)v] : [0xB8, (byte)(v >> 8), (byte)v];

    // Builds any 32-bit value from 16-bit pieces: hi, sixteen doublings (ADD wraps), then lo.
    internal static byte[] Const(int v)
    {
        short lo = (short)v;
        short hi = (short)((v - lo) >> 16);
        var code = new List<byte> { 0xB8, (byte)(hi >> 8), (byte)hi };
        for (int i = 0; i < 16; i++)
        {
            code.Add(0x20); // DUP
            code.Add(0x60); // ADD
        }

        code.AddRange([0xB8, (byte)(lo >> 8), (byte)lo, 0x60]);
        return [.. code];
    }

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    // ---- defect 1: int.MinValue on the stack must not throw ----

    internal static readonly (string Name, byte[] Program)[] MinValuePrograms =
    [
        ("abs", Cat(Const(int.MinValue), [0x64])),
        // The cut-in compare in MIAP: the cvt value is int.MinValue and the point projects to 0.
        ("miap", Cat([SvtcaY], Push(0), Const(int.MinValue), [Wcvtp], Push(0), Push(0), [Miap1])),
        // MIRP with the round bit: both compares see int.MinValue.
        ("mirp", Cat([SvtcaY], Push(0), Const(int.MinValue), [Wcvtp], Push(1), Push(0), [0xE4])),
        // MDRP in the twilight zone, whose original coordinates can hold int.MinValue.
        ("mdrp", Cat([SvtcaX], Push(0), [Szps], Push(0), Const(int.MinValue), [Wcvtp], Push(0), Push(0), [Miap0], Push(1), [0xC0])),
    ];

    [Fact]
    public void ExtremeValuesInArithmeticAndDistancesDoNotThrow()
    {
        TrueTypeHinter h = Hinter(Nop);
        var failures = new List<string>();
        foreach ((string name, byte[] program) in MinValuePrograms)
        {
            bool ran = false;
            Exception? thrown = Record.Exception(() => ran = Hint(h, program, out _));
            if (thrown is not null || !ran)
            {
                failures.Add($"{name}: {(thrown is null ? "the program did not run to its end" : thrown.GetType().Name)}");
            }
        }

        Assert.Empty(failures);
    }

    // ---- defect 2: ISECT division ----

    // Twilight points a0 (0,0), a1 (int.MinValue,3), b0 (2,-6), b1 (-715827881,-5): the discriminant is -1 and the numerator's product
    // wraps to long.MinValue, so a 64-bit division overflows.
    internal static readonly byte[] IsectProgram = BuildIsect();

    private static byte[] BuildIsect()
    {
        var code = new List<byte>();
        void Set(int point, int cvtX, int cvtY)
        {
            code.Add(SvtcaX);
            code.AddRange(Push(0));
            code.AddRange(Const(cvtX));
            code.Add(Wcvtp);
            code.AddRange(Push(point));
            code.AddRange(Push(0));
            code.Add(Miap0);
            // MIAP would reset the other axis, so y is set with SCFS, which keeps x.
            code.Add(SvtcaY);
            code.AddRange(Push(point));
            code.AddRange(Const(cvtY));
            code.Add(0x48);
        }

        code.AddRange(Push(0));
        code.Add(Szps);
        Set(1, int.MinValue, 3);
        Set(2, 2, -6);
        Set(3, -715827881, -5);
        foreach (int p in new[] { 4, 0, 1, 2, 3 })
        {
            code.AddRange(Push(p));
        }

        code.Add(0x0F); // ISECT
        return [.. code];
    }

    [Fact]
    public void IsectWithAnOverflowingDivisionDoesNotThrow()
    {
        TrueTypeHinter h = Hinter(Nop);
        bool ran = false;
        Exception? thrown = Record.Exception(() => ran = Hint(h, IsectProgram, out _));
        Assert.Null(thrown);
        Assert.True(ran, "the program did not run to its end");
    }

    // ---- defect 3: the twilight zone persists from the control value program ----

    // Prep: twilight point 0 is set to 10 pixels (640 in 26.6) along y.
    private static readonly byte[] PrepSetsTwilight = Cat([SvtcaY], Push(0), [0x13], Push(0), Push(640), [Wcvtp], Push(0), Push(0), [Miap0]);

    // Glyph: reads twilight point 0 and puts glyph point 3 at its y coordinate.
    private static readonly byte[] GlyphReadsTwilight = Cat([SvtcaY], Push(3), Push(0), [0x15], Push(0), [0x46], Push(1), [0x15, 0x48]);

    // Glyph: moves twilight point 0 to y = 0.
    private static readonly byte[] GlyphClearsTwilight = Cat([SvtcaY], Push(0), [0x13], Push(0), Push(1), [Miap0]);

    [Fact]
    public void TwilightPointsSetByTheControlValueProgramReachGlyphPrograms()
    {
        TrueTypeHinter h = Hinter(PrepSetsTwilight);
        Assert.True(Hint(h, GlyphReadsTwilight, out float[] y));
        Assert.Equal(10f * 1000 / 12, y[3], 0.01f); // 10 px in font units at 12 px per 1000 units
    }

    [Fact]
    public void EachGlyphStartsFromItsOwnCopyOfTheTwilightZone()
    {
        TrueTypeHinter h = Hinter(PrepSetsTwilight);
        Assert.True(Hint(h, GlyphClearsTwilight, out _));
        Assert.True(Hint(h, GlyphReadsTwilight, out float[] y));
        Assert.Equal(10f * 1000 / 12, y[3], 0.01f);
    }

    // ---- defect 4: one budget per top-level glyph ----

    // Counts 30000 loop iterations (about 180k instructions), then rounds point 3 so the glyph visibly moves.
    private static readonly byte[] SlowProgram =
    [
        0xB8, 0x75, 0x30,           // PUSHW 30000
        0xB0, 0x01, 0x61, 0x20,     // PUSHB 1; SUB; DUP
        0xB8, 0xFF, 0xF8, 0x23, 0x78, // PUSHW -8; SWAP; JROT (back to PUSHB 1)
        0x00, 0xB0, 0x03, 0x2F,     // SVTCA[y]; PUSHB 3; MDAP[rnd]
    ];

    internal static byte[] Square(byte[] instructions)
    {
        var d = new List<byte>();
        void W(int v) { d.Add((byte)(v >> 8)); d.Add((byte)v); }
        W(1); W(100); W(0); W(600); W(700); W(3); W(instructions.Length);
        d.AddRange(instructions);
        d.AddRange([1, 1, 1, 1]);
        foreach (int v in new[] { 100, 500, 0, -500, 0, 0, 700, 0 })
        {
            W(v);
        }

        return [.. d];
    }

    internal static byte[] Composite(int glyph, int count)
    {
        var d = new List<byte>();
        void W(int v) { d.Add((byte)(v >> 8)); d.Add((byte)v); }
        W(-1); W(0); W(0); W(0); W(0);
        for (int i = 0; i < count; i++)
        {
            W(i == count - 1 ? 0x0003 : 0x0023);
            W(glyph);
            W(0);
            W(0);
        }

        return [.. d];
    }

    private static FontFace Face(byte[] leaf, int components)
    {
        byte[] font = MiniFont.TrueTypeHinted([[], Square(leaf), Composite(1, components)], Nop);
        return FontFile.Parse(font)[0];
    }

    [Fact]
    public void ACompositeWithinTheSharedBudgetIsHinted()
    {
        FontFace face = Face(SlowProgram, 4);
        Assert.NotEqual(face.GetOutline(1).Coords, face.GetHintedOutline(1, 12f).Coords); // the leaf alone moves
        Assert.NotEqual(face.GetOutline(2).Coords, face.GetHintedOutline(2, 12f).Coords);
    }

    [Fact]
    public void ACompositeWhoseComponentsTogetherExceedTheBudgetIsDrawnUnhinted()
    {
        // Eight components of 180k instructions each pass the per-program limit but not the per-glyph total.
        FontFace face = Face(SlowProgram, 8);
        Assert.Equal(face.GetOutline(2).Coords, face.GetHintedOutline(2, 12f).Coords);
    }

    [Fact]
    public void ManyHostileComponentsStayBounded()
    {
        FontFace face = Face(Loop, 500);
        var sw = Stopwatch.StartNew();
        Assert.Equal(face.GetOutline(2).Coords, face.GetHintedOutline(2, 12f).Coords);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    // ---- fuzz seeds ----

    private static IEnumerable<(string File, byte[] Data)> Seeds()
    {
        foreach ((string name, byte[] program) in MinValuePrograms)
        {
            yield return ($"hint-minvalue-{name}.ttf", MiniFont.TrueTypeHinted([[], Square(program)], Nop));
        }

        yield return ("hint-isect.ttf", MiniFont.TrueTypeHinted([[], Square(IsectProgram)], Nop));
        yield return ("hint-twilight.ttf", MiniFont.TrueTypeHinted([[], Square(GlyphReadsTwilight)], PrepSetsTwilight));
        yield return ("hint-composite-budget.ttf", MiniFont.TrueTypeHinted([[], Square(SlowProgram), Composite(1, 8)], Nop));
    }

    [Fact]
    public void CommittedFuzzSeedsMatchTheirGenerator()
    {
        // Set ONEIMAGE_WRITE_SEEDS=<dir> to regenerate tests/testdata/fonts/hint-*.ttf.
        string? write = Environment.GetEnvironmentVariable("ONEIMAGE_WRITE_SEEDS");
        foreach ((string file, byte[] data) in Seeds())
        {
            if (!string.IsNullOrEmpty(write))
            {
                File.WriteAllBytes(Path.Combine(write, file), data);
            }
            else
            {
                Assert.Equal(data, TestFonts.Bytes(file));
            }
        }
    }
}
