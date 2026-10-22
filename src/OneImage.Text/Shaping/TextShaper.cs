using System.Globalization;
using OneImage.Text.Fonts;

namespace OneImage.Text.Shaping;

/// <summary>One glyph of a shaped run. Advances and offsets are in font units; the advance already includes pair adjustments.</summary>
internal struct ShapedRunGlyph
{
    public int GlyphId;

    /// <summary>The UTF-16 index of the first character the glyph stands for.</summary>
    public int Cluster;

    /// <summary>The first code point the glyph stands for.</summary>
    public int CodePoint;

    public int XAdvance;
    public int XOffset;
    public int YOffset;
}

/// <summary>
/// The OpenType shaping step: code points in, positioned glyphs out. A run is split by script, each part is mapped through the
/// character map, substituted with GSUB, positioned with GPOS (or the legacy <c>kern</c> table when GPOS has no kerning), and its
/// attachments are resolved into offsets.
/// </summary>
internal static class TextShaper
{
    /// <summary>Shapes <paramref name="codePoints"/> (with the UTF-16 index of each) and appends the glyphs to <paramref name="output"/>.</summary>
    public static void Shape(FontFace face, ReadOnlySpan<int> codePoints, ReadOnlySpan<int> clusters, uint language, FeatureSet features, List<ShapedRunGlyph> output)
    {
        int n = codePoints.Length;
        int start = 0;
        uint script = 0;
        while (start < n)
        {
            // A run is a stretch of one script; characters of no script (spaces, digits, punctuation) join the run they sit in.
            int end = start;
            uint runScript = 0;
            while (end < n)
            {
                uint s = Scripts.Of(codePoints[end]);
                if (s > Scripts.Inherited)
                {
                    if (runScript == 0)
                    {
                        runScript = s;
                    }
                    else if (s != runScript)
                    {
                        break;
                    }
                }

                end++;
            }

            if (runScript == 0)
            {
                runScript = script != 0 ? script : Tag.Latn;
            }

            script = runScript;
            GlyphBuffer buffer = new(end - start);
            OtFace layout = face.Layout;
            bool classes = layout.Gdef is { HasGlyphClasses: true };
            for (int k = start; k < end; k++)
            {
                int cp = codePoints[k];
                int glyph = face.GetGlyphIndex(cp);
                byte cls = classes ? (byte)layout.Gdef!.GlyphClass(glyph) : (Scripts.IsMark(cp) ? (byte)Gdef.ClassMark : (byte)Gdef.ClassBase);
                buffer.Add(new GlyphInfo { Glyph = glyph, Cluster = clusters[k], CodePoint = cp, Class = cls });
            }

            ShapeBuffer(face, buffer, runScript, language, features, end - start, output);
            start = end;
        }
    }

    /// <summary>Shapes a run of glyph ids directly, as if each had a plain base-letter character. Used to exercise the tables without a character map.</summary>
    public static void ShapeGlyphs(FontFace face, ReadOnlySpan<int> glyphs, uint language, FeatureSet features, List<ShapedRunGlyph> output)
    {
        GlyphBuffer buffer = new(glyphs.Length);
        OtFace layout = face.Layout;
        for (int k = 0; k < glyphs.Length; k++)
        {
            byte cls = layout.Gdef is { HasGlyphClasses: true } g ? (byte)g.GlyphClass(glyphs[k]) : (byte)Gdef.ClassBase;
            buffer.Add(new GlyphInfo { Glyph = glyphs[k], Cluster = k, CodePoint = 0x41, Class = cls });
        }

        ShapeBuffer(face, buffer, Tag.Latn, language, features, glyphs.Length, output);
    }

    private static void ShapeBuffer(FontFace face, GlyphBuffer buffer, uint script, uint language, FeatureSet features, int textLength, List<ShapedRunGlyph> output)
    {
        OtFace layout = face.Layout;
        ScriptShaper shaper = GenericShaper.Instance;
        ShapePlan plan = layout.GetPlan(shaper, script, language, features);
        shaper.PrepareRun(buffer);

        if (layout.Gsub is { } gsub)
        {
            var engine = new Engine(gsub, layout.Gdef, buffer, face.NumGlyphs, textLength);
            foreach (PlannedLookup[] stage in plan.Gsub)
            {
                if (stage.Length > 0)
                {
                    engine.Run(stage);
                }
            }
        }

        int count = buffer.Count;
        buffer.AllocatePositions();
        for (int i = 0; i < count; i++)
        {
            buffer.Pos[i].XAdvance = face.GetAdvance(buffer.Info[i].Glyph);
        }

        if (layout.Gpos is { } gpos && plan.Gpos.Length > 0)
        {
            new Engine(gpos, layout.Gdef, buffer, face.NumGlyphs, textLength).Run(plan.Gpos);
            Engine.ResolveAttachments(buffer);
        }

        if (!plan.HasGposKern && face.HasLegacyKerning && (!features.TryGet(Tag.Kern, out int kern) || kern != 0))
        {
            for (int i = 0; i + 1 < count; i++)
            {
                if (buffer.Info[i].Glyph != 0)
                {
                    buffer.Pos[i].XAdvance += face.GetLegacyKerning(buffer.Info[i].Glyph, buffer.Info[i + 1].Glyph);
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            ref GlyphInfo info = ref buffer.Info[i];
            ref GlyphPos pos = ref buffer.Pos[i];
            output.Add(new ShapedRunGlyph
            {
                GlyphId = info.Glyph,
                Cluster = info.Cluster,
                CodePoint = info.CodePoint,
                XAdvance = pos.XAdvance,
                XOffset = pos.XOffset,
                YOffset = pos.YOffset,
            });
        }
    }

    private static readonly FeatureSet KernOnly = FeatureSet.Create([(Tag.Mark, 0), (Tag.Mkmk, 0), (Tag.Curs, 0)]);

    /// <summary>The kerning between two glyphs for Latin text: the change to the first glyph's advance, in font units.</summary>
    public static int PairKerning(FontFace face, int left, int right)
    {
        OtFace layout = face.Layout;
        var buffer = new GlyphBuffer(2);
        buffer.Add(new GlyphInfo { Glyph = left, Class = Gdef.ClassBase });
        buffer.Add(new GlyphInfo { Glyph = right, Class = Gdef.ClassBase });
        buffer.AllocatePositions();
        buffer.Pos[0].XAdvance = face.GetAdvance(left);
        buffer.Pos[1].XAdvance = face.GetAdvance(right);
        ShapePlan plan = layout.GetPlan(GenericShaper.Instance, Tag.Latn, 0, KernOnly);
        if (layout.Gpos is { } gpos && plan.HasGposKern)
        {
            new Engine(gpos, layout.Gdef, buffer, face.NumGlyphs, 2).Run(plan.Gpos);
            return buffer.Pos[0].XAdvance - face.GetAdvance(left);
        }

        return face.HasLegacyKerning ? face.GetLegacyKerning(left, right) : 0;
    }
}

/// <summary>Which of the simple scripts a character belongs to, from the Unicode block it sits in.</summary>
internal static class Scripts
{
    /// <summary>Spaces, digits, punctuation and symbols: they take the script of their surroundings.</summary>
    public const uint Common = 0;

    /// <summary>Combining marks: they take the script of the base they follow.</summary>
    public const uint Inherited = 1;

    /// <summary>A script without a shaper of its own; shaped with the default script's rules.</summary>
    public const uint Other = 2;

    public static uint Of(int cp)
    {
        if (cp < 0x80)
        {
            return (uint)((cp | 0x20) - 'a') < 26u ? Tag.Latn : Common;
        }

        if (cp is (>= 0x300 and <= 0x36F) or (>= 0x1AB0 and <= 0x1AFF) or (>= 0x1DC0 and <= 0x1DFF) or (>= 0x20D0 and <= 0x20FF) or (>= 0xFE20 and <= 0xFE2F))
        {
            return Inherited;
        }

        if (cp is 0xAA or 0xBA or (>= 0xC0 and <= 0x2AF and not 0xD7 and not 0xF7) or (>= 0x1D00 and <= 0x1D25) or (>= 0x1E00 and <= 0x1EFF)
            or (>= 0x2C60 and <= 0x2C7F) or (>= 0xA722 and <= 0xA7FF) or (>= 0xAB30 and <= 0xAB5A) or (>= 0xFB00 and <= 0xFB06)
            or (>= 0xFF21 and <= 0xFF3A) or (>= 0xFF41 and <= 0xFF5A))
        {
            return Tag.Latn;
        }

        if (cp is (>= 0x370 and <= 0x3FF and not 0x37E and not 0x385 and not 0x387) or (>= 0x1F00 and <= 0x1FFF) or 0x2126)
        {
            return Tag.Grek;
        }

        if (cp is (>= 0x400 and <= 0x52F) or (>= 0x1C80 and <= 0x1C8F) or (>= 0x2DE0 and <= 0x2DFF) or (>= 0xA640 and <= 0xA69F))
        {
            return Tag.Cyrl;
        }

        // Latin-1 and Latin Extended letters were handled above; what is left below Greek is punctuation and symbols.
        return cp >= 0x370 && IsLetter(cp) ? Other : Common;
    }

    private static bool IsLetter(int cp)
    {
        UnicodeCategory c = CharUnicodeInfo.GetUnicodeCategory(cp);
        return c <= UnicodeCategory.OtherLetter || c == UnicodeCategory.LetterNumber;
    }

    /// <summary>Whether the character is a combining mark (Mn, Mc, Me).</summary>
    public static bool IsMark(int cp)
    {
        if (cp < 0x300)
        {
            return false;
        }

        UnicodeCategory c = CharUnicodeInfo.GetUnicodeCategory(cp);
        return c is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }
}
