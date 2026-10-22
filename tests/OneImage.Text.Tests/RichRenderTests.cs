using System.Numerics;
using OneImage.Drawing;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using RefDeco = SixLabors.Fonts.TextDecorations;
using Sharp = SixLabors.ImageSharp;

namespace OneImage.Text.Tests;

/// <summary>Rendered rich text, fallback text and text on a path against SixLabors.ImageSharp.Drawing 2.1.7 (public API, black on white).</summary>
public class RichRenderTests
{
    private const int W = 420, H = 200;

    private static byte[] Ours(Action<Canvas> draw)
    {
        var white = new Rgba32[W * H];
        Array.Fill(white, new Rgba32(255, 255, 255, 255));
        using Canvas canvas = Image.FromPixels<Rgba32>(white, W, H).ToCanvas();
        draw(canvas);
        return canvas.Snapshot().ToArray<Rgba32>().Select(p => p.R).ToArray();
    }

    private static byte[] Theirs(Action<IImageProcessingContext> draw)
    {
        using var img = new Sharp.Image<Sharp.PixelFormats.Rgba32>(W, H, Sharp.Color.White);
        img.Mutate(draw);
        var res = new byte[W * H];
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                Span<Sharp.PixelFormats.Rgba32> row = rows.GetRowSpan(y);
                for (int x = 0; x < W; x++)
                {
                    res[(y * W) + x] = row[x].R;
                }
            }
        });
        return res;
    }

    private static void Report(string name, byte[] ours, byte[] theirs, double meanLimit)
    {
        if (Environment.GetEnvironmentVariable("DUMPPNG") is { } dir)
        {
            var stack = new Gray8[W * H * 2];
            for (int i = 0; i < W * H; i++)
            {
                stack[i] = new Gray8(ours[i]);
                stack[(W * H) + i] = new Gray8(theirs[i]);
            }

            Image.FromPixels<Gray8>(stack, W, H * 2).Save(System.IO.Path.Combine(dir, "rich-" + name + ".png"));
        }

        long sum = 0, inkSum = 0, ink = 0;
        int max = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            int d = Math.Abs(ours[i] - theirs[i]);
            sum += d;
            max = Math.Max(max, d);
            if (ours[i] < 255 || theirs[i] < 255)
            {
                inkSum += d;
                ink++;
            }
        }

        double mean = (double)sum / ours.Length;
        TestReport.Line($"RICHRENDER {name}: mean {mean:F4} max {max} (mean over pixels either renders ink on: {(ink == 0 ? 0 : (double)inkSum / ink):F3}, {ink} ink pixels)");
        Assert.True(ink > 500, $"{name}: nothing drawn");
        Assert.True(mean < meanLimit, $"{name}: mean {mean}");
    }

    [Fact]
    public void RichTextWithDecorationsRenders()
    {
        const string text = "Hello big TEXT one and";
        var mine = new TextOptions(TestFonts.Noto(24)) { Origin = new Vector2(8.5f, 10.25f) };
        mine.TextRuns.Add(new TextRun(6, 9) { Font = TestFonts.Noto(48), TextDecorations = TextDecorations.Underline | TextDecorations.Strikeout });
        mine.TextRuns.Add(new TextRun(10, 14) { Font = TestFonts.Noto(28, FontStyle.Bold), TextDecorations = TextDecorations.Overline });
        RichTextOptions theirs = RichReference.Options(RichReference.Regular(24), mine);
        theirs.TextRuns =
        [
            new RichTextRun { Start = 6, End = 9, Font = RichReference.Regular(48), TextDecorations = RefDeco.Underline | RefDeco.Strikeout },
            new RichTextRun { Start = 10, End = 14, Font = RichReference.Bold(28), TextDecorations = RefDeco.Overline },
        ];
        byte[] a = Ours(c => c.DrawText(text, mine, new Rgba32(0, 0, 0, 255)));
        byte[] b = Theirs(c => c.DrawText(theirs, text, Sharp.Color.Black));
        Report("runs-decorations", a, b, 1.0);
    }

    [Fact]
    public void WrappedRichTextRenders()
    {
        const string text = "Hello big bold wild tall house with many nice big pals going home";
        var mine = new TextOptions(TestFonts.Noto(20)) { Origin = new Vector2(8.5f, 10.25f), WrappingLength = 260 };
        mine.TextRuns.Add(new TextRun(6, 9) { Font = TestFonts.Noto(40), TextDecorations = TextDecorations.Underline });
        mine.TextRuns.Add(new TextRun(30, 35) { TextAttributes = TextAttributes.Superscript });
        RichTextOptions theirs = RichReference.Options(RichReference.Regular(20), mine);
        theirs.TextRuns =
        [
            new RichTextRun { Start = 6, End = 9, Font = RichReference.Regular(40), TextDecorations = RefDeco.Underline },
            new RichTextRun { Start = 30, End = 35, TextAttributes = SixLabors.Fonts.TextAttributes.Superscript },
        ];
        Report("runs-wrapped", Ours(c => c.DrawText(text, mine, new Rgba32(0, 0, 0, 255))), Theirs(c => c.DrawText(theirs, text, Sharp.Color.Black)), 0.5);
    }

    [Fact]
    public void FallbackTextRenders()
    {
        const string text = "AB\u3042\u3044CD \u3042 xx";
        var mine = new TextOptions(TestFonts.Noto(40)) { Origin = new Vector2(8.5f, 10.25f) };
        mine.FallbackFontFamilies.Add(TestFonts.Fonts.Get("Noto Sans CJK JP"));
        RichTextOptions theirs = RichReference.Options(RichReference.Regular(40), mine);
        theirs.FallbackFontFamilies = [RichReference.Family(TestFonts.Cff)];
        Report("fallback", Ours(c => c.DrawText(text, mine, new Rgba32(0, 0, 0, 255))), Theirs(c => c.DrawText(theirs, text, Sharp.Color.Black)), 0.5);
    }

    [Fact]
    public void TextOnAPathRenders()
    {
        const string text = "Hill and valley";
        Vector2[] pts = [new(20, 150), new(150, 60), new(300, 150), new(400, 40)];
        var mine = new TextOptions(TestFonts.Noto(28)) { Path = OneImage.Drawing.Path.Polyline(pts) };
        RichTextOptions theirs = RichReference.Options(RichReference.Regular(28), mine);
        var route = new SixLabors.ImageSharp.Drawing.Path(new LinearLineSegment(pts[0], pts[1], [pts[2], pts[3]]));
        byte[] a = Ours(c => c.DrawText(text, mine, new Rgba32(0, 0, 0, 255)));
        byte[] b = Theirs(c => c.Fill(Sharp.Color.Black, SixLabors.ImageSharp.Drawing.TextBuilder.GenerateGlyphs(text, route, theirs)));
        Report("on-path", a, b, 0.5);
    }
}
