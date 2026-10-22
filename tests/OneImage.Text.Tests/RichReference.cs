using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using RefDeco = SixLabors.Fonts.TextDecorations;
using RefFont = SixLabors.Fonts.Font;
using RefOptions = SixLabors.Fonts.TextOptions;

namespace OneImage.Text.Tests;

/// <summary>What SixLabors.Fonts reports for a laid-out text: the ink box of each glyph and the decoration segments, in render order.</summary>
internal sealed class GlyphLog : IGlyphRenderer
{
    public readonly List<RefGlyph> Glyphs = [];
    public readonly List<RefSegment> Segments = [];
    private RefDeco _current;

    public sealed record RefGlyph(int Id, float Size, FontRectangle Ink, bool HasOutline);

    public sealed record RefSegment(RefDeco Kind, float X0, float X1, float Y, float Thickness);

    public static GlyphLog Render(string text, RefOptions options)
    {
        var log = new GlyphLog();
        TextRenderer.RenderTextTo(log, text, options);
        return log;
    }

    public void BeginText(in FontRectangle bounds)
    {
    }

    public bool BeginGlyph(in FontRectangle bounds, in GlyphRendererParameters parameters)
    {
        _current = parameters.TextRun?.TextDecorations ?? RefDeco.None;
        Glyphs.Add(new RefGlyph(parameters.GlyphId, parameters.PointSize, bounds, false));
        return true;
    }

    public void BeginFigure()
    {
    }

    public void MoveTo(Vector2 point) => Mark();

    public void LineTo(Vector2 point)
    {
    }

    public void QuadraticBezierTo(Vector2 secondControlPoint, Vector2 point)
    {
    }

    public void CubicBezierTo(Vector2 secondControlPoint, Vector2 thirdControlPoint, Vector2 point)
    {
    }

    public void ArcTo(float radiusX, float radiusY, float rotationDegrees, bool largeArc, bool sweep, Vector2 point) => Mark();

    public void EndFigure()
    {
    }

    public void EndGlyph()
    {
    }

    public void EndText()
    {
    }

    public RefDeco EnabledDecorations() => RefDeco.Underline | RefDeco.Strikeout | RefDeco.Overline;

    public void SetDecoration(RefDeco textDecorations, Vector2 start, Vector2 end, float thickness)
    {
        if ((_current & textDecorations) != 0)
        {
            Segments.Add(new RefSegment(textDecorations, start.X, end.X, start.Y, thickness));
        }
    }

    private void Mark()
    {
        RefGlyph g = Glyphs[^1];
        Glyphs[^1] = g with { HasOutline = true };
    }

    /// <summary>The decoration segments with neighbours that touch end to end on the same line merged, sorted by line, kind and start.</summary>
    public List<RefSegment> MergedSegments()
    {
        var merged = new List<RefSegment>();
        foreach (RefSegment s in Segments)
        {
            int i = merged.FindLastIndex(m => m.Kind == s.Kind && Math.Abs(m.Y - s.Y) < 0.001f && Math.Abs(m.Thickness - s.Thickness) < 0.001f && Math.Abs(m.X1 - s.X0) < 0.01f);
            if (i >= 0)
            {
                merged[i] = merged[i] with { X1 = s.X1 };
            }
            else
            {
                merged.Add(s);
            }
        }

        return [.. merged.OrderBy(m => m.Y).ThenBy(m => m.Kind).ThenBy(m => m.X0)];
    }
}

internal static class RichReference
{
    private static readonly Dictionary<string, SixLabors.Fonts.FontFamily> Families = [];

    /// <summary>A reference family from a font file, shared between tests.</summary>
    public static SixLabors.Fonts.FontFamily Family(string file)
    {
        lock (Families)
        {
            if (!Families.TryGetValue(file, out SixLabors.Fonts.FontFamily f))
            {
                var c = new SixLabors.Fonts.FontCollection();
                f = c.Add(TestFonts.PathOf(file));
                Families[file] = f;
            }

            return f;
        }
    }

    public static RefFont Regular(float size) => Family(TestFonts.Regular).CreateFont(size);

    public static RefFont Bold(float size) => Family(TestFonts.BoldKern).CreateFont(size, SixLabors.Fonts.FontStyle.Bold);

    public static RichTextOptions Options(RefFont font, OneImage.Text.TextOptions mine)
    {
        RichTextOptions r = Reference.Options(font, mine);
        return r;
    }

    /// <summary>Ink boxes of the glyphs that have an outline, in layout order, from our character bounds.</summary>
    public static List<System.Drawing.RectangleF> OurInk(string text, OneImage.Text.TextOptions o) =>
        [.. TextMeasurer.GetCharacterBounds(text, o).Where(b => !b.Ink.IsEmpty).Select(b => b.Ink)];

    public static List<FontRectangle> TheirInk(GlyphLog log) => [.. log.Glyphs.Where(g => g.HasOutline).Select(g => g.Ink)];

    /// <summary>Largest coordinate difference between two lists of boxes (same length required).</summary>
    public static double MaxDifference(List<System.Drawing.RectangleF> ours, List<FontRectangle> theirs)
    {
        Assert.Equal(theirs.Count, ours.Count);
        double worst = 0;
        for (int i = 0; i < ours.Count; i++)
        {
            System.Drawing.RectangleF a = ours[i];
            FontRectangle b = theirs[i];
            worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(a.Left - b.Left), Math.Abs(a.Top - b.Top)), Math.Max(Math.Abs(a.Width - b.Width), Math.Abs(a.Height - b.Height))));
        }

        return worst;
    }
}
