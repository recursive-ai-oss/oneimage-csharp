using System.Numerics;
using OneImage.Text.Fonts;
using OneImage.Text.Layout;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using RefFont = SixLabors.Fonts.Font;
using RefFontCollection = SixLabors.Fonts.FontCollection;

namespace OneImage.Text.Tests;

/// <summary>Records the glyph id, font family and lowest-left outline point of every glyph the reference draws.</summary>
internal sealed class FamilyGlyphRecorder : IGlyphRenderer
{
    public List<(int GlyphId, string Family, Vector2 Min, bool HasOutline)> Glyphs { get; } = [];

    private int _glyphId;
    private string _family = "";
    private Vector2 _min;
    private bool _any;

    public void BeginText(in FontRectangle bounds)
    {
    }

    public bool BeginGlyph(in FontRectangle bounds, in GlyphRendererParameters parameters)
    {
        _glyphId = parameters.GlyphId;
        _family = parameters.Font ?? "";
        _min = new Vector2(float.MaxValue, float.MaxValue);
        _any = false;
        return true;
    }

    private void Touch(Vector2 p)
    {
        _any = true;
        _min = Vector2.Min(_min, p);
    }

    public void BeginFigure()
    {
    }

    public void MoveTo(Vector2 point) => Touch(point);

    public void LineTo(Vector2 point) => Touch(point);

    public void QuadraticBezierTo(Vector2 secondControlPoint, Vector2 point)
    {
        Touch(secondControlPoint);
        Touch(point);
    }

    public void CubicBezierTo(Vector2 secondControlPoint, Vector2 thirdControlPoint, Vector2 point)
    {
        Touch(secondControlPoint);
        Touch(thirdControlPoint);
        Touch(point);
    }

    public void ArcTo(float radiusX, float radiusY, float rotationDegrees, bool largeArc, bool sweep, Vector2 point) => Touch(point);

    public void EndFigure()
    {
    }

    public void EndGlyph() => Glyphs.Add((_glyphId, _family, _min, _any));

    public void EndText()
    {
    }

    public SixLabors.Fonts.TextDecorations EnabledDecorations() => SixLabors.Fonts.TextDecorations.None;

    public void SetDecoration(SixLabors.Fonts.TextDecorations textDecorations, Vector2 start, Vector2 end, float thickness)
    {
    }
}

/// <summary>Our layout of right-to-left and mixed text side by side with SixLabors.Fonts 2.1.3, used only through its public API.</summary>
internal static class BidiHarness
{
    public const string Merged = "NotoSans-Bidi-Subset.ttf";
    public const string Latin = "NotoSans-Shaping-Subset.ttf";
    public const string Arabic = "NotoSansArabic-Subset-Regular.ttf";
    public const string Hebrew = "NotoSansHebrew-Subset-Regular.ttf";

    private static readonly Lazy<FontCollection> OurFonts = new(() =>
    {
        var c = new FontCollection();
        c.Add(TestFonts.PathOf(Merged));
        return c;
    });

    private static readonly Lazy<RefFontCollection> TheirFonts = new(() =>
    {
        var c = new RefFontCollection();
        c.Add(TestFonts.PathOf(Merged));
        return c;
    });

    public sealed record Glyph(int Id, string Family, float X, float Y, int Line = 0, bool LineHasRtl = true);

    /// <summary>
    /// Lays out <paramref name="text"/> in one font that holds the Latin, Arabic and Hebrew subsets, in both libraries, and returns the
    /// glyphs that have outlines as origins in pixels (our pen position plus offset; the reference's outline corner minus ours).
    /// </summary>
    public static (List<Glyph> Ours, List<Glyph> Theirs, TextLayout Layout) Run(string text, float size, Action<TextOptions>? ours = null, Action<RichTextOptions>? theirs = null)
    {
        FontCollection fc = OurFonts.Value;
        var font = fc.Get("Noto Sans").CreateFont(size);
        var o = new TextOptions(font);

        ours?.Invoke(o);
        TextLayout layout = TextLayout.Create(text, o);
        var ourList = new List<Glyph>();
        for (int li = 0; li < layout.Lines.Count; li++)
        {
            LayoutLine line = layout.Lines[li];
            bool hasRtl = false;
            for (int k = line.GlyphStart; k < line.GlyphEnd; k++)
            {
                hasRtl |= (layout.Glyphs[k].Level & 1) == 1;
            }

            for (int k = line.GlyphStart; k < line.GlyphEnd; k++)
            {
                ShapedGlyph g = layout.Glyphs[k];
                GlyphStyle st = layout.Styles[g.Style];
                if (g.IsSpace || st.Face.GetOutline(g.GlyphId).IsEmpty)
                {
                    continue;
                }

                ourList.Add(new Glyph(g.GlyphId, st.Face.FamilyName, line.Left + layout.PenX[k] + g.XOffset, line.Baseline - g.YOffset, li, hasRtl));
            }
        }

        RefFontCollection rc = TheirFonts.Value;
        var rfont = rc.Get("Noto Sans").CreateFont(size);
        var ro = new RichTextOptions(rfont);
        theirs?.Invoke(ro);
        var rec = new FamilyGlyphRecorder();
        TextRenderer.RenderTextTo(rec, text, ro);
        var theirList = new List<Glyph>();
        foreach ((int id, string fam, Vector2 min, bool has) in rec.Glyphs)
        {
            if (!has)
            {
                continue;
            }

            FontFace face = FaceOf(fam);
            Outline outline = face.GetOutline(id);
            float minX = float.MaxValue, minY = float.MaxValue;
            for (int k = 0; k + 1 < outline.Coords.Length; k += 2)
            {
                minX = Math.Min(minX, outline.Coords[k]);
                minY = Math.Min(minY, -outline.Coords[k + 1]);
            }

            float scale = size / face.UnitsPerEm;
            // A glyph belongs to the line whose baseline is nearest to where it sits.
            float y = min.Y - (minY * scale);
            int nearest = 0;
            for (int li = 1; li < layout.Lines.Count; li++)
            {
                if (Math.Abs(layout.Lines[li].Baseline - y) < Math.Abs(layout.Lines[nearest].Baseline - y))
                {
                    nearest = li;
                }
            }

            theirList.Add(new Glyph(id, fam, min.X - (minX * scale), y, nearest));
        }

        return (ourList, theirList, layout);
    }

    private static FontFace FaceOf(string font) => TestFonts.Face(Merged);

    public static string Describe(List<Glyph> l) => string.Join(" ", l.Select(g => $"{g.Id}@{g.X:0.##},{g.Y:0.##}"));
}
