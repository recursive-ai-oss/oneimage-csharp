using System.Buffers.Binary;
using System.Diagnostics;
using OneImage.Text.Fonts;
using OneImage.Text.Layout;

namespace OneImage.Text.Tests;

/// <summary>Damaged and hostile layout tables: shaping must finish, quickly, and never throw.</summary>
public class ShapingLimitsTests
{
    private static readonly string[] Samples =
    [
        "fi ffl office AVATAR To Wa",
        "e\u0301 a\u0308 o\u0302\u0301 e\u0323\u0301 A\u030A \u0328",
        "\u03B1\u0306\u0313\u0301 \u03B9\u0308\u0301 \u03AC \u0438\u0306 \u0430\u0301 \u041F\u0440\u0438\u0432\u0435\u0442",
        "Small Caps 0123456789 1/2 x2 Hello",
    ];

    private static readonly string[] LayoutTags = ["GSUB", "GPOS", "GDEF"];

    private static readonly string?[] FeatureChoices = [null, "smcp", "onum", "aalt"];

    // The sfnt directory entry of a table: (offset, length).
    private static (int Offset, int Length)? FindTable(byte[] font, string tag)
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

        return null;
    }

    private static void ShapeEverything(byte[] bytes)
    {
        FontFace face;
        try
        {
            face = FontFile.Parse(bytes)[0];
        }
        catch (FontFormatException)
        {
            return; // damage to a table the loader checks is allowed to refuse the font
        }

        var fonts = new FontCollection();
        FontFamily family = fonts.Add(bytes);
        foreach (string text in Samples)
        {
            foreach (string? feature in FeatureChoices)
            {
                var o = new TextOptions(family.CreateFont(16)) { Language = "ro" };
                if (feature is not null)
                {
                    o.Features.Add(FontFeature.On(feature));
                }

                _ = TextLayout.Create(text, o);
            }
        }

        _ = face;
    }

    [Fact]
    public void RandomlyDamagedLayoutTablesNeverThrowOrHang()
    {
        byte[] original = File.ReadAllBytes(TestFonts.PathOf(TestFonts.Shaping));
        (int Offset, int Length)[] ranges = [.. LayoutTags.Select(t => FindTable(original, t)!.Value)];
        var rnd = new Random(12345);
        var sw = Stopwatch.StartNew();
        int cases = 0;
        long slowest = 0;
        for (int n = 0; n < 3000; n++)
        {
            byte[] font = (byte[])original.Clone();
            (int offset, int length) = ranges[rnd.Next(ranges.Length)];
            int hits = 1 + rnd.Next(8);
            for (int h = 0; h < hits; h++)
            {
                int at = offset + rnd.Next(length);
                // Favor bytes that sit in the structural header areas, where damage steers walks the most.
                if (rnd.Next(3) == 0)
                {
                    at = offset + rnd.Next(Math.Min(length, 600));
                }

                font[at] = rnd.Next(4) switch { 0 => 0, 1 => 0xFF, 2 => (byte)(font[at] + 1), _ => (byte)rnd.Next(256) };
            }

            long t0 = sw.ElapsedMilliseconds;
            ShapeEverything(font);
            slowest = Math.Max(slowest, sw.ElapsedMilliseconds - t0);
            cases++;
        }

        TestReport.Line($"DAMAGED TABLES {cases} fonts shaped, slowest {slowest} ms, total {sw.ElapsedMilliseconds} ms");
        Assert.True(slowest < 3000, $"one damaged font took {slowest} ms");
    }

    [Fact]
    public void TruncatedLayoutTablesAreIgnored()
    {
        byte[] original = File.ReadAllBytes(TestFonts.PathOf(TestFonts.Shaping));
        foreach (string tag in LayoutTags)
        {
            (int offset, int length) = FindTable(original, tag)!.Value;
            foreach (int keep in new[] { 0, 1, 4, 9, 10, 11, 40, length / 2 })
            {
                byte[] font = (byte[])original.Clone();
                // Zero everything from `keep` on: the table keeps its length but its contents run out.
                Array.Clear(font, offset + keep, length - keep);
                ShapeEverything(font);
            }
        }
    }

    [Fact]
    public void ManyGlyphsShapeInLinearishTime()
    {
        // 200,000 characters with ligature and mark lookups must not behave quadratically on the glyph buffer.
        var o = new TextOptions(TestFonts.ShapingFonts.Get("Noto Sans").CreateFont(14));
        string text = string.Concat(Enumerable.Repeat("office fluffy e\u0301 \u03B1\u0301 ", 8000));
        var sw = Stopwatch.StartNew();
        TextLayout layout = TextLayout.Create(text, o);
        sw.Stop();
        TestReport.Line($"SHAPE {text.Length} chars: {sw.ElapsedMilliseconds} ms, {layout.Glyphs.Count} glyphs");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }
}
