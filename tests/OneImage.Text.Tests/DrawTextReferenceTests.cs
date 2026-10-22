using OneImage.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Sharp = SixLabors.ImageSharp;

namespace OneImage.Text.Tests;

/// <summary>DrawText against SixLabors.ImageSharp.Drawing 2.1.7 DrawText (with SixLabors.Fonts 2.1.3 layout): black text on white.</summary>
public class DrawTextReferenceTests
{
    public static TheoryData<string, float, float, string> Cases() => new()
    {
        { "Hello, World", 24, 0, "hello-24" },
        { "Sphinx of black quartz, judge my vow", 12, 0, "small-12" },
        { "Rag\u00e9 \u0416\u0443\u0440\u043d\u0430\u043b", 48, 0, "big-48" },
        { "The quick brown fox jumps over the lazy dog. Pack my box with nine dozen liquor jugs. How vexingly quick daft zebras jump!", 18, 320, "para-18" },
        { "AV To Wa Ty r. Vo", 36, 0, "kerned-36" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void RenderingAgreesWithTheReference(string text, float size, float wrap, string name)
    {
        const int W = 420, H = 260;
        var mine = new TextOptions(TestFonts.Noto(size)) { Origin = new System.Numerics.Vector2(12.5f, 20.25f), WrappingLength = wrap };
        byte[] ours = Ours(text, mine, W, H);
        byte[] theirs = Theirs(text, mine, W, H, size);
        if (Environment.GetEnvironmentVariable("DUMPPNG") is { } dir)
        {
            var stack = new Gray8[W * H * 2];
            for (int i = 0; i < W * H; i++)
            {
                stack[i] = new Gray8(ours[i]);
                stack[(W * H) + i] = new Gray8(theirs[i]);
            }

            Image.FromPixels<Gray8>(stack, W, H * 2).Save(System.IO.Path.Combine(dir, name + ".png"));
        }

        (double mean, int max, double inkMean) = Compare(ours, theirs);
        TestReport.Line($"DRAWTEXT {name}: mean {mean:F4} max {max} (mean over pixels either renders ink on: {inkMean:F3})");
        Assert.True(mean < 0.35, $"{name}: mean {mean}");
        Assert.True(max < 150, $"{name}: max {max}");
    }

    private static byte[] Ours(string text, TextOptions o, int w, int h)
    {
        var white = new Rgba32[w * h];
        Array.Fill(white, new Rgba32(255, 255, 255, 255));
        Rgba32[] px = Image.FromPixels<Rgba32>(white, w, h).DrawText(text, o, new Rgba32(0, 0, 0, 255)).ToArray<Rgba32>();
        return px.Select(p => p.R).ToArray();
    }

    private static byte[] Theirs(string text, TextOptions o, int w, int h, float size)
    {
        var font = Reference.Font(TestFonts.Regular, size);
        RichTextOptions options = Reference.Options(font, o);
        using var img = new Sharp.Image<Sharp.PixelFormats.Rgba32>(w, h, Sharp.Color.White);
        img.Mutate(c => c.DrawText(options, text, Sharp.Color.Black));
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

    private static (double Mean, int Max, double InkMean) Compare(byte[] a, byte[] b)
    {
        long sum = 0, inkSum = 0, ink = 0;
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            sum += d;
            max = Math.Max(max, d);
            if (a[i] < 255 || b[i] < 255)
            {
                inkSum += d;
                ink++;
            }
        }

        return ((double)sum / a.Length, max, ink == 0 ? 0 : (double)inkSum / ink);
    }
}
