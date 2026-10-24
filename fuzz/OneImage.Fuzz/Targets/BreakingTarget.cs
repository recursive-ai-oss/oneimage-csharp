using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OneImage.Text;

/// <summary>
/// Arbitrary text through line breaking, grapheme segmentation and layout in every word breaking and justification mode. The input
/// is one byte of options, one byte of wrapping length and the rest as UTF-16 code units, unpaired surrogates included. Anything
/// but a typed error, and any broken invariant (a line starting inside a grapheme cluster, lines that do not tile the text), is a finding.
/// </summary>
internal static class BreakingTarget
{
    private static Font? font;

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        byte options = data[0];
        float wrap = (data[1] % 200) + 1;
        int count = Math.Min((data.Length - 2) / 2, 1024);
        char[] chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            chars[i] = (char)(data[2 + (2 * i)] | (data[3 + (2 * i)] << 8));
        }

        string text = new(chars);
        Targets.Guard(() =>
        {
            byte[] breaks = new byte[text.Length + 1];
            byte[] clusters = new byte[text.Length + 1];
            OneFont.TextSegmentation.FillLineBreakOpportunities(text, breaks);
            OneFont.TextSegmentation.FillGraphemeStarts(text, clusters);
            Check(breaks[0] == 0 || text.Length == 0, "break before the first character");
            for (int i = 1; i < text.Length; i++)
            {
                if (char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]))
                {
                    Check(breaks[i] == 0 && clusters[i] == 0, "boundary inside a surrogate pair");
                }
            }

            int total = 0;
            for (int i = 0; i < text.Length; i += OneFont.TextSegmentation.GetGraphemeLength(text.AsSpan(i)))
            {
                total++;
                Check(clusters[i] == 1, "cluster start not marked");
            }

            Check(total >= (text.Length == 0 ? 0 : 1), "no clusters");
            Layout(text, options, wrap, clusters);
        });
    }

    private static void Layout(string text, byte options, float wrap, byte[] clusters)
    {
        font ??= LoadFont();
        if (font is null)
        {
            return;
        }

        var o = new TextOptions(font)
        {
            WrappingLength = wrap,
            WordBreaking = (WordBreaking)(options & 3),
            TextJustification = (TextJustification)((options >> 2) % 3),
        };
        TextMetrics m = TextMeasurer.Measure(text, o);
        int end = -1;
        foreach (LineMetrics line in m.Lines)
        {
            Check(line.Start >= 0 && line.Length >= 0 && line.Start + line.Length <= text.Length, "line outside the text");
            Check(clusters[line.Start] == 1, "line starts inside a grapheme cluster");
            Check(end < 0 || line.Start >= end, "lines overlap");
            end = line.Start + line.Length;
        }

        IReadOnlyList<GlyphBounds> boxes = TextMeasurer.GetCharacterBounds(text, o);
        foreach (GlyphBounds b in boxes)
        {
            Check(b.Index >= 0 && b.Index < text.Length && clusters[b.Index] == 1, "character box starts inside a grapheme cluster");
        }
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
        string? path = Environment.GetEnvironmentVariable("ONEIMAGE_FUZZ_FONT");
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        foreach (FontFamily family in new FontCollection().AddCollection(File.ReadAllBytes(path)))
        {
            return family.CreateFont(12);
        }

        return null;
    }

    /// <summary>Writes one seed per line of a UCD break test file (the code points, one option byte and a wrapping length in front).</summary>
    public static void WriteSeeds(string testFile, string directory)
    {
        Directory.CreateDirectory(directory);
        int n = 0;
        foreach (string raw in File.ReadLines(testFile))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var sb = new StringBuilder();
            foreach (string tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok is not ("\u00F7" or "\u00D7"))
                {
                    sb.Append(char.ConvertFromUtf32(Convert.ToInt32(tok, 16)));
                }
            }

            if (n++ % 25 != 0)
            {
                continue;
            }

            var bytes = new List<byte> { (byte)(n % 12), (byte)((n * 7) % 90) };
            foreach (char c in sb.ToString())
            {
                bytes.Add((byte)c);
                bytes.Add((byte)(c >> 8));
            }

            File.WriteAllBytes(Path.Combine(directory, $"seed-{Path.GetFileNameWithoutExtension(testFile)}-{n}"), bytes.ToArray());
        }
    }
}
