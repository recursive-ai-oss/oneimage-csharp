using System;
using System.IO;
using System.Numerics;
using OneImage;
using OneImage.Drawing;
using OneImage.Text;

/// <summary>
/// Rich text: runs with fonts, sizes, attributes and decorations, a fallback family, wrapping and a path, all taken from the input.
/// Layout, measuring and outlines have to survive any combination; only typed errors may come out.
/// Input: option byte, wrapping byte, run count (low four bits) and path point count (low three bits), then 4 bytes per run, 2 bytes
/// per point and the rest as UTF-16 text (at most 512 code units).
/// </summary>
internal static class RichTextTarget
{
    private static Font?[]? fonts;

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return;
        }

        byte options = data[0];
        float wrap = (options & 1) != 0 ? (data[1] % 200) + 1 : 0;
        int runs = data[2] & 15;
        int points = data[3] & 7;
        int need = 4 + (runs * 4) + (points * 2);
        if (data.Length < need)
        {
            return;
        }

        int count = Math.Min((data.Length - need) / 2, 512);
        char[] chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            chars[i] = (char)(data[need + (2 * i)] | (data[need + 1 + (2 * i)] << 8));
        }

        string text = new(chars);
        fonts ??= LoadFonts();
        if (fonts[0] is not { } primary || fonts[1] is not { } secondary)
        {
            return;
        }

        var o = new TextOptions(primary) { WrappingLength = wrap, Origin = new Vector2(8, 8) };
        if ((options & 2) != 0)
        {
            o.FallbackFontFamilies.Add(secondary.Family);
        }

        for (int r = 0; r < runs; r++)
        {
            int b = 4 + (r * 4);
            int start = data[b] * Math.Max(1, count) / 255;
            int end = start + (data[b + 1] * Math.Max(1, count) / 255);
            byte flags = data[b + 3];
            Font f = ((flags & 32) != 0 ? secondary : primary).WithSize(4 + (data[b + 2] % 60));
            o.TextRuns.Add(new TextRun(start, end)
            {
                Font = (flags & 64) != 0 ? f : null,
                TextDecorations = (TextDecorations)(flags & 7),
                TextAttributes = (TextAttributes)(((flags >> 3) & 3) % 3),
                UnderlinePen = (flags & 128) != 0 ? Pen.Solid(new Rgba32(255, 0, 0, 255), 1) : null,
            });
        }

        if ((options & 4) != 0 && points >= 2)
        {
            var pts = new Vector2[points];
            for (int p = 0; p < points; p++)
            {
                int b = 4 + (runs * 4) + (p * 2);
                pts[p] = new Vector2(data[b] - 20, data[b + 1] - 20);
            }

            o.Path = (options & 8) != 0 ? OneImage.Drawing.Path.Polygon(pts) : OneImage.Drawing.Path.Polyline(pts);
        }

        Targets.Guard(() =>
        {
            TextMetrics m = TextMeasurer.Measure(text, o);
            _ = TextMeasurer.GetCharacterBounds(text, o);
            if (m.Lines.Count > 0 && !(m.Advance.Width >= 0))
            {
                throw new InvalidOperationException("negative width");
            }

            try
            {
                _ = TextBuilder.GeneratePath(text, o);
            }
            catch (ArgumentException) when (o.Path is not null)
            {
                // Documented: a path without length (all points equal) cannot carry text.
            }
        });
    }

    private static Font?[] LoadFonts()
    {
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_FUZZ_FONT");
        if (path is null || !File.Exists(path))
        {
            return [null, null];
        }

        string other = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "NotoSans-Subset-Regular.ttf");
        var collection = new FontCollection();
        FontFamily first = collection.AddCollection(File.ReadAllBytes(path))[0];
        if (!File.Exists(other))
        {
            return [first.CreateFont(12), first.CreateFont(12)];
        }

        FontFamily second = collection.AddCollection(File.ReadAllBytes(other))[0];
        return [second.CreateFont(12), first.CreateFont(12)];
    }
}
