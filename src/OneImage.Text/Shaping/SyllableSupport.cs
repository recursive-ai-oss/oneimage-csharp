using OneImage.Text.Fonts;
using OneImage.Text.Unicode;

namespace OneImage.Text.Shaping;

/// <summary>Helpers the Indic, Universal Shaping Engine and Thai shapers share: glyph records, bounded reordering and feature probes.</summary>
internal static class SyllableSupport
{
    /// <summary>The longest syllable (in glyphs) the shapers reorder as a unit; longer stretches are cut into pieces, which bounds all per-syllable work.</summary>
    public const int MaxSyllable = 48;

    /// <summary>The dotted circle shown under a mark that has no base.</summary>
    public const int DottedCircle = 0x25CC;

    public static GlyphInfo MakeInfo(FontFace face, int cp, int cluster, byte cat)
    {
        OtFace layout = face.Layout;
        int glyph = face.GetGlyphIndex(cp);
        if (glyph == 0 && cp == 0xA0)
        {
            // A font without a no-break space shows the space glyph instead of a missing-glyph box.
            glyph = face.GetGlyphIndex(' ');
        }

        byte cls = layout.Gdef is { HasGlyphClasses: true } g ? (byte)g.GlyphClass(glyph) : (Scripts.IsMark(cp) ? (byte)Gdef.ClassMark : (byte)Gdef.ClassBase);
        return new GlyphInfo { Glyph = glyph, Cluster = cluster, CodePoint = cp, Class = cls, Cat = cat };
    }

    /// <summary>Sorts <c>info[start..end)</c> stably by <see cref="GlyphInfo.Pos"/>; syllables are short, so insertion sort is fine and bounded.</summary>
    public static void SortByPos(GlyphInfo[] info, int start, int end)
    {
        for (int i = start + 1; i < end; i++)
        {
            GlyphInfo x = info[i];
            int j = i - 1;
            while (j >= start && info[j].Pos > x.Pos)
            {
                info[j + 1] = info[j];
                j--;
            }

            info[j + 1] = x;
        }
    }

    /// <summary>Moves the glyph at <paramref name="from"/> so it sits just before the glyph now at <paramref name="before"/> (which may be after it).</summary>
    public static void MoveBefore(GlyphInfo[] info, int from, int before)
    {
        if (before == from || before == from + 1)
        {
            return;
        }

        GlyphInfo x = info[from];
        if (before < from)
        {
            Array.Copy(info, before, info, before + 1, from - before);
            info[before] = x;
        }
        else
        {
            Array.Copy(info, from + 1, info, from, before - from - 1);
            info[before - 1] = x;
        }
    }

    /// <summary>The end of the run of glyphs with the same syllable number that starts at <paramref name="start"/>.</summary>
    public static int SyllableEnd(GlyphBuffer buffer, int start)
    {
        int syl = buffer.Info[start].Syllable;
        int e = start + 1;
        while (e < buffer.Count && buffer.Info[e].Syllable == syl)
        {
            e++;
        }

        return e;
    }

    /// <summary>
    /// Whether running the lookups of the given plan stage on <paramref name="a"/> followed by <paramref name="b"/> (or on <paramref name="a"/>
    /// alone when <paramref name="b"/> is negative) would change them; this is how a font declares that a consonant has a reph, half,
    /// below-base or post-base form. Answers are remembered in the plan.
    /// </summary>
    public static bool Would(ShapeContext ctx, int stage, int a, int b)
    {
        ShapePlan plan = ctx.Plan;
        if (stage >= plan.Gsub.Length || plan.Gsub[stage].Length == 0 || ctx.Layout.Gsub is not { } gsub)
        {
            return false;
        }

        long key = ((long)stage << 40) | ((long)(uint)(a & 0xFFFFF) << 20) | (uint)(b & 0xFFFFF);
        if (plan.Probes.TryGetValue(key, out bool known))
        {
            return known;
        }

        var tmp = new GlyphBuffer(4);
        tmp.Add(new GlyphInfo { Glyph = a, Class = ClassOf(ctx, a), Mask = uint.MaxValue });
        if (b >= 0)
        {
            tmp.Add(new GlyphInfo { Glyph = b, Class = ClassOf(ctx, b), Mask = uint.MaxValue });
        }

        int before = tmp.Count;
        new Engine(gsub, ctx.Layout.Gdef, tmp, ctx.Face.NumGlyphs, 2).Run(plan.Gsub[stage]);
        bool changed = tmp.Count != before || tmp.Info[0].Glyph != a || (before > 1 && tmp.Info[1].Glyph != b);
        if (plan.Probes.Count > 8192)
        {
            plan.Probes.Clear();
        }

        plan.Probes[key] = changed;
        return changed;
    }

    private static byte ClassOf(ShapeContext ctx, int glyph) =>
        ctx.Layout.Gdef is { HasGlyphClasses: true } g ? (byte)g.GlyphClass(glyph) : (byte)Gdef.ClassBase;
}
