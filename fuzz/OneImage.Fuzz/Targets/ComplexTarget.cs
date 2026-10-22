using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using OneImage.Text;

/// <summary>
/// Text in the complex scripts (Indic, Universal Shaping Engine, Thai and Lao) through shaping, layout, measuring and drawing with the
/// matching Noto subset. The input is one font selector byte, one option byte (direction in bits 0-1, wrapping in bit 3), one byte of
/// wrapping length and the rest as UTF-16 code units. Lines must stay inside the text and every character box must lie inside it;
/// anything but a typed error is a finding.
/// </summary>
internal static partial class ComplexTarget
{
    private static readonly string[] Scripts =
    {
        "devanagari", "bengali", "gurmukhi", "gujarati", "oriya", "tamil", "telugu", "kannada", "malayalam",
        "khmer", "myanmar", "sinhala", "tibetan", "thai", "lao", "balinese", "javanese",
    };

    private static readonly Font?[] Fonts = new Font?[Scripts.Length];

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return;
        }

        int index = data[0] % Scripts.Length;
        byte options = data[1];
        float wrap = (options & 8) != 0 ? (data[2] % 200) + 1 : 0;
        int count = Math.Min((data.Length - 3) / 2, 1024);
        char[] chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            chars[i] = (char)(data[3 + (2 * i)] | (data[4 + (2 * i)] << 8));
        }

        string text = new(chars);
        Font? font = Fonts[index] ??= LoadFont(Scripts[index]);
        if (font is null)
        {
            return;
        }

        var o = new TextOptions(font)
        {
            WrappingLength = wrap,
            TextDirection = (TextDirection)((options & 3) % 3),
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

    private static Font? LoadFont(string script)
    {
        string? dir = Environment.GetEnvironmentVariable("ONEIMAGE_FUZZ_FONT_DIR");
        if (dir is null)
        {
            return null;
        }

        string path = Path.Combine(dir, "Noto" + (script == "tibetan" ? "Serif" : "Sans") + char.ToUpperInvariant(script[0]) + script[1..] + "-Subset-Regular.ttf");
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (FontFamily family in new FontCollection().AddCollection(File.ReadAllBytes(path)))
        {
            return family.CreateFont(16);
        }

        return null;
    }

    /// <summary>Writes one seed per case of the committed complex-script case files (tests/testdata/complex/*.txt).</summary>
    public static void WriteSeeds(string caseDirectory, string directory)
    {
        Directory.CreateDirectory(directory);
        for (int s = 0; s < Scripts.Length; s++)
        {
            string file = Path.Combine(caseDirectory, Scripts[s] + ".txt");
            if (!File.Exists(file))
            {
                continue;
            }

            int n = 0;
            foreach (string raw in File.ReadLines(file))
            {
                if (raw.StartsWith('#'))
                {
                    continue;
                }

                foreach (string word in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string text = Escape().Replace(word, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
                    var bytes = new List<byte> { (byte)s, (byte)(n % 3 == 0 ? 8 : 0), (byte)((n * 7) % 90) };
                    foreach (char c in text)
                    {
                        bytes.Add((byte)c);
                        bytes.Add((byte)(c >> 8));
                    }

                    File.WriteAllBytes(Path.Combine(directory, $"seed-complex-{s}-{n++}"), bytes.ToArray());
                }
            }
        }
    }

    [GeneratedRegex(@"\\u([0-9A-Fa-f]{4})")]
    private static partial Regex Escape();
}
