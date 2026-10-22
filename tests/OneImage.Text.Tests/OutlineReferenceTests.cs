using System.Numerics;
using OneImage.Text.Fonts;
using System.Text;

namespace OneImage.Text.Tests;

/// <summary>Glyph outlines against SixLabors.Fonts 2.1.3 rendered at one pixel per font unit, so coordinates are font units.</summary>
public class OutlineReferenceTests
{
    public static TheoryData<string, int, string> Cases => new()
    {
        { TestFonts.Regular, 0, "AgQ@&%8aéÅЖΩß€,.i" },
        { TestFonts.Collection, 0, "Ragéñ" },
        { TestFonts.Collection, 1, "RagéñAV" },
        { TestFonts.Cff, 0, "AgQ@&%8aéÅЖΩß€日本語あ一二三" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void OutlinesMatchTheReferencePointForPoint(string file, int faceIndex, string chars)
    {
        FontFace face = TestFonts.Face(file, faceIndex);
        var refFont = Reference.Font(file, face.UnitsPerEm, faceIndex);
        var options = new SixLabors.Fonts.TextOptions(refFont);
        double worst = 0;
        int glyphs = 0;
        foreach (Rune r in chars.EnumerateRunes())
        {
            var rec = new OutlineRecorder();
            SixLabors.Fonts.TextRenderer.RenderTextTo(rec, r.ToString(), options);
            Outline ours = face.GetOutline(face.GetGlyphIndex(r.Value));
            Assert.NotEmpty(rec.Commands);
            worst = Math.Max(worst, Compare(ours, rec, r.ToString()));
            glyphs++;
        }

        Assert.Equal(chars.EnumerateRunes().Count(), glyphs);

        TestReport.Line($"OUTLINE {file}#{faceIndex} ({glyphs} glyphs): largest point difference {worst}");

        // Same float32 arithmetic on integer inputs: the points agree to rounding of the layout offset.
        Assert.True(worst < 0.01, $"largest point difference {worst}");
    }

    // Both outlines are reduced to cyclic lists of segments (control points and end vertex), without zero-length lines or a closing
    // line that only repeats the start, rotated to start at the lowest vertex. The reference starts contours elsewhere and adds
    // degenerate lines, so the raw command lists differ even where the geometry is identical.
    private static double Compare(Outline ours, OutlineRecorder rec, string label)
    {
        List<(char Verb, Vector2[] Points)> theirs = rec.Commands;
        Vector2 theirMin = new(theirs.SelectMany(c => c.Points).Min(x => x.X), theirs.SelectMany(c => c.Points).Min(x => x.Y));
        var mine = new List<(char, Vector2[])>();
        int p = 0;
        foreach (byte v in ours.Verbs)
        {
            int n = v switch { Outline.Move or Outline.Line => 1, Outline.Quad => 2, Outline.Cubic => 3, _ => 0 };
            var pts = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                pts[i] = new Vector2(ours.Coords[p], -ours.Coords[p + 1]);
                p += 2;
            }

            mine.Add((v switch { Outline.Move => 'M', Outline.Line => 'L', Outline.Quad => 'Q', Outline.Cubic => 'C', _ => 'Z' }, pts));
        }

        Vector2 ourMin = new(mine.SelectMany(c => c.Item2).Min(x => x.X), mine.SelectMany(c => c.Item2).Min(x => x.Y));
        Vector2 delta = theirMin - ourMin;
        List<List<(Vector2[] Ctl, Vector2 End)>> a = Contours(mine, Vector2.Zero);
        List<List<(Vector2[] Ctl, Vector2 End)>> b = Contours(theirs, -delta);
        Assert.True(a.Count == b.Count, $"{label}: contours {a.Count} vs {b.Count}");
        double worst = 0;
        foreach (var ca in a)
        {
            double best = double.MaxValue;
            foreach (var cb in b)
            {
                if (cb.Count == ca.Count)
                {
                    best = Math.Min(best, Distance(ca, cb));
                }
            }

            Assert.True(best < double.MaxValue, $"{label}: no contour with {ca.Count} segments in the reference");
            worst = Math.Max(worst, best);
        }

        return worst;
    }

    private static double Distance(List<(Vector2[] Ctl, Vector2 End)> a, List<(Vector2[] Ctl, Vector2 End)> b)
    {
        double worst = 0;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Ctl.Length != b[i].Ctl.Length)
            {
                return double.MaxValue / 2;
            }

            worst = Math.Max(worst, Vector2.Distance(a[i].End, b[i].End));
            for (int k = 0; k < a[i].Ctl.Length; k++)
            {
                worst = Math.Max(worst, Vector2.Distance(a[i].Ctl[k], b[i].Ctl[k]));
            }
        }

        return worst;
    }

    private static List<List<(Vector2[] Ctl, Vector2 End)>> Contours(List<(char Verb, Vector2[] Points)> cmds, Vector2 shift)
    {
        var all = new List<List<(Vector2[], Vector2)>>();
        List<(Vector2[] Ctl, Vector2 End)>? cur = null;
        Vector2 start = default, last = default;
        void Finish()
        {
            if (cur is null)
            {
                return;
            }

            if (last != start)
            {
                cur.Add(([], start));
            }

            // Rotate so the segment ending at the lowest vertex is last.
            int low = 0;
            for (int i = 1; i < cur.Count; i++)
            {
                if (cur[i].End.X < cur[low].End.X || (cur[i].End.X == cur[low].End.X && cur[i].End.Y < cur[low].End.Y))
                {
                    low = i;
                }
            }

            var rotated = new List<(Vector2[], Vector2)>();
            for (int i = 1; i <= cur.Count; i++)
            {
                rotated.Add(cur[(low + i) % cur.Count]);
            }

            all.Add(rotated);
            cur = null;
        }

        foreach ((char verb, Vector2[] pts) in cmds)
        {
            Vector2[] q = [.. pts.Select(x => x + shift)];
            switch (verb)
            {
                case 'M':
                    Finish();
                    cur = [];
                    start = last = q[0];
                    break;
                case 'L':
                    if (q[0] != last)
                    {
                        cur!.Add(([], q[0]));
                        last = q[0];
                    }

                    break;
                case 'Q':
                case 'C':
                    cur!.Add((q[..^1], q[^1]));
                    last = q[^1];
                    break;
                default:
                    Finish();
                    break;
            }
        }

        Finish();
        return all;
    }
}
