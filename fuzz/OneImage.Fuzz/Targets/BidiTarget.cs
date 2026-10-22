using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OneImage.Text;

/// <summary>
/// Mixed-direction text through the bidirectional algorithm, Arabic joining and layout. The input is one option byte (paragraph
/// direction in bits 0-1, folding in bit 2, wrapping in bit 3), one byte of wrapping length and the rest as UTF-16 code units. With
/// folding, each unit is mapped onto a small alphabet of Latin, Arabic, Hebrew, digits, brackets, joiners and direction controls, so
/// mutations keep producing text that exercises reordering. Lines must stay inside the text, not overlap, and every character box
/// must lie inside the text; anything but a typed error is a finding.
/// </summary>
internal static class BidiTarget
{
    private static readonly char[] Alphabet =
    {
        'a', 'b', 'c', ' ', ' ', '1', '2', '(', ')', '[', ']', '.', ',', '-', '\n',
        '\u0627', '\u0628', '\u0644', '\u0645', '\u064A', '\u0629', '\u0640', '\u064E', '\u0651', '\u0661', '\u0662', '\u200D', '\u200C',
        '\u05D0', '\u05D1', '\u05E9', '\u05B0', '\u05BC',
        '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u2066', '\u2067', '\u2068', '\u2069', '\u200E', '\u200F', '\u061C',
    };

    private static Font? font;

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        byte options = data[0];
        float wrap = (options & 8) != 0 ? (data[1] % 200) + 1 : 0;
        int count = Math.Min((data.Length - 2) / 2, 1024);
        char[] chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            char c = (char)(data[2 + (2 * i)] | (data[3 + (2 * i)] << 8));
            chars[i] = (options & 4) != 0 ? Alphabet[c % Alphabet.Length] : c;
        }

        string text = new(chars);
        font ??= LoadFont();
        if (font is null)
        {
            return;
        }

        var o = new TextOptions(font)
        {
            WrappingLength = wrap,
            TextDirection = (TextDirection)((options & 3) % 3),
            TextAlignment = (TextAlignment)((options >> 4) % 3),
            Origin = new Vector2(4, 4),
        };
        Targets.Guard(() =>
        {
            TextMetrics m = TextMeasurer.Measure(text, o);
            int end = -1;
            foreach (LineMetrics line in m.Lines)
            {
                Check(line.Start >= 0 && line.Length >= 0 && line.Start + line.Length <= text.Length, "line outside the text");
                Check(line.Start >= end, "lines overlap");
                end = line.Start + line.Length;
            }

            foreach (GlyphBounds b in TextMeasurer.GetCharacterBounds(text, o))
            {
                Check(b.Index >= 0 && b.Index < text.Length, "character box outside the text");
            }

            _ = TextBuilder.GeneratePath(text, o);
        });
    }

    private static void Check(bool ok, string message)
    {
        if (!ok)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static Font? LoadFont()
    {
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_FUZZ_BIDI_FONT");
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        foreach (FontFamily family in new FontCollection().AddCollection(File.ReadAllBytes(path)))
        {
            return family.CreateFont(16);
        }

        return null;
    }

    /// <summary>Writes one seed per case of BidiCharacterTest.txt (every 40th), with the paragraph direction from the case.</summary>
    public static void WriteSeeds(string testFile, string directory)
    {
        Directory.CreateDirectory(directory);
        int n = 0;
        foreach (string raw in File.ReadLines(testFile))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0 || n++ % 40 != 0)
            {
                continue;
            }

            string[] fields = line.Split(';');
            int dir = int.Parse(fields[1]) switch { 0 => 1, 1 => 2, _ => 0 };
            var bytes = new List<byte> { (byte)(dir | (n % 3 == 0 ? 8 : 0)), (byte)((n * 7) % 90) };
            foreach (string cp in fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (char c in char.ConvertFromUtf32(Convert.ToInt32(cp, 16)))
                {
                    bytes.Add((byte)c);
                    bytes.Add((byte)(c >> 8));
                }
            }

            File.WriteAllBytes(Path.Combine(directory, $"seed-bidi-{n}"), bytes.ToArray());
        }
    }
}
