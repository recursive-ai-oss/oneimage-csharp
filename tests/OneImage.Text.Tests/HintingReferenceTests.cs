using OneImage.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Sharp = SixLabors.ImageSharp;

namespace OneImage.Text.Tests;

/// <summary>Hinted rendering against SixLabors.Fonts 2.1.3 / ImageSharp.Drawing 2.1.7 with HintingMode.Standard, black text on white.</summary>
public class HintingReferenceTests
{
    public const string HintedFile = "NotoSans-Hinted-Subset.ttf";

    private static readonly string[] Texts =
    [
        "Hamburgefonstiv",
        "Sphinx of black quartz, judge my vow 0123456789",
        "The quick brown fox jumps over the lazy dog",
    ];

    internal static Font Hinted(float size)
    {
        var c = new FontCollection();
        c.Add(TestFonts.PathOf(HintedFile));
        return c.Get("Noto Sans").CreateFont(size);
    }

    [Fact]
    public void HintedRendersAgreeWithTheReferenceAtSmallSizes()
    {
        const int W = 420, H = 40;
        double sumHinted = 0, sumUnhinted = 0, sumCross = 0;
        int cases = 0, closer = 0;
        double worst = 0;
        for (int size = 9; size <= 16; size++)
        {
            foreach (string text in Texts)
            {
                var o = new TextOptions(Hinted(size)) { Origin = new System.Numerics.Vector2(4, 4), HintingMode = HintingMode.Standard };
                var plain = new TextOptions(Hinted(size)) { Origin = new System.Numerics.Vector2(4, 4) };
                byte[] hinted = Ours(text, o, W, H);
                byte[] unhinted = Ours(text, plain, W, H);
                byte[] theirsHinted = Theirs(text, o, W, H, size, true);
                byte[] theirsPlain = Theirs(text, o, W, H, size, false);
                double a = Mean(hinted, theirsHinted);
                double b = Mean(unhinted, theirsHinted);
                sumHinted += a;
                sumUnhinted += b;
                sumCross += Mean(theirsPlain, theirsHinted);
                worst = Math.Max(worst, a);
                cases++;
                if (a < b)
                {
                    closer++;
                }
            }
        }

        TestReport.Line($"HINTING {cases} cases 9-16 px: hinted vs reference-hinted mean {sumHinted / cases:F3} (worst {worst:F3}); unhinted vs reference-hinted {sumUnhinted / cases:F3}; reference hinted vs reference unhinted {sumCross / cases:F3}; hinted closer in {closer}");
        Assert.True(sumHinted / cases < sumUnhinted / cases, "hinting should move us toward the reference");
    }

    private static byte[] Ours(string text, TextOptions o, int w, int h)
    {
        var white = new Rgba32[w * h];
        Array.Fill(white, new Rgba32(255, 255, 255, 255));
        Rgba32[] px = Image.FromPixels<Rgba32>(white, w, h).DrawText(text, o, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();
        return px.Select(p => p.R).ToArray();
    }

    private static byte[] Theirs(string text, TextOptions o, int w, int h, float size, bool hinting)
    {
        var c = new SixLabors.Fonts.FontCollection();
        c.Add(TestFonts.PathOf(HintedFile));
        SixLabors.Fonts.Font font = c.Get("Noto Sans").CreateFont(size);
        var options = Reference.Options(font, o);
        options.HintingMode = hinting ? SixLabors.Fonts.HintingMode.Standard : SixLabors.Fonts.HintingMode.None;
        using var img = new Sharp.Image<Sharp.PixelFormats.Rgba32>(w, h, Sharp.Color.White);
        img.Mutate(ctx => ctx.DrawText(options, text, Sharp.Color.Black));
        var res = new byte[w * h];
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                Span<Sharp.PixelFormats.Rgba32> row = rows.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    res[(y * w) + x] = row[x].R;
                }
            }
        });
        return res;
    }

    private static double Mean(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return (double)sum / a.Length;
    }
}
